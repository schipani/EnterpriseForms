using System.Security.Cryptography;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Application.Features.Journal;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Catalogue;
using AuthorizationAuthority.Domain.Features.Content;
using AuthorizationAuthority.Domain.Features.FieldSearch;
using AuthorizationAuthority.Domain.Features.Keys;
using AuthorizationAuthority.Domain.Features.Organizations;

namespace AuthorizationAuthority.Application.Features.Catalogue;

/// <summary>
/// Phase C7 M4 — G19 protection reconciliation (verifier workload only, C-56 D1(7), C-60 OD-1/OD-8).
/// <para>
/// The migration catalogue (AC-5) gives the complete expected population of a form. This service lets the verifier prove,
/// record by record, what the Authority-accepted current revision holds: <see cref="CensusAsync"/> verifies each record's
/// observed rows against its accepted manifest (the shared verification core: complete leaf set, every HMAC, root, archive
/// receipt; tampering quarantines the head) and counts the decision's field rows in the old and the target form from the
/// verified canonical bytes. <see cref="AttestAsync"/> then computes the report from Authority state alone, under the
/// organization lock: the form's registry population as it is <b>now</b>, every current head, and the census results of
/// exactly those heads. No count supplied by the business is used, and a census of an earlier head never counts for a newer
/// one. <see cref="CoverageAsync"/> lists every current protection decision of the organization with its latest attestation.
/// </para>
/// <para>
/// Nothing here writes record content: conversion stays the reprotection operation (B-28) under the same decision. The
/// verifier can only verify, count and attest.
/// </para>
/// </summary>
public sealed class ProtectionReconciliationService(IAuthorityStore store, ContentService content, TimeProvider clock)
{
    private static readonly string[] PopulationStates = [ResourceLifecycleStates.Active, ResourceLifecycleStates.Quarantined, ResourceLifecycleStates.Registered];

    /// <summary>Census outcomes that are stored: a verified revision, or the integrity failure that quarantined it.</summary>
    private static bool IsStoredOutcome(ContentService.ProtectionCensusVerification result) => result.Verified || result.Quarantined;

    public async Task<ProtectionCensusResponse> CensusAsync(string workloadName, Guid snapshotId, ProtectionCensusRequest request, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workloadName);
        ArgumentNullException.ThrowIfNull(request);
        if (request.Records is null || request.Records.Count is 0 or > ContentService.MaximumBatchRecords)
        {
            throw AuthorityException.Invalid($"records must contain 1..{ContentService.MaximumBatchRecords} items");
        }

        if (request.Records.Select(r => r.FormRecordId).Distinct().Count() != request.Records.Count)
        {
            throw AuthorityException.Invalid("each record may appear once per census");
        }

        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = await RequireSnapshotAsync(scope, workloadName, snapshotId, cancellationToken).ConfigureAwait(false);
        if (snapshot.SnapshotExpiresOn <= clock.GetUtcNow())
        {
            throw new AuthorityException(AuthorityErrorCodes.ContinuationInvalidated, 409, false, "the migration catalogue expired; request a new one");
        }

        var state = await scope.LockOrganizationAsync(snapshot.OrganizationId, cancellationToken).ConfigureAwait(false)
                    ?? throw AuthorityException.NotRegistered("organization is not registered");
        OrganizationGate.RequireOpen(await OrganizationGate.EvaluateAsync(scope, state, cancellationToken).ConfigureAwait(false));
        var decision = await ResolveDecisionAsync(scope, snapshot, cancellationToken).ConfigureAwait(false);

        var ids = request.Records.Select(r => r.FormRecordId).ToList();
        var entries = (await scope.GetCatalogueSnapshotEntriesForRecordsAsync(snapshot.SearchCatalogueSnapshotId, ids, cancellationToken).ConfigureAwait(false)).ToDictionary(e => e.FormRecordId);
        var held = (await scope.GetProtectionCensusResultsAsync(state.OrganizationId, decision.CommandId, ids, cancellationToken).ConfigureAwait(false))
            .ToDictionary(r => (r.FormRecordId, r.CommittedOperationId));
        var heads = (await scope.GetContentHeadsAsync(state.OrganizationId, ids, cancellationToken).ConfigureAwait(false)).ToDictionary(h => h.FormRecordId);

