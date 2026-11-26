using System.Text.Json;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.SecurityCommands;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using AuthorizationAuthority.Domain.Features.Sharing;
using AuthorizationAuthority.Infrastructure.Faults;
using Xunit;

namespace AuthorizationAuthority.Tests;

public sealed class ShareRevocationCascadeTests
{
    private static readonly Guid Organization = Guid.NewGuid();
    private static readonly Guid Record = Guid.NewGuid();
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Recipient = Guid.NewGuid();
    private static readonly Guid Item = Guid.NewGuid();
    private static readonly HumanCommandStepUpGate Gate =
        HumanCommandStepUpGate.Create(HumanCommandStepUpGate.SyntheticMode, "Testing", "TestIdentityProvider");
    private readonly InMemoryAuthorityStore store = new();

    public ShareRevocationCascadeTests()
    {
        store.Organizations.Add(new OrganizationAuthorizationState { OrganizationId = Organization, AuthorizationRevision = 5, ProjectionAppliedRevision = 5 });
        store.Bindings.Add(new IdentityBinding
        {
            IdentityBindingId = Guid.NewGuid(), OrganizationId = Organization, AuthorityUserId = Owner,
            IdentityIssuerUri = "https://idp", IdentitySubjectIdentifier = Owner.ToString("N"), IdentityBindingEnabled = true,
        });
        AddEdge(ResourceTypeNames.FormRecord, Record, "owner", ResourceTypeNames.User, Owner);
    }

