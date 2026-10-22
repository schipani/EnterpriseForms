using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Content;
using EnterpriseForms.Integrity.Canonical;
using EnterpriseForms.Integrity.Merkle;
using Xunit;
using static AuthorizationAuthority.Tests.ContributorWriteFixtures;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// Phase C5 column-class model (proposal §5–§8; D3, D4, D8, D14, D15). The current snapshot is staged under operation A
/// at revision 1, the candidate under operation B at revision 2 with fresh storage keys; the share editor may change only
/// the shared item <see cref="Shared"/>. Every substitution, binding and coverage case fails closed.
/// </summary>
public sealed class ContentDeltaRulesTests
{
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly Guid Record = Guid.CreateVersion7();
    private static readonly Guid OpA = Guid.CreateVersion7();
    private static readonly Guid OpB = Guid.CreateVersion7();
    private static readonly Guid SharedField = Guid.CreateVersion7();
    private static readonly Guid OtherField = Guid.CreateVersion7();
    private static readonly Guid SealedField = Guid.CreateVersion7();
    private static readonly Guid ChoiceField = Guid.CreateVersion7();
    private static readonly Guid Shared = Guid.CreateVersion7();
    private static readonly Guid Other = Guid.CreateVersion7();
    private static readonly Guid SealedItem = Guid.CreateVersion7();
    private static readonly Guid Choice = Guid.CreateVersion7();
    private static readonly Guid Occurrence = Guid.CreateVersion7();
    private static readonly Guid Document = Guid.CreateVersion7();
    private static readonly Guid OptionA = Guid.CreateVersion7();
    private static readonly Guid OptionB = Guid.CreateVersion7();

    private static List<Row> BaseRows() =>
    [
        Header(Org, Record),
        Text(Org, Record, SharedField, Shared, "before"),
        Text(Org, Record, OtherField, Other, "secret"),
        Sealed(Org, Record, SealedField, SealedItem, 10),
        Option(Org, Record, ChoiceField, Choice, OptionA),
        Group(Org, Record, Occurrence, 0),
        Attachment(Org, Record, Document),
    ];

    private static List<Staged> Current(IEnumerable<Row>? rows = null) => (rows ?? BaseRows()).Select(r => Encode(r, Org, OpA, 1)).ToList();

    private static List<Staged> Candidate(IEnumerable<Row> rows) => rows.Select(r => Encode(r, Org, OpB, 2)).ToList();

    private static ContentDeltaRules.ShareScope Scope(params Guid[] editable) => new(Org, Record, 1, OpA, 2, OpB, editable.ToHashSet());

    private static Dictionary<string, string> Classify(List<Staged> current, List<Staged> candidate, params Guid[] editable) =>
        ContentDeltaRules.ClassifyShareScoped(current.Select(c => c.Decoded).ToList(), candidate.Select(c => c.Decoded).ToList(), Scope(editable.Length == 0 ? [Shared] : editable));

    private static List<Row> Replace(List<Row> rows, Guid itemId, Func<Row, Row> change) => rows.Select(r => r.ItemId == itemId ? change(r) : r).ToList();

    private static string KeyHex(ManifestItemKind kind, Guid item) => Convert.ToHexStringLower(MerkleManifestV1.ItemKey((MerkleItemKind)(byte)kind, item));

    private static AuthorityException Refused(Func<object> act) => Assert.Throws<AuthorityException>(act);

    [Fact]
    public void ASharedValueChangeIsTheOnlyUpdateAndEveryOtherItemIsUnchanged()
    {
        var rows = BaseRows();
        var mutations = Classify(Current(rows), Candidate(Replace(rows, Shared, r => r.With("string_value", RowValue.Text("after")))));

        Assert.Equal(ContentMutationKinds.Update, mutations[KeyHex(ManifestItemKind.FieldValue, Shared)]);
        Assert.Equal(rows.Count, mutations.Count);
        Assert.Equal(rows.Count - 1, mutations.Values.Count(m => m == ContentMutationKinds.Unchanged));
    }

