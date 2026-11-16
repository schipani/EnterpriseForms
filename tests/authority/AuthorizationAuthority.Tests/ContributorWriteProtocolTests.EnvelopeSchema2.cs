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
/// E3-M2b-2 (contract §4.4, §23; owner decisions D-2, D-3, D-9): signed schema 2 of the two envelope kinds is registered at the
/// Authority, but every schema-2 candidate is still refused on every write path before anything is signed (the E3-M2a
/// EnvelopeRules schema/version refusal, unchanged); S-9 pins column types and nullability of both envelope kinds at Prepare
/// and again at Commit on the verified staged bytes, so it is not a Prepare-only rule and the content acceptance rule version
/// stays 1. The protection-migration (reprotection) path is covered in ProtectionReconciliationTests.
/// </summary>
public sealed partial class ContributorWriteProtocolTests
{
    private static readonly RowSchemaDefinition EncryptedValuesV2 = WithV2Columns(EncryptedValues);
    private static readonly RowSchemaDefinition AttachmentsV2 = WithV2Columns(Attachments);

    private static RowSchemaDefinition WithV2Columns(RowSchemaDefinition schema1) => new(schema1.EntityKind, 2,
        schema1.Columns.Concat([
            new RowSignedColumn("data_key_commitment_bytes", RowColumnType.Bytes, false),
            new RowSignedColumn("data_key_identifier", RowColumnType.Uuid, false),
            new RowSignedColumn("key_wrap_algorithm_code", RowColumnType.Int32, false),
        ]).OrderBy(c => c.ColumnName, StringComparer.Ordinal).ToList());

    /// <summary>
    /// A well-formed schema-2 fixture of an envelope row (the only way schema 2 exists before E3-M4 writers). E3-M2d: well-formed under
    /// the v2 rules too (canonical v2 key reference of the organization and the row's purpose, wrap code 1, a UUIDv7 data-key id, a
    /// 32-byte commitment, the v2 document ciphertext size), so a refusal of it is the M2d acceptance decision, not a shape error.
    /// </summary>
    private static Row AsSchema2(Row row, Guid org, int envelopeVersion = 2, Guid? dataKeyIdentifier = null, byte commitment = 0xdd)
    {
        var field = row.Schema.EntityKind == EncryptedValues.EntityKind;
        var schema = field ? EncryptedValuesV2 : AttachmentsV2;
        var v2 = (row with { Schema = schema })
            .With("encryption_envelope_version", RowValue.Int32(envelopeVersion))
            .With("encryption_key_reference", RowValue.Text($"localdev:{org:D}:{(field ? "field-value" : "document")}:1"))
            .With("data_key_commitment_bytes", RowValue.Bytes(Fill(32, commitment)))
            .With("data_key_identifier", RowValue.Uuid(dataKeyIdentifier ?? Guid.CreateVersion7()))
            .With("key_wrap_algorithm_code", RowValue.Int32(1));
        return field ? v2 : v2.With("ciphertext_size_bytes", RowValue.Int64(1020)); // 1,000 plaintext bytes + one 20-byte segment overhead
    }

    private static List<Row> WithSchema2(Harness h, string kind, int envelopeVersion) =>
        Change(h.Rows(), kind == "field" ? h.SealedItem : h.Document, r => AsSchema2(kind == "field" ? Sealed(h.Org, h.Record, h.SealedField, h.SealedItem, 70) : r, h.Org, envelopeVersion));

    public static TheoryData<string, int> Schema2Variants => new() { { "field", 2 }, { "field", 1 }, { "document", 2 }, { "document", 1 } };

    /// <summary>
    /// R-M2b2-11: a schema-2 row with envelope 1 is refused by the S-4 tie (before the envelope rules). E3-M2d: a well-formed tied
    /// schema-2 / envelope-2 row passes the tie and the v2 shape rules and, being a new v2 row (not a carry of the current head), is
    /// refused at Prepare before anything is signed (ENVELOPE_VERSION_NOT_ENABLED; before E3-M2d it was ENVELOPE_SCHEMA_UNSUPPORTED, D-9).
    /// </summary>
    private static string Schema2Reason(int envelopeVersion) => envelopeVersion == 2 ? EnvelopeV2Rules.Reasons.VersionNotEnabled : EnvelopeSchemaTie.MismatchReason;

