using AuthorizationAuthority.Application.Features.Authorization;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.Sharing;
using Xunit;

namespace AuthorizationAuthority.Tests;

public sealed class ShareLineageExpiryTests
{
    private static readonly Guid Organization = Guid.NewGuid();
    private static readonly Guid Record = Guid.NewGuid();
    private static readonly Guid Root = Guid.NewGuid();
    private static readonly Guid Child = Guid.NewGuid();
    private static readonly Guid Grandchild = Guid.NewGuid();
    private static readonly DateTimeOffset Deadline = new(2026, 11, 25, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EarliestAncestorExpiryAppliesAtTheExactInstant()
    {
        var shares = new Dictionary<Guid, ShareLineageNode>
        {
            [Root] = Node(Root, 1, expiry: Deadline),
            [Child] = Node(Child, 2, Root, Deadline.AddHours(3)),
            [Grandchild] = Node(Grandchild, 3, Child),
        };

        Assert.Equal(new(ShareLineageStatus.Active, Deadline), Evaluate(shares, Grandchild, Deadline.AddTicks(-1)));
        Assert.Equal(new(ShareLineageStatus.Expired, Deadline), Evaluate(shares, Grandchild, Deadline));
    }

    [Fact]
    public void EarlierLeafExpiryDoesNotExpireAnIndependentGrant()
    {
        var shares = new Dictionary<Guid, ShareLineageNode>
        {
            [Root] = Node(Root, 1),
            [Child] = Node(Child, 2, Root, Deadline),
        };

        Assert.Equal(new(ShareLineageStatus.Expired, Deadline), Evaluate(shares, Child, Deadline));
        Assert.Equal(new(ShareLineageStatus.Active), Evaluate(shares, Root, Deadline));
    }

    [Theory]
    [InlineData(ResourceLifecycleStates.Revoked)]
    [InlineData(ResourceLifecycleStates.Expired)]
    [InlineData(ResourceLifecycleStates.Invalidated)]
    public void TerminalParentCannotConferAccess(string state)
    {
        var parent = Node(Root, 1, expiry: Deadline.AddHours(3));
        parent.Resource.ResourceLifecycleState = state;
        var shares = new Dictionary<Guid, ShareLineageNode> { [Root] = parent, [Child] = Node(Child, 2, Root) };
        Assert.Equal(ShareLineageStatus.Invalid, Evaluate(shares, Child, Deadline).Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ParentMustBelongToTheSameOrganizationAndRecord(bool wrongOrganization)
    {
        var parent = Node(Root, 1);
        if (wrongOrganization)
        {
            parent.Resource.OrganizationId = Guid.NewGuid();
        }
        else
        {
            parent.Resource.ParentResourceEntityId = Guid.NewGuid();
        }

        var shares = new Dictionary<Guid, ShareLineageNode> { [Root] = parent, [Child] = Node(Child, 2, Root) };
        Assert.Equal(ShareLineageStatus.Invalid, Evaluate(shares, Child, Deadline).Status);
    }

    [Fact]
    public void LegacyShareDoesNotBecomeAClassifiedRoot()
    {
        var unresolved = Node(Root, 1) with { Metadata = null };
        var shares = new Dictionary<Guid, ShareLineageNode> { [Root] = unresolved, [Child] = Node(Child, 2, Root) };
        Assert.Equal(ShareLineageStatus.LegacyUnresolved, Evaluate(shares, Root, Deadline).Status);
        Assert.Equal(ShareLineageStatus.Invalid, Evaluate(shares, Child, Deadline).Status);
    }

    [Fact]
    public void MissingParentAndCyclicLineageFailClosed()
    {
        var shares = new Dictionary<Guid, ShareLineageNode> { [Child] = Node(Child, 2, Root) };
        Assert.Equal(ShareLineageStatus.Invalid, Evaluate(shares, Child, Deadline).Status);
        shares[Root] = Node(Root, 1, Child);
        Assert.Equal(ShareLineageStatus.Invalid, Evaluate(shares, Child, Deadline).Status);
    }

    private static ShareLineageEvaluation Evaluate(Dictionary<Guid, ShareLineageNode> shares, Guid share, DateTimeOffset at) =>
        ShareLineageExpiry.Evaluate(shares, Organization, Record, share, at);

    private static ShareLineageNode Node(Guid share, int depth, Guid? parent = null, DateTimeOffset? expiry = null) =>
        new(new AuthorityResource
        {
            OrganizationId = Organization,
            ParentResourceEntityId = Record,
            ResourceEntityId = share,
            ResourceTypeName = ResourceTypeNames.RecordShare,
            ResourceLifecycleState = ResourceLifecycleStates.Active,
        }, new ShareResourceMetadata
        {
            OrganizationId = Organization,
            RecordShareId = share,
            ParentRecordShareId = parent,
            DelegationDepth = depth,
            ShareExpiresOn = expiry,
        });
}
