using System.Text;
using AuthorizationAuthority.Application.Common.Ports;

namespace AuthorizationAuthority.Application.Features.Journal.Recovery;

public enum DPlusRecoveryLedgerStatus
{
    Loaded,

    /// <summary>The archive copy could not be listed: archive unavailability (the pass ends; DP-7).</summary>
    ArchiveUnavailable,

    /// <summary>The verification-store copy could not be listed: inconclusive, the audit stream waits (never "no recovery").</summary>
    StoreUnavailable,
}

/// <summary>A record that was ignored: malformed, non-canonical, for another scope, badly signed, or contradictory between the stores.</summary>
public sealed record DPlusRecoveryRecordProblem(string RelativeName, bool Contradictory, string Reason);

/// <summary>One recovery as derived from its records. Nothing here is mutable state: every value is recomputed from create-only records.</summary>
public sealed record DPlusRecoveryView(Guid RecoveryId, DPlusRecoveryStatement? Statement, string? StatementSha256Hex, DPlusStreamInvestigation? Investigation, DPlusRecoveryEvidence? Evidence,
    IReadOnlyList<DPlusVerifiedDecision> Decisions, DPlusRecoveryAuthorization? Authorization, DPlusRecoveryExecution? Execution, DPlusRecoveryAction? Action, DPlusRecoveryOutcome? Outcome,
    string? InvalidReason)
{
    public bool Authorized => InvalidReason is null && Authorization is { Authorized: true };

    /// <summary>A-4 §24.5 (SR-DL-1): authorized, never rejected or cancelled, and an execution record for its stop set names it.</summary>
    public bool Executed => Authorized && Execution is not null;
}

/// <summary>
/// SEC-BL-01 G14-R2 (A-4 §24.5): the recovery state of one (organization, stream), derived on every pass from the create-only records in
/// <b>both</b> stores. A record is usable when a copy exists in either store; two copies that differ make it contradictory and unusable.
/// AuthorityDb is never consulted. A stop record is resolved only when an executed recovery's signed statement names it exactly (object name
/// and SHA-256) at the statement's stop generation. Deleting any recovery record therefore re-stops the stream (fail closed); forging one
/// requires the approver private keys.
/// </summary>
public sealed class DPlusRecoveryLedger
{
    private DPlusRecoveryLedger(DPlusRecoveryLedgerStatus status, string? detail, IReadOnlyList<DPlusRecoveryView> recoveries, IReadOnlyList<DPlusRecoveryRecordProblem> problems,
        IReadOnlyDictionary<string, byte[]> records)
    {
        Status = status;
        Detail = detail;
        Recoveries = recoveries;
        Problems = problems;
        Records = records;
        var executed = recoveries.Where(r => r.Executed).ToList();
        Generation = executed.Count == 0 ? 0 : executed.Max(r => r.Statement!.StopGeneration) + 1;
        var top = executed.Where(r => r.Statement!.StopGeneration == Generation - 1).ToList();
        ActiveLift = top.Count == 0 ? null : top.OrderBy(r => r.RecoveryId).First();
        LiftContradictory = top.Select(r => r.Statement!.FailedRevision).Distinct().Count() > 1;
    }

    public DPlusRecoveryLedgerStatus Status { get; }

    public string? Detail { get; }

    public IReadOnlyList<DPlusRecoveryView> Recoveries { get; }

    public IReadOnlyList<DPlusRecoveryRecordProblem> Problems { get; }

    /// <summary>The usable bytes of every record by relative name (one copy, or two identical copies).</summary>
    public IReadOnlyDictionary<string, byte[]> Records { get; }

    /// <summary>
    /// A-4 §24.6 (stop generations): one plus the highest stop generation resolved by an executed recovery, or 0. New stops, findings and
    /// W2-S commitments are written at this generation.
    /// </summary>
    public int Generation { get; }

    /// <summary>The executed recovery of the latest generation (its revision may still be awaiting delivery: SR-DL-9).</summary>
    public DPlusRecoveryView? ActiveLift { get; }

    /// <summary>Two executed recoveries of the latest generation name different revisions: no lift is trusted (fail closed).</summary>
    public bool LiftContradictory { get; }

    public static DPlusRecoveryLedger Unavailable(DPlusRecoveryLedgerStatus status, string detail) => new(status, detail, [], [], new Dictionary<string, byte[]>(StringComparer.Ordinal));

    /// <summary>True when an executed recovery covers <paramref name="record"/> exactly (name, SHA-256 and, where known, the record's generation).</summary>
    public bool Covers(DPlusStopRecordReference record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return Recoveries.Any(r => r.Executed && r.Statement!.StopSet.Any(s => string.Equals(s.ObjectName, record.ObjectName, StringComparison.Ordinal)
            && string.Equals(s.Sha256Hex, record.Sha256Hex, StringComparison.Ordinal) && string.Equals(s.RecordKind, record.RecordKind, StringComparison.Ordinal)
            && (record.StopGeneration is null || record.StopGeneration == r.Statement.StopGeneration)));
    }

