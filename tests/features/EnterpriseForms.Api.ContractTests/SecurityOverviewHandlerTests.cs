using System.Reflection;
using System.Text.Json;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.Audit.Overview;
using EnterpriseForms.Domain.Features.FormDefinitions;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// C8 M4-4 business side without Docker: the Security Home handler asks the Authority with the session's organization only, copies
/// the Authority's counts field by field, adds business directory form names to coverage (OD-3), writes nothing, maps every
/// Authority refusal to a fail-closed problem and refuses an answer for another organization or section. The real Authority,
/// PostgreSQL and the browser run in Stage B (SecurityHomeTests) and Playwright, which are not run here.
/// </summary>
public sealed class SecurityOverviewHandlerTests
{
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly Guid Admin = Guid.CreateVersion7();
    private static readonly Guid Form = Guid.CreateVersion7();
    private static readonly Guid Field = Guid.CreateVersion7();
    private static readonly RequestContext Context = new(Org, Admin, "https://issuer.invalid", "admin-subject", "Ada Admin", "assertion", Guid.CreateVersion7());
    private static readonly DateTimeOffset T0 = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AuthoritySecurityOverview Overview(string section, Guid? organization = null) => new(organization ?? Org, section, "basis", T0,
        section == "gate" ? new AuthorityOverviewGate(15, 12, 40, "AwaitingProjection", false) : null,
        section == "commands" ? new AuthorityOverviewCommands([new("Pending", 2), new("CommittedAwaitingProjection", 0), new("FailedReconciliation", 1)], T0.AddHours(-1), 3600, [new("CreateShare", "Pending", null, T0.AddHours(-1), 3600)], 20, "list") : null,
        section == "archive" ? new AuthorityOverviewArchive([new("audit", 1, 5, 0, T0.AddMinutes(-5), 300, T0.AddMinutes(-1))], "RR-6 statement") : null,
        Guid.CreateVersion7());

    private static AuthoritySecurityCoverage CoverageAnswer(Guid? organization = null) => new(organization ?? Org, 1, 2, 1, 1, 0, 40,
        [new(Guid.CreateVersion7(), Form, Field, "Encrypt", true, "NoLaterOrganizationContentChange", new(Guid.CreateVersion7(), true, null, 7, 6, 6, 1, 0, 0, 0, 2, 9, 33, 40, T0)),
         new(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "Encrypt", false, "Missing", null)],
        T0, "coverage statement", "organization-wide staleness", "basis", Guid.CreateVersion7());

    [Theory]
    [InlineData("gate")]
    [InlineData("commands")]
    [InlineData("archive")]
    public async Task EachOverviewSectionIsTheAuthoritysAnswerForTheSessionOrganization(string section)
    {
        var world = new World { Answer = Overview(section) };
        var response = await world.Handler().ReadAsync(Context, section, Ct);

        var asked = Assert.Single(world.Requests);
        Assert.Equal((section, Org, Context.CorrelationId), (asked.Section, asked.Query.OrganizationId, asked.Query.CorrelationId));
        Assert.Equal((section, "basis", T0), (response.Section, response.AuthorizationBasis, response.ComputedOn));
        Assert.Equal(section == "gate", response.Gate is not null);
        Assert.Equal(section == "commands", response.Commands is not null);
        Assert.Equal(section == "archive", response.Archive is not null);
        Assert.Null(response.Coverage);
        Assert.Equal(0, world.StoreScopes); // overview sections read nothing from the business database
    }

    [Fact]
    public async Task SectionContentIsCopiedFieldByField()
    {
        var gate = (await new World { Answer = Overview("gate") }.Handler().ReadAsync(Context, "gate", Ct)).Gate!;
        Assert.Equal((15L, 12L, 40L, "AwaitingProjection", false), (gate.AuthorizationRevision, gate.ProjectionAppliedRevision, gate.ContentIntegrityRevision, gate.GateState, gate.IntegrityQuarantined));
        var commands = (await new World { Answer = Overview("commands") }.Handler().ReadAsync(Context, "commands", Ct)).Commands!;
        Assert.Equal([("Pending", 2), ("CommittedAwaitingProjection", 0), ("FailedReconciliation", 1)], commands.StateCounts.Select(s => (s.CommandState, s.CommandCount)).ToList());
        Assert.Equal(("CreateShare", 3600L, 20), (commands.Commands.Single().CommandKind, commands.Commands.Single().AgeSeconds, commands.ListLimit));
        var archive = (await new World { Answer = Overview("archive") }.Handler().ReadAsync(Context, "archive", Ct)).Archive!;
        Assert.Equal(("audit", 1, 5, "RR-6 statement"), (archive.Streams.Single().StreamName, archive.Streams.Single().PendingCount, archive.Streams.Single().AnchoredCount, archive.ArchiveStatement));
    }

