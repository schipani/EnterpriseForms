using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace EnterpriseForms.ArchitectureTests;

/// <summary>
/// E3-M2c OD-M2c-02a: the two field-decryption modes are kept apart at the call-site level (an additional safeguard; the run-time
/// checks of both modes and the self-sealed registry are the primary enforcement, proven by the contract tests).
/// (1) Product callers of a self-sealed entry point are exactly the reviewed inventory A–E. (2) A method that reads stored record
/// content (verified read, staged-row read) never also calls a self-sealed entry point, except path A, whose stored rows go only to
/// the verified reader. (3) The reviewed verified readers call a verified entry point and no self-sealed one. (4) The Envelope v1
/// document decryptor has no product caller other than the verified document dispatcher.
/// </summary>
public sealed partial class DecryptionCallSiteTests
{
    private const string VerifiedReader = "EnterpriseForms.Application.Features.FormRecords.Projection.VerifiedRecordReader";
    private const string Stager = "EnterpriseForms.Application.Features.FormRecords.Staging.RecordCandidateStager";

    /// <summary>Mode B entry points: the single-value v1 API (port and implementation) and the two named application wrappers.</summary>
    private static readonly HashSet<string> SelfSealedOperations = new(StringComparer.Ordinal)
    {
        "IFieldEncryptionService.DecryptAsync", "AesGcmFieldEncryptionService.DecryptAsync", "DecryptedFieldValues.DecryptSelfSealedAsync", "FieldValueCodec.FormatSelfSealedAsync",
    };

    /// <summary>Mode A entry points.</summary>
    private static readonly HashSet<string> VerifiedOperations = new(StringComparer.Ordinal)
    {
        "IFieldEncryptionService.DecryptVerifiedAsync", "AesGcmFieldEncryptionService.DecryptVerifiedAsync", "DecryptedFieldValues.DecryptAsync", "FieldValueCodec.FormatAsync",
        "IFileEncryptionService.DecryptVerifiedAsync", "AesGcmFileEncryptionService.DecryptVerifiedAsync",
    };

    /// <summary>The wrappers themselves call the self-sealed v1 API; they are the primitives, not callers.</summary>
    private static readonly HashSet<string> SelfSealedPrimitives = new(StringComparer.Ordinal)
    {
        "EnterpriseForms.Application.Features.FormRecords.Values.DecryptedFieldValues.DecryptSelfSealedAsync",
        "EnterpriseForms.Application.Features.FormRecords.Values.FieldValueCodec.FormatSelfSealedAsync",
    };

    /// <summary>The owner-approved self-sealed inventory (OD-M2c-02a §3), one entry per product call site.</summary>
    private static readonly Dictionary<string, string> ApprovedSelfSealedCallSites = new(StringComparer.Ordinal)
    {
        ["EnterpriseForms.Application.Features.FormDesigner.ProtectionMigration.FieldProtectionMigrationService.MigrateRecordAsync -> FieldValueCodec.FormatSelfSealedAsync"] = "A",
        ["EnterpriseForms.Application.Features.FormRecords.CreateFormRecord.CreateFormRecordHandler.CreateCoreAsync -> DecryptedFieldValues.DecryptSelfSealedAsync"] = "C",
        ["EnterpriseForms.Application.Features.FormRecords.UpdateFormRecord.UpdateFormRecordHandler.OwnerUpdateAsync -> DecryptedFieldValues.DecryptSelfSealedAsync"] = "D",
        ["EnterpriseForms.Application.Features.FormRecords.UpdateFormRecord.UpdateFormRecordHandler.DecryptSharedResponseAsync -> DecryptedFieldValues.DecryptSelfSealedAsync"] = "E",
    };

    /// <summary>Methods that read stored record content and may nevertheless call a self-sealed entry point (path A: its stored rows go to the verified reader; only its own fresh seal is self-sealed).</summary>
    private static readonly HashSet<string> StoredReadersWithApprovedSelfSealing = new(StringComparer.Ordinal)
    {
        "EnterpriseForms.Application.Features.FormDesigner.ProtectionMigration.FieldProtectionMigrationService.MigrateRecordAsync",
    };

    /// <summary>The reviewed readers of Authority-verified (or Authority-bound stored) content: verified mode only.</summary>
    private static readonly string[] VerifiedStorageReaders =
    [
        "EnterpriseForms.Application.Features.FormRecords.Projection.VerifiedRecord.ToValueResponsesAsync",
        "EnterpriseForms.Application.Features.FormRecords.Staging.CandidateBuilder.ApplyValuesDetailedAsync",
        "EnterpriseForms.Application.Features.FormRecords.SharedEdits.SharedFieldWriteBuilder.BuildAsync",
        "EnterpriseForms.Application.Features.FormRecords.History.RestorePlanner.DecryptAsync",
        "EnterpriseForms.Application.Features.FormDesigner.ProtectionMigration.FieldProtectionReconciliationService.DecryptsToTheFieldTypeAsync",
        "EnterpriseForms.Application.Features.Documents.Documents.DocumentHandlers.DownloadAsync",
    ];