    [Theory]
    [MemberData(nameof(Schema2Variants))]
    public async Task ARecordCannotBeCreatedWithASchemaTwoEnvelopeRow(string kind, int envelopeVersion)
    {
        var h = new Harness();
        var operation = Guid.CreateVersion7();
        var signatures = h.Signer.SignatureCount;
        var ex = await Assert.ThrowsAsync<AuthorityException>(() => h.PrepareAsync(h.Owner, operation, h.Stage(WithSchema2(h, kind, envelopeVersion), operation), withCurrent: true));

        Assert.Equal(AuthorityErrorCodes.InvalidCommand, ex.ErrorCode);
        Assert.Equal("ENVELOPE_INVALID: " + Schema2Reason(envelopeVersion), ex.Message); // D-9 for schema 2 / envelope 2; S-4 for a mismatched envelope 1
        Assert.Equal(signatures, h.Signer.SignatureCount);
        Assert.Empty(h.Store.Operations);
        Assert.Empty(h.Store.CandidateLeaves);
        Assert.Equal(0, h.Head.CommittedContentRevision);
    }

    [Theory]
    [MemberData(nameof(Schema2Variants))]
    public async Task AnOwnerEditCannotIntroduceASchemaTwoEnvelopeRow(string kind, int envelopeVersion)
    {
        var h = await new Harness().CreatedAsync();
        await RefusedBeforeSigningAsync(h, h.Owner, WithSchema2(h, kind, envelopeVersion), Schema2Reason(envelopeVersion));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(1)]
    public async Task AShareEditorCannotIntroduceASchemaTwoEnvelopeRow(int envelopeVersion)
    {
        var h = await new Harness().CreatedAsync();
        h.ShareWithContributor("contributor", false, h.SealedItem);
        await RefusedBeforeSigningAsync(h, h.Contributor, WithSchema2(h, "field", envelopeVersion), Schema2Reason(envelopeVersion));

        // Positive control: the same share editor still commits a fresh, well-formed schema-1 envelope.
        Assert.Equal(2, (await h.WriteAsync(h.Contributor, Change(h.Rows(), h.SealedItem, _ => Sealed(h.Org, h.Record, h.SealedField, h.SealedItem, 91)), withCurrent: true)).CommittedContentRevision);
    }

    public static TheoryData<string, string> S9Violations => new()
    {
        { "document scanned_on as text", "document" },          // F-13: accepted before E3-M2b-2 (EnvelopeRules does not read this column)
        { "document ciphertext size as int32", "document" },
        { "document scanner name NULL", "document" },
        { "field logical type NULL", "field" },
        { "field group as text", "field" },
        { "schema 2 wrap code NULL", "field" },
        { "schema 2 data-key id as text", "document" },
    };

    private static Row S9Violation(Harness h, string variant)
    {
        var field = Sealed(h.Org, h.Record, h.SealedField, h.SealedItem, 10);
        var document = h.Rows().Single(r => r.ItemId == h.Document);
        return variant switch
        {
            "document scanned_on as text" => document.With("scanned_on", RowValue.Text("2026-10-01T00:00:00Z")),
            "document ciphertext size as int32" => document.With("ciphertext_size_bytes", RowValue.Int32(1024)),
            "document scanner name NULL" => document.With("scanner_name", null),
            "field logical type NULL" => field.With("encrypted_logical_type", null),
            "field group as text" => field.With("form_record_group_id", RowValue.Text("not-a-uuid")),
            "schema 2 wrap code NULL" => AsSchema2(Sealed(h.Org, h.Record, h.SealedField, h.SealedItem, 80), h.Org).With("key_wrap_algorithm_code", null),
            "schema 2 data-key id as text" => AsSchema2(document, h.Org).With("data_key_identifier", RowValue.Text("0199a2b3-c4d5-7e6f-8123-456789abce0a")),
            _ => throw new ArgumentOutOfRangeException(nameof(variant)),
        };
    }