    [Fact]
    public async Task CoverageAddsDirectoryFormNamesAndIdentifiesFieldsByIdOnly() // OD-3
    {
        var world = new World { Coverage = CoverageAnswer() };
        var response = await world.Handler().ReadAsync(Context, "coverage", Ct);

        Assert.Equal(1, world.CoverageRequests);
        Assert.Empty(world.Requests);
        var coverage = response.Coverage!;
        Assert.Equal(("coverage", 2, 1, 1, 0, 40L), (response.Section, coverage.DecisionCount, coverage.AttestedCompleteDecisionCount, coverage.MissingDecisionCount, coverage.StaleDecisionCount, coverage.OrganizationContentIntegrityRevision));
        Assert.Equal(("Intake Form", Field), (coverage.Decisions[0].FormDisplayName, coverage.Decisions[0].FieldDefinitionId));
        Assert.Null(coverage.Decisions[1].FormDisplayName); // not in the directory: no name is invented
        Assert.Equal(("Missing", (object?)null), (coverage.Decisions[1].AttestationState, (object?)coverage.Decisions[1].Attestation));
        Assert.Equal("organization-wide staleness", coverage.StalenessStatement);
        Assert.Contains("id only", coverage.FormNameStatement, StringComparison.Ordinal);
        var json = JsonSerializer.Serialize(response);
        Assert.DoesNotContain("SENTINEL", json, StringComparison.Ordinal); // the directory row's description never travels
        Assert.Equal(0, world.Writes);
    }

    [Fact]
    public async Task FormNamesComeOnlyFromTheSessionOrganizationsDirectory() // item 7: no cross-organization association
    {
        var foreignOrg = Guid.CreateVersion7();
        var foreignForm = Guid.CreateVersion7();
        var answer = CoverageAnswer() with
        {
            Decisions = [CoverageAnswer().Decisions[0], CoverageAnswer().Decisions[0] with { SecurityCommandId = Guid.CreateVersion7(), FormDefinitionId = foreignForm }],
        };
        var world = new World
        {
            Coverage = answer,
            Directory = new()
            {
                [Org] = [new() { FormDefinitionId = Form, TenantId = Org, FormDefinitionName = "Intake Form" }],
                [foreignOrg] = [new() { FormDefinitionId = foreignForm, TenantId = foreignOrg, FormDefinitionName = "FOREIGN-FORM-NAME" }],
            },
        };
        var response = await world.Handler().ReadAsync(Context, "coverage", Ct);

        Assert.Equal([Org], world.DirectoryTenants); // only the session organization's directory is read
        Assert.Equal("Intake Form", response.Coverage!.Decisions[0].FormDisplayName);
        Assert.Null(response.Coverage.Decisions[1].FormDisplayName); // a form of another organization never gets a name
        Assert.DoesNotContain("FOREIGN-FORM-NAME", JsonSerializer.Serialize(response), StringComparison.Ordinal);
    }

    [Fact]
    public void TheWireShapesCarryNoFieldKeyLabelOrRecordId()
    {
        foreach (var type in typeof(EnterpriseForms.Contracts.Features.SecurityOverviewSectionResponse).Assembly.GetTypes().Where(t => t.Namespace == "EnterpriseForms.Contracts.Features" && t.Name.StartsWith("Security", StringComparison.Ordinal)
                     && (t.Name.StartsWith("SecurityOverview", StringComparison.Ordinal) || t.Name.StartsWith("SecurityCoverage", StringComparison.Ordinal))))
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.DoesNotContain("FieldKey", property.Name, StringComparison.Ordinal);
                Assert.DoesNotContain("FieldLabel", property.Name, StringComparison.Ordinal);
                Assert.DoesNotContain("FormRecordId", property.Name, StringComparison.Ordinal);
                Assert.DoesNotContain("Payload", property.Name, StringComparison.Ordinal);
                Assert.DoesNotContain("Reason", property.Name.Replace("ReasonCode", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
                Assert.DoesNotContain("Target", property.Name, StringComparison.Ordinal);
            }
        }
    }