    private static string? StoredContentRead(MethodReference method)
    {
        var type = method.DeclaringType.GetElementType().FullName;
        return (type, method.Name) switch
        {
            (VerifiedReader, "ReadAsync") => "VerifiedRecordReader.ReadAsync",
            (Stager, "ReadStagedAsync" or "ReadStagedBatchAsync") => "RecordCandidateStager." + method.Name,
            _ => null,
        };
    }

    private static SortedSet<string> Calls(IEnumerable<string> assemblyPaths, Func<MethodReference, string?> classify)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var path in assemblyPaths)
        {
            using var assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { ReadSymbols = false });
            foreach (var method in assembly.MainModule.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody))
            {
                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.Operand is MethodReference target && instruction.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj or Code.Ldftn or Code.Ldvirtftn
                        && classify(target) is { } operation)
                    {
                        found.Add(Caller(method) + " -> " + operation);
                    }
                }
            }
        }

        return found;
    }

    private static (string Caller, string Operation) Split(string callSite)
    {
        var at = callSite.IndexOf(" -> ", StringComparison.Ordinal);
        return (callSite[..at], callSite[(at + 4)..]);
    }

    /// <summary>Self-sealed call sites outside the primitives, from the given assemblies.</summary>
    private static List<string> SelfSealedCallSites(IEnumerable<string> assemblyPaths) =>
        Calls(assemblyPaths, DecryptingOperation).Where(c => SelfSealedOperations.Contains(Split(c).Operation) && !SelfSealedPrimitives.Contains(Split(c).Caller)).ToList();

    /// <summary>Callers (from the given assemblies) that read stored content and also call a self-sealed entry point, minus the approved ones.</summary>
    private static List<string> StoredReadersThatSelfSeal(IEnumerable<string> assemblyPaths)
    {
        var paths = assemblyPaths.ToList();
        var readers = Calls(paths, StoredContentRead).Select(c => Split(c).Caller).ToHashSet(StringComparer.Ordinal);
        return SelfSealedCallSites(paths).Select(c => Split(c).Caller).Where(readers.Contains).Where(c => !StoredReadersWithApprovedSelfSealing.Contains(c)).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>R-M2c-1: the narrow stored-fingerprint-key operation and its one approved caller.</summary>
    private const string FingerprintCaller = "EnterpriseForms.Application.Features.FormRecords.CreateFormRecord.CreationRequestFingerprint.MatchesAsync";

    private static List<(string Caller, string Operation)> FingerprintCallSites(IEnumerable<string> assemblyPaths) =>
        Calls(assemblyPaths, DecryptingOperation).Select(Split).Where(c => c.Operation.EndsWith(".OpenCreationFingerprintKeyAsync", StringComparison.Ordinal)).ToList();

    /// <summary>Not product code: deliberately unapproved self-sealed callers, used only to prove the rules find them.</summary>
    private static class UnapprovedSelfSealedSample
    {
        public static Task<EnterpriseForms.Application.Features.FormRecords.Values.DecryptedFieldValues> Leak(EnterpriseForms.Application.Features.FormRecords.Encryption.IFieldEncryptionService encryption, IReadOnlyList<EnterpriseForms.Domain.Features.FormRecords.ISignedContentRow> rows) =>
            EnterpriseForms.Application.Features.FormRecords.Values.DecryptedFieldValues.DecryptSelfSealedAsync(encryption, rows, CancellationToken.None);

        /// <summary>A stored read whose rows are then decrypted in self-sealed mode: exactly what OD-M2c-02a forbids.</summary>
        public static async Task<object> ReadThenSelfSeal(EnterpriseForms.Application.Features.FormRecords.Staging.RecordCandidateStager stager, EnterpriseForms.Application.Features.FormRecords.Encryption.IFieldEncryptionService encryption, Guid record, Guid operation)
        {
            var staged = await stager.ReadStagedAsync(record, operation, CancellationToken.None);
            return await EnterpriseForms.Application.Features.FormRecords.Values.DecryptedFieldValues.DecryptSelfSealedAsync(encryption, staged.Rows, CancellationToken.None);
        }

        /// <summary>A second caller of the fingerprint exception: forbidden.</summary>
        public static Task<byte[]> OpenSomeKey(EnterpriseForms.Application.Features.FormRecords.Encryption.IFieldEncryptionService encryption, EnterpriseForms.Application.Features.FormRecords.Encryption.CreationFingerprintKeyEnvelope envelope) =>
            encryption.OpenCreationFingerprintKeyAsync(envelope, CancellationToken.None);

        /// <summary>A product-style caller of the raw single-value API outside the two FieldValue wrappers: forbidden.</summary>
        public static Task<string> RawDecrypt(EnterpriseForms.Application.Features.FormRecords.Encryption.IFieldEncryptionService encryption, EnterpriseForms.Domain.Features.FormRecords.FieldEncryptedValue value) =>
            encryption.DecryptAsync(value, EnterpriseForms.Application.Features.Documents.Ports.EncryptionKeyPurpose.IdempotencyProtection, CancellationToken.None);
    }

    [Fact]
    public void OnlyTheCreationFingerprintCheckOpensStoredFingerprintKeys()
    {
        Assert.Equal([(FingerprintCaller, "IFieldEncryptionService.OpenCreationFingerprintKeyAsync")], FingerprintCallSites(ProductAssemblies()));
        var sample = FingerprintCallSites([typeof(DecryptionCallSiteTests).Assembly.Location]);
        Assert.Contains(("EnterpriseForms.ArchitectureTests.DecryptionCallSiteTests+UnapprovedSelfSealedSample.OpenSomeKey", "IFieldEncryptionService.OpenCreationFingerprintKeyAsync"), sample); // positive control
    }

    [Fact]
    public void TheRawSingleValueApiIsReachedOnlyThroughTheFieldValueWrappers()
    {
        static List<string> RawCallers(IEnumerable<string> paths) =>
            Calls(paths, DecryptingOperation).Select(Split).Where(c => c.Operation is "IFieldEncryptionService.DecryptAsync" or "AesGcmFieldEncryptionService.DecryptAsync").Select(c => c.Caller).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(SelfSealedPrimitives.Order(StringComparer.Ordinal).ToList(), RawCallers(ProductAssemblies())); // no IdempotencyProtection or other-purpose product use
        Assert.Contains("EnterpriseForms.ArchitectureTests.DecryptionCallSiteTests+UnapprovedSelfSealedSample.RawDecrypt", RawCallers([typeof(DecryptionCallSiteTests).Assembly.Location])); // positive control
    }

    [Fact]
    public void SelfSealedCallersArePinnedToTheReviewedInventory()
    {
        var discovered = SelfSealedCallSites(ProductAssemblies());
        Assert.Equal(ApprovedSelfSealedCallSites.Keys.Order(StringComparer.Ordinal).ToList(), discovered.Order(StringComparer.Ordinal).ToList());
        Assert.Equal(["A", "C", "D", "E"], ApprovedSelfSealedCallSites.Values.Order(StringComparer.Ordinal).ToList()); // B moved to the fingerprint operation (R-M2c-1)
    }

    [Fact]
    public void AnUnapprovedSelfSealedCallerIsDetected()
    {
        var testAssembly = new[] { typeof(DecryptionCallSiteTests).Assembly.Location };
        var found = SelfSealedCallSites(testAssembly);
        Assert.Contains("EnterpriseForms.ArchitectureTests.DecryptionCallSiteTests+UnapprovedSelfSealedSample.Leak -> DecryptedFieldValues.DecryptSelfSealedAsync", found);
        Assert.DoesNotContain(found, c => ApprovedSelfSealedCallSites.ContainsKey(c));
        Assert.Contains("EnterpriseForms.ArchitectureTests.DecryptionCallSiteTests+UnapprovedSelfSealedSample.ReadThenSelfSeal", StoredReadersThatSelfSeal(testAssembly));
    }

    [Fact]
    public void NoStoredContentReaderSwitchesToSelfSealedMode()
    {
        Assert.Empty(StoredReadersThatSelfSeal(ProductAssemblies()));
    }

    [Fact]
    public void TheVerifiedStorageReadersUseTheVerifiedReaderOnly()
    {
        var byCaller = Calls(ProductAssemblies(), DecryptingOperation).Select(Split).GroupBy(c => c.Caller, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Select(c => c.Operation).ToList(), StringComparer.Ordinal);
        foreach (var reader in VerifiedStorageReaders)
        {
            Assert.True(byCaller.TryGetValue(reader, out var operations), reader + " no longer decrypts (update the reviewed list)");
            Assert.Contains(operations, VerifiedOperations.Contains);
            Assert.DoesNotContain(operations, SelfSealedOperations.Contains);
        }
    }

    [Fact]
    public void OnlyTheVerifiedDocumentDispatcherReachesTheV1DocumentDecryptor()
    {
        var callers = Calls(ProductAssemblies(), DecryptingOperation).Select(Split)
            .Where(c => c.Operation is "IFileEncryptionService.DecryptAsync" or "AesGcmFileEncryptionService.DecryptAsync").Select(c => c.Caller).ToList();
        Assert.Equal(["EnterpriseForms.Infrastructure.Features.Documents.AesGcmFileEncryptionService.DecryptVerifiedAsync"], callers);
    }
}
