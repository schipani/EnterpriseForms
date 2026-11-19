using System.Collections.Concurrent;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Journal.Verification;
using AuthorizationAuthority.Domain.Features.Journal;

namespace AuthorizationAuthority.Application.Features.Journal.Reconciliation;

/// <summary>Development/Testing measurement defaults approved by OD-DV-6 (C-155); production targets are not set.</summary>
public sealed record JournalVerifierOptions
{
    public TimeSpan IncrementalInterval { get; init; } = TimeSpan.FromMinutes(5);

    public TimeSpan FullInterval { get; init; } = TimeSpan.FromHours(24);

    public TimeSpan DeliveryLagThreshold { get; init; } = TimeSpan.FromMinutes(15);

    public TimeSpan VerificationStaleness { get; init; } = TimeSpan.FromMinutes(15);

    public string VerifierIdentityLabel { get; init; } = "journal-verifier-dev";

    public JournalVerificationLimits Limits { get; init; } = new();

    /// <summary>
    /// WP-4A (A-1): an incremental sweep lists the archive only above the trusted revision (bounded prefixes). Changes below it are
    /// detected only by the next full sweep (<see cref="FullInterval"/>); the anchor at the trusted revision is still read every sweep.
    /// </summary>
    public bool BoundedIncrementalListing { get; init; } = true;
}

public enum JournalSweepKind
{
    Incremental,
    Full,
}

public enum JournalSweepOutcome
{
    /// <summary>No finding and nothing inconclusive; a checkpoint was created (VF-11).</summary>
    Clean,

    /// <summary>At least one finding; no checkpoint advance.</summary>
    Findings,

    /// <summary>Not decidable (unavailable, not baselined, unsupported, ...); never verified, no checkpoint advance.</summary>
    Inconclusive,

    /// <summary>Not run: another sweep holds this (organization, stream).</summary>
    Skipped,
}

/// <summary>
/// One sweep's result: findings with their run outcome and checkpoint reference, metrics, the checkpoint action. WP-4A (A-1):
/// <see cref="BoundedListing"/> marks an incremental sweep that listed only above the trusted revision (never a full-history verification);
/// <see cref="LastCompleteVerificationAtUtc"/> is the last Clean full sweep or baseline known to this process (null: none known);
/// <see cref="AcceptedObservations"/> are findings an unchanged accepted exception of the chain covers (reported, not findings).
/// </summary>
public sealed record JournalVerificationRun(Guid RunId, Guid OrganizationId, string StreamName, JournalSweepKind Kind, JournalSweepOutcome Outcome, IReadOnlyList<string> InconclusiveReasons,
    IReadOnlyList<JournalVerificationFinding> Findings, JournalCheckpointReference? StartCheckpoint, JournalCheckpointReference? NewCheckpoint, string CheckpointAction,
    JournalSweepMetrics Metrics, DateTimeOffset StartedAtUtc, DateTimeOffset CompletedAtUtc, bool FindingStoreUnavailable, bool BoundedListing = false,
    DateTimeOffset? LastCompleteVerificationAtUtc = null, IReadOnlyList<JournalAcceptedObservation>? AcceptedObservations = null);

