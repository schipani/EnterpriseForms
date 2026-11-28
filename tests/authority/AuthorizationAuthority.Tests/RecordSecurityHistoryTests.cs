using System.Text.Json;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.SecurityCommands;
using AuthorizationAuthority.Application.Features.Sharing;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Content;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using AuthorizationAuthority.Domain.Features.Sharing;
using AuthorizationAuthority.Infrastructure.Faults;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// C10 M1-WP9 (WP-0 Section 3.5, D10-11): the record-scoped sharing and ownership history is derived from committed Authority edges
/// of real commands: creation, scope change, revocation with cascaded invalidation and ownership transfer, newest first, each with
/// its own command state (a command awaiting projection is never shown as Effective); owners and ManageAccess holders read it; a
/// share's plain recipient, a former owner and other users are refused as for an unknown record.
/// </summary>
public sealed class RecordSecurityHistoryTests
{
    private static readonly HumanCommandStepUpGate Gate = HumanCommandStepUpGate.Create(HumanCommandStepUpGate.SyntheticMode, "Testing", "TestIdentityProvider");
    private readonly Guid org = Guid.NewGuid();
    private readonly Guid record = Guid.NewGuid();
    private readonly Guid owner = Guid.NewGuid();
    private readonly Guid newOwner = Guid.NewGuid();
    private readonly Guid manager = Guid.NewGuid();
    private readonly Guid reader = Guid.NewGuid();
    private readonly Guid stranger = Guid.NewGuid();
    private readonly Guid first = Guid.NewGuid();
    private readonly Guid second = Guid.NewGuid();
    private readonly InMemoryAuthorityStore store = new();
    private readonly ManualClock clock = new(DateTimeOffset.UtcNow);

