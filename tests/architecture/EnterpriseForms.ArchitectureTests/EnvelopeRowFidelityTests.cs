using System.Reflection;
using System.Text.RegularExpressions;
using EnterpriseForms.Application.Features.FormRecords.Staging;
using EnterpriseForms.Domain.Features.Documents;
using EnterpriseForms.Domain.Features.FormRecords;
using EnterpriseForms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace EnterpriseForms.ArchitectureTests;

/// <summary>
/// E3-M2b-2 clone fidelity and schema selection (contract §4.4 S-1/S-3, §8; M2-ARCH-01):
/// (1) every product construction site of the two envelope row types is an exact reviewed list (seal, clone, processed copy), so a
/// new copy path cannot appear without review; (2) the two clone helpers copy every EF-mapped property of the row except the
/// enumerated per-operation binding that the stager re-assigns, so a copy can never drop Envelope v2 metadata or the schema
/// version; (3) no product code assigns a constant signed-schema version to an envelope row (the schema follows the envelope).
/// </summary>
public sealed partial class EnvelopeRowFidelityTests
{
    private const string FieldType = "EnterpriseForms.Domain.Features.FormRecords.FieldEncryptedValue";
    private const string AttachmentType = "EnterpriseForms.Domain.Features.Documents.FileAttachmentVersion";

    /// <summary>The field-row schema setter is declared on the value-row base class (every value kind); no value row may get a constant either.</summary>
    private const string ValueRowBaseType = "EnterpriseForms.Domain.Features.FormRecords.FieldValueRowBase";

    /// <summary>The reviewed construction sites ("caller -> type") and why each is permitted.</summary>
    private static readonly Dictionary<string, string> Constructions = new(StringComparer.Ordinal)
    {
        ["EnterpriseForms.Infrastructure.Features.FormRecords.AesGcmFieldEncryptionService.EncryptAsync -> FieldEncryptedValue"] =
            "Seal: a fresh Envelope v1 row (the only field-row writer; no product writer produces envelope 2 before E3-M4).",
        ["EnterpriseForms.Application.Features.Documents.UploadSessions.UploadSessionHandlers.CloneValue -> FieldEncryptedValue"] =
            "Clone helper for every byte-identical carry of a field row (upload commit, document handlers, lifecycle, protection migration, shared edits).",
        ["EnterpriseForms.Application.Features.FormRecords.Staging.CandidateBuilder.CloneAttachment -> FileAttachmentVersion"] =
            "Clone helper for every per-operation copy of a document version row.",
        ["EnterpriseForms.Infrastructure.Features.FormRecords.AesGcmFieldEncryptionService.OpenCreationFingerprintKeyAsync -> FieldEncryptedValue"] =
            "Transient decrypt input for the creation-idempotency sealed key (unsigned JSON, F-03), built inside the narrow fingerprint operation (E3-M2c R-M2c-1) with the fixed seal identity, envelope 1, schema 0 and no v2 column: never staged, signed or cloned; its v2 shape is an E3-M4 writer concern.",
        ["EnterpriseForms.Application.Features.Documents.Processing.DocumentProcessingService.ProcessAsync -> FileAttachmentVersion"] =
            "The processed (pre-commit) document version; its signed schema follows the envelope the encryption service produced (S-1).",
    };

    /// <summary>Properties a clone deliberately does not copy: the per-operation binding the stager re-assigns, and a new storage key.</summary>
    private static readonly Dictionary<Type, string[]> PerOperationProperties = new()
    {
        [typeof(FieldEncryptedValue)] = ["FieldEncryptedValueId", "ContentChangeOperationId", "ContentRevision", "RowRevision", "RowSignature", "RowSignatureKeyVersion"],
        [typeof(FileAttachmentVersion)] = ["FileAttachmentVersionId", "ContentChangeOperationId", "ContentRevision", "RowRevision", "RowSignature", "RowSignatureKeyVersion", "VersionState"],
    };

    [Fact]
    public void EveryConstructionSiteOfAnEnvelopeRowIsReviewed()
    {
        var found = Discover((instruction, target) => instruction.OpCode.Code == Code.Newobj && target.Name == ".ctor" && target.DeclaringType.FullName is FieldType or AttachmentType
            ? Short(target.DeclaringType.FullName) : null);
        Assert.True(found.SetEquals(Constructions.Keys), "construction sites differ from the reviewed list. Found:\n" + string.Join("\n", found));
    }

    [Fact]
    public void NoProductCodeAssignsAConstantSignedSchemaVersionToAnEnvelopeRow()
    {
        var constants = Discover((instruction, target) => target.Name == "set_RowSignatureSchemaVersion" && target.DeclaringType.FullName is FieldType or AttachmentType or ValueRowBaseType
            && instruction.Previous is { } previous && previous.OpCode.Code is Code.Ldc_I4 or Code.Ldc_I4_S or Code.Ldc_I4_0 or Code.Ldc_I4_1 or Code.Ldc_I4_2 or Code.Ldc_I4_3 or Code.Ldc_I4_M1
            ? "constant schema version" : null);
        Assert.True(constants.Count == 0, "envelope rows must take their schema from the envelope (S-1): " + string.Join("; ", constants));

        // The scan does see a schema assignment on each envelope type (otherwise it would prove nothing).
        var assignments = Discover((_, target) => target.Name == "set_RowSignatureSchemaVersion" && target.DeclaringType.FullName is FieldType or AttachmentType or ValueRowBaseType ? "assignment" : null);
        Assert.Contains(assignments, a => a.StartsWith("EnterpriseForms.Application.Features.Documents.Processing.DocumentProcessingService.", StringComparison.Ordinal));
        Assert.Contains(assignments, a => a.StartsWith("EnterpriseForms.Application.Features.Documents.UploadSessions.UploadSessionHandlers.CloneValue", StringComparison.Ordinal));
        Assert.Contains(assignments, a => a.StartsWith("EnterpriseForms.Application.Features.FormRecords.Staging.CandidateBuilder.CloneAttachment", StringComparison.Ordinal));
    }

