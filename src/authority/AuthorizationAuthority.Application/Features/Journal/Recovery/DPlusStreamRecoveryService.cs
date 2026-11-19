using System.Text;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Journal.Reconciliation;
using AuthorizationAuthority.Application.Features.Journal.Verification;
using AuthorizationAuthority.Domain.Features.Journal;

namespace AuthorizationAuthority.Application.Features.Journal.Recovery;

/// <summary>What the requester supplies; every digest and expectation is computed by the tool from observed state (SR-ST-2).</summary>
public sealed record DPlusRecoveryProposal(Guid OrganizationId, string StreamName, string Strategy, string RequesterLabel, string RequestReason, string CauseClassification,
    bool ContainmentAttested, string? ContainmentNote, string AnalystLabel, string Narrative, TimeSpan Window, byte[]? RestoreBytes, string? SourceCommit);

public enum DPlusRecoveryCommandOutcome
{
    /// <summary>The command did what it was asked (records written, or an idempotent repeat).</summary>
    Done,

    /// <summary>Refused before any write (blocked, ineligible or invalid). Nothing was written.</summary>
    Refused,

    /// <summary>Definitively failed; a terminal <c>Failed</c> outcome was recorded where the recovery exists.</summary>
    Failed,

    /// <summary>A store could not be read or written; nothing is assumed, retry.</summary>
    Unavailable,
}

public sealed record DPlusRecoveryCommandResult(DPlusRecoveryCommandOutcome Outcome, string Code, string Detail, Guid? RecoveryId = null, string? StatementSha256Hex = null)
{
    public static DPlusRecoveryCommandResult Refused(string code, string detail, Guid? recoveryId = null) => new(DPlusRecoveryCommandOutcome.Refused, code, detail, recoveryId);

    public static DPlusRecoveryCommandResult Unavailable(string detail, Guid? recoveryId = null) => new(DPlusRecoveryCommandOutcome.Unavailable, "UNAVAILABLE", detail, recoveryId);
}

/// <summary>Refusal codes of the recovery tool (A-4 §24.8). None of them writes anything.</summary>
public static class DPlusRecoveryRefusals
{
    public const string DualControlUnavailable = "RECOVERY_DUAL_CONTROL_UNAVAILABLE";
    public const string StreamNotRecoverable = "RECOVERY_STREAM_NOT_RECOVERABLE";
    public const string StrategyNotOperational = "RECOVERY_STRATEGY_NOT_OPERATIONAL";
    public const string OrganizationQuarantined = "RECOVERY_ORGANIZATION_QUARANTINED";
    public const string NoDurableStop = "RECOVERY_NO_DURABLE_STOP";
    public const string StopSetNotSingleScope = "RECOVERY_STOP_SET_NOT_SINGLE_SCOPE";
    public const string ArchiveInstanceBlocked = "RECOVERY_ARCHIVE_INSTANCE_BLOCKED";
    public const string EvidenceUnavailable = "RECOVERY_EVIDENCE_UNAVAILABLE";
    public const string EvidenceContradictory = "RECOVERY_EVIDENCE_CONTRADICTORY";
    public const string ContainmentRequired = "RECOVERY_CONTAINMENT_REQUIRED";
    public const string StrategyPreconditionFailed = "RECOVERY_STRATEGY_PRECONDITION_FAILED";
    public const string AnchorUnavailable = "RECOVERY_ANCHOR_UNAVAILABLE";
    public const string SeparationOfDuties = "RECOVERY_SEPARATION_OF_DUTIES";
    public const string InvalidRequest = "RECOVERY_INVALID_REQUEST";
    public const string UnknownRecovery = "RECOVERY_UNKNOWN";
    public const string NotAuthorized = "RECOVERY_NOT_AUTHORIZED";
    public const string ApprovalsNotFresh = "RECOVERY_APPROVALS_NOT_FRESH";
    public const string WindowNotOpen = "RECOVERY_WINDOW_NOT_OPEN";
    public const string WindowExpired = "RECOVERY_WINDOW_EXPIRED";
    public const string Rejected = "RECOVERY_REJECTED";
    public const string Cancelled = "RECOVERY_CANCELLED";
    public const string EvidenceChanged = "RECOVERY_EVIDENCE_CHANGED";
    public const string StopSetChanged = "RECOVERY_STOP_SET_CHANGED";
    public const string ExecutionConflict = "RECOVERY_EXECUTION_CONFLICT";
    public const string RestoreConflict = "RECOVERY_RESTORE_CONFLICT";
    public const string Terminal = "RECOVERY_TERMINAL";
    public const string DecisionInvalid = "RECOVERY_DECISION_INVALID";
}

/// <summary>The derived state of one recovery (A-4 §24; state machine of <c>G14_R1_RECOVERY_STATE_MACHINE.md</c>). Never stored.</summary>
public sealed record DPlusRecoveryStatus(Guid RecoveryId, string State, string Detail, int StreamGeneration, int ApprovalCount, bool Executed, string? Outcome);

/// <summary>
/// SEC-BL-01 G14-R2 (A-4 §24), extended to every D+-enabled stream by WP-5b (A-6 §26): the controlled stream recovery tool behind the Development/Testing command line (no HTTP endpoint).
/// It builds statements from observed state, records signed decisions it can verify, and executes authorized SR-A and SR-C1 recoveries in
/// the A-4 order (action, row hint, execution record last). It holds no approver key and cannot approve anything; it never deletes or
/// overwrites a record, a stop record or an archived object, never re-signs, never changes a checkpoint and never upgrades assurance.
/// </summary>
public sealed class DPlusStreamRecoveryService(IAuthorityStore store, ISecurityEventArchive archive, IJournalVerificationFindingStore findings, DPlusDurability durability, TimeProvider clock)
{
    private static string ToolVersion => typeof(DPlusStreamRecoveryService).Assembly.GetName().Version?.ToString() ?? "unknown";

    private ISecurityEventArchiveInventory Inventory => archive as ISecurityEventArchiveInventory ?? throw new InvalidOperationException("the archive has no inventory");

    private IArchiveMetaRecords Meta => archive as IArchiveMetaRecords ?? throw new InvalidOperationException("the archive has no meta/ records");

    // ------------------------------------------------------------------ propose

