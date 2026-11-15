using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Content;
using AuthorizationAuthority.Domain.Features.Keys;
using EnterpriseForms.Integrity.Canonical;
using EnterpriseForms.Integrity.Merkle;
using Xunit;
using static AuthorizationAuthority.Tests.ContributorWriteFixtures;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// E3-M2b-1 CARV application rules on the real <see cref="ContentService"/> (contract §19.4, tests M2-CARV-01..06): Prepare
/// stamps P = V; Commit accepts only P = V and stamps C = V; an operation prepared under other rules (an older build's NULL
/// stamp included) is aborted at Commit before any verification and is never handed back by a Prepare replay. The database
/// fence (CHECKs, state guard, floor) is proven separately against real PostgreSQL (docs/evidence/e3-m2b1-carv).
/// </summary>
public sealed partial class ContributorWriteProtocolTests
{
    private sealed record AcceptedState(long Revision, Guid? Operation, string Root, int Commits, int Leaves, int Identities, int Journal, int Checkpoints);

    private static AcceptedState StateOf(Harness h) => new(h.Head.CommittedContentRevision, h.Head.CommittedOperationId, Convert.ToHexString(h.Head.CommittedManifestRoot),
        h.Store.Commits.Count, h.Store.ManifestLeaves.Count, h.Store.ItemIdentities.Count, h.Store.Journal.Count, h.Store.Checkpoints.Count);

    /// <summary>
    /// A Prepared operation exactly as another build's Prepare stores it: the candidate leaves signed with the organization's key
    /// (no envelope or cross-revision rule applied) and the given stamp (NULL = a pre-CARV build). Returns the staged items.
    /// </summary>
    private static async Task<List<StagedContentItem>> SeedPreparedAsync(Harness h, Guid operation, List<Staged> candidate, int? prepareAcceptanceRuleVersion, string? idempotencyKey = null)
    {
        var revision = h.Head.CommittedContentRevision + 1;
        var key = new IntegrityKeyReference(h.Org, IntegrityKeyPurposes.RowIntegrityV1, "org-key", 1);
        var signed = new List<(Staged Item, byte[] Signature)>();
        foreach (var item in candidate)
        {
            signed.Add((item, (await h.Mac.SignAsync(key, item.Bytes, CancellationToken.None)).Mac));
        }

        var manifest = MerkleManifestV1.Build(signed.Select(s => new MerkleLeafInput((MerkleItemKind)(byte)s.Item.Kind, s.Item.ItemId, s.Signature, s.Item.Digest, (ulong)revision, operation)));
        h.Store.Operations.Add(new ContentChangeOperation
        {
            ContentChangeOperationId = operation,
            OrganizationId = h.Org,
            FormRecordId = h.Record,
            ActorIdentityBindingId = h.Store.Bindings.Single(b => b.AuthorityUserId == h.Owner).IdentityBindingId,
            OperationIdempotencyKey = idempotencyKey ?? "other-build-" + operation.ToString("N"),
            OperationRequestDigest = new byte[32],
            ExpectedAuthorizationRevision = h.State.AuthorizationRevision,
            ExpectedContentRevision = h.Head.CommittedContentRevision,
            CandidateContentRevision = revision,
            CandidateKeyVersion = 1,
            CandidateManifestRoot = manifest.MerkleRoot,
            CandidateLeafIndexDigest = manifest.LeafIndexDigest,
            CandidateLeafCount = (long)manifest.LeafCount,
            OperationStateName = ContentOperationStates.Prepared,
            CorrelationIdentifier = Guid.CreateVersion7(),
            OperationCreatedOn = DateTimeOffset.UtcNow,
            OperationExpiresOn = DateTimeOffset.UtcNow.AddMinutes(10),
            PrepareAcceptanceRuleVersion = prepareAcceptanceRuleVersion,
        });
        h.Store.CandidateLeaves.AddRange(manifest.SortedLeaves.Select((leaf, i) => new ContentCandidateLeaf
        {
            ContentCandidateLeafId = Guid.CreateVersion7(),
            ContentChangeOperationId = operation,
            ManifestItemKey = leaf.ItemKey,
            ManifestItemKindCode = (byte)leaf.Kind,
            ManifestItemId = leaf.ItemId,
            MutationKindName = ContentMutationKinds.Update,
            CanonicalPayloadDigest = leaf.CanonicalPayloadDigest.ToArray(),
            RowSignature = leaf.RowSignature.ToArray(),
            LeafDigest = manifest.LeafHashes[i],
        }));
        return signed.Select(s => new StagedContentItem(s.Item.Kind, s.Item.ItemId, s.Item.Base64, Convert.ToBase64String(s.Signature))).ToList();
    }

