using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Journal.Recovery;
using AuthorizationAuthority.Domain.Features.Journal;

namespace AuthorizationAuthority.Application.Features.Journal;

/// <summary>WP-7A (C-160, A-9 TR-6′): the receipt validity bounds.</summary>
public sealed record TrustedJournalReceiptOptions
{
    /// <summary>The hard ceiling of <see cref="CurrentStateMaxAge"/> (RV-2): no configuration can trust current state for longer.</summary>
    public static readonly TimeSpan MaximumCurrentStateMaxAge = TimeSpan.FromSeconds(30);

    /// <summary>
    /// RV-2: the longest time a current-state attestation (the external stores read by one establishment) answers protected access.
    /// <see cref="TimeSpan.Zero"/> re-establishes on every access (concurrent accesses that arrive while an establishment is in flight share it,
    /// RV-7). Default 1 s (Development/Testing; owner decision D-7A-1).
    /// </summary>
    public TimeSpan CurrentStateMaxAge { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>TR-7: the most keys held (attestations and first-established evidence); cleared when full.</summary>
    public int CacheLimit { get; init; } = 200_000;

    /// <summary>RV-8: the most withdrawn-evidence keys held; when full every receipt is refused (never cleared).</summary>
    public int WithdrawnLimit { get; init; } = 200_000;

    public void Validate()
    {
        if (CurrentStateMaxAge < TimeSpan.Zero || CurrentStateMaxAge > MaximumCurrentStateMaxAge)
        {
            throw new InvalidOperationException("JournalReceipts:CurrentStateMaxAge must be between 0 and " + MaximumCurrentStateMaxAge.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s");
        }

        if (CacheLimit < 1 || WithdrawnLimit < 1)
        {
            throw new InvalidOperationException("JournalReceipts cache limits must be positive");
        }
    }
}

/// <summary>
/// SEC-BL-01 G08 / E-6 / E-7 (C-158, A-7 §27): the trusted delivery receipt of one journal entry (rules TR-1..TR-8). The AuthorityDb row is
/// only the claim; the proof is outside AuthorityDb:
/// <list type="number">
/// <item>TR-1 the row is <c>Anchored</c> (a cheap pre-filter; never sufficient) and its body hashes to its own digest;</item>
/// <item>TR-2 the archive object at (organization, stream, revision) exists and its SHA-256 equals the row digest;</item>
/// <item>TR-3 the archive's sentinel names the trusted expected instance (provisioning record or the stream's first checkpoint, never the
/// archive itself);</item>
/// <item>TR-4 the entry's W2-S evidence commitment exists in <b>both</b> stores at its highest stop generation, the two copies are identical,
/// and it names exactly this organization, stream, revision, generation, row identity, row digest, previous digest and the pinned instance;</item>
/// <item>TR-5 any store failure is <see cref="JournalReceiptStatus.Unavailable"/> (not delivered), never "absent";</item>
/// <item>TR-7 bounded memory; TR-8 no other input (receipt reference, receipt digest, archived-on time, error code, enforcement mode).</item>
/// </list>
/// <para>
/// WP-7A (C-160, A-9 TR-6′ RV-1..RV-9) replaces TR-6's "cached for the process lifetime while the head is unchanged" with an explicit validity
/// model. <b>Immutable facts</b> (the row's self-digest; that given object bytes hash to the row digest; that given commitment bytes name the
/// row) are pure functions and are recomputed or compared, never trusted from earlier state. <b>Current state</b> (the object is still present and
/// equal, the sentinel still names the pinned instance, the pin itself, both commitment copies at the current highest generation, no unresolved
/// stop, and for a stream head no successor evidence) is read from the stores by one <i>establishment</i> and answers protected access for at
/// most <see cref="TrustedJournalReceiptOptions.CurrentStateMaxAge"/> after that establishment <i>started</i> (RV-2); a failed or unavailable
/// establishment is never reused (RV-3). Evidence that verified a key and later fails or changes (object, instance, generation or commitment
/// bytes) is a contradiction of the create-only stores: the key is refused for the process lifetime (RV-6), and only the verifier's sweeps and an
/// operator can resolve it. A restart starts empty (RV-9): re-establishment is then required before any access.
/// </para>
/// Entries delivered before W2-S existed (no commitment) are not verified: fail closed (A-7 §27.5).
/// </summary>
public sealed class TrustedJournalReceipts : IJournalReceiptVerifier
{
    private readonly ISecurityEventArchive archive;
    private readonly DPlusDurability? durability;
    private readonly TrustedJournalReceiptOptions options;
    private readonly TimeProvider clock;

    /// <summary>RV-2/RV-7: the newest establishment per (key, scope): its start time and its (possibly still running) task.</summary>
    private readonly ConcurrentDictionary<(Key Key, bool Head), Attestation> attestations = new();

    /// <summary>RV-6: the evidence first established for a key; a later establishment must reproduce it exactly.</summary>
    private readonly ConcurrentDictionary<Key, EstablishedEvidence> established = new();

    /// <summary>RV-6/RV-8: keys whose earlier verified evidence was withdrawn or changed.</summary>
    private readonly ConcurrentDictionary<Key, string> withdrawn = new();

    private readonly Lock attestationGate = new();

    private long misses;

    public TrustedJournalReceipts(ISecurityEventArchive archive, DPlusDurability? durability, TrustedJournalReceiptOptions? options = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(archive);
        this.archive = archive;
        this.durability = durability;
        this.options = options ?? new TrustedJournalReceiptOptions();
        this.options.Validate();
        this.clock = clock ?? TimeProvider.System;
    }

    public TrustedJournalReceiptOptions Options => options;

    /// <summary>Establishments that reached the external stores; for measurement and tests.</summary>
    public long StoreVerifications => Interlocked.Read(ref misses);

    /// <summary>Attestations currently held (fresh or stale).</summary>
    public int CachedReceipts => attestations.Count;

    public Task<JournalReceiptVerdict> VerifyAsync(SecurityJournalEntry entry, CancellationToken cancellationToken) => VerifyCoreAsync(entry, head: false, cancellationToken);

    public Task<JournalReceiptVerdict> VerifyStreamHeadAsync(SecurityJournalEntry entry, CancellationToken cancellationToken) => VerifyCoreAsync(entry, head: true, cancellationToken);

    private async Task<JournalReceiptVerdict> VerifyCoreAsync(SecurityJournalEntry entry, bool head, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!string.Equals(entry.ArchiveDeliveryState, ArchiveDeliveryStates.Anchored, StringComparison.Ordinal))
        {
            return JournalReceiptVerdict.Not(JournalReceiptCodes.NotDelivered);
        }

        var body = Encoding.UTF8.GetBytes(entry.JournalEventBodyJson);
        if (entry.JournalEventDigest is not { Length: 32 } || !CanonicalJson.Digest(body).AsSpan().SequenceEqual(entry.JournalEventDigest))
        {
            return JournalReceiptVerdict.Not(JournalReceiptCodes.RowDigest);
        }

        if (durability is null || archive is not ISecurityEventArchiveInventory inventory || archive is not IArchiveMetaRecords meta)
        {
            return JournalReceiptVerdict.Not(JournalReceiptCodes.NotComposed);
        }

        var key = new Key(entry.OrganizationId, entry.JournalStreamName, entry.JournalStreamRevision, Convert.ToHexStringLower(entry.JournalEventDigest),
            entry.SecurityJournalEntryId, DPlusRecoveryCodec.Hex(entry.JournalPreviousDigest));
        if (withdrawn.ContainsKey(key))
        {
            return JournalReceiptVerdict.Not(JournalReceiptCodes.EvidenceWithdrawn);
        }

        if (withdrawn.Count >= options.WithdrawnLimit)
        {
            return JournalReceiptVerdict.Not(JournalReceiptCodes.ContradictionLimit);
        }

        var arrived = clock.GetTimestamp();
        Attestation attestation;
        lock (attestationGate)
        {
            // RV-2/RV-3/RV-7: reuse only an establishment that started within the freshness bound and has not failed; otherwise start one.
            if (!attestations.TryGetValue((key, head), out attestation!) || !Usable(attestation, arrived))
            {
                if (attestations.Count >= options.CacheLimit)
                {
                    attestations.Clear(); // TR-7
                }

                attestation = new Attestation(arrived, EstablishAndRecordAsync(key, head, inventory, meta, durability, CancellationToken.None));
                attestations[(key, head)] = attestation;
            }
        }

        // The establishment is shared, so it never observes one caller's cancellation; the caller's wait does.
        var verdict = await attestation.Verdict.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!verdict.IsVerified)
        {
            attestations.TryRemove(new KeyValuePair<(Key, bool), Attestation>((key, head), attestation)); // RV-3: a refusal or outage is never held
        }

        return withdrawn.ContainsKey(key) ? JournalReceiptVerdict.Not(JournalReceiptCodes.EvidenceWithdrawn) : verdict;
    }

