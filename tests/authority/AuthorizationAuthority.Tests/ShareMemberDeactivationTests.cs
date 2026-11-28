using System.Text.Json;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.SecurityCommands;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using AuthorizationAuthority.Domain.Features.Sharing;
using AuthorizationAuthority.Infrastructure.Faults;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// C10 M1-WP5 (WP-0 Section 3.4, "member deactivation"; ownership decision 2026-11-26): disabling a member invalidates, in the same
/// command, the shares that member granted under delegated authority and their subtrees (GrantorPathLost). Shares the member holds,
/// the member's owner-granted grants, unresolved legacy grants and every other grantor's delegations are kept; re-enabling restores
/// none of the invalidated shares; a refused disable invalidates nothing.
/// </summary>
public sealed class ShareMemberDeactivationTests
{
    private static readonly FieldSearchApprovalGate StepUp = FieldSearchApprovalGate.Create(FieldSearchApprovalGate.SyntheticMode, "Testing", "TestIdentityProvider");
    private readonly Guid org = Guid.NewGuid();
    private readonly Guid record = Guid.NewGuid();
    private readonly Guid orgOwner = Guid.NewGuid();
    private readonly Guid recordOwner = Guid.NewGuid();
    private readonly Guid leaving = Guid.NewGuid();
    private readonly Guid colleague = Guid.NewGuid();
    private readonly Guid recipient = Guid.NewGuid();
    private readonly Guid team = Guid.NewGuid();
    private readonly Guid item = Guid.NewGuid();
    private readonly InMemoryAuthorityStore store = new();
    private readonly Dictionary<Guid, Guid> bindings = [];