    /// <summary>Encodes a row whose registry declares other types or nullability than its values (a non-conforming encoder).</summary>
    private static Staged EncodeNonConforming(Row row, Guid organizationId, Guid operation, long revision)
    {
        var values = new Dictionary<string, RowValue?>(row.Values, StringComparer.Ordinal) { ["content_change_operation_id"] = RowValue.Uuid(operation) };
        var loose = new RowSchemaDefinition(row.Schema.EntityKind, row.Schema.SchemaVersion,
            row.Schema.Columns.Select(c => values[c.ColumnName] is { } v ? new RowSignedColumn(c.ColumnName, v.Type, c.Nullable) : new RowSignedColumn(c.ColumnName, c.Type, true)).ToList());
        var primaryKey = row.IdentityPrimaryKey ? row.ItemId : Guid.CreateVersion7();
        var bytes = RowCanonicalizerV1.Encode(loose, new RowSigningContext(1, organizationId, primaryKey, (ulong)revision), values);
        return new Staged(row, primaryKey, bytes, row.Kind, row.ItemId);
    }

    private static List<Staged> StageWithViolation(Harness h, Guid operation, Row violating)
    {
        var revision = h.Head.CommittedContentRevision + 1;
        return h.Rows().Select(r => r.ItemId == violating.ItemId ? EncodeNonConforming(violating, h.Org, operation, revision) : Encode(r, h.Org, operation, revision)).ToList();
    }

    [Theory]
    [MemberData(nameof(S9Violations))]
    public async Task S9RefusesAWrongTypeOrNullAtPrepareBeforeAnythingIsSigned(string variant, string kind)
    {
        _ = kind;
        var h = await new Harness().CreatedAsync();
        var operation = Guid.CreateVersion7();
        var head = (h.Head.CommittedContentRevision, h.Head.CommittedOperationId);
        var signatures = h.Signer.SignatureCount;
        var ex = await Assert.ThrowsAsync<AuthorityException>(() => h.PrepareAsync(h.Owner, operation, StageWithViolation(h, operation, S9Violation(h, variant)), withCurrent: true));

        Assert.Equal(AuthorityErrorCodes.InvalidCommand, ex.ErrorCode);
        Assert.Equal("UNSUPPORTED_SIGNED_SCHEMA: column type or nullability differs from the registered assignment", ex.Message);
        Assert.Equal(signatures, h.Signer.SignatureCount);
        Assert.DoesNotContain(h.Store.Operations, o => o.ContentChangeOperationId == operation);
        Assert.Equal(head, (h.Head.CommittedContentRevision, h.Head.CommittedOperationId));
    }

    /// <summary>
    /// D-2: an operation prepared without S-9 (its leaves signed as by a build before E3-M2b-2, stamped at this build's CARV
    /// version so it passes the CARV Commit rule) cannot commit a row S-9 refuses: Commit re-applies the rule to the verified
    /// staged bytes, aborts the operation and accepts nothing.
    /// </summary>
    [Theory]
    [InlineData("document scanned_on as text")]
    [InlineData("field group as text")]
    [InlineData("schema 2 wrap code NULL")]
    public async Task S9IsReappliedAtCommitOnTheVerifiedStagedBytes(string variant)
    {
        var h = await new Harness().CreatedAsync();
        var head = (h.Head.CommittedContentRevision, h.Head.CommittedOperationId, Root: h.Head.CommittedManifestRoot.ToArray());
        var operation = Guid.CreateVersion7();
        var staged = await SeedPreparedWithoutPrepareRulesAsync(h, operation, StageWithViolation(h, operation, S9Violation(h, variant)));

        var ex = await Assert.ThrowsAsync<AuthorityException>(() => h.Service.CommitAsync(Harness.Actor(h.Owner), operation, new CommitContentChangeRequest(h.Org, h.Record, staged), Guid.CreateVersion7(), CancellationToken.None));

        Assert.StartsWith("UNSUPPORTED_SIGNED_SCHEMA:", ex.Message, StringComparison.Ordinal);
        var aborted = h.Store.Operations.Single(o => o.ContentChangeOperationId == operation);
        Assert.Equal(ContentOperationStates.Aborted, aborted.OperationStateName);
        Assert.Equal(ContentService.SignedSchemaInvalidAtCommitReason, aborted.OperationQuarantineReasonCode);
        Assert.Null(aborted.CommitAcceptanceRuleVersion);
        Assert.Equal(head.CommittedContentRevision, h.Head.CommittedContentRevision);
        Assert.Equal(head.CommittedOperationId, h.Head.CommittedOperationId);
        Assert.Equal(head.Root, h.Head.CommittedManifestRoot);
        Assert.DoesNotContain(h.Store.Commits, c => c.CommittedOperationId == operation);
    }

