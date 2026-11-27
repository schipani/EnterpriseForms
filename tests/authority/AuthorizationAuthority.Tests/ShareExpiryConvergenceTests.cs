using System.Text.Json;
using AuthorizationAuthority.Api.Composition;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.SecurityCommands;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Content;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using AuthorizationAuthority.Domain.Features.Sharing;
using AuthorizationAuthority.Infrastructure.Faults;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// C10 M1-WP6 (WP-0 Section 4 tests 1, 2, 3, 4, 5, 7, 10, 11, 12, 14, 15, 17, 18, 19, 20): the internal ExpireShare convergence
/// command, driven offline on the in-memory store. The expiry-source command is a real UpdateShareScope committed through
/// SecurityCommandService, so its authorization-journal entry is exactly what production writes.
/// </summary>
public sealed class ShareExpiryConvergenceTests
{
    private static readonly HumanCommandStepUpGate Gate = HumanCommandStepUpGate.Create(HumanCommandStepUpGate.SyntheticMode, "Testing", "TestIdentityProvider");
    private readonly Guid org = Guid.NewGuid();
    private readonly Guid record = Guid.NewGuid();
    private readonly Guid owner = Guid.NewGuid();
    private readonly Guid recipient = Guid.NewGuid();
    private readonly Guid item = Guid.NewGuid();
    private readonly InMemoryAuthorityStore store = new();
    private readonly Clock clock = new();
    private readonly DateTimeOffset expiry = DateTimeOffset.UtcNow.AddHours(2);

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Alerts : ISecurityAlertSink
    {
        public List<SecurityAlert> Raised { get; } = [];

        public void Raise(SecurityAlert alert) => Raised.Add(alert);
    }

