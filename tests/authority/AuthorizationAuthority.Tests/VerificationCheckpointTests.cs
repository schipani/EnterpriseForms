using System.Text;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Journal.Verification;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Infrastructure.Archive;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// SEC-BL-01 WP-2 (C-155, contract §7): checkpoint canonical serialization, chain rules (monotonic sequence, digest chaining, carried
/// assurance), create-only conflicts, discovery outcomes (Empty, Head, Invalid, Regression, Inconclusive), the rollback classes
/// CKR-1..CKR-3, store unavailability, cancellation, and the Development/Testing Blob store's configuration guard.
/// </summary>
public sealed class VerificationCheckpointTests
{
    private static readonly Guid Org = Guid.Parse("019a5b2c-5ec1-7b01-8000-0000000000e1");
    private static readonly DateTimeOffset At = new(2026, 11, 17, 11, 0, 0, TimeSpan.Zero);
    private const string Stream = JournalStreamNames.Audit;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] D(byte fill) => Enumerable.Repeat(fill, 32).ToArray();

    private static JournalVerificationCheckpoint Baseline(long revision = 3, CheckpointAssuranceLevel assurance = CheckpointAssuranceLevel.Full, IReadOnlyList<string>? exceptions = null) =>
        new(Org, Stream, 1, revision, revision == 0 ? null : D(0x11), null, "archive-test-1", CheckpointWalkKind.Baseline, assurance, exceptions ?? [], "verifier-dev", At);

    private static JournalVerificationCheckpoint Next(JournalVerificationCheckpoint previous, byte[] previousDigest, long revision, byte fill = 0x22, CheckpointWalkKind kind = CheckpointWalkKind.Incremental) =>
        previous with
        {
            CheckpointSequence = previous.CheckpointSequence + 1, VerifiedRevision = revision, VerifiedDigest = revision == previous.VerifiedRevision ? previous.VerifiedDigest : D(fill),
            PreviousCheckpointDigest = previousDigest, WalkKind = kind, VerifiedAtUtc = previous.VerifiedAtUtc.AddMinutes(5),
        };

    /// <summary>A store holding a valid chain of <paramref name="count"/> checkpoints.</summary>
    private static async Task<(InMemoryVerificationCheckpointStore Store, List<JournalVerificationCheckpoint> Chain)> StoreWithChainAsync(int count)
    {
        var store = new InMemoryVerificationCheckpointStore();
        var chain = new List<JournalVerificationCheckpoint>();
        var current = await JournalVerificationCheckpoints.DiscoverAsync(store, Org, Stream, CheckpointDiscoveryMode.FullChain, 0, 100, Ct);
        for (var i = 0; i < count; i++)
        {
            var candidate = i == 0 ? Baseline() : Next(chain[^1], current.HeadDigest!, 3 + i, (byte)(0x30 + i));
            Assert.Equal(CheckpointAppendOutcome.Created, (await JournalVerificationCheckpoints.CreateNextAsync(store, current, candidate, Ct)).Outcome);
            chain.Add(candidate);
            current = await JournalVerificationCheckpoints.DiscoverAsync(store, Org, Stream, CheckpointDiscoveryMode.FullChain, 0, 100, Ct);
        }

        return (store, chain);
    }

    private static Task<CheckpointDiscoveryResult> DiscoverAsync(IVerificationCheckpointStore store, CheckpointDiscoveryMode mode = CheckpointDiscoveryMode.FullChain, long minimum = 0) =>
        JournalVerificationCheckpoints.DiscoverAsync(store, Org, Stream, mode, minimum, 2, Ct);

    // ---- Canonical serialization ----

    [Fact]
    public void SerializationIsDeterministicCanonicalAndRoundTrips()
    {
        var checkpoint = Baseline();
        var bytes = JournalVerificationCheckpoints.Serialize(checkpoint);
        Assert.Equal(bytes, JournalVerificationCheckpoints.Serialize(checkpoint with { BaselineExceptionIds = [] }));
        Assert.Equal(
            "{\"checkpoint_schema\":\"DynamicForms/JournalVerification/v1\",\"organization_id\":\"019a5b2c-5ec1-7b01-8000-0000000000e1\",\"stream_name\":\"audit\","
            + "\"checkpoint_sequence\":1,\"verified_revision\":3,\"verified_digest_hex\":\"" + new string('1', 64) + "\",\"previous_checkpoint_digest_hex\":null,"
            + "\"archive_instance_id\":\"archive-test-1\",\"walk_kind\":\"Baseline\",\"assurance_level\":\"Full\",\"baseline_exception_ids\":[],"
            + "\"verifier_identity_label\":\"verifier-dev\",\"verified_at_utc\":\"2026-11-17T11:00:00+00:00\"}",
            Encoding.UTF8.GetString(bytes));

        var parsed = JournalVerificationCheckpoints.Parse(bytes);
        Assert.Equal(CheckpointParseOutcome.Valid, parsed.Outcome);
        Assert.Equal(bytes, JournalVerificationCheckpoints.Serialize(parsed.Checkpoint!));
    }

    [Theory]
    [InlineData("\"walk_kind\":\"Baseline\"", "\"walk_kind\":\"baseline\"")] // enum spelling
    [InlineData("\"walk_kind\":\"Baseline\"", "\"walk_kind\":\"0\"")] // numeric enum
    [InlineData("\"verified_revision\":3,", "\"verified_revision\":3,\"unknown_member\":1,")] // unknown member
    [InlineData("\"stream_name\":\"audit\",", "")] // missing member
    [InlineData("\"stream_name\":\"audit\",", "\"stream_name\":\"audit\",\"stream_name\":\"audit\",")] // duplicate member
    [InlineData("{\"checkpoint_schema\"", "{ \"checkpoint_schema\"")] // whitespace: not canonical
    [InlineData("1111111111111111", "AAAAAAAAAAAAAAAA")] // non-lower-case hex
    [InlineData("\"stream_name\":\"audit\"", "\"stream_name\":\"other\"")] // unknown stream
    [InlineData("+00:00", "+01:00")] // not UTC
    public void NonCanonicalOrInvalidCheckpointBytesAreMalformed(string from, string to)
    {
        var text = Encoding.UTF8.GetString(JournalVerificationCheckpoints.Serialize(Baseline()));
        Assert.Contains(from, text, StringComparison.Ordinal);
        Assert.Equal(CheckpointParseOutcome.Malformed, JournalVerificationCheckpoints.Parse(Encoding.UTF8.GetBytes(text.Replace(from, to, StringComparison.Ordinal))).Outcome);
    }

    [Fact]
    public void AnUnknownSchemaIsUnsupportedNeverValid()
    {
        var text = Encoding.UTF8.GetString(JournalVerificationCheckpoints.Serialize(Baseline())).Replace("JournalVerification/v1", "JournalVerification/v2", StringComparison.Ordinal);
        Assert.Equal(CheckpointParseOutcome.Unsupported, JournalVerificationCheckpoints.Parse(Encoding.UTF8.GetBytes(text)).Outcome);
        Assert.Equal(CheckpointParseOutcome.Malformed, JournalVerificationCheckpoints.Parse("[]"u8).Outcome);
        Assert.Equal(CheckpointParseOutcome.Malformed, JournalVerificationCheckpoints.Parse("not json"u8).Outcome);
    }

    [Fact]
    public void FieldRulesAreEnforced()
    {
        Assert.Null(JournalVerificationCheckpoints.Validate(Baseline()));
        Assert.Null(JournalVerificationCheckpoints.Validate(Baseline(revision: 0)));
        Assert.Null(JournalVerificationCheckpoints.Validate(Baseline(assurance: CheckpointAssuranceLevel.Reduced, exceptions: ["BX-1", "BX-2"])));
        Assert.NotNull(JournalVerificationCheckpoints.Validate(Baseline() with { WalkKind = CheckpointWalkKind.Incremental })); // sequence 1 is a baseline
        Assert.NotNull(JournalVerificationCheckpoints.Validate(Baseline() with { PreviousCheckpointDigest = D(1) })); // no link at sequence 1
        Assert.NotNull(JournalVerificationCheckpoints.Validate(Baseline() with { CheckpointSequence = 2 })); // a link is required after 1
        Assert.NotNull(JournalVerificationCheckpoints.Validate(Baseline() with { VerifiedDigest = null })); // digest required for revision > 0
        Assert.NotNull(JournalVerificationCheckpoints.Validate(Baseline(revision: 0) with { VerifiedDigest = D(1) }));
        Assert.NotNull(JournalVerificationCheckpoints.Validate(Baseline(assurance: CheckpointAssuranceLevel.Reduced))); // Reduced names its exceptions
        Assert.NotNull(JournalVerificationCheckpoints.Validate(Baseline(exceptions: ["BX-1"]))); // Full carries none
        Assert.NotNull(JournalVerificationCheckpoints.Validate(Baseline(assurance: CheckpointAssuranceLevel.Reduced, exceptions: ["BX-2", "BX-1"]))); // sorted
        Assert.NotNull(JournalVerificationCheckpoints.Validate(Baseline(assurance: CheckpointAssuranceLevel.Reduced, exceptions: ["BX-1", "BX-1"]))); // distinct
        Assert.NotNull(JournalVerificationCheckpoints.Validate(Baseline() with { VerifiedAtUtc = At.ToOffset(TimeSpan.FromHours(2)) }));
        Assert.Throws<ArgumentException>(() => JournalVerificationCheckpoints.Serialize(Baseline() with { CheckpointSequence = 0 }));
    }

    // ---- Chain rules (CK-2, CK-3) ----

    [Fact]
    public void ASuccessorMustLinkAdvanceMonotonicallyAndCarryAssurance()
    {
        var first = Baseline();
        var digest = JournalVerificationCheckpoints.Digest(JournalVerificationCheckpoints.Serialize(first));
        Assert.Null(JournalVerificationCheckpoints.CheckSuccessor(first, digest, Next(first, digest, 5)));
        Assert.Null(JournalVerificationCheckpoints.CheckSuccessor(first, digest, Next(first, digest, 3))); // no progress is allowed, same digest

        Assert.NotNull(JournalVerificationCheckpoints.CheckSuccessor(first, digest, Next(first, D(9), 5))); // wrong link
        Assert.NotNull(JournalVerificationCheckpoints.CheckSuccessor(first, digest, Next(first, digest, 5) with { CheckpointSequence = 3 })); // skipped sequence
        Assert.NotNull(JournalVerificationCheckpoints.CheckSuccessor(first, digest, Next(first, digest, 2))); // revision decreased
        Assert.NotNull(JournalVerificationCheckpoints.CheckSuccessor(first, digest, Next(first, digest, 3) with { VerifiedDigest = D(7) })); // same revision, other digest
        Assert.NotNull(JournalVerificationCheckpoints.CheckSuccessor(first, digest, Next(first, digest, 5) with { OrganizationId = Guid.NewGuid() }));
        Assert.NotNull(JournalVerificationCheckpoints.CheckSuccessor(first, digest, Next(first, digest, 5) with { ArchiveInstanceId = "archive-other" }));
        Assert.NotNull(JournalVerificationCheckpoints.CheckSuccessor(first, digest,
            Next(first, digest, 5) with { AssuranceLevel = CheckpointAssuranceLevel.Reduced, BaselineExceptionIds = ["BX-1"] })); // assurance changes only at a baseline

        // A re-baseline (BL-10 as amended by A-1) may add exceptions (and so become Reduced), still linked to the chain ...
        Assert.Null(JournalVerificationCheckpoints.CheckSuccessor(first, digest,
            Next(first, digest, 5, kind: CheckpointWalkKind.Baseline) with { AssuranceLevel = CheckpointAssuranceLevel.Reduced, BaselineExceptionIds = ["BX-1"] }));

        // ... but never switch archive instance (recovery, G14), drop an exception or upgrade assurance.
        Assert.NotNull(JournalVerificationCheckpoints.CheckSuccessor(first, digest,
            Next(first, digest, 5, kind: CheckpointWalkKind.Baseline) with { ArchiveInstanceId = "archive-test-2" }));
        var reduced = first with { AssuranceLevel = CheckpointAssuranceLevel.Reduced, BaselineExceptionIds = ["BX-1", "BX-2"] };
        var reducedDigest = JournalVerificationCheckpoints.Digest(JournalVerificationCheckpoints.Serialize(reduced));
        Assert.NotNull(JournalVerificationCheckpoints.CheckSuccessor(reduced, reducedDigest,
            Next(reduced, reducedDigest, 5, kind: CheckpointWalkKind.Baseline) with { BaselineExceptionIds = ["BX-1"] }));
        Assert.NotNull(JournalVerificationCheckpoints.CheckSuccessor(reduced, reducedDigest,
            Next(reduced, reducedDigest, 5, kind: CheckpointWalkKind.Baseline) with { AssuranceLevel = CheckpointAssuranceLevel.Full, BaselineExceptionIds = [] }));
        Assert.Null(JournalVerificationCheckpoints.CheckSuccessor(reduced, reducedDigest,
            Next(reduced, reducedDigest, 5, kind: CheckpointWalkKind.Baseline) with { BaselineExceptionIds = ["BX-1", "BX-2", "BX-3"] }));
    }

    [Fact]
    public async Task ReducedAssuranceIsCarriedForwardAndNeverUpgradedByAnIncrementalCheckpoint()
    {
        var store = new InMemoryVerificationCheckpointStore();
        var empty = await DiscoverAsync(store);
        var reduced = Baseline(assurance: CheckpointAssuranceLevel.Reduced, exceptions: ["BX-LEGACY-1"]);
        Assert.Equal(CheckpointAppendOutcome.Created, (await JournalVerificationCheckpoints.CreateNextAsync(store, empty, reduced, Ct)).Outcome);
        var head = await DiscoverAsync(store);

        var upgraded = Next(reduced, head.HeadDigest!, 5) with { AssuranceLevel = CheckpointAssuranceLevel.Full, BaselineExceptionIds = [] };
        Assert.Equal(CheckpointAppendOutcome.Refused, (await JournalVerificationCheckpoints.CreateNextAsync(store, head, upgraded, Ct)).Outcome);
        Assert.Equal(CheckpointAppendOutcome.Created, (await JournalVerificationCheckpoints.CreateNextAsync(store, head, Next(reduced, head.HeadDigest!, 5), Ct)).Outcome);
        Assert.Equal(CheckpointAssuranceLevel.Reduced, (await DiscoverAsync(store)).Head!.AssuranceLevel);
    }

    // ---- Create-only store, conflicts, idempotent retry ----

    [Fact]
    public async Task TheStoreIsCreateOnlyIdenticalBytesAreIdempotentAndOtherBytesConflictWithoutOverwriting()
    {
        var store = new InMemoryVerificationCheckpointStore();
        var bytes = JournalVerificationCheckpoints.Serialize(Baseline());
        Assert.Equal(CheckpointCreateStatus.Created, (await store.CreateAsync(Org, Stream, 1, bytes, Ct)).Status);
        Assert.Equal(CheckpointCreateStatus.IdenticalExists, (await store.CreateAsync(Org, Stream, 1, bytes, Ct)).Status);

        var other = JournalVerificationCheckpoints.Serialize(Baseline(revision: 4));
        var conflict = await store.CreateAsync(Org, Stream, 1, other, Ct);
        Assert.Equal(CheckpointCreateStatus.Conflict, conflict.Status);
        Assert.Equal(bytes, conflict.ExistingBytes);
        Assert.Equal(bytes, (await store.ReadAsync(Org, Stream, 1, Ct)).Bytes);
    }

    [Fact]
    public async Task CreateNextReportsAnIdenticalRetryAndAConflictingCheckpointAtTheSameSequence()
    {
        var store = new InMemoryVerificationCheckpointStore();
        var empty = await DiscoverAsync(store);
        Assert.Equal(CheckpointAppendOutcome.Created, (await JournalVerificationCheckpoints.CreateNextAsync(store, empty, Baseline(), Ct)).Outcome);
        Assert.Equal(CheckpointAppendOutcome.AlreadyStored, (await JournalVerificationCheckpoints.CreateNextAsync(store, empty, Baseline(), Ct)).Outcome);

        var conflict = await JournalVerificationCheckpoints.CreateNextAsync(store, empty, Baseline(revision: 9), Ct);
        Assert.Equal((CheckpointAppendOutcome.Conflict, CheckpointFindingCodes.Conflict), (conflict.Outcome, conflict.FindingCode));
        Assert.Equal(3, (await DiscoverAsync(store)).Head!.VerifiedRevision);
    }

    [Fact]
    public async Task CreateNextRefusesToAppendAfterAnInvalidRegressedOrInconclusiveDiscoveryOrOutOfOrder()
    {
        var (store, chain) = await StoreWithChainAsync(2);
        var head = await DiscoverAsync(store);
        foreach (var outcome in new[] { CheckpointDiscoveryOutcome.Invalid, CheckpointDiscoveryOutcome.Regression, CheckpointDiscoveryOutcome.Inconclusive })
        {
            var result = await JournalVerificationCheckpoints.CreateNextAsync(store, head with { Outcome = outcome }, Next(chain[^1], head.HeadDigest!, 9), Ct);
            Assert.Equal(CheckpointAppendOutcome.Refused, result.Outcome);
        }

        Assert.Equal(CheckpointAppendOutcome.Refused, (await JournalVerificationCheckpoints.CreateNextAsync(store, head, Next(chain[^1], D(5), 9), Ct)).Outcome); // wrong link
        Assert.Equal(CheckpointAppendOutcome.Refused, (await JournalVerificationCheckpoints.CreateNextAsync(store, head, Next(chain[^1], head.HeadDigest!, 1), Ct)).Outcome); // decreasing
        Assert.Equal(CheckpointAppendOutcome.Refused, (await JournalVerificationCheckpoints.CreateNextAsync(store, await DiscoverAsync(new InMemoryVerificationCheckpointStore()), chain[1], Ct)).Outcome);
        Assert.Equal(2, (await DiscoverAsync(store)).Head!.CheckpointSequence);
    }

    // ---- Discovery and rollback classes ----

    [Fact]
    public async Task AnEmptyStoreIsEmptyAndAChainYieldsItsVerifiedHeadInBothModes()
    {
        Assert.Equal(CheckpointDiscoveryOutcome.Empty, (await DiscoverAsync(new InMemoryVerificationCheckpointStore())).Outcome);
        var (store, chain) = await StoreWithChainAsync(4);
        foreach (var mode in new[] { CheckpointDiscoveryMode.HeadAndPredecessor, CheckpointDiscoveryMode.FullChain })
        {
            var found = await DiscoverAsync(store, mode);
            Assert.Equal(CheckpointDiscoveryOutcome.Head, found.Outcome);
            Assert.Equal((4L, chain[3].VerifiedRevision), (found.Head!.CheckpointSequence, found.Head.VerifiedRevision));
            Assert.Equal(JournalVerificationCheckpoints.Digest(JournalVerificationCheckpoints.Serialize(chain[3])), found.HeadDigest);
        }
    }

    [Fact]
    public async Task CKR1CorruptionIsDetectedByChainVerification()
    {
        var (store, chain) = await StoreWithChainAsync(3);
        store.PutObjectForTesting(InMemoryVerificationCheckpointStore.KeyForTesting(Org, Stream, 1), JournalVerificationCheckpoints.Serialize(chain[0] with { VerifiedRevision = 4, VerifiedDigest = D(4) }));

        var full = await DiscoverAsync(store, CheckpointDiscoveryMode.FullChain);
        Assert.Equal((CheckpointDiscoveryOutcome.Invalid, CheckpointFindingCodes.ChainInvalid), (full.Outcome, full.FindingCode));

        // Head-and-predecessor mode reads only sequences 2..3: corruption of 1 waits for the full-chain check (CK-5).
        Assert.Equal(CheckpointDiscoveryOutcome.Head, (await DiscoverAsync(store, CheckpointDiscoveryMode.HeadAndPredecessor)).Outcome);

        store.PutObjectForTesting(InMemoryVerificationCheckpointStore.KeyForTesting(Org, Stream, 3), "{not json"u8.ToArray());
        Assert.Equal(CheckpointDiscoveryOutcome.Invalid, (await DiscoverAsync(store, CheckpointDiscoveryMode.HeadAndPredecessor)).Outcome);
    }

    [Fact]
    public async Task AGapAnUnexpectedObjectOrContentDifferentFromItsKeyIsInvalid()
    {
        var (store, chain) = await StoreWithChainAsync(3);
        store.RemoveObjectForTesting(InMemoryVerificationCheckpointStore.KeyForTesting(Org, Stream, 2));
        Assert.Equal(CheckpointFindingCodes.ChainInvalid, (await DiscoverAsync(store)).FindingCode);

        var (other, _) = await StoreWithChainAsync(2);
        other.PutObjectForTesting($"{Org:D}/{Stream}/02", "{}"u8.ToArray());
        Assert.Equal((CheckpointDiscoveryOutcome.Invalid, CheckpointFindingCodes.UnexpectedObject), ((await DiscoverAsync(other)).Outcome, (await DiscoverAsync(other)).FindingCode));

        var (moved, movedChain) = await StoreWithChainAsync(1);
        moved.PutObjectForTesting(InMemoryVerificationCheckpointStore.KeyForTesting(Org, Stream, 2), JournalVerificationCheckpoints.Serialize(movedChain[0]));
        Assert.Equal(CheckpointDiscoveryOutcome.Invalid, (await DiscoverAsync(moved)).Outcome);
        Assert.Equal(3, chain.Count);
    }

    [Fact]
    public async Task CKR2ARollbackBelowATrustedHeadIsARegression()
    {
        var (store, _) = await StoreWithChainAsync(3);
        store.RemoveObjectForTesting(InMemoryVerificationCheckpointStore.KeyForTesting(Org, Stream, 3));
        var result = await DiscoverAsync(store, minimum: 3);
        Assert.Equal((CheckpointDiscoveryOutcome.Regression, CheckpointFindingCodes.StoreRegression), (result.Outcome, result.FindingCode));
        Assert.Null(result.Head);

        var (wiped, _) = await StoreWithChainAsync(1);
        wiped.RemoveObjectForTesting(InMemoryVerificationCheckpointStore.KeyForTesting(Org, Stream, 1));
        Assert.Equal(CheckpointDiscoveryOutcome.Regression, (await DiscoverAsync(wiped, minimum: 1)).Outcome);
    }

    [Fact]
    public async Task CKR3WithoutATrustedHeadTheSameRollbackIsUndetectableAndYieldsTheOlderHead()
    {
        // Contract §7.3: a checkpoint hash chain does not prove its newest checkpoint still exists. Deleting the newest checkpoint leaves a
        // valid shorter chain; without an independently anchored head (production: Confidential Ledger, not implemented) nothing detects it.
        var (store, chain) = await StoreWithChainAsync(3);
        store.RemoveObjectForTesting(InMemoryVerificationCheckpointStore.KeyForTesting(Org, Stream, 3));
        var result = await DiscoverAsync(store, minimum: 0);
        Assert.Equal(CheckpointDiscoveryOutcome.Head, result.Outcome);
        Assert.Equal(chain[1].CheckpointSequence, result.Head!.CheckpointSequence);
    }

    [Fact]
    public async Task StoreUnavailabilityAListedObjectThatCannotBeReadAndAnUnknownSchemaAreInconclusive()
    {
        Assert.Equal((CheckpointDiscoveryOutcome.Inconclusive, CheckpointFindingCodes.StoreUnavailable),
            ((await DiscoverAsync(new ScriptedStore(failListing: true))).Outcome, (await DiscoverAsync(new ScriptedStore(failListing: true))).FindingCode));
        Assert.Equal(CheckpointFindingCodes.StoreUnavailable, (await DiscoverAsync(new ScriptedStore(failReads: true))).FindingCode);
        Assert.Equal(CheckpointFindingCodes.ListedObjectMissing, (await DiscoverAsync(new ScriptedStore(readAbsent: true))).FindingCode);

        var (store, chain) = await StoreWithChainAsync(1);
        var v2 = Encoding.UTF8.GetString(JournalVerificationCheckpoints.Serialize(chain[0])).Replace("JournalVerification/v1", "JournalVerification/v2", StringComparison.Ordinal);
        store.PutObjectForTesting(InMemoryVerificationCheckpointStore.KeyForTesting(Org, Stream, 1), Encoding.UTF8.GetBytes(v2));
        Assert.Equal((CheckpointDiscoveryOutcome.Inconclusive, CheckpointFindingCodes.SchemaUnsupported), ((await DiscoverAsync(store)).Outcome, (await DiscoverAsync(store)).FindingCode));

        var unavailable = await JournalVerificationCheckpoints.CreateNextAsync(new ScriptedStore(failWrites: true), await DiscoverAsync(new InMemoryVerificationCheckpointStore()), Baseline(), Ct);
        Assert.Equal((CheckpointAppendOutcome.Unavailable, CheckpointFindingCodes.StoreUnavailable), (unavailable.Outcome, unavailable.FindingCode));
    }

    [Fact]
    public async Task CancellationPropagatesThroughDiscoveryCreationAndTheStore()
    {
        var (store, _) = await StoreWithChainAsync(2);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => JournalVerificationCheckpoints.DiscoverAsync(store, Org, Stream, CheckpointDiscoveryMode.FullChain, 0, 10, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await JournalVerificationCheckpoints.CreateNextAsync(store, await DiscoverAsync(new InMemoryVerificationCheckpointStore()), Baseline(), cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadAsync(Org, Stream, 1, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ListAsync(Org, Stream, null, 10, cancelled.Token));
    }

    [Fact]
    public async Task CheckpointsAreIsolatedByOrganizationAndStream()
    {
        var (store, _) = await StoreWithChainAsync(2);
        Assert.Equal(CheckpointDiscoveryOutcome.Empty, (await JournalVerificationCheckpoints.DiscoverAsync(store, Guid.NewGuid(), Stream, CheckpointDiscoveryMode.FullChain, 0, 10, Ct)).Outcome);
        Assert.Equal(CheckpointDiscoveryOutcome.Empty, (await JournalVerificationCheckpoints.DiscoverAsync(store, Org, JournalStreamNames.Content, CheckpointDiscoveryMode.FullChain, 0, 10, Ct)).Outcome);
    }

    // ---- Development/Testing Blob store guard ----

    private static VerificationCheckpointStoreOptions StoreOptions(string uri = "http://journal-verification-store:10000/efcheckpoints", string account = "efcheckpoints", string key = "c2VwYXJhdGUtY2hlY2twb2ludC1rZXktZm9yLXRlc3RzLW9ubHk=") =>
        new() { Provider = "Blob", BlobServiceUri = uri, AccountName = account, AccountKey = key };

    private static ArchiveOptions ArchiveSettings() => new()
    {
        Provider = "Blob", BlobServiceUri = "http://archive-store:10000/devstoreaccount1", AccountName = "devstoreaccount1", AccountKey = BlobVerificationCheckpointStore.WellKnownDevelopmentStorageKey,
    };

    [Fact]
    public void TheBlobCheckpointStoreRefusesTheWellKnownKeyAndAnyConfigurationThatIsTheArchive()
    {
        Assert.Null(BlobVerificationCheckpointStore.RefusalReason(StoreOptions(), ArchiveSettings()));
        Assert.NotNull(BlobVerificationCheckpointStore.RefusalReason(new VerificationCheckpointStoreOptions(), ArchiveSettings()));
        Assert.NotNull(BlobVerificationCheckpointStore.RefusalReason(StoreOptions(uri: "not a uri"), ArchiveSettings()));
        Assert.NotNull(BlobVerificationCheckpointStore.RefusalReason(StoreOptions(key: BlobVerificationCheckpointStore.WellKnownDevelopmentStorageKey), new ArchiveOptions()));
        Assert.NotNull(BlobVerificationCheckpointStore.RefusalReason(StoreOptions(key: "c2FtZS1rZXk="), ArchiveSettings().With(key: "c2FtZS1rZXk=")));
        Assert.NotNull(BlobVerificationCheckpointStore.RefusalReason(StoreOptions(account: "DEVSTOREACCOUNT1"), ArchiveSettings()));
        Assert.NotNull(BlobVerificationCheckpointStore.RefusalReason(StoreOptions(uri: "http://archive-store:10000/efcheckpoints"), ArchiveSettings()));

        var options = Microsoft.Extensions.Options.Options.Create(StoreOptions(key: BlobVerificationCheckpointStore.WellKnownDevelopmentStorageKey));
        Assert.Throws<InvalidOperationException>(() => new BlobVerificationCheckpointStore(options, Microsoft.Extensions.Options.Options.Create(ArchiveSettings())));
    }

    [Theory]
    [InlineData("verification/019a5b2c-5ec1-7b01-8000-0000000000e1/audit/000000000007.json", true, 7L)]
    [InlineData("verification/019a5b2c-5ec1-7b01-8000-0000000000e1/audit/000000000000.json", false, null)]
    [InlineData("verification/019a5b2c-5ec1-7b01-8000-0000000000e1/audit/7.json", false, null)]
    [InlineData("verification/019a5b2c-5ec1-7b01-8000-0000000000e1/audit/x/000000000007.json", false, null)]
    [InlineData("verification/019a5b2c-5ec1-7b01-8000-0000000000e2/audit/000000000007.json", false, null)]
    public void BlobCheckpointNamesAreClassifiedStrictly(string name, bool canonical, long? sequence)
    {
        var listed = BlobVerificationCheckpointStore.Classify(name, $"verification/{Org:D}/audit/");
        Assert.Equal((canonical, sequence), (listed.IsCanonicalCheckpointKey, listed.CheckpointSequence));
        Assert.Equal($"verification/{Org:D}/audit/000000000007.json", BlobVerificationCheckpointStore.CheckpointBlobName(Org, Stream, 7));
    }

    /// <summary>A store scripted to fail: listing, reads or writes unavailable, or a listed sequence that reads as absent.</summary>
    private sealed class ScriptedStore(bool failListing = false, bool failReads = false, bool failWrites = false, bool readAbsent = false) : IVerificationCheckpointStore
    {
        public string StoreName => "Scripted";

        public Task<CheckpointCreateResult> CreateAsync(Guid organizationId, string streamName, long checkpointSequence, ReadOnlyMemory<byte> checkpointBytes, CancellationToken cancellationToken) =>
            failWrites ? throw new CheckpointStoreUnavailableException("scripted write outage") : Task.FromResult(new CheckpointCreateResult(CheckpointCreateStatus.Created, null));

        public Task<StoredObjectRead> ReadAsync(Guid organizationId, string streamName, long checkpointSequence, CancellationToken cancellationToken) =>
            failReads ? throw new CheckpointStoreUnavailableException("scripted read outage")
            : Task.FromResult(readAbsent ? StoredObjectRead.Absent : StoredObjectRead.Present(JournalVerificationCheckpoints.Serialize(Baseline())));

        public Task<CheckpointListingPage> ListAsync(Guid organizationId, string streamName, string? continuationToken, int pageSizeHint, CancellationToken cancellationToken) =>
            failListing ? throw new CheckpointStoreUnavailableException("scripted listing outage")
            : Task.FromResult(new CheckpointListingPage([new CheckpointListedObject("scripted/1", true, 1)], null, false));
    }
}

internal static class ArchiveOptionsTestExtensions
{
    public static ArchiveOptions With(this ArchiveOptions options, string key) =>
        new() { Provider = options.Provider, BlobServiceUri = "http://elsewhere:10000/other", AccountName = "otheraccount", AccountKey = key, ContainerName = options.ContainerName };
}
