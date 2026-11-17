using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace EnterpriseForms.ArchitectureTests;

/// <summary>
/// E3-M1: the Envelope v2 cryptographic core is isolated. E3-M2c (dark readers) opens exactly one door: (1) outside the namespace,
/// in any product assembly (Business and Authority), only the verified decryption paths of <c>AesGcmFieldEncryptionService</c> and
/// <c>AesGcmFileEncryptionService</c> reference v2 code, and only <c>EnvelopeV2ProductReader</c> (and its nested types) — in a
/// signature, field, base type, interface or method body; no product code references the opener, the document cipher, the seal
/// batch or the key schedule directly. (2) The product reader itself references no writer entry point (seal batch, document
/// encryption) and no key schedule, so v2 writers stay unreachable from product code. (3) Every v2 type is non-public, so only
/// the contract tests reach the rest (InternalsVisibleTo). (4) v2 code references no logging, console, debug or trace API, so
/// plaintext, seeds and derived keys cannot reach a log through it. DecryptionCallSiteTests separately pins its decrypt call sites.
/// </summary>
public sealed class EnvelopeV2IsolationTests
{
    private const string EnvelopeV2Namespace = "EnterpriseForms.Infrastructure.Features.Documents.EnvelopeV2";
    private const string ProductReader = EnvelopeV2Namespace + ".EnvelopeV2ProductReader";

    /// <summary>E3-M2c: the only product types (with their compiler-generated nested types) allowed to reference the v2 product reader.</summary>
    private static readonly HashSet<string> ApprovedGatewayUsers = new(StringComparer.Ordinal)
    {
        "EnterpriseForms.Infrastructure.Features.FormRecords.AesGcmFieldEncryptionService",
        "EnterpriseForms.Infrastructure.Features.Documents.AesGcmFileEncryptionService",
    };

    /// <summary>Writer entry points and key material of the core that the product reader must never reach.</summary>
    private static readonly string[] WriterOrKeyTypes =
    [
        EnvelopeV2Namespace + ".FieldSealBatchV2", EnvelopeV2Namespace + ".EnvelopeV2KeySchedule", EnvelopeV2Namespace + ".DocumentEncryptionV2Outcome",
    ];

    private static readonly string[] ForbiddenPrefixes =
    [
        "Microsoft.Extensions.Logging.", "System.Console", "System.Diagnostics.Debug", "System.Diagnostics.Trace", "System.Diagnostics.EventLog",
        "System.Diagnostics.Tracing.", "System.Diagnostics.Activity", "Serilog.", "OpenTelemetry.",
    ];

    private static IEnumerable<string> ProductAssemblies() =>
        Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll")
            .Where(path =>
            {
                var name = Path.GetFileNameWithoutExtension(path);
                return (name.StartsWith("EnterpriseForms.", StringComparison.Ordinal) || name.StartsWith("AuthorizationAuthority.", StringComparison.Ordinal))
                       && !name.EndsWith("Tests", StringComparison.Ordinal);
            })
            .Order(StringComparer.Ordinal);

    private static TypeReference Outermost(TypeReference type)
    {
        var outer = type;
        while (outer.DeclaringType is { } declaring)
        {
            outer = declaring;
        }

        return outer;
    }

    private static bool InEnvelopeV2(TypeReference type) => Outermost(type).Namespace == EnvelopeV2Namespace;

    private static bool IsProductReader(TypeReference type) => Outermost(type).FullName == ProductReader;

    /// <summary>Members of the core a method body calls or loads (the writer check is member-level: <c>DocumentEnvelopeV2Cipher</c> holds both directions).</summary>
    private static IEnumerable<string> ReferencedMembers(TypeDefinition type) =>
        type.Methods.Where(m => m.HasBody).SelectMany(m => m.Body.Instructions).Select(i => i.Operand).OfType<MethodReference>()
            .Select(m => m.DeclaringType.GetElementType().FullName + "::" + m.Name);

    private static IEnumerable<TypeReference> Flatten(TypeReference? type)
    {
        if (type is null)
        {
            yield break;
        }

        yield return type.GetElementType();
        if (type is GenericInstanceType generic)
        {
            foreach (var argument in generic.GenericArguments.SelectMany(Flatten))
            {
                yield return argument;
            }
        }
    }

    private static IEnumerable<TypeReference> Referenced(Instruction instruction) => instruction.Operand switch
    {
        TypeReference type => Flatten(type),
        MethodReference method => Flatten(method.DeclaringType).Concat(Flatten(method.ReturnType)).Concat(method.Parameters.SelectMany(p => Flatten(p.ParameterType))),
        FieldReference field => Flatten(field.DeclaringType).Concat(Flatten(field.FieldType)),
        _ => [],
    };

    private static IEnumerable<TypeReference> Referenced(TypeDefinition type) =>
        Flatten(type.BaseType)
            .Concat(type.Interfaces.SelectMany(i => Flatten(i.InterfaceType)))
            .Concat(type.Fields.SelectMany(f => Flatten(f.FieldType)))
            .Concat(type.Properties.SelectMany(p => Flatten(p.PropertyType)))
            .Concat(type.Methods.SelectMany(m => Flatten(m.ReturnType).Concat(m.Parameters.SelectMany(p => Flatten(p.ParameterType)))))
            .Concat(type.Methods.Where(m => m.HasBody).SelectMany(m => m.Body.Instructions.SelectMany(Referenced).Concat(m.Body.Variables.SelectMany(v => Flatten(v.VariableType)))));