    public static TheoryData<int, string, int, string> Refusals => new()
    {
        { 404, "NOT_FOUND", 403, "SECURITY_ADMINISTRATION_FORBIDDEN" },
        { 403, "FORBIDDEN", 403, "SECURITY_ADMINISTRATION_FORBIDDEN" },
        { 422, "INVALID_REQUEST", 422, "INVALID_REQUEST" },
        { 429, "RATE_LIMITED", 429, "RATE_LIMITED" },
        { 503, "AUTHORITY_NOT_READY", 503, "AUTHORITY_UNAVAILABLE" },
        { 503, "AUTHORITY_UNAVAILABLE", 503, "AUTHORITY_UNAVAILABLE" },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task EveryAuthorityRefusalIsAFailClosedProblem(int status, string code, int expectedStatus, string expectedCode)
    {
        foreach (var section in SecurityOverviewHandler.Sections)
        {
            var world = new World { Refusal = new AuthorityClientException(code, status, status >= 500, "detail") };
            var ex = await Assert.ThrowsAsync<BusinessException>(() => world.Handler().ReadAsync(Context, section, Ct));
            Assert.Equal((expectedStatus, expectedCode), (ex.HttpStatusCode, ex.ErrorCode));
            Assert.DoesNotContain("detail", ex.Message, StringComparison.Ordinal); // Authority detail is never reflected
        }
    }

    [Fact]
    public async Task AnAnswerForAnotherOrganizationOrSectionOrWithoutItsContentIsRefused()
    {
        async Task Refused(World world, string section)
        {
            var ex = await Assert.ThrowsAsync<BusinessException>(() => world.Handler().ReadAsync(Context, section, Ct));
            Assert.Equal((503, "AUTHORITY_UNAVAILABLE"), (ex.HttpStatusCode, ex.ErrorCode));
        }

        await Refused(new World { Answer = Overview("gate", Guid.CreateVersion7()) }, "gate");
        await Refused(new World { Answer = Overview("archive") }, "gate"); // section mismatch
        await Refused(new World { Answer = Overview("gate") with { Section = "commands" } }, "commands");
        await Refused(new World { Answer = Overview("commands") with { Commands = null } }, "commands");
        await Refused(new World { Coverage = CoverageAnswer(Guid.CreateVersion7()) }, "coverage");
    }

    [Theory]
    [InlineData("incidents")]
    [InlineData("search-approvals")]
    [InlineData("Gate")]
    [InlineData("")]
    public async Task AnUnknownSectionIsNotFoundWithoutAskingTheAuthority(string section)
    {
        var world = new World();
        var ex = await Assert.ThrowsAsync<BusinessException>(() => world.Handler().ReadAsync(Context, section, Ct));
        Assert.Equal(404, ex.HttpStatusCode);
        Assert.Empty(world.Requests);
        Assert.Equal(0, world.CoverageRequests);
    }

    private sealed class World
    {
        public AuthoritySecurityOverview? Answer { get; init; }

        public AuthoritySecurityCoverage? Coverage { get; init; }

        public AuthorityClientException? Refusal { get; init; }

        public List<(string Section, SecurityOverviewQuery Query)> Requests { get; } = [];

        public int CoverageRequests { get; private set; }

        public int StoreScopes { get; private set; }

        public int Writes { get; private set; }

        public SecurityOverviewHandler Handler()
        {
            var authority = Stub.Create<ISecurityOverviewAuthority>((method, args) =>
            {
                if (method.Name == nameof(ISecurityOverviewAuthority.ReadProtectionCoverageAsync))
                {
                    CoverageRequests++;
                    return Refusal is { } r ? Task.FromException<AuthoritySecurityCoverage>(r) : Task.FromResult(Coverage!);
                }

                Requests.Add(((string)args[1]!, (SecurityOverviewQuery)args[2]!));
                return Refusal is { } refusal ? Task.FromException<AuthoritySecurityOverview>(refusal) : Task.FromResult(Answer!);
            });
            var store = Stub.Create<IBusinessStore>((method, _) =>
            {
                StoreScopes++;
                return method.Name == nameof(IBusinessStore.BeginScopeAsync) ? Task.FromResult(Scope()) : throw new NotSupportedException(method.Name);
            });
            return new SecurityOverviewHandler(store, authority);
        }

        /// <summary>Tenants whose directory was read, in order.</summary>
        public List<Guid> DirectoryTenants { get; } = [];

        /// <summary>Forms by tenant, as the tenant-filtered store returns them (form ids are global primary keys).</summary>
        public Dictionary<Guid, List<FormDefinition>> Directory { get; init; } = new() { [Org] = [new() { FormDefinitionId = Form, TenantId = Org, FormDefinitionName = "Intake Form", FormDefinitionDescription = "SENTINEL description" }] };

        private Task<List<FormDefinition>> DirectoryOf(Guid tenant)
        {
            DirectoryTenants.Add(tenant);
            return Task.FromResult(Directory.GetValueOrDefault(tenant) ?? []);
        }

        private IBusinessScope Scope() => Stub.Create<IBusinessScope>((method, args) => method.Name switch
        {
            nameof(IBusinessScope.GetFormDefinitionsAsync) => DirectoryOf((Guid)args[0]!),
            nameof(IAsyncDisposable.DisposeAsync) => ValueTask.CompletedTask,
            _ => CountWrite(method.Name),
        });

        private object CountWrite(string name)
        {
            Writes++;
            throw new NotSupportedException(name);
        }
    }

    public class Stub<T> : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Handler { get; set; } = (_, _) => null;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args ?? []);
    }

    private static class Stub
    {
        public static T Create<T>(Func<MethodInfo, object?[], object?> handler)
            where T : class
        {
            var proxy = DispatchProxy.Create<T, Stub<T>>();
            ((Stub<T>)(object)proxy).Handler = handler;
            return proxy;
        }
    }
}
