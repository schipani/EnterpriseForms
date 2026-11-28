using System.Text.Json;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.SecurityCommands;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Content;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using AuthorizationAuthority.Domain.Features.Sharing;
using AuthorizationAuthority.Infrastructure.Faults;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// G27: Human security commands (CreateShare, RevokeShare, TransferOwnership) require step-up assurance.
/// These tests validate that the HumanCommandStepUpGate blocks commands without valid synthetic step-up evidence,
/// and allows commands with valid evidence when the synthetic path is configured.
/// </summary>
public sealed class HumanCommandStepUpTests
{
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly Guid Form = Guid.CreateVersion7();
    private static readonly Guid Record = Guid.CreateVersion7();
    private static readonly Guid Owner = Guid.CreateVersion7();
    private static readonly Guid Recipient = Guid.CreateVersion7();
    private static readonly Guid FieldValue1 = Guid.CreateVersion7();
    private static readonly Guid ManifestCommit = Guid.CreateVersion7();

    private static readonly HumanCommandStepUpGate Synthetic = HumanCommandStepUpGate.Create(HumanCommandStepUpGate.SyntheticMode, "Testing", "TestIdentityProvider");
    private static readonly HumanCommandStepUpGate Unavailable = HumanCommandStepUpGate.Unavailable;

    private static readonly FieldSearchApprovalGate FieldSearchSynthetic = FieldSearchApprovalGate.Create(FieldSearchApprovalGate.SyntheticMode, "Testing", "TestIdentityProvider");

    private readonly InMemoryAuthorityStore store = new();