        var results = new List<ProtectionCensusRecordResult>(request.Records.Count);
        var changed = false;
        var now = clock.GetUtcNow();
        foreach (var item in request.Records)
        {
            if (!entries.TryGetValue(item.FormRecordId, out var entry) || entry.CommittedOperationId != item.ExpectedCommittedOperationId)
            {
                // Only the snapshot's own work-list at the head it fixed; anything else is not this verifier's census to take.
                results.Add(new ProtectionCensusRecordResult(item.FormRecordId, ContentService.NotInScope, 0, null, 0, 0, 0, false));
                continue;
            }

            var head = heads.GetValueOrDefault(item.FormRecordId);
            if (!request.Reverify && held.TryGetValue((item.FormRecordId, item.ExpectedCommittedOperationId), out var known) && head is { ContentHeadQuarantined: false } && head.CommittedOperationId == item.ExpectedCommittedOperationId && known.CensusOutcomeName == ProtectionCensusOutcomes.Verified)
            {
                // Accepted content of an operation is immutable: within one reconciliation a census already verified for it stays
                // true (no re-verification). Every reconciliation's first round re-verifies (Reverify), so a business copy tampered
                // with after an earlier census is never vouched for by reuse.
                results.Add(new ProtectionCensusRecordResult(item.FormRecordId, known.CensusOutcomeName, known.CommittedContentRevision, known.CommittedOperationId, known.FieldOccurrenceCount, known.OldFormOccurrenceCount, known.EnvelopeIssueCount, true));
                continue;
            }

            var verified = await content.VerifyForProtectionCensusAsync(scope, state, decision.FormDefinitionId, item.FormRecordId, item.ExpectedCommittedOperationId, item.ObservedItems ?? [], decision.FieldDefinitionId, decision.Direction, workloadName, decision.CommandId, correlationId, cancellationToken).ConfigureAwait(false);
            changed |= verified.Quarantined;
            if (IsStoredOutcome(verified) && verified.CommittedOperationId is { } operation && !held.ContainsKey((item.FormRecordId, operation)))
            {
                scope.AddProtectionCensusResult(new ProtectionCensusResult
                {
                    ProtectionCensusResultId = Guid.CreateVersion7(),
                    OrganizationId = state.OrganizationId,
                    ScopingSecurityCommandId = decision.CommandId,
                    FormDefinitionId = decision.FormDefinitionId,
                    FieldDefinitionId = decision.FieldDefinitionId,
                    FormRecordId = item.FormRecordId,
                    CommittedOperationId = operation,
                    CommittedContentRevision = verified.CommittedContentRevision,
                    CensusOutcomeName = verified.Outcome,
                    FieldOccurrenceCount = verified.FieldOccurrenceCount,
                    OldFormOccurrenceCount = verified.OldFormOccurrenceCount,
                    EnvelopeIssueCount = verified.EnvelopeIssueCount,
                    VerifiedRecordStatus = verified.RecordStatus,
                    RequestingWorkloadName = workloadName,
                    CensusVerifiedOn = now,
                });
                changed = true;
            }

            results.Add(new ProtectionCensusRecordResult(item.FormRecordId, verified.Outcome, verified.CommittedContentRevision, verified.CommittedOperationId, verified.FieldOccurrenceCount, verified.OldFormOccurrenceCount, verified.EnvelopeIssueCount, false));
        }

        if (changed)
        {
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return new ProtectionCensusResponse(snapshot.SearchCatalogueSnapshotId, decision.FieldDefinitionId, DirectionName(decision.Direction), results, correlationId);
    }

