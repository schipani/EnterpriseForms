using System.Text.Json;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.OperatorPlane;
using AuthorizationAuthority.Application.Features.SecurityCommands;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// C-107: the organization owner limit (ten ACTIVE owners: owner role held and membership enabled) through the real command service
/// over the in-memory store. Owner grants are checked when proposed (early feedback) and again at the final operation, inside the
/// organization-locked command transaction; re-enabling an owner is an addition too; disabled owners do not count; nothing is removed to
/// make room and a refused command changes nothing. Onboarding (2–10 owners), the legacy bootstrap and owner recovery share the same
/// constant (see OrganizationOnboardingTests and OwnerRecoveryTests). Real concurrency is proven on the Docker stack.
/// </summary>
public sealed class OwnerLimitTests
{
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly FieldSearchApprovalGate StepUp = FieldSearchApprovalGate.Create(FieldSearchApprovalGate.SyntheticMode, "Testing", "TestIdentityProvider");
    private const string Issuer = "https://idp.test.invalid";

    private readonly InMemoryAuthorityStore store = new();
    private readonly List<Guid> owners = Enumerable.Range(0, 9).Select(_ => Guid.CreateVersion7()).ToList();
    private readonly Guid memberOne = Guid.CreateVersion7();
    private readonly Guid memberTwo = Guid.CreateVersion7();
    private readonly Guid memberThree = Guid.CreateVersion7();