    /// <summary>The same seeding with a conforming candidate commits: the Commit-time S-9 rule refuses only non-conforming bytes.</summary>
    [Fact]
    public async Task ACommitOfAConformingOperationPreparedWithoutS9StillCommits()
    {
        var h = await new Harness().CreatedAsync();
        var operation = Guid.CreateVersion7();
        var staged = await SeedPreparedWithoutPrepareRulesAsync(h, operation, h.Stage(Change(h.Rows(), h.SealedItem, _ => Sealed(h.Org, h.Record, h.SealedField, h.SealedItem, 77)), operation));
        var revision = await h.Service.CommitAsync(Harness.Actor(h.Owner), operation, new CommitContentChangeRequest(h.Org, h.Record, staged), Guid.CreateVersion7(), CancellationToken.None);
        Assert.Equal(2, revision.CommittedContentRevision);
        Assert.Equal(ContentAcceptanceRules.CurrentVersion, h.Store.Operations.Single(o => o.ContentChangeOperationId == operation).CommitAcceptanceRuleVersion);
    }

    [Fact]
    public void SchemaTwoIsRegisteredForBothEnvelopeKindsWithPinnedShapesAndTheV1AllowListIsUnchanged()
    {
        foreach (var (schema1, schema2) in new[] { (EncryptedValues, EncryptedValuesV2), (Attachments, AttachmentsV2) })
        {
            var rule1 = ContentDeltaRules.FindRule(schema1.EntityKind, 1)!;
            var rule2 = ContentDeltaRules.FindRule(schema2.EntityKind, 2)!;
            Assert.Equal(rule1.Kind, rule2.Kind);
            Assert.Equal(rule1.PrimaryKey, rule2.PrimaryKey);
            Assert.Equal(rule1.ItemIdColumn, rule2.ItemIdColumn);
            Assert.Equal(rule1.ShareUpdatable, rule2.ShareUpdatable);
            foreach (var (name, cls) in rule1.Columns)
            {
                Assert.Equal(cls, rule2.Columns[name]);
            }

            foreach (var name in new[] { "data_key_commitment_bytes", "data_key_identifier", "key_wrap_algorithm_code" })
            {
                Assert.Equal(SignedColumnClass.Content, rule2.Columns[name]);
                Assert.False(rule2.PinnedShapes![name].Nullable);
                Assert.False(rule1.Columns.ContainsKey(name));
            }

            foreach (var column in schema2.Columns)
            {
                Assert.Equal(new PinnedColumnShape(column.Type, column.Nullable), rule2.PinnedShapes![column.ColumnName]);
            }

            Assert.Null(ContentDeltaRules.FindRule(schema1.EntityKind, 3));
        }

        // D-2 (E3-M2b-2): S-9 is re-derived at Commit and did not bump the rule version; E3-M2d's Prepare-only v2 rules did (its D-2).
        Assert.Equal(2, ContentAcceptanceRules.CurrentVersion);
        Assert.Equal(new HashSet<int> { 1 }, EnvelopeRules.AllowedEnvelopeVersions);
        Assert.Null(ContentDeltaRules.FindRule("form_records", 2)!.PinnedShapes); // OD-M2-22: only the envelope kinds are pinned
    }

    /// <summary>Signs <paramref name="candidate"/> with the organization's key and records it as a Prepared operation stamped at this build's CARV version, without running Prepare.</summary>
    private static async Task<List<StagedContentItem>> SeedPreparedWithoutPrepareRulesAsync(Harness h, Guid operation, List<Staged> candidate)
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
            OperationIdempotencyKey = "pre-m2b2-" + operation.ToString("N"),
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
            PrepareAcceptanceRuleVersion = ContentAcceptanceRules.CurrentVersion,
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
}