    public ShareExpiryConvergenceTests()
    {
        store.Organizations.Add(new OrganizationAuthorizationState { OrganizationId = org, AuthorizationRevision = 5, ProjectionAppliedRevision = 5, ProjectionZedToken = "zed-5" });
        store.Bindings.Add(new IdentityBinding { IdentityBindingId = Guid.NewGuid(), OrganizationId = org, AuthorityUserId = owner, IdentityIssuerUri = "https://idp", IdentitySubjectIdentifier = owner.ToString("N"), IdentityBindingEnabled = true });
        store.Resources.Add(new AuthorityResource { OrganizationId = org, ResourceEntityId = record, ResourceTypeName = ResourceTypeNames.FormRecord, ResourceLifecycleState = ResourceLifecycleStates.Active });
        var commit = Guid.NewGuid();
        store.Heads.Add(new RecordContentHead { OrganizationId = org, FormRecordId = record, CurrentRecordManifestCommitId = commit });
        store.ManifestLeaves.Add(new RecordManifestLeaf { RecordManifestCommitId = commit, ManifestItemId = item, ManifestItemKindCode = (byte)EnterpriseForms.Integrity.Merkle.MerkleItemKind.FieldValue });
        Edge(ResourceTypeNames.FormRecord, record, "owner", ResourceTypeNames.User, owner);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ShareExpiryConvergenceService Convergence => new(store, clock);

    /// <summary>A share whose expiry was set by an Effective human UpdateShareScope, with the projection caught up.</summary>
    private async Task<(Guid Share, Guid Source)> ExpiringShareAsync(Guid? parent = null, string key = "update-expiry-1")
    {
        var share = AddShare(parent);
        var payload = new UpdateShareScopePayload(share, record, RecipientRoles.Reader, [item], 1, HumanCommandStepUpGate.SyntheticStepUpEvidence, ShareExpiresOn: expiry);
        var state = store.Organizations.Single();
        var request = new SecurityCommandRequest(org, SecurityCommandKind.UpdateShareScope, new ResourceRef(ResourceKind.FormRecord, record), state.AuthorizationRevision,
            JsonSerializer.SerializeToElement(payload, CanonicalJson.Options), "set an expiry");
        var status = await new SecurityCommandService(store, new NoOpFaultInjector(), TimeProvider.System, FieldSearchApprovalGate.Unavailable, humanCommandGate: Gate)
            .SubmitAsync(new VerifiedActor("https://idp", owner.ToString("N"), "test"), request, key, Guid.NewGuid(), Ct);
        Project();
        return (share, status.CommandId);
    }

    /// <summary>The projection worker's effect: every committed command Effective, the projection at the authorization revision.</summary>
    private void Project()
    {
        var state = store.Organizations.Single();
        state.ProjectionAppliedRevision = state.AuthorizationRevision;
        foreach (var command in store.Commands.Where(c => c.CommandStateName == SecurityCommandStates.CommittedAwaitingProjection))
        {
            command.CommandStateName = SecurityCommandStates.Effective;
        }
    }

    private Guid AddShare(Guid? parent = null)
    {
        var id = Guid.NewGuid();
        store.Resources.Add(new AuthorityResource { OrganizationId = org, ResourceEntityId = id, ResourceTypeName = ResourceTypeNames.RecordShare, ParentResourceEntityId = record, ResourceLifecycleState = ResourceLifecycleStates.Active });
        var depth = parent is { } p ? store.ShareMetadata.Single(m => m.RecordShareId == p).DelegationDepth + 1 : 1;
        store.ShareMetadata.Add(new ShareResourceMetadata { OrganizationId = org, RecordShareId = id, ParentRecordShareId = parent, DelegationDepth = depth });
        Edge(ResourceTypeNames.RecordShare, id, "reader", ResourceTypeNames.User, recipient);
        Edge(ResourceTypeNames.FormRecord, record, "shared_via", ResourceTypeNames.RecordShare, id);
        Edge(ResourceTypeNames.FieldItem, item, "shared_via", ResourceTypeNames.RecordShare, id);
        return id;
    }

    private void Edge(string type, Guid id, string relation, string subjectType, Guid subject) =>
        store.Edges.Add(new AuthorityRelationshipEdge { AuthorityRelationshipEdgeId = Guid.NewGuid(), OrganizationId = org, ResourceTypeName = type, ResourceEntityId = id, RelationshipName = relation, SubjectTypeName = subjectType, SubjectEntityId = subject });

    private ShareResourceMetadata Metadata(Guid share) => store.ShareMetadata.Single(m => m.RecordShareId == share);

    private AuthorityResource Resource(Guid share) => store.Resources.Single(r => r.ResourceEntityId == share);

    private int ExpireCommands => store.Commands.Count(c => c.CommandKindName == SecurityCommandKinds.ExpireShare);

    [Fact]
    public async Task AnExpiredShareConvergesAttributedToTheHumanExpiryDecisionAndInvalidatesDescendants()
    {
        var (share, source) = await ExpiringShareAsync();
        var child = AddShare(share);
        var independent = AddShare();
        clock.Now = expiry.AddMinutes(1);

        var outcome = await Convergence.ConvergeAsync(org, share, Metadata(share).ShareRevision, Ct);

        Assert.Equal(ShareExpiryConvergenceService.Result.Converged, outcome.Result);
        Assert.Equal(ResourceLifecycleStates.Expired, Resource(share).ResourceLifecycleState);
        Assert.Equal(ResourceLifecycleStates.Invalidated, Resource(child).ResourceLifecycleState);
        Assert.Equal(ShareInvalidationReasons.UpstreamExpired, Metadata(child).ShareInvalidationReasonCode);
        Assert.Equal(ResourceLifecycleStates.Active, Resource(independent).ResourceLifecycleState);
        Assert.DoesNotContain(store.Edges, e => (e.SubjectEntityId == share || e.ResourceEntityId == share || e.SubjectEntityId == child || e.ResourceEntityId == child) && e.RemovedBySecurityCommandId is null);
        Assert.Contains(store.Edges, e => e.SubjectEntityId == independent && e.RemovedBySecurityCommandId is null);

        var command = store.Commands.Single(c => c.SecurityCommandId == outcome.SecurityCommandId);
        var sourceCommand = store.Commands.Single(c => c.SecurityCommandId == source);
        Assert.Equal(SecurityCommandKinds.ExpireShare, command.CommandKindName);
        Assert.Equal(sourceCommand.ActorIdentityBindingId, command.ActorIdentityBindingId); // derived attribution, no generic system actor
        Assert.Equal($"share-expiry:{share:D}:{Metadata(share).ShareRevision}", command.CommandIdempotencyKey);
        Assert.Equal(SecurityCommandStates.CommittedAwaitingProjection, command.CommandStateName); // never Effective before projection (A11)
        Assert.Equal(7, command.CommittedAuthorizationRevision);
        Assert.Single(store.Outbox, o => o.SecurityCommandId == command.SecurityCommandId);
        Assert.Single(store.Journal, j => j.JournalStreamName == JournalStreamNames.Authorization && j.SecurityCommandId == command.SecurityCommandId);

        var expired = Assert.Single(store.Journal, j => j.JournalStreamName == JournalStreamNames.Audit && j.JournalEventKind == ShareLifecycleJournal.ShareExpired);
        using var body = JsonDocument.Parse(expired.JournalEventBodyJson);
        var fields = body.RootElement.GetProperty("body");
        Assert.Equal(source, fields.GetProperty("expiry_source_security_command_id").GetGuid());
        Assert.Equal(CommandPlanner.ShareExpiryWorkloadName, fields.GetProperty("workload_name").GetString());
        Assert.Equal([child], fields.GetProperty("invalidated_descendant_share_ids").EnumerateArray().Select(e => e.GetGuid()));
        var invalidated = Assert.Single(store.Journal, j => j.JournalEventKind == ShareLifecycleJournal.ShareInvalidated);
        Assert.Contains(ShareInvalidationReasons.UpstreamExpired, invalidated.JournalEventBodyJson, StringComparison.Ordinal);
        Assert.DoesNotContain(HumanCommandStepUpGate.SyntheticStepUpEvidence, command.CommandPayloadJson, StringComparison.Ordinal);

        var envelope = Application.Features.Audit.SecurityCommandTimelineCatalog.BuildAuditEnvelope(ShareLifecycleJournal.ShareExpired, expired.JournalEventBodyJson);
        Assert.Contains("share_expiry", envelope.WithheldFields); // the instant is evidence, not timeline content
        Assert.DoesNotContain(envelope.Details, d => d.Key == "share_expires_on");
    }

    [Fact]
    public async Task AnUnexpiredOrExtendedShareIsNeverExpiredAndNothingIsJournalled()
    {
        var (share, _) = await ExpiringShareAsync();
        var revision = Metadata(share).ShareRevision;
        var journal = store.Journal.Count;

        clock.Now = expiry.AddMinutes(-1);
        Assert.Equal("NOT_YET_EXPIRED", (await Convergence.ConvergeAsync(org, share, revision, Ct)).ReasonCode);

        clock.Now = expiry.AddMinutes(1);
        Assert.Equal("SHARE_REVISION_MISMATCH", (await Convergence.ConvergeAsync(org, share, revision - 1, Ct)).ReasonCode); // the loop's read is stale

        Assert.Equal(0, ExpireCommands);
        Assert.Equal(journal, store.Journal.Count);
        Assert.Equal(ResourceLifecycleStates.Active, Resource(share).ResourceLifecycleState);
    }

    [Fact]
    public async Task TheSameKeyTwiceRecordsOneCommandAndTerminalStatesStayTerminal()
    {
        var (share, _) = await ExpiringShareAsync();
        clock.Now = expiry.AddMinutes(1);
        var revision = Metadata(share).ShareRevision;
        Assert.Equal(ShareExpiryConvergenceService.Result.Converged, (await Convergence.ConvergeAsync(org, share, revision, Ct)).Result);
        Project();

        var again = await Convergence.ConvergeAsync(org, share, revision, Ct);
        Assert.Equal(ShareExpiryConvergenceService.Result.Ineligible, again.Result);
        Assert.Equal("SHARE_NOT_ACTIVE", again.ReasonCode);
        Assert.Equal(1, ExpireCommands);
    }

    [Fact]
    public async Task AClosedGateACrossTenantIdAndANonEffectiveSourceAreIneligible()
    {
        var (share, source) = await ExpiringShareAsync();
        clock.Now = expiry.AddMinutes(1);
        var revision = Metadata(share).ShareRevision;
        var state = store.Organizations.Single();

        state.ProjectionAppliedRevision--;
        Assert.Equal("GATE_CLOSED", (await Convergence.ConvergeAsync(org, share, revision, Ct)).ReasonCode);
        state.ProjectionAppliedRevision++;

        store.Organizations.Add(new OrganizationAuthorizationState { OrganizationId = Guid.NewGuid(), AuthorizationRevision = 1, ProjectionAppliedRevision = 1 });
        Assert.Equal("SHARE_NOT_FOUND", (await Convergence.ConvergeAsync(store.Organizations[1].OrganizationId, share, revision, Ct)).ReasonCode);

        store.Commands.Single(c => c.SecurityCommandId == source).CommandStateName = SecurityCommandStates.CommittedAwaitingProjection;
        Assert.Equal("SOURCE_COMMAND_NOT_EFFECTIVE", (await Convergence.ConvergeAsync(org, share, revision, Ct)).ReasonCode);
        Assert.Equal(0, ExpireCommands);
    }

    [Fact]
    public async Task TheExpiryBindingIsTakenFromTheJournalNotTheMutableCommandRow()
    {
        var (share, source) = await ExpiringShareAsync();
        clock.Now = expiry.AddMinutes(1);
        var revision = Metadata(share).ShareRevision;
        var row = store.Commands.Single(c => c.SecurityCommandId == source);
        var authorizationEntry = store.Journal.Single(j => j.JournalStreamName == JournalStreamNames.Authorization && j.SecurityCommandId == source);

        // A rewritten row payload changes nothing: admission reads the hash-chained journal body.
        row.CommandPayloadJson = "{}";
        var outcome = await Convergence.ConvergeAsync(org, share, revision, Ct);
        Assert.Equal(ShareExpiryConvergenceService.Result.Converged, outcome.Result);

        // Without its journal entry the source proves nothing: no command.
        var (other, otherSource) = await ExpiringShareAsync(key: "update-expiry-2");
        store.Journal.RemoveAll(j => j.JournalStreamName == JournalStreamNames.Authorization && j.SecurityCommandId == otherSource);
        Assert.Equal("PAYLOAD_MALFORMED", (await Convergence.ConvergeAsync(org, other, Metadata(other).ShareRevision, Ct)).ReasonCode);
        Assert.NotNull(authorizationEntry);
    }

    [Fact]
    public async Task ABackwardsClockStopsTheLoopWithAnAlertAndRecordsNothing()
    {
        var (share, _) = await ExpiringShareAsync();
        clock.Now = DateTimeOffset.UtcNow.AddHours(-1); // earlier than the journal entries just written, and expiry is "past" relative to nothing
        Metadata(share).ShareExpiresOn = clock.Now.AddMinutes(-1);
        var alerts = new Alerts();
        var loop = new ShareExpiryLoop(store, Convergence, alerts, clock, NullLogger<ShareExpiryLoop>.Instance);

        Assert.Equal(0, await loop.RunPassAsync(Ct));
        Assert.True(loop.Stopped);
        Assert.Contains(alerts.Raised, a => a.AlertKind == ShareExpiryLoop.ClockAlertKind);
        Assert.Equal(0, ExpireCommands);
    }

    [Fact]
    public async Task TheLoopConvergesEligibleSharesAndAlertsOnOneStuckForMoreThanTwoPasses()
    {
        var (share, _) = await ExpiringShareAsync();
        var (stuck, stuckSource) = await ExpiringShareAsync(key: "update-expiry-3");
        store.Commands.Single(c => c.SecurityCommandId == stuckSource).CommandStateName = SecurityCommandStates.Pending; // never eligible
        clock.Now = expiry.AddMinutes(1);
        var alerts = new Alerts();
        var loop = new ShareExpiryLoop(store, Convergence, alerts, clock, NullLogger<ShareExpiryLoop>.Instance);

        Assert.Equal(1, await loop.RunPassAsync(Ct));
        Assert.Equal(ResourceLifecycleStates.Expired, Resource(share).ResourceLifecycleState);
        Project();
        await loop.RunPassAsync(Ct);
        Assert.Empty(alerts.Raised);
        await loop.RunPassAsync(Ct);
        Assert.Single(alerts.Raised, a => a.AlertKind == ShareExpiryLoop.StuckAlertKind);
        Assert.Equal(ResourceLifecycleStates.Active, Resource(stuck).ResourceLifecycleState);
    }

    [Fact]
    public async Task NoExternalCallerCanSubmitOrOccupyTheInternalCommand()
    {
        Assert.DoesNotContain(Enum.GetNames<SecurityCommandKind>(), n => n == SecurityCommandKinds.ExpireShare); // not a contract kind

        var share = AddShare();
        var payload = new RevokeSharePayload(share, record, HumanCommandStepUpGate.SyntheticStepUpEvidence);
        var request = new SecurityCommandRequest(org, SecurityCommandKind.RevokeShare, new ResourceRef(ResourceKind.FormRecord, record), 5, JsonSerializer.SerializeToElement(payload, CanonicalJson.Options));
        var refused = await Assert.ThrowsAsync<AuthorityException>(() => new SecurityCommandService(store, new NoOpFaultInjector(), TimeProvider.System, FieldSearchApprovalGate.Unavailable, humanCommandGate: Gate)
            .SubmitAsync(new VerifiedActor("https://idp", owner.ToString("N"), "test"), request, $"share-expiry:{share:D}:1", Guid.NewGuid(), Ct));
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, refused.ErrorCode);
        Assert.Empty(store.Commands); // the reserved key never reaches a command row
    }