    public DPlusRecoveryView? Find(Guid recoveryId) => Recoveries.FirstOrDefault(r => r.RecoveryId == recoveryId);

    public static async Task<DPlusRecoveryLedger> LoadAsync(Guid organizationId, string streamName, IArchiveMetaRecords archive, IJournalVerificationRecordStore store, DPlusRecoveryApprovalVerifier verifier,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(verifier);
        var root = DPlusRecoveryNames.StreamRoot(organizationId, streamName);
        IReadOnlyList<ArchiveMetaObject> archiveCopies;
        IReadOnlyList<ArchiveMetaObject> storeCopies;
        try
        {
            archiveCopies = await archive.ListMetaAsync(DPlusRecoveryNames.ArchiveRecoveryPrefix + root, cancellationToken).ConfigureAwait(false);
        }
        catch (ArchiveUnavailableException ex)
        {
            return Unavailable(DPlusRecoveryLedgerStatus.ArchiveUnavailable, ex.Message);
        }

        try
        {
            storeCopies = await store.ListAsync(DPlusRecoveryNames.StoreRecoveryPrefix + root, cancellationToken).ConfigureAwait(false);
        }
        catch (CheckpointStoreUnavailableException ex)
        {
            return Unavailable(DPlusRecoveryLedgerStatus.StoreUnavailable, ex.Message);
        }

        var problems = new List<DPlusRecoveryRecordProblem>();
        var copies = new Dictionary<string, (byte[]? Archive, byte[]? Store)>(StringComparer.Ordinal);
        foreach (var item in archiveCopies)
        {
            var relative = item.ObjectName[DPlusRecoveryNames.ArchiveRecoveryPrefix.Length..];
            copies[relative] = (item.Bytes, copies.TryGetValue(relative, out var c) ? c.Store : null);
        }

        foreach (var item in storeCopies)
        {
            var relative = item.ObjectName[DPlusRecoveryNames.StoreRecoveryPrefix.Length..];
            copies[relative] = (copies.TryGetValue(relative, out var c) ? c.Archive : null, item.Bytes);
        }

        var records = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (name, pair) in copies)
        {
            if (pair.Archive is { } a && pair.Store is { } s && !a.AsSpan().SequenceEqual(s))
            {
                problems.Add(new DPlusRecoveryRecordProblem(name, true, "the two copies of this record differ"));
                continue;
            }

            records[name] = pair.Archive ?? pair.Store!;
        }

        // Group by recovery id; executions are stream-level.
        var byRecovery = new Dictionary<Guid, Dictionary<string, byte[]>>();
        var executions = new List<(string Name, byte[] Bytes)>();
        foreach (var (name, bytes) in records)
        {
            var rest = name.StartsWith(root, StringComparison.Ordinal) ? name[root.Length..] : null;
            var slash = rest?.IndexOf('/', StringComparison.Ordinal) ?? -1;
            if (rest is null || slash <= 0)
            {
                problems.Add(new DPlusRecoveryRecordProblem(name, false, "unexpected name under the recovery prefix"));
                continue;
            }

            var head = rest[..slash];
            var tail = rest[(slash + 1)..];
            if (head == "executions")
            {
                executions.Add((name, bytes));
                continue;
            }

            if (!Guid.TryParseExact(head, "D", out var recoveryId) || !IsKnownRecoveryFile(tail))
            {
                problems.Add(new DPlusRecoveryRecordProblem(name, false, "unexpected name under the recovery prefix"));
                continue;
            }

            if (!byRecovery.TryGetValue(recoveryId, out var files))
            {
                byRecovery[recoveryId] = files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            }

            files[tail] = bytes;
        }

        var views = new List<DPlusRecoveryView>();
        foreach (var (recoveryId, files) in byRecovery.OrderBy(p => p.Key))
        {
            views.Add(BuildView(organizationId, streamName, recoveryId, files, verifier, problems));
        }

