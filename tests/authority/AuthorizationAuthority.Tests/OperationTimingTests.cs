using System.Text.Json;
using AuthorizationAuthority.Api.Composition;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.SecurityCommands;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// C-137 (R-37): the optional operation-timing observer reports a command's phases and outcome without changing it, and the
/// diagnostic refuses to start outside Development and Testing.
/// </summary>
public sealed class OperationTimingTests
{
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly FieldSearchApprovalGate StepUp = FieldSearchApprovalGate.Create(FieldSearchApprovalGate.SyntheticMode, "Testing", "TestIdentityProvider");
    private const string Issuer = "https://idp.test.invalid";
    private readonly InMemoryAuthorityStore store = new();
    private readonly Guid ownerOne = Guid.CreateVersion7();
    private readonly Guid ownerTwo = Guid.CreateVersion7();
    private readonly Guid member = Guid.CreateVersion7();
    private readonly Guid memberTwo = Guid.CreateVersion7();
    private readonly Recording observer = new();

    public OperationTimingTests()
    {
        store.Organizations.Add(new OrganizationAuthorizationState { OrganizationId = Org, OrganizationDisplayName = "Timed Org", AuthorizationRevision = 3, ProjectionAppliedRevision = 3 });
        foreach (var user in new[] { ownerOne, ownerTwo, member, memberTwo })
        {
            store.Bindings.Add(new IdentityBinding { IdentityBindingId = Guid.CreateVersion7(), OrganizationId = Org, AuthorityUserId = user, IdentityIssuerUri = Issuer, IdentitySubjectIdentifier = "subject-" + user.ToString("N"), IdentityBindingEnabled = true });
        }

        foreach (var owner in new[] { ownerOne, ownerTwo })
        {
            store.Edges.Add(new AuthorityRelationshipEdge
            {
                AuthorityRelationshipEdgeId = Guid.CreateVersion7(), OrganizationId = Org, ResourceTypeName = ResourceTypeNames.Organization, ResourceEntityId = Org, RelationshipName = OrganizationRoleNames.OrganizationOwner,
                SubjectTypeName = ResourceTypeNames.User, SubjectEntityId = owner, SubjectRelationName = string.Empty, RelationshipCreatedOn = DateTimeOffset.UtcNow,
            });
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Recording : IOperationTimingObserver
    {
        public List<SecurityCommandTiming> Commands { get; } = [];

        public void SecurityCommand(SecurityCommandTiming timing) => Commands.Add(timing);

        public void ContentOperation(ContentOperationTiming timing)
        {
        }
    }

    private sealed class NoFaults : IFaultInjector
    {
        public void CrashIfArmed(string faultName)
        {
        }
    }

    private Task<OperationStatus> ProposeAsync(long expectedRevision, string key, Guid? target = null) =>
        new SecurityCommandService(store, new NoFaults(), TimeProvider.System, StepUp, timing: observer).SubmitAsync(new VerifiedActor(Issuer, "subject-" + ownerOne.ToString("N"), "test"),
            new SecurityCommandRequest(Org, SecurityCommandKind.ProposeOrganizationOwnerChange, new ResourceRef(ResourceKind.Organization, Org), expectedRevision,
                JsonSerializer.SerializeToElement(new OwnerChangeProposalPayload(target ?? member, OwnerChangeKinds.GrantOwner, "board decision", FieldSearchApprovalGate.SyntheticStepUpEvidence), CanonicalJson.Options)),
            key, Guid.CreateVersion7(), Ct);

    [Fact]
    public async Task ACommittedCommandReportsItsPhasesAndAReplayAndARefusalReportTheirOutcomes()
    {
        await ProposeAsync(3, "timing-" + Guid.NewGuid().ToString("N"));
        var committed = Assert.Single(observer.Commands);
        Assert.Equal((Org, nameof(SecurityCommandKind.ProposeOrganizationOwnerChange), "Committed", 2), (committed.OrganizationId, committed.CommandKind, committed.Outcome, committed.GraphEdgeCount));
        Assert.True(committed.TotalMilliseconds >= committed.LockWaitMilliseconds + committed.GraphLoadMilliseconds + committed.PlanMilliseconds + committed.CommitMilliseconds - 0.001);

        var revision = store.Organizations.Single().AuthorizationRevision;
        var refused = await Assert.ThrowsAsync<AuthorityException>(() => ProposeAsync(revision - 1, "timing-" + Guid.NewGuid().ToString("N")));
        Assert.Equal(AuthorityErrorCodes.StaleAuthorizationRevision, refused.ErrorCode);
        Assert.Equal(AuthorityErrorCodes.StaleAuthorizationRevision, observer.Commands[^1].Outcome);
        Assert.Equal(revision, store.Organizations.Single().AuthorizationRevision); // observing changes nothing

        var key = "timing-" + Guid.NewGuid().ToString("N");
        var first = await ProposeAsync(revision, key, memberTwo);
        var again = await ProposeAsync(revision, key, memberTwo);
        Assert.Equal(first.CommandId, again.CommandId);
        Assert.Equal("Replayed", observer.Commands[^1].Outcome);
    }

    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "authority";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static IConfiguration Configuration(bool enabled) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [OperationTimingComposition.ConfigurationKey] = enabled ? "true" : "false" }).Build();

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void TheDiagnosticRefusesToStartOutsideDevelopmentAndTesting(string environment) =>
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddOperationTiming(Configuration(true), new Environment(environment)));

    [Theory]
    [InlineData("Development", true, true)]
    [InlineData("Testing", true, true)]
    [InlineData("Testing", false, false)]
    [InlineData("Production", false, false)]
    public void TheObserverIsRegisteredOnlyWhenConfiguredInDevelopmentOrTesting(string environment, bool enabled, bool registered)
    {
        var services = new ServiceCollection().AddOperationTiming(Configuration(enabled), new Environment(environment));
        Assert.Equal(registered, services.Any(d => d.ServiceType == typeof(IOperationTimingObserver)));
    }
}