    /// <summary>
    /// The Authority's report for the snapshot's protection decision, computed now under the organization lock and journaled
    /// (ProtectionReconciliationAttested) before it is returned. The snapshot only identifies the decision and the verifier;
    /// the population and heads are read again, so records registered, changed or quarantined since the snapshot was issued
    /// show up as uncensused or quarantined rather than being missed.
    /// </summary>
    public async Task<ProtectionAttestation> AttestAsync(string workloadName, Guid snapshotId, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workloadName);
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = await RequireSnapshotAsync(scope, workloadName, snapshotId, cancellationToken).ConfigureAwait(false);
        var state = await scope.LockOrganizationAsync(snapshot.OrganizationId, cancellationToken).ConfigureAwait(false)
                    ?? throw AuthorityException.NotRegistered("organization is not registered");
        OrganizationGate.RequireOpen(await OrganizationGate.EvaluateAsync(scope, state, cancellationToken).ConfigureAwait(false));
        var decision = await ResolveDecisionAsync(scope, snapshot, cancellationToken).ConfigureAwait(false);

        var population = await scope.GetFormRecordResourcesAsync(state.OrganizationId, decision.FormDefinitionId, PopulationStates, cancellationToken).ConfigureAwait(false);
        var retired = (await scope.GetFormRecordResourcesAsync(state.OrganizationId, decision.FormDefinitionId, [ResourceLifecycleStates.Revoked], cancellationToken).ConfigureAwait(false)).Count;
        var ids = population.Select(r => r.ResourceEntityId).ToList();
        var heads = ids.Count == 0 ? [] : (await scope.GetContentHeadsAsync(state.OrganizationId, ids, cancellationToken).ConfigureAwait(false)).ToDictionary(h => h.FormRecordId);
        var census = ids.Count == 0 ? [] : (await scope.GetProtectionCensusResultsAsync(state.OrganizationId, decision.CommandId, ids, cancellationToken).ConfigureAwait(false)).ToDictionary(r => (r.FormRecordId, r.CommittedOperationId));
        var commits = await scope.CountManifestCommitsAsync(state.OrganizationId, ids, cancellationToken).ConfigureAwait(false);
        var key = await scope.FindKeyRegistrationAsync(state.OrganizationId, IntegrityKeyPurposes.RowIntegrityV1, cancellationToken).ConfigureAwait(false);
        var approvals = (await scope.GetFieldSearchApprovalsAsync(state.OrganizationId, decision.FormDefinitionId, cancellationToken).ConfigureAwait(false))
            .Count(a => a.FieldDefinitionId == decision.FieldDefinitionId && a.ApprovalStateName == FieldSearchApprovalStates.Active);

        var tally = new Tally();
        foreach (var resource in population)
        {
            var head = heads.GetValueOrDefault(resource.ResourceEntityId);
            var contentful = head is { CommittedContentRevision: > 0, CommittedOperationId: not null };
            if (resource.ResourceLifecycleState == ResourceLifecycleStates.Quarantined || head?.ContentHeadQuarantined == true)
            {
                tally.Quarantined++;
                continue;
            }

            if (!contentful || resource.ResourceLifecycleState == ResourceLifecycleStates.Registered)
            {
                tally.Pending++; // no accepted content; the protection barrier admits a first revision only in the required form
                continue;
            }

            tally.Accepted++;
            tally.RetainedHistory += Math.Max(0, commits.GetValueOrDefault(resource.ResourceEntityId) - 1);
            if (key is null || head!.SignatureKeyVersion < key.MinimumVerificationKeyVersion)
            {
                tally.Unverified++;
                continue;
            }

            if (!census.TryGetValue((resource.ResourceEntityId, head.CommittedOperationId!.Value), out var result))
            {
                tally.NotCensused++;
                continue;
            }

            if (result.CensusOutcomeName != ProtectionCensusOutcomes.Verified)
            {
                tally.Unverified++;
                continue;
            }

            tally.Archived += result.VerifiedRecordStatus == "Archived" ? 1 : 0;
            tally.TargetOccurrences += result.FieldOccurrenceCount - result.OldFormOccurrenceCount;
            if (result.OldFormOccurrenceCount > 0)
            {
                tally.OldFormRecords++;
                tally.OldFormOccurrences += result.OldFormOccurrenceCount;
            }
            else if (result.EnvelopeIssueCount > 0)
            {
                tally.EnvelopeIssues++;
            }
            else if (result.FieldOccurrenceCount == 0)
            {
                tally.NotApplicable++;
            }
            else
            {
                tally.Compliant++;
            }
        }

