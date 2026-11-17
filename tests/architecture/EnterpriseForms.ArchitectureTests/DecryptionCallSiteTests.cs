using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace EnterpriseForms.ArchitectureTests;

/// <summary>
/// E0-5: every call site in product code that decrypts (or can decrypt) protected content is identified from the compiled IL
/// and must appear in the reviewed allow-list below, with the reason it is permitted. A new call site — including one inside a
/// lambda, local function or async state machine, which are attributed to the method that declares them — fails this test
/// until it is reviewed and added. Removing a call site also fails it, so the list stays exact.
///
/// Decrypting operations: the field and document encryption ports and their AES-GCM implementations; the paths that decrypt
/// through the field port (<c>FieldValueCodec.FormatAsync</c> and the E-1 decryption step <c>DecryptedFieldValues.DecryptAsync</c>;
/// the synchronous <c>Format</c>/<c>FormatField</c> overloads only read what such a step already decrypted); data-key unwrapping
/// (key provider port and implementations); and any <c>System.Security.Cryptography</c> decryption primitive (<c>Decrypt*</c>, <c>CreateDecryptor</c>).
/// </summary>
public sealed partial class DecryptionCallSiteTests
{
    private const string FieldPort = "EnterpriseForms.Application.Features.FormRecords.Encryption.IFieldEncryptionService";
    private const string FilePort = "EnterpriseForms.Application.Features.Documents.Ports.IFileEncryptionService";
    private const string KeyPort = "EnterpriseForms.Application.Features.Documents.Ports.IEncryptionKeyProvider";
    private const string FieldService = "EnterpriseForms.Infrastructure.Features.FormRecords.AesGcmFieldEncryptionService";
    private const string FileService = "EnterpriseForms.Infrastructure.Features.Documents.AesGcmFileEncryptionService";
    private const string Codec = "EnterpriseForms.Application.Features.FormRecords.Values.FieldValueCodec";
    private const string DecryptionStep = "EnterpriseForms.Application.Features.FormRecords.Values.DecryptedFieldValues";
    private const string LocalKeys = "EnterpriseForms.Infrastructure.Features.Documents.LocalDevelopmentKeyProvider";
    private const string UnavailableKeys = "EnterpriseForms.Infrastructure.Features.Documents.UnavailableKeyProvider";
    private const string FieldOpenerV2 = "EnterpriseForms.Infrastructure.Features.Documents.EnvelopeV2.FieldEnvelopeV2Opener";
    private const string DocumentCipherV2 = "EnterpriseForms.Infrastructure.Features.Documents.EnvelopeV2.DocumentEnvelopeV2Cipher";
    private const string FieldBatchV2 = "EnterpriseForms.Infrastructure.Features.Documents.EnvelopeV2.EnvelopeV2ProductReader/FieldBatch";
    private const string PreparedDocumentV2 = "EnterpriseForms.Infrastructure.Features.Documents.EnvelopeV2.EnvelopeV2ProductReader/PreparedDocument";