    private bool Usable(Attestation attestation, long arrived)
    {
        if (attestation.Verdict.IsCompleted && (attestation.Verdict.IsFaulted || attestation.Verdict.IsCanceled || !attestation.Verdict.Result.IsVerified))
        {
            return false; // RV-3: never reuse a refusal or an outage
        }

        // Started no earlier than the bound before this access (an in-flight establishment that started after the access arrived also qualifies).
        return attestation.StartedTimestamp >= arrived || clock.GetElapsedTime(attestation.StartedTimestamp, arrived) <= options.CurrentStateMaxAge;
    }

    private async Task<JournalReceiptVerdict> EstablishAndRecordAsync(Key key, bool head, ISecurityEventArchiveInventory inventory, IArchiveMetaRecords meta, DPlusDurability stores,
        CancellationToken cancellationToken)
    {
        await Task.Yield(); // run outside the lock
        Interlocked.Increment(ref misses);
        JournalReceiptVerdict verdict;
        EstablishedEvidence? evidence;
        try
        {
            (verdict, evidence) = await EstablishAsync(key, head, inventory, meta, stores, cancellationToken).ConfigureAwait(false);
        }
        catch (ArchiveUnavailableException)
        {
            return JournalReceiptVerdict.Unavailable(JournalReceiptCodes.ArchiveUnavailable); // TR-5, RV-3: not reused, not withdrawn
        }
        catch (CheckpointStoreUnavailableException)
        {
            return JournalReceiptVerdict.Unavailable(JournalReceiptCodes.StoreUnavailable);
        }

        if (verdict.Status == JournalReceiptStatus.Unavailable)
        {
            return verdict;
        }

        var earlier = established.TryGetValue(key, out var first) ? first : null;
        if (evidence is null)
        {
            // RV-6: the key's own evidence (object, instance, pin, commitment) failed although it verified earlier in this process.
            if (earlier is not null)
            {
                Withdraw(key, verdict.Code);
                return JournalReceiptVerdict.Not(JournalReceiptCodes.EvidenceWithdrawn);
            }

            return verdict;
        }

        if (earlier is not null && earlier != evidence)
        {
            Withdraw(key, "changed"); // another instance, generation or commitment for the same row: the create-only stores were changed
            return JournalReceiptVerdict.Not(JournalReceiptCodes.EvidenceWithdrawn);
        }

        if (earlier is null)
        {
            if (established.Count >= options.CacheLimit)
            {
                established.Clear(); // TR-7 (bounded; a cleared key is re-established from the stores, never assumed)
            }

            established.TryAdd(key, evidence);
        }

        return verdict; // Verified, or a positional refusal (successor evidence, unresolved stop) that says nothing against the key's own evidence
    }

