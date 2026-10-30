using System.Text.Json;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.SecurityCommands;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// Phase C8 AC-9 at unit level (package §1.2, RC-10/RC-14/RC-18/RC-21/RC-24 logic): the outcome is bound to (organization, key,
/// allowlisted kind); the pair is the command's own; effective membership at P is derived from edge revisions; quarantine and
/// rejection report status only; unknown answers are uniform; a lookup never commits anything. The HTTP boundary (credentials,
/// assertion refusal, rate limit) is covered by ReconcilerBoundaryTests; the live stack by the Stage B RC suite.
/// </summary>
public sealed class SecurityCommandOutcomeServiceTests
{
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly Guid OtherOrg = Guid.CreateVersion7();
    private static readonly Guid Team = Guid.CreateVersion7();
    private static readonly Guid Member = Guid.CreateVersion7();
    private static readonly Guid Bystander = Guid.CreateVersion7();

    private readonly InMemoryAuthorityStore store = new();
    private readonly SecurityCommandOutcomeService service;

    public SecurityCommandOutcomeServiceTests()
    {
        service = new SecurityCommandOutcomeService(store);
        store.Organizations.Add(new OrganizationAuthorizationState { OrganizationId = Org, AuthorizationRevision = 0, ProjectionAppliedRevision = 0, ProjectionZedToken = "z" });
        store.Organizations.Add(new OrganizationAuthorizationState { OrganizationId = OtherOrg, AuthorizationRevision = 0, ProjectionAppliedRevision = 0, ProjectionZedToken = "z" });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private OrganizationAuthorizationState State(Guid org) => store.Organizations.Single(o => o.OrganizationId == org);

    /// <summary>Commits a team membership command at the next revision (as ApplyPlan does) and optionally makes it effective.</summary>
    private SecurityCommand Commit(string key, bool add, Guid member, bool effective = true, Guid? org = null)
    {
        var state = State(org ?? Org);
        var revision = ++state.AuthorizationRevision;
        var command = new SecurityCommand
        {
            SecurityCommandId = Guid.CreateVersion7(),
            OrganizationId = state.OrganizationId,
            CommandKindName = add ? SecurityCommandKinds.AddTeamMember : SecurityCommandKinds.RemoveTeamMember,
            CommandIdempotencyKey = key,
            CommandPayloadJson = JsonSerializer.Serialize(new { team_id = Team, member_user_id = member }),
            CommittedAuthorizationRevision = revision,
            CommandStateName = SecurityCommandStates.CommittedAwaitingProjection,
        };
        store.Commands.Add(command);
        if (add)
        {
            store.Edges.Add(new AuthorityRelationshipEdge
            {
                AuthorityRelationshipEdgeId = Guid.CreateVersion7(),
                OrganizationId = state.OrganizationId,
                ResourceTypeName = ResourceTypeNames.Team,
                ResourceEntityId = Team,
                RelationshipName = "member",
                SubjectTypeName = ResourceTypeNames.User,
                SubjectEntityId = member,
                SubjectRelationName = string.Empty,
                CreatedBySecurityCommandId = command.SecurityCommandId,
            });
        }
        else
        {
            store.Edges.Single(e => e.OrganizationId == state.OrganizationId && e.SubjectEntityId == member && e.RemovedBySecurityCommandId is null).RemovedBySecurityCommandId = command.SecurityCommandId;
        }

        if (effective)
        {
            MakeEffectiveThrough(revision, org ?? Org);
        }

        return command;
    }

    private void MakeEffectiveThrough(long revision, Guid org)
    {
        var state = State(org);
        state.ProjectionAppliedRevision = revision;
        foreach (var command in store.Commands.Where(c => c.OrganizationId == org && c.CommittedAuthorizationRevision <= revision && c.CommandStateName == SecurityCommandStates.CommittedAwaitingProjection))
        {
            command.CommandStateName = SecurityCommandStates.Effective;
        }
    }

    private Task<SecurityCommandOutcome> Lookup(string key, SecurityCommandKind kind, Guid? org = null) =>
        service.LookupAsync(new SecurityCommandOutcomeLookupRequest(org ?? Org, key, kind), Guid.CreateVersion7(), Ct);

    [Fact]
    public async Task AnEffectiveAddReportsTheCommandsOwnPairAsAMemberAtItsRevision()
    {
        var add = Commit("team-add:k1-aaaaaaaa", add: true, Member);
        var outcome = await Lookup("team-add:k1-aaaaaaaa", SecurityCommandKind.AddTeamMember);

        Assert.True(outcome.Found);
        Assert.Equal(add.SecurityCommandId, outcome.SecurityCommandId);
        Assert.Equal(CommandStatus.Effective, outcome.CommandState);
        Assert.Equal(1, outcome.CommittedAuthorizationRevision);
        Assert.Equal(Team, outcome.TeamId);
        Assert.Equal(Member, outcome.MemberUserId);
        Assert.Equal(new SecurityCommandOutcomeMembership(1, true, 1, false), outcome.Membership);
        Assert.Equal(new SecurityCommandOutcomeOrganizationState(1, 1, ProjectionGateState.Ready, false), outcome.OrganizationState);
    }

    [Fact]
    public async Task C8M102AnEarlierAddAnsweredAfterALaterRemoveReportsTheAuthoritysCurrentAbsence()
    {
        Commit("team-add:k2-aaaaaaaa", add: true, Member);
        Commit("team-remove:k2-bbbbbbb", add: false, Member);

        // Either confirmation, processed in either order, yields the same pair state at P = 2: absent, E = r2.
        var addAnswer = await Lookup("team-add:k2-aaaaaaaa", SecurityCommandKind.AddTeamMember);
        var removeAnswer = await Lookup("team-remove:k2-bbbbbbb", SecurityCommandKind.RemoveTeamMember);
        Assert.Equal(new SecurityCommandOutcomeMembership(2, false, 2, false), addAnswer.Membership);
        Assert.Equal(new SecurityCommandOutcomeMembership(2, false, 2, false), removeAnswer.Membership);
        Assert.Equal(1, addAnswer.CommittedAuthorizationRevision);
        Assert.Equal(2, removeAnswer.CommittedAuthorizationRevision);
    }

    [Fact]
    public async Task RC21AnEffectiveAddWithALaterRemovePendingReportsPresentAtR1AndTheLaterChange()
    {
        Commit("team-add:k3-aaaaaaaa", add: true, Member);
        var remove = Commit("team-remove:k3-bbbbbbb", add: false, Member, effective: false);

        var addAnswer = await Lookup("team-add:k3-aaaaaaaa", SecurityCommandKind.AddTeamMember);
        Assert.Equal(new SecurityCommandOutcomeMembership(1, true, 1, true), addAnswer.Membership);
        var removeAnswer = await Lookup("team-remove:k3-bbbbbbb", SecurityCommandKind.RemoveTeamMember);
        Assert.Equal(CommandStatus.CommittedAwaitingProjection, removeAnswer.CommandState);
        Assert.Equal(new SecurityCommandOutcomeOrganizationState(2, 1, ProjectionGateState.AwaitingProjection, false), removeAnswer.OrganizationState);

        MakeEffectiveThrough(remove.CommittedAuthorizationRevision!.Value, Org);
        Assert.Equal(new SecurityCommandOutcomeMembership(2, false, 2, false), (await Lookup("team-add:k3-aaaaaaaa", SecurityCommandKind.AddTeamMember)).Membership);
    }

    [Fact]
    public async Task ReAddingAfterARemovalReportsTheLatestPairChangeAsTheMembershipRevision()
    {
        Commit("team-add:k4-aaaaaaaa", add: true, Member);
        Commit("team-remove:k4-bbbbbbb", add: false, Member);
        Commit("team-add:k4-ccccccccc", add: true, Member);
        Commit("team-add:k4-bystander", add: true, Bystander); // another pair never influences this one

        var first = await Lookup("team-add:k4-aaaaaaaa", SecurityCommandKind.AddTeamMember);
        Assert.Equal(new SecurityCommandOutcomeMembership(4, true, 3, false), first.Membership);
    }

    [Fact]
    public async Task RC14UnknownOrganizationKeyOrKindMismatchIsOneUniformNotFound()
    {
        Commit("team-add:k5-aaaaaaaa", add: true, Member);
        Commit("team-add:k5-otherorg", add: true, Member, org: OtherOrg);

        var answers = new[]
        {
            await Lookup("team-add:k5-aaaaaaaa", SecurityCommandKind.AddTeamMember, org: OtherOrg), // right key, wrong organization
            await Lookup("team-add:k5-otherorg", SecurityCommandKind.AddTeamMember), // another organization's key
            await Lookup("team-add:k5-zzzzzzzz", SecurityCommandKind.AddTeamMember), // unknown key
            await Lookup("team-add:k5-aaaaaaaa", SecurityCommandKind.RemoveTeamMember), // kind mismatch
            await Lookup("team-add:k5-aaaaaaaa", SecurityCommandKind.AddTeamMember, org: Guid.CreateVersion7()), // unknown organization
        };
        foreach (var answer in answers)
        {
            Assert.False(answer.Found);
            Assert.Null(answer.SecurityCommandId);
            Assert.Null(answer.CommandState);
            Assert.Null(answer.TeamId);
            Assert.Null(answer.MemberUserId);
            Assert.Null(answer.OrganizationState);
            Assert.Null(answer.Membership);
        }
    }

    [Fact]
    public async Task RC08ARejectedCommandReportsItsReasonAndNoMembership()
    {
        store.Commands.Add(new SecurityCommand
        {
            SecurityCommandId = Guid.CreateVersion7(),
            OrganizationId = Org,
            CommandKindName = SecurityCommandKinds.AddTeamMember,
            CommandIdempotencyKey = "team-add:k6-aaaaaaaa",
            CommandPayloadJson = JsonSerializer.Serialize(new { team_id = Team, member_user_id = Member }),
            CommandStateName = SecurityCommandStates.Rejected,
            RejectionReasonCode = AuthorityErrorCodes.Forbidden,
        });

        var outcome = await Lookup("team-add:k6-aaaaaaaa", SecurityCommandKind.AddTeamMember);
        Assert.True(outcome.Found);
        Assert.Equal(CommandStatus.Rejected, outcome.CommandState);
        Assert.Equal(AuthorityErrorCodes.Forbidden, outcome.RejectionReasonCode);
        Assert.Null(outcome.CommittedAuthorizationRevision);
        Assert.Null(outcome.Membership);
        Assert.Equal(Team, outcome.TeamId);
    }

    [Fact]
    public async Task RC24DuringIntegrityQuarantineOnlyStatusIsReported()
    {
        Commit("team-add:k7-aaaaaaaa", add: true, Member);
        State(Org).IntegrityQuarantineEnabled = true;

        var outcome = await Lookup("team-add:k7-aaaaaaaa", SecurityCommandKind.AddTeamMember);
        Assert.True(outcome.Found);
        Assert.Null(outcome.Membership);
        Assert.True(outcome.OrganizationState!.IntegrityQuarantined);
        Assert.Equal(ProjectionGateState.Quarantined, outcome.OrganizationState.ProjectionGateState);

        State(Org).IntegrityQuarantineEnabled = false; // the Authority's own clearance
        Assert.NotNull((await Lookup("team-add:k7-aaaaaaaa", SecurityCommandKind.AddTeamMember)).Membership);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("")]
    public async Task AnInvalidKeyIsAnInvalidRequest(string key)
    {
        var refused = await Assert.ThrowsAsync<AuthorityException>(() => Lookup(key, SecurityCommandKind.AddTeamMember));
        Assert.Equal((AuthorityErrorCodes.InvalidRequest, 400), (refused.ErrorCode, refused.HttpStatusCode));
        var tooLong = await Assert.ThrowsAsync<AuthorityException>(() => Lookup(new string('k', 129), SecurityCommandKind.AddTeamMember));
        Assert.Equal(400, tooLong.HttpStatusCode);
    }

    [Theory]
    [InlineData(SecurityCommandKind.RegisterTeam)]
    [InlineData(SecurityCommandKind.CreateShare)]
    [InlineData(SecurityCommandKind.GrantSystemRole)]
    public async Task OnlyTeamMembershipKindsAreReconcilable(SecurityCommandKind kind)
    {
        var refused = await Assert.ThrowsAsync<AuthorityException>(() => Lookup("team-add:k8-aaaaaaaa", kind));
        Assert.Equal((AuthorityErrorCodes.KindNotReconcilable, 422), (refused.ErrorCode, refused.HttpStatusCode));
    }

    [Fact]
    public async Task RC18ALookupUsesAReadOnlySnapshotAndChangesNoAuthorityState()
    {
        Commit("team-add:k9-aaaaaaaa", add: true, Member);
        var before = (State(Org).AuthorizationRevision, State(Org).ProjectionAppliedRevision, store.Journal.Count, store.Outbox.Count, store.Commands.Count, store.Edges.Count, store.ScopeCommitCount);

        await Lookup("team-add:k9-aaaaaaaa", SecurityCommandKind.AddTeamMember);
        await Lookup("team-add:k9-unknownk", SecurityCommandKind.AddTeamMember);

        Assert.Equal(2, store.ReadOnlyScopeCount);
        Assert.Equal(before, (State(Org).AuthorizationRevision, State(Org).ProjectionAppliedRevision, store.Journal.Count, store.Outbox.Count, store.Commands.Count, store.Edges.Count, store.ScopeCommitCount));
    }

    [Fact]
    public async Task AnUnreadableStoredPayloadReturnsNoPair()
    {
        store.Commands.Add(new SecurityCommand
        {
            SecurityCommandId = Guid.CreateVersion7(),
            OrganizationId = Org,
            CommandKindName = SecurityCommandKinds.AddTeamMember,
            CommandIdempotencyKey = "team-add:k10-aaaaaaa",
            CommandPayloadJson = "{\"unexpected\":true}",
            CommandStateName = SecurityCommandStates.Rejected,
            RejectionReasonCode = AuthorityErrorCodes.InvalidCommand,
        });

        var outcome = await Lookup("team-add:k10-aaaaaaa", SecurityCommandKind.AddTeamMember);
        Assert.True(outcome.Found);
        Assert.Null(outcome.TeamId);
        Assert.Null(outcome.MemberUserId);
        Assert.Null(outcome.Membership);
    }

    [Fact]
    public async Task AHistoryEdgeReferencingAnUncommittedCommandFailsClosedAndRetryable()
    {
        var add = Commit("team-add:k11-aaaaaaa", add: true, Member);
        add.CommittedAuthorizationRevision = null; // inconsistent Authority state: never guessed

        var failure = await Assert.ThrowsAsync<AuthorityException>(() => Lookup("team-add:k11-aaaaaaa", SecurityCommandKind.AddTeamMember));
        Assert.Equal((AuthorityErrorCodes.AuthorityNotReady, 503, true), (failure.ErrorCode, failure.HttpStatusCode, failure.Retryable));
    }
}