    /// <summary>The reviewed call sites ("caller -> operation") and why each is permitted.</summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        // ---- Business API, user-facing (every one after an Authority-verified read of the record) ----
        ["EnterpriseForms.Application.Features.Documents.Documents.DocumentHandlers.DownloadAsync -> IFileEncryptionService.DecryptVerifiedAsync"] =
            "Document download (E3-M2c verified mode, S-8 dispatch): only an attachment of the Authority-verified, authorized projection (VerifiedRecordReader, ProtectedAction.Read); an earlier version only for a complete projection and only after VerifiedHistoricVersionAsync verifies that operation's manifest.",
        ["EnterpriseForms.Application.Features.FormRecords.Projection.VerifiedRecord.ToValueResponsesAsync -> DecryptedFieldValues.DecryptAsync"] =
            "Record read projection: decrypts exactly the released AuthorizedRows of a VerifyRecordProjection-verified read (partial readers: shared fields only, no repeating sections).",
        ["EnterpriseForms.Application.Features.FormRecords.Staging.CandidateBuilder.ApplyValuesDetailedAsync -> DecryptedFieldValues.DecryptAsync"] =
            "Edit carry-forward: decrypts the current values of known fields to compare them with the edit. Reached with current rows only from complete (owner) verified reads with ProtectedAction.Edit (owner update, lifecycle submit validation, restore); record creation passes no current rows.",
        ["EnterpriseForms.Application.Features.FormRecords.SharedEdits.SharedFieldWriteBuilder.BuildAsync -> DecryptedFieldValues.DecryptAsync"] =
            "Shared (contributor) edit: decrypts lazily, per field, only ReadableRows of the verified partial projection that the edit reads (edited fields and rule inputs), after every refusal check.",
        ["EnterpriseForms.Application.Features.FormRecords.UpdateFormRecord.UpdateFormRecordHandler.OwnerUpdateAsync -> DecryptedFieldValues.DecryptSelfSealedAsync"] =
            "OD-M2c-02a path D (self-sealed): owner update response, also on an idempotent replay. Every encrypted candidate row is a fresh seal of this operation (CandidateBuilder re-creates each value as a typed row and ProtectAsync seals it); no stored ciphertext is in the candidate. Enforced at run time by the self-sealed registry.",
        ["EnterpriseForms.Application.Features.FormRecords.UpdateFormRecord.UpdateFormRecordHandler.DecryptSharedResponseAsync -> DecryptedFieldValues.DecryptSelfSealedAsync"] =
            "OD-M2c-02a path E (self-sealed part): shared update response, also on an idempotent replay; only the builder's EditedRows (this request's fresh seals), restricted to the editor's readable items.",
        ["EnterpriseForms.Application.Features.FormRecords.UpdateFormRecord.UpdateFormRecordHandler.DecryptSharedResponseAsync -> DecryptedFieldValues.DecryptAsync"] =
            "OD-M2c-02a path E (verified part): every other released row is a byte-identical copy of a row of the Authority-verified partial projection (stored ciphertext), restricted to the editor's readable items; verified mode.",
        ["EnterpriseForms.Application.Features.FormRecords.CreateFormRecord.CreateFormRecordHandler.CreateCoreAsync -> DecryptedFieldValues.DecryptSelfSealedAsync"] =
            "OD-M2c-02a path C (self-sealed): creation response, first attempt and every retry or replay. candidateValues are always this request's own fresh seals (no current rows); an earlier attempt's staged or committed rows are never formatted (a head past revision 1 returns the verified read).",
        ["EnterpriseForms.Application.Features.FormRecords.History.RestorePlanner.DecryptAsync -> DecryptedFieldValues.DecryptAsync"] =
            "Version restore planning: the restorable rows of the current and source snapshots, both complete verified reads (ProtectedAction.Edit; the source checked against the Authority's history entry).",
        ["EnterpriseForms.Application.Features.FormRecords.CreateFormRecord.CreationRequestFingerprint.MatchesAsync -> IFieldEncryptionService.OpenCreationFingerprintKeyAsync"] =
            "R-M2c-1 (closure): creation idempotency through the narrow fingerprint-key operation (no longer the self-sealed API): opens only the sealed HMAC key of this tenant's and record's creation attempt (unsigned JSON sealed by this business host; IdempotencyProtection purpose, no user content, never released), compares in constant time, zeroes the key.",

        // ---- Worker, trusted internal operations (no plaintext released to a user) ----
        ["EnterpriseForms.Application.Features.FormDesigner.ProtectionMigration.FieldProtectionMigrationService.MigrateRecordAsync -> FieldValueCodec.FormatAsync"] =
            "Approved field-protection migration (Authority reprotection command), Decrypt direction: stored committed rows of the head the Authority names (verified mode, S-8); the candidate is committed only through the Authority's reprotection rules, which bind the current snapshot to the accepted head.",
        ["EnterpriseForms.Application.Features.FormDesigner.ProtectionMigration.FieldProtectionMigrationService.MigrateRecordAsync -> FieldValueCodec.FormatSelfSealedAsync"] =
            "OD-M2c-02a path A (self-sealed): Encrypt direction self-check, decrypts only the envelope ProtectAsync just sealed from the record's plaintext row in this operation.",
        ["EnterpriseForms.Application.Features.FormDesigner.ProtectionMigration.FieldProtectionReconciliationService.DecryptsToTheFieldTypeAsync -> FieldValueCodec.FormatAsync"] =
            "Protection reconciliation (protection-verifier credential, Worker only): authenticates one ciphertext row and its logical type; the plaintext is discarded immediately and never logged.",

