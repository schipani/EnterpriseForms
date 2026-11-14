using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace EnterpriseForms.ArchitectureTests;

/// <summary>
/// E3-M1: the Envelope v2 cryptographic core is isolated and disconnected from production execution until E3-M2 is separately
/// authorized. (1) No type outside its namespace, in any product assembly (Business and Authority), references a v2 type — in a
/// signature, field, base type, interface or method body. (2) Every v2 type is non-public, so only the contract tests reach it
/// (InternalsVisibleTo). (3) v2 code references no logging, console, debug or trace API, so plaintext, seeds and derived keys
/// cannot reach a log through it. DecryptionCallSiteTests separately pins its decrypt call sites and treats its two entry points
/// as decrypting operations.
/// </summary>
public sealed class EnvelopeV2IsolationTests
{
    private const string EnvelopeV2Namespace = "EnterpriseForms.Infrastructure.Features.Documents.EnvelopeV2";

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

    private static bool InEnvelopeV2(TypeReference type)
    {
        var outer = type;
        while (outer.DeclaringType is { } declaring)
        {
            outer = declaring;
        }

        return outer.Namespace == EnvelopeV2Namespace;
    }

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
    public void NoProductCodeOutsideTheCoreReferencesEnvelopeV2()
    {
        var violations = new SortedSet<string>(StringComparer.Ordinal);
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
                    violations.Add(type.FullName + " -> " + referenced.FullName);
                }
            }
        }

        Assert.True(coreTypes >= 10, "the Envelope v2 core was not found in the scanned assemblies");
        Assert.Empty(violations);
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
