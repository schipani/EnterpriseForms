using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace EnterpriseForms.ArchitectureTests;

/// <summary>
/// E3-M2f-2 (<c>docs/security/E3_M2F_CONTRACT_CLOSURE.md</c> §5 AM-F1, §6 CAP-O1..O7, §3 CAP-S1, §7 CAP-E3), from the compiled IL (async state
/// machines are attributed to the method that declares them; IL order is source order within a method):
/// <list type="bullet">
/// <item>M2F-21 / CAP-O7: the shared check <c>EnvelopeCapabilityFloor.RequireAsync</c> is called exactly once from each of exactly the seven
/// content entry points, and the binding-existence port has exactly one caller, the shared check (AM-F1 #2/#3);</item>
/// <item>M2F-23: the other ledger ports are used only by the content write paths (Prepare and Commit), never by read, verify, search or
/// history paths (AM-F1 #6, L-10);</item>
/// <item>M2F-25: at each entry point the check follows the organization lock (and, at Prepare and Commit, the Release B fence) and precedes
/// every other store, resolution, gate, replay, verification, journal or index step;</item>
/// <item>the check itself only reads the existence port, reports and throws: it writes nothing and uses no other store member (CAP-F6);</item>
/// <item>each of the seven Api endpoints passes the parsed request header to its service; no other Api call site exists;</item>
/// <item>M2F-31 / CAP-S1: the business and Authority header names and error codes are equal, and the header literal appears only in the
/// business declarer and the Authority Api header helper / OpenAPI contract;</item>
/// <item>CAP-E3: the business workers handle the refusal terminally before their generic non-retryable/retryable handling.</item>
/// </list>
/// </summary>
public sealed class EnvelopeCapabilityEnforcementBoundaryTests
{
    private const string Floor = "AuthorizationAuthority.Application.Features.Content.EnvelopeCapabilityFloor";
    private const string Scope = "AuthorizationAuthority.Application.Common.Ports.IAuthorityScope";
    private const string Store = "AuthorizationAuthority.Application.Common.Ports.IAuthorityStore";
    private const string ContentService = "AuthorizationAuthority.Application.Features.Content.ContentService";
    private const string Reconciliation = "AuthorizationAuthority.Application.Features.Catalogue.ProtectionReconciliationService";
    private const string Builder = "AuthorizationAuthority.Application.Features.SearchIndex.SearchIndexBuilderService";
    private const string Header = "EnterpriseForms-Envelope-Capability";

    /// <summary>The seven entry points of closure §6.1 (the source methods that hold the organization lock), with whether the Release B fence precedes the check.</summary>
    private static readonly (string Type, string Method, bool Fence)[] EntryPoints =
    [
        (ContentService, "PrepareCoreAsync", true),
        (ContentService, "CommitCoreAsync", true),
        (ContentService, "VerifyProjectionAsync", false),
        (ContentService, "VerifyProjectionsAsync", false),
        (Reconciliation, "CensusAsync", false),
        (Builder, "BackfillAsync", false),
        (Builder, "DeepVerifyAsync", false),
    ];

    /// <summary>The public service methods the Api calls for those seven endpoints.</summary>
    private static readonly (string Type, string Method)[] ServiceMethods =
    [
        (ContentService, "PrepareAsync"), (ContentService, "CommitAsync"), (ContentService, "VerifyProjectionAsync"), (ContentService, "VerifyProjectionsAsync"),
        (Reconciliation, "CensusAsync"), (Builder, "BackfillAsync"), (Builder, "DeepVerifyAsync"),
    ];

    private sealed record Site(string Assembly, string OuterType, string SourceMethod, MethodDefinition Method);

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