    [Fact]
    public async Task ConvergenceNeedsNoStepUpWhileHumanCommandsStillDo()
    {
        var (share, _) = await ExpiringShareAsync();
        clock.Now = expiry.AddMinutes(1);
        Assert.Equal(ShareExpiryConvergenceService.Result.Converged, (await Convergence.ConvergeAsync(org, share, Metadata(share).ShareRevision, Ct)).Result);

        Project();
        var other = AddShare();
        var unproven = new RevokeSharePayload(other, record);
        var request = new SecurityCommandRequest(org, SecurityCommandKind.RevokeShare, new ResourceRef(ResourceKind.FormRecord, record), store.Organizations.Single().AuthorizationRevision,
            JsonSerializer.SerializeToElement(unproven, CanonicalJson.Options));
        var refused = await Assert.ThrowsAsync<AuthorityException>(() => new SecurityCommandService(store, new NoOpFaultInjector(), TimeProvider.System, FieldSearchApprovalGate.Unavailable, humanCommandGate: Gate)
            .SubmitAsync(new VerifiedActor("https://idp", owner.ToString("N"), "test"), request, "revoke-without-step-up", Guid.NewGuid(), Ct));
        Assert.Equal("STEP_UP_REQUIRED", refused.ErrorCode);
    }

    [Theory]
    [InlineData("added edge")]
    [InlineData("owner edge")]
    [InlineData("other resource")]
    [InlineData("role change")]
    [InlineData("wrong reason")]
    public void ThePlanShapeGuardRefusesAnythingButRemovingAccess(string injection)
    {
        var share = new AuthorityResource { OrganizationId = org, ResourceEntityId = Guid.NewGuid(), ResourceTypeName = ResourceTypeNames.RecordShare, ParentResourceEntityId = record, ResourceLifecycleState = ResourceLifecycleStates.Active };
        var child = new AuthorityResource { OrganizationId = org, ResourceEntityId = Guid.NewGuid(), ResourceTypeName = ResourceTypeNames.RecordShare, ParentResourceEntityId = record, ResourceLifecycleState = ResourceLifecycleStates.Active };
        CommandPlan Plan()
        {
            var plan = new CommandPlan
            {
                ShareExpiry = new ShareExpiryEvidence(share.ResourceEntityId, record, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1, Guid.NewGuid()),
                InvalidationSourceShareId = share.ResourceEntityId,
            };
            plan.RevokeResources.Add(share);
            plan.InvalidatedDescendantShares.Add((child, new ShareResourceMetadata { RecordShareId = child.ResourceEntityId }, ShareInvalidationReasons.UpstreamExpired));
            plan.RemoveEdges.Add(new AuthorityRelationshipEdge { ResourceTypeName = ResourceTypeNames.RecordShare, ResourceEntityId = child.ResourceEntityId, RelationshipName = "reader", SubjectTypeName = ResourceTypeNames.User, SubjectEntityId = recipient });
            return plan;
        }

        ExpireSharePlanGuard.Require(Plan(), share.ResourceEntityId, record); // the legitimate shape passes

        var injected = Plan();
        switch (injection)
        {
            case "added edge":
                injected.AddEdges.Add((ResourceTypeNames.RecordShare, share.ResourceEntityId, "manager", ResourceTypeNames.User, recipient, ""));
                break;
            case "owner edge":
                injected.RemoveEdges.Add(new AuthorityRelationshipEdge { ResourceTypeName = ResourceTypeNames.FormRecord, ResourceEntityId = record, RelationshipName = "owner", SubjectTypeName = ResourceTypeNames.User, SubjectEntityId = owner });
                break;
            case "other resource":
                injected.RevokeResources.Add(new AuthorityResource { ResourceTypeName = ResourceTypeNames.Team, ResourceEntityId = Guid.NewGuid(), ResourceLifecycleState = ResourceLifecycleStates.Active });
                break;
            case "role change":
                injected.ShareMetadataUpdate = (new ShareResourceMetadata(), 2, null);
                break;
            case "wrong reason":
                injected.InvalidatedDescendantShares[0] = (child, new ShareResourceMetadata(), ShareInvalidationReasons.UpstreamRevoked);
                break;
        }

        Assert.Throws<InvalidOperationException>(() => ExpireSharePlanGuard.Require(injected, share.ResourceEntityId, record));
    }