    public ShareMemberDeactivationTests()
    {
        store.Organizations.Add(new OrganizationAuthorizationState { OrganizationId = org, AuthorizationRevision = 5, ProjectionAppliedRevision = 5 });
        foreach (var user in new[] { orgOwner, recordOwner, leaving, colleague, recipient })
        {
            bindings[user] = Guid.NewGuid();
            store.Bindings.Add(new IdentityBinding { IdentityBindingId = bindings[user], OrganizationId = org, AuthorityUserId = user, IdentityIssuerUri = "https://idp", IdentitySubjectIdentifier = user.ToString("N"), IdentityBindingEnabled = true });
        }

        store.Resources.Add(new AuthorityResource { OrganizationId = org, ResourceEntityId = record, ResourceTypeName = ResourceTypeNames.FormRecord, ResourceLifecycleState = ResourceLifecycleStates.Active });
        store.Resources.Add(new AuthorityResource { OrganizationId = org, ResourceEntityId = team, ResourceTypeName = ResourceTypeNames.Team, ResourceLifecycleState = ResourceLifecycleStates.Active });
        Edge(ResourceTypeNames.Organization, org, OrganizationRoleNames.OrganizationOwner, ResourceTypeNames.User, orgOwner);
        Edge(ResourceTypeNames.FormRecord, record, "owner", ResourceTypeNames.User, recordOwner);
        Edge(ResourceTypeNames.Team, team, "member", ResourceTypeNames.User, leaving);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void Edge(string type, Guid id, string relation, string subjectType, Guid subject, string subjectRelation = "") =>
        store.Edges.Add(new AuthorityRelationshipEdge { AuthorityRelationshipEdgeId = Guid.NewGuid(), OrganizationId = org, ResourceTypeName = type, ResourceEntityId = id, RelationshipName = relation, SubjectTypeName = subjectType, SubjectEntityId = subject, SubjectRelationName = subjectRelation });

    private Guid Share(Guid grantor, string role, Guid? parent, Guid? userRecipient = null, Guid? teamRecipient = null, bool legacy = false)
    {
        var id = Guid.NewGuid();
        var command = Guid.NewGuid();
        store.Commands.Add(new SecurityCommand { SecurityCommandId = command, OrganizationId = org, ActorIdentityBindingId = bindings[grantor], CommandKindName = SecurityCommandKinds.CreateShare, CommandStateName = SecurityCommandStates.Effective, CommandIdempotencyKey = "seed-" + id.ToString("N") });
        store.Resources.Add(new AuthorityResource { OrganizationId = org, ResourceEntityId = id, ResourceTypeName = ResourceTypeNames.RecordShare, ParentResourceEntityId = record, ResourceLifecycleState = ResourceLifecycleStates.Active, RegisteredBySecurityCommandId = command });
        var depth = parent is { } p ? store.ShareMetadata.Single(m => m.RecordShareId == p).DelegationDepth + 1 : 1;
        store.ShareMetadata.Add(new ShareResourceMetadata { OrganizationId = org, RecordShareId = id, ParentRecordShareId = parent, DelegationDepth = depth, LegacyLineageUnresolved = legacy });
        if (teamRecipient is { } t)
        {
            Edge(ResourceTypeNames.RecordShare, id, role, ResourceTypeNames.Team, t, "participant");
        }
        else
        {
            Edge(ResourceTypeNames.RecordShare, id, role, ResourceTypeNames.User, userRecipient ?? recipient);
        }

        Edge(ResourceTypeNames.FormRecord, record, "shared_via", ResourceTypeNames.RecordShare, id);
        Edge(ResourceTypeNames.FieldItem, item, "shared_via", ResourceTypeNames.RecordShare, id);
        return id;
    }

    private Task<OperationStatus> SetEnabledAsync(Guid member, bool enable) =>
        new SecurityCommandService(store, new NoOpFaultInjector(), TimeProvider.System, StepUp).SubmitAsync(
            new VerifiedActor("https://idp", orgOwner.ToString("N"), "test"),
            new SecurityCommandRequest(org, enable ? SecurityCommandKind.EnableIdentityBinding : SecurityCommandKind.DisableIdentityBinding, new ResourceRef(ResourceKind.Organization, org),
                store.Organizations.Single().AuthorizationRevision,
                JsonSerializer.SerializeToElement(new MembershipStatePayload(member, "Leaving the organization", null, FieldSearchApprovalGate.SyntheticStepUpEvidence), CanonicalJson.Options)),
            (enable ? "enable-" : "disable-") + Guid.NewGuid().ToString("N")[..12], Guid.NewGuid(), Ct);

    private string State(Guid share) => store.Resources.Single(r => r.ResourceEntityId == share).ResourceLifecycleState;

    private bool HasLiveEdges(Guid share) => store.Edges.Any(e => (e.ResourceEntityId == share || e.SubjectEntityId == share) && e.RemovedBySecurityCommandId is null);

    private List<SecurityJournalEntry> Invalidations() => store.Journal.Where(j => j.JournalStreamName == JournalStreamNames.Audit && j.JournalEventKind == ShareLifecycleJournal.ShareInvalidated).ToList();

    [Fact]
    public async Task DisablingAMemberInvalidatesTheirDelegationsAndSubtreesOnly()
    {
        var directManager = Share(recordOwner, "manager", null, userRecipient: leaving);
        var teamManager = Share(recordOwner, "manager", null, teamRecipient: team);
        var delegatedDirect = Share(leaving, "manager", directManager);
        var below = Share(recipient, "reader", delegatedDirect);
        var delegatedViaTeam = Share(leaving, "reader", teamManager);
        Edge(ResourceTypeNames.RecordShare, directManager, "manager", ResourceTypeNames.User, colleague);
        var colleagues = Share(colleague, "reader", directManager);
        var independent = Share(recordOwner, "reader", null);
        var legacy = Share(leaving, "reader", null, legacy: true);

        var status = await SetEnabledAsync(leaving, enable: false);

        foreach (var share in new[] { delegatedDirect, below, delegatedViaTeam })
        {
            Assert.Equal(ResourceLifecycleStates.Invalidated, State(share));
            Assert.Equal(ShareInvalidationReasons.GrantorPathLost, store.ShareMetadata.Single(m => m.RecordShareId == share).ShareInvalidationReasonCode);
            Assert.False(HasLiveEdges(share));
        }

        foreach (var share in new[] { directManager, teamManager, colleagues, independent, legacy })
        {
            Assert.Equal(ResourceLifecycleStates.Active, State(share));
            Assert.True(HasLiveEdges(share));
        }

        // The member's own held shares keep their edges (disabling keeps every edge the member holds).
        Assert.Contains(store.Edges, e => e.ResourceEntityId == directManager && e.SubjectEntityId == leaving && e.RemovedBySecurityCommandId is null);
        Assert.False(store.Bindings.Single(b => b.AuthorityUserId == leaving).IdentityBindingEnabled);
        Assert.Equal(6, store.Organizations.Single().AuthorizationRevision);
        Assert.Equal(new[] { delegatedDirect, below, delegatedViaTeam }.Order(),
            Invalidations().Select(j => JsonDocument.Parse(j.JournalEventBodyJson).RootElement.GetProperty("body").GetProperty("record_share_id").GetGuid()).Order());
        var command = store.Commands.Single(c => c.SecurityCommandId == status.CommandId);
        Assert.DoesNotContain("invalidated_descendant_share_ids", command.CommandPayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReEnablingRestoresTheMembersAccessButNoInvalidatedShare()
    {
        var directManager = Share(recordOwner, "manager", null, userRecipient: leaving);
        var delegated = Share(leaving, "reader", directManager);

        await SetEnabledAsync(leaving, enable: false);
        await SetEnabledAsync(leaving, enable: true);

        Assert.True(store.Bindings.Single(b => b.AuthorityUserId == leaving).IdentityBindingEnabled);
        Assert.Equal(ResourceLifecycleStates.Active, State(directManager));
        Assert.Equal(ResourceLifecycleStates.Invalidated, State(delegated));
        Assert.False(HasLiveEdges(delegated));
        Assert.Single(Invalidations());
    }

    [Fact]
    public async Task ARefusedDisableInvalidatesNothing()
    {
        var directManager = Share(recordOwner, "manager", null, userRecipient: leaving);
        var delegated = Share(leaving, "reader", directManager);
        Edge(ResourceTypeNames.FormRecord, Guid.NewGuid(), "owner", ResourceTypeNames.User, leaving); // owns a record: D8-4 refuses
        store.Resources.Add(new AuthorityResource { OrganizationId = org, ResourceEntityId = store.Edges.Last().ResourceEntityId, ResourceTypeName = ResourceTypeNames.FormRecord, ResourceLifecycleState = ResourceLifecycleStates.Active });

        var refused = await Assert.ThrowsAsync<AuthorityException>(() => SetEnabledAsync(leaving, enable: false));

        Assert.Equal(AuthorityErrorCodes.OwnsRecords, refused.ErrorCode);
        Assert.Equal(ResourceLifecycleStates.Active, State(delegated));
        Assert.True(HasLiveEdges(delegated));
        Assert.Empty(Invalidations());
        Assert.Equal(5, store.Organizations.Single().AuthorizationRevision);
    }

    [Fact]
    public async Task AMemberWithNoDelegationsIsDisabledWithoutInvalidations()
    {
        var held = Share(recordOwner, "reader", null, userRecipient: leaving);

        await SetEnabledAsync(leaving, enable: false);

        Assert.Equal(ResourceLifecycleStates.Active, State(held));
        Assert.Empty(Invalidations());
    }
}