    private static string SourceMethod(MethodDefinition method)
    {
        for (TypeReference? type = method.DeclaringType; type?.DeclaringType is not null; type = type.DeclaringType)
        {
            if (type.Name.StartsWith('<') && type.Name.IndexOf('>', StringComparison.Ordinal) is > 1 and var end)
            {
                return type.Name[1..end];
            }
        }

        // Lambdas compiled as methods of a display class or of the declaring type: "<Outer>b__0_1".
        if (method.Name.StartsWith('<') && method.Name.IndexOf('>', StringComparison.Ordinal) is > 1 and var close)
        {
            return method.Name[1..close];
        }

        return method.Name;
    }

    private static string Key(MethodReference called) => called.DeclaringType.Namespace + "." + called.DeclaringType.Name + "::" + called.Name;

    /// <summary>Every call site of a method matching <paramref name="match"/>, in every product assembly (the assembly stays open while bodies are read).</summary>
    private static List<Site> CallSites(Func<MethodReference, bool> match)
    {
        var sites = new List<Site>();
        foreach (var path in ProductAssemblies())
        {
            using var assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { ReadSymbols = false });
            foreach (var type in assembly.MainModule.GetTypes())
            {
                foreach (var method in type.Methods.Where(m => m.HasBody))
                {
                    foreach (var instruction in method.Body.Instructions)
                    {
                        if (instruction.Operand is MethodReference called && (instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt || instruction.OpCode == OpCodes.Newobj) && match(called))
                        {
                            sites.Add(new Site(Path.GetFileNameWithoutExtension(path), Outermost(type).FullName, SourceMethod(method), method));
                        }
                    }
                }
            }
        }

