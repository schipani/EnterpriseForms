using System.Text.Json;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Application.Features.SecurityCommands;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// RB-CARV-1 (contract §22), the secondary content path: registering a record creates its accepted (empty) content head, so a
/// Release B instance whose floor is not enforced refuses it before the plan is staged. The refusal is recorded as an ordinary
/// rejected command; no resource, edge or head is created and no revision is consumed. Other commands do not ask for the fence.
/// </summary>
public sealed class ContentAcceptanceRegistrationTests
{
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly Guid Form = Guid.CreateVersion7();
    private static readonly Guid User = Guid.CreateVersion7();
    private const string Issuer = "https://idp.test.invalid";
    private static readonly FieldSearchApprovalGate StepUp = FieldSearchApprovalGate.Create(FieldSearchApprovalGate.SyntheticMode, "Testing", "TestIdentityProvider");
    private readonly InMemoryAuthorityStore store = new();

    public ContentAcceptanceRegistrationTests()
    {
        store.Organizations.Add(new OrganizationAuthorizationState { OrganizationId = Org, OrganizationDisplayName = "Fence Org", AuthorizationRevision = 3, ProjectionAppliedRevision = 3 });
        store.Bindings.Add(new IdentityBinding { IdentityBindingId = Guid.CreateVersion7(), OrganizationId = Org, AuthorityUserId = User, IdentityIssuerUri = Issuer, IdentitySubjectIdentifier = "subject-" + User.ToString("N"), IdentityBindingEnabled = true });
        store.Resources.Add(new AuthorityResource { OrganizationId = Org, ResourceTypeName = ResourceTypeNames.FormDefinition, ResourceEntityId = Form, ResourceLifecycleState = ResourceLifecycleStates.Active, RegisteredBySecurityCommandId = Guid.CreateVersion7() });
        store.Edges.Add(new AuthorityRelationshipEdge
        {
            AuthorityRelationshipEdgeId = Guid.CreateVersion7(), OrganizationId = Org, ResourceTypeName = ResourceTypeNames.FormDefinition, ResourceEntityId = Form, RelationshipName = "form_user",
            SubjectTypeName = ResourceTypeNames.User, SubjectEntityId = User, SubjectRelationName = string.Empty, RelationshipCreatedOn = DateTimeOffset.UtcNow,
        });
    }

    private sealed class NoFaults : IFaultInjector
    {
        public void CrashIfArmed(string faultName)
        {
        }
    }

    private Task<OperationStatus> RegisterAsync(Guid record) =>
        new SecurityCommandService(store, new NoFaults(), TimeProvider.System, StepUp).SubmitAsync(new VerifiedActor(Issuer, "subject-" + User.ToString("N"), "test"),
            new SecurityCommandRequest(Org, SecurityCommandKind.RegisterRecord, new ResourceRef(ResourceKind.FormRecord, record), store.Organizations.Single().AuthorizationRevision,
                JsonSerializer.SerializeToElement(new { form_record_id = record, form_definition_id = Form, owner_team_id = (Guid?)null }, CanonicalJson.Options)),
            "register-" + record.ToString("N"), Guid.CreateVersion7(), TestContext.Current.CancellationToken);

    [Fact]
    public async Task RegisteringARecordChecksTheFenceAndCreatesItsHeadInReleaseA()
    {
        var record = Guid.CreateVersion7();
        await RegisterAsync(record);

        Assert.Equal(1, store.ContentAcceptanceFenceChecks);
        Assert.Single(store.Heads, h => h.FormRecordId == record);
    }

    [Fact]
    public async Task AReleaseBRegistrationIsRefusedBeforeThePlanIsStagedAndRecordedAsARejectedCommand()
    {
        store.ContentAcceptanceFenceRefusal = ContentAcceptanceFloorFailures.FloorNotInstalled;
        var record = Guid.CreateVersion7();
        var resources = store.Resources.Count;
        var edges = store.Edges.Count;

        var ex = await Assert.ThrowsAsync<AuthorityException>(() => RegisterAsync(record));

        Assert.Equal((AuthorityErrorCodes.ContentAcceptanceFloorNotEnforced, 503, false), (ex.ErrorCode, ex.HttpStatusCode, ex.Retryable));
        Assert.Empty(store.Heads);
        Assert.Equal(resources, store.Resources.Count);
        Assert.Equal(edges, store.Edges.Count);
        Assert.Equal(3, store.Organizations.Single().AuthorizationRevision); // no revision consumed
        var rejected = Assert.Single(store.Commands);
        Assert.Equal((SecurityCommandStates.Rejected, AuthorityErrorCodes.ContentAcceptanceFloorNotEnforced), (rejected.CommandStateName, rejected.RejectionReasonCode));
    }

    [Fact]
    public async Task ACommandThatRegistersNoRecordNeverAsksForTheFence()
    {
        store.ContentAcceptanceFenceRefusal = ContentAcceptanceFloorFailures.FloorNotInstalled;
        var secondOwner = Guid.CreateVersion7();
        var member = Guid.CreateVersion7();
        foreach (var user in new[] { secondOwner, member })
        {
            store.Bindings.Add(new IdentityBinding { IdentityBindingId = Guid.CreateVersion7(), OrganizationId = Org, AuthorityUserId = user, IdentityIssuerUri = Issuer, IdentitySubjectIdentifier = "subject-" + user.ToString("N"), IdentityBindingEnabled = true });
        }

        foreach (var owner in new[] { User, secondOwner })
        {
            store.Edges.Add(new AuthorityRelationshipEdge
            {
                AuthorityRelationshipEdgeId = Guid.CreateVersion7(), OrganizationId = Org, ResourceTypeName = ResourceTypeNames.Organization, ResourceEntityId = Org, RelationshipName = OrganizationRoleNames.OrganizationOwner,
                SubjectTypeName = ResourceTypeNames.User, SubjectEntityId = owner, SubjectRelationName = string.Empty, RelationshipCreatedOn = DateTimeOffset.UtcNow,
            });
        }

        // A security command that registers no record (as in OperationTimingTests): it commits while the floor is not enforced.
        await new SecurityCommandService(store, new NoFaults(), TimeProvider.System, StepUp).SubmitAsync(new VerifiedActor(Issuer, "subject-" + User.ToString("N"), "test"),
            new SecurityCommandRequest(Org, SecurityCommandKind.ProposeOrganizationOwnerChange, new ResourceRef(ResourceKind.Organization, Org), 3,
                JsonSerializer.SerializeToElement(new OwnerChangeProposalPayload(member, OwnerChangeKinds.GrantOwner, "board decision", FieldSearchApprovalGate.SyntheticStepUpEvidence), CanonicalJson.Options)),
            "propose-" + member.ToString("N"), Guid.CreateVersion7(), TestContext.Current.CancellationToken);

        Assert.Equal(0, store.ContentAcceptanceFenceChecks); // revocations and other commands never depend on the floor
        Assert.Equal(4, store.Organizations.Single().AuthorizationRevision);
    }
}