    [Fact]
    public void PreservedCiphertextAndArtifactsAreUnchangedWhileTheirOperationBindingAndStorageKeysAreNew()
    {
        // D15: the sealed envelope and the attachment row are carried byte-for-byte; only B (operation, revision) and S (surrogate key) differ — and both are checked.
        var rows = BaseRows();
        var current = Current(rows);
        var candidate = Candidate(Replace(rows, Shared, r => r.With("string_value", RowValue.Text("after"))));
        var sealedBefore = current.Single(s => s.ItemId == SealedItem).Decoded.Document;
        var sealedAfter = candidate.Single(s => s.ItemId == SealedItem).Decoded.Document;
        Assert.NotEqual(sealedBefore.Header.PrimaryKeyId, sealedAfter.Header.PrimaryKeyId);
        Assert.True(sealedBefore.Column("encrypted_value_bytes")!.FramedValueEquals(sealedAfter.Column("encrypted_value_bytes")!));
        Assert.False(sealedBefore.Column("content_change_operation_id")!.FramedValueEquals(sealedAfter.Column("content_change_operation_id")!));

        var mutations = Classify(current, candidate);
        Assert.Equal(ContentMutationKinds.Unchanged, mutations[KeyHex(ManifestItemKind.FieldValue, SealedItem)]);
        Assert.Equal(ContentMutationKinds.Unchanged, mutations[KeyHex(ManifestItemKind.Attachment, Document)]);
    }