        // Executions: each names one recovery and must equal that recovery's statement digest, stop set and generation.
        foreach (var (name, bytes) in executions)
        {
            var execution = DPlusRecoveryCodec.TryParse<DPlusRecoveryExecution>(bytes);
            var view = execution is null ? null : views.FirstOrDefault(v => v.RecoveryId == execution.RecoveryId);
            var expectedName = execution is null ? null : DPlusRecoveryNames.Execution(organizationId, streamName, execution.StopSetSha256Hex);
            if (execution is null || view?.Statement is null || !string.Equals(name, expectedName, StringComparison.Ordinal)
                || !string.Equals(execution.ExecutionSchema, DPlusRecoveryExecution.SchemaV1, StringComparison.Ordinal)
                || execution.OrganizationId != organizationId || !string.Equals(execution.StreamName, streamName, StringComparison.Ordinal)
                || !string.Equals(execution.StatementSha256Hex, view.StatementSha256Hex, StringComparison.Ordinal)
                || !string.Equals(execution.StopSetSha256Hex, view.Statement.StopSetSha256Hex, StringComparison.Ordinal)
                || execution.FailedRevision != view.Statement.FailedRevision || execution.StopGeneration != view.Statement.StopGeneration)
            {
                problems.Add(new DPlusRecoveryRecordProblem(name, false, "an execution record that names no valid statement of this stop set"));
                continue;
            }

            if (view.Execution is not null)
            {
                problems.Add(new DPlusRecoveryRecordProblem(name, false, "a second execution record names the same recovery"));
                continue;
            }

            views[views.IndexOf(view)] = view with { Execution = execution };
        }

