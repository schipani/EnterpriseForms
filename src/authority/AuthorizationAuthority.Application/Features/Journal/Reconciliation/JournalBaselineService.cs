using System.Globalization;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Journal.Verification;
using AuthorizationAuthority.Domain.Features.Journal;

namespace AuthorizationAuthority.Application.Features.Journal.Reconciliation;

/// <summary>
/// SEC-BL-01 WP-3 (C-155, contract §10 BL-1..BL-10) and WP-4A (amendment A-1): read-only baseline reconciliation of one (organization,
/// stream). It compares every AuthorityDb row with the archived bytes under R-1..R-7, classifies the result as CLEAN, EXCEPTION_ACCEPTED,
/// FAILED or INCONCLUSIVE and produces a deterministic report. Only when explicitly requested it creates checkpoint 1 of a first baseline, or
/// (A-1, OBS-4 decided) a re-baseline that <b>continues</b> the existing chain at the next sequence.
/// <para>
/// It never writes AuthorityDb, never repairs or re-delivers, never accepts an exception without an explicit owner disposition, never
/// restarts or overwrites a chain, never moves the verified revision backward and never upgrades assurance: accepted exceptions of the chain
/// are carried and re-verified. Its only possible writes are the create-only sentinel (when requested), create-only accepted-exception
/// records (A-1) and the create-only checkpoint.
/// </para>
/// </summary>
public sealed class JournalBaselineService(IJournalVerificationReader reader, ISecurityEventArchiveInventory archive, IVerificationCheckpointStore checkpoints,
    IJournalAcceptedExceptionStore exceptionStore, TimeProvider clock, JournalVerificationLimits? limits = null)
{
    public const string ReportSchema = "DynamicForms/JournalBaselineReport/v2";

    /// <summary>BL-9: mandatory in every report.</summary>
    public const string HistoricalStatement =
        "This baseline proves only that AuthorityDb equalled the archive at the baseline time for the revisions compared. It does not prove that history before the baseline was genuine.";

    private readonly JournalVerificationLimits bounds = limits ?? new JournalVerificationLimits();

    public async Task<JournalBaselineReport> RunAsync(JournalBaselineRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ArchiveCompleteness.KnownStreams.Contains(request.StreamName, StringComparer.Ordinal))
        {
            throw new ArgumentException("unknown stream", nameof(request));
        }

        var started = clock.GetUtcNow();
        var builder = new ReportBuilder(request, started);
        builder.ValidateDispositions(started);

        // ---- Sentinel (BL-1, AS-1) ----
        string? archiveInstance = null;
        try
        {
            var identity = await archive.ReadIdentityAsync(cancellationToken).ConfigureAwait(false);
            if (identity.Status == StoredObjectStatus.Present)
            {
                if (identity.Identity is null)
                {
                    builder.Finding(JournalFindingCodes.ArchiveRegression, null, null, null, ["archive:sentinel"], "the archive identity sentinel is not a canonical v1 sentinel");
                }

                archiveInstance = identity.Identity?.ArchiveInstanceId;
                builder.SentinelAction = "Existing";
            }
            else if (request.CreateSentinelIfAbsent)
            {
                var created = await archive.CreateIdentityAsync(ArchiveIdentityCodec.NewIdentity(started), cancellationToken).ConfigureAwait(false);
                archiveInstance = created.Existing?.ArchiveInstanceId;
                builder.SentinelAction = created.Status == ArchiveIdentityCreateStatus.Created ? "Created" : "Existing";
            }
            else
            {
                builder.SentinelAction = "AbsentNotCreated";
                builder.Inconclusive(JournalInconclusiveReasons.SentinelAbsent, null);
            }
        }
        catch (ArchiveUnavailableException)
        {
            builder.Inconclusive(JournalInconclusiveReasons.ArchiveUnavailable, null);
        }

        builder.ArchiveInstanceId = archiveInstance;

        // ---- Existing chain (BL-10, A-1) ----
        var discovery = await JournalVerificationCheckpoints.DiscoverAsync(checkpoints, request.OrganizationId, request.StreamName, CheckpointDiscoveryMode.FullChain, 0, bounds.ListingPageSize, cancellationToken)
            .ConfigureAwait(false);
        switch (discovery.Outcome)
        {
            case CheckpointDiscoveryOutcome.Inconclusive:
                builder.Inconclusive(discovery.FindingCode == CheckpointFindingCodes.SchemaUnsupported ? JournalInconclusiveReasons.CheckpointSchemaUnsupported : JournalInconclusiveReasons.CheckpointStoreUnavailable, null);
                break;
            case CheckpointDiscoveryOutcome.Invalid:
            case CheckpointDiscoveryOutcome.Regression:
                builder.Finding(discovery.Outcome == CheckpointDiscoveryOutcome.Regression ? JournalFindingCodes.CheckpointStoreRegression : JournalFindingCodes.CheckpointChainInvalid,
                    discovery.FindingCode, null, null, [], discovery.Detail ?? "checkpoint store invalid");
                break;
        }

        var head = discovery.Outcome == CheckpointDiscoveryOutcome.Head ? discovery.Head : null;
        string checkpointAction = "NotCreated:NOT_EVALUATED";
        JournalCheckpointReference? checkpointReference = null;
        JournalCheckpointReference? previousReference = head is null ? null : new JournalCheckpointReference(head.CheckpointSequence, head.VerifiedRevision, Convert.ToHexStringLower(discovery.HeadDigest!));
        string? rebaselineProposal = null;
        IReadOnlyList<string> recordIds = [];
        var assurance = "None";

        await using (var snapshot = await reader.OpenSnapshotAsync(cancellationToken).ConfigureAwait(false))
        {
            var locked = await snapshot.TryAcquireSweepLockAsync(request.OrganizationId, request.StreamName, cancellationToken).ConfigureAwait(false);
            if (!locked)
            {
                builder.Inconclusive(JournalInconclusiveReasons.SweepLockHeld, null);
            }

            // Existing accepted exceptions of the chain are re-verified (A-1) and carried; they never disappear in a re-baseline.
            IReadOnlyList<JournalAcceptedExceptionRecord> carried = [];
            if (locked && head is not null && head.BaselineExceptionIds.Count > 0 && !builder.IsInconclusive)
            {
                try
                {
                    var (verified, problems) = await JournalAcceptedExceptions.LoadAndVerifyAsync(exceptionStore, snapshot, archive, request.OrganizationId, request.StreamName, head.BaselineExceptionIds,
                        head.ArchiveInstanceId, head.CheckpointSequence, cancellationToken).ConfigureAwait(false);
                    carried = verified;
                    foreach (var problem in problems)
                    {
                        builder.Finding(problem.FindingCode, problem.Subtype, problem.FirstRevision, problem.LastRevision, problem.EvidenceReferences, problem.Detail);
                    }
                }
                catch (CheckpointStoreUnavailableException)
                {
                    builder.Inconclusive(JournalInconclusiveReasons.CheckpointStoreUnavailable, null);
                }
                catch (ArchiveUnavailableException)
                {
                    builder.Inconclusive(JournalInconclusiveReasons.ArchiveUnavailable, null);
                }
            }

            if (locked && !builder.IsInconclusive)
            {
                var acceptedGaps = carried.Concat<object>(request.Dispositions).Select(GapOf).Where(g => g is not null).Select(g => g!.Value).ToList();
                var comparison = await JournalStreamComparer.CompareAsync(snapshot, archive,
                    new JournalComparisonRequest(request.OrganizationId, request.StreamName, 1, null, null, 1, string.Equals(request.StreamName, JournalStreamNames.Audit, StringComparison.Ordinal),
                        started, request.DeliveryLagThreshold, acceptedGaps), bounds, clock, cancellationToken).ConfigureAwait(false);
                builder.Absorb(comparison);
                if (comparison.ArchiveContiguousHead > comparison.DatabaseHead)
                {
                    // Even if the owner accepted BEHIND_ARCHIVE, a checkpoint must not anchor archived revisions AuthorityDb does not hold.
                    builder.CheckpointBlocker = "ARCHIVE_AHEAD_OF_DATABASE";
                }
            }

            // ---- Classification (BL-3..BL-6, A-1) ----
            builder.ApplyCarried(carried);
            builder.ApplyDispositions(request.Dispositions);
            var outcome = builder.Outcome();
            var newExceptions = builder.NewExceptions();
            var exceptionIds = carried.Select(c => c.ExceptionId).Concat(newExceptions.Select(n => n.ExceptionId)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            var reduced = exceptionIds.Count > 0 || head?.AssuranceLevel == CheckpointAssuranceLevel.Reduced;
            assurance = outcome is JournalBaselineOutcome.Clean or JournalBaselineOutcome.ExceptionAccepted ? (reduced ? "Reduced" : "Full") : "None";
            var targetSequence = (head?.CheckpointSequence ?? 0) + 1;

            // ---- Checkpoint (CK-1, BL-3, BL-4, BL-10 as amended by A-1) ----
            if (head is not null && !request.ContinueExistingChain)
            {
                checkpointAction = "NotCreated:REBASELINE_NOT_REQUESTED";
                rebaselineProposal = string.Create(CultureInfo.InvariantCulture,
                    $"A re-baseline continues the existing chain (A-1): sequence {targetSequence}, walk kind Baseline, previous checkpoint digest {previousReference!.CheckpointDigestHex}. Request it explicitly.");
            }
            else if (outcome is not (JournalBaselineOutcome.Clean or JournalBaselineOutcome.ExceptionAccepted))
            {
                checkpointAction = "NotCreated:OUTCOME_" + outcome.ToString().ToUpperInvariant();
            }
            else if (builder.CheckpointBlocker is { } blocker)
            {
                checkpointAction = "NotCreated:" + blocker;
            }
            else if (!request.CreateCheckpoint)
            {
                checkpointAction = "NotCreated:NOT_REQUESTED";
            }
            else if (archiveInstance is null)
            {
                checkpointAction = "NotCreated:ARCHIVE_IDENTITY_UNKNOWN";
            }
            else if (head is not null && !string.Equals(head.ArchiveInstanceId, archiveInstance, StringComparison.Ordinal))
            {
                checkpointAction = "NotCreated:REBASELINE_ARCHIVE_INSTANCE_CHANGED";
            }
            else if (head is not null && builder.ArchiveContiguousHead < head.VerifiedRevision)
            {
                checkpointAction = "NotCreated:REBASELINE_REVISION_REGRESSION";
            }
            else
            {
                // Records first: a checkpoint never names an exception whose record is not stored.
                var recordConflict = false;
                var written = new List<string>();
                foreach (var exception in newExceptions)
                {
                    var digest = await JournalAcceptedExceptions.ComputeEvidenceDigestAsync(snapshot, archive, request.OrganizationId, request.StreamName, exception.FirstRevision, exception.LastRevision,
                        cancellationToken).ConfigureAwait(false);
                    var record = new JournalAcceptedExceptionRecord(JournalAcceptedExceptionRecord.SchemaV1, exception.ExceptionId, exception.ExceptionClass, request.OrganizationId, request.StreamName,
                        exception.FirstRevision, exception.LastRevision, exception.FindingCode, exception.Subtype, digest, exception.OwnerDispositionReference,
                        exception.DispositionRecordedAtUtc.ToUniversalTime(), request.RunId, targetSequence, archiveInstance, request.ToolVersion, request.SourceCommit, request.OperatorLabel,
                        started.ToUniversalTime(), JournalAcceptedExceptionRecord.ReducedAssurance);
                    var stored = await exceptionStore.CreateAsync(request.OrganizationId, request.StreamName, exception.ExceptionId, JournalAcceptedExceptions.Serialize(record), cancellationToken)
                        .ConfigureAwait(false);
                    if (stored.Status == CheckpointCreateStatus.Conflict)
                    {
                        recordConflict = true;
                        break;
                    }

                    written.Add(exception.ExceptionId);
                }

                recordIds = written;
                if (recordConflict)
                {
                    checkpointAction = "NotCreated:EXCEPTION_RECORD_CONFLICT";
                }
                else
                {
                    var candidate = new JournalVerificationCheckpoint(request.OrganizationId, request.StreamName, targetSequence, builder.ArchiveContiguousHead,
                        builder.ArchiveContiguousHead == 0 ? null : builder.ArchiveHeadDigest, head is null ? null : discovery.HeadDigest, archiveInstance, CheckpointWalkKind.Baseline,
                        reduced ? CheckpointAssuranceLevel.Reduced : CheckpointAssuranceLevel.Full, exceptionIds, request.VerifierIdentityLabel, started.ToUniversalTime());
                    var appended = await JournalVerificationCheckpoints.CreateNextAsync(checkpoints, discovery, candidate, cancellationToken).ConfigureAwait(false);
                    checkpointAction = appended.Outcome switch
                    {
                        CheckpointAppendOutcome.Conflict => head is null ? "Conflict" : "NotCreated:REBASELINE_CONFLICT",
                        _ => appended.Outcome.ToString() + (appended.Detail is null ? string.Empty : ":" + appended.Detail),
                    };
                    if (appended.CheckpointDigest is { } digest)
                    {
                        checkpointReference = new JournalCheckpointReference(targetSequence, candidate.VerifiedRevision, Convert.ToHexStringLower(digest));
                    }
                }
            }

            return builder.Build(outcome, assurance, checkpointAction, checkpointReference, previousReference, head is not null && request.ContinueExistingChain, rebaselineProposal,
                [.. carried.Select(c => c.ExceptionId).Order(StringComparer.Ordinal)], recordIds, clock.GetUtcNow());
        }
    }

    /// <summary>Deterministic canonical bytes of a report (same inputs and clock ⇒ same bytes).</summary>
    public static byte[] Serialize(JournalBaselineReport report) => CanonicalJson.SerializeBytes(report);

    private static (long First, long Last)? GapOf(object source) => source switch
    {
        JournalAcceptedExceptionRecord r when r.FindingCode == JournalFindingCodes.ArchiveDiscontinuity => (r.FirstRevision, r.LastRevision),
        JournalBaselineExceptionDisposition d when d.FindingCode == JournalFindingCodes.ArchiveDiscontinuity => (d.FirstRevision, d.LastRevision),
        _ => null,
    };

    private sealed class ReportBuilder(JournalBaselineRequest request, DateTimeOffset started)
    {
        private readonly List<JournalBaselineFindingEntry> findings = [];
        private readonly List<string> inconclusive = [];
        private readonly List<JournalAppliedExceptionRecord> applied = [];
        private readonly List<JournalBaselineExceptionDisposition> newExceptions = [];
        private readonly List<string> unused = [];
        private JournalSweepMetrics metrics = new(0, 0, 0, 0, 0);

        public string SentinelAction { get; set; } = "NotRead";

        public string? ArchiveInstanceId { get; set; }

        public string? CheckpointBlocker { get; set; }

        public long DatabaseHead { get; private set; }

        public long ArchiveContiguousHead { get; private set; }

        public long ArchiveHighest { get; private set; }

        public byte[]? ArchiveHeadDigest { get; private set; }

        public bool IsInconclusive => inconclusive.Count > 0;

        public void ValidateDispositions(DateTimeOffset now)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var d in request.Dispositions)
            {
                if (!JournalAcceptedExceptions.IsValidIdentifier(d.ExceptionId) || !ids.Add(d.ExceptionId) || string.IsNullOrEmpty(d.OwnerDispositionReference)
                    || d.DispositionRecordedAtUtc == default || d.DispositionRecordedAtUtc > now || d.FirstRevision < 1 || d.LastRevision < d.FirstRevision
                    || d.LastRevision - d.FirstRevision + 1 > JournalAcceptedExceptions.MaximumScopeRevisions)
                {
                    Inconclusive(JournalInconclusiveReasons.DispositionInvalid, null);
                    return;
                }
            }
        }

        public void Finding(string code, string? subtype, long? first, long? last, IReadOnlyList<string> evidence, string detail) =>
            findings.Add(new JournalBaselineFindingEntry(code, subtype, JournalFindingCodes.SeverityOf(code).ToString(), first, last, [.. evidence.Order(StringComparer.Ordinal)], detail, null, null));

        public void Inconclusive(string reason, long? revision) =>
            inconclusive.Add(revision is { } r ? reason + "@" + r.ToString(CultureInfo.InvariantCulture) : reason);

        public void Absorb(JournalComparisonResult comparison)
        {
            foreach (var f in comparison.Findings)
            {
                Finding(f.FindingCode, f.Subtype, f.FirstRevision, f.LastRevision, f.EvidenceReferences, f.Detail);
            }

            foreach (var (reason, revision) in comparison.InconclusiveReasons)
            {
                Inconclusive(reason, revision);
            }

            DatabaseHead = comparison.DatabaseHead;
            ArchiveContiguousHead = comparison.ArchiveContiguousHead;
            ArchiveHighest = comparison.ArchiveHighest;
            ArchiveHeadDigest = comparison.ArchiveHeadDigest;
            metrics = new JournalSweepMetrics(comparison.DatabaseRowsInspected, comparison.ArchiveObjectsListed, comparison.ArchiveObjectsRead, comparison.RowBatches, comparison.ElapsedMilliseconds);

            // Candidate classification only (never acceptance): a stored body that no longer matches its digest and was never archived
            // is what the jsonb-era rows look like (E-12).
            for (var i = 0; i < findings.Count; i++)
            {
                var f = findings[i];
                if (f.FindingCode == JournalFindingCodes.ChainIntegrityFailure && f.Subtype == JournalFailureCodes.BodyDigestMismatch && f.FirstRevision > ArchiveContiguousHead)
                {
                    findings[i] = f with { CandidateExceptionClass = "LEGACY_UNVERIFIABLE" };
                }
            }
        }

        /// <summary>A-1: findings covered by a re-verified accepted exception of the chain stay accepted under that exception's id.</summary>
        public void ApplyCarried(IReadOnlyList<JournalAcceptedExceptionRecord> carried)
        {
            for (var i = 0; i < findings.Count; i++)
            {
                var f = findings[i];
                if (f.AcceptedByExceptionId is null && JournalAcceptedExceptions.Covering(carried, new JournalPendingFinding(f.FindingCode, f.Subtype, f.FirstRevision, f.LastRevision, f.EvidenceReferences, f.Detail))
                        is { } record)
                {
                    findings[i] = f with { AcceptedByExceptionId = record.ExceptionId };
                }
            }
        }

        /// <summary>BL-4/BL-5: a finding is accepted only by an explicit owner disposition whose scope covers it exactly (security and operational findings alike).</summary>
        public void ApplyDispositions(IReadOnlyList<JournalBaselineExceptionDisposition> dispositions)
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < findings.Count; i++)
            {
                var f = findings[i];
                if (f.AcceptedByExceptionId is not null || f.FirstRevision is null || f.LastRevision is null)
                {
                    continue;
                }

                var match = dispositions.FirstOrDefault(d => d.OrganizationId == request.OrganizationId && string.Equals(d.StreamName, request.StreamName, StringComparison.Ordinal)
                    && string.Equals(d.FindingCode, f.FindingCode, StringComparison.Ordinal) && string.Equals(d.Subtype, f.Subtype, StringComparison.Ordinal)
                    && d.FirstRevision <= f.FirstRevision && f.LastRevision <= d.LastRevision);
                if (match is null)
                {
                    continue;
                }

                findings[i] = f with { AcceptedByExceptionId = match.ExceptionId };
                if (used.Add(match.ExceptionId))
                {
                    newExceptions.Add(match);
                    applied.Add(new JournalAppliedExceptionRecord(match.ExceptionId, match.ExceptionClass, match.FindingCode, match.Subtype, match.FirstRevision, match.LastRevision,
                        match.OwnerDispositionReference, request.ToolVersion, request.SourceCommit, request.OperatorLabel, started.ToUniversalTime(), ArchiveInstanceId,
                        "Reduced assurance: the accepted scope is not verified; it stays recorded in every later checkpoint of this chain."));
                }
            }

            unused.AddRange(dispositions.Where(d => !used.Contains(d.ExceptionId)).Select(d => d.ExceptionId).Order(StringComparer.Ordinal));
        }

        public List<JournalBaselineExceptionDisposition> NewExceptions() => newExceptions;

        public JournalBaselineOutcome Outcome()
        {
            // A confirmed, unaccepted security finding fails the baseline even if something else was undecidable (BL-5 before BL-6).
            var security = findings.Where(f => f.Severity == nameof(JournalFindingSeverity.Security)).ToList();
            if (security.Any(f => f.AcceptedByExceptionId is null))
            {
                return JournalBaselineOutcome.Failed;
            }

            if (inconclusive.Count > 0)
            {
                return JournalBaselineOutcome.Inconclusive;
            }

            return security.Count > 0 ? JournalBaselineOutcome.ExceptionAccepted : JournalBaselineOutcome.Clean;
        }

        public JournalBaselineReport Build(JournalBaselineOutcome outcome, string assurance, string checkpointAction, JournalCheckpointReference? checkpoint, JournalCheckpointReference? previous,
            bool rebaseline, string? rebaselineProposal, IReadOnlyList<string> carriedIds, IReadOnlyList<string> recordIds, DateTimeOffset completed) => new(
            ReportSchema, request.RunId, request.OrganizationId, request.StreamName, outcome.ToString(), assurance,
            started.ToUniversalTime(), completed.ToUniversalTime(), request.ToolVersion, request.SourceCommit, request.OperatorLabel, request.VerifierIdentityLabel, ArchiveInstanceId, SentinelAction,
            DatabaseHead, ArchiveContiguousHead, ArchiveHighest, metrics,
            [.. findings.OrderBy(f => f.FindingCode, StringComparer.Ordinal).ThenBy(f => f.Subtype, StringComparer.Ordinal).ThenBy(f => f.FirstRevision).ThenBy(f => f.LastRevision)
                .ThenBy(f => string.Join(',', f.EvidenceReferences), StringComparer.Ordinal)],
            [.. applied.OrderBy(a => a.ExceptionId, StringComparer.Ordinal)], [.. unused], [.. inconclusive.Order(StringComparer.Ordinal)],
            checkpointAction, checkpoint, rebaselineProposal, HistoricalStatement, rebaseline, previous, carriedIds, [.. recordIds.Order(StringComparer.Ordinal)]);
    }
}