    [Fact]
    public async Task AShareWhoseConvergenceFailsNeverStopsThePassAndAlertsAfterTwoPasses()
    {
        // C10 independent review F5: one share that cannot converge (here an inconsistent lineage below it) is counted as unconverged;
        // the other expired shares still converge in the same pass.
        var (share, _) = await ExpiringShareAsync();
        var (broken, _) = await ExpiringShareAsync(key: "update-expiry-4");
        var child = AddShare(broken);
        Metadata(child).DelegationDepth = 3; // not parent depth + 1: the descendant walk refuses it
        clock.Now = expiry.AddMinutes(1);
        var alerts = new Alerts();
        var loop = new ShareExpiryLoop(store, Convergence, alerts, clock, NullLogger<ShareExpiryLoop>.Instance);

        await loop.RunPassAsync(Ct);
        Assert.Equal(ResourceLifecycleStates.Expired, Resource(share).ResourceLifecycleState);
        Assert.Equal(ResourceLifecycleStates.Active, Resource(broken).ResourceLifecycleState);
        Project();
        await loop.RunPassAsync(Ct);
        await loop.RunPassAsync(Ct);
        Assert.Contains(alerts.Raised, a => a.AlertKind == ShareExpiryLoop.StuckAlertKind);
        Assert.False(loop.Stopped);
    }
}