        return sites;
    }

    /// <summary>The ordered call targets of the IL bodies that make up one source method (the method and its state machine / closures).</summary>
    private static List<string> OrderedCalls(string assemblyName, string typeName, string sourceMethod)
    {
        var path = ProductAssemblies().Single(p => Path.GetFileNameWithoutExtension(p) == assemblyName);
        using var assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { ReadSymbols = false });
        var calls = new List<string>();
        var bodies = assembly.MainModule.GetTypes()
            .Where(t => Outermost(t).FullName == typeName)
            .SelectMany(t => t.Methods.Where(m => m.HasBody && SourceMethod(m) == sourceMethod && (m.Name == "MoveNext" || m.DeclaringType.FullName == typeName)))
            .ToList();
        // An async method's work is its state machine's MoveNext; a synchronous method is its own body.
        var body = bodies.Count(b => b.Name == "MoveNext") == 1 ? bodies.Single(b => b.Name == "MoveNext") : Assert.Single(bodies, b => b.Name == sourceMethod);
        foreach (var instruction in body.Body.Instructions)
        {
            if (instruction.Operand is MethodReference called && (instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt || instruction.OpCode == OpCodes.Newobj))
            {
                calls.Add(Key(called));
            }
        }

        return calls;
    }

    // ---- M2F-21 / CAP-O7: exactly seven call sites of the shared check, one per entry point ----

    [Fact]
    public void M2F21TheSharedCheckIsCalledOnceFromEachOfExactlyTheSevenEntryPoints()
    {
        var sites = CallSites(m => m.DeclaringType.FullName == Floor && m.Name == "RequireAsync")
            .Select(s => (s.Assembly, s.OuterType, s.SourceMethod)).ToList();
        Assert.All(sites, s => Assert.Equal("AuthorizationAuthority.Application", s.Assembly));
        Assert.Equal(EntryPoints.Select(e => (e.Type, e.Method)).Order(), sites.Select(s => (s.OuterType, s.SourceMethod)).Order());
    }

    [Fact]
    public void M2F21TheBindingExistencePortHasExactlyOneCallerTheSharedCheck()
    {
        var sites = CallSites(m => m.Name == "OrganizationHasDataKeyBindingsAsync" && m.DeclaringType.FullName == Scope);
        var site = Assert.Single(sites);
        Assert.Equal((Floor, "RequireAsync"), (site.OuterType, site.SourceMethod));
    }

    // ---- M2F-23: the other ledger ports stay on the content write paths ----

    [Theory]
    [InlineData("FindDataKeyBindingsAsync")]
    [InlineData("GetDataKeyBindingsForOperationAsync")]
    [InlineData("AddDataKeyBindings")]
    [InlineData("RequireDataKeyLedgerAsync")]
    public void M2F23NoReadVerifySearchOrHistoryPathUsesALedgerPort(string port)
    {
        var sites = CallSites(m => m.Name == port && m.DeclaringType.FullName == Scope).Select(s => (s.OuterType, s.SourceMethod)).Distinct().ToList();
        Assert.NotEmpty(sites);
        Assert.All(sites, s => Assert.Contains(s, new[] { (ContentService, "PrepareCoreAsync"), (ContentService, "CommitCoreAsync") }));
    }

    // ---- M2F-25 / CAP-O2..O4: position at each entry point ----

    /// <summary>Store members that may precede the check: the scope itself, the lock, the Release B fence and the census snapshot lookup (before the lock, read-only).</summary>
    private static readonly HashSet<string> AllowedStoreCalls = new(StringComparer.Ordinal)
    {
        Store + "::BeginScopeAsync",
        Scope + "::LockOrganizationAsync",
        Scope + "::RequireContentAcceptanceFloorAsync",
        Scope + "::FindCatalogueSnapshotAsync",
    };

    /// <summary>Own-service helpers that may precede the check: input validation and the census snapshot lookup; nothing that resolves, verifies or writes.</summary>
    private static readonly HashSet<string> AllowedOwnCalls = new(StringComparer.Ordinal)
    {
        ContentService + "::get_CapabilityFloor",
        Reconciliation + "::RequireSnapshotAsync",
        Builder + "::RequireDark",
        Builder + "::RequireBatch",
    };

    /// <summary>A step the check must precede: any other store/scope/index member, or any resolution, gate, verification, journal, signing, alert or own-service step.</summary>
    private static bool IsSubstantive(string call)
    {
        if (AllowedStoreCalls.Contains(call) || AllowedOwnCalls.Contains(call))
        {
            return false;
        }

        string[] substantivePrefixes =
        [
            Scope + "::", Store + "::", "AuthorizationAuthority.Application.Common.Ports.ISearchIndexStore::", "AuthorizationAuthority.Application.Common.Ports.ISearchAccessStore::",
            ContentService + "::", Reconciliation + "::", Builder + "::",
            "AuthorizationAuthority.Application.Common.ActorResolver::", "AuthorizationAuthority.Application.Common.OrganizationGate::",
            "AuthorizationAuthority.Application.Common.ProjectionIntegrityVerifier::", "AuthorizationAuthority.Application.Features.Journal.",
            "AuthorizationAuthority.Application.Features.SearchIndex.ContentRevisionCoordinator::", "AuthorizationAuthority.Application.Features.SearchIndex.SearchIndexStateTransitions::",
            "AuthorizationAuthority.Application.Features.SearchIndex.SearchIndexWriter::", "AuthorizationAuthority.Application.Features.Catalogue.ProtectionCommandResolver::",
            "AuthorizationAuthority.Application.Common.Ports.IIntegrityMacProvider::", "AuthorizationAuthority.Application.Common.Ports.ISecurityAlertSink::",
        ];
        return substantivePrefixes.Any(p => call.StartsWith(p, StringComparison.Ordinal));
    }

    [Fact]
    public void M2F25AtEachEntryPointTheCheckFollowsTheLockAndFenceAndPrecedesEveryOtherStep()
    {
        foreach (var (type, method, fence) in EntryPoints)
        {
            var calls = OrderedCalls("AuthorizationAuthority.Application", type, method);
            var check = calls.IndexOf(Floor + "::RequireAsync");
            var locked = calls.IndexOf(Scope + "::LockOrganizationAsync");
            Assert.True(check >= 0 && locked >= 0, $"{type}.{method}: lock and check present");
            Assert.Single(calls, c => c == Floor + "::RequireAsync");
            Assert.True(locked < check, $"{type}.{method}: the check follows the organization lock");
            var fenced = calls.IndexOf(Scope + "::RequireContentAcceptanceFloorAsync");
            if (fence)
            {
                Assert.True(locked < fenced && fenced < check, $"{type}.{method}: the Release B fence stays first, the check follows it");
            }
            else
            {
                Assert.Equal(-1, fenced);
            }

            var before = calls.Take(check).Where(IsSubstantive).ToList();
            Assert.True(before.Count == 0, $"{type}.{method}: steps before the capability check: {string.Join(", ", before)}");
            Assert.Contains(calls.Skip(check + 1), IsSubstantive); // the entry point's own work follows the check
        }
    }

    // ---- CAP-F6: the check reads one boolean, reports and throws; nothing else ----

    [Fact]
    public void TheCheckOnlyQueriesTheExistencePortReportsAndRefuses()
    {
        var calls = OrderedCalls("AuthorizationAuthority.Application", Floor, "RequireAsync");
        var store = calls.Where(c => c.StartsWith(Scope + "::", StringComparison.Ordinal) || c.StartsWith(Store + "::", StringComparison.Ordinal)).Distinct().ToList();
        Assert.Equal([Scope + "::OrganizationHasDataKeyBindingsAsync"], store);
        var application = calls.Where(c => c.StartsWith("AuthorizationAuthority.", StringComparison.Ordinal)).Distinct().Order(StringComparer.Ordinal).ToList();
        Assert.Subset(new HashSet<string>(StringComparer.Ordinal)
        {
            Scope + "::OrganizationHasDataKeyBindingsAsync",
            Floor + "::get_DeploymentFloor",
            Floor + "::Refuse",
            "AuthorizationAuthority.Application.Features.Content.ClientEnvelopeCapability::get_Legacy",
            "AuthorizationAuthority.Application.Features.Content.ClientEnvelopeCapability::get_Current",
            "AuthorizationAuthority.Application.Features.Content.ClientEnvelopeCapability::get_Level",
            "AuthorizationAuthority.Application.Features.Content.IEnvelopeCapabilityObserver::Evaluated",
            "AuthorizationAuthority.Application.Common.AuthorityException::NotReady",
            "AuthorizationAuthority.Domain.Features.Organizations.OrganizationAuthorizationState::get_OrganizationId",
        }, application.ToHashSet(StringComparer.Ordinal));
    }

    // ---- Api wiring: each of the seven endpoints passes the parsed header ----

    [Fact]
    public void EachOfTheSevenEndpointsPassesTheParsedRequestHeaderToItsService()
    {
        var sites = CallSites(m => ServiceMethods.Contains((m.DeclaringType.FullName, m.Name)) && m.Parameters.Count > 0 && m.Parameters[^1].ParameterType.Name == "ClientEnvelopeCapability")
            .Where(s => s.Assembly == "AuthorizationAuthority.Api").ToList();
        Assert.Equal(7, sites.Count);
        foreach (var site in sites)
        {
            var parses = site.Method.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "EnvelopeCapability" && m.DeclaringType.FullName == "AuthorizationAuthority.Api.Composition.InternalAuthentication");
            Assert.True(parses, $"{site.OuterType}.{site.SourceMethod} passes InternalAuthentication.EnvelopeCapability(context)");
        }

        // No other product call site of the seven service methods (tests and tools pass their own declarations).
        var all = CallSites(m => ServiceMethods.Contains((m.DeclaringType.FullName, m.Name)));
        Assert.Equal(7, all.Count);
        Assert.All(all, s => Assert.Equal("AuthorizationAuthority.Api", s.Assembly));
    }

    [Fact]
    public void TheHeaderHelperParsesEveryHeaderInstanceWithTheSharedParser()
    {
        var calls = OrderedCalls("AuthorizationAuthority.Api", "AuthorizationAuthority.Api.Composition.InternalAuthentication", "EnvelopeCapability");
        Assert.Contains("AuthorizationAuthority.Application.Features.Content.ClientEnvelopeCapability::FromHeaderValues", calls);
    }

    // ---- M2F-31 / CAP-S1: constants and literal locations ----

    [Fact]
    public void M2F31TheBusinessAndAuthorityHeaderNamesAndErrorCodesAreEqual()
    {
        Assert.Equal(Header, EnterpriseForms.Infrastructure.Authority.EnvelopeCapability.HeaderName);
        Assert.Equal(EnterpriseForms.Infrastructure.Authority.EnvelopeCapability.HeaderName, AuthorizationAuthority.Application.Features.Content.ClientEnvelopeCapability.HeaderName);
        Assert.Equal(EnterpriseForms.Infrastructure.Authority.EnvelopeCapability.DeclaredLevel, AuthorizationAuthority.Application.Features.Content.ClientEnvelopeCapability.Current.ToString());
        Assert.Equal(AuthorizationAuthority.Application.Common.AuthorityErrorCodes.ClientEnvelopeCapabilityInsufficient, EnterpriseForms.Application.Common.Authority.EnvelopeCapabilityRefusal.ErrorCode);
    }

    [Fact]
    public void TheHeaderLiteralExistsOnlyInTheBusinessDeclarerAndTheAuthorityHeaderContract()
    {
        var mentions = new List<(string Assembly, string Type)>();
        var constants = new List<(string Assembly, string Type)>();
        foreach (var path in ProductAssemblies())
        {
            using var assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { ReadSymbols = false });
            var name = Path.GetFileNameWithoutExtension(path);
            foreach (var type in assembly.MainModule.GetTypes())
            {
                constants.AddRange(type.Fields.Where(f => f.HasConstant && f.Constant is string s && s.Contains("Envelope-Capability", StringComparison.OrdinalIgnoreCase)).Select(_ => (name, Outermost(type).FullName)));
                mentions.AddRange(type.Methods.Where(m => m.HasBody)
                    .Where(m => m.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldstr && i.Operand is string s && s.Contains("Envelope-Capability", StringComparison.OrdinalIgnoreCase)))
                    .Select(_ => (name, Outermost(type).FullName)));
            }
        }

        Assert.Equal(
            [("AuthorizationAuthority.Application", "AuthorizationAuthority.Application.Features.Content.ClientEnvelopeCapability"), ("EnterpriseForms.Infrastructure", "EnterpriseForms.Infrastructure.Authority.EnvelopeCapability")],
            constants.Distinct().Order());
        Assert.Equal(
            [
                ("AuthorizationAuthority.Api", "AuthorizationAuthority.Api.Composition.EnvelopeCapabilityComposition"),
                ("AuthorizationAuthority.Api", "AuthorizationAuthority.Api.Composition.InternalAuthentication"),
                ("EnterpriseForms.Infrastructure", "EnterpriseForms.Infrastructure.Authority.EnvelopeCapability"),
            ],
            mentions.Distinct().Order());
    }

    // ---- M2F-30 / CAP-E1: the regenerated internal OpenAPI document ----

    [Fact]
    public void M2F30TheInternalOpenApiDocumentsTheHeaderAndTheRefusalOnExactlyTheSevenOperations()
    {
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(RepositoryPaths.Combine("contracts", "openapi", "authorization-authority-internal-v1.json")));
        var withHeader = new List<string>();
        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                var parameters = operation.Value.TryGetProperty("parameters", out var p) ? p.EnumerateArray().ToList() : [];
                var header = parameters.Where(x => x.GetProperty("name").GetString() == Header).ToList();
                if (header.Count == 0)
                {
                    continue;
                }

                var id = operation.Value.GetProperty("operationId").GetString()!;
                withHeader.Add(id);
                var single = Assert.Single(header);
                Assert.Equal("header", single.GetProperty("in").GetString());
                Assert.False(single.TryGetProperty("required", out var required) && required.GetBoolean(), id + ": the header is optional (absent is level 1)");
                Assert.Equal(["1", "2"], single.GetProperty("schema").GetProperty("enum").EnumerateArray().Select(e => e.GetString()));
                var conflict = operation.Value.GetProperty("responses").GetProperty("409");
                Assert.Contains("CLIENT_ENVELOPE_CAPABILITY_INSUFFICIENT", conflict.GetProperty("description").GetString(), StringComparison.Ordinal);
                Assert.EndsWith("/ErrorResponse", conflict.GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString(), StringComparison.Ordinal);
            }
        }

        Assert.Equal(
            ["BackfillSearchIndex", "CommitContentChange", "DeepVerifySearchIndex", "PrepareContentChange", "TakeProtectionCensus", "VerifyRecordProjection", "VerifyRecordProjections"],
            withHeader.Order(StringComparer.Ordinal));
    }

    // ---- CAP-E3: the business workers are terminal on the refusal ----

    private static string BackendSource(string relative) =>
        File.ReadAllText(RepositoryPaths.Combine("src", "backend", relative.Replace('/', Path.DirectorySeparatorChar))).Replace("\r\n", "\n", StringComparison.Ordinal);

    [Fact]
    public void TheProtectionMigrationFailsAnItemTerminallyBeforeItsBoundedFreshAttemptPath()
    {
        var source = BackendSource("EnterpriseForms.Application/Features/FormDesigner/ProtectionMigration/FieldProtectionMigrationService.cs");
        var refusal = source.IndexOf("catch (AuthorityClientException ex) when (EnvelopeCapabilityRefusal.Is(ex))", StringComparison.Ordinal);
        var generic = source.IndexOf("catch (AuthorityClientException ex) when (!ex.Retryable)\n", StringComparison.Ordinal);
        var retryable = source.IndexOf("catch (AuthorityClientException ex) when (IsRetryableConflict(ex.ErrorCode))", StringComparison.Ordinal);
        Assert.True(retryable >= 0 && refusal > retryable && generic > refusal, "the refusal clause sits before the generic non-retryable clause of the same try");
        var block = source[refusal..generic];
        Assert.Contains("FieldProtectionMigrationItemStates.Failed", block, StringComparison.Ordinal);
        Assert.Contains("Retry: false", block, StringComparison.Ordinal);
        Assert.DoesNotContain("Failure(", block, StringComparison.Ordinal); // never the bounded-attempt path that prepares a fresh operation
    }

    [Fact]
    public void TheProtectionReconciliationFinishesIncompleteBeforeItsBlockAndRetryPath()
    {
        var source = BackendSource("EnterpriseForms.Application/Features/FormDesigner/ProtectionMigration/FieldProtectionReconciliationService.cs");
        var refusal = source.IndexOf("catch (AuthorityClientException ex) when (EnvelopeCapabilityRefusal.Is(ex))", StringComparison.Ordinal);
        var retry = source.IndexOf("catch (AuthorityClientException ex) when (ex.Retryable || ex.HttpStatusCode == 503)", StringComparison.Ordinal);
        Assert.True(refusal >= 0 && retry > refusal);
        var block = source[refusal..retry];
        Assert.Contains("FinishAsync(reconciliation.FieldProtectionReconciliationId, FieldProtectionReconciliationStates.Incomplete, ex.ErrorCode", block, StringComparison.Ordinal);
        Assert.DoesNotContain("BlockAsync", block, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBusinessProblemHandlerMapsTheRefusalBeforeTheGenericAuthorityCase()
    {
        var source = BackendSource("EnterpriseForms.Api/Composition/BusinessProblemHandler.cs");
        var refusal = source.IndexOf("case AuthorityClientException ex when EnvelopeCapabilityRefusal.Is(ex):", StringComparison.Ordinal);
        var generic = source.IndexOf("case AuthorityClientException ex:", StringComparison.Ordinal);
        Assert.True(refusal >= 0 && generic > refusal);
    }
}
