using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace EnterpriseForms.ArchitectureTests;

/// <summary>
/// E3-M2f-1 (<c>docs/security/E3_M2F_CONTRACT_CLOSURE.md</c> §3 CAP-S1/S2, §12.1), from the compiled IL:
/// <list type="bullet">
/// <item>the capability declaration is made only from the single send path of exactly the three content workload clients (async state
/// machines are attributed to the method that declares them), once per client;</item>
/// <item>the header literal exists only in the declarer, in every business and Authority assembly;</item>
/// <item>the declarer reads no configuration, options or environment; it only sets one constant header value;</item>
/// <item>no Authority assembly references the header yet (existing Authorities ignore it). E3-M2f-2 must change that pin when
/// Authority-side enforcement is introduced.</item>
/// </list>
/// </summary>
public sealed class EnvelopeCapabilityEmissionBoundaryTests
{
    private const string Header = "EnterpriseForms-Envelope-Capability";
    private const string Declarer = "EnterpriseForms.Infrastructure.Authority.EnvelopeCapability";

    private static readonly string[] DeclaringClients =
    [
        "EnterpriseForms.Infrastructure.Authority.AuthorityHttpClient",
        "EnterpriseForms.Infrastructure.Authority.ProtectionVerifierHttpClient",
        "EnterpriseForms.Infrastructure.Authority.SearchIndexBuilderHttpClient",
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

    /// <summary>The source method an IL method belongs to: the method itself, or the method whose async state machine or lambda it is.</summary>
    private static string SourceMethod(MethodDefinition method)
    {
        for (TypeReference? type = method.DeclaringType; type?.DeclaringType is not null; type = type.DeclaringType)
        {
            if (type.Name.StartsWith('<') && type.Name.IndexOf('>', StringComparison.Ordinal) is > 1 and var end)
            {
                return type.Name[1..end];
            }
        }

        return method.Name;
    }

    private static List<(string Assembly, TypeDefinition Type, MethodDefinition Method, Instruction Instruction)> Instructions(Func<Instruction, bool> match)
    {
        var found = new List<(string, TypeDefinition, MethodDefinition, Instruction)>();
        foreach (var path in ProductAssemblies())
        {
            using var assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { ReadSymbols = false });
            foreach (var type in assembly.MainModule.GetTypes())
            {
                foreach (var method in type.Methods.Where(m => m.HasBody))
                {
                    foreach (var instruction in method.Body.Instructions.Where(match))
                    {
                        found.Add((Path.GetFileNameWithoutExtension(path), type, method, instruction));
                    }
                }
            }
        }

        return found;
    }

    private static bool CallsDeclare(Instruction instruction) =>
        instruction.Operand is MethodReference called && called.Name == "Declare" && called.DeclaringType.FullName == Declarer;

    private static bool MentionsHeader(Instruction instruction) =>
        instruction.OpCode == OpCodes.Ldstr && instruction.Operand is string text && text.Contains("Envelope-Capability", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void OnlyTheSendPathOfEachOfTheThreeContentClientsDeclaresTheCapabilityOnce()
    {
        var sites = Instructions(CallsDeclare).Select(s => (Client: Outermost(s.Type).FullName, Source: SourceMethod(s.Method))).ToList();
        Assert.Equal(DeclaringClients, sites.Select(s => s.Client).Order(StringComparer.Ordinal));
        Assert.All(sites, s => Assert.Equal("SendAsync", s.Source));
        Assert.DoesNotContain(sites, s => s.Client.EndsWith("TeamMembershipReconcilerHttpClient", StringComparison.Ordinal));
    }

    [Fact]
    public void TheHeaderLiteralExistsOnlyInTheDeclarerOnTheBusinessSide()
    {
        var mentions = Instructions(MentionsHeader).Select(s => (s.Assembly, Type: Outermost(s.Type).FullName, Text: (string)s.Instruction.Operand))
            .Where(m => m.Assembly.StartsWith("EnterpriseForms.", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(mentions);
        Assert.All(mentions, m =>
        {
            Assert.Equal("EnterpriseForms.Infrastructure", m.Assembly);
            Assert.Equal(Declarer, m.Type);
            Assert.Equal(Header, m.Text);
        });

        // E3-M2f-2 replaced the M2f-1 compatibility pin ("no Authority assembly references the header yet"): the Authority now reads the
        // header for enforcement. Where it may appear there is pinned by EnvelopeCapabilityEnforcementBoundaryTests.
    }

    [Fact]
    public void TheDeclarerReadsNoConfigurationAndSetsOnlyTheConstantLevel()
    {
        var path = ProductAssemblies().Single(p => Path.GetFileNameWithoutExtension(p) == "EnterpriseForms.Infrastructure");
        using var assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { ReadSymbols = false });
        var declarer = assembly.MainModule.GetType(Declarer);
        Assert.NotNull(declarer);
        Assert.DoesNotContain(declarer.Fields, f => !f.HasConstant); // no mutable or static state
        Assert.Equal("2", declarer.Fields.Single(f => f.Name == "DeclaredLevel").Constant);

        var declare = declarer.Methods.Single(m => m.Name == "Declare");
        Assert.True(declare.IsAssembly && declare.IsStatic);
        var calls = declare.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>().Select(m => m.DeclaringType.Namespace + "." + m.DeclaringType.Name + "::" + m.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Subset(new HashSet<string>(StringComparer.Ordinal) { "System.ArgumentNullException::ThrowIfNull", "System.Net.Http.Headers.HttpHeaders::Remove", "System.Net.Http.Headers.HttpHeaders::Add", "System.Net.Http.HttpRequestMessage::get_Headers" }, calls);
        var literals = declare.Body.Instructions.Where(i => i.OpCode == OpCodes.Ldstr).Select(i => (string)i.Operand).ToList();
        Assert.Equal(["request", Header, Header, "2"], literals); // "request": ThrowIfNull's caller-argument expression
    }
}