    private static Task<ContentRevision> CommitStagedAsync(Harness h, Guid operation, List<StagedContentItem> staged) =>
        h.Service.CommitAsync(Harness.Actor(h.Owner), operation, new CommitContentChangeRequest(h.Org, h.Record, staged), Guid.CreateVersion7(), CancellationToken.None);

    private static ContentChangeOperation Operation(Harness h, Guid operation) => h.Store.Operations.Single(o => o.ContentChangeOperationId == operation);

    // ---- M2-CARV-01 -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task PrepareStampsTheBuildRuleVersionAndCommitStampsItWithTheCommittedTransition()
    {
        var h = new Harness();
        foreach (var write in new[] { "creation", "owner edit", "owner edit carrying the sealed item" })
        {
            var operation = Guid.CreateVersion7();
            var candidate = h.Stage(write == "owner edit" ? Change(h.Rows(), h.Other, r => r.With("string_value", RowValue.Text("edited"))) : h.Rows(), operation);
            var prepared = await h.PrepareAsync(h.Owner, operation, candidate, withCurrent: true);

            var stored = Operation(h, operation);
            Assert.Equal((ContentOperationStates.Prepared, (int?)ContentAcceptanceRules.CurrentVersion, (int?)null), (stored.OperationStateName, stored.PrepareAcceptanceRuleVersion, stored.CommitAcceptanceRuleVersion));

            await h.CommitAsync(h.Owner, prepared, candidate);
            Assert.Equal((ContentOperationStates.Committed, (int?)ContentAcceptanceRules.CurrentVersion, (int?)ContentAcceptanceRules.CurrentVersion), (stored.OperationStateName, stored.PrepareAcceptanceRuleVersion, stored.CommitAcceptanceRuleVersion));
        }

        Assert.Equal(1, ContentAcceptanceRules.CurrentVersion); // M2b-1 = the E3-M2a rule set (a bump is a reviewed change)
        Assert.All(h.Store.Operations, o => Assert.Equal(o.PrepareAcceptanceRuleVersion, o.CommitAcceptanceRuleVersion));
    }

    [Fact]
    public async Task AbortedOperationsKeepTheirPrepareStampAndNeverReceiveACommitStamp()
    {
        var h = await new Harness().CreatedAsync();
        var loser = Guid.CreateVersion7();
        var loserCandidate = h.Stage(Change(h.Rows(), h.Other, r => r.With("string_value", RowValue.Text("loser"))), loser);
        var loserPrepared = await h.PrepareAsync(h.Owner, loser, loserCandidate, withCurrent: true);
        await h.WriteAsync(h.Owner, Change(h.Rows(), h.Other, r => r.With("string_value", RowValue.Text("winner"))), withCurrent: true);

        Assert.Equal(AuthorityErrorCodes.ContentRevisionConflict, (await Assert.ThrowsAsync<AuthorityException>(() => h.CommitAsync(h.Owner, loserPrepared, loserCandidate))).ErrorCode);
        var aborted = Operation(h, loser);
        Assert.Equal((ContentOperationStates.Aborted, (int?)ContentAcceptanceRules.CurrentVersion, (int?)null), (aborted.OperationStateName, aborted.PrepareAcceptanceRuleVersion, aborted.CommitAcceptanceRuleVersion));
    }

    // ---- M2-CARV-02 -------------------------------------------------------------------------------------------------------

    public static TheoryData<int?> OtherRuleVersions => new() { (int?)null, ContentAcceptanceRules.CurrentVersion + 1 };