public enum JournalBaselineOutcome
{
    Clean,
    ExceptionAccepted,
    Failed,
    Inconclusive,
}

/// <summary>
/// One baseline run. <see cref="Dispositions"/> are explicit owner decisions (BL-4, OI-DR-5); none is inferred. <see cref="CreateSentinelIfAbsent"/>,
/// <see cref="CreateCheckpoint"/> and <see cref="ContinueExistingChain"/> (A-1 re-baseline) are the only write permissions and all default to off.
/// </summary>
public sealed record JournalBaselineRequest(Guid OrganizationId, string StreamName, Guid RunId, string OperatorLabel, string VerifierIdentityLabel, string ToolVersion, string? SourceCommit,
    bool CreateSentinelIfAbsent, bool CreateCheckpoint, IReadOnlyList<JournalBaselineExceptionDisposition> Dispositions, TimeSpan DeliveryLagThreshold, bool ContinueExistingChain = false);

/// <summary>An owner disposition (BL-7, A-1): exception id and class, exact scope (finding code, subtype, revision range), the decision reference and when it was recorded.</summary>
public sealed record JournalBaselineExceptionDisposition(string ExceptionId, string ExceptionClass, Guid OrganizationId, string StreamName, string FindingCode, string? Subtype,
    long FirstRevision, long LastRevision, string OwnerDispositionReference, DateTimeOffset DispositionRecordedAtUtc);

