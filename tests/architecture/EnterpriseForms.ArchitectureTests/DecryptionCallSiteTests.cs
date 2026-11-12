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
/// Decrypting operations: the field and document encryption ports and their AES-GCM implementations; the codec overloads that
/// decrypt through the field port (<c>FieldValueCodec.Format</c> with an encryption service, <c>FieldValueCodec.FormatField</c>);
/// data-key unwrapping (key provider port and implementations); and any <c>System.Security.Cryptography</c> decryption
/// primitive (<c>Decrypt*</c>, <c>CreateDecryptor</c>).
/// </summary>
public sealed partial class DecryptionCallSiteTests
{
    private const string FieldPort = "EnterpriseForms.Application.Features.FormRecords.Encryption.IFieldEncryptionService";
    private const string FilePort = "EnterpriseForms.Application.Features.Documents.Ports.IFileEncryptionService";
    private const string KeyPort = "EnterpriseForms.Application.Features.Documents.Ports.IEncryptionKeyProvider";
    private const string FieldService = "EnterpriseForms.Infrastructure.Features.FormRecords.AesGcmFieldEncryptionService";
    private const string FileService = "EnterpriseForms.Infrastructure.Features.Documents.AesGcmFileEncryptionService";
    private const string Codec = "EnterpriseForms.Application.Features.FormRecords.Values.FieldValueCodec";
    private const string LocalKeys = "EnterpriseForms.Infrastructure.Features.Documents.LocalDevelopmentKeyProvider";
    private const string UnavailableKeys = "EnterpriseForms.Infrastructure.Features.Documents.UnavailableKeyProvider";

    /// <summary>The reviewed call sites ("caller -> operation") and why each is permitted.</summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        // ---- Business API, user-facing (every one after an Authority-verified read of the record) ----
        ["EnterpriseForms.Application.Features.Documents.Documents.DocumentHandlers.DownloadAsync -> IFileEncryptionService.DecryptAsync"] =
            "Document download: only an attachment of the Authority-verified, authorized projection (VerifiedRecordReader, ProtectedAction.Read); an earlier version only for a complete projection and only after VerifiedHistoricVersionAsync verifies that operation's manifest.",
        ["EnterpriseForms.Application.Features.FormRecords.Projection.VerifiedRecord.ToValueResponses -> FieldValueCodec.Format(row, options, encryption)"] =
            "Record read projection: formats only AuthorizedRows of a VerifyRecordProjection-verified read (partial readers: shared fields only, no repeating sections).",
        ["EnterpriseForms.Application.Features.FormRecords.Staging.CandidateBuilder.ApplyScope -> FieldValueCodec.Format(row, options, encryption)"] =
            "Edit carry-forward: compares current values with the edit. Reached with current rows only from complete (owner) verified reads with ProtectedAction.Edit (owner update, lifecycle submit validation, restore); record creation passes no current rows.",
        ["EnterpriseForms.Application.Features.FormRecords.SharedEdits.SharedFieldWriteBuilder.Build -> FieldValueCodec.FormatField"] =
            "Shared (contributor) edit: decrypts only ReadableRows of the verified partial projection (the editor's shared, readable fields), as rule inputs and edited fields.",
        ["EnterpriseForms.Application.Features.FormRecords.UpdateFormRecord.UpdateFormRecordHandler.OwnerUpdateAsync -> FieldValueCodec.Format(row, options, encryption)"] =
            "Owner update response: values of the candidate just committed for a complete (owner) verified ProtectedAction.Edit read.",
        ["EnterpriseForms.Application.Features.FormRecords.UpdateFormRecord.UpdateFormRecordHandler.SharedUpdateAsync -> FieldValueCodec.Format(row, options, encryption)"] =
            "Shared update response: released rows are restricted to the editor's readable items of the verified partial projection.",
        ["EnterpriseForms.Application.Features.FormRecords.CreateFormRecord.CreateFormRecordHandler.CreateCoreAsync -> FieldValueCodec.Format(row, options, encryption)"] =
            "Creation response: echoes the values the creating caller itself submitted in this request (sealed, committed, then formatted for the response).",
        ["EnterpriseForms.Application.Features.FormRecords.History.RestorePlanner.Build -> FieldValueCodec.FormatField"] =
            "Version restore planning: current and source snapshots are both complete verified reads (ProtectedAction.Edit; the source checked against the Authority's history entry) before planning.",
        ["EnterpriseForms.Application.Features.FormRecords.CreateFormRecord.CreationRequestFingerprint.Matches -> IFieldEncryptionService.Decrypt"] =
            "Creation idempotency: opens only the sealed HMAC key of this tenant's and record's creation attempt (no user content), compares in constant time, zeroes the key.",

