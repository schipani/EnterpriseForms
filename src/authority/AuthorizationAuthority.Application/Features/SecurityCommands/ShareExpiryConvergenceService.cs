using System.Text.Json;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using AuthorizationAuthority.Domain.Features.Sharing;

namespace AuthorizationAuthority.Application.Features.SecurityCommands;

/// <summary>
/// C10 M1-WP6 (WP-0 Section 4, approved C-194 C10-A): records the Authority-internal <c>ExpireShare</c> convergence command for one
/// share. It is not the access control: access-time enforcement (<see cref="ShareExpiryOverlay"/>) denies an expired share whether
/// or not this ever runs. Its only input is a share id and the share revision the caller read; there is no endpoint and no
/// delegated user.
/// <para>
/// Under the organization lock it re-reads the share, its metadata and the human command that set its expiry, verifies that command
/// against its hash-chained authorization-journal entry (the expiry binding is taken from the journalled body), and asks
/// <see cref="ExpireShareAdmission"/>. Anything not eligible records nothing. An eligible share gets the deterministic idempotency key
/// <c>share-expiry:{share}:{revision}</c> (reserved: no external caller may use it), a plan refused by
/// <see cref="ExpireSharePlanGuard"/> unless it only removes access, the derived attribution of the expiry-source command's binding
/// with workload name <c>share-expiry-worker</c>, and the ordinary commit path: desired state, command, outbox, authorization journal
/// and the <c>ShareExpired</c> and <c>ShareInvalidated</c> audit entries in one transaction. Step-up is never involved.
/// </para>
/// </summary>
public sealed class ShareExpiryConvergenceService(IAuthorityStore store, TimeProvider clock)
{
    public const string IdempotencyKeyPrefix = "share-expiry:";

    public enum Result
    {
        Converged,
        Replayed,
        Ineligible,
        ClockUnsafe,
    }

    public sealed record Outcome(Result Result, string ReasonCode, Guid? SecurityCommandId);

    public async Task<Outcome> ConvergeAsync(Guid organizationId, Guid recordShareId, long expectedShareRevision, CancellationToken cancellationToken)
    {
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var state = await scope.LockOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            return new Outcome(Result.Ineligible, "ORGANIZATION_NOT_REGISTERED", null);
        }

        var share = await scope.FindResourceAsync(organizationId, ResourceTypeNames.RecordShare, recordShareId, cancellationToken).ConfigureAwait(false);
        var metadata = await scope.FindShareMetadataAsync(organizationId, recordShareId, cancellationToken).ConfigureAwait(false);
        if (share is null || metadata is null)
        {
            return new Outcome(Result.Ineligible, "SHARE_NOT_FOUND", null);
        }

        var now = clock.GetUtcNow();
        var source = metadata.ExpirySourceSecurityCommandId is { } sourceId ? await VerifiedSourceAsync(scope, organizationId, sourceId, cancellationToken).ConfigureAwait(false) : null;
        var gate = await OrganizationGate.EvaluateAsync(scope, state, cancellationToken).ConfigureAwait(false);
        var latestJournal = await LatestJournalTimestampAsync(scope, organizationId, cancellationToken).ConfigureAwait(false);
        var admission = ExpireShareAdmission.Evaluate(state, share, metadata, expectedShareRevision, source, now, gate == ProjectionGateState.Ready, latestJournal, hasDelegatedAssertion: false);
        if (admission.Disposition == ExpireShareAdmission.Disposition.ClockUnsafe)
        {
            return new Outcome(Result.ClockUnsafe, admission.ReasonCode, null);
        }

        if (admission.Disposition != ExpireShareAdmission.Disposition.Eligible || admission.IdempotencyKey is not { } key || admission.DerivedActorIdentityBindingId is not { } bindingId)
        {
            return new Outcome(Result.Ineligible, admission.ReasonCode, null);
        }

