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
/// E3-M2a: the central Envelope v1 rules on the user write paths of the real <see cref="ContentService"/> — record creation
/// and owner edits (which had no envelope check before), share-editor updates, and commit (an operation prepared by an
/// earlier Authority build). A refusal happens before anything is signed or stored; v1 behaviour for well-formed envelopes
/// is unchanged. The protection-migration path and the census are in ProtectionReconciliationTests.
/// </summary>
public sealed partial class ContributorWriteProtocolTests
{
    private static Row Malformed(Row row, string variant) => variant switch
    {
        "field version 2" => row.With("encryption_envelope_version", RowValue.Int32(2)),
        "field algorithm" => row.With("encryption_algorithm_name", RowValue.Text("AES-128-CBC")),
        "field nonce" => row.With("encryption_nonce_bytes", RowValue.Bytes(Fill(8, 1))),
        "field tag" => row.With("encryption_auth_tag_bytes", RowValue.Bytes(Fill(12, 1))),
        "field wrapped key" => row.With("wrapped_data_key_bytes", RowValue.Bytes(Fill(32, 1))),
        "field empty ciphertext" => row.With("encrypted_value_bytes", RowValue.Bytes(Array.Empty<byte>())),
        "field reference" => row.With("encryption_key_reference", RowValue.Text("unknown-key")),
        "document version 2" => row.With("encryption_envelope_version", RowValue.Int32(2)),
        "document prefix" => row.With("encryption_nonce_prefix", RowValue.Bytes(Fill(12, 1))),
        "document segment" => row.With("encryption_segment_size_bytes", RowValue.Int32(4096)),
        "document wrapped key" => row.With("wrapped_data_key", RowValue.Bytes(Fill(16, 1))),
        "document reference" => row.With("encryption_key_reference", RowValue.Text("localdev:" + Guid.CreateVersion7().ToString("D") + ":document:1")),
        _ => throw new ArgumentOutOfRangeException(nameof(variant)),
    };

    private static List<Row> WithMalformed(Harness h, string variant) =>
        Change(h.Rows(), variant.StartsWith("field", StringComparison.Ordinal) ? h.SealedItem : h.Document, r => Malformed(r, variant));

    public static TheoryData<string> MalformedVariants =>
    [
        "field version 2", "field algorithm", "field nonce", "field tag", "field wrapped key", "field empty ciphertext", "field reference",
        "document version 2", "document prefix", "document segment", "document wrapped key", "document reference",
    ];

    [Theory]
    [MemberData(nameof(MalformedVariants))]
    public async Task ARecordCreatedWithAMalformedEnvelopeIsRefusedBeforeAnythingIsSigned(string variant)
    {
        var h = new Harness();
        var operation = Guid.CreateVersion7();
        var ex = await Assert.ThrowsAsync<AuthorityException>(() => h.PrepareAsync(h.Owner, operation, h.Stage(WithMalformed(h, variant), operation), withCurrent: true));

        Assert.Equal(AuthorityErrorCodes.InvalidCommand, ex.ErrorCode);
        Assert.StartsWith("ENVELOPE_INVALID: ENVELOPE_", ex.Message, StringComparison.Ordinal);
        Assert.Empty(h.Store.Operations);
        Assert.Empty(h.Store.CandidateLeaves);
        Assert.Equal(0, h.Head.CommittedContentRevision);
    }