    private void Withdraw(Key key, string reason)
    {
        withdrawn.TryAdd(key, reason);
        established.TryRemove(key, out _);
    }

    /// <summary>
    /// One establishment: every store read happens here, in parallel where independent. Returns the verdict and, when the key's own evidence
    /// (TR-2..TR-4) verified, the evidence identity used by RV-6. Positional checks (RV-4, RV-5) can refuse with evidence still present.
    /// </summary>
    private static async Task<(JournalReceiptVerdict Verdict, EstablishedEvidence? Evidence)> EstablishAsync(Key key, bool head, ISecurityEventArchiveInventory inventory, IArchiveMetaRecords meta,
        DPlusDurability durability, CancellationToken cancellationToken)
    {
        var org = key.Organization;
        var stream = key.Stream;
        var revision = key.Revision;
        var streamPrefix = org.ToString("D") + "/" + stream + "/";
        var root = streamPrefix + Revision(revision);

        var objectRead = inventory.ReadEntryAsync(org, stream, revision, cancellationToken);
        var pinRead = DPlusArchivePins.ResolveAsync(durability, org, stream, cancellationToken);
        var sentinelRead = inventory.ReadIdentityAsync(cancellationToken);
        var archivedRead = meta.ListMetaAsync(DPlusRecoveryNames.ArchiveCommitmentPrefix + root, cancellationToken);
        var storedRead = durability.Records.ListAsync(DPlusRecoveryNames.StoreCommitmentPrefix + root, cancellationToken);
        var stopsRead = inventory.ReadStopMarkersAsync(org, stream, cancellationToken);
        Task<StoredObjectRead>? successorObject = null;
        Task<IReadOnlyList<ArchiveMetaObject>>? successorArchived = null;
        Task<IReadOnlyList<ArchiveMetaObject>>? successorStored = null;
        if (head)
        {
            successorObject = inventory.ReadEntryAsync(org, stream, revision + 1, cancellationToken);
            successorArchived = meta.ListMetaAsync(DPlusRecoveryNames.ArchiveCommitmentPrefix + streamPrefix + Revision(revision + 1), cancellationToken);
            successorStored = durability.Records.ListAsync(DPlusRecoveryNames.StoreCommitmentPrefix + streamPrefix + Revision(revision + 1), cancellationToken);
        }

        var all = new List<Task> { objectRead, pinRead, sentinelRead, archivedRead, storedRead, stopsRead };
        if (head)
        {
            all.AddRange([successorObject!, successorArchived!, successorStored!]);
        }

        try
        {
            await Task.WhenAll(all).ConfigureAwait(false);
        }
        catch (Exception) when (all.Any(t => t.IsFaulted))
        {
            // Rethrow the first store failure (TR-5); anything else propagates as is.
            foreach (var faulted in all.Where(t => t.IsFaulted))
            {
                var inner = faulted.Exception!.InnerException;
                if (inner is ArchiveUnavailableException or CheckpointStoreUnavailableException)
                {
                    throw inner;
                }
            }

            throw;
        }

        // TR-2: the archived object is exactly the row.
        var read = objectRead.Result;
        if (read.Status != StoredObjectStatus.Present)
        {
            return (JournalReceiptVerdict.Not(JournalReceiptCodes.ArchiveAbsent), null);
        }

        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(read.Bytes!)), key.DigestHex, StringComparison.Ordinal))
        {
            return (JournalReceiptVerdict.Not(JournalReceiptCodes.ArchiveMismatch), null);
        }

        // TR-3: the archive is the pinned instance.
        var pin = pinRead.Result;
        switch (pin.Status)
        {
            case DPlusPinStatus.Unavailable:
                return (JournalReceiptVerdict.Unavailable(JournalReceiptCodes.StoreUnavailable), null);
            case DPlusPinStatus.Pinned:
                break;
            default:
                return (JournalReceiptVerdict.Not(JournalReceiptCodes.InstanceUnpinned), null);
        }

        var sentinel = sentinelRead.Result;
        if (sentinel.Identity?.ArchiveInstanceId is not { } instance || !string.Equals(instance, pin.ExpectedArchiveInstanceId, StringComparison.Ordinal))
        {
            return (JournalReceiptVerdict.Not(JournalReceiptCodes.InstanceMismatch), null);
        }

        // TR-4: the W2-S commitment of the highest stop generation, identical in both stores, names exactly this row in the pinned instance.
        var archived = Generations(archivedRead.Result, DPlusRecoveryNames.ArchiveCommitmentPrefix + streamPrefix, revision);
        var stored = Generations(storedRead.Result, DPlusRecoveryNames.StoreCommitmentPrefix + streamPrefix, revision);
        if (archived.Malformed || stored.Malformed)
        {
            return (JournalReceiptVerdict.Not(JournalReceiptCodes.CommitmentContradictory), null);
        }

        var generation = archived.Copies.Keys.Concat(stored.Copies.Keys).DefaultIfEmpty(-1).Max();
        if (generation < 0 || !archived.Copies.TryGetValue(generation, out var a) || !stored.Copies.TryGetValue(generation, out var s))
        {
            return (JournalReceiptVerdict.Not(JournalReceiptCodes.CommitmentMissing), null); // W2-S writes both copies before any evaluation
        }

        if (!a.AsSpan().SequenceEqual(s))
        {
            return (JournalReceiptVerdict.Not(JournalReceiptCodes.CommitmentContradictory), null);
        }

        var commitment = DPlusRecoveryCodec.TryParse<DPlusEvidenceCommitment>(a);
        if (commitment is null || !string.Equals(commitment.CommitmentSchema, DPlusEvidenceCommitment.SchemaV1, StringComparison.Ordinal) || commitment.OrganizationId != org
            || !string.Equals(commitment.StreamName, stream, StringComparison.Ordinal) || commitment.StreamRevision != revision || commitment.StopGeneration != generation
            || commitment.RowEntryId != key.EntryId || !string.Equals(commitment.RowBodySha256Hex, key.DigestHex, StringComparison.Ordinal)
            || !string.Equals(commitment.RowPreviousDigestHex, key.PreviousDigestHex, StringComparison.Ordinal)
            || !string.Equals(commitment.ArchiveInstanceId, instance, StringComparison.Ordinal))
        {
            return (JournalReceiptVerdict.Not(JournalReceiptCodes.CommitmentMismatch), null);
        }

        // RV-5: a commitment of generation g ≥ 1 exists only after a durable stop of generation g − 1 on the stream at or below this revision
        // (A-4 §24.6: after an executed recovery, every later commitment carries the next generation). Without that stop the generation was
        // substituted, at first sight or after a restart.
        if (generation > 0 && !stopsRead.Result.Any(m => DPlusStreamStops.ParseMarkerObjectName(m.ObjectName, org, stream) is { } stop && stop.Revision <= revision && stop.Generation == generation - 1))
        {
            return (JournalReceiptVerdict.Not(JournalReceiptCodes.CommitmentMismatch), null);
        }

        var evidence = new EstablishedEvidence(instance, pin.ExpectedArchiveInstanceId!, generation, Convert.ToHexStringLower(SHA256.HashData(a)));

        // RV-4: a claimed stream head has no successor evidence outside AuthorityDb.
        if (head && (successorObject!.Result.Status != StoredObjectStatus.Absent || successorArchived!.Result.Count > 0 || successorStored!.Result.Count > 0))
        {
            return (JournalReceiptVerdict.Not(JournalReceiptCodes.SuccessorEvidence), evidence);
        }

        // RV-4/RV-5: stop markers. Above the entry: successor evidence for a head (an entry is not delivered past an unresolved stop, and a
        // non-head entry's later stops do not concern it). At or below it: resolved only by a later-generation commitment in both stores.
        foreach (var marker in stopsRead.Result)
        {
            if (DPlusStreamStops.ParseMarkerObjectName(marker.ObjectName, org, stream) is not { } stop)
            {
                return (JournalReceiptVerdict.Not(JournalReceiptCodes.StopUnresolved), evidence); // not canonical: never ignored
            }

            if (stop.Revision > revision)
            {
                if (head)
                {
                    return (JournalReceiptVerdict.Not(JournalReceiptCodes.SuccessorEvidence), evidence);
                }

                continue;
            }

            var resolvedGeneration = stop.Revision == revision ? generation
                : await ResolvedGenerationAsync(meta, durability, streamPrefix, stop.Revision, cancellationToken).ConfigureAwait(false);
            if (resolvedGeneration <= stop.Generation)
            {
                return (JournalReceiptVerdict.Not(JournalReceiptCodes.StopUnresolved), evidence);
            }
        }

        return (new JournalReceiptVerdict(JournalReceiptStatus.Verified, JournalReceiptCodes.Verified, generation, instance), evidence);
    }

    /// <summary>The highest generation committed in both stores for an earlier revision (-1 when none, or when the copies disagree).</summary>
    private static async Task<int> ResolvedGenerationAsync(IArchiveMetaRecords meta, DPlusDurability durability, string streamPrefix, long revision, CancellationToken cancellationToken)
    {
        var root = streamPrefix + Revision(revision);
        var archivedRead = meta.ListMetaAsync(DPlusRecoveryNames.ArchiveCommitmentPrefix + root, cancellationToken);
        var storedRead = durability.Records.ListAsync(DPlusRecoveryNames.StoreCommitmentPrefix + root, cancellationToken);
        await Task.WhenAll(archivedRead, storedRead).ConfigureAwait(false);
        var archived = Generations(archivedRead.Result, DPlusRecoveryNames.ArchiveCommitmentPrefix + streamPrefix, revision);
        var stored = Generations(storedRead.Result, DPlusRecoveryNames.StoreCommitmentPrefix + streamPrefix, revision);
        if (archived.Malformed || stored.Malformed)
        {
            return -1;
        }

        return archived.Copies.Keys.Where(g => stored.Copies.TryGetValue(g, out var copy) && copy.AsSpan().SequenceEqual(archived.Copies[g])).DefaultIfEmpty(-1).Max();
    }

    private static string Revision(long revision) => revision.ToString("D12", CultureInfo.InvariantCulture);

    private sealed record Key(Guid Organization, string Stream, long Revision, string DigestHex, Guid EntryId, string? PreviousDigestHex);

    /// <summary>RV-6: the identity of the evidence one establishment verified for a key.</summary>
    private sealed record EstablishedEvidence(string ArchiveInstanceId, string PinnedInstanceId, int StopGeneration, string CommitmentSha256Hex);

    private sealed record Attestation(long StartedTimestamp, Task<JournalReceiptVerdict> Verdict);

    private sealed record CommitmentCopies(Dictionary<int, byte[]> Copies, bool Malformed);

    /// <summary>The copies of one revision's commitments by stop generation; any name that is not a canonical key of this revision is malformed.</summary>
    private static CommitmentCopies Generations(IReadOnlyList<ArchiveMetaObject> objects, string streamPrefix, long revision)
    {
        var copies = new Dictionary<int, byte[]>();
        var malformed = false;
        foreach (var item in objects)
        {
            var name = item.ObjectName.StartsWith(streamPrefix, StringComparison.Ordinal) ? item.ObjectName[streamPrefix.Length..] : item.ObjectName;
            if (DPlusStreamStops.ParseMarkerFileName(name) is { } key && key.Revision == revision && item.Bytes is { } bytes)
            {
                copies[key.Generation] = bytes;
            }
            else
            {
                malformed = true;
            }
        }

        return new CommitmentCopies(copies, malformed);
    }
}