    [Theory]
    [MemberData(nameof(OtherRuleVersions))]
    public async Task ACommitOfAnOperationPreparedUnderOtherRulesIsAbortedBeforeAnyVerificationAndNothingIsAccepted(int? prepareVersion)
    {
        var h = await new Harness().CreatedAsync();
        var before = StateOf(h);
        var operation = Guid.CreateVersion7();
        var staged = await SeedPreparedAsync(h, operation, h.Stage(Change(h.Rows(), h.Other, r => r.With("string_value", RowValue.Text("other build"))), operation), prepareVersion);
        var verificationsBefore = h.Signer.VerificationCount;

        var ex = await Assert.ThrowsAsync<AuthorityException>(() => CommitStagedAsync(h, operation, staged));

        Assert.Equal((AuthorityErrorCodes.OperationRulesOutdated, 409, false), (ex.ErrorCode, ex.HttpStatusCode, ex.Retryable));
        var aborted = Operation(h, operation);
        Assert.Equal((ContentOperationStates.Aborted, ContentAcceptanceRules.OutdatedAbortReason), (aborted.OperationStateName, aborted.OperationQuarantineReasonCode));
        Assert.Equal((prepareVersion, (int?)null), (aborted.PrepareAcceptanceRuleVersion, aborted.CommitAcceptanceRuleVersion)); // stamp untouched
        Assert.Null(aborted.OperationCommittedOn);
        Assert.Equal(before, StateOf(h)); // head, manifest, leaves, identities, journal and checkpoint unchanged
        Assert.Equal(verificationsBefore, h.Signer.VerificationCount); // refused before any signature verification

        // The same operation can never be retried into acceptance: it is aborted now.
        Assert.Equal(AuthorityErrorCodes.OperationStateInvalid, (await Assert.ThrowsAsync<AuthorityException>(() => CommitStagedAsync(h, operation, staged))).ErrorCode);
        Assert.Equal(before, StateOf(h));
    }

    [Fact]
    public async Task AnExpiredOutdatedOperationIsStillReportedAsExpired()
    {
        var h = await new Harness().CreatedAsync();
        var operation = Guid.CreateVersion7();
        var staged = await SeedPreparedAsync(h, operation, h.Stage(h.Rows(), operation), null);
        Operation(h, operation).OperationExpiresOn = DateTimeOffset.UtcNow.AddMinutes(-1);

        Assert.Equal(AuthorityErrorCodes.OperationExpired, (await Assert.ThrowsAsync<AuthorityException>(() => CommitStagedAsync(h, operation, staged))).ErrorCode);
        Assert.Equal(ContentOperationStates.Aborted, Operation(h, operation).OperationStateName);
        Assert.Null(Operation(h, operation).OperationQuarantineReasonCode); // the existing expiry path, unchanged
    }

    // ---- M2-CARV-03: R-M2a-3 regression ------------------------------------------------------------------------------------

    /// <summary>
    /// R-M2a-3: an operation prepared by a build without the E3-M2a cross-revision rule, whose candidate changes the sealed item's
    /// ciphertext under the CURRENT wrapped key and nonce. This build's Prepare refuses that candidate; Commit holds no current
    /// bytes and cannot re-derive the rule, so before CARV it was accepted. CARV refuses it at Commit.
    /// </summary>
    [Fact]
    public async Task AnUnstampedOperationReusingTheCurrentKeyAndNonceIsRefusedAtCommitAndNeverAccepted()
    {
        var h = await new Harness().CreatedAsync();
        var reuse = Change(h.Rows(), h.SealedItem, _ => ResealedWith(h, h.SealedItem, h.SealedField, ("encrypted_value_bytes", 99)));

        // Control: this build's Prepare refuses exactly this candidate.
        var refusedHere = Guid.CreateVersion7();
        Assert.Equal("ENVELOPE_INVALID: " + EnvelopeRules.Reasons.NonceReused, (await Assert.ThrowsAsync<AuthorityException>(() => h.PrepareAsync(h.Owner, refusedHere, h.Stage(reuse, refusedHere), withCurrent: true))).Message);

        var before = StateOf(h);
        var operation = Guid.CreateVersion7();
        var staged = await SeedPreparedAsync(h, operation, h.Stage(reuse, operation), prepareAcceptanceRuleVersion: null);
        var ex = await Assert.ThrowsAsync<AuthorityException>(() => CommitStagedAsync(h, operation, staged));

        Assert.Equal(AuthorityErrorCodes.OperationRulesOutdated, ex.ErrorCode);
        Assert.Equal(before, StateOf(h));
        Assert.DoesNotContain(h.Store.Commits, c => c.CommittedOperationId == operation);
        Assert.NotEqual(operation, h.Head.CommittedOperationId);
    }

