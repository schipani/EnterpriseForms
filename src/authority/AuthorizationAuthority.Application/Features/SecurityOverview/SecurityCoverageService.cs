using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Catalogue;
using AuthorizationAuthority.Contracts.V1;

namespace AuthorizationAuthority.Application.Features.SecurityOverview;

/// <summary>
/// C8 M4-4 — AC-8.5 coverage read (DR-C7-5; contract §5.5, FIELD_PROTECTION_RECONCILIATION.md §13). User plane: business workload
/// plus a delegated Security Administrator, authorized by <see cref="EffectiveEdgeRule"/> with role <c>system_administrator</c> only
/// (owner Q-3), otherwise 404. Computed from the same Authority state as the verifier coverage
/// (<see cref="ProtectionReconciliationService.ComputeCoverageAsync"/>) inside the one read-only snapshot that evaluated the rule.
/// It never calls the verifier routes, never enumerates records and makes no SpiceDB call. Each decision is copied field by field
/// into an allowlist of counts and identifiers: no record ids, no attestation digest, no catalogue snapshot id, no statement text
/// taken from storage.
/// <para>
/// <b>Staleness (OD-1):</b> an attestation is <c>StaleOrganizationWide</c> when the organization's content-integrity revision is
/// greater than the one the attestation recorded. That revision advances on content changes anywhere in the organization, so this
/// is a conservative organization-wide comparison: it can report an attestation stale when its own form did not change, and it is
/// never a per-form verification result. Decision-level counts are as the latest attestation recorded them, not re-verified.
/// </para>
/// </summary>
public sealed class SecurityCoverageService(IAuthorityStore store, TimeProvider clock)
{
    public static readonly IReadOnlyList<string> Roles = [OrganizationRoleNames.SystemAdministrator];

    public const string StalenessStatement =
        "Staleness is an organization-wide comparison: an attestation is marked stale when content anywhere in the organization changed after it was taken "
        + "(the organization's content-integrity revision advanced). It does not mean this form changed, and \"no later organization content change\" is not a "
        + "new verification. Counts are as the latest Authority attestation recorded them.";

    public const string CoverageStatement =
        "Every current field protection decision the Authorization Authority holds for the organization, with its latest Authority attestation (point in time). "
        + "An attestation covers the current accepted revisions of the decision's form only; retained earlier revisions and retired records are counted, not migrated. "
        + "Current versions only (DR-C7-4). Complete attestations are counted as recorded when they were taken: a possibly stale attestation is still counted as complete "
        + "and is not a current verification. This is operational coverage information, not a compliance certification.";

    public async Task<SecurityCoverageResponse> ReadAsync(VerifiedActor actor, SecurityCoverageRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (request is null || request.OrganizationId == Guid.Empty || request.CorrelationId == Guid.Empty)
        {
            throw AuthorityException.Invalid("coverage: organization and correlation id are required");
        }

        await using var snapshot = await store.BeginReadOnlySnapshotScopeAsync(cancellationToken).ConfigureAwait(false);
        var state = await snapshot.ReadOrganizationAsync(request.OrganizationId, cancellationToken).ConfigureAwait(false);
        if (state is null || !await EffectiveEdgeRule.HoldsAnyAsync(snapshot, state.OrganizationId, actor, Roles, cancellationToken).ConfigureAwait(false))
        {
            throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false);
        }

        var coverage = await ProtectionReconciliationService.ComputeCoverageAsync(snapshot, state, clock, request.CorrelationId, cancellationToken).ConfigureAwait(false);
        var organizationRevision = state.ContentIntegrityRevision;
        var decisions = coverage.Decisions.Select(d => Decision(d, organizationRevision)).ToList();
        return new SecurityCoverageResponse(
            state.OrganizationId,
            coverage.RegisteredFormCount,
            decisions.Count,
            decisions.Count(d => d.Attestation is { AttestationComplete: true }),
            decisions.Count(d => d.AttestationState == SecurityCoverageAttestationStates.Missing),
            decisions.Count(d => d.AttestationState == SecurityCoverageAttestationStates.StaleOrganizationWide),
            organizationRevision,
            decisions,
            coverage.ComputedOn,
            CoverageStatement,
            StalenessStatement,
            SecurityOverviewService.AuthorizationBasis,
            request.CorrelationId);
    }

    /// <summary>The attestation state of one decision under the OD-1 organization-wide comparison.</summary>
    public static string StateOf(ProtectionAttestation? attestation, long organizationContentIntegrityRevision) =>
        attestation is null ? SecurityCoverageAttestationStates.Missing
        : attestation.ContentIntegrityRevision < organizationContentIntegrityRevision ? SecurityCoverageAttestationStates.StaleOrganizationWide
        : SecurityCoverageAttestationStates.NoLaterOrganizationContentChange;

    private static SecurityCoverageDecision Decision(ProtectionCoverageDecision d, long organizationRevision)
    {
        var a = d.LatestAttestation;
        var attestation = a is null ? null : new SecurityCoverageAttestation(
            a.ProtectionReconciliationAttestationId,
            a.AttestationComplete,
            a.AttestationIncompleteReason,
            a.ExpectedRecordCount,
            a.AcceptedRecordCount,
            a.CompliantRecordCount,
            a.PendingCreationRecordCount,
            a.QuarantinedRecordCount,
            a.UnverifiedRecordCount,
            a.NotYetCensusedRecordCount,
            a.RetiredRecordCount,
            a.RetainedHistoryRevisionCount,
            a.AuditJournalRevision,
            a.ContentIntegrityRevision,
            a.AttestedOn);
        return new SecurityCoverageDecision(d.SecurityCommandId, d.FormDefinitionId, d.FieldDefinitionId, d.ProtectionDirection, d.FormRegisteredActive, StateOf(a, organizationRevision), attestation);
    }
}