        if (await scope.FindCommandByIdempotencyKeyAsync(organizationId, key, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            // The deterministic key was already used: by this command (a replay), or by nothing else (the prefix is reserved).
            return existing.CommandKindName == SecurityCommandKinds.ExpireShare && existing.ActorIdentityBindingId == bindingId
                ? new Outcome(Result.Replayed, "REPLAYED", existing.SecurityCommandId)
                : new Outcome(Result.Ineligible, "IDEMPOTENCY_KEY_CONFLICT", null);
        }

        var binding = await scope.FindIdentityBindingByIdAsync(bindingId, cancellationToken).ConfigureAwait(false);
        if (binding is null || binding.OrganizationId != organizationId)
        {
            return new Outcome(Result.Ineligible, "SOURCE_BINDING_NOT_FOUND", null);
        }

        var graph = new DesiredStateGraph(await scope.GetActiveEdgesAsync(organizationId, cancellationToken).ConfigureAwait(false));
        var plan = await CommandPlanner.PlanExpireShareAsync(scope, organizationId, share, metadata, graph, now, source!.SecurityCommandId, cancellationToken).ConfigureAwait(false);
        var record = share.ParentResourceEntityId!.Value;
        ExpireSharePlanGuard.Require(plan, recordShareId, record);

        share.ResourceLifecycleState = ResourceLifecycleStates.Expired;
        var command = new SecurityCommand
        {
            SecurityCommandId = Guid.CreateVersion7(),
            OrganizationId = organizationId,
            ActorIdentityBindingId = binding.IdentityBindingId,
            CommandKindName = SecurityCommandKinds.ExpireShare,
            CommandIdempotencyKey = key,
            TargetFormRecordId = record,
            CommandRequestDigest = CanonicalJson.Digest(System.Text.Encoding.UTF8.GetBytes(plan.NormalizedPayloadJson)),
            ExpectedAuthorizationRevision = state.AuthorizationRevision,
            CommandPayloadJson = plan.NormalizedPayloadJson,
            CommandReasonText = plan.CommandReasonOverride,
            CorrelationIdentifier = Guid.CreateVersion7(),
            CommandCreatedOn = now,
            CommandModifiedOn = now,
            CommandStateName = SecurityCommandStates.Rejected,
        };

        var actor = new BoundActor(new VerifiedActor(binding.IdentityIssuerUri, binding.IdentitySubjectIdentifier, CommandPlanner.ShareExpiryWorkloadName), binding);
        SecurityCommandService.ApplyPlan(scope, state, command, plan, actor, new ResourceRef(ResourceKind.FormRecord, record), cancellationToken);
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new Outcome(Result.Converged, "CONVERGED", command.SecurityCommandId);
    }

    /// <summary>
    /// The expiry-source command as its authorization-journal entry records it: the entry at the command's committed revision must
    /// name the command, and the payload handed to admission is the journalled normalized payload, not the mutable row's.
    /// </summary>
    private static async Task<SecurityCommand?> VerifiedSourceAsync(IAuthorityScope scope, Guid organizationId, Guid sourceId, CancellationToken cancellationToken)
    {
        var source = await scope.FindCommandAsync(sourceId, cancellationToken).ConfigureAwait(false);
        if (source is null || source.OrganizationId != organizationId || source.CommittedAuthorizationRevision is not { } revision)
        {
            return source is null ? null : WithoutPayload(source);
        }

        var entry = await scope.FindJournalEntryAsync(organizationId, JournalStreamNames.Authorization, revision, cancellationToken).ConfigureAwait(false);
        if (entry is null || entry.SecurityCommandId != sourceId || entry.JournalEventKind != "SecurityCommandCommitted")
        {
            return WithoutPayload(source);
        }

        try
        {
            using var document = JsonDocument.Parse(entry.JournalEventBodyJson);
            if (!document.RootElement.TryGetProperty("body", out var body) || !body.TryGetProperty("normalized_payload", out var payload) ||
                !body.TryGetProperty("security_command_id", out var named) || named.GetGuid() != sourceId)
            {
                return WithoutPayload(source);
            }

            return Copy(source, payload.GetRawText());
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            return WithoutPayload(source);
        }
    }

    /// <summary>A source whose journal evidence is missing or unreadable keeps its identity but has no payload: admission refuses it.</summary>
    private static SecurityCommand WithoutPayload(SecurityCommand source) => Copy(source, string.Empty);

    private static SecurityCommand Copy(SecurityCommand source, string payload) => new()
    {
        SecurityCommandId = source.SecurityCommandId,
        OrganizationId = source.OrganizationId,
        ActorIdentityBindingId = source.ActorIdentityBindingId,
        CommandKindName = source.CommandKindName,
        CommandStateName = source.CommandStateName,
        CommittedAuthorizationRevision = source.CommittedAuthorizationRevision,
        CommandPayloadJson = payload,
    };

    private static async Task<DateTimeOffset> LatestJournalTimestampAsync(IAuthorityScope scope, Guid organizationId, CancellationToken cancellationToken)
    {
        var latest = DateTimeOffset.MinValue;
        foreach (var stream in new[] { JournalStreamNames.Authorization, JournalStreamNames.Audit })
        {
            if (await scope.FindLatestJournalEntryAsync(organizationId, stream, cancellationToken).ConfigureAwait(false) is { } entry && entry.JournalCreatedOn > latest)
            {
                latest = entry.JournalCreatedOn;
            }
        }

        return latest;
    }
}
