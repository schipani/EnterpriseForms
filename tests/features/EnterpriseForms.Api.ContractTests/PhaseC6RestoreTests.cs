using System.Security.Cryptography;
using System.Text;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Features.FormDefinitions.Layout;
using EnterpriseForms.Application.Features.FormRecords.Encryption;
using EnterpriseForms.Application.Features.FormRecords.History;
using EnterpriseForms.Application.Features.FormRecords.Staging;
using EnterpriseForms.Application.Features.FormRecords.Values;
using EnterpriseForms.Domain.Features.FormDefinitions;
using EnterpriseForms.Domain.Features.FormRecords;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// Phase C6 M3 restore plan + the ordinary owner write it feeds (architecture §4.2): exact restore (OD-11), identity rule I-R
/// (nothing ever takes a source-only id; entries matched by stable id only), values of fields no longer placed (OD-12), the
/// current protection policy and the plaintext acknowledgement (OD-13), all-or-nothing validation, unavailable historic keys,
/// sections that cannot be restored exactly, and what is never restored (documents are not value rows; status is the caller's).
/// </summary>
public sealed class PhaseC6RestoreTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly Guid Record = Guid.CreateVersion7();
    private static readonly Guid Actor = Guid.CreateVersion7();
    private static readonly Guid Group = Guid.CreateVersion7();
    private static readonly Guid OrphanGroup = Guid.CreateVersion7();

    private static readonly FieldDefinition Title = Field("case_title");
    private static readonly FieldDefinition Notes = Field("case_notes");
    private static readonly FieldDefinition Extra = Field("legacy_code"); // defined, not placed (OD-12)
    private static readonly FieldDefinition Secret = Field("informant", FieldProtectionModes.ApplicationEncrypted);
    private static readonly FieldDefinition Released = Field("released_note"); // Standard now; encrypted in older versions
    private static readonly FieldDefinition Witness = Field("witness_name", group: Group);
    private static readonly FieldDefinition OrphanField = Field("old_entry", group: OrphanGroup);
    private static readonly FieldDefinition Documents = Field("case_documents", dataType: FieldDataTypes.File);
    private static readonly FieldDefinition[] Fields = [Title, Notes, Extra, Secret, Released, Witness, OrphanField, Documents];
    private static readonly GroupDefinition[] Groups = [new() { GroupDefinitionId = Group, GroupKey = "witnesses" }, new() { GroupDefinitionId = OrphanGroup, GroupKey = "old_section" }];

    private static FieldDefinition Field(string key, string protection = FieldProtectionModes.Standard, Guid? group = null, string dataType = FieldDataTypes.String) =>
        new() { FieldDefinitionId = Guid.CreateVersion7(), FieldKey = key, FieldDisplayName = key, FieldDataType = dataType, FieldProtectionMode = protection, GroupDefinitionId = group };

    private static FormLayout Layout(bool titleRequired = false)
    {
        static string FieldElement(FieldDefinition f, bool required = false) =>
            "{\"kind\":\"field\",\"fieldDefinitionId\":\"" + f.FieldDefinitionId + "\",\"control\":\"text\",\"label\":\"" + f.FieldKey + "\",\"required\":" + (required ? "true" : "false") + ",\"layout\":{\"columnSpan\":2,\"startOnNewRow\":false}}";
        var json = $$"""
            {"layoutSchemaVersion":2,"title":"Case","sections":[
              {"sectionKey":"main","title":"Case","gridColumns":4,"elements":[{{FieldElement(Title, titleRequired)}},{{FieldElement(Notes)}},{{FieldElement(Secret)}},{{FieldElement(Released)}}]},
              {"sectionKey":"witnesses","title":"Witnesses","gridColumns":4,"repeat":{"groupDefinitionId":"{{Group}}","minOccurrences":0,"maxOccurrences":10,"allowAdd":true,"allowRemove":true,"allowReorder":true},"elements":[{{FieldElement(Witness)}}]}
            ]}
            """;
        return FormLayoutCodec.Parse(json);
    }

    /// <summary>Reversible stand-in for AES-GCM: a fresh "nonce" per call so re-sealing is observable; optionally a lost key for source rows.</summary>
    private sealed class FakeEncryption(Func<FieldEncryptedValue, bool>? lostKey = null) : IFieldEncryptionService
    {
        public string ProviderName => "Test";

        public bool IsAvailable => true;

        public FieldEncryptedValue Encrypt(FieldEncryptionContext context, string canonicalPlaintext) => new()
        {
            FieldEncryptedValueId = Guid.CreateVersion7(), TenantId = context.TenantId, FormRecordId = context.FormRecordId, FieldDefinitionId = context.FieldDefinitionId, FieldValueItemId = context.FieldValueItemId,
            EncryptedValueBytes = Encoding.UTF8.GetBytes(canonicalPlaintext).Reverse().ToArray(), EncryptionNonceBytes = RandomNumberGenerator.GetBytes(12),
        };

        public string Decrypt(FieldEncryptedValue value) =>
            lostKey?.Invoke(value) == true ? throw new CryptographicException("unknown key reference") : Encoding.UTF8.GetString(value.EncryptedValueBytes.Reverse().ToArray());
    }

    private static readonly FakeEncryption Crypto = new();

    private static FieldStringValue Text(FieldDefinition field, string value, Guid? item = null, Guid? entry = null) =>
        new() { FieldStringValueId = Guid.CreateVersion7(), TenantId = Tenant, FormRecordId = Record, FieldDefinitionId = field.FieldDefinitionId, FieldValueItemId = item ?? Guid.CreateVersion7(), FormRecordGroupId = entry, StringValue = value };

    private static FieldEncryptedValue Sealed(FieldDefinition field, string value, Guid? item = null) =>
        Crypto.Encrypt(new FieldEncryptionContext(Tenant, Record, field.FieldDefinitionId, item ?? Guid.CreateVersion7(), field.FieldDataType), value);

    private static FormRecordGroupRevision Entry(Guid id, long order, Guid? group = null) =>
        new() { FormRecordGroupRevisionId = Guid.CreateVersion7(), TenantId = Tenant, FormRecordId = Record, FormRecordGroupId = id, GroupDefinitionId = group ?? Group, GroupSortOrder = order };

    private static RestorePlanner.RestorePlan Plan(RestorePlanner.Snapshot current, RestorePlanner.Snapshot source, FormLayout? layout = null, IFieldEncryptionService? encryption = null) =>
        RestorePlanner.Build(current, source, layout ?? Layout(), Fields, Groups, new Dictionary<Guid, FieldOption>(), encryption ?? Crypto);

    private static CandidateBuilder.ApplyResult Apply(RestorePlanner.Snapshot current, RestorePlanner.RestorePlan plan, FormLayout? layout = null) =>
        CandidateBuilder.ApplyValuesDetailed(current.Values, plan.Values, layout ?? Layout(), Fields, [], null, Tenant, Record, DateTimeOffset.UtcNow, Actor, new Dictionary<Guid, FieldOption>(), Crypto,
            groups: new CandidateBuilder.GroupInput(Groups, current.Occurrences, plan.GroupWrites.Count == 0 ? null : plan.GroupWrites));

    private static string Value(IEnumerable<FieldValueRowBase> rows, FieldDefinition field, Guid? entry = null) =>
        rows.Where(r => r.FieldDefinitionId == field.FieldDefinitionId && r.FormRecordGroupId == entry).Select(r => r is FieldEncryptedValue e ? Crypto.Decrypt(e) : ((FieldStringValue)r).StringValue).SingleOrDefault() ?? "";

    [Fact]
    public void RestoreIsExactChangedAddedAndClearedFieldsAreAllPlanned()
    {
        var titleItem = Guid.CreateVersion7();
        var current = new RestorePlanner.Snapshot([Text(Title, "Current title", titleItem), Text(Extra, "only now")], []);
        var source = new RestorePlanner.Snapshot([Text(Title, "Original title", titleItem), Text(Notes, "original notes")], []);

        var plan = Plan(current, source);

        Assert.Equal(new Dictionary<string, string> { ["case_title"] = "Original title", ["case_notes"] = "original notes", ["legacy_code"] = "" }, plan.Values);
        Assert.Equal(RestorePlanner.Changes.Changed, plan.Fields.Single(f => f.FieldKey == "case_title").Change);
        Assert.Equal(RestorePlanner.Changes.Added, plan.Fields.Single(f => f.FieldKey == "case_notes").Change);
        var cleared = plan.Fields.Single(f => f.FieldKey == "legacy_code");
        Assert.Equal(RestorePlanner.Changes.Cleared, cleared.Change);
        Assert.False(cleared.InCurrentForm); // OD-12: flagged, still restored (cleared exactly)
        Assert.Equal(["legacy_code"], plan.ClearedFieldKeys);
        Assert.True(plan.HasChanges);
    }

    [Fact]
    public void ARestoredValueThatExistsOnlyInTheSourceGetsANewIdentityAndAnUpdatedOneKeepsTheCurrentItem()
    {
        var currentTitleItem = Guid.CreateVersion7();
        var sourceTitleItem = Guid.CreateVersion7(); // a value that was cleared and re-added since: different item ids
        var sourceNotesItem = Guid.CreateVersion7(); // removed since; an old share edge may still name this id (R-07)
        var current = new RestorePlanner.Snapshot([Text(Title, "Current title", currentTitleItem)], []);
        var source = new RestorePlanner.Snapshot([Text(Title, "Original title", sourceTitleItem), Text(Notes, "original notes", sourceNotesItem)], []);

        var applied = Apply(current, Plan(current, source));

        var title = applied.Rows.Single(r => r.FieldDefinitionId == Title.FieldDefinitionId);
        var notes = applied.Rows.Single(r => r.FieldDefinitionId == Notes.FieldDefinitionId);
        Assert.Equal(currentTitleItem, title.FieldValueItemId); // like any owner edit of an existing value
        Assert.NotEqual(sourceNotesItem, notes.FieldValueItemId); // never resurrected under the old id
        Assert.NotEqual(sourceTitleItem, title.FieldValueItemId);
        Assert.Equal("original notes", Value(applied.Rows, Notes));
    }

    [Fact]
    public void RepeatingSectionsAreRestoredInSourceOrderExistingEntriesKeepTheirIdAndSourceOnlyEntriesAreNew()
    {
        Guid kept = Guid.CreateVersion7(), removed = Guid.CreateVersion7(), sourceOnly = Guid.CreateVersion7();
        var current = new RestorePlanner.Snapshot([Text(Witness, "Kept (edited)", entry: kept), Text(Witness, "Added later", entry: removed)], [Entry(kept, 0), Entry(removed, 1)]);
        var source = new RestorePlanner.Snapshot([Text(Witness, "Original witness", entry: sourceOnly), Text(Witness, "Kept", entry: kept)], [Entry(sourceOnly, 0), Entry(kept, 1)]);

        var plan = Plan(current, source);
        var write = Assert.Single(plan.GroupWrites);
        Assert.Equal("witnesses", write.GroupKey);
        Assert.Equal([null, kept], write.Occurrences.Select(o => o.FormRecordGroupId)); // a source-only entry is never adopted by id
        var section = Assert.Single(plan.Sections);
        Assert.Equal((2, 2, 1, 1), (section.CurrentEntryCount, section.RestoredEntryCount, section.NewEntryCount, section.RemovedEntryCount));

        var applied = Apply(current, plan);
        Assert.Equal(2, applied.Occurrences.Count);
        var first = applied.Occurrences.OrderBy(o => o.GroupSortOrder).First();
        Assert.NotEqual(sourceOnly, first.FormRecordGroupId); // fresh identity
        Assert.NotEqual(removed, first.FormRecordGroupId);
        Assert.Equal("Original witness", Value(applied.Rows, Witness, first.FormRecordGroupId));
        Assert.Equal("Kept", Value(applied.Rows, Witness, kept));
        Assert.DoesNotContain(applied.Occurrences, o => o.FormRecordGroupId == removed);
        Assert.DoesNotContain(applied.Rows, r => r.FormRecordGroupId == removed);
    }

    [Fact]
    public void ValuesAreStoredUnderTheCurrentPolicyNeverByCopyingSourceCiphertext()
    {
        var item = Guid.CreateVersion7();
        var sourceSealed = Sealed(Secret, "ZETA-7741", item);
        var current = new RestorePlanner.Snapshot([Sealed(Secret, "changed", item)], []);
        var source = new RestorePlanner.Snapshot([sourceSealed, Text(Title, "plain then")], []);

        var plan = Plan(current, source);
        Assert.True(plan.Fields.Single(f => f.FieldKey == "informant").StoredEncrypted);
        Assert.Empty(plan.PlaintextAcknowledgementFieldKeys);

        var applied = Apply(current, plan);
        var restored = Assert.IsType<FieldEncryptedValue>(applied.Rows.Single(r => r.FieldDefinitionId == Secret.FieldDefinitionId));
        Assert.Equal("ZETA-7741", Crypto.Decrypt(restored));
        Assert.False(restored.EncryptionNonceBytes.SequenceEqual(sourceSealed.EncryptionNonceBytes)); // a fresh envelope, not the old one
        Assert.IsType<FieldStringValue>(applied.Rows.Single(r => r.FieldDefinitionId == Title.FieldDefinitionId)); // a Standard field stays as its current policy says
    }

    [Fact]
    public void AValueEncryptedInTheSourceForAFieldReleasedSinceNeedsAnExplicitAcknowledgement()
    {
        var current = new RestorePlanner.Snapshot([], []);
        var source = new RestorePlanner.Snapshot([Sealed(Released, "was encrypted")], []);

        var plan = Plan(current, source);

        Assert.Equal(["released_note"], plan.PlaintextAcknowledgementFieldKeys);
        var field = plan.Fields.Single(f => f.FieldKey == "released_note");
        Assert.True(field.PlaintextAcknowledgementRequired);
        Assert.False(field.StoredEncrypted);
    }

    [Fact]
    public void ASourceValueWhoseKeyIsUnavailableFailsTheWholeRestore()
    {
        var current = new RestorePlanner.Snapshot([Text(Title, "now")], []);
        var lost = Sealed(Secret, "old secret");
        var source = new RestorePlanner.Snapshot([Text(Title, "then"), lost], []);

        var failure = Assert.Throws<BusinessException>(() => Plan(current, source, encryption: new FakeEncryption(v => ReferenceEquals(v, lost))));
        Assert.Equal(("HISTORIC_KEY_UNAVAILABLE", 503), (failure.ErrorCode, failure.HttpStatusCode));
    }

    [Fact]
    public void ASectionNoLongerInTheFormThatDiffersCannotBeRestoredExactlyAndAnIdenticalOneIsFine()
    {
        var orphan = Guid.CreateVersion7();
        var current = new RestorePlanner.Snapshot([Text(Title, "now"), Text(OrphanField, "entry now", entry: orphan)], [Entry(orphan, 0, OrphanGroup)]);
        var differing = new RestorePlanner.Snapshot([Text(Title, "then"), Text(OrphanField, "entry then", entry: orphan)], [Entry(orphan, 0, OrphanGroup)]);
        var identical = new RestorePlanner.Snapshot([Text(Title, "then"), Text(OrphanField, "entry now", entry: orphan)], [Entry(orphan, 0, OrphanGroup)]);

        Assert.NotNull(Plan(current, differing).Unsupported);
        Assert.Null(Plan(current, identical).Unsupported);
    }

    [Fact]
    public void CurrentFormValidationRejectsTheWholeRestoreWhenAFieldIsNowRequired()
    {
        var current = new RestorePlanner.Snapshot([Text(Title, "now")], []);
        var source = new RestorePlanner.Snapshot([Text(Notes, "title was empty then")], []);
        var plan = Plan(current, source, Layout(titleRequired: true));

        var failure = Assert.Throws<BusinessException>(() => Apply(current, plan, Layout(titleRequired: true)));
        Assert.Equal("VALIDATION_FAILED", failure.ErrorCode);
        Assert.Contains("case_title", failure.FieldKeys);
    }

    [Fact]
    public void DocumentFieldsAreNeverPlannedAndAnIdenticalVersionChangesNothing()
    {
        var current = new RestorePlanner.Snapshot([Text(Title, "same")], []);
        var plan = Plan(current, new RestorePlanner.Snapshot([Text(Title, "same")], []));
        Assert.False(plan.HasChanges);
        Assert.DoesNotContain(plan.Fields, f => f.FieldKey == Documents.FieldKey);
    }
}
