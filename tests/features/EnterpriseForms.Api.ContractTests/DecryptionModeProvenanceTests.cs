using EnterpriseForms.Application.Features.Documents.Ports;
using EnterpriseForms.Application.Features.FormDefinitions.Layout;
using EnterpriseForms.Application.Features.FormRecords.Staging;
using EnterpriseForms.Application.Features.FormRecords.Values;
using EnterpriseForms.Domain.Features.FormDefinitions;
using EnterpriseForms.Domain.Features.FormRecords;
using EnterpriseForms.Infrastructure.Features.FormRecords;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// E3-M2c OD-M2c-02a §3, paths C and D: the creation and owner-update responses decrypt in self-sealed mode only because every
/// encrypted row they format is a fresh seal of the current operation — on the first attempt and on a retry or replay alike. These
/// tests prove that property where the rows are produced (<see cref="CandidateBuilder.ApplyValuesDetailedAsync"/>, the code both
/// handlers use; the handlers themselves run only in Stage B, which is not run here) and prove that stored or committed rows of an
/// earlier attempt are refused by the self-sealed step and open only on the verified path. Path E: PhaseC5SharedEditTests.
/// </summary>
public sealed class DecryptionModeProvenanceTests
{
    private static readonly Guid Tenant = Guid.Parse("019a5b2c-0d00-7000-8000-0000000000a1");
    private static readonly Guid Record = Guid.Parse("019a5b2c-0d00-7000-8000-0000000000a2");
    private static readonly Guid Actor = Guid.Parse("019a5b2c-0d00-7000-8000-0000000000a3");
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FieldDefinition Field(string key, string protection) => new()
    {
        FieldDefinitionId = Guid.CreateVersion7(), FieldKey = key, FieldDisplayName = key, FieldDataType = "String", FieldClassification = protection == "ApplicationEncrypted" ? "Confidential" : "Internal", FieldProtectionMode = protection, OptionSelectionMode = "Single",
    };

    private static LayoutElement Element(FieldDefinition field) =>
        new(LayoutElementKinds.Field, field.FieldDefinitionId, null, "text", field.FieldDisplayName, null, new LayoutPlacement(4, false), new LayoutValidation(false, null, null, null, null, null, null, null), null, null);

    private sealed class Form
    {
        public FieldDefinition Secret { get; } = Field("secret", "ApplicationEncrypted");
        public FieldDefinition Note { get; } = Field("note", "ApplicationEncrypted");
        public FieldDefinition Title { get; } = Field("title", "Standard");
        public EnvelopeV2KeyDouble Keys { get; } = new();
        public AesGcmFieldEncryptionService Encryption { get; }

        public Form() => Encryption = new AesGcmFieldEncryptionService(Keys);

        public FieldDefinition[] Fields => [Secret, Note, Title];

        public FormLayout Layout => new(2, "Test", [new LayoutSection("main", "Main", 4, Fields.Select(Element).ToList())]);

        public List<FieldValueRowBase> Apply(IReadOnlyList<FieldValueRowBase> current, Dictionary<string, string> values, bool creating) =>
            InProcessCompletion.Completed(CandidateBuilder.ApplyValuesDetailedAsync(current, values, Layout, Fields, [], null, Tenant, Record, Now, Actor, new Dictionary<Guid, FieldOption>(), Encryption, Ct, creating: creating)).Rows;

        public DecryptedFieldValues SelfSealed(IEnumerable<FieldValueRowBase> rows) => InProcessCompletion.Completed(DecryptedFieldValues.DecryptSelfSealedAsync(Encryption, rows, Ct));

        public DecryptedFieldValues VerifiedRead(IEnumerable<FieldValueRowBase> rows) => InProcessCompletion.Completed(DecryptedFieldValues.DecryptAsync(Encryption, rows, Ct));

        public static string Wire(IEnumerable<FieldValueRowBase> rows, FieldDefinition field, DecryptedFieldValues decrypted) => FieldValueCodec.Format(rows.Single(r => r.FieldDefinitionId == field.FieldDefinitionId), new Dictionary<Guid, FieldOption>(), decrypted);
    }