/// <summary>
/// SEC-BL-01 WP-4 (C-155, contract §8, §9): one incremental or full sweep of one (organization, stream). Read-only on AuthorityDb
/// (<see cref="IJournalVerificationReader"/>), reads the archive (<see cref="ISecurityEventArchiveInventory"/>) and the checkpoint store,
/// writes only create-only checkpoints after a Clean sweep and create-only finding records. <b>Alert-only</b> (OD-DV-3): it never
/// quarantines, repairs, deletes, re-signs, re-delivers or replays anything (VF-14).
/// <para>
/// Checkpoint freshness: the highest checkpoint sequence seen is remembered per process (CKR-3); a lower head in a later sweep of the
/// same process is <c>CHECKPOINT_STORE_REGRESSION</c>. Across restarts a rollback of the checkpoint store is undetectable without an
/// independently anchored head (production: Confidential Ledger, not implemented).
/// </para>
/// </summary>
public sealed class JournalVerifier(IJournalVerificationReader reader, ISecurityEventArchiveInventory archive, IVerificationCheckpointStore checkpoints,
    IJournalVerificationFindingStore findingStore, IJournalAcceptedExceptionStore exceptionStore, ISecurityAlertSink alerts, TimeProvider clock, JournalVerifierOptions options,
    IJournalVerificationRecordStore? commitmentStore = null)
{
    private readonly ConcurrentDictionary<(Guid, string), long> knownHeads = new();
    private readonly ConcurrentDictionary<(Guid, string), DateTimeOffset> lastComplete = new();
    private readonly ConcurrentDictionary<(Guid, string), byte> running = new();
    private readonly ConcurrentDictionary<string, byte> alerted = new(StringComparer.Ordinal);

    public JournalVerifierOptions Options => options;

    public async Task<JournalVerificationRun> SweepAsync(Guid organizationId, string streamName, JournalSweepKind kind, CancellationToken cancellationToken)
    {
        if (!ArchiveCompleteness.KnownStreams.Contains(streamName, StringComparer.Ordinal))
        {
            throw new ArgumentException("unknown stream", nameof(streamName));
        }

        var started = clock.GetUtcNow();
        var runId = Guid.CreateVersion7(started);
        var key = (organizationId, streamName);
        if (!running.TryAdd(key, 0))
        {
            return Skipped(runId, organizationId, streamName, kind, started, JournalInconclusiveReasons.SweepLockHeld);
        }

        try
        {
            await using var snapshot = await reader.OpenSnapshotAsync(cancellationToken).ConfigureAwait(false);
            if (!await snapshot.TryAcquireSweepLockAsync(organizationId, streamName, cancellationToken).ConfigureAwait(false))
            {
                return Skipped(runId, organizationId, streamName, kind, started, JournalInconclusiveReasons.SweepLockHeld);
            }

            var work = new SweepState();
            await SweepCoreAsync(snapshot, organizationId, streamName, kind, started, work, cancellationToken).ConfigureAwait(false);
            return await FinishAsync(runId, organizationId, streamName, kind, started, work, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            running.TryRemove(key, out _);
        }
    }

    private async Task SweepCoreAsync(IJournalVerificationSnapshot snapshot, Guid org, string stream, JournalSweepKind kind, DateTimeOffset now, SweepState work, CancellationToken cancellationToken)
    {
        var mode = kind == JournalSweepKind.Full ? CheckpointDiscoveryMode.FullChain : CheckpointDiscoveryMode.HeadAndPredecessor;
        var minimum = knownHeads.TryGetValue((org, stream), out var known) ? known : 0;
        var discovery = await JournalVerificationCheckpoints.DiscoverAsync(checkpoints, org, stream, mode, minimum, options.Limits.ListingPageSize, cancellationToken).ConfigureAwait(false);
        switch (discovery.Outcome)
        {
            case CheckpointDiscoveryOutcome.Empty:
                work.Inconclusive.Add(JournalInconclusiveReasons.NotBaselined);
                return;
            case CheckpointDiscoveryOutcome.Inconclusive:
                if (discovery.FindingCode == CheckpointFindingCodes.StoreUnavailable)
                {
                    work.Add(JournalFindingCodes.CheckpointStoreUnavailable, null, null, null, [], "checkpoint store unavailable");
                    work.Inconclusive.Add(JournalInconclusiveReasons.CheckpointStoreUnavailable);
                }
                else
                {
                    work.Inconclusive.Add(discovery.FindingCode == CheckpointFindingCodes.SchemaUnsupported ? JournalInconclusiveReasons.CheckpointSchemaUnsupported : JournalInconclusiveReasons.CheckpointStoreUnavailable);
                }

                return;
            case CheckpointDiscoveryOutcome.Invalid:
                work.Add(JournalFindingCodes.CheckpointChainInvalid, discovery.FindingCode, null, null, [], discovery.Detail ?? "checkpoint chain invalid");
                return;
            case CheckpointDiscoveryOutcome.Regression:
                work.Add(JournalFindingCodes.CheckpointStoreRegression, null, null, null, ["checkpoint:" + minimum.ToString(System.Globalization.CultureInfo.InvariantCulture)],
                    "the checkpoint store's newest checkpoint is below the head this process already verified (rollback of the checkpoint store)");
                return;
        }

        var head = discovery.Head!;
        work.Discovery = discovery;
        work.Start = new JournalCheckpointReference(head.CheckpointSequence, head.VerifiedRevision, Convert.ToHexStringLower(discovery.HeadDigest!));
        knownHeads.AddOrUpdate((org, stream), head.CheckpointSequence, (_, existing) => Math.Max(existing, head.CheckpointSequence));
        if (head.WalkKind is CheckpointWalkKind.Baseline or CheckpointWalkKind.Full)
        {
            RecordComplete(org, stream, head.VerifiedAtUtc);
        }

        // AS-2/AS-3: the archive instance must be the one the checkpoint chain verified.
        try
        {
            var identity = await archive.ReadIdentityAsync(cancellationToken).ConfigureAwait(false);
            if (identity.Status != StoredObjectStatus.Present || identity.Identity is null || !string.Equals(identity.Identity.ArchiveInstanceId, head.ArchiveInstanceId, StringComparison.Ordinal))
            {
                work.Add(JournalFindingCodes.ArchiveRegression, JournalInconclusiveReasons.ArchiveIdentityMismatch, null, null, ["archive:sentinel"],
                    identity.Status == StoredObjectStatus.Present ? "the archive identity differs from the checkpoint's archive instance" : "the archive identity sentinel is absent (" + identity.Status + ")");
            }
        }
        catch (ArchiveUnavailableException)
        {
            work.Add(JournalFindingCodes.ArchiveUnavailable, null, null, null, [], "archive unavailable");
            work.Inconclusive.Add(JournalInconclusiveReasons.ArchiveUnavailable);
            return;
        }

        // A-1: the accepted exceptions the chain names are re-verified every sweep (scope, provenance, evidence digest); a changed or
        // missing one is a finding, never silently accepted.
        if (head.BaselineExceptionIds.Count > 0)
        {
            try
            {
                var (verified, problems) = await JournalAcceptedExceptions.LoadAndVerifyAsync(exceptionStore, snapshot, archive, org, stream, head.BaselineExceptionIds, head.ArchiveInstanceId,
                    head.CheckpointSequence, cancellationToken).ConfigureAwait(false);
                work.Accepted.AddRange(verified);
                work.Findings.AddRange(problems);
            }
            catch (CheckpointStoreUnavailableException)
            {
                work.Add(JournalFindingCodes.CheckpointStoreUnavailable, null, null, null, [], "accepted-exception records unavailable");
                work.Inconclusive.Add(JournalInconclusiveReasons.CheckpointStoreUnavailable);
                return;
            }
            catch (ArchiveUnavailableException)
            {
                work.Add(JournalFindingCodes.ArchiveUnavailable, null, null, null, [], "archive unavailable");
                work.Inconclusive.Add(JournalInconclusiveReasons.ArchiveUnavailable);
                return;
            }
        }

        var acceptedGaps = work.Accepted.Where(a => a.FindingCode == JournalFindingCodes.ArchiveDiscontinuity).Select(a => (a.FirstRevision, a.LastRevision)).ToList();
        var v = head.VerifiedRevision;
        var request = kind == JournalSweepKind.Full
            // WP-7A (A-9 VF-15, OI-DR-7): the full sweep re-reads and byte-compares every archived object from revision 1, including those at or
            // below the trusted checkpoint, against the chain-verified AuthorityDb rows (and the commitments against the archived bytes).
            ? new JournalComparisonRequest(org, stream, 1, v == 0 ? null : v, head.VerifiedDigest, 1, string.Equals(stream, JournalStreamNames.Audit, StringComparison.Ordinal), now, options.DeliveryLagThreshold,
                acceptedGaps)
            : new JournalComparisonRequest(org, stream, Math.Max(1, v), v == 0 ? null : v, head.VerifiedDigest, v + 1, false, now, options.DeliveryLagThreshold,
                acceptedGaps, options.BoundedIncrementalListing);
        var comparison = await JournalStreamComparer.CompareAsync(snapshot, archive, request, options.Limits, clock, cancellationToken, commitmentStore).ConfigureAwait(false);
        work.Comparison = comparison;
        foreach (var f in comparison.Findings)
        {
            if (JournalAcceptedExceptions.Covering(work.Accepted, f) is { } accepted)
            {
                work.Observations.Add(new JournalAcceptedObservation(accepted.ExceptionId, f.FindingCode, f.Subtype, f.FirstRevision, f.LastRevision));
            }
            else
            {
                work.Findings.Add(f);
            }
        }

        foreach (var (reason, revision) in comparison.InconclusiveReasons)
        {
            work.Inconclusive.Add(revision is { } r ? reason + "@" + r.ToString(System.Globalization.CultureInfo.InvariantCulture) : reason);
        }
    }

    private async Task<JournalVerificationRun> FinishAsync(Guid runId, Guid org, string stream, JournalSweepKind kind, DateTimeOffset started, SweepState work, CancellationToken cancellationToken)
    {
        // A confirmed security finding is never masked as Inconclusive; the inconclusive reasons are still reported.
        var hasSecurity = work.Findings.Any(f => JournalFindingCodes.SeverityOf(f.FindingCode) == JournalFindingSeverity.Security);
        var outcome = hasSecurity ? JournalSweepOutcome.Findings
            : work.Inconclusive.Count > 0 ? JournalSweepOutcome.Inconclusive
            : work.Findings.Count > 0 ? JournalSweepOutcome.Findings : JournalSweepOutcome.Clean;
        var checkpointAction = "NotCreated:" + outcome.ToString().ToUpperInvariant();
        JournalCheckpointReference? created = null;
        if (outcome == JournalSweepOutcome.Clean)
        {
            var head = work.Discovery!.Head!;
            var comparison = work.Comparison!;
            var verified = comparison.ArchiveContiguousHead;
            var next = head with
            {
                CheckpointSequence = head.CheckpointSequence + 1,
                VerifiedRevision = verified,
                VerifiedDigest = verified == head.VerifiedRevision ? head.VerifiedDigest : comparison.ArchiveHeadDigest,
                PreviousCheckpointDigest = work.Discovery.HeadDigest,
                WalkKind = kind == JournalSweepKind.Full ? CheckpointWalkKind.Full : CheckpointWalkKind.Incremental,
                VerifierIdentityLabel = options.VerifierIdentityLabel,
                VerifiedAtUtc = clock.GetUtcNow().ToUniversalTime(),
            };
            var appended = await JournalVerificationCheckpoints.CreateNextAsync(checkpoints, work.Discovery, next, cancellationToken).ConfigureAwait(false);
            checkpointAction = appended.Outcome.ToString() + (appended.Detail is null ? string.Empty : ":" + appended.Detail);
            switch (appended.Outcome)
            {
                case CheckpointAppendOutcome.Created:
                case CheckpointAppendOutcome.AlreadyStored:
                    created = new JournalCheckpointReference(next.CheckpointSequence, next.VerifiedRevision, Convert.ToHexStringLower(appended.CheckpointDigest!));
                    knownHeads.AddOrUpdate((org, stream), next.CheckpointSequence, (_, existing) => Math.Max(existing, next.CheckpointSequence));
                    if (kind == JournalSweepKind.Full)
                    {
                        RecordComplete(org, stream, next.VerifiedAtUtc);
                    }

                    break;
                case CheckpointAppendOutcome.Conflict:
                    work.Add(JournalFindingCodes.CheckpointChainInvalid, CheckpointFindingCodes.Conflict, null, null, ["checkpoint:" + next.CheckpointSequence.ToString(System.Globalization.CultureInfo.InvariantCulture)],
                        "another checkpoint is already stored at the next sequence");
                    outcome = JournalSweepOutcome.Findings;
                    break;
                default:
                    work.Add(JournalFindingCodes.CheckpointStoreUnavailable, null, null, null, [], "checkpoint could not be created: " + appended.Outcome);
                    work.Inconclusive.Add(JournalInconclusiveReasons.CheckpointStoreUnavailable);
                    outcome = JournalSweepOutcome.Inconclusive;
                    break;
            }
        }

        // VERIFICATION_STALE: the newest checkpoint (the last Clean sweep) is older than the staleness threshold and this sweep did not advance.
        var now = clock.GetUtcNow();
        if (outcome != JournalSweepOutcome.Clean && work.Discovery?.Head is { } stale && now - stale.VerifiedAtUtc > options.VerificationStaleness)
        {
            work.Add(JournalFindingCodes.VerificationStale, null, null, null, [], "no Clean sweep for longer than the staleness threshold");
        }

        var outcomeText = outcome.ToString();
        var checkpointReference = created ?? work.Start;
        var findings = work.Findings.Select(f => new JournalVerificationFinding(org, stream, f.FindingCode, f.Subtype, JournalFindingCodes.SeverityOf(f.FindingCode), f.FirstRevision, f.LastRevision,
            runId, now, f.EvidenceReferences, f.Detail, outcomeText, checkpointReference)).ToList();
        var storeUnavailable = false;
        foreach (var finding in findings)
        {
            try
            {
                await findingStore.RecordAsync(org, stream, finding.Identity, finding.ToCanonicalBytes(), cancellationToken).ConfigureAwait(false);
            }
            catch (CheckpointStoreUnavailableException)
            {
                storeUnavailable = true; // the alert below still fires; the run reports the lost record
            }

            if (finding.Severity == JournalFindingSeverity.Security && alerted.TryAdd(finding.Identity, 0))
            {
                alerts.Raise(new SecurityAlert("JournalVerificationFinding", "Critical", org, runId, null,
                    $"{finding.FindingCode}{(finding.Subtype is null ? string.Empty : "/" + finding.Subtype)} stream={stream} revisions={finding.FirstRevision}..{finding.LastRevision} run={runId:D} (alert-only)", runId));
            }
        }

        var c = work.Comparison;
        var metrics = new JournalSweepMetrics(c?.DatabaseRowsInspected ?? 0, c?.ArchiveObjectsListed ?? 0, c?.ArchiveObjectsRead ?? 0, c?.RowBatches ?? 0, (long)(now - started).TotalMilliseconds);
        return new JournalVerificationRun(runId, org, stream, kind, outcome, [.. work.Inconclusive], findings, work.Start, created, checkpointAction, metrics, started, now, storeUnavailable,
            c?.BoundedListing ?? false, lastComplete.TryGetValue((org, stream), out var complete) ? complete : null, [.. work.Observations]);
    }

    private void RecordComplete(Guid org, string stream, DateTimeOffset at) =>
        lastComplete.AddOrUpdate((org, stream), at, (_, existing) => existing > at ? existing : at);

    private static JournalVerificationRun Skipped(Guid runId, Guid org, string stream, JournalSweepKind kind, DateTimeOffset started, string reason) =>
        new(runId, org, stream, kind, JournalSweepOutcome.Skipped, [reason], [], null, null, "NotCreated:SKIPPED", new JournalSweepMetrics(0, 0, 0, 0, 0), started, started, false);

    private sealed class SweepState
    {
        public List<JournalPendingFinding> Findings { get; } = [];

        public List<string> Inconclusive { get; } = [];

        public List<JournalAcceptedExceptionRecord> Accepted { get; } = [];

        public List<JournalAcceptedObservation> Observations { get; } = [];

        public CheckpointDiscoveryResult? Discovery { get; set; }

        public JournalCheckpointReference? Start { get; set; }

        public JournalComparisonResult? Comparison { get; set; }

        public void Add(string code, string? subtype, long? first, long? last, IReadOnlyList<string> evidence, string detail) =>
            Findings.Add(new JournalPendingFinding(code, subtype, first, last, evidence, detail));
    }
}

/// <summary>Pure scheduling decision (deterministic, testable): which sweep, if any, is due for one (organization, stream).</summary>
public static class JournalVerificationSchedule
{
    /// <summary>
    /// Full when no full sweep ran in this process yet or the full interval elapsed (a restart therefore re-verifies history first);
    /// otherwise incremental when the incremental interval elapsed since the last sweep of either kind; otherwise none.
    /// </summary>
    public static JournalSweepKind? Due(DateTimeOffset now, DateTimeOffset? lastSweepStarted, DateTimeOffset? lastFullStarted, JournalVerifierOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (lastFullStarted is null || now - lastFullStarted.Value >= options.FullInterval)
        {
            return JournalSweepKind.Full;
        }

        return lastSweepStarted is null || now - lastSweepStarted.Value >= options.IncrementalInterval ? JournalSweepKind.Incremental : null;
    }
}

/// <summary>
/// Runs the due sweeps for every organization and the three streams, one at a time. Overlap for the same (organization, stream) is
/// prevented twice: in-process by <see cref="JournalVerifier"/> and across processes by the snapshot's sweep lock.
/// </summary>
public sealed class JournalVerificationCoordinator(JournalVerifier verifier, IJournalVerificationReader reader, TimeProvider clock)
{
    private readonly ConcurrentDictionary<(Guid, string), (DateTimeOffset? Sweep, DateTimeOffset? Full)> lastRuns = new();

    public const int OrganizationPageSize = 500;

    public async Task<IReadOnlyList<JournalVerificationRun>> RunDueAsync(CancellationToken cancellationToken)
    {
        var organizations = new List<Guid>();
        await using (var snapshot = await reader.OpenSnapshotAsync(cancellationToken).ConfigureAwait(false))
        {
            Guid? after = null;
            while (true)
            {
                var page = await snapshot.ListOrganizationsAsync(after, OrganizationPageSize, cancellationToken).ConfigureAwait(false);
                organizations.AddRange(page);
                if (page.Count < OrganizationPageSize)
                {
                    break;
                }

                after = page[^1];
            }
        }

        var runs = new List<JournalVerificationRun>();
        foreach (var organization in organizations)
        {
            foreach (var stream in ArchiveCompleteness.KnownStreams)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var now = clock.GetUtcNow();
                var last = lastRuns.GetValueOrDefault((organization, stream));
                if (JournalVerificationSchedule.Due(now, last.Sweep, last.Full, verifier.Options) is not { } kind)
                {
                    continue;
                }

                var run = await verifier.SweepAsync(organization, stream, kind, cancellationToken).ConfigureAwait(false);
                if (run.Outcome != JournalSweepOutcome.Skipped)
                {
                    lastRuns[(organization, stream)] = (now, kind == JournalSweepKind.Full ? now : last.Full);
                }

                runs.Add(run);
            }
        }

        return runs;
    }
}