    public async Task<DPlusRecoveryCommandResult> ProposeAsync(DPlusRecoveryProposal request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!durability.Approvals.Operational)
        {
            return DPlusRecoveryCommandResult.Refused(DPlusRecoveryRefusals.DualControlUnavailable, "recovery approval is not available in this environment (SR-PB-6)");
        }

        if (!AuditDeliveryEnforcement.IsEnforced(request.StreamName))
        {
            return DPlusRecoveryCommandResult.Refused(DPlusRecoveryRefusals.StreamNotRecoverable, "only a D+-enabled stream is recoverable (SR-PB-2, amendment A-6)");
        }

        if (!DPlusRecoveryStrategies.IsOperational(request.Strategy))
        {
            return DPlusRecoveryCommandResult.Refused(DPlusRecoveryRefusals.StrategyNotOperational, "strategy " + request.Strategy + " is not operational in this increment (only SR-A and SR-C1)");
        }

        if (string.IsNullOrWhiteSpace(request.RequesterLabel) || string.IsNullOrWhiteSpace(request.AnalystLabel) || string.IsNullOrWhiteSpace(request.RequestReason)
            || string.IsNullOrWhiteSpace(request.Narrative) || request.Window <= TimeSpan.Zero || request.Window > DPlusRecoveryStatement.MaximumWindow)
        {
            return DPlusRecoveryCommandResult.Refused(DPlusRecoveryRefusals.InvalidRequest, "requester, analyst, reason, narrative and a window of at most 24 hours are required");
        }

        if (durability.Approvals.ApproverIds.Any(id => string.Equals(id, request.RequesterLabel, StringComparison.Ordinal) || string.Equals(id, request.AnalystLabel, StringComparison.Ordinal)))
        {
            return DPlusRecoveryCommandResult.Refused(DPlusRecoveryRefusals.SeparationOfDuties, "the requester and the analyst must not be recovery approvers");
        }

        var causeOk = request.Strategy == DPlusRecoveryStrategies.FalseAlarm
            ? DPlusRecoveryStrategies.FalseAlarmCauses.Contains(request.CauseClassification, StringComparer.Ordinal)
            : string.Equals(request.CauseClassification, DPlusRecoveryStrategies.PredecessorLostCause, StringComparison.Ordinal);
        if (!causeOk)
        {
            return DPlusRecoveryCommandResult.Refused(DPlusRecoveryRefusals.StrategyPreconditionFailed, "the investigation's cause classification does not permit " + request.Strategy);
        }

        if ((request.Strategy == DPlusRecoveryStrategies.RestorePredecessor) != (request.RestoreBytes is not null))
        {
            return DPlusRecoveryCommandResult.Refused(DPlusRecoveryRefusals.InvalidRequest, "SR-C1 requires the predecessor copy, and only SR-C1 accepts one");
        }

        var observed = await ObserveAsync(request.OrganizationId, request.StreamName, request.Strategy, request.RestoreBytes, null, false, cancellationToken).ConfigureAwait(false);
        if (observed.Refusal is { } refusal)
        {
            return refusal;
        }

        var evidence = observed.Evidence!;
        if (evidence.StopRecordIntegrityCodes.Count > 0 && !request.ContainmentAttested)
        {
            return DPlusRecoveryCommandResult.Refused(DPlusRecoveryRefusals.ContainmentRequired, "stop-record integrity findings exist; the investigation must attest containment (SR-EL-6, SR-PB-5)");
        }

        if (!string.Equals(evidence.EvaluationVerdict, nameof(AuditDeliveryVerdictKind.Deliver), StringComparison.Ordinal))
        {
            return DPlusRecoveryCommandResult.Refused(DPlusRecoveryRefusals.StrategyPreconditionFailed, "D+ does not pass on the " + (request.Strategy == DPlusRecoveryStrategies.FalseAlarm
                ? "unchanged current evidence" : "evidence with the anchored predecessor") + ": " + evidence.EvaluationVerdict);
        }

        var now = clock.GetUtcNow().ToUniversalTime();
        var recoveryId = Guid.CreateVersion7(clock.GetUtcNow());
        var org = request.OrganizationId;
        var stream = request.StreamName;
        var investigation = new DPlusStreamInvestigation(DPlusStreamInvestigation.SchemaV1, recoveryId, org, stream, evidence.FailedRevision, request.CauseClassification, request.ContainmentAttested,
            request.ContainmentNote is null ? null : DPlusRecoveryCodec.Sha256Hex(request.ContainmentNote), request.AnalystLabel, DPlusRecoveryCodec.Sha256Hex(request.Narrative));
        var investigationBytes = DPlusRecoveryCodec.Serialize(investigation);
        var evidenceBytes = DPlusRecoveryCodec.Serialize(evidence);
        var expected = new DPlusRecoveryExpectation(evidence.PinnedArchiveInstanceId, evidence.RowBodySha256Hex,
            request.Strategy == DPlusRecoveryStrategies.RestorePredecessor ? nameof(AuditPredecessorState.Present) : evidence.PredecessorState,
            request.Strategy == DPlusRecoveryStrategies.RestorePredecessor ? evidence.RestoreSha256Hex : evidence.PredecessorSha256Hex);
        var statement = new DPlusRecoveryStatement(DPlusRecoveryStatement.SchemaV1, recoveryId, org, stream, evidence.FailedRevision, evidence.StopGeneration, observed.StopSet!,
            DPlusRecoveryCodec.StopSetSha256Hex(observed.StopSet!), request.Strategy, observed.EligibilityClass!, DPlusRecoveryCodec.Sha256Hex(evidenceBytes), expected,
            DPlusRecoveryCodec.Sha256Hex(investigationBytes), request.RequesterLabel, DPlusRecoveryCodec.Sha256Hex(request.RequestReason), TruncateToSeconds(now),
            TruncateToSeconds(now) + request.Window, ToolVersion, request.SourceCommit);
        if (DPlusRecoveryLedger.ValidateStatement(statement, org, stream, recoveryId) is { } invalid)
        {
            return DPlusRecoveryCommandResult.Refused(DPlusRecoveryRefusals.InvalidRequest, invalid);
        }