    /// <summary>What the store returns for committed rows: new instances (EF materialization), signed schema assigned by staging.</summary>
    private static List<FieldValueRowBase> AsStored(IEnumerable<FieldValueRowBase> rows) => rows.Select(r =>
    {
        FieldValueRowBase stored = r switch
        {
            FieldEncryptedValue e => new FieldEncryptedValue
            {
                FieldEncryptedValueId = Guid.CreateVersion7(), EncryptedValueBytes = e.EncryptedValueBytes.ToArray(), EncryptionNonceBytes = e.EncryptionNonceBytes.ToArray(), EncryptionAuthTagBytes = e.EncryptionAuthTagBytes.ToArray(),
                WrappedDataKeyBytes = e.WrappedDataKeyBytes.ToArray(), EncryptionAlgorithmName = e.EncryptionAlgorithmName, EncryptionKeyReference = e.EncryptionKeyReference, EncryptionEnvelopeVersion = e.EncryptionEnvelopeVersion, EncryptedLogicalType = e.EncryptedLogicalType,
            },
            FieldStringValue s => new FieldStringValue { FieldStringValueId = Guid.CreateVersion7(), StringValue = s.StringValue },
            _ => throw new InvalidOperationException("fixture covers string and encrypted rows"),
        };
        stored.TenantId = r.TenantId;
        stored.FormRecordId = r.FormRecordId;
        stored.FieldDefinitionId = r.FieldDefinitionId;
        stored.FieldValueItemId = r.FieldValueItemId;
        stored.CreatedOn = r.CreatedOn;
        stored.CreatedByUserId = r.CreatedByUserId;
        stored.RowSignatureSchemaVersion = 1;
        return stored;
    }).ToList();

    /// <summary>Every encrypted candidate row is a new instance with new ciphertext, never one of the stored rows (no substitution).</summary>
    private static void AssertFreshSeals(IReadOnlyList<FieldValueRowBase> candidate, IReadOnlyList<FieldValueRowBase> stored)
    {
        var storedEncrypted = stored.OfType<FieldEncryptedValue>().ToList();
        foreach (var row in candidate.OfType<FieldEncryptedValue>())
        {
            Assert.DoesNotContain(storedEncrypted, s => ReferenceEquals(s, row));
            Assert.DoesNotContain(storedEncrypted, s => s.EncryptedValueBytes.SequenceEqual(row.EncryptedValueBytes) || s.WrappedDataKeyBytes.SequenceEqual(row.WrappedDataKeyBytes));
            Assert.Equal(0, row.RowSignatureSchemaVersion); // not yet staged
        }
    }

    [Fact]
    public void PathCTheCreationResponseRowsAreThisRequestsOwnSealsOnTheFirstAttemptAndOnEveryRetry()
    {
        var form = new Form();
        var request = new Dictionary<string, string> { ["secret"] = "s-1", ["note"] = "n-1", ["title"] = "t-1" };

        var first = form.Apply([], request, creating: true);
        var firstAnswer = form.SelfSealed(first);
        Assert.Equal(("s-1", "n-1"), (Form.Wire(first, form.Secret, firstAnswer), Form.Wire(first, form.Note, firstAnswer)));

        // The first attempt's rows are staged and committed; a retry of the same request (the fingerprint proved it the same) builds
        // its candidate again from its own input, with no current rows: fresh seals, the same values, never the committed ones.
        var committed = AsStored(first);
        var retry = form.Apply([], request, creating: true);
        AssertFreshSeals(retry, committed);
        var retryAnswer = form.SelfSealed(retry);
        Assert.Equal(("s-1", "n-1"), (Form.Wire(retry, form.Secret, retryAnswer), Form.Wire(retry, form.Note, retryAnswer)));

        // Replay cannot route the committed rows through self-sealed mode; they open only on the verified path.
        Assert.Throws<InvalidOperationException>(() => form.SelfSealed(committed));
        Assert.Equal("s-1", Form.Wire(committed, form.Secret, form.VerifiedRead(committed)));
    }