        // ---- The decrypting primitives themselves ----
        ["EnterpriseForms.Application.Features.FormRecords.Values.DecryptedFieldValues.DecryptAsync -> IFieldEncryptionService.DecryptVerifiedAsync"] =
            "E-1 decryption step, Mode A (verified): one verified batch over exactly the rows its caller passes (one organization, FieldValue purpose); its own call sites are listed above.",
        ["EnterpriseForms.Application.Features.FormRecords.Values.DecryptedFieldValues.DecryptSelfSealedAsync -> IFieldEncryptionService.DecryptAsync"] =
            "E-1 decryption step, Mode B (self-sealed): refuses any row not freshly sealed by ProtectAsync in this process, then the single-value v1 API; its own call sites are pinned to paths C, D, E.",
        ["EnterpriseForms.Application.Features.FormRecords.Values.FieldValueCodec.FormatAsync -> IFieldEncryptionService.DecryptVerifiedAsync"] =
            "The codec's decrypting overload, Mode A (verified, one-row batch); its own call sites are listed above.",
        ["EnterpriseForms.Application.Features.FormRecords.Values.FieldValueCodec.FormatSelfSealedAsync -> IFieldEncryptionService.DecryptAsync"] =
            "The codec's decrypting overload, Mode B (self-sealed registry check, then the single-value v1 API); its own call site is pinned to path A.",
        ["EnterpriseForms.Infrastructure.Features.FormRecords.AesGcmFieldEncryptionService.OpenV1Async -> IEncryptionKeyProvider.UnwrapDataKeyAsync"] =
            "Field encryption implementation, v1 core shared by both modes after their own admission checks: unwraps the per-value DEK with the value's tenant and the requested purpose (zeroed after use).",
        ["EnterpriseForms.Infrastructure.Features.FormRecords.AesGcmFieldEncryptionService.OpenV1Async -> AesGcm.Decrypt"] =
            "Field encryption implementation, v1 core: authenticated AES-256-GCM decryption with the v1 AAD.",
        ["EnterpriseForms.Infrastructure.Features.FormRecords.AesGcmFieldEncryptionService.DecryptVerifiedAsync -> EnvelopeV2ProductReader.FieldBatch.OpenAsync"] =
            "E3-M2c verified field reader: schema-2/envelope-2 rows only, after every row of the batch passed the S-8 pair check and full v2 validation; one opener call (one unwrap per data-key group).",
        ["EnterpriseForms.Infrastructure.Features.Documents.AesGcmFileEncryptionService.DecryptVerifiedAsync -> AesGcmFileEncryptionService.DecryptAsync"] =
            "E3-M2c verified document reader, S-8 (schema 1, envelope 1, no v2 column): the unchanged v1 decryptor.",
        ["EnterpriseForms.Infrastructure.Features.Documents.AesGcmFileEncryptionService.DecryptVerifiedAsync -> EnvelopeV2ProductReader.PreparedDocument.DecryptAsync"] =
            "E3-M2c verified document reader, S-8 (schema 2, envelope 2, every v2 column): the M1 document decryptor after full envelope and identity validation.",
        ["EnterpriseForms.Infrastructure.Features.Documents.EnvelopeV2.EnvelopeV2ProductReader+FieldBatch.OpenAsync -> FieldEnvelopeV2Opener.OpenAsync"] =
            "E3-M2c product gateway into the v2 core (opener only): the validated requests of one organization, FieldValue purpose.",
        ["EnterpriseForms.Infrastructure.Features.Documents.EnvelopeV2.EnvelopeV2ProductReader+PreparedDocument.DecryptAsync -> DocumentEnvelopeV2Cipher.DecryptAsync"] =
            "E3-M2c product gateway into the v2 core (document decryptor only): the validated envelope and the verified version identity.",
        ["EnterpriseForms.Infrastructure.Features.Documents.AesGcmFileEncryptionService.DecryptAsync -> IEncryptionKeyProvider.UnwrapDataKeyAsync"] =
            "Document encryption implementation: unwraps the per-version DEK with the document's tenant and the Document purpose (zeroed after use).",
        ["EnterpriseForms.Infrastructure.Features.Documents.AesGcmFileEncryptionService.DecryptAsync -> AesGcm.Decrypt"] =
            "Document encryption implementation: authenticated segment decryption with the v1 segment AAD.",
        ["EnterpriseForms.Infrastructure.Features.Documents.LocalDevelopmentKeyProvider.Open -> AesGcm.Decrypt"] =
            "Development/Testing keyring: AES-GCM key unwrap of a scoped or legacy wrapped DEK (refused outside Development/Testing at startup, E0-1).",