    [Fact]
    public async Task RevocationInvalidatesDescendantsAndPreservesIndependentGrantsOnReplay()
    {
        var root = AddShare();
        var child = AddShare(root.Metadata);
        var grandchild = AddShare(child.Metadata);
        var independent = AddShare();
        var result = await RevokeAsync(root.Resource.ResourceEntityId);
        var replay = await RevokeAsync(root.Resource.ResourceEntityId, 6);

        Assert.Equal(result.CommandId, replay.CommandId);
        Assert.Equal(ResourceLifecycleStates.Revoked, root.Resource.ResourceLifecycleState);
        Assert.Equal(ResourceLifecycleStates.Invalidated, child.Resource.ResourceLifecycleState);
        Assert.Equal(ResourceLifecycleStates.Invalidated, grandchild.Resource.ResourceLifecycleState);
        Assert.Equal(ShareInvalidationReasons.UpstreamRevoked, child.Metadata.ShareInvalidationReasonCode);
        Assert.Equal(ShareInvalidationReasons.UpstreamRevoked, grandchild.Metadata.ShareInvalidationReasonCode);
        Assert.Equal(ResourceLifecycleStates.Active, independent.Resource.ResourceLifecycleState);
        Assert.Contains(store.Edges, e => e.SubjectEntityId == independent.Resource.ResourceEntityId && e.RemovedBySecurityCommandId is null);
        Assert.DoesNotContain(store.Edges, e => e.SubjectEntityId == child.Resource.ResourceEntityId && e.RemovedBySecurityCommandId is null);
        Assert.Single(store.Outbox);
        Assert.Equal(6, Assert.Single(store.Organizations).AuthorizationRevision);
        var command = Assert.Single(store.Commands);
        using var payload = JsonDocument.Parse(command.CommandPayloadJson);
        var ids = payload.RootElement.GetProperty("invalidated_descendant_share_ids").EnumerateArray().Select(id => id.GetGuid()).ToArray();
        Assert.Equal(new[] { child.Resource.ResourceEntityId, grandchild.Resource.ResourceEntityId }.Order().ToArray(), ids);
        Assert.DoesNotContain(HumanCommandStepUpGate.SyntheticStepUpEvidence, command.CommandPayloadJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MalformedDescendantDoesNotPartlyRevokeTheGraph(bool cycle)
    {
        var root = AddShare();
        var child = AddShare(root.Metadata);
        if (cycle)
        {
            root.Metadata.ParentRecordShareId = child.Resource.ResourceEntityId;
        }
        else
        {
            child.Resource.ParentResourceEntityId = Guid.NewGuid();
        }

        var error = await Assert.ThrowsAsync<AuthorityException>(() => RevokeAsync(root.Resource.ResourceEntityId));
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, error.ErrorCode);
        Assert.Equal(ResourceLifecycleStates.Active, root.Resource.ResourceLifecycleState);
        Assert.Equal(ResourceLifecycleStates.Active, child.Resource.ResourceLifecycleState);
        Assert.Empty(store.Outbox);
        Assert.All(store.Edges, edge => Assert.Null(edge.RemovedBySecurityCommandId));
    }

    [Fact]
    public async Task RevokeWorksForALegacyRootWithoutMetadataOrDependents()
    {
        var root = AddShare();
        store.ShareMetadata.Clear();
        await RevokeAsync(root.Resource.ResourceEntityId);
        Assert.Equal(ResourceLifecycleStates.Revoked, root.Resource.ResourceLifecycleState);
    }

    [Fact]
    public async Task AValidWideLineageCanBeRevoked()
    {
        var root = AddShare();
        var children = Enumerable.Range(0, 101).Select(_ => AddShare(root.Metadata)).ToArray();
        await RevokeAsync(root.Resource.ResourceEntityId);
        Assert.All(children, child => Assert.Equal(ResourceLifecycleStates.Invalidated, child.Resource.ResourceLifecycleState));
    }

    private (AuthorityResource Resource, ShareResourceMetadata Metadata) AddShare(ShareResourceMetadata? parent = null)
    {
        var id = Guid.NewGuid();
        var resource = new AuthorityResource
        {
            OrganizationId = Organization, ResourceEntityId = id, ResourceTypeName = ResourceTypeNames.RecordShare,
            ParentResourceEntityId = Record,
        };
        var metadata = new ShareResourceMetadata
        {
            OrganizationId = Organization, RecordShareId = id, ParentRecordShareId = parent?.RecordShareId,
            DelegationDepth = (parent?.DelegationDepth ?? 0) + 1,
        };
        store.Resources.Add(resource);
        store.ShareMetadata.Add(metadata);
        AddEdge(ResourceTypeNames.RecordShare, id, "reader", ResourceTypeNames.User, Recipient);
        AddEdge(ResourceTypeNames.FormRecord, Record, "shared_via", ResourceTypeNames.RecordShare, id);
        AddEdge(ResourceTypeNames.FieldItem, Item, "shared_via", ResourceTypeNames.RecordShare, id);
        return (resource, metadata);
    }

    private void AddEdge(string resourceType, Guid resource, string relation, string subjectType, Guid subject) =>
        store.Edges.Add(new AuthorityRelationshipEdge
        {
            AuthorityRelationshipEdgeId = Guid.NewGuid(), OrganizationId = Organization, ResourceTypeName = resourceType,
            ResourceEntityId = resource, RelationshipName = relation, SubjectTypeName = subjectType, SubjectEntityId = subject,
        });

    private Task<OperationStatus> RevokeAsync(Guid shareId, long revision = 5)
    {
        var service = new SecurityCommandService(store, new NoOpFaultInjector(), TimeProvider.System,
            FieldSearchApprovalGate.Unavailable, humanCommandGate: Gate);
        var payload = new RevokeSharePayload(shareId, Record, HumanCommandStepUpGate.SyntheticStepUpEvidence);
        var request = new SecurityCommandRequest(Organization, SecurityCommandKind.RevokeShare,
            new ResourceRef(ResourceKind.FormRecord, Record), revision, JsonSerializer.SerializeToElement(payload, CanonicalJson.Options));
        return service.SubmitAsync(new VerifiedActor("https://idp", Owner.ToString("N"), "test"), request,
            "revoke-with-cascade", Guid.NewGuid(), TestContext.Current.CancellationToken);
    }
}