        var statementBytes = DPlusRecoveryCodec.Serialize(statement);
        try
        {
            // Evidence copies first (archive only), then the records; the statement last, so a statement never exists without its evidence.
            await CreateArchiveOnlyAsync(DPlusRecoveryNames.RowCopy(org, stream, recoveryId), Encoding.UTF8.GetBytes(observed.Row!.JournalEventBodyJson), cancellationToken).ConfigureAwait(false);
            if (request.RestoreBytes is { } restore)
            {
                await CreateArchiveOnlyAsync(DPlusRecoveryNames.RestoreCopy(org, stream, recoveryId), restore, cancellationToken).ConfigureAwait(false);
            }

            await CreateBothAsync(DPlusRecoveryNames.Investigation(org, stream, recoveryId), investigationBytes, cancellationToken).ConfigureAwait(false);
            await CreateBothAsync(DPlusRecoveryNames.Evidence(org, stream, recoveryId), evidenceBytes, cancellationToken).ConfigureAwait(false);
            await CreateBothAsync(DPlusRecoveryNames.Statement(org, stream, recoveryId), statementBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArchiveUnavailableException or CheckpointStoreUnavailableException)
        {
            return DPlusRecoveryCommandResult.Unavailable(ex.Message, recoveryId);
        }

        return new DPlusRecoveryCommandResult(DPlusRecoveryCommandOutcome.Done, "PROPOSED", "statement recorded; two distinct recovery approvers must sign statement_sha256", recoveryId,
            DPlusRecoveryCodec.Sha256Hex(statementBytes));
    }

    // ------------------------------------------------------------------ decisions

    /// <summary>Records one signed decision (approve, reject or cancel) after verifying it; an unverifiable token is never stored.</summary>
    public async Task<DPlusRecoveryCommandResult> RecordDecisionAsync(Guid organizationId, string streamName, Guid recoveryId, string token, CancellationToken cancellationToken)
    {
        var ledger = await DPlusRecoveryLedger.LoadAsync(organizationId, streamName, Meta, durability.Records, durability.Approvals, cancellationToken).ConfigureAwait(false);
        if (ledger.Status != DPlusRecoveryLedgerStatus.Loaded)
        {
            return DPlusRecoveryCommandResult.Unavailable(ledger.Detail ?? "records unavailable", recoveryId);
        }

        if (ledger.Find(recoveryId) is not { Statement: { } statement, InvalidReason: null } view)
        {
            return DPlusRecoveryCommandResult.Refused(DPlusRecoveryRefusals.UnknownRecovery, "no valid statement for this recovery", recoveryId);
        }

        var verified = durability.Approvals.Verify(token, new DPlusRecoveryBinding(recoveryId, organizationId, streamName, statement.FailedRevision, statement.StopGeneration, view.StatementSha256Hex!), out var reason);
        if (verified is null)
        {
            return DPlusRecoveryCommandResult.Refused(DPlusRecoveryRefusals.DecisionInvalid, "the decision does not verify: " + reason, recoveryId);
        }

        if (verified.Decision == DPlusRecoveryClaims.Approve && (string.Equals(verified.ApproverId, statement.RequesterLabel, StringComparison.Ordinal)
            || string.Equals(verified.ApproverId, view.Investigation!.AnalystLabel, StringComparison.Ordinal)))
        {
            return DPlusRecoveryCommandResult.Refused(DPlusRecoveryRefusals.SeparationOfDuties, "an approver must be neither the requester nor the analyst", recoveryId);
        }

        try
        {
            var created = await CreateBothAsync(DPlusRecoveryNames.Decision(organizationId, streamName, recoveryId, verified.Jti), Encoding.ASCII.GetBytes(token), cancellationToken).ConfigureAwait(false);
            return created
                ? new DPlusRecoveryCommandResult(DPlusRecoveryCommandOutcome.Done, "DECISION_RECORDED", verified.ApproverId + " " + verified.Decision, recoveryId, view.StatementSha256Hex)
                : DPlusRecoveryCommandResult.Refused(DPlusRecoveryRefusals.DecisionInvalid, "another decision with this identifier is already stored (single use)", recoveryId);
        }
        catch (Exception ex) when (ex is ArchiveUnavailableException or CheckpointStoreUnavailableException)
        {
            return DPlusRecoveryCommandResult.Unavailable(ex.Message, recoveryId);
        }
    }

    // ------------------------------------------------------------------ execute