        var reason = tally.Quarantined > 0 ? ProtectionAttestationReasons.Quarantined
            : tally.Unverified > 0 ? ProtectionAttestationReasons.Unverified
            : tally.NotCensused > 0 ? ProtectionAttestationReasons.NotYetCensused
            : tally.OldFormRecords > 0 ? ProtectionAttestationReasons.OldFormRemaining
            : tally.EnvelopeIssues > 0 ? ProtectionAttestationReasons.EnvelopeIssues
            : approvals > 0 ? ProtectionAttestationReasons.SearchApprovalActive
            : null;
        var now = clock.GetUtcNow();
        var attestationId = Guid.CreateVersion7();
        var statement = CoverageStatement(tally.RetainedHistory, retired, tally.Pending);
        var body = new AttestationBody(attestationId, state.OrganizationId, decision.CommandId, decision.FormDefinitionId, decision.FieldDefinitionId, DirectionName(decision.Direction), snapshot.SearchCatalogueSnapshotId,
            population.Count, tally.Accepted, tally.Pending, tally.Quarantined, tally.Compliant, tally.NotApplicable, tally.OldFormRecords, tally.OldFormOccurrences, tally.TargetOccurrences, tally.Unverified, tally.NotCensused,
            tally.EnvelopeIssues, tally.Archived, retired, tally.RetainedHistory, approvals, reason is null, reason, state.AuthorizationRevision, state.ContentIntegrityRevision, now, workloadName, statement, correlationId);
        var digest = SHA256.HashData(CanonicalJson.SerializeBytes(body));
        AuditJournal.Append(scope, state, "ProtectionReconciliationAttested", body, clock);
        var attestation = new ProtectionReconciliationAttestation
        {
            ProtectionReconciliationAttestationId = attestationId,
            OrganizationId = state.OrganizationId,
            ScopingSecurityCommandId = decision.CommandId,
            FormDefinitionId = decision.FormDefinitionId,
            FieldDefinitionId = decision.FieldDefinitionId,
            ProtectionDirectionName = DirectionName(decision.Direction),
            SearchCatalogueSnapshotId = snapshot.SearchCatalogueSnapshotId,
            ExpectedRecordCount = population.Count,
            AcceptedRecordCount = tally.Accepted,
            PendingCreationRecordCount = tally.Pending,
            QuarantinedRecordCount = tally.Quarantined,
            CompliantRecordCount = tally.Compliant,
            NotApplicableRecordCount = tally.NotApplicable,
            OldFormRemainingRecordCount = tally.OldFormRecords,
            OldFormRemainingOccurrenceCount = tally.OldFormOccurrences,
            TargetFormOccurrenceCount = tally.TargetOccurrences,
            UnverifiedRecordCount = tally.Unverified,
            NotYetCensusedRecordCount = tally.NotCensused,
            EnvelopeIssueRecordCount = tally.EnvelopeIssues,
            ArchivedRecordCount = tally.Archived,
            RetiredRecordCount = retired,
            RetainedHistoryRevisionCount = tally.RetainedHistory,
            ActiveSearchApprovalCount = approvals,
            AttestationComplete = reason is null,
            AttestationIncompleteReason = reason,
            AttestationDigest = digest,
            AttestedAuthorizationRevision = state.AuthorizationRevision,
            AttestedContentIntegrityRevision = state.ContentIntegrityRevision,
            AttestedAuditJournalRevision = state.AuditJournalRevision,
            RequestingWorkloadName = workloadName,
            AttestedOn = now,
        };
        scope.AddProtectionAttestation(attestation);
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToContract(attestation, correlationId);
    }

    /// <summary>
    /// Every current protection decision of the organization (active FieldProtectionRequirement / FieldProtectionRelease
    /// resources — never the business database's form list) with its latest attestation. Complete for the organization only
    /// when each decision has a complete attestation taken under that very decision.
    /// </summary>
    public async Task<ProtectionCoverageResponse> CoverageAsync(ProtectionCoverageRequest request, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var state = await scope.ReadOrganizationAsync(request.OrganizationId, cancellationToken).ConfigureAwait(false)
                    ?? throw AuthorityException.NotRegistered("organization is not registered");
        var decisions = await scope.GetResourcesByTypeAsync(state.OrganizationId, [ResourceTypeNames.FieldProtectionRequirement, ResourceTypeNames.FieldProtectionRelease], [ResourceLifecycleStates.Active], cancellationToken).ConfigureAwait(false);
        var forms = (await scope.GetResourcesByTypeAsync(state.OrganizationId, [ResourceTypeNames.FormDefinition], [ResourceLifecycleStates.Active], cancellationToken).ConfigureAwait(false)).Select(f => f.ResourceEntityId).ToHashSet();
        var commandIds = decisions.Select(d => d.RegisteredBySecurityCommandId).ToList();
        var latest = (await scope.GetLatestProtectionAttestationsAsync(state.OrganizationId, commandIds, cancellationToken).ConfigureAwait(false)).ToDictionary(a => a.ScopingSecurityCommandId);

        var rows = decisions.Select(d =>
        {
            var command = d.RegisteredBySecurityCommandId;
            var attestation = latest.GetValueOrDefault(command);
            return new ProtectionCoverageDecision(command, d.ParentResourceEntityId ?? Guid.Empty, d.ResourceEntityId,
                d.ResourceTypeName == ResourceTypeNames.FieldProtectionRequirement ? DirectionName(ProtectionDirection.Encrypt) : DirectionName(ProtectionDirection.Decrypt),
                d.ParentResourceEntityId is { } form && forms.Contains(form), attestation is null ? null : ToContract(attestation, correlationId));
        }).ToList();
        var complete = rows.Count(r => r.LatestAttestation is { AttestationComplete: true });
        return new ProtectionCoverageResponse(state.OrganizationId, forms.Count, rows.Count, complete, complete == rows.Count, rows, state.AuthorizationRevision, clock.GetUtcNow(),
            "Every current field protection decision the Authorization Authority holds for the organization, each with its latest Authority attestation (point in time). An attestation covers the current accepted revisions of the decision's form only; retained earlier revisions and retired records are reported, not migrated.",
            correlationId);
    }

    private static async Task<SearchCatalogueSnapshot> RequireSnapshotAsync(IAuthorityScope scope, string workloadName, Guid snapshotId, CancellationToken cancellationToken)
    {
        var snapshot = await scope.FindCatalogueSnapshotAsync(snapshotId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null || snapshot.SnapshotScopeKind != CatalogueScopeKinds.MigrationVerification || !string.Equals(snapshot.RequestingWorkloadName, workloadName, StringComparison.Ordinal) || snapshot.ScopingSecurityCommandId is null)
        {
            throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false);
        }

        return snapshot;
    }

    /// <summary>The snapshot's decision must still be the field's current one; otherwise nothing is counted or attested under it.</summary>
    private static async Task<ProtectionCommandScope> ResolveDecisionAsync(IAuthorityScope scope, SearchCatalogueSnapshot snapshot, CancellationToken cancellationToken)
    {
        try
        {
            var decision = await ProtectionCommandResolver.ResolveAsync(scope, snapshot.OrganizationId, snapshot.ScopingSecurityCommandId!.Value, cancellationToken).ConfigureAwait(false);
            if (decision.FormDefinitionId != snapshot.FormDefinitionId)
            {
                throw new AuthorityException(AuthorityErrorCodes.ContinuationInvalidated, 409, false, "the protection decision no longer scopes this form");
            }

            return decision;
        }
        catch (AuthorityException ex) when (ex.ErrorCode == AuthorityErrorCodes.Forbidden)
        {
            throw new AuthorityException(AuthorityErrorCodes.ContinuationInvalidated, 409, false, "the protection decision was superseded or cancelled; reconcile under the current decision");
        }
    }

    private static string DirectionName(ProtectionDirection direction) => direction == ProtectionDirection.Encrypt ? "Encrypt" : "Decrypt";

    private static string CoverageStatement(long retainedHistory, int retired, int pending) =>
        $"Current accepted revision of every record the Authorization Authority registers for this form, verified by the Authority. Not covered and not migrated: {retainedHistory} retained earlier revision(s) (immutable history) and {retired} retired record(s). {pending} pending creation(s) hold no accepted content.";

    private static ProtectionAttestation ToContract(ProtectionReconciliationAttestation a, Guid correlationId) => new(
        a.ProtectionReconciliationAttestationId, a.OrganizationId, a.ScopingSecurityCommandId, a.FormDefinitionId, a.FieldDefinitionId, a.ProtectionDirectionName, a.SearchCatalogueSnapshotId,
        a.ExpectedRecordCount, a.AcceptedRecordCount, a.PendingCreationRecordCount, a.QuarantinedRecordCount, a.CompliantRecordCount, a.NotApplicableRecordCount, a.OldFormRemainingRecordCount, a.OldFormRemainingOccurrenceCount,
        a.TargetFormOccurrenceCount, a.UnverifiedRecordCount, a.NotYetCensusedRecordCount, a.EnvelopeIssueRecordCount, a.ArchivedRecordCount, a.RetiredRecordCount, a.RetainedHistoryRevisionCount, a.ActiveSearchApprovalCount,
        a.AttestationComplete, a.AttestationIncompleteReason, Convert.ToHexStringLower(a.AttestationDigest), a.AttestedAuthorizationRevision, a.AttestedContentIntegrityRevision, a.AttestedAuditJournalRevision, a.AttestedOn,
        CoverageStatement(a.RetainedHistoryRevisionCount, a.RetiredRecordCount, a.PendingCreationRecordCount), correlationId);

    private sealed class Tally
    {
        public int Accepted;
        public int Pending;
        public int Quarantined;
        public int Compliant;
        public int NotApplicable;
        public int OldFormRecords;
        public int OldFormOccurrences;
        public int TargetOccurrences;
        public int Unverified;
        public int NotCensused;
        public int EnvelopeIssues;
        public int Archived;
        public long RetainedHistory;
    }

    /// <summary>Audit body (OD-8): identifiers, counts and revisions only — never record ids, values or ciphertext.</summary>
    private sealed record AttestationBody(Guid ProtectionReconciliationAttestationId, Guid OrganizationId, Guid SecurityCommandId, Guid FormDefinitionId, Guid FieldDefinitionId, string ProtectionDirection, Guid SearchCatalogueSnapshotId,
        int ExpectedRecordCount, int AcceptedRecordCount, int PendingCreationRecordCount, int QuarantinedRecordCount, int CompliantRecordCount, int NotApplicableRecordCount, int OldFormRemainingRecordCount, int OldFormRemainingOccurrenceCount,
        int TargetFormOccurrenceCount, int UnverifiedRecordCount, int NotYetCensusedRecordCount, int EnvelopeIssueRecordCount, int ArchivedRecordCount, int RetiredRecordCount, long RetainedHistoryRevisionCount, int ActiveSearchApprovalCount,
        bool AttestationComplete, string? AttestationIncompleteReason, long AuthorizationRevision, long ContentIntegrityRevision, DateTimeOffset AttestedOn, string WorkloadName, string CoverageStatement, Guid CorrelationId);
}