    public RecordSecurityHistoryTests()
    {
        store.Organizations.Add(new OrganizationAuthorizationState { OrganizationId = org, AuthorizationRevision = 5, ProjectionAppliedRevision = 5 });
        foreach (var user in new[] { owner, newOwner, manager, reader, stranger })
        {
            store.Bindings.Add(new IdentityBinding { IdentityBindingId = Guid.NewGuid(), OrganizationId = org, AuthorityUserId = user, IdentityIssuerUri = "https://idp", IdentitySubjectIdentifier = user.ToString("N"), IdentityBindingEnabled = true });
        }

        store.Resources.Add(new AuthorityResource { OrganizationId = org, ResourceEntityId = record, ResourceTypeName = ResourceTypeNames.FormRecord, ResourceLifecycleState = ResourceLifecycleStates.Active });
        store.Edges.Add(new AuthorityRelationshipEdge { AuthorityRelationshipEdgeId = Guid.NewGuid(), OrganizationId = org, ResourceTypeName = ResourceTypeNames.FormRecord, ResourceEntityId = record, RelationshipName = "owner", SubjectTypeName = ResourceTypeNames.User, SubjectEntityId = owner });
        var commit = Guid.NewGuid();
        store.Heads.Add(new RecordContentHead { OrganizationId = org, FormRecordId = record, CurrentRecordManifestCommitId = commit });
        foreach (var item in new[] { first, second })
        {
            store.ManifestLeaves.Add(new RecordManifestLeaf { RecordManifestCommitId = commit, ManifestItemId = item, ManifestItemKindCode = (byte)EnterpriseForms.Integrity.Merkle.MerkleItemKind.FieldValue });
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Guid> SubmitAsync(Guid actor, SecurityCommandKind kind, object payload)
    {
        clock.Advance(TimeSpan.FromSeconds(1)); // distinct creation times, so "newest first" is observable
        var status = await new SecurityCommandService(store, new NoOpFaultInjector(), clock, FieldSearchApprovalGate.Unavailable, humanCommandGate: Gate).SubmitAsync(
            new VerifiedActor("https://idp", actor.ToString("N"), "test"),
            new SecurityCommandRequest(org, kind, new ResourceRef(ResourceKind.FormRecord, record), store.Organizations.Single().AuthorizationRevision, JsonSerializer.SerializeToElement(payload, CanonicalJson.Options)),
            "history-" + Guid.NewGuid().ToString("N")[..12], Guid.NewGuid(), Ct);
        return status.CommandId;
    }

    /// <summary>C10 re-review: a reader's own authority comes only from Effective grants, so tests that read through a new grant project it first.</summary>
    private void MakeEffective(params Guid[] commands)
    {
        foreach (var command in store.Commands.Where(c => commands.Length == 0 ? c.CommandStateName == SecurityCommandStates.CommittedAwaitingProjection : commands.Contains(c.SecurityCommandId)))
        {
            command.CommandStateName = SecurityCommandStates.Effective;
            command.CommandEffectiveOn = clock.GetUtcNow();
        }
    }

    private Task<RecordSecurityHistory> ReadAsync(Guid actor) =>
        new RecordSecurityHistoryService(store, clock).GetAsync(new VerifiedActor("https://idp", actor.ToString("N"), "test"), org, record, Ct);

    private async Task<(Guid ManagerShare, Guid Delegated, Guid Create, Guid Delegate, Guid Update, Guid Revoke, Guid Transfer)> HistoryAsync()
    {
        var managerShare = Guid.NewGuid();
        var delegated = Guid.NewGuid();
        var step = HumanCommandStepUpGate.SyntheticStepUpEvidence;
        var create = await SubmitAsync(owner, SecurityCommandKind.CreateShare, new CreateSharePayload(managerShare, record, manager, null, RecipientRoles.Manager, [first, second], step));
        var delegate_ = await SubmitAsync(manager, SecurityCommandKind.CreateShare, new CreateSharePayload(delegated, record, reader, null, RecipientRoles.Reader, [second], step, managerShare));
        var update = await SubmitAsync(owner, SecurityCommandKind.UpdateShareScope, new UpdateShareScopePayload(managerShare, record, RecipientRoles.Manager, [first, second], 1, step, null, clock.GetUtcNow().AddDays(30)));
        var revoke = await SubmitAsync(owner, SecurityCommandKind.RevokeShare, new RevokeSharePayload(managerShare, record, step));
        var transfer = await SubmitAsync(owner, SecurityCommandKind.TransferOwnership, new TransferOwnershipPayload(record, newOwner, null, step));
        return (managerShare, delegated, create, delegate_, update, revoke, transfer);
    }

    [Fact]
    public async Task TheHistoryListsEveryChangeNewestFirstWithItsOwnState()
    {
        var h = await HistoryAsync();
        MakeEffective(h.Transfer); // the new owner reads through the Effective transfer; the other commands stay pending

        var history = await ReadAsync(newOwner);

        Assert.False(history.Truncated);
        Assert.Equal(new[] { h.Transfer, h.Revoke, h.Update, h.Delegate, h.Create }, history.Items.Select(i => i.SecurityCommandId));
        Assert.Equal(SecurityCommandStates.Effective, history.Items[0].CommandState);
        Assert.All(history.Items.Skip(1), i => Assert.Equal(SecurityCommandStates.CommittedAwaitingProjection, i.CommandState)); // never claimed Effective
        var transfer = history.Items[0];
        Assert.Equal(nameof(SecurityCommandKind.TransferOwnership), transfer.CommandKind);
        Assert.Equal(owner, transfer.ActorUserId);
        Assert.Contains(transfer.Changes, c => c is { ChangeKind: "OwnerRemoved", SubjectKind: "User" } && c.SubjectId == owner);
        Assert.Contains(transfer.Changes, c => c is { ChangeKind: "OwnerAdded", SubjectKind: "User" } && c.SubjectId == newOwner);
        var revoke = history.Items[1];
        Assert.Contains(revoke.Changes, c => c.ChangeKind == "ShareEnded" && c.RecordShareId == h.ManagerShare && c.ShareEndState == ResourceLifecycleStates.Revoked);
        Assert.Contains(revoke.Changes, c => c.ChangeKind == "ShareEnded" && c.RecordShareId == h.Delegated && c.ShareEndState == ResourceLifecycleStates.Invalidated &&
                                             c.InvalidationReasonCode == ShareInvalidationReasons.UpstreamRevoked);
        Assert.Contains(history.Items[2].Changes, c => c.ChangeKind == "ShareScopeChanged" && c.RecordShareId == h.ManagerShare); // expiry-only update
        Assert.Contains(history.Items[3].Changes, c => c.ChangeKind == "ShareCreated" && c.RecordShareId == h.Delegated);
        Assert.Equal(manager, history.Items[3].ActorUserId);
        Assert.Contains(history.Items[4].Changes, c => c.ChangeKind == "ShareCreated" && c.RecordShareId == h.ManagerShare);
    }

    [Fact]
    public async Task AnEffectiveCommandIsReportedAsEffective()
    {
        var h = await HistoryAsync();
        var effective = store.Commands.Single(c => c.SecurityCommandId == h.Create);
        effective.CommandStateName = SecurityCommandStates.Effective;
        effective.CommandEffectiveOn = clock.GetUtcNow();
        MakeEffective(h.Transfer);

        var history = await ReadAsync(newOwner);

        var item = history.Items.Single(i => i.SecurityCommandId == h.Create);
        Assert.Equal(SecurityCommandStates.Effective, item.CommandState);
        Assert.NotNull(item.EffectiveOn);
        Assert.Equal(SecurityCommandStates.CommittedAwaitingProjection, history.Items.Single(i => i.SecurityCommandId == h.Revoke).CommandState);
    }

    [Fact]
    public async Task AnActiveManagerReadsButRecipientsFormerOwnersAndOthersDoNot()
    {
        var managerShare = Guid.NewGuid();
        var step = HumanCommandStepUpGate.SyntheticStepUpEvidence;
        await SubmitAsync(owner, SecurityCommandKind.CreateShare, new CreateSharePayload(managerShare, record, manager, null, RecipientRoles.Manager, [first], step));
        await SubmitAsync(owner, SecurityCommandKind.CreateShare, new CreateSharePayload(Guid.NewGuid(), record, reader, null, RecipientRoles.Reader, [first], step));
        MakeEffective();

        Assert.Equal(2, (await ReadAsync(manager)).Items.Count);
        Assert.Equal(2, (await ReadAsync(owner)).Items.Count);
        foreach (var refused in new[] { reader, stranger })
        {
            Assert.Equal(AuthorityErrorCodes.ResourceNotRegistered, (await Assert.ThrowsAsync<AuthorityException>(() => ReadAsync(refused))).ErrorCode);
        }

        await SubmitAsync(owner, SecurityCommandKind.TransferOwnership, new TransferOwnershipPayload(record, newOwner, null, step));
        Assert.Equal(AuthorityErrorCodes.ResourceNotRegistered, (await Assert.ThrowsAsync<AuthorityException>(() => ReadAsync(owner))).ErrorCode);

        // A revoked manager loses the read in the same committed revision (desired state, never behind the projection).
        await SubmitAsync(newOwner, SecurityCommandKind.RevokeShare, new RevokeSharePayload(managerShare, record, step));
        Assert.Equal(AuthorityErrorCodes.ResourceNotRegistered, (await Assert.ThrowsAsync<AuthorityException>(() => ReadAsync(manager))).ErrorCode);
    }

    [Fact]
    public async Task AnExpiredManagerShareConfersNoRead()
    {
        var step = HumanCommandStepUpGate.SyntheticStepUpEvidence;
        await SubmitAsync(owner, SecurityCommandKind.CreateShare, new CreateSharePayload(Guid.NewGuid(), record, manager, null, RecipientRoles.Manager, [first], step, null, clock.GetUtcNow().AddHours(2)));

        clock.Advance(TimeSpan.FromHours(3));

        Assert.Equal(AuthorityErrorCodes.ResourceNotRegistered, (await Assert.ThrowsAsync<AuthorityException>(() => ReadAsync(manager))).ErrorCode);
        Assert.Single((await ReadAsync(owner)).Items);
    }

    [Fact]
    public async Task EverySupersededExpiryOnlyUpdateIsListedWithTheExpiryItCommitted()
    {
        var share = Guid.NewGuid();
        var step = HumanCommandStepUpGate.SyntheticStepUpEvidence;
        var first = clock.GetUtcNow().AddDays(10);
        var second = clock.GetUtcNow().AddDays(20);
        await SubmitAsync(owner, SecurityCommandKind.CreateShare, new CreateSharePayload(share, record, reader, null, RecipientRoles.Reader, [this.first], step));
        var update1 = await SubmitAsync(owner, SecurityCommandKind.UpdateShareScope, new UpdateShareScopePayload(share, record, RecipientRoles.Reader, [this.first], 1, step, null, first));
        var update2 = await SubmitAsync(owner, SecurityCommandKind.UpdateShareScope, new UpdateShareScopePayload(share, record, RecipientRoles.Reader, [this.first], 2, step, null, second));

        var history = await ReadAsync(owner);

        var expiries = history.Items.Where(i => i.SecurityCommandId == update1 || i.SecurityCommandId == update2)
            .ToDictionary(i => i.SecurityCommandId, i => Assert.Single(i.Changes).ShareExpiresOn);
        Assert.Equal(first, expiries[update1]);
        Assert.Equal(second, expiries[update2]); // the superseded first update is kept with its own committed value
        Assert.All(history.Items, i => Assert.True(i.JournalBacked));
    }

    [Fact]
    public async Task ACommandWithoutAJournalEntryIsNotJournalBacked()
    {
        var command = await SubmitAsync(owner, SecurityCommandKind.CreateShare, new CreateSharePayload(Guid.NewGuid(), record, reader, null, RecipientRoles.Reader, [first], HumanCommandStepUpGate.SyntheticStepUpEvidence));
        store.Journal.RemoveAll(j => j.SecurityCommandId == command && j.JournalStreamName == AuthorizationAuthority.Domain.Features.Journal.JournalStreamNames.Authorization);

        Assert.False(Assert.Single((await ReadAsync(owner)).Items).JournalBacked);
    }

    [Fact]
    public async Task ARecordWithoutSharesHasAnEmptyHistory()
    {
        var history = await ReadAsync(owner);

        Assert.Empty(history.Items);
        Assert.Equal(5, history.AuthorizationRevision);
    }

    [Fact]
    public async Task OwnershipHistoryIsForTheOwnerAndSecurityAuditorsAndManagersSeeNoOwnerChanges()
    {
        // C10 independent review F-4/F3 (WP-0 Section 3.5).
        var auditor = Guid.NewGuid();
        store.Bindings.Add(new IdentityBinding { IdentityBindingId = Guid.NewGuid(), OrganizationId = org, AuthorityUserId = auditor, IdentityIssuerUri = "https://idp", IdentitySubjectIdentifier = auditor.ToString("N"), IdentityBindingEnabled = true });
        store.Edges.Add(new AuthorityRelationshipEdge { AuthorityRelationshipEdgeId = Guid.NewGuid(), OrganizationId = org, ResourceTypeName = ResourceTypeNames.Organization, ResourceEntityId = org, RelationshipName = OrganizationRoleNames.SecurityAuditor, SubjectTypeName = ResourceTypeNames.User, SubjectEntityId = auditor });
        var step = HumanCommandStepUpGate.SyntheticStepUpEvidence;
        await SubmitAsync(owner, SecurityCommandKind.CreateShare, new CreateSharePayload(Guid.NewGuid(), record, manager, null, RecipientRoles.Manager, [first], step));
        var transfer = await SubmitAsync(owner, SecurityCommandKind.TransferOwnership, new TransferOwnershipPayload(record, newOwner, null, step));
        MakeEffective();

        var asManager = await ReadAsync(manager); // the owner-granted manager share survives the transfer
        Assert.NotEmpty(asManager.Items);
        Assert.DoesNotContain(asManager.Items, i => i.SecurityCommandId == transfer);
        Assert.All(asManager.Items, i => Assert.DoesNotContain(i.Changes, c => c.ChangeKind is "OwnerAdded" or "OwnerRemoved"));
        Assert.Equal(AuthorityErrorCodes.ResourceNotRegistered, (await Assert.ThrowsAsync<AuthorityException>(() => ReadOwnershipAsync(manager))).ErrorCode);

        foreach (var reader in new[] { newOwner, auditor })
        {
            var ownership = await ReadOwnershipAsync(reader);
            var item = Assert.Single(ownership.Items);
            Assert.Equal(transfer, item.SecurityCommandId);
            Assert.All(item.Changes, c => Assert.True(c.ChangeKind is "OwnerAdded" or "OwnerRemoved"));
        }

        Assert.Equal(AuthorityErrorCodes.ResourceNotRegistered, (await Assert.ThrowsAsync<AuthorityException>(() => ReadAsync(auditor))).ErrorCode); // share history: not an auditor read
        Assert.Contains((await ReadAsync(newOwner)).Items, i => i.SecurityCommandId == transfer); // the owner sees every change
    }

    [Fact]
    public async Task AGrantAwaitingItsProjectionConfersNoReadButItsCommandIsListedToReadersInEffect()
    {
        // C10 re-review (F-5/F3 residual, A11): a reader's own authority is never a pending grant.
        var step = HumanCommandStepUpGate.SyntheticStepUpEvidence;
        var create = await SubmitAsync(owner, SecurityCommandKind.CreateShare, new CreateSharePayload(Guid.NewGuid(), record, manager, null, RecipientRoles.Manager, [first], step));

        Assert.Equal(AuthorityErrorCodes.ResourceNotRegistered, (await Assert.ThrowsAsync<AuthorityException>(() => ReadAsync(manager))).ErrorCode);
        var listed = Assert.Single((await ReadAsync(owner)).Items); // the owner's grant predates the command
        Assert.Equal((create, SecurityCommandStates.CommittedAwaitingProjection), (listed.SecurityCommandId, listed.CommandState));

        MakeEffective(create);
        Assert.Single((await ReadAsync(manager)).Items);
    }

    [Fact]
    public async Task AnOwnerChangeOlderThanTheCommandLimitStaysInTheOwnershipView()
    {
        // C10 review N-2: the ownership view reads the commands of the record's owner edges, so newer commands never push an owner change out.
        var transfer = await SubmitAsync(owner, SecurityCommandKind.TransferOwnership, new TransferOwnershipPayload(record, newOwner, null, HumanCommandStepUpGate.SyntheticStepUpEvidence));
        MakeEffective(transfer);
        var binding = store.Bindings.First(b => b.AuthorityUserId == owner).IdentityBindingId;
        for (var i = 0; i < RecordSecurityHistoryLimits.MaximumCommands + 1; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            store.Commands.Add(new SecurityCommand
            {
                SecurityCommandId = Guid.NewGuid(), OrganizationId = org, TargetFormRecordId = record, CommandKindName = "UpdateShareScope", ActorIdentityBindingId = binding,
                CommandStateName = SecurityCommandStates.Effective, CommandCreatedOn = clock.GetUtcNow(), CommandEffectiveOn = clock.GetUtcNow(),
            });
        }

        var history = await new RecordSecurityHistoryService(store, clock).GetAsync(new VerifiedActor("https://idp", newOwner.ToString("N"), "test"), org, record, ownershipView: true, Ct);

        Assert.False(history.Truncated);
        Assert.Equal(transfer, Assert.Single(history.Items).SecurityCommandId);
    }

    [Fact]
    public async Task TheHistoryIsRefusedWhileTheOrganizationIsQuarantined()
    {
        store.Organizations.Single().IntegrityQuarantineEnabled = true;

        Assert.Equal(AuthorityErrorCodes.IntegrityQuarantine, (await Assert.ThrowsAsync<AuthorityException>(() => ReadAsync(owner))).ErrorCode);
        Assert.Equal(AuthorityErrorCodes.IntegrityQuarantine, (await Assert.ThrowsAsync<AuthorityException>(() => ReadOwnershipAsync(owner))).ErrorCode);
    }

    private Task<RecordSecurityHistory> ReadOwnershipAsync(Guid actor) =>
        new RecordSecurityHistoryService(store, clock).GetAsync(new VerifiedActor("https://idp", actor.ToString("N"), "test"), org, record, ownershipView: true, Ct);
}