    /// <summary>
    /// A-4 §24.4 (SR-AU-7): preconditions in order, then the SR-C1 action, the row hint and the execution record last. A termination between
    /// any two steps leaves the stream stopped; a re-run continues idempotently from the records it finds.
    /// </summary>
    public async Task<DPlusRecoveryCommandResult> ExecuteAsync(Guid organizationId, string streamName, Guid recoveryId, string executorLabel, CancellationToken cancellationToken)
    {
        if (!durability.Approvals.Operational)
        {
            return DPlusRecoveryCommandResult.Refused(DPlusRecoveryRefusals.DualControlUnavailable, "recovery approval is not available in this environment (SR-PB-6)", recoveryId);
        }

        var ledger = await DPlusRecoveryLedger.LoadAsync(organizationId, streamName, Meta, durability.Records, durability.Approvals, cancellationToken).ConfigureAwait(false);
        if (ledger.Status != DPlusRecoveryLedgerStatus.Loaded)
        {
            return DPlusRecoveryCommandResult.Unavailable(ledger.Detail ?? "records unavailable", recoveryId);
        }

        if (ledger.Find(recoveryId) is not { Statement: { } statement, InvalidReason: null } view)
        {
            return DPlusRecoveryCommandResult.Refused(DPlusRecoveryRefusals.UnknownRecovery, "no valid statement for this recovery", recoveryId);
        }

        if (view.Outcome is { } outcome)
        {
            return DPlusRecoveryCommandResult.Refused(DPlusRecoveryRefusals.Terminal, "the recovery already has the outcome " + outcome.OutcomeKind, recoveryId);
        }

        var now = clock.GetUtcNow();
        var authorization = view.Authorization!;
        if (authorization.Rejected || authorization.Cancelled)
        {
            return await FailAsync(organizationId, streamName, recoveryId, authorization.Rejected ? DPlusRecoveryRefusals.Rejected : DPlusRecoveryRefusals.Cancelled, authorization.Reason, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!authorization.Authorized)
        {
            return DPlusRecoveryCommandResult.Refused(DPlusRecoveryRefusals.NotAuthorized, authorization.Reason, recoveryId);
        }

        // An existing execution record for this stop set: resume when it is ours, otherwise another recovery owns the stop set.
        var executionName = DPlusRecoveryNames.Execution(organizationId, streamName, statement.StopSetSha256Hex);
        var resuming = view.Execution is not null;
        if (!resuming && ledger.Records.TryGetValue(executionName, out var foreignBytes) && DPlusRecoveryCodec.TryParse<DPlusRecoveryExecution>(foreignBytes)?.RecoveryId != recoveryId)
        {
            return await FailAsync(organizationId, streamName, recoveryId, DPlusRecoveryRefusals.ExecutionConflict, "another recovery already executed this stop set", cancellationToken).ConfigureAwait(false);
        }

        if (!resuming)
        {
            if (now > statement.NotAfterUtc)
            {
                return await FailAsync(organizationId, streamName, recoveryId, DPlusRecoveryRefusals.WindowExpired, "the execution window has closed", cancellationToken).ConfigureAwait(false);
            }

            if (now < statement.NotBeforeUtc)
            {
                return DPlusRecoveryCommandResult.Refused(DPlusRecoveryRefusals.WindowNotOpen, "the execution window has not opened", recoveryId);
            }

            var fresh = authorization.Approvals.Where(a => a.ExpiresOn > now - DPlusRecoveryApprovalVerifier.ClockSkew && a.IssuedOn <= now + DPlusRecoveryApprovalVerifier.ClockSkew
                && a.NotBefore <= now + DPlusRecoveryApprovalVerifier.ClockSkew).Select(a => a.ApproverId).Distinct(StringComparer.Ordinal).Count();
            if (fresh < DPlusRecoveryApprovalVerifier.RequiredApprovals)
            {
                return DPlusRecoveryCommandResult.Refused(DPlusRecoveryRefusals.ApprovalsNotFresh, "two unexpired approvals from distinct approvers are required at execution", recoveryId);
            }

            // Freshly re-read evidence and stop set must equal what was approved.
            var actionDone = view.Action is not null;
            var restore = statement.Strategy == DPlusRecoveryStrategies.RestorePredecessor ? await ReadRestoreAsync(organizationId, streamName, recoveryId, view.Evidence!, cancellationToken).ConfigureAwait(false) : null;
            if (statement.Strategy == DPlusRecoveryStrategies.RestorePredecessor && restore is null)
            {
                return await FailAsync(organizationId, streamName, recoveryId, DPlusRecoveryRefusals.EvidenceChanged, "the bound predecessor copy is missing or changed", cancellationToken).ConfigureAwait(false);
            }

            var observed = await ObserveAsync(organizationId, streamName, statement.Strategy, restore, view.Evidence!.Anchor?.CheckpointSequence, actionDone, cancellationToken).ConfigureAwait(false);
            if (observed.Refusal is { Outcome: DPlusRecoveryCommandOutcome.Unavailable } unavailable)
            {
                return unavailable with { RecoveryId = recoveryId };
            }

            if (observed.Refusal is null && !string.Equals(DPlusRecoveryCodec.StopSetSha256Hex(observed.StopSet!), statement.StopSetSha256Hex, StringComparison.Ordinal))
            {
                return await FailAsync(organizationId, streamName, recoveryId, DPlusRecoveryRefusals.StopSetChanged, "the unresolved stop set differs from the approved one", cancellationToken).ConfigureAwait(false);
            }

            if (observed.Refusal is not null || !string.Equals(DPlusRecoveryCodec.Sha256Hex(DPlusRecoveryCodec.Serialize(observed.Evidence!)), statement.EvidenceSha256Hex, StringComparison.Ordinal))
            {
                return await FailAsync(organizationId, streamName, recoveryId, DPlusRecoveryRefusals.EvidenceChanged,
                    "the freshly observed evidence differs from the approved snapshot" + (observed.Refusal is { } r ? " (" + r.Code + ")" : string.Empty), cancellationToken).ConfigureAwait(false);
            }

            try
            {
                // 1. SR-C1 only: the action record, then the create-only predecessor write with exactly the bound bytes.
                if (statement.Strategy == DPlusRecoveryStrategies.RestorePredecessor)
                {
                    var objectName = DPlusEvidenceCommitments.PredecessorObjectName(organizationId, streamName, statement.FailedRevision - 1);
                    var action = new DPlusRecoveryAction(DPlusRecoveryAction.SchemaV1, recoveryId, statement.Strategy, objectName, view.Evidence.RestoreSha256Hex!);
                    await CreateBothAsync(DPlusRecoveryNames.Action(organizationId, streamName, recoveryId), DPlusRecoveryCodec.Serialize(action), cancellationToken).ConfigureAwait(false);
                    try
                    {
                        await archive.AppendAsync(organizationId, streamName, statement.FailedRevision - 1, restore!, System.Security.Cryptography.SHA256.HashData(restore!), cancellationToken).ConfigureAwait(false);
                    }
                    catch (ArchiveConflictException)
                    {
                        return await FailAsync(organizationId, streamName, recoveryId, DPlusRecoveryRefusals.RestoreConflict, "another object exists at the predecessor key; nothing is overwritten", cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex) when (ex is ArchiveUnavailableException or CheckpointStoreUnavailableException)
            {
                return DPlusRecoveryCommandResult.Unavailable(ex.Message, recoveryId);
            }
        }

        // 2. The row hint (scheduling only; never trusted). 3. The execution record, last.
        if (await SetHintAsync(organizationId, streamName, statement.FailedRevision, recoveryId, cancellationToken).ConfigureAwait(false) is { } hintFailure)
        {
            return hintFailure;
        }

        var execution = new DPlusRecoveryExecution(DPlusRecoveryExecution.SchemaV1, recoveryId, organizationId, streamName, statement.FailedRevision, statement.StopGeneration, view.StatementSha256Hex!,
            statement.StopSetSha256Hex, executorLabel, ToolVersion);
        try
        {
            if (!await CreateBothAsync(executionName, DPlusRecoveryCodec.Serialize(execution), cancellationToken).ConfigureAwait(false) && !resuming)
            {
                return await FailAsync(organizationId, streamName, recoveryId, DPlusRecoveryRefusals.ExecutionConflict, "a concurrent execution record exists for this stop set", cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is ArchiveUnavailableException or CheckpointStoreUnavailableException)
        {
            return DPlusRecoveryCommandResult.Unavailable(ex.Message, recoveryId);
        }

        return new DPlusRecoveryCommandResult(DPlusRecoveryCommandOutcome.Done, resuming ? "EXECUTION_RESUMED" : "EXECUTED",
            "the lift takes effect on the next D+ pass; D+ re-verifies the records every pass", recoveryId, view.StatementSha256Hex);
    }

    // ------------------------------------------------------------------ outcome and status

    /// <summary>Writes the terminal outcome once it is observable: Completed (the recovered revision is archived as expected) or Failed (a later-generation stop at it).</summary>
    public async Task<DPlusRecoveryCommandResult> RecordOutcomeAsync(Guid organizationId, string streamName, Guid recoveryId, CancellationToken cancellationToken)
    {
        var ledger = await DPlusRecoveryLedger.LoadAsync(organizationId, streamName, Meta, durability.Records, durability.Approvals, cancellationToken).ConfigureAwait(false);
        if (ledger.Status != DPlusRecoveryLedgerStatus.Loaded)
        {
            return DPlusRecoveryCommandResult.Unavailable(ledger.Detail ?? "records unavailable", recoveryId);
        }

        if (ledger.Find(recoveryId) is not { Statement: { } statement, InvalidReason: null } view)
        {
            return DPlusRecoveryCommandResult.Refused(DPlusRecoveryRefusals.UnknownRecovery, "no valid statement for this recovery", recoveryId);
        }

        if (view.Outcome is { } existing)
        {
            return new DPlusRecoveryCommandResult(DPlusRecoveryCommandOutcome.Done, existing.OutcomeKind.ToUpperInvariant(), existing.Detail, recoveryId);
        }

        if (!view.Executed)
        {
            return DPlusRecoveryCommandResult.Refused(DPlusRecoveryRefusals.NotAuthorized, "the recovery has not been executed", recoveryId);
        }

        try
        {
            var read = await Inventory.ReadEntryAsync(organizationId, streamName, statement.FailedRevision, cancellationToken).ConfigureAwait(false);
            if (read.Status == StoredObjectStatus.Present && string.Equals(DPlusRecoveryCodec.Sha256Hex(read.Bytes!), statement.Expected.RowBodySha256Hex, StringComparison.Ordinal))
            {
                return await WriteOutcomeAsync(organizationId, streamName, recoveryId, DPlusRecoveryOutcome.Completed, "RECOVERED_REVISION_ARCHIVED", "the recovered revision is archived with the expected digest",
                    cancellationToken).ConfigureAwait(false);
            }

            var markers = await Inventory.ReadStopMarkersAsync(organizationId, streamName, cancellationToken).ConfigureAwait(false);
            var later = markers.Select(m => DPlusStreamStops.ParseMarkerObjectName(m.ObjectName, organizationId, streamName))
                .Any(n => n is { } p && p.Revision == statement.FailedRevision && p.Generation > statement.StopGeneration);
            if (later)
            {
                return await WriteOutcomeAsync(organizationId, streamName, recoveryId, DPlusRecoveryOutcome.Failed, DPlusRecoveryRefusals.EvidenceChanged,
                    "D+ stopped the recovered revision again at a later generation", cancellationToken).ConfigureAwait(false);
            }
        }
        catch (ArchiveUnavailableException ex)
        {
            return DPlusRecoveryCommandResult.Unavailable(ex.Message, recoveryId);
        }

        return new DPlusRecoveryCommandResult(DPlusRecoveryCommandOutcome.Done, "PENDING_DELIVERY", "executed; the recovered revision is not yet archived", recoveryId);
    }

    public async Task<DPlusRecoveryStatus?> StatusAsync(Guid organizationId, string streamName, Guid recoveryId, CancellationToken cancellationToken)
    {
        var ledger = await DPlusRecoveryLedger.LoadAsync(organizationId, streamName, Meta, durability.Records, durability.Approvals, cancellationToken).ConfigureAwait(false);
        if (ledger.Status != DPlusRecoveryLedgerStatus.Loaded || ledger.Find(recoveryId) is not { } view)
        {
            return null;
        }

        var approvals = view.Authorization?.Approvals.Count ?? 0;
        var state = view switch
        {
            { InvalidReason: not null } => "Invalid",
            { Outcome: { } o } => o.OutcomeKind,
            { Executed: true } => "Executed",
            { Authorization.Rejected: true } => "Rejected",
            { Authorization.Cancelled: true } => "Cancelled",
            { Action: not null } => "Executing",
            { Statement: { } s } when clock.GetUtcNow() > s.NotAfterUtc => "Expired",
            { Authorized: true } => "Approved",
            _ => "Proposed",
        };
        return new DPlusRecoveryStatus(recoveryId, state, view.InvalidReason ?? view.Authorization?.Reason ?? string.Empty, ledger.Generation, approvals, view.Executed, view.Outcome?.OutcomeKind);
    }

    // ------------------------------------------------------------------ observation

    private sealed record Observation(DPlusRecoveryCommandResult? Refusal, DPlusRecoveryEvidence? Evidence, IReadOnlyList<DPlusStopRecordReference>? StopSet, string? EligibilityClass,
        SecurityJournalEntry? Row);

    private static Observation Refuse(string code, string detail) => new(DPlusRecoveryCommandResult.Refused(code, detail), null, null, null, null);

    private static Observation Unavailable(string detail) => new(DPlusRecoveryCommandResult.Unavailable(detail), null, null, null, null);

    /// <summary>
    /// Reads the current state read-only and builds the deterministic evidence snapshot plus the complete unresolved stop set. Used by the
    /// proposal and again, unchanged, by the executor (the digests must match). <paramref name="treatRestoredAsAbsent"/> lets a resumed SR-C1
    /// execution see its own completed predecessor write as the approved absent state.
    /// </summary>
    private async Task<Observation> ObserveAsync(Guid org, string stream, string strategy, byte[]? restoreBytes, long? anchorSequence, bool treatRestoredAsAbsent, CancellationToken cancellationToken)
    {
        try
        {
            // Organization quarantine (SR-PB-1) and the failed row come from AuthorityDb (Observed only, never trusted for the lift).
            var ledger = await DPlusRecoveryLedger.LoadAsync(org, stream, Meta, durability.Records, durability.Approvals, cancellationToken).ConfigureAwait(false);
            if (ledger.Status != DPlusRecoveryLedgerStatus.Loaded)
            {
                return Unavailable(ledger.Detail ?? "recovery records unavailable");
            }

            var markerObjects = await Inventory.ReadStopMarkersAsync(org, stream, cancellationToken).ConfigureAwait(false);
            var findingBytes = await findings.ListAsync(org, stream, cancellationToken).ConfigureAwait(false);
            var stopSet = new List<DPlusStopRecordReference>();
            var codes = new SortedSet<string>(StringComparer.Ordinal);
            var integrityCodes = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var item in markerObjects)
            {
                var name = DPlusStreamStops.ParseMarkerObjectName(item.ObjectName, org, stream);
                var marker = name is { } p ? DPlusStreamStops.TryParse(item.Bytes, org, stream, p.Revision, p.Generation) : null;
                var reference = marker is not null
                    ? new DPlusStopRecordReference(DPlusStopRecordReference.Marker, item.ObjectName, DPlusRecoveryCodec.Sha256Hex(item.Bytes), name!.Value.Revision, name.Value.Generation)
                    : new DPlusStopRecordReference(DPlusStopRecordReference.InvalidMarkerObject, item.ObjectName, DPlusRecoveryCodec.Sha256Hex(item.Bytes), name?.Revision, null);
                if (!ledger.Covers(reference))
                {
                    stopSet.Add(reference);
                    if (marker is not null)
                    {
                        codes.Add(marker.FindingCode + (marker.Subtype is null ? string.Empty : "/" + marker.Subtype));
                    }
                    else
                    {
                        integrityCodes.Add(JournalFindingCodes.DPlusStopMarkerInvalid);
                    }
                }
            }

            foreach (var bytes in findingBytes)
            {
                if (DPlusStreamStops.TryParseStoppingRefusal(bytes) is { } refusal)
                {
                    var reference = new DPlusStopRecordReference(DPlusStopRecordReference.Finding, "findings/" + org.ToString("D") + "/" + stream + "/" + refusal.Identity + ".json",
                        DPlusRecoveryCodec.Sha256Hex(bytes), refusal.FirstRevision, refusal.StopGeneration ?? 0);
                    if (!ledger.Covers(reference))
                    {
                        stopSet.Add(reference);
                        codes.Add(refusal.FindingCode + (refusal.Subtype is null ? string.Empty : "/" + refusal.Subtype));
                    }
                }
                else if (System.Text.Json.JsonSerializer.Deserialize<JournalVerificationFinding>(bytes, CanonicalJson.Options) is { SweepOutcome: "DeliveryStopCheck" } check
                    && check.FindingCode is JournalFindingCodes.DPlusStopMarkerInvalid or JournalFindingCodes.DPlusStopRecordIncomplete or JournalFindingCodes.DPlusStopMarkerConflict)
                {
                    integrityCodes.Add(check.FindingCode);
                }
            }

            var stops = DPlusRecoveryCodec.SortStopSet(stopSet);
            var stopping = stops.Where(s => s.RecordKind != DPlusStopRecordReference.InvalidMarkerObject).ToList();
            if (stopping.Count == 0)
            {
                return Refuse(DPlusRecoveryRefusals.NoDurableStop, "the stream has no unresolved durable stop record (SR-EL-1, SR-EL-7)");
            }

            if (stopping.Select(s => s.FailedRevision).Distinct().Count() != 1 || stopping.Any(s => s.StopGeneration != ledger.Generation)
                || stops.Any(s => s.RecordKind == DPlusStopRecordReference.InvalidMarkerObject && s.FailedRevision is { } r && r != stopping[0].FailedRevision))
            {
                return Refuse(DPlusRecoveryRefusals.StopSetNotSingleScope, "the unresolved stop records do not name exactly one revision at the stream's current generation");
            }

            if (codes.Any(c => c.StartsWith(JournalFindingCodes.ArchiveRegression, StringComparison.Ordinal)))
            {
                return Refuse(DPlusRecoveryRefusals.ArchiveInstanceBlocked, "an archive-instance stop needs SR-D, which is not operational (SR-PB-3)");
            }

            var k = stopping[0].FailedRevision!.Value;
            var eligibility = codes.Count == 1 ? codes.Min! : "Mixed:" + string.Join(",", codes);
            if (strategy == DPlusRecoveryStrategies.RestorePredecessor && !string.Equals(eligibility, JournalFindingCodes.PredecessorMissing, StringComparison.Ordinal))
            {
                return Refuse(DPlusRecoveryRefusals.StrategyPreconditionFailed, "SR-C1 applies only to a missing predecessor (" + eligibility + ")");
            }

            SecurityJournalEntry? row;
            bool quarantined;
            await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
            {
                var state = await scope.LockOrganizationAsync(org, cancellationToken).ConfigureAwait(false);
                quarantined = state?.IntegrityQuarantineEnabled ?? true;
                row = state is null ? null : await scope.FindJournalEntryAsync(org, stream, k, cancellationToken).ConfigureAwait(false);
            }

            if (quarantined)
            {
                return Refuse(DPlusRecoveryRefusals.OrganizationQuarantined, "the organization is under integrity quarantine or unknown (SR-PB-1)");
            }

            var rowBody = row is null ? null : Encoding.UTF8.GetBytes(row.JournalEventBodyJson);
            if (row is null || !CanonicalJson.Digest(rowBody!).AsSpan().SequenceEqual(row.JournalEventDigest))
            {
                return Refuse(DPlusRecoveryRefusals.EvidenceUnavailable, "the failed row is missing or its body no longer matches its digest (SR-PB-7)");
            }

            var pin = await DPlusArchivePins.ResolveAsync(durability, org, stream, cancellationToken).ConfigureAwait(false);
            if (pin.Status == DPlusPinStatus.Unavailable)
            {
                return Unavailable(pin.Detail);
            }

            if (pin.Status != DPlusPinStatus.Pinned)
            {
                return Refuse(DPlusRecoveryRefusals.ArchiveInstanceBlocked, "no trusted archive instance pin (" + pin.Status + "); recovery across instances is not operational (SR-PB-3)");
            }

            // D+ evaluation without any write (no commitment callback), on the current evidence or, for SR-C1, with the anchored copy.
            var pass = new AuditDeliveryPass { Pin = AuditArchivePinStatus.Pinned, ExpectedArchiveInstanceId = pin.ExpectedArchiveInstanceId };
            AuditDeliveryEvidence? gathered = null;
            var verdict = await AuditDeliveryEnforcement.EvaluateAsync(row, Inventory, pass, cancellationToken, (e, _) =>
            {
                gathered = e;
                return Task.FromResult<AuditDeliveryVerdict?>(null);
            }).ConfigureAwait(false);
            if (verdict.Kind == AuditDeliveryVerdictKind.Unavailable)
            {
                return Unavailable(verdict.Detail ?? "archive unavailable");
            }

            if (gathered is null || pass.SentinelBytes is null)
            {
                return Refuse(DPlusRecoveryRefusals.ArchiveInstanceBlocked, "the archive sentinel or instance does not match the pin: " + verdict.ErrorCode + " (SR-PB-3)");
            }

            var predecessorState = gathered.PredecessorState;
            var predecessorSha = gathered.PredecessorBytes is { } pb ? DPlusRecoveryCodec.Sha256Hex(pb) : null;
            DPlusRecoveryAnchor? anchor = null;
            string? restoreSha = null;
            if (strategy == DPlusRecoveryStrategies.RestorePredecessor)
            {
                restoreSha = DPlusRecoveryCodec.Sha256Hex(restoreBytes!);
                if (treatRestoredAsAbsent && predecessorState == AuditPredecessorState.Present && string.Equals(predecessorSha, restoreSha, StringComparison.Ordinal))
                {
                    predecessorState = AuditPredecessorState.Absent; // our own completed create-only write (resumed execution)
                    predecessorSha = null;
                }

                if (predecessorState != AuditPredecessorState.Absent)
                {
                    return Refuse(DPlusRecoveryRefusals.StrategyPreconditionFailed, "SR-C1 requires the predecessor to be absent (nothing is ever overwritten)");
                }

                var chain = await JournalVerificationCheckpoints.DiscoverAsync(durability.Checkpoints, org, stream, CheckpointDiscoveryMode.FullChain, 0, 1000, cancellationToken).ConfigureAwait(false);
                if (chain.Outcome == CheckpointDiscoveryOutcome.Inconclusive)
                {
                    return Unavailable("checkpoint chain: " + chain.Detail);
                }

                if (chain.Outcome != CheckpointDiscoveryOutcome.Head || chain.Head is not { } head)
                {
                    return Refuse(DPlusRecoveryRefusals.AnchorUnavailable, "no valid checkpoint chain to anchor the predecessor (" + chain.Outcome + "; SR-PB-8)");
                }

                var sequence = anchorSequence ?? head.CheckpointSequence;
                var anchorRead = await durability.Checkpoints.ReadAsync(org, stream, sequence, cancellationToken).ConfigureAwait(false);
                var anchorCheckpoint = anchorRead.Status == StoredObjectStatus.Present ? JournalVerificationCheckpoints.Parse(anchorRead.Bytes).Checkpoint : null;
                if (anchorCheckpoint is null || sequence > head.CheckpointSequence || anchorCheckpoint.VerifiedRevision != k - 1 || head.VerifiedRevision != k - 1
                    || !string.Equals(anchorCheckpoint.ArchiveInstanceId, pin.ExpectedArchiveInstanceId, StringComparison.Ordinal) || anchorCheckpoint.VerifiedDigest is null)
                {
                    return Refuse(DPlusRecoveryRefusals.AnchorUnavailable, "no trusted checkpoint verifies exactly the predecessor revision in the pinned instance");
                }

                if (!string.Equals(Convert.ToHexStringLower(anchorCheckpoint.VerifiedDigest), restoreSha, StringComparison.Ordinal))
                {
                    return Refuse(DPlusRecoveryRefusals.StrategyPreconditionFailed, "the predecessor copy does not equal the trusted checkpoint digest");
                }

                anchor = new DPlusRecoveryAnchor(sequence, DPlusRecoveryCodec.Sha256Hex(anchorRead.Bytes!), anchorCheckpoint.VerifiedRevision, Convert.ToHexStringLower(anchorCheckpoint.VerifiedDigest),
                    anchorCheckpoint.AssuranceLevel.ToString(), anchorCheckpoint.ArchiveInstanceId);

                // D+ on the hypothetical evidence: the anchored copy as the predecessor.
                var hypothetical = new AuditDeliveryPass { Pin = AuditArchivePinStatus.Pinned, ExpectedArchiveInstanceId = pin.ExpectedArchiveInstanceId, LastDeliveredRevision = k - 1, LastDeliveredBytes = restoreBytes };
                verdict = await AuditDeliveryEnforcement.EvaluateAsync(row, Inventory, hypothetical, cancellationToken).ConfigureAwait(false);
            }
            else if (treatRestoredAsAbsent)
            {
                return Refuse(DPlusRecoveryRefusals.InvalidRequest, "only SR-C1 has an action");
            }

            // The W2-S commitment of the failed revision at this generation (both copies must agree).
            var commitmentName = DPlusRecoveryNames.Commitment(org, stream, k, ledger.Generation);
            var archived = await Meta.ReadMetaAsync(DPlusRecoveryNames.ArchiveCommitmentPrefix + commitmentName, cancellationToken).ConfigureAwait(false);
            var stored = await durability.Records.ReadAsync(DPlusRecoveryNames.StoreCommitmentPrefix + commitmentName, cancellationToken).ConfigureAwait(false);
            if (archived.Status == StoredObjectStatus.Present && stored.Status == StoredObjectStatus.Present && !archived.Bytes!.AsSpan().SequenceEqual(stored.Bytes))
            {
                return Refuse(DPlusRecoveryRefusals.EvidenceContradictory, "the two copies of the evidence commitment differ (SR-PB-7)");
            }

            var commitmentBytes = archived.Status == StoredObjectStatus.Present ? archived.Bytes : stored.Status == StoredObjectStatus.Present ? stored.Bytes : null;
            var evidence = new DPlusRecoveryEvidence(DPlusRecoveryEvidence.SchemaV1, org, stream, k, ledger.Generation, row.SecurityJournalEntryId, DPlusRecoveryCodec.Sha256Hex(rowBody!),
                DPlusRecoveryCodec.Hex(row.JournalPreviousDigest), DPlusEvidenceCommitments.PredecessorObjectName(org, stream, k - 1), predecessorState.ToString(), predecessorSha,
                pin.ExpectedArchiveInstanceId!, pass.ArchiveInstanceId!, DPlusRecoveryCodec.Sha256Hex(pass.SentinelBytes), pin.ProvisioningRecordSha256Hex,
                commitmentBytes is null ? null : commitmentName, commitmentBytes is null ? null : DPlusRecoveryCodec.Sha256Hex(commitmentBytes), anchor, restoreSha, [.. integrityCodes],
                verdict.Kind == AuditDeliveryVerdictKind.Deliver ? nameof(AuditDeliveryVerdictKind.Deliver) : verdict.ErrorCode, DPlusEvidenceCommitment.CurrentRuleSetVersion);
            return new Observation(null, evidence, stops, eligibility, row);
        }
        catch (ArchiveUnavailableException ex)
        {
            return Unavailable("archive: " + ex.Message);
        }
        catch (CheckpointStoreUnavailableException ex)
        {
            return Unavailable("journal-verification store: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------ writes

    /// <summary>Create-only in both stores. True when both now hold exactly these bytes; false on a conflict (different bytes; nothing overwritten).</summary>
    private async Task<bool> CreateBothAsync(string relativeName, byte[] bytes, CancellationToken cancellationToken)
    {
        var a = await Meta.CreateMetaAsync(DPlusRecoveryNames.ArchiveRecoveryPrefix + relativeName, bytes, cancellationToken).ConfigureAwait(false);
        var s = await durability.Records.CreateAsync(DPlusRecoveryNames.StoreRecoveryPrefix + relativeName, bytes, cancellationToken).ConfigureAwait(false);
        return a.Status != CheckpointCreateStatus.Conflict && s.Status != CheckpointCreateStatus.Conflict;
    }

    private async Task CreateArchiveOnlyAsync(string relativeName, byte[] bytes, CancellationToken cancellationToken)
    {
        if ((await Meta.CreateMetaAsync(DPlusRecoveryNames.ArchiveRecoveryPrefix + relativeName, bytes, cancellationToken).ConfigureAwait(false)).Status == CheckpointCreateStatus.Conflict)
        {
            throw new ArchiveUnavailableException("an evidence copy with other bytes already exists at " + relativeName);
        }
    }

    private async Task<byte[]?> ReadRestoreAsync(Guid org, string stream, Guid recoveryId, DPlusRecoveryEvidence evidence, CancellationToken cancellationToken)
    {
        var read = await Meta.ReadMetaAsync(DPlusRecoveryNames.ArchiveRecoveryPrefix + DPlusRecoveryNames.RestoreCopy(org, stream, recoveryId), cancellationToken).ConfigureAwait(false);
        return read.Status == StoredObjectStatus.Present && string.Equals(DPlusRecoveryCodec.Sha256Hex(read.Bytes!), evidence.RestoreSha256Hex, StringComparison.Ordinal) ? read.Bytes : null;
    }

    /// <summary>SR-DL-5: <c>VerificationFailed → Pending</c> with the recovery hint (a scheduling hint only; D+ never trusts it).</summary>
    private async Task<DPlusRecoveryCommandResult?> SetHintAsync(Guid org, string stream, long revision, Guid recoveryId, CancellationToken cancellationToken)
    {
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        if (await scope.LockOrganizationAsync(org, cancellationToken).ConfigureAwait(false) is null
            || await scope.FindJournalEntryAsync(org, stream, revision, cancellationToken).ConfigureAwait(false) is not { } row)
        {
            return DPlusRecoveryCommandResult.Refused(DPlusRecoveryRefusals.EvidenceUnavailable, "the failed row is missing", recoveryId);
        }

        if (string.Equals(row.ArchiveDeliveryState, ArchiveDeliveryStates.Anchored, StringComparison.Ordinal))
        {
            return null; // already delivered (a resumed run after D+ delivered it)
        }

        row.ArchiveDeliveryState = ArchiveDeliveryStates.Pending;
        row.LastArchiveErrorCode = ArchiveProcessor.RecoveryAuthorizedCodePrefix + recoveryId.ToString("D");
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return null;
    }

    private async Task<DPlusRecoveryCommandResult> FailAsync(Guid org, string stream, Guid recoveryId, string code, string detail, CancellationToken cancellationToken)
    {
        var written = await WriteOutcomeAsync(org, stream, recoveryId, DPlusRecoveryOutcome.Failed, code, detail, cancellationToken).ConfigureAwait(false);
        return written.Outcome == DPlusRecoveryCommandOutcome.Unavailable ? written : new DPlusRecoveryCommandResult(DPlusRecoveryCommandOutcome.Failed, code, detail, recoveryId);
    }

    private async Task<DPlusRecoveryCommandResult> WriteOutcomeAsync(Guid org, string stream, Guid recoveryId, string kind, string code, string detail, CancellationToken cancellationToken)
    {
        var outcome = new DPlusRecoveryOutcome(DPlusRecoveryOutcome.SchemaV1, recoveryId, kind, code, detail, TruncateToSeconds(clock.GetUtcNow().ToUniversalTime()), ToolVersion);
        try
        {
            await CreateBothAsync(DPlusRecoveryNames.Outcome(org, stream, recoveryId), DPlusRecoveryCodec.Serialize(outcome), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArchiveUnavailableException or CheckpointStoreUnavailableException)
        {
            return DPlusRecoveryCommandResult.Unavailable(ex.Message, recoveryId);
        }

        return new DPlusRecoveryCommandResult(DPlusRecoveryCommandOutcome.Done, kind.ToUpperInvariant(), detail, recoveryId);
    }

    private static DateTimeOffset TruncateToSeconds(DateTimeOffset value) => DateTimeOffset.FromUnixTimeSeconds(value.ToUnixTimeSeconds());
}