    // ---- M2-CARV-04: Prepare replay -----------------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(OtherRuleVersions))]
    public async Task APrepareReplayOfAnOutdatedPreparedOperationIsRefusedAndWritesNothing(int? storedVersion)
    {
        var h = await new Harness().CreatedAsync();
        var operation = Guid.CreateVersion7();
        var candidate = h.Stage(Change(h.Rows(), h.Other, r => r.With("string_value", RowValue.Text("replayed"))), operation);
        await h.PrepareAsync(h.Owner, operation, candidate, withCurrent: true, key: "carv-replay-0001");
        var stored = Operation(h, operation);
        stored.PrepareAcceptanceRuleVersion = storedVersion; // the same row as another build's Prepare stores it
        var (operations, leaves, signatures) = (h.Store.Operations.Count, h.Store.CandidateLeaves.Count, h.Signer.SignatureCount);

        var ex = await Assert.ThrowsAsync<AuthorityException>(() => h.PrepareAsync(h.Owner, operation, candidate, withCurrent: true, key: "carv-replay-0001"));

        Assert.Equal((AuthorityErrorCodes.OperationRulesOutdated, 409, false), (ex.ErrorCode, ex.HttpStatusCode, ex.Retryable));
        Assert.Equal((operations, leaves, signatures), (h.Store.Operations.Count, h.Store.CandidateLeaves.Count, h.Signer.SignatureCount));
        Assert.Equal((ContentOperationStates.Prepared, storedVersion), (stored.OperationStateName, stored.PrepareAcceptanceRuleVersion)); // nothing written
    }

    [Fact]
    public async Task APrepareReplayOfACurrentOperationIsUnchangedAndAnIdempotencyConflictStillWins()
    {
        var h = await new Harness().CreatedAsync();
        var operation = Guid.CreateVersion7();
        var candidate = h.Stage(Change(h.Rows(), h.Other, r => r.With("string_value", RowValue.Text("replayed"))), operation);
        var first = await h.PrepareAsync(h.Owner, operation, candidate, withCurrent: true, key: "carv-replay-0002");
        var again = await h.PrepareAsync(h.Owner, operation, candidate, withCurrent: true, key: "carv-replay-0002");
        Assert.Equal((first.OperationId, first.CandidateManifestRootBase64), (again.OperationId, again.CandidateManifestRootBase64));

        // A different request under the key stays IDEMPOTENCY_CONFLICT, whatever the stored stamp.
        Operation(h, operation).PrepareAcceptanceRuleVersion = null;
        var other = h.Stage(Change(h.Rows(), h.Other, r => r.With("string_value", RowValue.Text("different"))), operation);
        Assert.Equal(AuthorityErrorCodes.IdempotencyConflict, (await Assert.ThrowsAsync<AuthorityException>(() => h.PrepareAsync(h.Owner, operation, other, withCurrent: true, key: "carv-replay-0002"))).ErrorCode);
    }

    // ---- M2-CARV-05: historical committed operations ------------------------------------------------------------------------

    [Fact]
    public async Task ACommitReplayOfAHistoricalUnstampedCommittedOperationReturnsTheHeadAndWritesNothing()
    {
        var h = new Harness();
        var operation = Guid.CreateVersion7();
        var candidate = h.Stage(h.Rows(), operation);
        var prepared = await h.PrepareAsync(h.Owner, operation, candidate, withCurrent: true);
        await h.CommitAsync(h.Owner, prepared, candidate);
        var historical = Operation(h, operation);
        (historical.PrepareAcceptanceRuleVersion, historical.CommitAcceptanceRuleVersion) = (null, null); // committed before CARV existed
        var before = StateOf(h);

        var replay = await h.CommitAsync(h.Owner, prepared, candidate);

        Assert.Equal((1L, operation), (replay.CommittedContentRevision, replay.CommittedOperationId));
        Assert.Equal(before, StateOf(h));
        Assert.Equal(((int?)null, (int?)null, ContentOperationStates.Committed), (historical.PrepareAcceptanceRuleVersion, historical.CommitAcceptanceRuleVersion, historical.OperationStateName));
    }
}