        // ---- E3-M1 Envelope v2 core: internal, NOT reachable from any product path (EnvelopeV2IsolationTests). Since E3-M2c it is reached
        // only through EnvelopeV2ProductReader (verified readers; dark: no product writer produces v2 rows). ----
        ["EnterpriseForms.Infrastructure.Features.Documents.EnvelopeV2.FieldEnvelopeV2Opener.OpenAsync -> IEncryptionKeyProvider.UnwrapDataKeyAsync"] =
            "E3-M1 v2 field opener: unwraps one seal-batch seed per data-key group, only after every request's organization, purpose and expected item identity and the group's consistency were checked; zeroed after use. Reached only through EnvelopeV2ProductReader.FieldBatch (E3-M2c).",
        ["EnterpriseForms.Infrastructure.Features.Documents.EnvelopeV2.FieldEnvelopeV2Opener.Decrypt -> AesGcm.Decrypt"] =
            "E3-M1 v2 field opener: AES-256-GCM with the HKDF-derived single-use item key and AAD = H ‖ C_field, after the key commitment matched. Reached only through EnvelopeV2ProductReader.FieldBatch (E3-M2c).",
        ["EnterpriseForms.Infrastructure.Features.Documents.EnvelopeV2.DocumentEnvelopeV2Cipher.DecryptAsync -> IEncryptionKeyProvider.UnwrapDataKeyAsync"] =
            "E3-M1 v2 document cipher: unwraps the version's seed after the organization and document identity were checked; zeroed after the document key is derived. Reached only through EnvelopeV2ProductReader.PreparedDocument (E3-M2c).",
        ["EnterpriseForms.Infrastructure.Features.Documents.EnvelopeV2.DocumentEnvelopeV2Cipher.DecryptAsync -> AesGcm.Decrypt"] =
            "E3-M1 v2 document cipher: segment AES-256-GCM with AAD = H ‖ C_doc ‖ segment ‖ final, after the key commitment matched. Reached only through EnvelopeV2ProductReader.PreparedDocument (E3-M2c).",
    };

    [GeneratedRegex(@"^<+([^<>]+)>")]
    private static partial Regex GeneratedOrigin();

    private static string? DecryptingOperation(MethodReference method)
    {
        var type = method.DeclaringType.GetElementType().FullName;
        var name = method.Name;
        return (type, name) switch
        {
            (FieldPort or FilePort or FieldService or FileService, "DecryptAsync" or "DecryptVerifiedAsync") => Short(type) + "." + name,
            (FieldPort or FieldService, "OpenCreationFingerprintKeyAsync") => Short(type) + "." + name,
            (Codec, "FormatAsync" or "FormatSelfSealedAsync") => "FieldValueCodec." + name,
            (DecryptionStep, "DecryptAsync" or "DecryptSelfSealedAsync") => "DecryptedFieldValues." + name,
            (FieldBatchV2, "OpenAsync") => "EnvelopeV2ProductReader.FieldBatch.OpenAsync",
            (PreparedDocumentV2, "DecryptAsync") => "EnvelopeV2ProductReader.PreparedDocument.DecryptAsync",
            (KeyPort or LocalKeys or UnavailableKeys, "UnwrapDataKeyAsync") => Short(type) + ".UnwrapDataKeyAsync",
            (FieldOpenerV2, "OpenAsync") or (DocumentCipherV2, "DecryptAsync") => Short(type) + "." + name,
            _ when type.StartsWith("System.Security.Cryptography.", StringComparison.Ordinal) && (name.StartsWith("Decrypt", StringComparison.Ordinal) || name == "CreateDecryptor") => Short(type) + "." + name,
            _ => null,
        };
    }

    private static string Short(string fullName) => fullName[(fullName.LastIndexOf('.') + 1)..];

    /// <summary>The user-written method a compiled method belongs to (lambdas, local functions, async/iterator state machines).</summary>
    private static string Caller(MethodDefinition method)
    {
        var type = method.DeclaringType;
        var name = method.Name;
        while (type.IsNested && type.Name.StartsWith('<'))
        {
            if (!name.StartsWith('<') && GeneratedOrigin().Match(type.Name) is { Success: true } fromType)
            {
                name = fromType.Groups[1].Value; // MoveNext of <Method>d__N
            }

            type = type.DeclaringType;
        }

        if (GeneratedOrigin().Match(name) is { Success: true } fromMethod)
        {
            name = fromMethod.Groups[1].Value; // <Method>b__N_M, <Method>g__Local|N_M
        }

        return type.FullName.Replace('/', '+') + "." + name;
    }

    private static IEnumerable<string> ProductAssemblies() =>
        Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll")
            .Where(path =>
            {
                var name = Path.GetFileNameWithoutExtension(path);
                return (name.StartsWith("EnterpriseForms.", StringComparison.Ordinal) || name.StartsWith("AuthorizationAuthority.", StringComparison.Ordinal))
                       && !name.EndsWith("Tests", StringComparison.Ordinal);
            })
            .Order(StringComparer.Ordinal);

    private static SortedSet<string> DiscoveredCallSites() => DiscoveredCallSites(ProductAssemblies());

    private static SortedSet<string> DiscoveredCallSites(IEnumerable<string> assemblyPaths)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var path in assemblyPaths)
        {
            using var assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { ReadSymbols = false });
            foreach (var type in assembly.MainModule.GetTypes())
            {
                foreach (var method in type.Methods.Where(m => m.HasBody))
                {
                    foreach (var instruction in method.Body.Instructions)
                    {
                        if (instruction.Operand is MethodReference target && instruction.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj or Code.Ldftn or Code.Ldvirtftn
                            && DecryptingOperation(target) is { } operation)
                        {
                            found.Add(Caller(method) + " -> " + operation);
                        }
                    }
                }
            }
        }

        return found;
    }

    [Fact]
    public void TheScanCoversTheProductAssemblies()
    {
        var names = ProductAssemblies().Select(Path.GetFileNameWithoutExtension).ToList();
        foreach (var expected in new[] { "EnterpriseForms.Application", "EnterpriseForms.Infrastructure", "EnterpriseForms.Api", "EnterpriseForms.Worker", "EnterpriseForms.Domain", "AuthorizationAuthority.Application", "AuthorizationAuthority.Infrastructure", "AuthorizationAuthority.Api" })
        {
            Assert.Contains(expected, names);
        }
    }

    /// <summary>Not product code: a deliberately unreviewed call site, used only to prove the scan finds and attributes one.</summary>
    private static class UnreviewedCallSiteSample
    {
        public static Func<Task<string>> Leak(EnterpriseForms.Application.Features.FormRecords.Encryption.IFieldEncryptionService encryption, EnterpriseForms.Domain.Features.FormRecords.FieldEncryptedValue value) =>
            async () =>
            {
                await Task.Yield();
                return await encryption.DecryptAsync(value, EnterpriseForms.Application.Features.Documents.Ports.EncryptionKeyPurpose.FieldValue, CancellationToken.None);
            };
    }

    [Fact]
    public void TheScanFindsAndAttributesACallSiteHiddenInAnAsyncLambda()
    {
        var found = DiscoveredCallSites([typeof(DecryptionCallSiteTests).Assembly.Location]);
        Assert.Contains("EnterpriseForms.ArchitectureTests.DecryptionCallSiteTests+UnreviewedCallSiteSample.Leak -> IFieldEncryptionService.DecryptAsync", found);
    }

    [Fact]
    public void EveryDecryptionCallSiteIsReviewedAndAllowListed()
    {
        var discovered = DiscoveredCallSites();
        Assert.Equal(Allowed.Keys.Order(StringComparer.Ordinal).ToList(), discovered.ToList());
        Assert.All(Allowed, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Value), entry.Key));
    }
}