    [Fact]
    public void ReEncryptingAnOutOfScopeValueIsAChangeAndIsRefused()
    {
        var rows = BaseRows();
        var candidate = Replace(Replace(rows, Shared, r => r.With("string_value", RowValue.Text("after"))), SealedItem, _ => Sealed(Org, Record, SealedField, SealedItem, 40));
        Assert.Contains("SHARE_ITEM_NOT_IN_SCOPE", Refused(() => Classify(Current(rows), Candidate(candidate))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUpdateOfAnUnsharedItemIsRefused()
    {
        var rows = BaseRows();
        var ex = Refused(() => Classify(Current(rows), Candidate(Replace(rows, Other, r => r.With("string_value", RowValue.Text("leaked"))))));
        Assert.Equal(AuthorityErrorCodes.Forbidden, ex.ErrorCode);
        Assert.Contains("SHARE_ITEM_NOT_IN_SCOPE", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Other.ToString("D"), ex.Message, StringComparison.Ordinal); // reason codes only, never item ids
    }

    [Fact]
    public void SubstitutionNewItemIdAddedOrRemovedIsRefused()
    {
        var rows = BaseRows();
        var renamed = Replace(rows, Shared, _ => Text(Org, Record, SharedField, Guid.CreateVersion7(), "after"));
        Assert.Contains("SHARE_ITEM_ADDED_OR_REMOVED", Refused(() => Classify(Current(rows), Candidate(renamed))).Message, StringComparison.Ordinal);
        var removed = rows.Where(r => r.ItemId != Other).ToList();
        Assert.Contains("SHARE_ITEM_ADDED_OR_REMOVED", Refused(() => Classify(Current(rows), Candidate(removed))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SubstitutionItemIdColumnDiffersFromTheManifestItemIdIsRefused()
    {
        var rows = BaseRows();
        var forged = Replace(rows, Shared, r => r.With("field_value_item_id", RowValue.Uuid(Other)));
        Assert.Contains("IDENTITY_BINDING", Refused(() => Classify(Current(rows), Candidate(forged))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SubstitutionSwappingTheRowsOfASharedAndAnUnsharedItemIsRefused()
    {
        var rows = BaseRows();
        var shared = rows.Single(r => r.ItemId == Shared);
        var other = rows.Single(r => r.ItemId == Other);
        var swapped = rows.Select(r => r.ItemId == Shared ? other with { ItemId = Shared } : r.ItemId == Other ? shared with { ItemId = Other } : r).ToList();
        Assert.Contains("IDENTITY_BINDING", Refused(() => Classify(Current(rows), Candidate(swapped))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SubstitutionWritingIntoAnotherFieldThroughASharedItemIsRefused()
    {
        var rows = BaseRows();
        var moved = Replace(rows, Shared, r => r.With("field_definition_id", RowValue.Uuid(OtherField)).With("string_value", RowValue.Text("into another field")));
        Assert.Contains("SHARE_ITEM_IDENTITY_CHANGED", Refused(() => Classify(Current(rows), Candidate(moved))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SubstitutionMovingAValueIntoARepeatingEntryIsRefused()
    {
        var rows = BaseRows();
        var moved = Replace(rows, Shared, r => r.With("form_record_group_id", RowValue.Uuid(Occurrence)));
        Assert.Contains("SHARE_ITEM_IDENTITY_CHANGED", Refused(() => Classify(Current(rows), Candidate(moved))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SubstitutionChangingTheItemKindOrStorageTypeIsRefused()
    {
        var rows = BaseRows();
        var asSelection = Replace(rows, Shared, _ => Option(Org, Record, SharedField, Shared, OptionB));
        Assert.Contains("SHARE_ITEM_ADDED_OR_REMOVED", Refused(() => Classify(Current(rows), Candidate(asSelection))).Message, StringComparison.Ordinal);
        var asInteger = Replace(rows, Shared, _ => Integer(Org, Record, SharedField, Shared, 7));
        Assert.Contains("SHARE_ITEM_FRAMING_CHANGED", Refused(() => Classify(Current(rows), Candidate(asInteger))).Message, StringComparison.Ordinal);
        var asSealed = Replace(rows, Shared, _ => Sealed(Org, Record, SharedField, Shared, 50));
        Assert.Contains("SHARE_ITEM_FRAMING_CHANGED", Refused(() => Classify(Current(rows), Candidate(asSealed))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SubstitutionChangingTheEncryptedLogicalTypeIsRefused()
    {
        var rows = BaseRows();
        var retyped = Replace(rows, SealedItem, _ => Sealed(Org, Record, SealedField, SealedItem, 60, logicalType: "Integer"));
        Assert.Contains("SHARE_ITEM_IDENTITY_CHANGED", Refused(() => Classify(Current(rows), Candidate(retyped), SealedItem)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HeaderGroupAndAttachmentChangesAreRefused()
    {
        var rows = BaseRows();
        Assert.Contains("SHARE_ITEM_NOT_UPDATABLE", Refused(() => Classify(Current(rows), Candidate(Replace(rows, Record, r => r.With("record_status", RowValue.Text("Submitted")))))).Message, StringComparison.Ordinal);
        Assert.Contains("SHARE_ITEM_IDENTITY_CHANGED", Refused(() => Classify(Current(rows), Candidate(Replace(rows, Record, r => r.With("parent_form_record_id", RowValue.Uuid(Guid.CreateVersion7())))))).Message, StringComparison.Ordinal);
        Assert.Contains("SHARE_ITEM_NOT_UPDATABLE", Refused(() => Classify(Current(rows), Candidate(Replace(rows, Occurrence, r => r.With("group_sort_order", RowValue.Int64(5)))))).Message, StringComparison.Ordinal);
        Assert.Contains("SHARE_ITEM_IDENTITY_CHANGED", Refused(() => Classify(Current(rows), Candidate(Replace(rows, Occurrence, r => r.With("group_definition_id", RowValue.Uuid(Guid.CreateVersion7())))))).Message, StringComparison.Ordinal);
        Assert.Contains("SHARE_ITEM_NOT_UPDATABLE", Refused(() => Classify(Current(rows), Candidate(Replace(rows, Document, r => r.With("storage_object_key", RowValue.Text("objects/forged")))))).Message, StringComparison.Ordinal);
        Assert.Contains("SHARE_ITEM_NOT_UPDATABLE", Refused(() => Classify(Current(rows), Candidate(Replace(rows, Document, r => r.With("scan_verdict", RowValue.Text("Infected")))))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AReusedOrDuplicatedStorageKeyIsRefused()
    {
        var rows = BaseRows();
        var current = Current(rows);
        var reusedKey = current.Single(s => s.ItemId == Other).StorageKey;
        var candidate = rows.Select(r => r.ItemId == Shared ? Encode(r.With("string_value", RowValue.Text("after")), Org, OpB, 2)
            : r.ItemId == Other ? Encode(r, Org, OpB, 2, storageKey: reusedKey) : Encode(r, Org, OpB, 2)).ToList();
        Assert.Contains("SHARE_ITEM_STORAGE_KEY_REUSED", Refused(() => Classify(current, candidate)).Message, StringComparison.Ordinal);

        var duplicate = Guid.CreateVersion7();
        var twice = rows.Select(r => r.ItemId == Shared ? Encode(r.With("string_value", RowValue.Text("after")), Org, OpB, 2, storageKey: duplicate)
            : r.ItemId == Other ? Encode(r, Org, OpB, 2, storageKey: duplicate) : Encode(r, Org, OpB, 2)).ToList();
        Assert.Contains("SHARE_ITEM_STORAGE_KEY_REUSED", Refused(() => Classify(current, twice)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CandidateOperationBindingMustBeTheNewOperationAndRevision()
    {
        var rows = BaseRows();
        var changed = Replace(rows, Shared, r => r.With("string_value", RowValue.Text("after")));
        var staleOperation = changed.Select(r => Encode(r, Org, OpA, 2)).ToList(); // stale operation id reused
        var ex = Refused(() => Classify(Current(rows), staleOperation));
        Assert.Equal(AuthorityErrorCodes.CandidateMismatch, ex.ErrorCode);
        Assert.Contains("CANDIDATE_OPERATION_BINDING", ex.Message, StringComparison.Ordinal);
        var staleRevision = changed.Select(r => Encode(r, Org, OpB, 1)).ToList();
        Assert.Contains("CANDIDATE_OPERATION_BINDING", Refused(() => Classify(Current(rows), staleRevision)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyCurrentRowsWithAnotherOperationBindingFailClosed()
    {
        // U7 (C-34 condition 6): no compatibility bypass.
        var rows = BaseRows();
        var legacy = rows.Select(r => r.ItemId == Other ? Encode(r, Org, Guid.CreateVersion7(), 1) : Encode(r, Org, OpA, 1)).ToList();
        var ex = Refused(() => Classify(legacy, Candidate(Replace(rows, Shared, r => r.With("string_value", RowValue.Text("after"))))));
        Assert.Equal(AuthorityErrorCodes.CurrentSnapshotMismatch, ex.ErrorCode);
        Assert.Contains("CURRENT_SNAPSHOT_LEGACY_BINDING", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EncryptedUpdatesNeedAWhollyNewAllowListedEnvelope()
    {
        var rows = BaseRows();
        var fresh = Replace(rows, SealedItem, _ => Sealed(Org, Record, SealedField, SealedItem, 80));
        Assert.Equal(ContentMutationKinds.Update, Classify(Current(rows), Candidate(fresh), SealedItem)[KeyHex(ManifestItemKind.FieldValue, SealedItem)]);

        var sameNonce = Replace(rows, SealedItem, r => r.With("encrypted_value_bytes", RowValue.Bytes(Fill(24, 99))));
        Assert.Contains("SHARE_ENVELOPE_REUSED", Refused(() => Classify(Current(rows), Candidate(sameNonce), SealedItem)).Message, StringComparison.Ordinal);
        var weakAlgorithm = Replace(rows, SealedItem, _ => Sealed(Org, Record, SealedField, SealedItem, 90, algorithm: "AES-128-CBC"));
        Assert.Contains("SHARE_ENVELOPE_NOT_ALLOWED", Refused(() => Classify(Current(rows), Candidate(weakAlgorithm), SealedItem)).Message, StringComparison.Ordinal);
        var unknownEnvelope = Replace(rows, SealedItem, _ => Sealed(Org, Record, SealedField, SealedItem, 100, envelope: 9));
        Assert.Contains("SHARE_ENVELOPE_NOT_ALLOWED", Refused(() => Classify(Current(rows), Candidate(unknownEnvelope), SealedItem)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SingleSelectionChangesAreUpdatesButMultiSelectionsAreRefused()
    {
        var rows = BaseRows();
        var single = Replace(rows, Choice, r => r.With("field_option_id", RowValue.Uuid(OptionB)));
        Assert.Equal(ContentMutationKinds.Update, Classify(Current(rows), Candidate(single), Choice)[KeyHex(ManifestItemKind.FieldSelection, Choice)]);

        // D3 at the trusted boundary: a field holding two selection items is a multi-selection; swapping one is refused even if shared.
        var second = Guid.CreateVersion7();
        var multi = rows.Append(Option(Org, Record, ChoiceField, second, OptionB)).ToList();
        var swapped = Replace(multi, Choice, r => r.With("field_option_id", RowValue.Uuid(Guid.CreateVersion7())));
        Assert.Contains("SHARE_ITEM_NOT_SINGLE_VALUE", Refused(() => Classify(Current(multi), Candidate(swapped), Choice, second)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValuesInsideRepeatingEntriesAreNotShareEditable()
    {
        var inEntry = Guid.CreateVersion7();
        var rows = BaseRows().Append(Text(Org, Record, Guid.CreateVersion7(), inEntry, "entry value", Occurrence)).ToList();
        var changed = Replace(rows, inEntry, r => r.With("string_value", RowValue.Text("changed")));
        Assert.Contains("SHARE_ITEM_IN_REPEATING_SECTION", Refused(() => Classify(Current(rows), Candidate(changed), inEntry)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACandidateWithoutAnyChangeIsRefused()
    {
        var rows = BaseRows();
        var ex = Refused(() => Classify(Current(rows), Candidate(rows)));
        Assert.Equal(422, ex.HttpStatusCode);
        Assert.Contains("NO_CHANGE", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownSchemasExtraColumnsAndWrongTenantsOrRecordsFailClosed()
    {
        var rows = BaseRows();
        var unknownKind = new RowSchemaDefinition("field_money_values", 1, StringValues.Columns);
        var unknown = Replace(rows, Shared, r => r with { Schema = unknownKind });
        Assert.Contains("UNSUPPORTED_SIGNED_SCHEMA", Refused(() => Classify(Current(rows), Candidate(unknown))).Message, StringComparison.Ordinal);

        var extraColumnSchema = new RowSchemaDefinition("field_string_values", 1, StringValues.Columns.Append(new RowSignedColumn("zz_extra_column", RowColumnType.Text, true)).ToList());
        var extra = Replace(rows, Shared, r => (r with { Schema = extraColumnSchema }).With("zz_extra_column", null));
        Assert.Contains("UNSUPPORTED_SIGNED_SCHEMA", Refused(() => Classify(Current(rows), Candidate(extra))).Message, StringComparison.Ordinal);

        var otherTenant = Replace(rows, Shared, r => r.With("tenant_id", RowValue.Uuid(Guid.CreateVersion7())).With("string_value", RowValue.Text("x")));
        Assert.Contains("IDENTITY_BINDING", Refused(() => Classify(Current(rows), Candidate(otherTenant))).Message, StringComparison.Ordinal);

        var otherRecord = Replace(rows, Shared, r => r.With("form_record_id", RowValue.Uuid(Guid.CreateVersion7())).With("string_value", RowValue.Text("x")));
        Assert.Contains("IDENTITY_BINDING", Refused(() => Classify(Current(rows), Candidate(otherRecord))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IdentityBindingAcceptsWellFormedOwnerRowsAndRejectsForgedOnes()
    {
        foreach (var staged in Current())
        {
            var decoded = staged.Decoded;
            ContentDeltaRules.EnforceIdentityBinding(decoded.Kind, decoded.ItemId, decoded.Document, Org, Record); // no throw
        }

        var item = Encode(Text(Org, Record, SharedField, Shared, "x"), Org, OpA, 1).Decoded;
        Assert.Throws<AuthorityException>(() => ContentDeltaRules.EnforceIdentityBinding(ManifestItemKind.FieldValue, Other, item.Document, Org, Record));
        Assert.Throws<AuthorityException>(() => ContentDeltaRules.EnforceIdentityBinding(ManifestItemKind.FieldValue, Shared, item.Document, Guid.CreateVersion7(), Record));
        Assert.Throws<AuthorityException>(() => ContentDeltaRules.EnforceIdentityBinding(ManifestItemKind.FieldValue, Shared, item.Document, Org, Guid.CreateVersion7()));
        Assert.Throws<AuthorityException>(() => ContentDeltaRules.EnforceIdentityBinding(ManifestItemKind.FieldSelection, Shared, item.Document, Org, Record));
        var group = Encode(Group(Org, Record, Occurrence, 0), Org, OpA, 1).Decoded;
        Assert.Throws<AuthorityException>(() => ContentDeltaRules.EnforceIdentityBinding(ManifestItemKind.RepeatedGroup, Guid.CreateVersion7(), group.Document, Org, Record));
        var attachment = Encode(Attachment(Org, Record, Document), Org, OpA, 1).Decoded;
        Assert.Throws<AuthorityException>(() => ContentDeltaRules.EnforceIdentityBinding(ManifestItemKind.Attachment, Guid.CreateVersion7(), attachment.Document, Org, Record));
    }

    [Fact]
    public void EveryRuleClassifiesEveryColumnAndHeaderV2IsSupported()
    {
        foreach (var rule in ContentDeltaRules.Rules)
        {
            Assert.Contains(ContentDeltaRules.OperationColumn, rule.Columns.Keys);
            Assert.Equal(SignedColumnClass.OperationBinding, rule.Columns[ContentDeltaRules.OperationColumn]);
            Assert.Equal(SignedColumnClass.Identity, rule.Columns[ContentDeltaRules.TenantColumn]);
            Assert.Equal(rule.PrimaryKey == PrimaryKeyClass.StorageKey, rule.ItemIdColumn is not null);
        }

        var v2 = new Row(ManifestItemKind.RecordHeader, Record, HeaderV2, new Dictionary<string, RowValue?>(StringComparer.Ordinal)
        {
            ["created_by_user_id"] = RowValue.Uuid(Guid.CreateVersion7()), ["created_form_version_id"] = RowValue.Uuid(Guid.CreateVersion7()), ["form_definition_id"] = RowValue.Uuid(Guid.CreateVersion7()),
            ["record_status"] = RowValue.Text("Draft"), ["submitted_on"] = null, ["tenant_id"] = RowValue.Uuid(Org),
        }, IdentityPrimaryKey: true);
        var rows = BaseRows().Select(r => r.ItemId == Record ? v2 : r).ToList();
        var mutations = Classify(Current(rows), Candidate(Replace(rows, Shared, r => r.With("string_value", RowValue.Text("after")))));
        Assert.Equal(ContentMutationKinds.Unchanged, mutations[KeyHex(ManifestItemKind.RecordHeader, Record)]); // v2 preserved, not upgraded
    }

    // ---- D4: binding the supplied current snapshot to the accepted state ----

    private static (ContentDeltaRules.AcceptedState Accepted, List<StagedContentItem> Items) AcceptedSnapshot()
    {
        var current = Current();
        var signatures = current.ToDictionary(s => s.ItemId, _ => Fill(32, 1).Select((_, i) => (byte)Random.Shared.Next(256)).ToArray());
        var manifest = MerkleManifestV1.Build(current.Select(s => new MerkleLeafInput((MerkleItemKind)(byte)s.Kind, s.ItemId, signatures[s.ItemId], s.Digest, 1, OpA)));
        var leaves = current.Select(s => (s.Key, (byte)s.Kind, s.ItemId, s.Digest, signatures[s.ItemId])).ToList();
        var accepted = new ContentDeltaRules.AcceptedState(1, OpA, manifest.MerkleRoot, manifest.LeafIndexDigest, (long)manifest.LeafCount, 1, leaves);
        var items = current.Select(s => new StagedContentItem(s.Kind, s.ItemId, s.Base64, Convert.ToBase64String(signatures[s.ItemId]))).ToList();
        return (accepted, items);
    }

    [Fact]
    public void TheExactAcceptedSnapshotBinds()
    {
        var (accepted, items) = AcceptedSnapshot();
        var (failure, decoded) = ContentDeltaRules.BindCurrentSnapshot(items, accepted, Org);
        Assert.Null(failure);
        Assert.Equal(items.Count, decoded.Count);
    }

    [Fact]
    public void IncompleteExtraDuplicatedOrAlteredSnapshotsDoNotBind()
    {
        var (accepted, items) = AcceptedSnapshot();
        Assert.Equal("CURRENT_SNAPSHOT_COVERAGE", ContentDeltaRules.BindCurrentSnapshot(items.Skip(1).ToList(), accepted, Org).FailureCode);
        Assert.Equal("CURRENT_SNAPSHOT_COVERAGE", ContentDeltaRules.BindCurrentSnapshot(items.Append(items[0] with { ItemId = Guid.CreateVersion7() }).ToList(), accepted, Org).FailureCode);
        Assert.Equal("CURRENT_SNAPSHOT_COVERAGE", ContentDeltaRules.BindCurrentSnapshot(items.Take(items.Count - 1).Append(items[0]).ToList(), accepted, Org).FailureCode);

        // TAM-01: one byte of an out-of-scope item changed in the untrusted store.
        var bytes = Convert.FromBase64String(items[2].CanonicalBytesBase64);
        bytes[^1] ^= 0x01;
        var altered = items.Select((s, i) => i == 2 ? s with { CanonicalBytesBase64 = Convert.ToBase64String(bytes) } : s).ToList();
        Assert.Equal("CURRENT_SNAPSHOT_DIGEST", ContentDeltaRules.BindCurrentSnapshot(altered, accepted, Org).FailureCode);

        // RPL-01: an older valid row (different value, same item) presented as current.
        var older = Encode(Text(Org, Record, OtherField, Other, "older"), Org, OpA, 1);
        var replayed = items.Select(s => s.ItemId == Other ? s with { CanonicalBytesBase64 = older.Base64 } : s).ToList();
        Assert.Equal("CURRENT_SNAPSHOT_DIGEST", ContentDeltaRules.BindCurrentSnapshot(replayed, accepted, Org).FailureCode);

        // A stored leaf index that does not reproduce the accepted root/leaf-index digest is refused (accepted-state coherence).
        var tamperedLeaves = accepted.Leaves.Select((l, i) => i == 0 ? (l.Key, l.KindCode, l.ItemId, l.Digest, Fill(32, 3)) : l).ToList();
        Assert.Equal("CURRENT_SNAPSHOT_ROOT", ContentDeltaRules.BindCurrentSnapshot(items, accepted with { Leaves = tamperedLeaves }, Org).FailureCode);
        Assert.Equal("CURRENT_SNAPSHOT_COVERAGE", ContentDeltaRules.BindCurrentSnapshot(items, accepted with { LeafCount = accepted.LeafCount + 1 }, Org).FailureCode);
        Assert.Equal("CURRENT_SNAPSHOT_BINDING", ContentDeltaRules.BindCurrentSnapshot(items, accepted, Guid.CreateVersion7()).FailureCode);
        Assert.Equal("CURRENT_SNAPSHOT_BINDING", ContentDeltaRules.BindCurrentSnapshot(items, accepted with { SignatureKeyVersion = 2 }, Org).FailureCode);
    }
}