    [Fact]
    public void PathDTheOwnerUpdateResponseRowsAreFreshSealsAlsoForCarriedValuesAndOnAnIdempotentReplay()
    {
        var form = new Form();
        var stored = AsStored(form.Apply([], new() { ["secret"] = "unchanged", ["note"] = "before", ["title"] = "t" }, creating: true));
        var unwraps = form.Keys.UnwrapCalls;

        // The edit decrypts the verified current rows (Mode A) for carry-forward, then re-seals every encrypted value it emits.
        var candidate = form.Apply(stored, new() { ["note"] = "after" }, creating: false);
        Assert.Equal(unwraps + 2, form.Keys.UnwrapCalls); // the two stored encrypted rows, through the verified reader
        AssertFreshSeals(candidate, stored);
        var answer = form.SelfSealed(candidate);
        Assert.Equal(("unchanged", "after"), (Form.Wire(candidate, form.Secret, answer), Form.Wire(candidate, form.Note, answer)));

        // Idempotent replay: the stager returns the accepted head without staging, and the handler formats the candidate it rebuilt
        // from the verified snapshot, which now holds the committed edit. Still only fresh seals of this request.
        var committed = AsStored(candidate);
        var replay = form.Apply(committed, new() { ["note"] = "after" }, creating: false);
        AssertFreshSeals(replay, committed);
        var replayAnswer = form.SelfSealed(replay);
        Assert.Equal(("unchanged", "after"), (Form.Wire(replay, form.Secret, replayAnswer), Form.Wire(replay, form.Note, replayAnswer)));

        // The stored rows of either revision can never be decrypted in self-sealed mode.
        Assert.Throws<InvalidOperationException>(() => form.SelfSealed(stored));
        Assert.Throws<InvalidOperationException>(() => form.SelfSealed(committed));
    }

    [Fact]
    public void TheCandidateBuilderReadsTheCurrentRowsOnlyThroughTheVerifiedReader()
    {
        var form = new Form();
        var fresh = form.Apply([], new() { ["secret"] = "x", ["note"] = "y", ["title"] = "z" }, creating: true);

        // Unstaged (schema 0) rows passed as the record's current content are not Authority-verified content: refused, nothing unwrapped.
        var unwraps = form.Keys.UnwrapCalls;
        var refused = Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => form.Apply(fresh, new() { ["note"] = "changed" }, creating: false));
        Assert.Equal(Application.Features.FormRecords.Encryption.DecryptionFailureReasons.UnsupportedEnvelope, Application.Features.FormRecords.Encryption.DecryptionFailure.Find(refused)?.ReasonCode);
        Assert.Equal(unwraps, form.Keys.UnwrapCalls);
    }

    [Fact]
    public void AProtectionSelfCheckDecryptsOnlyItsOwnSeal()
    {
        // Path A shape: ProtectAsync seals a plaintext row; the self-check formats exactly that envelope in self-sealed mode.
        var form = new Form();
        var plaintext = new FieldStringValue { FieldStringValueId = Guid.CreateVersion7(), TenantId = Tenant, FormRecordId = Record, FieldDefinitionId = form.Secret.FieldDefinitionId, FieldValueItemId = Guid.CreateVersion7(), StringValue = "to protect", CreatedOn = Now, CreatedByUserId = Actor };
        var options = new Dictionary<Guid, FieldOption>();
        var converted = InProcessCompletion.Completed(FieldValueCodec.ProtectAsync(plaintext, form.Secret, options, form.Encryption, Ct));
        Assert.Equal("to protect", InProcessCompletion.Completed(FieldValueCodec.FormatSelfSealedAsync(converted, options, form.Encryption, Ct)));
        Assert.Throws<InvalidOperationException>(() => InProcessCompletion.Completed(FieldValueCodec.FormatSelfSealedAsync(AsStored([converted])[0], options, form.Encryption, Ct)));
        Assert.Equal(EncryptionKeyPurpose.FieldValue.Token, ((FieldEncryptedValue)converted).EncryptionKeyReference.Split(':')[2]);
    }
}