        // ---- Worker, trusted internal operations (no plaintext released to a user) ----
        ["EnterpriseForms.Application.Features.FormDesigner.ProtectionMigration.FieldProtectionMigrationService.MigrateRecordAsync -> FieldValueCodec.Format(row, options, encryption)"] =
            "Approved field-protection migration (Authority reprotection command): decrypt/self-check of the committed head the Authority names; the candidate is committed only through the Authority's reprotection rules.",
        ["EnterpriseForms.Application.Features.FormDesigner.ProtectionMigration.FieldProtectionReconciliationService.DecryptsToTheFieldType -> FieldValueCodec.Format(row, options, encryption)"] =
            "Protection reconciliation (protection-verifier credential, Worker only): authenticates one ciphertext row and its logical type; the plaintext is discarded immediately and never logged.",

        // ---- The decrypting primitives themselves ----
        ["EnterpriseForms.Application.Features.FormRecords.Values.FieldValueCodec.Format -> IFieldEncryptionService.Decrypt"] =
            "The codec's decrypting overload: the single application path from a stored encrypted row to its wire value; its own call sites are listed above.",
        ["EnterpriseForms.Application.Features.FormRecords.Values.FieldValueCodec.FormatField -> FieldValueCodec.Format(row, options, encryption)"] =
            "Field-level wrapper over the decrypting overload; its own call sites are listed above.",
        ["EnterpriseForms.Infrastructure.Features.FormRecords.AesGcmFieldEncryptionService.Decrypt -> IEncryptionKeyProvider.UnwrapDataKey"] =
            "Field encryption implementation: unwraps the per-value DEK (zeroed after use).",
        ["EnterpriseForms.Infrastructure.Features.FormRecords.AesGcmFieldEncryptionService.Decrypt -> AesGcm.Decrypt"] =
            "Field encryption implementation: authenticated AES-256-GCM decryption with the v1 AAD.",
        ["EnterpriseForms.Infrastructure.Features.Documents.AesGcmFileEncryptionService.DecryptAsync -> IEncryptionKeyProvider.UnwrapDataKey"] =
            "Document encryption implementation: unwraps the per-version DEK (zeroed after use).",
        ["EnterpriseForms.Infrastructure.Features.Documents.AesGcmFileEncryptionService.DecryptAsync -> AesGcm.Decrypt"] =
            "Document encryption implementation: authenticated segment decryption with the v1 segment AAD.",
        ["EnterpriseForms.Infrastructure.Features.Documents.LocalDevelopmentKeyProvider.UnwrapDataKey -> AesGcm.Decrypt"] =
            "Development/Testing key provider: AES-GCM key unwrap (refused outside Development/Testing at startup, E0-1).",
    };

    [GeneratedRegex(@"^<+([^<>]+)>")]
    private static partial Regex GeneratedOrigin();

    private static string? DecryptingOperation(MethodReference method)
    {
        var type = method.DeclaringType.GetElementType().FullName;
        var name = method.Name;
        return (type, name) switch
        {
            (FieldPort, "Decrypt") or (FilePort, "DecryptAsync") or (FieldService, "Decrypt") or (FileService, "DecryptAsync") => Short(type) + "." + name,
            (Codec, "Format") when method.Parameters.Count == 3 => "FieldValueCodec.Format(row, options, encryption)",
            (Codec, "FormatField") => "FieldValueCodec.FormatField",
            (KeyPort or LocalKeys or UnavailableKeys, "UnwrapDataKey") => Short(type) + ".UnwrapDataKey",
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
                return encryption.Decrypt(value);
            };
    }

    [Fact]
    public void TheScanFindsAndAttributesACallSiteHiddenInAnAsyncLambda()
    {
        var found = DiscoveredCallSites([typeof(DecryptionCallSiteTests).Assembly.Location]);
        Assert.Contains("EnterpriseForms.ArchitectureTests.DecryptionCallSiteTests+UnreviewedCallSiteSample.Leak -> IFieldEncryptionService.Decrypt", found);
    }

    [Fact]
    public void EveryDecryptionCallSiteIsReviewedAndAllowListed()
    {
        var discovered = DiscoveredCallSites();
        Assert.Equal(Allowed.Keys.Order(StringComparer.Ordinal).ToList(), discovered.ToList());
        Assert.All(Allowed, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Value), entry.Key));
    }
}