    [Fact]
    public void TheFieldCloneCopiesEveryMappedPropertyExceptThePerOperationBinding()
    {
        var source = Populated<FieldEncryptedValue>();
        var cloneValue = typeof(CandidateBuilder).Assembly.GetType("EnterpriseForms.Application.Features.Documents.UploadSessions.UploadSessionHandlers", throwOnError: true)!
            .GetMethod("CloneValue", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
        var copy = (FieldEncryptedValue)cloneValue.Invoke(null, [source])!;
        AssertCopied(source, copy);
    }

    [Fact]
    public void TheAttachmentCloneCopiesEveryMappedPropertyExceptThePerOperationBinding()
    {
        var source = Populated<FileAttachmentVersion>();
        AssertCopied(source, CandidateBuilder.CloneAttachment(source, source.VersionState));
    }

    private static void AssertCopied<T>(T source, T copy)
        where T : class
    {
        var mapped = MappedProperties(typeof(T));
        Assert.Contains("DataKeyIdentifier", mapped);
        Assert.Contains("DataKeyCommitmentBytes", mapped);
        Assert.Contains("KeyWrapAlgorithmCode", mapped);
        Assert.Contains("RowSignatureSchemaVersion", mapped);
        Assert.Contains("EncryptionEnvelopeVersion", mapped);
        foreach (var name in mapped.Except(PerOperationProperties[typeof(T)]))
        {
            var property = typeof(T).GetProperty(name)!;
            var expected = property.GetValue(source);
            var actual = property.GetValue(copy);
            Assert.True(expected is byte[] a && actual is byte[] b ? a.AsSpan().SequenceEqual(b) : Equals(expected, actual), $"{typeof(T).Name}.{name} is not copied by the clone helper");
        }
    }

    /// <summary>The EF-mapped (persisted) properties of an entity: the clone must preserve each one that is not per-operation.</summary>
    private static List<string> MappedProperties(Type entity)
    {
        var options = new DbContextOptionsBuilder<BusinessDbContext>().UseNpgsql("Host=model-only.invalid;Database=unused;Username=unused;Password=unused").Options;
        using var context = new BusinessDbContext(options);
        return context.Model.FindEntityType(entity)!.GetProperties().Where(p => p.PropertyInfo is not null).Select(p => p.Name).ToList();
    }

    /// <summary>Every settable property set to a distinct non-default value, so an uncopied property is visible.</summary>
    private static T Populated<T>()
        where T : new()
    {
        var instance = new T();
        var seed = 1;
        foreach (var property in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite))
        {
            seed++;
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            object value = type == typeof(Guid) ? Guid.CreateVersion7()
                : type == typeof(int) ? seed
                : type == typeof(long) ? (long)seed * 1000
                : type == typeof(string) ? "value-" + seed
                : type == typeof(byte[]) ? Enumerable.Repeat((byte)seed, 8).ToArray()
                : type == typeof(DateTimeOffset) ? new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero).AddMinutes(seed)
                : type == typeof(bool) ? true
                : throw new InvalidOperationException(typeof(T).Name + "." + property.Name + ": add a value for " + type.Name);
            property.SetValue(instance, value);
        }

        return instance;
    }

    private static SortedSet<string> Discover(Func<Instruction, MethodReference, string?> match)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var path in ProductAssemblies())
        {
            using var assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { ReadSymbols = false });
            foreach (var type in assembly.MainModule.GetTypes())
            {
                foreach (var method in type.Methods.Where(m => m.HasBody))
                {
                    foreach (var instruction in method.Body.Instructions)
                    {
                        if (instruction.Operand is MethodReference target && instruction.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj && match(instruction, target) is { } what)
                        {
                            found.Add(Caller(method) + " -> " + what);
                        }
                    }
                }
            }
        }

        return found;
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

    [GeneratedRegex(@"^<+([^<>]+)>")]
    private static partial Regex GeneratedOrigin();

    private static string Short(string fullName) => fullName[(fullName.LastIndexOf('.') + 1)..];

    private static string Caller(MethodDefinition method)
    {
        var type = method.DeclaringType;
        var name = method.Name;
        while (type.IsNested && type.Name.StartsWith('<'))
        {
            if (!name.StartsWith('<') && GeneratedOrigin().Match(type.Name) is { Success: true } fromType)
            {
                name = fromType.Groups[1].Value;
            }

            type = type.DeclaringType;
        }

        if (GeneratedOrigin().Match(name) is { Success: true } fromMethod)
        {
            name = fromMethod.Groups[1].Value;
        }

        return type.FullName.Replace('/', '+') + "." + name;
    }
}