        return new DPlusRecoveryLedger(DPlusRecoveryLedgerStatus.Loaded, null, views, problems, records);
    }

    private static bool IsKnownRecoveryFile(string tail) => tail is "statement.json" or "investigation.json" or "evidence.json" or "evidence/row.json" or "evidence/restore.json"
        or "action.json" or "outcome.json" || (tail.StartsWith("decisions/", StringComparison.Ordinal) && tail.EndsWith(".jws", StringComparison.Ordinal)
            && Guid.TryParseExact(tail["decisions/".Length..^".jws".Length], "D", out _));

    private static DPlusRecoveryView BuildView(Guid organizationId, string streamName, Guid recoveryId, Dictionary<string, byte[]> files, DPlusRecoveryApprovalVerifier verifier,
        List<DPlusRecoveryRecordProblem> problems)
    {
        var prefix = DPlusRecoveryNames.StreamRoot(organizationId, streamName) + recoveryId.ToString("D") + "/";
        DPlusRecoveryView Invalid(string reason, DPlusRecoveryStatement? statement = null, string? sha = null)
        {
            problems.Add(new DPlusRecoveryRecordProblem(prefix + "statement.json", false, reason));
            return new DPlusRecoveryView(recoveryId, statement, sha, null, null, [], null, null, null, null, reason);
        }

        if (!files.TryGetValue("statement.json", out var statementBytes))
        {
            return new DPlusRecoveryView(recoveryId, null, null, null, null, [], null, null, null, null, "no statement");
        }

        var statement = DPlusRecoveryCodec.TryParse<DPlusRecoveryStatement>(statementBytes);
        var statementSha = DPlusRecoveryCodec.Sha256Hex(statementBytes);
        if (statement is null)
        {
            return Invalid("the statement is malformed or not canonical");
        }

        if (ValidateStatement(statement, organizationId, streamName, recoveryId) is { } statementProblem)
        {
            return Invalid(statementProblem, statement, statementSha);
        }

        DPlusStreamInvestigation? investigation = null;
        if (!files.TryGetValue("investigation.json", out var investigationBytes) || !string.Equals(DPlusRecoveryCodec.Sha256Hex(investigationBytes), statement.InvestigationSha256Hex, StringComparison.Ordinal)
            || (investigation = DPlusRecoveryCodec.TryParse<DPlusStreamInvestigation>(investigationBytes)) is null
            || !string.Equals(investigation.InvestigationSchema, DPlusStreamInvestigation.SchemaV1, StringComparison.Ordinal) || investigation.RecoveryId != recoveryId
            || investigation.OrganizationId != organizationId || !string.Equals(investigation.StreamName, streamName, StringComparison.Ordinal) || investigation.FailedRevision != statement.FailedRevision)
        {
            return Invalid("the investigation record is missing or does not match the statement", statement, statementSha);
        }

        DPlusRecoveryEvidence? evidence = null;
        if (!files.TryGetValue("evidence.json", out var evidenceBytes) || !string.Equals(DPlusRecoveryCodec.Sha256Hex(evidenceBytes), statement.EvidenceSha256Hex, StringComparison.Ordinal)
            || (evidence = DPlusRecoveryCodec.TryParse<DPlusRecoveryEvidence>(evidenceBytes)) is null || evidence.OrganizationId != organizationId
            || !string.Equals(evidence.StreamName, streamName, StringComparison.Ordinal) || evidence.FailedRevision != statement.FailedRevision || evidence.StopGeneration != statement.StopGeneration)
        {
            return Invalid("the evidence snapshot is missing or does not match the statement", statement, statementSha);
        }

        var binding = new DPlusRecoveryBinding(recoveryId, organizationId, streamName, statement.FailedRevision, statement.StopGeneration, statementSha);
        var decisions = new List<DPlusVerifiedDecision>();
        foreach (var (tail, bytes) in files.Where(f => f.Key.StartsWith("decisions/", StringComparison.Ordinal)).OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            var token = Encoding.ASCII.GetString(bytes);
            var verified = verifier.Verify(token, binding, out var reason);
            if (verified is null || !string.Equals(tail, "decisions/" + verified.Jti.ToString("D") + ".jws", StringComparison.Ordinal))
            {
                problems.Add(new DPlusRecoveryRecordProblem(prefix + tail, false, verified is null ? "decision not verified: " + reason : "decision stored under another identifier"));
                continue;
            }

            decisions.Add(verified);
        }

        var authorization = DPlusRecoveryAuthorization.Evaluate(decisions, statement.RequesterLabel, investigation.AnalystLabel);
        var action = files.TryGetValue("action.json", out var actionBytes) ? DPlusRecoveryCodec.TryParse<DPlusRecoveryAction>(actionBytes) : null;
        var outcome = files.TryGetValue("outcome.json", out var outcomeBytes) ? DPlusRecoveryCodec.TryParse<DPlusRecoveryOutcome>(outcomeBytes) : null;
        return new DPlusRecoveryView(recoveryId, statement, statementSha, investigation, evidence, decisions, authorization, null, action, outcome, null);
    }

    /// <summary>Structural validation of a statement (A-4 §24.3). Deferred strategies are refused: they never authorize anything.</summary>
    public static string? ValidateStatement(DPlusRecoveryStatement statement, Guid organizationId, string streamName, Guid recoveryId)
    {
        ArgumentNullException.ThrowIfNull(statement);
        if (!string.Equals(statement.StatementSchema, DPlusRecoveryStatement.SchemaV1, StringComparison.Ordinal) || statement.RecoveryId != recoveryId
            || statement.OrganizationId != organizationId || !string.Equals(statement.StreamName, streamName, StringComparison.Ordinal))
        {
            return "the statement is for another scope or schema";
        }

        if (!AuditDeliveryEnforcement.IsEnforced(streamName))
        {
            return "recovery is defined for the D+ audit stream only (SR-PB-2)";
        }

        if (!DPlusRecoveryStrategies.IsOperational(statement.Strategy))
        {
            return "strategy " + statement.Strategy + " is not operational in this increment";
        }

        if (statement.FailedRevision < 1 || statement.StopGeneration < 0 || statement.StopSet.Count == 0)
        {
            return "the statement has no failed revision, generation or stop set";
        }

        var sorted = DPlusRecoveryCodec.SortStopSet(statement.StopSet);
        if (!sorted.SequenceEqual(statement.StopSet) || sorted.Select(s => s.ObjectName).Distinct(StringComparer.Ordinal).Count() != sorted.Count
            || !string.Equals(DPlusRecoveryCodec.StopSetSha256Hex(sorted), statement.StopSetSha256Hex, StringComparison.Ordinal)
            || sorted.Any(s => !DPlusRecoveryCodec.IsSha256Hex(s.Sha256Hex) || s.RecordKind is not (DPlusStopRecordReference.Marker or DPlusStopRecordReference.InvalidMarkerObject or DPlusStopRecordReference.Finding)))
        {
            return "the stop set is not canonical or its digest does not match";
        }

        if (sorted.Any(s => s.RecordKind != DPlusStopRecordReference.InvalidMarkerObject && (s.FailedRevision != statement.FailedRevision || s.StopGeneration != statement.StopGeneration)))
        {
            return "the stop set names another revision or generation";
        }

        if (statement.NotBeforeUtc.Offset != TimeSpan.Zero || statement.NotAfterUtc.Offset != TimeSpan.Zero || statement.NotAfterUtc <= statement.NotBeforeUtc
            || statement.NotAfterUtc - statement.NotBeforeUtc > DPlusRecoveryStatement.MaximumWindow)
        {
            return "the execution window is not UTC, empty or longer than 24 hours";
        }

        if (string.IsNullOrWhiteSpace(statement.RequesterLabel) || !DPlusRecoveryCodec.IsSha256Hex(statement.EvidenceSha256Hex) || !DPlusRecoveryCodec.IsSha256Hex(statement.InvestigationSha256Hex)
            || !DPlusRecoveryCodec.IsSha256Hex(statement.RequestReasonSha256Hex) || !DPlusRecoveryCodec.IsSha256Hex(statement.Expected.RowBodySha256Hex))
        {
            return "the statement lacks a requester or a digest";
        }

        return null;
    }
}