    public OwnerLimitTests()
    {
        store.Organizations.Add(new OrganizationAuthorizationState { OrganizationId = Org, OrganizationDisplayName = "Crowded Org", AuthorizationRevision = 5, ProjectionAppliedRevision = 5 });
        foreach (var user in owners.Concat([memberOne, memberTwo, memberThree]))
        {
            store.Bindings.Add(new IdentityBinding { IdentityBindingId = Guid.CreateVersion7(), OrganizationId = Org, AuthorityUserId = user, IdentityIssuerUri = Issuer, IdentitySubjectIdentifier = "subject-" + user.ToString("N"), IdentityBindingEnabled = true });
        }

        foreach (var owner in owners)
        {
            store.Edges.Add(Edge(OrganizationRoleNames.OrganizationOwner, owner));
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AuthorityRelationshipEdge Edge(string relation, Guid user) => new()
    {
        AuthorityRelationshipEdgeId = Guid.CreateVersion7(), OrganizationId = Org, ResourceTypeName = ResourceTypeNames.Organization, ResourceEntityId = Org, RelationshipName = relation,
        SubjectTypeName = ResourceTypeNames.User, SubjectEntityId = user, SubjectRelationName = string.Empty, RelationshipCreatedOn = DateTimeOffset.UtcNow,
    };

    private sealed class NoFaults : IFaultInjector
    {
        public void CrashIfArmed(string faultName)
        {
        }
    }

    private static VerifiedActor Actor(Guid user) => new(Issuer, "subject-" + user.ToString("N"), "test");

    private long Revision => store.Organizations.Single().AuthorizationRevision;

    private bool IsOwner(Guid user) => store.Edges.Any(e => e.RemovedBySecurityCommandId is null && e.ResourceEntityId == Org && e.RelationshipName == OrganizationRoleNames.OrganizationOwner && e.SubjectEntityId == user);

    private int ActiveOwners => store.Bindings.Count(b => b.IdentityBindingEnabled && IsOwner(b.AuthorityUserId));

    private Task<OperationStatus> SubmitAsync(Guid actor, SecurityCommandKind kind, object payload) =>
        new SecurityCommandService(store, new NoFaults(), TimeProvider.System, StepUp).SubmitAsync(Actor(actor),
            new SecurityCommandRequest(Org, kind, new ResourceRef(ResourceKind.Organization, Org), Revision, JsonSerializer.SerializeToElement(payload, CanonicalJson.Options)),
            "limit-" + Guid.NewGuid().ToString("N"), Guid.CreateVersion7(), Ct);

    private async Task<Guid> ProposeAsync(Guid actor, Guid target, string change) =>
        (await SubmitAsync(actor, SecurityCommandKind.ProposeOrganizationOwnerChange, new OwnerChangeProposalPayload(target, change, "board decision", FieldSearchApprovalGate.SyntheticStepUpEvidence))).CommandId;

    private Task<OperationStatus> GrantAsync(Guid actor, Guid target, Guid proposal) =>
        SubmitAsync(actor, SecurityCommandKind.GrantOrganizationRole, new OrganizationRolePayload(target, OrganizationRoleNames.OrganizationOwner, "governance ticket 7", proposal, FieldSearchApprovalGate.SyntheticStepUpEvidence));

    private Task<OperationStatus> MembershipAsync(Guid actor, Guid target, bool enable, Guid proposal) =>
        SubmitAsync(actor, enable ? SecurityCommandKind.EnableIdentityBinding : SecurityCommandKind.DisableIdentityBinding, new MembershipStatePayload(target, "left the unit", proposal, FieldSearchApprovalGate.SyntheticStepUpEvidence));

    private static async Task<string> RefusedAsync(Func<Task> action) => (await Assert.ThrowsAsync<AuthorityException>(action)).ErrorCode;

    private async Task MakeOwnerAsync(Guid target)
    {
        var proposal = await ProposeAsync(owners[0], target, OwnerChangeKinds.GrantOwner);
        await GrantAsync(owners[1], target, proposal);
        Assert.True(IsOwner(target));
    }

    [Fact]
    public async Task TheTenthOwnerIsAllowedAndTheEleventhIsRefusedWhenProposedAndAtTheFinalOperation()
    {
        // Two proposals made while there is room for one: the limit is checked again when each is executed.
        var first = await ProposeAsync(owners[0], memberOne, OwnerChangeKinds.GrantOwner);
        var second = await ProposeAsync(owners[0], memberTwo, OwnerChangeKinds.GrantOwner);
        await GrantAsync(owners[1], memberOne, first);
        Assert.Equal(10, ActiveOwners);
        var revision = Revision;
        Assert.Equal(AuthorityErrorCodes.OrganizationOwnerLimit, await RefusedAsync(() => GrantAsync(owners[1], memberTwo, second))); // the final operation
        Assert.False(IsOwner(memberTwo));
        Assert.Equal(10, ActiveOwners);
        Assert.Equal(revision, Revision); // a refused grant changes nothing, and nobody was removed to make room
        // And a new proposal is refused up front.
        Assert.Equal(AuthorityErrorCodes.OrganizationOwnerLimit, await RefusedAsync(() => ProposeAsync(owners[0], memberThree, OwnerChangeKinds.GrantOwner)));
    }

    [Fact]
    public async Task DisabledOwnersDoNotCountButReEnablingOneIsRefusedAtTheLimit()
    {
        await MakeOwnerAsync(memberOne); // 10 active
        var disable = await ProposeAsync(owners[0], owners[8], OwnerChangeKinds.DisableOwner);
        await MembershipAsync(owners[1], owners[8], enable: false, disable);
        Assert.Equal(9, ActiveOwners); // the disabled owner keeps the role but is not active
        Assert.True(IsOwner(owners[8]));
        await MakeOwnerAsync(memberTwo); // room again: 10 active
        Assert.Equal(10, ActiveOwners);

        var enable = await ProposeAsync(owners[0], owners[8], OwnerChangeKinds.EnableOwner);
        Assert.Equal(AuthorityErrorCodes.OrganizationOwnerLimit, await RefusedAsync(() => MembershipAsync(owners[1], owners[8], enable: true, enable)));
        Assert.False(store.Bindings.Single(b => b.AuthorityUserId == owners[8]).IdentityBindingEnabled);
        Assert.Equal(10, ActiveOwners);
    }

    [Fact]
    public void TheOneLimitIsSharedByOnboardingRecoveryAndTheBootstrap()
    {
        Assert.Equal(10, OrganizationOwnerLimits.MaximumActiveOwners);
        Assert.Equal(OrganizationOwnerLimits.MaximumActiveOwners, AuthorizationAuthority.Application.Features.OrganizationOnboarding.OrganizationOnboardingPolicy.MaximumOwners);
        Assert.Equal(3, AuthorizationAuthority.Application.Features.OrganizationOnboarding.OwnerRecoveryService.MaximumReplacements);
    }
}