    public HumanCommandStepUpTests()
    {
        store.Organizations.Add(new OrganizationAuthorizationState { OrganizationId = Org, AuthorizationRevision = 5, ProjectionAppliedRevision = 5 });
        foreach (var user in new[] { Owner, Recipient })
        {
            store.Bindings.Add(new IdentityBinding { IdentityBindingId = Guid.CreateVersion7(), OrganizationId = Org, AuthorityUserId = user, IdentityIssuerUri = "https://idp", IdentitySubjectIdentifier = user.ToString("N"), IdentityBindingEnabled = true });
        }

        store.Resources.Add(new AuthorityResource { AuthorityResourceId = Guid.CreateVersion7(), OrganizationId = Org, ResourceTypeName = ResourceTypeNames.FormDefinition, ResourceEntityId = Form, ResourceLifecycleState = ResourceLifecycleStates.Active });
        store.Resources.Add(new AuthorityResource { AuthorityResourceId = Guid.CreateVersion7(), OrganizationId = Org, ResourceTypeName = ResourceTypeNames.FormRecord, ResourceEntityId = Record, ParentResourceEntityId = Form, ResourceLifecycleState = ResourceLifecycleStates.Active });
        store.Resources.Add(new AuthorityResource { AuthorityResourceId = Guid.CreateVersion7(), OrganizationId = Org, ResourceTypeName = ResourceTypeNames.FieldItem, ResourceEntityId = FieldValue1, ParentResourceEntityId = Record, ResourceLifecycleState = ResourceLifecycleStates.Active });
        store.Edges.Add(Edge(ResourceTypeNames.FormRecord, Record, "owner", ResourceTypeNames.User, Owner));
        store.Edges.Add(Edge(ResourceTypeNames.FieldItem, FieldValue1, "value_of", ResourceTypeNames.FormRecord, Record));

        // Add a committed content head with an accepted manifest so CreateShare can validate field items.
        store.Heads.Add(new RecordContentHead
        {
            RecordContentHeadId = Guid.CreateVersion7(),
            OrganizationId = Org,
            FormRecordId = Record,
            CommittedContentRevision = 1,
            CurrentRecordManifestCommitId = ManifestCommit,
            CommittedManifestRoot = new byte[32],
            SignatureKeyVersion = 1,
            ContentHeadModifiedOn = DateTimeOffset.UtcNow,
        });
        store.ManifestLeaves.Add(new RecordManifestLeaf
        {
            RecordManifestLeafId = Guid.CreateVersion7(),
            RecordManifestCommitId = ManifestCommit,
            ManifestItemId = FieldValue1,
            ManifestItemKindCode = (byte)EnterpriseForms.Integrity.Merkle.MerkleItemKind.FieldValue,
        });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AuthorityRelationshipEdge Edge(string resourceType, Guid resourceId, string relation, string subjectType, Guid subjectId, string subjectRelation = "") => new()
    {
        AuthorityRelationshipEdgeId = Guid.CreateVersion7(),
        OrganizationId = Org,
        ResourceTypeName = resourceType,
        ResourceEntityId = resourceId,
        RelationshipName = relation,
        SubjectTypeName = subjectType,
        SubjectEntityId = subjectId,
        SubjectRelationName = subjectRelation,
    };

    private BoundActor Actor(Guid user) => new(new VerifiedActor("https://idp", user.ToString("N"), "test"), store.Bindings.Single(b => b.AuthorityUserId == user));

    private async Task<CommandPlan> PlanAsync(Guid user, SecurityCommandKind kind, ResourceKind resourceKind, Guid resourceId, object payload, HumanCommandStepUpGate? humanGate = null, FieldSearchApprovalGate? fieldGate = null)
    {
        await using var scope = await store.BeginScopeAsync(Ct);
        var graph = new DesiredStateGraph(store.Edges.Where(e => e.RemovedBySecurityCommandId is null).ToList());
        var request = new SecurityCommandRequest(Org, kind, new ResourceRef(resourceKind, resourceId), 5, JsonSerializer.SerializeToElement(payload, CanonicalJson.Options));
        return await CommandPlanner.PlanAsync(scope, Org, Actor(user), request, graph, Ct, fieldGate ?? FieldSearchSynthetic, humanCommandGate: humanGate ?? Synthetic);
    }

    // ---- Gate configuration tests ----

    [Fact]
    public void GateCreateFailsForSyntheticModeOutsideDevOrTesting()
    {
        Assert.Throws<InvalidOperationException>(() =>
            HumanCommandStepUpGate.Create(HumanCommandStepUpGate.SyntheticMode, "Production", "TestIdentityProvider"));
        Assert.Throws<InvalidOperationException>(() =>
            HumanCommandStepUpGate.Create(HumanCommandStepUpGate.SyntheticMode, "Staging", "TestIdentityProvider"));
    }

    [Fact]
    public void GateCreateFailsForSyntheticModeWithNonTestProvider()
    {
        Assert.Throws<InvalidOperationException>(() =>
            HumanCommandStepUpGate.Create(HumanCommandStepUpGate.SyntheticMode, "Development", "EntraId"));
        Assert.Throws<InvalidOperationException>(() =>
            HumanCommandStepUpGate.Create(HumanCommandStepUpGate.SyntheticMode, "Testing", "EntraId"));
    }

    [Fact]
    public void GateCreateSucceedsForUnavailableInAnyEnvironment()
    {
        var gate = HumanCommandStepUpGate.Create("Unavailable", "Production", "EntraId");
        Assert.False(gate.StepUpOperational);
        Assert.Equal(HumanCommandStepUpGate.UnavailableReasonCode, gate.UnavailableReason);
    }

    [Fact]
    public void GateCreateSucceedsForSyntheticModeInDevOrTestingWithTestProvider()
    {
        var devGate = HumanCommandStepUpGate.Create(HumanCommandStepUpGate.SyntheticMode, "Development", "TestIdentityProvider");
        Assert.True(devGate.StepUpOperational);
        Assert.Equal(string.Empty, devGate.UnavailableReason);

        var testGate = HumanCommandStepUpGate.Create(HumanCommandStepUpGate.SyntheticMode, "Testing", "TestIdentityProvider");
        Assert.True(testGate.StepUpOperational);
    }

    // ---- CreateShare step-up tests ----

    [Fact]
    public async Task CreateShareSucceedsWithValidStepUpEvidence()
    {
        var newShare = Guid.CreateVersion7();
        var plan = await PlanAsync(Owner, SecurityCommandKind.CreateShare, ResourceKind.FormRecord, Record,
            new CreateSharePayload(newShare, Record, Recipient, null, RecipientRoles.Reader, [FieldValue1], HumanCommandStepUpGate.SyntheticStepUpEvidence),
            Synthetic);

        Assert.NotEmpty(plan.NormalizedPayloadJson);
        Assert.DoesNotContain(HumanCommandStepUpGate.SyntheticStepUpEvidence, plan.NormalizedPayloadJson, StringComparison.Ordinal);
        var resource = Assert.Single(plan.RegisterResources, r => r.ResourceTypeName == ResourceTypeNames.RecordShare);
        Assert.Equal(newShare, resource.ResourceEntityId);
    }

    [Fact]
    public async Task CreateShareFailsWithoutStepUpEvidence()
    {
        var newShare = Guid.CreateVersion7();
        var ex = await Assert.ThrowsAsync<AuthorityException>(() =>
            PlanAsync(Owner, SecurityCommandKind.CreateShare, ResourceKind.FormRecord, Record,
                new CreateSharePayload(newShare, Record, Recipient, null, RecipientRoles.Reader, [FieldValue1], null),
                Synthetic));

        Assert.Equal(AuthorityErrorCodes.StepUpRequired, ex.ErrorCode);
    }

    [Fact]
    public async Task CreateShareFailsWithInvalidStepUpEvidence()
    {
        var newShare = Guid.CreateVersion7();
        var ex = await Assert.ThrowsAsync<AuthorityException>(() =>
            PlanAsync(Owner, SecurityCommandKind.CreateShare, ResourceKind.FormRecord, Record,
                new CreateSharePayload(newShare, Record, Recipient, null, RecipientRoles.Reader, [FieldValue1], "wrong-evidence"),
                Synthetic));

        Assert.Equal(AuthorityErrorCodes.StepUpRequired, ex.ErrorCode);
    }

    [Fact]
    public async Task CreateShareFailsWhenGateIsUnavailableEvenWithEvidence()
    {
        // WP0 staging/production: gate unavailable => PB08_STEP_UP_NOT_AVAILABLE
        var newShare = Guid.CreateVersion7();
        var ex = await Assert.ThrowsAsync<AuthorityException>(() =>
            PlanAsync(Owner, SecurityCommandKind.CreateShare, ResourceKind.FormRecord, Record,
                new CreateSharePayload(newShare, Record, Recipient, null, RecipientRoles.Reader, [FieldValue1], HumanCommandStepUpGate.SyntheticStepUpEvidence),
                Unavailable));

        Assert.Equal(HumanCommandStepUpGate.UnavailableReasonCode, ex.ErrorCode);
        Assert.Contains("not available", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(5, AuthorityErrorCodes.StepUpRequired)]
    [InlineData(4, AuthorityErrorCodes.StaleAuthorizationRevision)]
    public async Task RejectedHumanCommandKeepsNoUntrustedEvidence(long expectedRevision, string expectedError)
    {
        var service = new SecurityCommandService(store, new NoOpFaultInjector(), TimeProvider.System, FieldSearchSynthetic,
            humanCommandGate: Synthetic);
        var payload = new CreateSharePayload(Guid.CreateVersion7(), Record, Recipient, null, RecipientRoles.Reader, [FieldValue1], "raw evidence");
        var request = new SecurityCommandRequest(Org, SecurityCommandKind.CreateShare, new ResourceRef(ResourceKind.FormRecord, Record),
            expectedRevision, JsonSerializer.SerializeToElement(payload, CanonicalJson.Options));

        var error = await Assert.ThrowsAsync<AuthorityException>(() =>
            service.SubmitAsync(new VerifiedActor("https://idp", Owner.ToString("N"), "test"), request, "rejected-step-up", Guid.NewGuid(), Ct));

        Assert.Equal(expectedError, error.ErrorCode);
        var command = Assert.Single(store.Commands);
        Assert.Equal(SecurityCommandStates.Rejected, command.CommandStateName);
        Assert.Equal("{}", command.CommandPayloadJson);
        Assert.Empty(store.Outbox);
        Assert.Equal(5, Assert.Single(store.Organizations).AuthorizationRevision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdempotentReplayKeepsTheExistingCommandWithOrWithoutAnExplicitMarker(bool useMarker)
    {
        var binding = store.Bindings.Single(b => b.AuthorityUserId == Owner);
        var service = new SecurityCommandService(store, new NoOpFaultInjector(), TimeProvider.System, FieldSearchSynthetic,
            humanCommandGate: Synthetic);
        var share = Guid.NewGuid();
        var payload = useMarker
            ? JsonSerializer.SerializeToElement(new
            {
                record_share_id = share,
                form_record_id = Record,
                step_up_evidence = HumanCommandStepUpGate.SyntheticStepUpEvidence,
            })
            : JsonSerializer.SerializeToElement(new { record_share_id = share, form_record_id = Record });
        var request = new SecurityCommandRequest(Org, SecurityCommandKind.RevokeShare, new ResourceRef(ResourceKind.FormRecord, Record), 0, payload);
        var stored = new SecurityCommand
        {
            OrganizationId = Org,
            SecurityCommandId = Guid.NewGuid(),
            ActorIdentityBindingId = binding.IdentityBindingId,
            CommandKindName = nameof(SecurityCommandKind.RevokeShare),
            CommandStateName = SecurityCommandStates.Effective,
            CommandIdempotencyKey = "replay-step-up",
            CommittedAuthorizationRevision = 5,
            CommandRequestDigest = CanonicalJson.Digest(CanonicalJson.SerializeBytes(request)),
        };
        store.Commands.Add(stored);

        var status = await service.SubmitAsync(new VerifiedActor("https://idp", Owner.ToString("N"), "test"),
            request with { ExpectedAuthorizationRevision = 5 }, stored.CommandIdempotencyKey, Guid.NewGuid(), Ct);

        Assert.Equal(stored.SecurityCommandId, status.CommandId);
        Assert.Single(store.Commands);
        Assert.Empty(store.Outbox);
    }

    [Theory]
    [InlineData(4, 1, AuthorityErrorCodes.StaleAuthorizationRevision)]
    [InlineData(5, 2, AuthorityErrorCodes.StaleShareRevision)]
    public async Task ScopeUpdateRevisionsAreDistinctAndRejectionLeavesTheShareUnchanged(long organizationRevision, long shareRevision, string expectedError)
    {
        var metadata = SeedShareForScopeUpdate();
        var payload = new UpdateShareScopePayload(metadata.RecordShareId, Record, RecipientRoles.Contributor, [FieldValue1],
            shareRevision, HumanCommandStepUpGate.SyntheticStepUpEvidence);
        var request = new SecurityCommandRequest(Org, SecurityCommandKind.UpdateShareScope, new ResourceRef(ResourceKind.FormRecord, Record),
            organizationRevision, JsonSerializer.SerializeToElement(payload, CanonicalJson.Options));
        var service = new SecurityCommandService(store, new NoOpFaultInjector(), TimeProvider.System, FieldSearchSynthetic, humanCommandGate: Synthetic);

        var error = await Assert.ThrowsAsync<AuthorityException>(() => service.SubmitAsync(
            new VerifiedActor("https://idp", Owner.ToString("N"), "test"), request, "update-share-test", Guid.NewGuid(), Ct));

        Assert.Equal(expectedError, error.ErrorCode);
        Assert.Equal(1, metadata.ShareRevision);
        Assert.Empty(store.Outbox);
        Assert.Equal("{}", Assert.Single(store.Commands).CommandPayloadJson);
        Assert.Equal(5, Assert.Single(store.Organizations).AuthorizationRevision);
    }

    [Fact]
    public async Task AppliedScopeUpdateBindsNewExpiryAndAnIdempotentReplayDoesNotAdvanceIt()
    {
        var metadata = SeedShareForScopeUpdate();
        var expiry = DateTimeOffset.UtcNow.AddHours(2);
        var payload = new UpdateShareScopePayload(metadata.RecordShareId, Record, RecipientRoles.Contributor, [FieldValue1],
            1, HumanCommandStepUpGate.SyntheticStepUpEvidence, ShareExpiresOn: expiry);
        var request = new SecurityCommandRequest(Org, SecurityCommandKind.UpdateShareScope, new ResourceRef(ResourceKind.FormRecord, Record), 5,
            JsonSerializer.SerializeToElement(payload, CanonicalJson.Options));
        var service = new SecurityCommandService(store, new NoOpFaultInjector(), TimeProvider.System, FieldSearchSynthetic, humanCommandGate: Synthetic);
        var actor = new VerifiedActor("https://idp", Owner.ToString("N"), "test");

        var status = await service.SubmitAsync(actor, request, "update-share-test", Guid.NewGuid(), Ct);
        var replay = await service.SubmitAsync(actor, request with { ExpectedAuthorizationRevision = 6 }, "update-share-test", Guid.NewGuid(), Ct);

        Assert.Equal(status.CommandId, replay.CommandId);
        Assert.Equal(expiry, metadata.ShareExpiresOn);
        Assert.Equal(status.CommandId, metadata.ExpirySourceSecurityCommandId);
        Assert.Equal(2, metadata.ShareRevision);
        Assert.Single(store.Outbox);
        Assert.DoesNotContain(HumanCommandStepUpGate.SyntheticStepUpEvidence, Assert.Single(store.Commands).CommandPayloadJson, StringComparison.Ordinal);
    }

    private ShareResourceMetadata SeedShareForScopeUpdate()
    {
        var id = Guid.NewGuid();
        store.Resources.Add(new AuthorityResource
        {
            OrganizationId = Org, ResourceTypeName = ResourceTypeNames.RecordShare, ResourceEntityId = id, ParentResourceEntityId = Record,
        });
        store.Edges.Add(Edge(ResourceTypeNames.FormRecord, Record, "shared_via", ResourceTypeNames.RecordShare, id));
        store.Edges.Add(Edge(ResourceTypeNames.RecordShare, id, "reader", ResourceTypeNames.User, Recipient));
        store.Edges.Add(Edge(ResourceTypeNames.FieldItem, FieldValue1, "shared_via", ResourceTypeNames.RecordShare, id));
        var metadata = new ShareResourceMetadata { OrganizationId = Org, RecordShareId = id };
        store.ShareMetadata.Add(metadata);
        return metadata;
    }

    // ---- RevokeShare step-up tests ----

    [Fact]
    public async Task RevokeShareSucceedsWithValidStepUpEvidence()
    {
        // First create a share to revoke.
        var shareId = Guid.CreateVersion7();
        var createPlan = await PlanAsync(Owner, SecurityCommandKind.CreateShare, ResourceKind.FormRecord, Record,
            new CreateSharePayload(shareId, Record, Recipient, null, RecipientRoles.Reader, [FieldValue1], HumanCommandStepUpGate.SyntheticStepUpEvidence),
            Synthetic);
        // Apply the create plan.
        foreach (var r in createPlan.RegisterResources)
        {
            r.RegisteredBySecurityCommandId = Guid.CreateVersion7();
            store.Resources.Add(r);
        }
        foreach (var e in createPlan.AddEdges)
        {
            store.Edges.Add(Edge(e.ResourceType, e.ResourceId, e.Relation, e.SubjectType, e.SubjectId, e.SubjectRelation));
        }

        // Now revoke with valid evidence.
        var revokePlan = await PlanAsync(Owner, SecurityCommandKind.RevokeShare, ResourceKind.FormRecord, Record,
            new RevokeSharePayload(shareId, Record, HumanCommandStepUpGate.SyntheticStepUpEvidence),
            Synthetic);

        Assert.NotEmpty(revokePlan.NormalizedPayloadJson);
        Assert.DoesNotContain(HumanCommandStepUpGate.SyntheticStepUpEvidence, revokePlan.NormalizedPayloadJson, StringComparison.Ordinal);
        Assert.Single(revokePlan.RevokeResources);
    }

    [Fact]
    public async Task RevokeShareFailsWithoutStepUpEvidence()
    {
        // First create a share.
        var shareId = Guid.CreateVersion7();
        var createPlan = await PlanAsync(Owner, SecurityCommandKind.CreateShare, ResourceKind.FormRecord, Record,
            new CreateSharePayload(shareId, Record, Recipient, null, RecipientRoles.Reader, [FieldValue1], HumanCommandStepUpGate.SyntheticStepUpEvidence),
            Synthetic);
        foreach (var r in createPlan.RegisterResources)
        {
            r.RegisteredBySecurityCommandId = Guid.CreateVersion7();
            store.Resources.Add(r);
        }
        foreach (var e in createPlan.AddEdges)
        {
            store.Edges.Add(Edge(e.ResourceType, e.ResourceId, e.Relation, e.SubjectType, e.SubjectId, e.SubjectRelation));
        }

        // Now try to revoke without evidence.
        var ex = await Assert.ThrowsAsync<AuthorityException>(() =>
            PlanAsync(Owner, SecurityCommandKind.RevokeShare, ResourceKind.FormRecord, Record,
                new RevokeSharePayload(shareId, Record, null),
                Synthetic));

        Assert.Equal(AuthorityErrorCodes.StepUpRequired, ex.ErrorCode);
    }

    [Fact]
    public async Task RevokeShareFailsWhenGateIsUnavailable()
    {
        // WP0 staging/production: gate unavailable => PB08_STEP_UP_NOT_AVAILABLE
        var shareId = Guid.CreateVersion7();
        var ex = await Assert.ThrowsAsync<AuthorityException>(() =>
            PlanAsync(Owner, SecurityCommandKind.RevokeShare, ResourceKind.FormRecord, Record,
                new RevokeSharePayload(shareId, Record, HumanCommandStepUpGate.SyntheticStepUpEvidence),
                Unavailable));

        Assert.Equal(HumanCommandStepUpGate.UnavailableReasonCode, ex.ErrorCode);
        Assert.Contains("not available", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- TransferOwnership step-up tests ----

    [Fact]
    public async Task TransferOwnershipSucceedsWithValidStepUpEvidence()
    {
        var plan = await PlanAsync(Owner, SecurityCommandKind.TransferOwnership, ResourceKind.FormRecord, Record,
            new TransferOwnershipPayload(Record, Recipient, null, HumanCommandStepUpGate.SyntheticStepUpEvidence),
            Synthetic);

        Assert.NotEmpty(plan.NormalizedPayloadJson);
        Assert.DoesNotContain(HumanCommandStepUpGate.SyntheticStepUpEvidence, plan.NormalizedPayloadJson, StringComparison.Ordinal);
        Assert.Contains(plan.AddEdges, e => e.Relation == "owner" && e.SubjectId == Recipient);
    }

    [Fact]
    public async Task TransferOwnershipFailsWithoutStepUpEvidence()
    {
        var ex = await Assert.ThrowsAsync<AuthorityException>(() =>
            PlanAsync(Owner, SecurityCommandKind.TransferOwnership, ResourceKind.FormRecord, Record,
                new TransferOwnershipPayload(Record, Recipient, null, null),
                Synthetic));

        Assert.Equal(AuthorityErrorCodes.StepUpRequired, ex.ErrorCode);
    }

    [Fact]
    public async Task TransferOwnershipFailsWithInvalidStepUpEvidence()
    {
        var ex = await Assert.ThrowsAsync<AuthorityException>(() =>
            PlanAsync(Owner, SecurityCommandKind.TransferOwnership, ResourceKind.FormRecord, Record,
                new TransferOwnershipPayload(Record, Recipient, null, "bogus-evidence"),
                Synthetic));

        Assert.Equal(AuthorityErrorCodes.StepUpRequired, ex.ErrorCode);
    }

    [Fact]
    public async Task TransferOwnershipFailsWhenGateIsUnavailable()
    {
        // WP0 staging/production: gate unavailable => PB08_STEP_UP_NOT_AVAILABLE
        var ex = await Assert.ThrowsAsync<AuthorityException>(() =>
            PlanAsync(Owner, SecurityCommandKind.TransferOwnership, ResourceKind.FormRecord, Record,
                new TransferOwnershipPayload(Record, Recipient, null, HumanCommandStepUpGate.SyntheticStepUpEvidence),
                Unavailable));

        Assert.Equal(HumanCommandStepUpGate.UnavailableReasonCode, ex.ErrorCode);
        Assert.Contains("not available", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Evidence is stripped from journaled payload ----

    [Fact]
    public async Task StepUpEvidenceIsNotPresentInNormalizedPayloads()
    {
        // CreateShare
        var createPlan = await PlanAsync(Owner, SecurityCommandKind.CreateShare, ResourceKind.FormRecord, Record,
            new CreateSharePayload(Guid.CreateVersion7(), Record, Recipient, null, RecipientRoles.Reader, [FieldValue1], HumanCommandStepUpGate.SyntheticStepUpEvidence),
            Synthetic);
        Assert.DoesNotContain("synthetic_step_up_", createPlan.NormalizedPayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(HumanCommandStepUpGate.SyntheticStepUpEvidence, createPlan.NormalizedPayloadJson, StringComparison.Ordinal);

        // TransferOwnership
        var transferPlan = await PlanAsync(Owner, SecurityCommandKind.TransferOwnership, ResourceKind.FormRecord, Record,
            new TransferOwnershipPayload(Record, Recipient, null, HumanCommandStepUpGate.SyntheticStepUpEvidence),
            Synthetic);
        Assert.DoesNotContain("synthetic_step_up_", transferPlan.NormalizedPayloadJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(HumanCommandStepUpGate.SyntheticStepUpEvidence, transferPlan.NormalizedPayloadJson, StringComparison.Ordinal);
    }
}