    [Theory]
    [MemberData(nameof(MalformedVariants))]
    public async Task AnOwnerEditIntroducingAMalformedEnvelopeIsRefusedAndTheHeadStays(string variant)
    {
        var h = await new Harness().CreatedAsync();
        var head = (h.Head.CommittedContentRevision, h.Head.CommittedOperationId);
        var operation = Guid.CreateVersion7();
        var ex = await Assert.ThrowsAsync<AuthorityException>(() => h.PrepareAsync(h.Owner, operation, h.Stage(WithMalformed(h, variant), operation), withCurrent: true));

        Assert.Contains("ENVELOPE_INVALID", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(h.Store.Operations, o => o.ContentChangeOperationId == operation);
        Assert.Equal(head, (h.Head.CommittedContentRevision, h.Head.CommittedOperationId));
    }

    [Fact]
    public async Task AnOwnerEditCarryingTwoValuesUnderOneWrappedKeyAndNonceIsRefused()
    {
        var h = await new Harness().CreatedAsync();
        var twin = Sealed(h.Org, h.Record, h.OtherField, Guid.CreateVersion7(), 10); // another item, the sealed item's key and nonce
        var operation = Guid.CreateVersion7();
        var ex = await Assert.ThrowsAsync<AuthorityException>(() => h.PrepareAsync(h.Owner, operation, h.Stage([.. h.Rows(), twin], operation), withCurrent: true));
        Assert.Contains(EnvelopeRules.Reasons.NonceReused, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(h.Store.Operations, o => o.ContentChangeOperationId == operation);
    }

    [Fact]
    public async Task EveryReferenceFormTheBusinessWritesIsAcceptedAndCarriedOnLaterWrites()
    {
        var h = new Harness();

        // Each reference form arrives with a fresh field envelope (as a re-seal does): a changed reference on the same wrapped key
        // would be data-key reuse (E3-M2a remediation), and the business never relabels a carried envelope.
        List<Row> Scoped(string fieldFamily, string documentFamily, byte seed) =>
        [
            .. Change(Change(h.Rows(), h.SealedItem, _ => Sealed(h.Org, h.Record, h.SealedField, h.SealedItem, seed).With("encryption_key_reference", RowValue.Text($"{fieldFamily}:{h.Org:D}:field-value:3"))),
                h.Document, r => r.With("encryption_key_reference", RowValue.Text($"{documentFamily}:{h.Org:D}:document:1"))),
        ];

        Assert.Equal(1, (await h.WriteAsync(h.Owner, Scoped("localdev", "vault-transit", 110), withCurrent: true)).CommittedContentRevision);
        Assert.Equal(2, (await h.WriteAsync(h.Owner, Scoped("localdev", "vault-transit", 110), withCurrent: true)).CommittedContentRevision); // carried
        Assert.Equal(3, (await h.WriteAsync(h.Owner, Scoped("vault-transit", "localdev", 120), withCurrent: true)).CommittedContentRevision);
        Assert.Equal(4, (await h.WriteAsync(h.Owner, h.Rows(), withCurrent: true)).CommittedContentRevision); // legacy literal, fresh envelope (seed 10)

        // Relabelling a carried envelope with another reference keeps its data key: refused.
        var relabelled = Change(h.Rows(), h.SealedItem, r => r.With("encryption_key_reference", RowValue.Text($"localdev:{h.Org:D}:field-value:4")));
        await RefusedBeforeSigningAsync(h, h.Owner, relabelled, EnvelopeRules.Reasons.NonceReused);
    }

    [Fact]
    public async Task AShareEditorCannotIntroduceAnEnvelopeTheRulesRefuseButAWellFormedUpdateStillCommits()
    {
        var h = await new Harness().CreatedAsync();
        h.ShareWithContributor("contributor", false, h.SealedItem);

        // A fresh, otherwise well-formed envelope under version 2 is refused by the central rules before the share classifier runs.
        var v2 = Guid.CreateVersion7();
        var refused = await Assert.ThrowsAsync<AuthorityException>(() => h.PrepareAsync(h.Contributor, v2, h.Stage(Change(h.Rows(), h.SealedItem, _ => Sealed(h.Org, h.Record, h.SealedField, h.SealedItem, 90, envelope: 2)), v2), withCurrent: true));
        Assert.Equal("ENVELOPE_INVALID: " + EnvelopeRules.Reasons.VersionUnsupported, refused.Message);

        var foreign = Guid.CreateVersion7();
        var otherOrganization = Change(h.Rows(), h.SealedItem, _ => Sealed(h.Org, h.Record, h.SealedField, h.SealedItem, 90).With("encryption_key_reference", RowValue.Text($"vault-transit:{Guid.CreateVersion7():D}:field-value:1")));
        Assert.Contains(EnvelopeRules.Reasons.KeyReferenceOrganization, (await Assert.ThrowsAsync<AuthorityException>(() => h.PrepareAsync(h.Contributor, foreign, h.Stage(otherOrganization, foreign), withCurrent: true))).Message, StringComparison.Ordinal);
        Assert.DoesNotContain(h.Store.Operations, o => o.ContentChangeOperationId == v2 || o.ContentChangeOperationId == foreign);

        // Positive control: a fresh well-formed v1 envelope (new nonce and wrapped key) is still accepted for the shared item.
        var revision = await h.WriteAsync(h.Contributor, Change(h.Rows(), h.SealedItem, _ => Sealed(h.Org, h.Record, h.SealedField, h.SealedItem, 90)), withCurrent: true);
        Assert.Equal(2, revision.CommittedContentRevision);
    }

    /// <summary>
    /// An operation prepared by an Authority build before E3-M2a (simulated: candidate leaves signed with the organization's key
    /// exactly as that build's Prepare did, without envelope rules) cannot commit once the rules exist: the verified staged
    /// bytes are checked again, the operation is aborted, and nothing is accepted.
    /// </summary>
    [Fact]
    public async Task ACommitOfAnOperationPreparedWithoutTheRulesIsRefusedAndTheOperationAborted()
    {
        var h = await new Harness().CreatedAsync();
        var head = (h.Head.CommittedContentRevision, h.Head.CommittedOperationId, Root: h.Head.CommittedManifestRoot.ToArray());
        var operation = Guid.CreateVersion7();
        var candidate = h.Stage(WithMalformed(h, "field nonce"), operation);
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
            OperationIdempotencyKey = "pre-m2a-" + operation.ToString("N"),
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

        var staged = signed.Select(s => new StagedContentItem(s.Item.Kind, s.Item.ItemId, s.Item.Base64, Convert.ToBase64String(s.Signature))).ToList();
        var ex = await Assert.ThrowsAsync<AuthorityException>(() => h.Service.CommitAsync(Harness.Actor(h.Owner), operation, new CommitContentChangeRequest(h.Org, h.Record, staged), Guid.CreateVersion7(), CancellationToken.None));

        Assert.Equal("ENVELOPE_INVALID: " + EnvelopeRules.Reasons.NonceLength, ex.Message);
        var aborted = h.Store.Operations.Single(o => o.ContentChangeOperationId == operation);
        Assert.Equal(ContentOperationStates.Aborted, aborted.OperationStateName);
        Assert.Equal("ENVELOPE_INVALID_AT_COMMIT", aborted.OperationQuarantineReasonCode);
        Assert.Equal(head.CommittedContentRevision, h.Head.CommittedContentRevision);
        Assert.Equal(head.CommittedOperationId, h.Head.CommittedOperationId);
        Assert.Equal(head.Root, h.Head.CommittedManifestRoot);
        Assert.DoesNotContain(h.Store.Commits, c => c.CommittedOperationId == operation);
    }

    // ---- E3-M2a remediation: data-key and nonce reuse against the verified current revision ----

    /// <summary>Prepare must refuse with <paramref name="reason"/> before anything is signed, stored or committed.</summary>
    private static async Task RefusedBeforeSigningAsync(Harness h, Guid actor, List<Row> rows, string reason)
    {
        var operation = Guid.CreateVersion7();
        var head = (h.Head.CommittedContentRevision, h.Head.CommittedOperationId);
        var signaturesBefore = h.Signer.SignatureCount;
        var leavesBefore = h.Store.CandidateLeaves.Count;
        var ex = await Assert.ThrowsAsync<AuthorityException>(() => h.PrepareAsync(actor, operation, h.Stage(rows, operation), withCurrent: true));

        Assert.Equal("ENVELOPE_INVALID: " + reason, ex.Message);
        Assert.Equal(signaturesBefore, h.Signer.SignatureCount);
        Assert.Equal(leavesBefore, h.Store.CandidateLeaves.Count);
        Assert.DoesNotContain(h.Store.Operations, o => o.ContentChangeOperationId == operation);
        Assert.Equal(head, (h.Head.CommittedContentRevision, h.Head.CommittedOperationId));
    }

    /// <summary>The sealed item's current envelope (seed 10 in <see cref="Harness.Rows"/>) with only the named columns changed.</summary>
    private static Row ResealedWith(Harness h, Guid item, Guid field, params (string Column, byte Fill)[] changes)
    {
        var row = Sealed(h.Org, h.Record, field, item, 10);
        foreach (var (column, fill) in changes)
        {
            row = row.With(column, RowValue.Bytes(Fill(column == "encryption_nonce_bytes" ? 12 : 24, fill)));
        }

        return row;
    }

    [Fact]
    public async Task OwnerByteIdenticalCarryForwardAndFreshEncryptionAreBothAccepted()
    {
        var h = await new Harness().CreatedAsync();
        Assert.Equal(2, (await h.WriteAsync(h.Owner, h.Rows(), withCurrent: true)).CommittedContentRevision); // the sealed item carried unchanged
        Assert.Equal(3, (await h.WriteAsync(h.Owner, h.Rows(), withCurrent: true)).CommittedContentRevision); // and again
        var fresh = Change(h.Rows(), h.SealedItem, _ => Sealed(h.Org, h.Record, h.SealedField, h.SealedItem, 70)); // new key, nonce, ciphertext
        Assert.Equal(4, (await h.WriteAsync(h.Owner, fresh, withCurrent: true)).CommittedContentRevision);
    }

    [Fact]
    public async Task OwnerChangedCiphertextUnderTheCurrentKeyAndNonceIsRefusedBeforeSigning()
    {
        var h = await new Harness().CreatedAsync();
        var reuse = Change(h.Rows(), h.SealedItem, _ => ResealedWith(h, h.SealedItem, h.SealedField, ("encrypted_value_bytes", 99)));
        await RefusedBeforeSigningAsync(h, h.Owner, reuse, EnvelopeRules.Reasons.NonceReused);
    }

    [Fact]
    public async Task OwnerReuseOfTheCurrentKeyUnderANewNonceIsRefusedBeforeSigning()
    {
        var h = await new Harness().CreatedAsync();
        var reuse = Change(h.Rows(), h.SealedItem, _ => ResealedWith(h, h.SealedItem, h.SealedField, ("encryption_nonce_bytes", 98), ("encrypted_value_bytes", 97)));
        await RefusedBeforeSigningAsync(h, h.Owner, reuse, EnvelopeRules.Reasons.WrappedKeyReused);
    }

    [Fact]
    public async Task AnotherItemCannotTakeTheCurrentKeyEvenWhenTheOriginalItemIsRemovedOrReplaced()
    {
        var h = await new Harness().CreatedAsync();
        var newItem = Guid.CreateVersion7();
        var newField = Guid.CreateVersion7();
        var withoutSealed = h.Rows().Where(r => r.ItemId != h.SealedItem).ToList();

        // The original item removed, its key and nonce moved to a new item (deletion does not free a key).
        await RefusedBeforeSigningAsync(h, h.Owner, [.. withoutSealed, ResealedWith(h, newItem, newField, ("encrypted_value_bytes", 96))], EnvelopeRules.Reasons.NonceReused);

        // The original item replaced by a fresh envelope, the old key reused under a new nonce by another item.
        var replaced = Change(h.Rows(), h.SealedItem, _ => Sealed(h.Org, h.Record, h.SealedField, h.SealedItem, 70));
        await RefusedBeforeSigningAsync(h, h.Owner, [.. replaced, ResealedWith(h, newItem, newField, ("encryption_nonce_bytes", 95), ("encrypted_value_bytes", 94))], EnvelopeRules.Reasons.WrappedKeyReused);
    }

    [Fact]
    public async Task ARecordCreatedWithTwoValuesUnderOneWrappedKeyIsRefusedBeforeSigning()
    {
        var h = new Harness();
        var twin = Sealed(h.Org, h.Record, h.OtherField, Guid.CreateVersion7(), 10).With("encryption_nonce_bytes", RowValue.Bytes(Fill(12, 93)));
        await RefusedBeforeSigningAsync(h, h.Owner, [.. h.Rows().Where(r => r.ItemId != h.Other), twin], EnvelopeRules.Reasons.WrappedKeyReused);
        Assert.Equal(0, h.Signer.SignatureCount);
    }

    [Fact]
    public async Task AShareEditorCannotReuseTheCurrentKeyOfItsItemOrOfAnotherItem()
    {
        var h = new Harness();
        var secondField = Guid.CreateVersion7();
        var secondItem = Guid.CreateVersion7();
        List<Row> Rows() => [.. h.Rows(), Sealed(h.Org, h.Record, secondField, secondItem, 30)];
        await h.WriteAsync(h.Owner, Rows(), withCurrent: true);
        h.ShareWithContributor("contributor", false, h.SealedItem);

        // Its own item: the current key under a new nonce (formerly SHARE_ENVELOPE_REUSED; now the central rule, earlier).
        await RefusedBeforeSigningAsync(h, h.Contributor, Change(Rows(), h.SealedItem, _ => ResealedWith(h, h.SealedItem, h.SealedField, ("encryption_nonce_bytes", 92), ("encrypted_value_bytes", 91))), EnvelopeRules.Reasons.WrappedKeyReused);

        // Another item's current key and nonce for the shared item's new ciphertext (formerly not detected for a share edit). The
        // other item is left out so that only the cross-revision rule can see the reuse (the share classifier, which would also
        // refuse the removal, runs later).
        var stolen = Sealed(h.Org, h.Record, h.SealedField, h.SealedItem, 30).With("encrypted_value_bytes", RowValue.Bytes(Fill(24, 90)));
        await RefusedBeforeSigningAsync(h, h.Contributor, Change(h.Rows(), h.SealedItem, _ => stolen), EnvelopeRules.Reasons.NonceReused);

        // Positive control: a fresh envelope for the shared item commits.
        Assert.Equal(2, (await h.WriteAsync(h.Contributor, Change(Rows(), h.SealedItem, _ => Sealed(h.Org, h.Record, h.SealedField, h.SealedItem, 70)), withCurrent: true)).CommittedContentRevision);
    }

    /// <summary>
    /// A candidate checked against an older head cannot be accepted on a newer one: B introduces a key on the newer head, A
    /// (prepared earlier with the same key) is refused at commit by the head compare-and-set, and A re-prepared on the new head
    /// is refused by the reuse rule.
    /// </summary>
    [Fact]
    public async Task AStaleHeadCannotBypassTheReuseRule()
    {
        var h = await new Harness().CreatedAsync();
        var itemA = Guid.CreateVersion7();
        var itemB = Guid.CreateVersion7();
        var fieldA = Guid.CreateVersion7();
        var fieldB = Guid.CreateVersion7();

        var opA = Guid.CreateVersion7();
        var candidateA = h.Stage([.. h.Rows(), Sealed(h.Org, h.Record, fieldA, itemA, 50)], opA);
        var preparedA = await h.PrepareAsync(h.Owner, opA, candidateA, withCurrent: true);

        var opB = Guid.CreateVersion7();
        var candidateB = h.Stage([.. h.Rows(), Sealed(h.Org, h.Record, fieldB, itemB, 50).With("encrypted_value_bytes", RowValue.Bytes(Fill(24, 89)))], opB);
        await h.CommitAsync(h.Owner, await h.PrepareAsync(h.Owner, opB, candidateB, withCurrent: true), candidateB);
        Assert.Equal(opB, h.Head.CommittedOperationId);

        var stale = await Assert.ThrowsAsync<AuthorityException>(() => h.CommitAsync(h.Owner, preparedA, candidateA));
        Assert.Equal(AuthorityErrorCodes.ContentRevisionConflict, stale.ErrorCode);
        Assert.Equal(ContentOperationStates.Aborted, h.Store.Operations.Single(o => o.ContentChangeOperationId == opA).OperationStateName);
        Assert.Equal(opB, h.Head.CommittedOperationId);

        // Re-prepared on the new head, with B's item removed so only the cross-revision rule can see the reuse.
        await RefusedBeforeSigningAsync(h, h.Owner, [.. h.Rows(), Sealed(h.Org, h.Record, fieldA, itemA, 50)], EnvelopeRules.Reasons.NonceReused);
    }
}
