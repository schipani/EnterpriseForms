using System.Text.Json;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using AuthorizationAuthority.Domain.Features.Sharing;

namespace AuthorizationAuthority.Application.Features.SecurityCommands;

/// <summary>
/// C10 M1-WP6: internal read-only admission decision for the ExpireShare convergence command. Evaluates whether
/// a RecordShare loaded under the organization lock is eligible for deterministic expiry. Returns
/// eligible/ineligible disposition with safe evidence. Eligible result provides derived actor and idempotency key.
/// No public route, no mutations, no scheduler; types are internal.
/// </summary>
internal static class ExpireShareAdmission
{
    internal enum Disposition
    {
        Eligible,
        Ineligible,
        ClockUnsafe,
    }

    internal sealed record AdmissionResult(
        Disposition Disposition,
        string ReasonCode,
        Guid? DerivedActorIdentityBindingId,
        string? IdempotencyKey);

    /// <summary>
    /// Evaluates whether the given share is eligible for expiry convergence under the organization lock.
    /// The caller holds the organization lock and verifies the source command against the authorization journal
    /// before passing it here. Admission checks the current expiry, expected revision, and human source attribution.
    /// </summary>
    internal static AdmissionResult Evaluate(
        OrganizationAuthorizationState organization,
        AuthorityResource shareResource,
        ShareResourceMetadata shareMetadata,
        long expectedShareRevision,
        SecurityCommand? expirySourceCommand,
        DateTimeOffset evaluatedOn,
        bool gateOpen,
        DateTimeOffset latestJournalTimestamp,
        bool hasDelegatedAssertion)
    {
        // Clock safety: now must not be earlier than the latest journal timestamp
        if (evaluatedOn < latestJournalTimestamp)
        {
            return new AdmissionResult(Disposition.ClockUnsafe, "CLOCK_BACKWARDS", null, null);
        }

        if (hasDelegatedAssertion)
        {
            return Ineligible("DELEGATED_ASSERTION_REFUSED");
        }

        if (!gateOpen || organization.IntegrityQuarantineEnabled)
        {
            return Ineligible("GATE_CLOSED");
        }

        // Resource type must be record_share
        if (shareResource.ResourceTypeName != ResourceTypeNames.RecordShare ||
            shareMetadata.ResourceTypeName != ResourceTypeNames.RecordShare)
        {
            return Ineligible("RESOURCE_TYPE_NOT_RECORD_SHARE");
        }

        // Resource and metadata share IDs must equal
        if (shareMetadata.RecordShareId == Guid.Empty || shareResource.ResourceEntityId != shareMetadata.RecordShareId)
        {
            return Ineligible("RESOURCE_METADATA_ID_MISMATCH");
        }

        // Resource and metadata must belong to same organization
        if (shareResource.OrganizationId != shareMetadata.OrganizationId)
        {
            return Ineligible("RESOURCE_METADATA_ORG_MISMATCH");
        }

        // Tenant isolation: share must belong to this organization
        if (organization.OrganizationId == Guid.Empty || shareMetadata.OrganizationId != organization.OrganizationId)
        {
            return Ineligible("SHARE_WRONG_TENANT");
        }

        // ParentResourceEntityId (record) must be nonempty
        if (shareResource.ParentResourceEntityId == null || shareResource.ParentResourceEntityId == Guid.Empty)
        {
            return Ineligible("RECORD_ID_MISSING");
        }

        // Share must be Active
        if (shareResource.ResourceLifecycleState != ResourceLifecycleStates.Active || shareMetadata.LegacyLineageUnresolved)
        {
            return Ineligible("SHARE_NOT_ACTIVE");
        }

        // Expected revision must match metadata under lock
        if (expectedShareRevision <= 0 || shareMetadata.ShareRevision != expectedShareRevision)
        {
            return Ineligible("SHARE_REVISION_MISMATCH");
        }

        // Share must have an expiry set
        if (shareMetadata.ShareExpiresOn is not { } committedExpiry)
        {
            return Ineligible("NO_EXPIRY_SET");
        }

        // Share must be expired (expiry <= now)
        if (committedExpiry > evaluatedOn)
        {
            return Ineligible("NOT_YET_EXPIRED");
        }

        // Share must have an expiry source command ID
        if (shareMetadata.ExpirySourceSecurityCommandId is not { } sourceCommandId || sourceCommandId == Guid.Empty)
        {
            return Ineligible("NO_EXPIRY_SOURCE_COMMAND");
        }

        // Expiry source command must exist
        if (expirySourceCommand is null)
        {
            return Ineligible("EXPIRY_SOURCE_COMMAND_NOT_FOUND");
        }

        // Source command ID must match metadata exactly
        if (expirySourceCommand.SecurityCommandId != sourceCommandId)
        {
            return Ineligible("SOURCE_COMMAND_ID_MISMATCH");
        }

        // Source command must be Effective
        if (expirySourceCommand.CommandStateName != SecurityCommandStates.Effective)
        {
            return Ineligible("SOURCE_COMMAND_NOT_EFFECTIVE");
        }

        // Source command must be in same organization
        if (expirySourceCommand.OrganizationId != organization.OrganizationId)
        {
            return Ineligible("SOURCE_COMMAND_WRONG_ORG");
        }

        // Source command must be CreateShare or UpdateShareScope
        if (expirySourceCommand.CommandKindName != SecurityCommandKinds.CreateShare &&
            expirySourceCommand.CommandKindName != "UpdateShareScope")
        {
            return Ineligible("SOURCE_COMMAND_WRONG_KIND");
        }

        // Actor identity binding ID must be nonempty
        if (expirySourceCommand.ActorIdentityBindingId == Guid.Empty)
        {
            return Ineligible("ACTOR_ID_EMPTY");
        }

        // Parse and validate normalized JSON payload
        if (string.IsNullOrEmpty(expirySourceCommand.CommandPayloadJson))
        {
            return Ineligible("PAYLOAD_MALFORMED");
        }

        SharePayload payload;
        try
        {
            using var jsonDoc = JsonDocument.Parse(expirySourceCommand.CommandPayloadJson);
            payload = ReadSourceBindings(jsonDoc.RootElement);
        }
        catch (JsonException)
        {
            return Ineligible("PAYLOAD_MALFORMED");
        }

        // The verified payload must identify the same share, record and expiry.
        if (payload.RecordShareId != shareMetadata.RecordShareId)
        {
            return Ineligible("PAYLOAD_SHARE_ID_MISMATCH");
        }

        // Normalized JSON must have signed record ID matching resource parent
        if (payload.FormRecordId != shareResource.ParentResourceEntityId)
        {
            return Ineligible("PAYLOAD_RECORD_ID_MISMATCH");
        }

        // Normalized JSON must have UTC expiry instant exactly matching committed expiry
        if (payload.ShareExpiresOn != committedExpiry)
        {
            return Ineligible("PAYLOAD_EXPIRY_MISMATCH");
        }

        // All checks passed: eligible
        var idempotencyKey = $"share-expiry:{shareMetadata.RecordShareId.ToString("D")}:{expectedShareRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        return new AdmissionResult(
            Disposition.Eligible,
            "ELIGIBLE",
            expirySourceCommand.ActorIdentityBindingId,
            idempotencyKey);
    }

    private static AdmissionResult Ineligible(string reasonCode)
    {
        return new AdmissionResult(Disposition.Ineligible, reasonCode, null, null);
    }

    private static SharePayload ReadSourceBindings(JsonElement payload)
    {
        const string shareKey = "record_share_id";
        const string recordKey = "form_record_id";
        const string expiryKey = "share_expires_on";
        if (payload.ValueKind != JsonValueKind.Object ||
            payload.EnumerateObject().Count(p => p.NameEquals(shareKey) || p.NameEquals(recordKey) || p.NameEquals(expiryKey)) != 3 ||
            !payload.TryGetProperty(shareKey, out var share) || share.ValueKind != JsonValueKind.String ||
            !share.TryGetGuid(out var shareId) ||
            !payload.TryGetProperty(recordKey, out var record) || record.ValueKind != JsonValueKind.String ||
            !record.TryGetGuid(out var recordId) ||
            !payload.TryGetProperty(expiryKey, out var expiry) || expiry.ValueKind != JsonValueKind.String ||
            !expiry.TryGetDateTimeOffset(out var expiresOn))
        {
            throw new JsonException("Expiry source binding is missing, duplicated or malformed");
        }

        return new SharePayload(shareId, recordId, expiresOn);
    }

    private readonly record struct SharePayload(Guid RecordShareId, Guid FormRecordId, DateTimeOffset ShareExpiresOn);
}