public sealed record JournalBaselineFindingEntry(string FindingCode, string? Subtype, string Severity, long? FirstRevision, long? LastRevision, IReadOnlyList<string> EvidenceReferences,
    string Detail, string? CandidateExceptionClass, string? AcceptedByExceptionId);

/// <summary>BL-7 exception record in the report: scope, provenance, owner disposition and the reduced-assurance statement.</summary>
public sealed record JournalAppliedExceptionRecord(string ExceptionId, string ExceptionClass, string FindingCode, string? Subtype, long FirstRevision, long LastRevision, string OwnerDispositionReference,
    string ToolVersion, string? SourceCommit, string OperatorLabel, DateTimeOffset AppliedAtUtc, string? ArchiveInstanceId, string AssuranceStatement);

/// <summary>BL-8 report v2 (A-1 adds the re-baseline and accepted-exception record fields; canonical bytes via <see cref="JournalBaselineService.Serialize"/>).</summary>
public sealed record JournalBaselineReport(string ReportSchema, Guid RunId, Guid OrganizationId, string StreamName, string Outcome, string AssuranceLevel, DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc, string ToolVersion, string? SourceCommit, string OperatorLabel, string VerifierIdentityLabel, string? ArchiveInstanceId, string SentinelAction,
    long DatabaseHeadRevision, long ArchiveContiguousRevision, long ArchiveHighestRevision, JournalSweepMetrics Metrics, IReadOnlyList<JournalBaselineFindingEntry> Findings,
    IReadOnlyList<JournalAppliedExceptionRecord> AppliedExceptions, IReadOnlyList<string> UnusedDispositions, IReadOnlyList<string> InconclusiveReasons, string CheckpointAction,
    JournalCheckpointReference? Checkpoint, string? RebaselineProposal, string HistoricalStatement, bool Rebaseline, JournalCheckpointReference? PreviousCheckpoint,
    IReadOnlyList<string> CarriedExceptionIds, IReadOnlyList<string> AcceptedExceptionRecordsWritten);
