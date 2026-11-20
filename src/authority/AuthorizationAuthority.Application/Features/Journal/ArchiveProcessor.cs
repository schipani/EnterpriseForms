using System.Collections.Concurrent;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Journal.Reconciliation;
using AuthorizationAuthority.Application.Features.Journal.Recovery;
using AuthorizationAuthority.Domain.Features.Journal;

namespace AuthorizationAuthority.Application.Features.Journal;

/// <summary>
/// Delivers pending journal bodies to the independent archive and records receipts. While an authorization-stream
/// entry is unarchived the organization gate stays closed; a content checkpoint that is unarchived keeps its
/// revision unreadable (SEC-P12). Retries are idempotent (same key, same bytes).
/// <para>
/// SEC-BL-01 WP-5a (D+, phase P1): every pending <c>audit</c> entry passes <see cref="AuditDeliveryEnforcement"/> before it is appended.
/// A refusal raises a Critical alert. A stream-stopping refusal writes a create-only stop marker to the archive and its finding to the
/// journal-verification store (WP-5a.2, A-3); the entry is marked <c>VerificationFailed</c> only when both are confirmed, and either one
/// alone already stops the stream durably (discovered on every pass, before D+, independent of AuthorityDb state). With neither, the stream
/// is held in memory (A-3 §3 residual window). An archive-level refusal leaves the entry Pending and writes no marker (DD-1/DD-11). D+ never
/// quarantines (DP-11), never deletes a marker and never repairs evidence.
/// </para>
/// <para>
/// SEC-BL-01 WP-5b (C-157, amendment A-6 §26): D+ applies to every stream in <see cref="AuditDeliveryEnforcement.EnforcedStreams"/>. Every
/// stream, enforced or not, is fetched in its <b>own</b> bounded batch, so the stopped rows of one stream (which stay Pending, §5.3) never
/// consume the batch of another. A D+ refusal or wait ends only its own stream; an archive outage, a body-digest mismatch or an archive
/// conflict ends the pass for the organization (DP-7, DP-10, unchanged). With all three streams enabled the pre-WP-5a delivery path is removed.
/// </para>
/// <para>
/// G14-R2 (contract amendment A-4, §24): discovery also loads the stream's signed recovery records from both stores; a stop record is resolved
/// only when an executed, two-approver recovery names it exactly. New stops are written at the stream's stop generation. Before evaluation
/// the sentinel's instance must equal the trusted expected instance (pinning), and the gathered evidence is committed durably to both stores
/// (W2-S). While a lift awaits delivery of its revision, nothing above it is evaluated (SR-DL-9). Without <see cref="DPlusDurability"/> the
/// audit stream is never evaluated (fail closed; WP-5b: no D+-enabled stream is evaluated).
/// </para>
/// </summary>
public sealed class ArchiveProcessor(IAuthorityStore store, ISecurityEventArchive archive, TimeProvider clock, ISecurityAlertSink? alerts = null,
    IJournalVerificationFindingStore? findings = null, DPlusDurability? durability = null)
{
    public const int BatchSize = 32;

    /// <summary>Row error code of an audit entry held behind an earlier refused revision (§5.3: later rows stay Pending, not cascaded).</summary>
    public const string StreamStoppedCode = "DPLUS_STREAM_STOPPED";

    /// <summary>Row error code while a confirmed refusal could not be recorded durably (WP-5a.1): the entry stays Pending, the stream is held.</summary>
    public const string FindingStoreUnavailableCode = "DPLUS_FINDING_STORE_UNAVAILABLE";

    /// <summary>WP-5a.2 (A-3): the finding is durable but the archive stop marker is not yet; the stream is stopped (durable stop established).</summary>
    public const string StopMarkerUnavailableCode = "DPLUS_STOP_MARKER_UNAVAILABLE";

    /// <summary>WP-5a.2 (A-3 §3): neither the marker nor the finding could be stored; the stream is held in memory only (durable stop NOT established).</summary>
    public const string DurableStopNotEstablishedCode = "DPLUS_DURABLE_STOP_NOT_ESTABLISHED";

    /// <summary>G14-R2 (A-4 §24): D+ durability (record stores, pin anchors, recovery keys) is not composed; no D+-enabled stream is evaluated.</summary>
    public const string DurabilityNotComposedCode = "DPLUS_DURABILITY_NOT_COMPOSED";

    /// <summary>G14-R2: the W2-S evidence commitment could not be established in both stores; no evaluation, the stream waits.</summary>
    public const string CommitmentUnavailableCode = "DPLUS_EVIDENCE_COMMITMENT_UNAVAILABLE";

    /// <summary>G14-R2: the instance anchors could not be read; no evaluation, the stream waits.</summary>
    public const string PinUnavailableCode = "DPLUS_ARCHIVE_PIN_UNAVAILABLE";

    /// <summary>G14-R2: the verification-store copy of the recovery records could not be listed; inconclusive, the stream waits.</summary>
    public const string RecoveryRecordsUnavailableCode = "DPLUS_RECOVERY_RECORDS_UNAVAILABLE";

    /// <summary>G14-R2 (A-4 §24.6): the row hint the recovery executor writes; it is a scheduling hint only and never trusted.</summary>
    public const string RecoveryAuthorizedCodePrefix = "DPLUS_RECOVERY_AUTHORIZED:";

    private readonly ConcurrentDictionary<string, byte> alerted = new(StringComparer.Ordinal);

    /// <summary>
    /// WP-5a.1/WP-5a.2: confirmed refusals with no durable record yet (neither stop marker nor finding), per (organization, stream). While one
    /// is held, D+ is not re-evaluated and nothing of that stream is delivered, even if the archive evidence changes; each pass retries the
    /// original records. Process-local: this is the residual window accepted with A-3 (§23.3, W1); a restart inside it loses the refusal.
    /// With W2-S (A-4) a restart re-reads the durable evidence commitment instead, so replaced evidence is detected.
    /// </summary>
    private readonly ConcurrentDictionary<(Guid Organization, string Stream), UnresolvedRefusal> unresolved = new();

    private sealed record UnresolvedRefusal(long Revision, AuditDeliveryVerdict Verdict, JournalVerificationFinding Finding, DPlusStreamStop? Marker, int Generation);

    public async Task<int> ProcessOrganizationAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var state = await scope.LockOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            return 0;
        }

        var archived = 0;

        // WP-5b (A-6 §26): every journal stream is D+-enabled, in the pre-WP-5a ordinal order (audit, authorization, content), each in its own
        // batch. There is no delivery path without D+ (§5.7); rows of an unknown stream name are never delivered.
        foreach (var stream in AuditDeliveryEnforcement.EnforcedStreams)
        {
            var pending = await scope.GetPendingJournalEntriesByStreamAsync(organizationId, stream, includeStream: true, BatchSize, cancellationToken).ConfigureAwait(false);
            if (pending.Count == 0)
            {
                continue;
            }

            var (delivered, end) = await DeliverEnforcedStreamAsync(scope, state, organizationId, pending, cancellationToken).ConfigureAwait(false);
            archived += delivered;
            if (end)
            {
                break; // DP-7 outage, DP-10 body mismatch or archive conflict: the pass ends for the organization
            }
        }

        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return archived;
    }

    /// <summary>
    /// One D+ stream in revision order. Returns the number delivered and whether the whole pass ends (the existing responses to a body
    /// mismatch, an archive conflict and archive unavailability). A D+ refusal ends only this stream, so the other streams still run.
    /// WP-5a.2 (A-3 SM-5): after the first entry's existing R-1 check, the durable stops (archive markers and D+ findings) are discovered on
    /// every pass before D+ evaluates anything; AuthorityDb delivery state never lifts them. G14-R2: only an executed recovery does.
    /// </summary>
    private async Task<(int Delivered, bool EndPass)> DeliverEnforcedStreamAsync(IAuthorityScope scope, Domain.Features.Organizations.OrganizationAuthorizationState state, Guid organizationId,
        List<SecurityJournalEntry> pending, CancellationToken cancellationToken)
    {
        var delivered = 0;
        var pass = new AuditDeliveryPass();
        var first = true;
        DPlusRecoveryLedger? ledger = null;
        foreach (var entry in pending.OrderBy(e => e.JournalStreamRevision))
        {
            entry.ArchiveAttemptCount++;
            var body = System.Text.Encoding.UTF8.GetBytes(entry.JournalEventBodyJson);

            // DP-10: the existing R-1 check and its existing response (quarantine) are unchanged.
            if (!CanonicalJson.Digest(body).AsSpan().SequenceEqual(entry.JournalEventDigest))
            {
                entry.ArchiveDeliveryState = ArchiveDeliveryStates.VerificationFailed;
                entry.LastArchiveErrorCode = JournalBodyDigestMismatch;
                OrganizationQuarantine.Set(state, JournalBodyDigestMismatch);
                return (delivered, true);
            }

            if (archive is not ISecurityEventArchiveInventory inventory)
            {
                // D+ needs the sentinel and predecessor reads; an archive without them cannot satisfy D+. Fail closed for this stream only
                // (a configuration fact, not an outage): the entry stays Pending and the other streams are not held.
                entry.LastArchiveErrorCode = "DPLUS_ARCHIVE_INVENTORY_UNAVAILABLE";
                return (delivered, false);
            }

            if (durability is null || archive is not IArchiveMetaRecords meta)
            {
                // A-4 §24: without the record stores and anchors, neither the W2-S commitment nor the instance pin can be established.
                entry.LastArchiveErrorCode = DurabilityNotComposedCode;
                return (delivered, false);
            }

            if (first)
            {
                first = false;
                var (discovery, loaded) = await DiscoverDurableStopAsync(scope, inventory, meta, organizationId, entry, pending, cancellationToken).ConfigureAwait(false);
                switch (discovery)
                {
                    case StopDiscovery.ArchiveUnavailable:
                        return (0, true); // DP-7: Pending, the pass ends; never "no marker"
                    case StopDiscovery.Stopped:
                    case StopDiscovery.Inconclusive:
                        return (0, false); // this stream is held; other streams continue
                }

                ledger = loaded!;
                if (unresolved.TryGetValue((organizationId, entry.JournalStreamName), out var held))
                {
                    await RetryUnresolvedAsync(scope, inventory, meta, organizationId, pending, held, cancellationToken).ConfigureAwait(false);
                    return (0, false); // held in memory: no D+ re-evaluation, no delivery
                }

                // SR-DL-9: while a lift's revision is not archived, nothing above it is evaluated (the hint may be missing or reverted).
                if (ledger.ActiveLift?.Statement is { } lifted && entry.JournalStreamRevision > lifted.FailedRevision)
                {
                    StoredObjectRead read;
                    try
                    {
                        read = await inventory.ReadEntryAsync(organizationId, entry.JournalStreamName, lifted.FailedRevision, cancellationToken).ConfigureAwait(false);
                    }
                    catch (ArchiveUnavailableException ex)
                    {
                        entry.LastArchiveErrorCode = Truncate(ex.Message);
                        return (0, true);
                    }

                    if (read.Status != StoredObjectStatus.Present)
                    {
                        entry.LastArchiveErrorCode = StreamStoppedCode;
                        return (0, false);
                    }
                }

                // A-4 §24 archive-instance pinning (once per pass).
                var pin = await DPlusArchivePins.ResolveAsync(durability, organizationId, entry.JournalStreamName, cancellationToken).ConfigureAwait(false);
                if (pin.Status == DPlusPinStatus.Unavailable)
                {
                    entry.LastArchiveErrorCode = PinUnavailableCode;
                    return (0, false);
                }

                pass.Pin = pin.Status switch
                {
                    DPlusPinStatus.Pinned => AuditArchivePinStatus.Pinned,
                    DPlusPinStatus.Contradictory => AuditArchivePinStatus.Contradictory,
                    _ => AuditArchivePinStatus.Unpinned,
                };
                pass.ExpectedArchiveInstanceId = pin.ExpectedArchiveInstanceId;
            }

            var generation = ledger!.Generation;
            var lift = ledger.ActiveLift?.Statement;
            var verdict = await AuditDeliveryEnforcement.EvaluateAsync(entry, inventory, pass, cancellationToken,
                (evidence, ct) => BeforeEvaluationAsync(evidence, generation, lift, meta, ct)).ConfigureAwait(false);
            if (verdict.Kind == AuditDeliveryVerdictKind.Unavailable)
            {
                entry.LastArchiveErrorCode = Truncate(verdict.Detail ?? "archive unavailable");
                return (delivered, true); // DP-7: Pending, the pass ends (existing ordering behaviour), no finding
            }

            if (verdict.Kind == AuditDeliveryVerdictKind.Wait)
            {
                entry.LastArchiveErrorCode = verdict.FindingCode;
                RaiseOnce("wait/" + entry.OrganizationId.ToString("D") + "/" + verdict.FindingCode, "JournalDeliveryEvidenceCommitmentUnavailable", entry,
                    $"{verdict.FindingCode} stream={entry.JournalStreamName} revision={entry.JournalStreamRevision} (no evaluation; the stream waits)", Guid.CreateVersion7(clock.GetUtcNow()));
                return (delivered, false);
            }

            if (verdict.Kind == AuditDeliveryVerdictKind.Refuse)
            {
                await RefuseAsync(scope, inventory, meta, entry, verdict, pass, generation, cancellationToken).ConfigureAwait(false);
                return (delivered, false); // this stream stops for this pass; other streams continue
            }

            try
            {
                var receipt = await archive.AppendAsync(organizationId, entry.JournalStreamName, entry.JournalStreamRevision, body, entry.JournalEventDigest, cancellationToken).ConfigureAwait(false);
                entry.ArchiveDeliveryState = ArchiveDeliveryStates.Anchored;
                entry.ArchiveReceiptReference = receipt.ReceiptReference;
                entry.ArchiveReceiptDigest = receipt.ReceiptDigest;
                entry.JournalArchivedOn = clock.GetUtcNow();
                entry.LastArchiveErrorCode = null;
                pass.LastDeliveredRevision = entry.JournalStreamRevision;
                pass.LastDeliveredBytes = body; // DP-4: the predecessor of the next revision in this pass
                delivered++;
            }
            catch (ArchiveConflictException)
            {
                entry.ArchiveDeliveryState = ArchiveDeliveryStates.VerificationFailed;
                entry.LastArchiveErrorCode = "ARCHIVE_BODY_MISMATCH";
                OrganizationQuarantine.Set(state, "ARCHIVE_BODY_MISMATCH");
                return (delivered, true);
            }
            catch (ArchiveUnavailableException ex)
            {
                entry.LastArchiveErrorCode = Truncate(ex.Message);
                return (delivered, true); // keep ordering; retry on the next pass
            }
        }

        return (delivered, false);
    }

    /// <summary>
    /// A-4 §24 (W2-S, SR-DL-3/4): the durable commitment to exactly <paramref name="evidence"/> in both stores, before anything is evaluated;
    /// then, for the revision of an executed recovery, the comparison with the statement's expectation. A non-null result preempts evaluation.
    /// </summary>
    private async Task<AuditDeliveryVerdict?> BeforeEvaluationAsync(AuditDeliveryEvidence evidence, int generation, DPlusRecoveryStatement? lift, IArchiveMetaRecords meta,
        CancellationToken cancellationToken)
    {
        var commitment = await DPlusEvidenceCommitments.CommitAsync(evidence, generation, meta, durability!.Records, cancellationToken).ConfigureAwait(false);
        switch (commitment.Outcome)
        {
            case DPlusCommitmentOutcome.Unavailable:
                return AuditDeliveryVerdict.Wait(CommitmentUnavailableCode, commitment.Detail);
            case DPlusCommitmentOutcome.Changed when await AlreadyArchivedAsCommittedAsync(evidence.Entry, commitment.Existing, cancellationToken).ConfigureAwait(false):
                // The revision was delivered with exactly the committed row: the archive object is the stronger commitment. Evaluation and the
                // create-only append proceed, so an altered row still collides there with the existing response (ARCHIVE_BODY_MISMATCH, DP-10).
                break;
            case DPlusCommitmentOutcome.Changed:
                return AuditDeliveryVerdict.Refuse(JournalFindingCodes.DPlusEvidenceChangedAfterCommitment, null, commitment.Detail, marksEntry: true);
            case DPlusCommitmentOutcome.Contradictory:
                return AuditDeliveryVerdict.Refuse(JournalFindingCodes.DPlusEvidenceChangedAfterCommitment, "COMMITMENT_CONTRADICTORY", commitment.Detail, marksEntry: true);
        }

        if (lift is not null && evidence.Entry.JournalStreamRevision == lift.FailedRevision)
        {
            var expected = lift.Expected;
            var candidate = commitment.Candidate;
            if (!string.Equals(expected.ArchiveInstanceId, candidate.ArchiveInstanceId, StringComparison.Ordinal)
                || !string.Equals(expected.RowBodySha256Hex, candidate.RowBodySha256Hex, StringComparison.Ordinal)
                || !string.Equals(expected.PredecessorState, candidate.PredecessorState, StringComparison.Ordinal)
                || !string.Equals(expected.PredecessorSha256Hex, candidate.PredecessorSha256Hex, StringComparison.Ordinal))
            {
                return AuditDeliveryVerdict.Refuse(JournalFindingCodes.DPlusRecoveryEvidenceMismatch, null,
                    "the evidence of the recovered revision differs from the executed recovery's expectation", marksEntry: true);
            }
        }

        return null;
    }

    /// <summary>True when the archive already holds this revision with exactly the row bytes the existing commitment bound (a re-sent delivered row).</summary>
    private async Task<bool> AlreadyArchivedAsCommittedAsync(SecurityJournalEntry entry, DPlusEvidenceCommitment? existing, CancellationToken cancellationToken)
    {
        if (existing is null || archive is not ISecurityEventArchiveInventory inventory)
        {
            return false;
        }

        try
        {
            var read = await inventory.ReadEntryAsync(entry.OrganizationId, entry.JournalStreamName, entry.JournalStreamRevision, cancellationToken).ConfigureAwait(false);
            return read.Status == StoredObjectStatus.Present && string.Equals(DPlusRecoveryCodec.Sha256Hex(read.Bytes!), existing.RowBodySha256Hex, StringComparison.Ordinal);
        }
        catch (ArchiveUnavailableException)
        {
            return false; // fail closed: refused as a change
        }
    }

    private enum StopDiscovery
    {
        Clear,
        Stopped,
        Inconclusive,
        ArchiveUnavailable,
    }

    private sealed record DiscoveredFinding(JournalVerificationFinding Finding, DPlusStopRecordReference Reference);

    private sealed record DiscoveredMarker(DPlusStreamStop Marker, DPlusStopRecordReference Reference);

    /// <summary>
    /// A-3 SM-5/SM-6 with the A-4 lift. Lists the stream's stop markers (archive), D+ refusal findings (journal-verification store) and
    /// recovery records (both stores). A stop record that an executed recovery covers exactly is resolved; any other valid marker, stream-stopping
    /// D+ finding or invalid marker object stops the stream. A missing half of an unresolved stop is completed (create-only), never cleared.
    /// Any listing failing is unavailability or inconclusive (no delivery), never "no stop". When both halves of an unresolved stop are durable,
    /// the failed revision is marked <c>VerificationFailed</c> with its recorded classification.
    /// </summary>
    private async Task<(StopDiscovery Discovery, DPlusRecoveryLedger? Ledger)> DiscoverDurableStopAsync(IAuthorityScope scope, ISecurityEventArchiveInventory inventory, IArchiveMetaRecords meta,
        Guid organizationId, SecurityJournalEntry first, List<SecurityJournalEntry> pending, CancellationToken cancellationToken)
    {
        var stream = first.JournalStreamName;
        IReadOnlyList<ArchiveMetaObject> objects;
        try
        {
            objects = await inventory.ReadStopMarkersAsync(organizationId, stream, cancellationToken).ConfigureAwait(false);
        }
        catch (ArchiveUnavailableException ex)
        {
            first.LastArchiveErrorCode = Truncate(ex.Message);
            return (StopDiscovery.ArchiveUnavailable, null);
        }

        var refusals = new List<DiscoveredFinding>();
        if (findings is not null)
        {
            try
            {
                foreach (var bytes in await findings.ListAsync(organizationId, stream, cancellationToken).ConfigureAwait(false))
                {
                    if (DPlusStreamStops.TryParseStoppingRefusal(bytes) is { } refusal)
                    {
                        refusals.Add(new DiscoveredFinding(refusal, new DPlusStopRecordReference(DPlusStopRecordReference.Finding, FindingObjectName(refusal), DPlusRecoveryCodec.Sha256Hex(bytes),
                            refusal.FirstRevision, refusal.StopGeneration ?? 0)));
                    }
                }
            }
            catch (CheckpointStoreUnavailableException)
            {
                // A recorded stop without its marker cannot be ruled out: inconclusive, the stream waits (no finding is fabricated).
                first.LastArchiveErrorCode = FindingStoreUnavailableCode;
                return (StopDiscovery.Inconclusive, null);
            }
        }

        var ledger = await DPlusRecoveryLedger.LoadAsync(organizationId, stream, meta, durability!.Records, durability.Approvals, cancellationToken).ConfigureAwait(false);
        switch (ledger.Status)
        {
            case DPlusRecoveryLedgerStatus.ArchiveUnavailable:
                first.LastArchiveErrorCode = Truncate(ledger.Detail ?? "archive unavailable");
                return (StopDiscovery.ArchiveUnavailable, null);
            case DPlusRecoveryLedgerStatus.StoreUnavailable:
                first.LastArchiveErrorCode = RecoveryRecordsUnavailableCode;
                return (StopDiscovery.Inconclusive, null);
        }

        foreach (var problem in ledger.Problems)
        {
            await ReportAsync(organizationId, stream, problem.Contradictory ? JournalFindingCodes.RecoveryRecordContradictory : JournalFindingCodes.RecoveryRecordInvalid,
                "RECORD:" + DPlusRecoveryCodec.Sha256Hex(problem.RelativeName)[..16], null, "recovery:" + problem.RelativeName, problem.Reason, cancellationToken).ConfigureAwait(false);
        }

        var markers = new List<DiscoveredMarker>();
        var invalid = false;
        foreach (var item in objects)
        {
            var name = DPlusStreamStops.ParseMarkerObjectName(item.ObjectName, organizationId, stream);
            var reference = new DPlusStopRecordReference(DPlusStopRecordReference.Marker, item.ObjectName, DPlusRecoveryCodec.Sha256Hex(item.Bytes), name?.Revision, name?.Generation);
            if (name is { } parsed && DPlusStreamStops.TryParse(item.Bytes, organizationId, stream, parsed.Revision, parsed.Generation) is { } marker)
            {
                markers.Add(new DiscoveredMarker(marker, reference));
                continue;
            }

            var invalidReference = reference with { RecordKind = DPlusStopRecordReference.InvalidMarkerObject, StopGeneration = null };
            if (ledger.Covers(invalidReference))
            {
                continue; // covered by an executed recovery (it stays in place forever)
            }

            invalid = true;
            await ReportAsync(organizationId, stream, JournalFindingCodes.DPlusStopMarkerInvalid, null, name?.Revision, "archive:" + item.ObjectName,
                "a stop marker is malformed, not canonical, or for another scope; the stream stays stopped", cancellationToken).ConfigureAwait(false);
        }

        // A-4 SR-DL-1: only unresolved stop records stop the stream; a resolved one is never completed, marked or cleared.
        markers = [.. markers.Where(m => !ledger.Covers(m.Reference))];
        refusals = [.. refusals.Where(f => !ledger.Covers(f.Reference))];
        if (!invalid && markers.Count == 0 && refusals.Count == 0 && !ledger.LiftContradictory)
        {
            return (StopDiscovery.Clear, ledger);
        }

        // A marker without its finding: the finding write did not complete (store outage) or the finding was removed (J6). Complete it.
        foreach (var marker in markers.Where(m => findings is not null && !refusals.Any(f => string.Equals(f.Finding.Identity, m.Marker.FindingIdentity, StringComparison.Ordinal))).ToList())
        {
            var restored = FindingFrom(marker.Marker, markerStored: true);
            if (await TryRecordAsync(restored, cancellationToken).ConfigureAwait(false))
            {
                refusals.Add(new DiscoveredFinding(restored, new DPlusStopRecordReference(DPlusStopRecordReference.Finding, FindingObjectName(restored),
                    DPlusRecoveryCodec.Sha256Hex(restored.ToCanonicalBytes()), restored.FirstRevision, restored.StopGeneration ?? 0)));
            }
        }

        // A finding without its marker: if the finding says the marker had been stored, the marker was removed (J4/J5, a security finding);
        // otherwise the marker write did not complete. Either way the marker is created (create-only), never left missing.
        string? instance = null;
        foreach (var refusal in refusals.Where(f => !markers.Any(m => string.Equals(m.Marker.FindingIdentity, f.Finding.Identity, StringComparison.Ordinal))).ToList())
        {
            if (refusal.Finding.EvidenceReferences.Any(r => r.StartsWith(MarkerReferencePrefix, StringComparison.Ordinal)))
            {
                await ReportAsync(organizationId, stream, JournalFindingCodes.DPlusStopRecordIncomplete, refusal.Finding.FindingCode, refusal.Finding.FirstRevision, "finding:" + refusal.Finding.Identity,
                    "the stop marker of a recorded D+ refusal is missing from the archive", cancellationToken).ConfigureAwait(false);
            }

            instance ??= await ReadInstanceAsync(inventory, cancellationToken).ConfigureAwait(false);
            if (instance is not null && MarkerFrom(refusal.Finding, instance) is { } recreated
                && await TryCreateMarkerAsync(inventory, meta, recreated, cancellationToken).ConfigureAwait(false) == MarkerWrite.Stored)
            {
                markers.Add(new DiscoveredMarker(recreated, new DPlusStopRecordReference(DPlusStopRecordReference.Marker,
                    DPlusStreamStops.MarkerObjectName(organizationId, stream, recreated.FailedRevision, recreated.StopGeneration), string.Empty, recreated.FailedRevision, recreated.StopGeneration)));
            }
        }

        // SM-4: a failed revision whose marker and finding are both durable is marked with its recorded classification.
        foreach (var marker in markers.Where(m => refusals.Any(f => string.Equals(f.Finding.Identity, m.Marker.FindingIdentity, StringComparison.Ordinal))).Select(m => m.Marker))
        {
            var row = pending.FirstOrDefault(e => e.JournalStreamRevision == marker.FailedRevision)
                ?? await scope.FindJournalEntryAsync(organizationId, stream, marker.FailedRevision, cancellationToken).ConfigureAwait(false);
            if (row is not null && string.Equals(row.ArchiveDeliveryState, ArchiveDeliveryStates.Pending, StringComparison.Ordinal))
            {
                row.ArchiveDeliveryState = ArchiveDeliveryStates.VerificationFailed;
                row.LastArchiveErrorCode = Truncate("DPLUS_" + marker.FindingCode + (marker.Subtype is null ? string.Empty : ":" + marker.Subtype));
            }
        }

        if (string.Equals(first.ArchiveDeliveryState, ArchiveDeliveryStates.Pending, StringComparison.Ordinal) && !markers.Any(m => m.Marker.FailedRevision == first.JournalStreamRevision))
        {
            first.LastArchiveErrorCode = StreamStoppedCode;
        }

        unresolved.TryRemove((organizationId, stream), out _); // a durable stop supersedes the in-memory hold
        return (StopDiscovery.Stopped, ledger);
    }

    /// <summary>The finding-store object name of a finding (identity-keyed; identical in the Blob and in-memory stores).</summary>
    private static string FindingObjectName(JournalVerificationFinding finding) => "findings/" + finding.OrganizationId.ToString("D") + "/" + finding.StreamName + "/" + finding.Identity + ".json";

    /// <summary>
    /// §5.3 / DP-12 as corrected by WP-5a.1 and A-3. An entry whose immediate predecessor row is already <c>VerificationFailed</c> is held
    /// behind that stop (<see cref="StreamStoppedCode"/>, no new finding). Otherwise: Critical alert; for a stream-stopping refusal the stop
    /// marker (archive), then the finding (journal-verification store); the row is marked only when both are confirmed. One confirmed write
    /// establishes the durable stop (completed by later discovery); none leaves the refusal held in memory only (A-3 §3 residual window).
    /// Archive-level refusals (DD-1/DD-11) write no marker. Never quarantines. G14-R2: records are written at <paramref name="generation"/>.
    /// </summary>
    private async Task RefuseAsync(IAuthorityScope scope, ISecurityEventArchiveInventory inventory, IArchiveMetaRecords meta, SecurityJournalEntry entry, AuditDeliveryVerdict verdict,
        AuditDeliveryPass pass, int generation, CancellationToken cancellationToken)
    {
        if (verdict.MarksEntry && entry.JournalStreamRevision > 1 && string.Equals(verdict.FindingCode, JournalFindingCodes.PredecessorMissing, StringComparison.Ordinal))
        {
            var previous = await scope.FindJournalEntryAsync(entry.OrganizationId, entry.JournalStreamName, entry.JournalStreamRevision - 1, cancellationToken).ConfigureAwait(false);
            if (previous is not null && string.Equals(previous.ArchiveDeliveryState, ArchiveDeliveryStates.VerificationFailed, StringComparison.Ordinal))
            {
                entry.LastArchiveErrorCode = StreamStoppedCode;
                return;
            }
        }

        var now = clock.GetUtcNow().ToUniversalTime();
        var stopping = verdict.MarksEntry && DPlusStreamStops.StopsStream(verdict.FindingCode!, verdict.Subtype);
        DPlusStreamStop? marker = null;
        var markerWrite = MarkerWrite.NotApplicable;
        if (stopping)
        {
            var instance = pass.ArchiveInstanceId ?? await ReadInstanceAsync(inventory, cancellationToken).ConfigureAwait(false);
            var identity = BuildFinding(entry, verdict, now, markerStored: false, generation).Identity;
            if (instance is not null)
            {
                marker = new DPlusStreamStop(DPlusStreamStop.SchemaFor(generation), entry.OrganizationId, entry.JournalStreamName, entry.JournalStreamRevision, verdict.FindingCode!, verdict.Subtype,
                    identity, entry.SecurityJournalEntryId, instance, now, ToolVersion, generation);
                markerWrite = await TryCreateMarkerAsync(inventory, meta, marker, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                markerWrite = MarkerWrite.Unavailable;
            }

            if (markerWrite == MarkerWrite.Conflict)
            {
                await ReportAsync(entry.OrganizationId, entry.JournalStreamName, JournalFindingCodes.DPlusStopMarkerConflict, verdict.FindingCode, entry.JournalStreamRevision,
                    "row:" + entry.SecurityJournalEntryId.ToString("D"), "another stop marker with different content exists at this revision; the stream stays stopped", cancellationToken).ConfigureAwait(false);
            }
        }

        var finding = BuildFinding(entry, verdict, now, markerStored: markerWrite == MarkerWrite.Stored, generation);
        if (alerts is not null && alerted.TryAdd(finding.Identity, 0))
        {
            alerts.Raise(new SecurityAlert("JournalDeliveryRefused", "Critical", entry.OrganizationId, finding.RunId, null,
                $"{verdict.ErrorCode} stream={entry.JournalStreamName} revision={entry.JournalStreamRevision} (D+ refused delivery; stream stopped; no quarantine)", finding.RunId));
        }

        var findingStored = await TryRecordAsync(finding, cancellationToken).ConfigureAwait(false);
        if (!stopping)
        {
            if (findingStored)
            {
                ApplyRecorded(entry, verdict);
            }
            else
            {
                Hold(entry, new UnresolvedRefusal(entry.JournalStreamRevision, verdict, finding, null, generation), durable: false);
            }

            return;
        }

        var markerDurable = markerWrite is MarkerWrite.Stored or MarkerWrite.Conflict;
        if (markerWrite == MarkerWrite.Stored && findingStored)
        {
            ApplyRecorded(entry, verdict); // durable stop established, both records confirmed
            return;
        }

        if (markerDurable || findingStored)
        {
            // Durable stop established by one record; discovery completes the other on a later pass.
            entry.LastArchiveErrorCode = findingStored ? StopMarkerUnavailableCode : FindingStoreUnavailableCode;
            RaiseOnce("store-unavailable/" + finding.Identity, findingStored ? "JournalDeliveryStopMarkerUnavailable" : "JournalDeliveryFindingStoreUnavailable", entry,
                $"{entry.LastArchiveErrorCode} for {verdict.ErrorCode} stream={entry.JournalStreamName} revision={entry.JournalStreamRevision} (durable stop established by one record; stream stopped)", finding.RunId);
            return;
        }

        Hold(entry, new UnresolvedRefusal(entry.JournalStreamRevision, verdict, finding, marker, generation), durable: true);
    }

    /// <summary>A refusal held in memory: retry its original marker and finding; once either is durable, discovery takes over.</summary>
    private async Task RetryUnresolvedAsync(IAuthorityScope scope, ISecurityEventArchiveInventory inventory, IArchiveMetaRecords meta, Guid organizationId, List<SecurityJournalEntry> pending,
        UnresolvedRefusal held, CancellationToken cancellationToken)
    {
        var entry = pending.FirstOrDefault(e => e.JournalStreamRevision == held.Revision)
            ?? await scope.FindJournalEntryAsync(organizationId, held.Finding.StreamName, held.Revision, cancellationToken).ConfigureAwait(false);
        var marker = held.Marker;
        var stopping = held.Verdict.MarksEntry && DPlusStreamStops.StopsStream(held.Verdict.FindingCode!, held.Verdict.Subtype);
        if (stopping && marker is null && await ReadInstanceAsync(inventory, cancellationToken).ConfigureAwait(false) is { } instance)
        {
            marker = new DPlusStreamStop(DPlusStreamStop.SchemaFor(held.Generation), organizationId, held.Finding.StreamName, held.Revision, held.Verdict.FindingCode!, held.Verdict.Subtype,
                held.Finding.Identity, entry?.SecurityJournalEntryId ?? Guid.Empty, instance, held.Finding.ObservedAtUtc, ToolVersion, held.Generation);
        }

        var markerStored = marker is not null && await TryCreateMarkerAsync(inventory, meta, marker, cancellationToken).ConfigureAwait(false) is MarkerWrite.Stored or MarkerWrite.Conflict;
        var finding = markerStored ? held.Finding with { EvidenceReferences = [.. held.Finding.EvidenceReferences, MarkerReference(marker!)] } : held.Finding;
        var findingStored = await TryRecordAsync(finding, cancellationToken).ConfigureAwait(false);
        if (!markerStored && !findingStored)
        {
            if (entry is not null && string.Equals(entry.ArchiveDeliveryState, ArchiveDeliveryStates.Pending, StringComparison.Ordinal))
            {
                Hold(entry, held with { Marker = marker }, durable: stopping);
            }

            return;
        }

        unresolved.TryRemove((organizationId, held.Finding.StreamName), out _);
        if (entry is not null && string.Equals(entry.ArchiveDeliveryState, ArchiveDeliveryStates.Pending, StringComparison.Ordinal))
        {
            if (!stopping || (markerStored && findingStored))
            {
                if (findingStored)
                {
                    ApplyRecorded(entry, held.Verdict);
                }
            }
            else
            {
                entry.LastArchiveErrorCode = findingStored ? StopMarkerUnavailableCode : FindingStoreUnavailableCode;
            }
        }
    }

    private enum MarkerWrite
    {
        NotApplicable,
        Stored,
        Conflict,
        Unavailable,
    }

    /// <summary>Generation 0 through the A-3 inventory path (unchanged key); generation ≥ 1 through the A-4 meta path at the <c>.r{g:D4}</c> key.</summary>
    private static async Task<MarkerWrite> TryCreateMarkerAsync(ISecurityEventArchiveInventory inventory, IArchiveMetaRecords meta, DPlusStreamStop marker, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = DPlusStreamStops.Serialize(marker);
            var result = marker.StopGeneration == 0
                ? await inventory.CreateStopMarkerAsync(marker.OrganizationId, marker.StreamName, marker.FailedRevision, bytes, cancellationToken).ConfigureAwait(false)
                : await meta.CreateMetaAsync(DPlusStreamStops.MarkerObjectName(marker.OrganizationId, marker.StreamName, marker.FailedRevision, marker.StopGeneration), bytes, cancellationToken).ConfigureAwait(false);
            return result.Status == CheckpointCreateStatus.Conflict ? MarkerWrite.Conflict : MarkerWrite.Stored;
        }
        catch (ArchiveUnavailableException)
        {
            return MarkerWrite.Unavailable;
        }
    }

    private static async Task<string?> ReadInstanceAsync(ISecurityEventArchiveInventory inventory, CancellationToken cancellationToken)
    {
        try
        {
            return (await inventory.ReadIdentityAsync(cancellationToken).ConfigureAwait(false)).Identity?.ArchiveInstanceId;
        }
        catch (ArchiveUnavailableException)
        {
            return null;
        }
    }

    private const string MarkerReferencePrefix = "marker:";

    private static string MarkerReference(DPlusStreamStop marker) =>
        MarkerReferencePrefix + DPlusStreamStops.MarkerObjectName(marker.OrganizationId, marker.StreamName, marker.FailedRevision, marker.StopGeneration);

    private JournalVerificationFinding BuildFinding(SecurityJournalEntry entry, AuditDeliveryVerdict verdict, DateTimeOffset now, bool markerStored, int generation)
    {
        List<string> evidence = ["row:" + entry.SecurityJournalEntryId.ToString("D")];
        if (markerStored)
        {
            evidence.Add(MarkerReferencePrefix + DPlusStreamStops.MarkerObjectName(entry.OrganizationId, entry.JournalStreamName, entry.JournalStreamRevision, generation));
        }

        var runId = Guid.CreateVersion7(clock.GetUtcNow());
        return new JournalVerificationFinding(entry.OrganizationId, entry.JournalStreamName, verdict.FindingCode!, verdict.Subtype, JournalFindingCodes.SeverityOf(verdict.FindingCode!),
            entry.JournalStreamRevision, entry.JournalStreamRevision, runId, now, evidence, "D+ refused delivery: " + verdict.Detail, "DeliveryRefused", null, generation == 0 ? null : generation);
    }

    /// <summary>The D+ finding a marker stands for (same deterministic identity), used to complete a missing finding record.</summary>
    private JournalVerificationFinding FindingFrom(DPlusStreamStop marker, bool markerStored)
    {
        List<string> evidence = ["row:" + marker.SecurityJournalEntryId.ToString("D")];
        if (markerStored)
        {
            evidence.Add(MarkerReference(marker));
        }

        var runId = Guid.CreateVersion7(clock.GetUtcNow());
        return new JournalVerificationFinding(marker.OrganizationId, marker.StreamName, marker.FindingCode, marker.Subtype, JournalFindingCodes.SeverityOf(marker.FindingCode),
            marker.FailedRevision, marker.FailedRevision, runId, marker.DetectedAtUtc, evidence, "D+ refused delivery (completed from the stop marker)", "DeliveryRefused", null,
            marker.StopGeneration == 0 ? null : marker.StopGeneration);
    }

    /// <summary>The marker a recorded refusal stands for, used to complete a missing marker; null when the finding lacks its row reference.</summary>
    private static DPlusStreamStop? MarkerFrom(JournalVerificationFinding refusal, string archiveInstanceId)
    {
        var row = refusal.EvidenceReferences.FirstOrDefault(r => r.StartsWith("row:", StringComparison.Ordinal));
        var generation = refusal.StopGeneration ?? 0;
        return row is not null && Guid.TryParseExact(row["row:".Length..], "D", out var entryId)
            ? new DPlusStreamStop(DPlusStreamStop.SchemaFor(generation), refusal.OrganizationId, refusal.StreamName, refusal.FirstRevision!.Value, refusal.FindingCode, refusal.Subtype, refusal.Identity,
                entryId, archiveInstanceId, refusal.ObservedAtUtc.ToUniversalTime(), ToolVersion, generation)
            : null;
    }

    /// <summary>A security finding about the stop records themselves (never a stream-stopping classification); recorded when possible, alerted once.</summary>
    private async Task ReportAsync(Guid organizationId, string stream, string code, string? subtype, long? revision, string evidence, string detail, CancellationToken cancellationToken)
    {
        var runId = Guid.CreateVersion7(clock.GetUtcNow());
        var finding = new JournalVerificationFinding(organizationId, stream, code, subtype, JournalFindingCodes.SeverityOf(code), revision, revision, runId, clock.GetUtcNow().ToUniversalTime(),
            [evidence], detail, "DeliveryStopCheck", null);
        await TryRecordAsync(finding, cancellationToken).ConfigureAwait(false);
        if (alerts is not null && alerted.TryAdd(finding.Identity, 0))
        {
            alerts.Raise(new SecurityAlert("JournalDeliveryStopRecord", "Critical", organizationId, runId, null, $"{code} stream={stream} revision={revision} ({detail})", runId));
        }
    }

    private void RaiseOnce(string key, string kind, SecurityJournalEntry entry, string summary, Guid runId)
    {
        if (alerts is not null && alerted.TryAdd(key, 0))
        {
            alerts.Raise(new SecurityAlert(kind, "Critical", entry.OrganizationId, runId, null, summary, runId));
        }
    }

    private async Task<bool> TryRecordAsync(JournalVerificationFinding finding, CancellationToken cancellationToken)
    {
        if (findings is null)
        {
            return false; // no durable store composed: never a substitute by logging (DP-12, WP-5a.1)
        }

        try
        {
            await findings.RecordAsync(finding.OrganizationId, finding.StreamName, finding.Identity, finding.ToCanonicalBytes(), cancellationToken).ConfigureAwait(false);
            return true; // Created or AlreadyRecorded: the deterministic identity makes retries idempotent
        }
        catch (CheckpointStoreUnavailableException)
        {
            return false;
        }
    }

    private void Hold(SecurityJournalEntry entry, UnresolvedRefusal refusal, bool durable)
    {
        unresolved[(entry.OrganizationId, entry.JournalStreamName)] = refusal;
        entry.LastArchiveErrorCode = durable ? DurableStopNotEstablishedCode : FindingStoreUnavailableCode;
        RaiseOnce("not-established/" + refusal.Finding.Identity, durable ? "JournalDeliveryDurableStopNotEstablished" : "JournalDeliveryFindingStoreUnavailable", entry,
            $"{entry.LastArchiveErrorCode} for {refusal.Verdict.ErrorCode} stream={entry.JournalStreamName} revision={refusal.Revision} (no durable record yet; stream held in memory; no quarantine)",
            refusal.Finding.RunId);
    }

    private static void ApplyRecorded(SecurityJournalEntry entry, AuditDeliveryVerdict verdict)
    {
        entry.LastArchiveErrorCode = Truncate(verdict.ErrorCode);
        if (verdict.MarksEntry)
        {
            entry.ArchiveDeliveryState = ArchiveDeliveryStates.VerificationFailed;
        }
    }

    private static string ToolVersion => typeof(ArchiveProcessor).Assembly.GetName().Version?.ToString() ?? "unknown";

    public const string JournalBodyDigestMismatch = "JOURNAL_BODY_DIGEST_MISMATCH";

    private static string Truncate(string text) => text.Length <= 100 ? text : text[..100];
}