    /// <summary>Evaluates <paramref name="check"/> while the Infrastructure assembly is open (Cecil reads method bodies lazily).</summary>
    private static T OverInfrastructureTypes<T>(Func<IEnumerable<TypeDefinition>, T> check)
    {
        var path = ProductAssemblies().Single(p => Path.GetFileNameWithoutExtension(p) == "EnterpriseForms.Infrastructure");
        using var assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { ReadSymbols = false });
        return check(assembly.MainModule.GetTypes());
    }

    [Fact]
    public void OnlyTheApprovedDecryptionServicesReachEnvelopeV2AndOnlyThroughTheProductReader()
    {
        var violations = new SortedSet<string>(StringComparer.Ordinal);
        var gatewayUses = new SortedSet<string>(StringComparer.Ordinal);
        var coreTypes = 0;
        foreach (var path in ProductAssemblies())
        {
            using var assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { ReadSymbols = false });
            foreach (var type in assembly.MainModule.GetTypes())
            {
                if (InEnvelopeV2(type))
                {
                    coreTypes++;
                    continue;
                }

                foreach (var referenced in Referenced(type).Where(InEnvelopeV2))
                {
                    if (IsProductReader(referenced) && ApprovedGatewayUsers.Contains(Outermost(type).FullName))
                    {
                        gatewayUses.Add(Outermost(type).FullName);
                        continue;
                    }

                    violations.Add(type.FullName + " -> " + referenced.FullName);
                }
            }
        }

        Assert.True(coreTypes >= 10, "the Envelope v2 core was not found in the scanned assemblies");
        Assert.Empty(violations);
        Assert.Equal(ApprovedGatewayUsers.Order(StringComparer.Ordinal), gatewayUses); // both verified readers are wired (E3-M2c)
    }

    [Fact]
    public void TheProductReaderReachesNoWriterEntryPointOrKeySchedule()
    {
        var found = OverInfrastructureTypes(types =>
        {
            var reader = types.Where(t => InEnvelopeV2(t) && IsProductReader(t)).ToList();
            var referencedTypes = reader.SelectMany(Referenced).Select(r => r.FullName).ToHashSet(StringComparer.Ordinal);
            var members = reader.SelectMany(ReferencedMembers).ToHashSet(StringComparer.Ordinal);
            return (Count: reader.Count, Types: referencedTypes, Members: members);
        });

        Assert.True(found.Count >= 3, "the product reader and its nested types were not found");
        // Positive control: the scan sees what the reader is supposed to call.
        Assert.Contains(EnvelopeV2Namespace + ".FieldEnvelopeV2Opener::OpenAsync", found.Members);
        Assert.Contains(EnvelopeV2Namespace + ".DocumentEnvelopeV2Cipher::DecryptAsync", found.Members);
        Assert.All(WriterOrKeyTypes, writer => Assert.DoesNotContain(writer, found.Types));
        Assert.DoesNotContain(EnvelopeV2Namespace + ".DocumentEnvelopeV2Cipher::EncryptAsync", found.Members);
        Assert.DoesNotContain(found.Members, m => m.StartsWith(EnvelopeV2Namespace + ".FieldSealBatchV2::", StringComparison.Ordinal));
    }

    [Fact]
    public void TheScanDetectsReferencesToEnvelopeV2TypesInSignaturesFieldsAndMethodBodies()
    {
        // Positive control: inside the core, references the scan must see (a field type, and a newobj in an async state machine).
        var found = OverInfrastructureTypes(types =>
        {
            var all = types.ToList();
            var batch = all.Single(t => t.FullName == EnvelopeV2Namespace + ".FieldSealBatchV2");
            var opener = all.Where(t => InEnvelopeV2(t) && t.FullName.StartsWith(EnvelopeV2Namespace + ".FieldEnvelopeV2Opener", StringComparison.Ordinal));
            return (Field: Referenced(batch).Any(r => r.FullName == EnvelopeV2Namespace + ".EnvelopeV2KeySchedule"),
                Body: opener.SelectMany(Referenced).Any(r => r.FullName == EnvelopeV2Namespace + ".EnvelopeV2KeySchedule"));
        });
        Assert.True(found.Field);
        Assert.True(found.Body);
    }

    [Fact]
    public void EveryEnvelopeV2TypeIsNonPublic()
    {
        var publicTypes = OverInfrastructureTypes(types => types.Where(InEnvelopeV2).Where(t => t.IsPublic || (t.IsNestedPublic && t.DeclaringType.IsPublic)).Select(t => t.FullName).ToList());
        Assert.Empty(publicTypes);
    }

    [Fact]
    public void EnvelopeV2CodeReferencesNoLoggingConsoleOrTraceApi()
    {
        var violations = OverInfrastructureTypes(types => types.Where(InEnvelopeV2)
            .SelectMany(t => Referenced(t).Select(r => (Type: t.FullName, Referenced: r.FullName)))
            .Where(x => ForbiddenPrefixes.Any(prefix => x.Referenced.StartsWith(prefix, StringComparison.Ordinal)))
            .Select(x => x.Type + " -> " + x.Referenced)
            .Distinct(StringComparer.Ordinal)
            .ToList());
        Assert.Empty(violations);
    }
}
