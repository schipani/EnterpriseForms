using System.Security.Cryptography;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Features.Documents.Ports;
using EnterpriseForms.Application.Features.FormDefinitions.Layout;
using EnterpriseForms.Application.Features.FormRecords.Encryption;
using EnterpriseForms.Application.Features.FormRecords.Integrity;
using EnterpriseForms.Application.Features.FormRecords.Projection;
using EnterpriseForms.Application.Features.FormRecords.SharedEdits;
using EnterpriseForms.Application.Features.FormRecords.Values;
using EnterpriseForms.Contracts.Features;
using EnterpriseForms.Domain.Features.Documents;
using EnterpriseForms.Domain.Features.FormDefinitions;
using EnterpriseForms.Domain.Features.FormRecords;
using EnterpriseForms.Infrastructure.Features.Documents;
using EnterpriseForms.Infrastructure.Features.FormRecords;
using EnterpriseForms.Integrity.Canonical;
using Microsoft.Extensions.Options;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// Phase C5 business side of share-editor writes (D2, D3, D5, D6, D9, D15; C5-F2/F3/F4): verbatim preservation of every
/// unedited row (sealed ciphertext and document versions byte-for-byte, signed schema versions kept), no decryption
/// outside the read scope, one indistinguishable refusal for every key outside the editable projection, and the R1/R2
/// rule-eligibility gates. The Authority re-decides scope independently (AuthorizationAuthority.Tests).
/// </summary>
public sealed class PhaseC5SharedEditTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly Guid Record = Guid.CreateVersion7();
    private static readonly Guid Owner = Guid.CreateVersion7();
    private static readonly Guid Editor = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);
    private static readonly string[] RefusedKeys = ["does_not_exist", "unshared", "view_only", "locked", "tags", "restricted_value"];

    private sealed class SpyEncryption(IFieldEncryptionService inner) : IFieldEncryptionService
    {
        public List<Guid> DecryptedItems { get; } = [];

        public string ProviderName => inner.ProviderName;

        public bool IsAvailable => inner.IsAvailable;

        public FieldEncryptedValue Encrypt(FieldEncryptionContext context, string canonicalPlaintext) => inner.Encrypt(context, canonicalPlaintext);

        public string Decrypt(FieldEncryptedValue value)
        {
            DecryptedItems.Add(value.FieldValueItemId);
            return inner.Decrypt(value);
        }
    }

    private static SpyEncryption NewEncryption() => new(new AesGcmFieldEncryptionService(new LocalDevelopmentKeyProvider(Options.Create(new EncryptionOptions { KeyProvider = "LocalDevelopment", LocalDevelopmentKekBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }))));

    private static FieldDefinition Field(string key, string type = "String", string protection = "Standard", string classification = "Internal", string mode = "Single") => new()
    {
        FieldDefinitionId = Guid.CreateVersion7(), FieldKey = key, FieldDisplayName = key, FieldDataType = type, FieldClassification = classification, FieldProtectionMode = protection, OptionSelectionMode = mode,
    };

    private static LayoutElement Element(FieldDefinition field, bool readOnly = false, RuleCondition? visibleWhen = null, RuleCondition? requiredWhen = null, bool clearWhenHidden = false) =>
        new(LayoutElementKinds.Field, field.FieldDefinitionId, null, field.FieldDataType == "Option" ? "select" : "text", field.FieldDisplayName, null, new LayoutPlacement(4, false), new LayoutValidation(false, null, null, null, null, null, null, null), null, null,
            ReadOnly: readOnly, ClearWhenHidden: clearWhenHidden, VisibleWhen: visibleWhen, RequiredWhen: requiredWhen);

    private static RuleComparison Eq(FieldDefinition field, string value) => new(RuleOperators.Equal, field.FieldDefinitionId, value, null);

    /// <summary>Builds a verified record: one stored row per (field, wire), protected per field; readable/editable item sets as the Authority would report.</summary>
    private sealed class Fixture
    {
        public SpyEncryption Encryption { get; } = NewEncryption();
        public List<FieldDefinition> Fields { get; } = [];
        public List<FieldOption> Options { get; } = [];
        public List<LayoutElement> Elements { get; } = [];
        public List<ISignedContentRow> Rows { get; } = [];
        public HashSet<Guid> ReadableItems { get; } = [];
        public HashSet<Guid> EditableItems { get; } = [];
        public int HeaderSchemaVersion { get; set; } = 3;
        public bool SwitchOn { get; set; } = true;

        public FieldValueRowBase Add(FieldDefinition field, string wire, bool readable, bool editable, LayoutElement? element = null, bool placed = true)
        {
            Fields.Add(field);
            element ??= Element(field);
            if (placed)
            {
                Elements.Add(element);
            }

            var typed = FieldValueCodec.CreateRow(field, element, Options, wire, Tenant, Record, Guid.CreateVersion7(), Now, Owner);
            var row = FieldValueCodec.Protect(typed, field, Options.ToDictionary(o => o.FieldOptionId), Encryption);
            Stamp(row);
            Rows.Add(row);
            if (readable)
            {
                ReadableItems.Add(row.FieldValueItemId);
            }

            if (editable)
            {
                EditableItems.Add(row.FieldValueItemId);
            }

            return row;
        }

        public List<FieldOption> OptionsFor(FieldDefinition field, params string[] keys)
        {
            var created = keys.Select((k, i) => new FieldOption { FieldOptionId = Guid.CreateVersion7(), FieldDefinitionId = field.FieldDefinitionId, OptionKey = k, OptionDisplayText = k, OptionSortOrder = i, IsActive = true }).ToList();
            Options.AddRange(created);
            return created;
        }

        public static void Stamp(ISignedContentRow row)
        {
            row.ContentChangeOperationId = Guid.CreateVersion7();
            row.ContentRevision = 1;
            row.RowRevision = 1;
            row.RowSignatureKeyVersion = 1;
            row.RowSignatureSchemaVersion = BusinessSignedSchemas.CurrentSchemaVersion(row);
            row.RowSignature = new byte[32];
        }

        public VerifiedRecord Build()
        {
            var header = new FormRecordRevision { FormRecordRevisionId = Guid.CreateVersion7(), TenantId = Tenant, FormRecordId = Record, FormDefinitionId = Guid.CreateVersion7(), CreatedFormVersionId = Guid.CreateVersion7(), CreatedByUserId = Owner, RecordStatus = "Draft" };
            Stamp(header);
            header.RowSignatureSchemaVersion = HeaderSchemaVersion;
            var items = Rows.Select(r => new VerifiedProjectionItem((ManifestItemKind)r.ItemKindCode, r.ItemId)).ToList();
            var readable = items.Where(i => ReadableItems.Contains(i.ItemId)).ToList();
            var editable = items.Where(i => EditableItems.Contains(i.ItemId)).ToList();
            var verification = new VerifyRecordProjectionResult(true, null, 1, Guid.CreateVersion7(), 5, editable, false, Guid.CreateVersion7(), readable, editable, SwitchOn);
            var layout = new FormLayout(2, "Test", [new LayoutSection("main", "Main", 4, Elements)]);
            var head = new AcceptedContentRevision(Record, 1, verification.CommittedOperationId, Convert.ToBase64String(new byte[32]), 5, 1, 1, "Anchored", Guid.CreateVersion7());
            return new VerifiedRecord(head, verification, header, Rows, Rows.Where(r => EditableItems.Contains(r.ItemId)).ToList(), new FormDefinition { FormDefinitionId = header.FormDefinitionId, FormDefinitionName = "Test" },
                new FormVersion { FormVersionId = header.CreatedFormVersionId, LayoutJson = "{}" }, layout, Fields, Options, Encryption, [], []);
        }
    }

    private static BusinessException Refusal(VerifiedRecord verified, Dictionary<string, string> values, IReadOnlyList<RecordGroupValuesRequest>? groups = null) =>
        Assert.Throws<BusinessException>(() => SharedFieldWriteBuilder.Build(verified, values, groups, Tenant, Record, Now, Editor));

    private static FileAttachmentVersion Attachment()
    {
        var attachment = new FileAttachmentVersion
        {
            FileAttachmentVersionId = Guid.CreateVersion7(), TenantId = Tenant, FormRecordId = Record, FileAttachmentId = Guid.CreateVersion7(), FieldDefinitionId = Guid.CreateVersion7(), VersionNumber = 1, OriginalFileName = "a.pdf",
            DetectedContentType = "application/pdf", PlaintextSizeBytes = 10, PlaintextSha256 = new byte[32], CiphertextSizeBytes = 26, CiphertextSha256 = new byte[32], StorageProviderName = "azurite", StorageObjectKey = "objects/a",
            EncryptionAlgorithmName = "AES-256-GCM", EncryptionKeyReference = "kek", EncryptionEnvelopeVersion = 1, WrappedDataKey = new byte[60], EncryptionNoncePrefix = new byte[8], EncryptionSegmentSizeBytes = 1048576,
            ScanVerdict = "Clean", ScannerName = "clamav", ScannedOn = Now, UploadedByUserId = Owner, UploadedOn = Now,
        };
        Fixture.Stamp(attachment);
        return attachment;
    }

    private static Dictionary<string, RowCanonicalColumn> Columns(ISignedContentRow row) => RowCanonicalDecoder.Decode(BusinessSignedSchemas.Encode(row, 1)).Columns.ToDictionary(c => c.Name);

    [Fact]
    public void EveryUneditedRowIsCopiedVerbatimAndNothingOutsideTheReadScopeIsDecrypted()
    {
        var f = new Fixture();
        var note = f.Add(Field("note"), "before", readable: true, editable: true);
        var secret = f.Add(Field("secret", protection: "ApplicationEncrypted", classification: "Confidential"), "never decrypt me", readable: false, editable: false, placed: false);
        var reference = f.Add(Field("reference", protection: "ApplicationEncrypted", classification: "Confidential"), "readable only", readable: true, editable: false);
        var attachment = Attachment();
        f.Rows.Add(attachment);
        var occurrence = new FormRecordGroupRevision { FormRecordGroupRevisionId = Guid.CreateVersion7(), TenantId = Tenant, FormRecordId = Record, FormRecordGroupId = Guid.CreateVersion7(), GroupDefinitionId = Guid.CreateVersion7(), GroupSortOrder = 0, CreatedOn = Now, CreatedByUserId = Owner };
        Fixture.Stamp(occurrence);
        f.Rows.Add(occurrence);
        var verified = f.Build();
        f.Encryption.DecryptedItems.Clear();

        var result = SharedFieldWriteBuilder.Build(verified, new Dictionary<string, string> { ["note"] = "after" }, null, Tenant, Record, Now, Editor);

        Assert.Equal(["note"], result.EditedFieldKeys);
        Assert.DoesNotContain(secret.FieldValueItemId, f.Encryption.DecryptedItems);
        Assert.DoesNotContain(reference.FieldValueItemId, f.Encryption.DecryptedItems);
        Assert.Equal(f.Rows.Count, result.Rows.Count);
        foreach (var original in f.Rows.Where(r => r.ItemId != note.ItemId))
        {
            var copy = result.Rows.Single(r => r.ItemKindCode == original.ItemKindCode && r.ItemId == original.ItemId);
            if (original.RowPrimaryKey == original.ItemId)
            {
                Assert.Equal(original.RowPrimaryKey, copy.RowPrimaryKey); // identity key (entry) unchanged
            }
            else
            {
                Assert.NotEqual(original.RowPrimaryKey, copy.RowPrimaryKey); // fresh surrogate for the new staged physical row
            }

            Assert.Equal(original.RowSignatureSchemaVersion, copy.RowSignatureSchemaVersion);
            // Same canonical columns once the envelope (operation binding) is set identically: protected content preserved byte-for-byte.
            copy.ContentChangeOperationId = original.ContentChangeOperationId;
            copy.RowRevision = original.RowRevision;
            var before = Columns(original);
            var after = Columns(copy);
            Assert.Equal(before.Keys.Order(), after.Keys.Order());
            Assert.All(before, c => Assert.True(c.Value.FramedValueEquals(after[c.Key]), c.Key));
        }

        var edited = Assert.Single(result.EditedRows);
        Assert.Equal(note.FieldValueItemId, edited.FieldValueItemId);
        Assert.Equal("after", Assert.IsType<FieldStringValue>(edited).StringValue);
        Assert.Equal(verified.Header.RowSignatureSchemaVersion, result.Header.RowSignatureSchemaVersion);
    }

    [Fact]
    public void AnEditedEncryptedValueIsSealedAfreshUnderTheSameItem()
    {
        var f = new Fixture();
        var sealedField = Field("secret_note", protection: "ApplicationEncrypted", classification: "Confidential");
        var current = (FieldEncryptedValue)f.Add(sealedField, "old", readable: true, editable: true);
        var result = SharedFieldWriteBuilder.Build(f.Build(), new Dictionary<string, string> { ["secret_note"] = "new" }, null, Tenant, Record, Now, Editor);
        var edited = Assert.IsType<FieldEncryptedValue>(Assert.Single(result.EditedRows));
        Assert.Equal(current.FieldValueItemId, edited.FieldValueItemId);
        Assert.Equal(sealedField.FieldDefinitionId, edited.FieldDefinitionId);
        Assert.False(edited.EncryptionNonceBytes.AsSpan().SequenceEqual(current.EncryptionNonceBytes));
        Assert.False(edited.WrappedDataKeyBytes.AsSpan().SequenceEqual(current.WrappedDataKeyBytes));
        Assert.Equal("new", f.Encryption.Decrypt(edited));
    }

    [Fact]
    public void EveryKeyOutsideTheEditableProjectionGetsTheSameRefusalBeforeAnythingIsDecrypted()
    {
        var f = new Fixture();
        f.Add(Field("note"), "x", readable: true, editable: true);
        f.Add(Field("unshared", protection: "ApplicationEncrypted", classification: "Confidential"), "hidden", readable: false, editable: false, placed: false);
        f.Add(Field("view_only"), "x", readable: true, editable: false);
        var locked = Field("locked");
        f.Add(locked, "x", readable: true, editable: true, element: Element(locked, readOnly: true));
        var tags = Field("tags", type: "Option", mode: "Multiple");
        f.OptionsFor(tags, "a", "b");
        f.Add(tags, "a", readable: true, editable: true);
        f.Add(Field("restricted_value", classification: "Restricted"), "x", readable: true, editable: true);
        var verified = f.Build();

        var refusals = RefusedKeys
            .Select(key =>
            {
                f.Encryption.DecryptedItems.Clear();
                var ex = Refusal(verified, new Dictionary<string, string> { ["note"] = "changed", [key] = "y" });
                Assert.Empty(f.Encryption.DecryptedItems); // refused before any decryption
                return ex;
            })
            .ToList();
        Assert.All(refusals, ex => Assert.Equal((SharedFieldWriteBuilder.NotEditableCode, 403, SharedFieldWriteBuilder.NotEditableMessage), (ex.ErrorCode, ex.HttpStatusCode, ex.Message)));
        Assert.Equal(SharedFieldWriteBuilder.NotEditableCode, Refusal(verified, new Dictionary<string, string>(), [new RecordGroupValuesRequest("entries", [])]).ErrorCode);
    }

    [Fact]
    public void ClearingAndNoOpChangesAreRefused()
    {
        var f = new Fixture();
        f.Add(Field("note"), "same", readable: true, editable: true);
        var verified = f.Build();
        Assert.Equal("CLEAR_NOT_PERMITTED", Refusal(verified, new Dictionary<string, string> { ["note"] = "" }).ErrorCode);
        Assert.Equal("NO_CHANGE", Refusal(verified, new Dictionary<string, string> { ["note"] = "same" }).ErrorCode);
    }

    [Fact]
    public void EligibilityFollowsTheV1LimitsAndRulesR1AndR2()
    {
        var f = new Fixture();
        var unshared = Field("unshared_input");
        var kind = Field("kind");
        var guarded = Field("guarded");      // R1: visibility reads an unshared field
        var driver = Field("driver");        // R2: drives a rule on an unshared field
        var driven = Field("driven_hidden");
        var free = Field("free");
        var pairA = Field("pair_a");         // R2 satisfied: drives an editable field whose inputs are readable
        var pairB = Field("pair_b");
        var pending = Field("pending_migration", protection: "ApplicationEncrypted", classification: "Confidential");
        f.Add(unshared, "x", readable: false, editable: false, placed: true);
        f.Add(kind, "a", readable: true, editable: true);
        f.Add(guarded, "x", readable: true, editable: true, element: Element(guarded, visibleWhen: Eq(unshared, "x")));
        f.Add(driver, "x", readable: true, editable: true);
        f.Add(driven, "x", readable: false, editable: false, element: Element(driven, requiredWhen: Eq(driver, "x")));
        f.Add(free, "x", readable: true, editable: true);
        f.Add(pairA, "x", readable: true, editable: true);
        f.Add(pairB, "x", readable: true, editable: true, element: Element(pairB, visibleWhen: Eq(pairA, "x")));
        f.Add(Field("view_only"), "x", readable: true, editable: false);
        // A field strengthened to encryption whose stored row is still plaintext (migration pending).
        pending.FieldProtectionMode = "Standard";
        f.Add(pending, "legacy", readable: true, editable: true);
        pending.FieldProtectionMode = "ApplicationEncrypted";
        var verified = f.Build();

        var access = SharedFieldEligibility.Compute(verified.Layout, verified.FieldsById, SharedFieldWriteBuilder.ReadableRows(verified), f.EditableItems).Values.ToDictionary(a => a.Field.FieldKey, a => a.NotEditableReason);
        Assert.False(access.ContainsKey("unshared_input"));   // not readable ⇒ not even listed
        Assert.False(access.ContainsKey("driven_hidden"));
        Assert.Null(access["kind"]);
        Assert.Null(access["free"]);
        Assert.Null(access["pair_b"]);
        Assert.Null(access["pair_a"]);
        Assert.Equal(SharedEditReasons.RuleDependency, access["guarded"]);
        Assert.Equal(SharedEditReasons.RuleDependency, access["driver"]);
        Assert.Equal(SharedEditReasons.ViewOnlyShare, access["view_only"]);
        Assert.Equal(SharedEditReasons.ProtectionMigrationPending, access["pending_migration"]);
    }

    [Fact]
    public void RuleEffectsOnEditableFieldsAreEnforcedOverReadableValues()
    {
        var f = new Fixture();
        var toggle = Field("toggle");
        var detail = Field("detail");
        f.Add(toggle, "show", readable: true, editable: true);
        f.Add(detail, "kept", readable: true, editable: true, element: Element(detail, visibleWhen: Eq(toggle, "show"), clearWhenHidden: true));
        var verified = f.Build();

        // R2 dynamic: hiding a field the designer marked clear-when-hidden would remove a value; refused generically.
        Assert.Equal("CHANGE_AFFECTS_OTHER_FIELDS", Refusal(verified, new Dictionary<string, string> { ["toggle"] = "hide" }).ErrorCode);

        var g = new Fixture();
        var mode = Field("mode");
        var extra = Field("extra");
        g.Add(mode, "off", readable: true, editable: true);
        g.Add(extra, "x", readable: true, editable: true, element: Element(extra, visibleWhen: Eq(mode, "on")));
        // R1 dynamic: a field currently hidden by a rule over readable inputs cannot be changed.
        Assert.Equal("FIELD_HIDDEN_BY_RULE", Refusal(g.Build(), new Dictionary<string, string> { ["extra"] = "changed" }).ErrorCode);
    }

    [Fact]
    public void ASingleSelectChangeIsValidatedAgainstTheActiveOptionCatalog()
    {
        var f = new Fixture();
        var status = Field("status", type: "Option");
        var options = f.OptionsFor(status, "open", "closed", "retired");
        options[2].IsActive = false;
        f.Add(status, "open", readable: true, editable: true);
        var verified = f.Build();

        var changed = SharedFieldWriteBuilder.Build(verified, new Dictionary<string, string> { ["status"] = "closed" }, null, Tenant, Record, Now, Editor);
        Assert.Equal(options[1].FieldOptionId, Assert.IsType<FieldOptionValue>(Assert.Single(changed.EditedRows)).FieldOptionId);
        Assert.Throws<BusinessException>(() => SharedFieldWriteBuilder.Build(verified, new Dictionary<string, string> { ["status"] = "retired" }, null, Tenant, Record, Now, Editor));
        Assert.Throws<BusinessException>(() => SharedFieldWriteBuilder.Build(verified, new Dictionary<string, string> { ["status"] = "forged" }, null, Tenant, Record, Now, Editor));
    }

    [Fact]
    public void AStageAHeaderKeepsItsSignedSchemaVersion()
    {
        var f = new Fixture { HeaderSchemaVersion = 2 };
        f.Add(Field("note"), "x", readable: true, editable: true);
        var result = SharedFieldWriteBuilder.Build(f.Build(), new Dictionary<string, string> { ["note"] = "y" }, null, Tenant, Record, Now, Editor);
        Assert.Equal(2, result.Header.RowSignatureSchemaVersion);
    }
}
