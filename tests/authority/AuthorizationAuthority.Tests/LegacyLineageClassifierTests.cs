using System.Text.Json;
using AuthorizationAuthority.Application.Features.Lineage;
using AuthorizationAuthority.Domain.Features.Lineage;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using AuthorizationAuthority.Domain.Features.Sharing;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// C10 M1-WP10: unit tests for the legacy lineage classification service.
/// Covers all classification outcomes defined in Section 5.1.
/// </summary>
public sealed class LegacyLineageClassifierTests
{
    private static readonly DateTimeOffset ObservedOn = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly Guid Record = Guid.CreateVersion7();
    private static readonly Guid OwnerUser = Guid.CreateVersion7();
    private static readonly Guid ManagerUser = Guid.CreateVersion7();
    private static readonly Guid RecipientUser = Guid.CreateVersion7();
    private static readonly Guid FieldItem1 = Guid.CreateVersion7();
    private static readonly Guid FieldItem2 = Guid.CreateVersion7();
    private static readonly Guid Doc1 = Guid.CreateVersion7();
    private static readonly byte[] TestKeyMaterial = new byte[32];

    static LegacyLineageClassifierTests()
    {
        Random.Shared.NextBytes(TestKeyMaterial);
    }


    #region OwnerGranted Tests

    [Fact]
    public void ClassifyShareOwnerGrantedByDirectOwnerReturnsOwnerGrantedWithDepth1()
    {
        // Arrange
        var shareId = Guid.CreateVersion7();
        var share = CreateShareResource(shareId, Record);
        var shareEdges = CreateShareEdges(shareId, RecipientUser, RecipientKind.User, "reader");
        var ownerInfo = new OwnerInfoAtRegistration
        {
            OwnerUserId = OwnerUser,
            IsVerifiable = true
        };
        var grantsSnapshot = new RecordGrantsSnapshot { ObservedOn = ObservedOn, ActiveShares = [] };
        var pseudonymizer = new LineagePseudonymizer(Org, TestKeyMaterial);

        // Act
        var result = LegacyLineageClassifier.ClassifyShare(
            share,
            null,
            shareEdges,
            new HashSet<Guid> { FieldItem1 },
            [],
            grantorAtRegistration: OwnerUser,
            ownerAtRegistration: ownerInfo,
            grantsSnapshot,
            pseudonymizer);

        // Assert
        Assert.Equal(LegacyShareClassificationOutcome.OwnerGranted, result.Classification);
        Assert.Equal(1, result.ProposedDelegationDepth);
        Assert.Null(result.ProposedParentShareId);
        Assert.Empty(result.CandidateParentShareIds);
    }

    [Fact]
    public void ClassifyShareOwnerGrantedByTeamMemberReturnsOwnerGrantedWithDepth1()
    {
        // Arrange
        var shareId = Guid.CreateVersion7();
        var owningTeam = Guid.CreateVersion7();
        var teamMember = Guid.CreateVersion7();
        var share = CreateShareResource(shareId, Record);
        var shareEdges = CreateShareEdges(shareId, RecipientUser, RecipientKind.User, "reader");
        var ownerInfo = new OwnerInfoAtRegistration
        {
            OwnerTeamId = owningTeam,
            TeamMemberUserIds = new HashSet<Guid> { teamMember, OwnerUser },
            IsVerifiable = true
        };
        var grantsSnapshot = new RecordGrantsSnapshot { ObservedOn = ObservedOn, ActiveShares = [] };
        var pseudonymizer = new LineagePseudonymizer(Org, TestKeyMaterial);

        // Act
        var result = LegacyLineageClassifier.ClassifyShare(
            share,
            null,
            shareEdges,
            new HashSet<Guid> { FieldItem1 },
            [],
            grantorAtRegistration: teamMember, // Team member granted the share
            ownerAtRegistration: ownerInfo,
            grantsSnapshot,
            pseudonymizer);

        // Assert
        Assert.Equal(LegacyShareClassificationOutcome.OwnerGranted, result.Classification);
        Assert.Equal(1, result.ProposedDelegationDepth);
    }

    #endregion

    #region UnambiguousParent Tests

    [Fact]
    public void ClassifyShareUnambiguousParentSingleCoveringManagerShareReturnsUnambiguousParentWithDepth2()
    {
        // Arrange
        var shareId = Guid.CreateVersion7();
        var managerShareId = Guid.CreateVersion7();
        var share = CreateShareResource(shareId, Record);
        var shareEdges = CreateShareEdges(shareId, RecipientUser, RecipientKind.User, "reader");
        var ownerInfo = new OwnerInfoAtRegistration
        {
            OwnerUserId = OwnerUser,
            IsVerifiable = true
        };

        // Manager holds a share that covers the target's field items
        var managerShare = new ActiveShareInfo
        {
            RecordShareId = managerShareId,
            OrganizationId = Org,
            RecordId = Record,
            Role = "manager",
            DelegationDepth = 1,
            IsActive = true,
            EffectiveExpiresOn = null,
            FieldItemIds = new HashSet<Guid> { FieldItem1, FieldItem2 },
            DocumentBindings = [],
            DirectUserRecipientIds = new HashSet<Guid> { ManagerUser },
            TeamRecipientIds = new HashSet<Guid>(),
            TeamMemberUserIds = new HashSet<Guid>()
        };

        var grantsSnapshot = new RecordGrantsSnapshot { ObservedOn = ObservedOn, ActiveShares = [managerShare] };
        var pseudonymizer = new LineagePseudonymizer(Org, TestKeyMaterial);

        // Act
        var result = LegacyLineageClassifier.ClassifyShare(
            share,
            null,
            shareEdges,
            new HashSet<Guid> { FieldItem1 }, // Target only needs FieldItem1
            [],
            grantorAtRegistration: ManagerUser, // Non-owner granted
            ownerAtRegistration: ownerInfo,
            grantsSnapshot,
            pseudonymizer);

        // Assert
        Assert.Equal(LegacyShareClassificationOutcome.UnambiguousParent, result.Classification);
        Assert.Equal(managerShareId, result.ProposedParentShareId);
        Assert.Equal(2, result.ProposedDelegationDepth);
        Assert.Single(result.CandidateParentShareIds);
    }

    [Fact]
    public void ClassifyShareUnambiguousParentDepth2ParentCreatesDepth3Reader()
    {
        // Arrange
        var shareId = Guid.CreateVersion7();
        var managerShareId = Guid.CreateVersion7();
        var share = CreateShareResource(shareId, Record);
        var shareEdges = CreateShareEdges(shareId, RecipientUser, RecipientKind.User, "reader");
        var ownerInfo = new OwnerInfoAtRegistration
        {
            OwnerUserId = OwnerUser,
            IsVerifiable = true
        };

        var managerShare = new ActiveShareInfo
        {
            RecordShareId = managerShareId,
            OrganizationId = Org,
            RecordId = Record,
            Role = "manager",
            IsActive = true,
            DelegationDepth = 2, // Parent is already depth 2
            EffectiveExpiresOn = null,
            FieldItemIds = new HashSet<Guid> { FieldItem1 },
            DocumentBindings = [],
            DirectUserRecipientIds = new HashSet<Guid> { ManagerUser },
            TeamRecipientIds = new HashSet<Guid>(),
            TeamMemberUserIds = new HashSet<Guid>()
        };

        var grantsSnapshot = new RecordGrantsSnapshot { ObservedOn = ObservedOn, ActiveShares = WithVerifiedAncestors(managerShare) };
        var pseudonymizer = new LineagePseudonymizer(Org, TestKeyMaterial);

        // Act
        var result = LegacyLineageClassifier.ClassifyShare(
            share,
            null,
            shareEdges,
            new HashSet<Guid> { FieldItem1 },
            [],
            grantorAtRegistration: ManagerUser,
            ownerAtRegistration: ownerInfo,
            grantsSnapshot,
            pseudonymizer);

        // Assert
        Assert.Equal(LegacyShareClassificationOutcome.UnambiguousParent, result.Classification);
        Assert.Equal(3, result.ProposedDelegationDepth);
    }

    #endregion

    #region MultipleCandidateParents Tests

    [Fact]
    public void ClassifyShareMultipleCandidateParentsReturnsMultipleCandidatesNeverAutoResolved()
    {
        // Arrange
        var shareId = Guid.CreateVersion7();
        var managerShare1Id = Guid.CreateVersion7();
        var managerShare2Id = Guid.CreateVersion7();
        var share = CreateShareResource(shareId, Record);
        var shareEdges = CreateShareEdges(shareId, RecipientUser, RecipientKind.User, "reader");
        var ownerInfo = new OwnerInfoAtRegistration
        {
            OwnerUserId = OwnerUser,
            IsVerifiable = true
        };

        // Two manager shares that both cover the target scope
        var managerShare1 = new ActiveShareInfo
        {
            RecordShareId = managerShare1Id,
            OrganizationId = Org,
            RecordId = Record,
            Role = "manager",
            DelegationDepth = 1,
            IsActive = true,
            EffectiveExpiresOn = null,
            FieldItemIds = new HashSet<Guid> { FieldItem1, FieldItem2 },
            DocumentBindings = [],
            DirectUserRecipientIds = new HashSet<Guid> { ManagerUser },
            TeamRecipientIds = new HashSet<Guid>(),
            TeamMemberUserIds = new HashSet<Guid>()
        };

        var managerShare2 = new ActiveShareInfo
        {
            RecordShareId = managerShare2Id,
            OrganizationId = Org,
            RecordId = Record,
            Role = "manager",
            DelegationDepth = 1,
            IsActive = true,
            EffectiveExpiresOn = null,
            FieldItemIds = new HashSet<Guid> { FieldItem1, FieldItem2 },
            DocumentBindings = [],
            DirectUserRecipientIds = new HashSet<Guid> { ManagerUser },
            TeamRecipientIds = new HashSet<Guid>(),
            TeamMemberUserIds = new HashSet<Guid>()
        };

        var grantsSnapshot = new RecordGrantsSnapshot { ObservedOn = ObservedOn, ActiveShares = [managerShare1, managerShare2] };
        var pseudonymizer = new LineagePseudonymizer(Org, TestKeyMaterial);

        // Act
        var result = LegacyLineageClassifier.ClassifyShare(
            share,
            null,
            shareEdges,
            new HashSet<Guid> { FieldItem1 },
            [],
            grantorAtRegistration: ManagerUser,
            ownerAtRegistration: ownerInfo,
            grantsSnapshot,
            pseudonymizer);

        // Assert
        Assert.Equal(LegacyShareClassificationOutcome.MultipleCandidateParents, result.Classification);
        Assert.Null(result.ProposedParentShareId);
        Assert.Null(result.ProposedDelegationDepth);
        Assert.Equal(2, result.CandidateParentShareIds.Count);
        Assert.Contains(managerShare1Id, result.CandidateParentShareIds);
        Assert.Contains(managerShare2Id, result.CandidateParentShareIds);
    }

    #endregion

    #region NoCoveringParent Tests

    [Fact]
    public void ClassifyShareNoCoveringParentGrantorHasNoManagerShareReturnsNoCoveringParent()
    {
        // Arrange
        var shareId = Guid.CreateVersion7();
        var share = CreateShareResource(shareId, Record);
        var shareEdges = CreateShareEdges(shareId, RecipientUser, RecipientKind.User, "reader");
        var ownerInfo = new OwnerInfoAtRegistration
        {
            OwnerUserId = OwnerUser,
            IsVerifiable = true
        };

        // No manager shares exist that the grantor holds
        var grantsSnapshot = new RecordGrantsSnapshot { ObservedOn = ObservedOn, ActiveShares = [] };
        var pseudonymizer = new LineagePseudonymizer(Org, TestKeyMaterial);

        // Act
        var result = LegacyLineageClassifier.ClassifyShare(
            share,
            null,
            shareEdges,
            new HashSet<Guid> { FieldItem1 },
            [],
            grantorAtRegistration: ManagerUser, // Non-owner with no manager shares
            ownerAtRegistration: ownerInfo,
            grantsSnapshot,
            pseudonymizer);

        // Assert
        Assert.Equal(LegacyShareClassificationOutcome.NoCoveringParent, result.Classification);
    }

    [Fact]
    public void ClassifyShareNoCoveringParentManagerShareDoesNotCoverScopeReturnsNoCoveringParent()
    {
        // Arrange
        var shareId = Guid.CreateVersion7();
        var managerShareId = Guid.CreateVersion7();
        var share = CreateShareResource(shareId, Record);
        var shareEdges = CreateShareEdges(shareId, RecipientUser, RecipientKind.User, "reader");
        var ownerInfo = new OwnerInfoAtRegistration
        {
            OwnerUserId = OwnerUser,
            IsVerifiable = true
        };

        // Manager share covers only FieldItem2, not FieldItem1
        var managerShare = new ActiveShareInfo
        {
            RecordShareId = managerShareId,
            OrganizationId = Org,
            RecordId = Record,
            Role = "manager",
            IsActive = true,
            DelegationDepth = 1,
            EffectiveExpiresOn = null,
            FieldItemIds = new HashSet<Guid> { FieldItem2 }, // Does NOT cover FieldItem1
            DocumentBindings = [],
            DirectUserRecipientIds = new HashSet<Guid> { ManagerUser },
            TeamRecipientIds = new HashSet<Guid>(),
            TeamMemberUserIds = new HashSet<Guid>()
        };

        var grantsSnapshot = new RecordGrantsSnapshot { ObservedOn = ObservedOn, ActiveShares = [managerShare] };
        var pseudonymizer = new LineagePseudonymizer(Org, TestKeyMaterial);

        // Act
        var result = LegacyLineageClassifier.ClassifyShare(
            share,
            null,
            shareEdges,
            new HashSet<Guid> { FieldItem1 }, // Target needs FieldItem1
            [],
            grantorAtRegistration: ManagerUser,
            ownerAtRegistration: ownerInfo,
            grantsSnapshot,
            pseudonymizer);

        // Assert
        Assert.Equal(LegacyShareClassificationOutcome.NoCoveringParent, result.Classification);
    }

    [Fact]
    public void ClassifyShareNoCoveringParentUnknownGrantorReturnsNoCoveringParent()
    {
        // Arrange
        var shareId = Guid.CreateVersion7();
        var share = CreateShareResource(shareId, Record);
        var shareEdges = CreateShareEdges(shareId, RecipientUser, RecipientKind.User, "reader");
        var grantsSnapshot = new RecordGrantsSnapshot { ObservedOn = ObservedOn, ActiveShares = [] };
        var pseudonymizer = new LineagePseudonymizer(Org, TestKeyMaterial);

        // Act - null grantor
        var result = LegacyLineageClassifier.ClassifyShare(
            share,
            null,
            shareEdges,
            new HashSet<Guid> { FieldItem1 },
            [],
            grantorAtRegistration: null,
            ownerAtRegistration: null,
            grantsSnapshot,
            pseudonymizer);

        // Assert
        Assert.Equal(LegacyShareClassificationOutcome.NoCoveringParent, result.Classification);
    }

    #endregion

    #region ExceedsDepth Tests

    [Fact]
    public void ClassifyShareExceedsDepthWouldExceedMaxDepthReturnsExceedsDepth()
    {
        // Arrange
        var shareId = Guid.CreateVersion7();
        var managerShareId = Guid.CreateVersion7();
        var share = CreateShareResource(shareId, Record);
        var shareEdges = CreateShareEdges(shareId, RecipientUser, RecipientKind.User, "reader");
        var ownerInfo = new OwnerInfoAtRegistration
        {
            OwnerUserId = OwnerUser,
            IsVerifiable = true
        };

        // Manager share is already at depth 3 (max)
        var managerShare = new ActiveShareInfo
        {
            RecordShareId = managerShareId,
            OrganizationId = Org,
            RecordId = Record,
            Role = "manager",
            IsActive = true,
            DelegationDepth = 3, // Already at max
            EffectiveExpiresOn = null,
            FieldItemIds = new HashSet<Guid> { FieldItem1 },
            DocumentBindings = [],
            DirectUserRecipientIds = new HashSet<Guid> { ManagerUser },
            TeamRecipientIds = new HashSet<Guid>(),
            TeamMemberUserIds = new HashSet<Guid>()
        };

        var grantsSnapshot = new RecordGrantsSnapshot { ObservedOn = ObservedOn, ActiveShares = WithVerifiedAncestors(managerShare) };
        var pseudonymizer = new LineagePseudonymizer(Org, TestKeyMaterial);

        // Act
        var result = LegacyLineageClassifier.ClassifyShare(
            share,
            null,
            shareEdges,
            new HashSet<Guid> { FieldItem1 },
            [],
            grantorAtRegistration: ManagerUser,
            ownerAtRegistration: ownerInfo,
            grantsSnapshot,
            pseudonymizer);

        // Assert
        Assert.Equal(LegacyShareClassificationOutcome.ExceedsDepth, result.Classification);
    }

    [Fact]
    public void ClassifyShareExceedsDepthManagerShareAtDepth3ReturnsExceedsDepth()
    {
        // Arrange: A Manager share at depth 2 trying to create another Manager share would put it at depth 3
        // which is not allowed for Manager shares (Section 5)
        var shareId = Guid.CreateVersion7();
        var managerShareId = Guid.CreateVersion7();
        var share = CreateShareResource(shareId, Record);
        var shareEdges = CreateShareEdges(shareId, RecipientUser, RecipientKind.User, "manager"); // Target is Manager
        var ownerInfo = new OwnerInfoAtRegistration
        {
            OwnerUserId = OwnerUser,
            IsVerifiable = true
        };

        var managerShare = new ActiveShareInfo
        {
            RecordShareId = managerShareId,
            OrganizationId = Org,
            RecordId = Record,
            Role = "manager",
            IsActive = true,
            DelegationDepth = 2, // Parent at depth 2
            EffectiveExpiresOn = null,
            FieldItemIds = new HashSet<Guid> { FieldItem1 },
            DocumentBindings = [],
            DirectUserRecipientIds = new HashSet<Guid> { ManagerUser },
            TeamRecipientIds = new HashSet<Guid>(),
            TeamMemberUserIds = new HashSet<Guid>()
        };

        var grantsSnapshot = new RecordGrantsSnapshot { ObservedOn = ObservedOn, ActiveShares = WithVerifiedAncestors(managerShare) };
        var pseudonymizer = new LineagePseudonymizer(Org, TestKeyMaterial);

        // Act
        var result = LegacyLineageClassifier.ClassifyShare(
            share,
            null,
            shareEdges,
            new HashSet<Guid> { FieldItem1 },
            [],
            grantorAtRegistration: ManagerUser,
            ownerAtRegistration: ownerInfo,
            grantsSnapshot,
            pseudonymizer);

        // Assert
        Assert.Equal(LegacyShareClassificationOutcome.ExceedsDepth, result.Classification);
        Assert.Contains(managerShareId, result.CandidateParentShareIds);
    }

    #endregion

    #region IndependentCoverage Tests

    [Fact]
    public void ClassifyShareIndependentCoverageRecipientHasOtherCoveringShareReturnsIndependentlyCovered()
    {
        // Arrange
        var shareId = Guid.CreateVersion7();
        var otherShareId = Guid.CreateVersion7();
        var share = CreateShareResource(shareId, Record);
        var shareEdges = CreateShareEdges(shareId, RecipientUser, RecipientKind.User, "reader");
        var ownerInfo = new OwnerInfoAtRegistration
        {
            OwnerUserId = OwnerUser,
            IsVerifiable = true
        };

        // Another share that also covers the recipient with the same field items
        var otherShare = new ActiveShareInfo
        {
            RecordShareId = otherShareId,
            OrganizationId = Org,
            RecordId = Record,
            Role = "reader",
            DelegationDepth = 1,
            IsActive = true,
            EffectiveExpiresOn = null,
            FieldItemIds = new HashSet<Guid> { FieldItem1, FieldItem2 },
            DocumentBindings = [],
            DirectUserRecipientIds = new HashSet<Guid> { RecipientUser },
            TeamRecipientIds = new HashSet<Guid>(),
            TeamMemberUserIds = new HashSet<Guid>()
        };

        var grantsSnapshot = new RecordGrantsSnapshot { ObservedOn = ObservedOn, ActiveShares = [otherShare] };
        var pseudonymizer = new LineagePseudonymizer(Org, TestKeyMaterial);

        // Act - with independent coverage evaluation enabled
        var result = LegacyLineageClassifier.ClassifyShare(
            share,
            null,
            shareEdges,
            new HashSet<Guid> { FieldItem1 },
            [],
            grantorAtRegistration: OwnerUser,
            ownerAtRegistration: ownerInfo,
            grantsSnapshot,
            pseudonymizer,
            evaluateIndependentCoverage: true);

        // Assert
        Assert.Equal(IndependentCoverageAnnotation.IndependentlyCovered, result.IndependentCoverage);
    }

    [Fact]
    public void ClassifyShareIndependentCoverageRecipientHasNoOtherShareReturnsNotCovered()
    {
        // Arrange
        var shareId = Guid.CreateVersion7();
        var share = CreateShareResource(shareId, Record);
        var shareEdges = CreateShareEdges(shareId, RecipientUser, RecipientKind.User, "reader");
        var ownerInfo = new OwnerInfoAtRegistration
        {
            OwnerUserId = OwnerUser,
            IsVerifiable = true
        };

        var grantsSnapshot = new RecordGrantsSnapshot { ObservedOn = ObservedOn, ActiveShares = [] };
        var pseudonymizer = new LineagePseudonymizer(Org, TestKeyMaterial);

        // Act
        var result = LegacyLineageClassifier.ClassifyShare(
            share,
            null,
            shareEdges,
            new HashSet<Guid> { FieldItem1 },
            [],
            grantorAtRegistration: OwnerUser,
            ownerAtRegistration: ownerInfo,
            grantsSnapshot,
            pseudonymizer,
            evaluateIndependentCoverage: true);

        // Assert
        Assert.Equal(IndependentCoverageAnnotation.NotCovered, result.IndependentCoverage);
    }

    #endregion

    #region Document Binding Tests

    [Fact]
    public void ClassifyShareDocumentBindingsParentMustCoverDocumentFlags()
    {
        // Arrange
        var shareId = Guid.CreateVersion7();
        var managerShareId = Guid.CreateVersion7();
        var share = CreateShareResource(shareId, Record);
        var shareEdges = CreateShareEdges(shareId, RecipientUser, RecipientKind.User, "reader");
        var ownerInfo = new OwnerInfoAtRegistration
        {
            OwnerUserId = OwnerUser,
            IsVerifiable = true
        };

        // Target share wants CanDownloadDocument
        var targetDocBinding = new ShareDocumentBinding
        {
            ShareDocumentBindingId = Guid.CreateVersion7(),
            OrganizationId = Org,
            RecordShareId = shareId,
            FileAttachmentId = Doc1,
            CanViewDocument = true,
            CanDownloadDocument = true,
            CanReplaceDocument = false
        };

        // Manager share only has CanViewDocument, not CanDownloadDocument
        var parentDocBinding = new ShareDocumentBinding
        {
            ShareDocumentBindingId = Guid.CreateVersion7(),
            OrganizationId = Org,
            RecordShareId = managerShareId,
            FileAttachmentId = Doc1,
            CanViewDocument = true,
            CanDownloadDocument = false, // Does not have download
            CanReplaceDocument = false
        };

        var managerShare = new ActiveShareInfo
        {
            RecordShareId = managerShareId,
            OrganizationId = Org,
            RecordId = Record,
            Role = "manager",
            IsActive = true,
            DelegationDepth = 1,
            EffectiveExpiresOn = null,
            FieldItemIds = new HashSet<Guid> { FieldItem1 },
            DocumentBindings = [parentDocBinding],
            DirectUserRecipientIds = new HashSet<Guid> { ManagerUser },
            TeamRecipientIds = new HashSet<Guid>(),
            TeamMemberUserIds = new HashSet<Guid>()
        };

        var grantsSnapshot = new RecordGrantsSnapshot { ObservedOn = ObservedOn, ActiveShares = [managerShare] };
        var pseudonymizer = new LineagePseudonymizer(Org, TestKeyMaterial);

        // Act
        var result = LegacyLineageClassifier.ClassifyShare(
            share,
            null,
            shareEdges,
            new HashSet<Guid> { FieldItem1 },
            [targetDocBinding],
            grantorAtRegistration: ManagerUser,
            ownerAtRegistration: ownerInfo,
            grantsSnapshot,
            pseudonymizer);

        // Assert - should not find a covering parent due to insufficient document flags
        Assert.Equal(LegacyShareClassificationOutcome.NoCoveringParent, result.Classification);
    }

    #endregion

    #region Recipient Kind Tests

    [Fact]
    public void ClassifyShareTeamRecipientCorrectlyIdentifiesKind()
    {
        // Arrange
        var shareId = Guid.CreateVersion7();
        var recipientTeam = Guid.CreateVersion7();
        var share = CreateShareResource(shareId, Record);
        var shareEdges = CreateShareEdges(shareId, recipientTeam, RecipientKind.Team, "reader");
        var ownerInfo = new OwnerInfoAtRegistration
        {
            OwnerUserId = OwnerUser,
            IsVerifiable = true
        };
        var grantsSnapshot = new RecordGrantsSnapshot { ObservedOn = ObservedOn, ActiveShares = [] };
        var pseudonymizer = new LineagePseudonymizer(Org, TestKeyMaterial);

        // Act
        var result = LegacyLineageClassifier.ClassifyShare(
            share,
            null,
            shareEdges,
            new HashSet<Guid> { FieldItem1 },
            [],
            grantorAtRegistration: OwnerUser,
            ownerAtRegistration: ownerInfo,
            grantsSnapshot,
            pseudonymizer);

        // Assert
        Assert.Equal(RecipientKind.Team, result.RecipientKind);
    }

    #endregion

    #region Cross-Tenant and Cross-Record Tests

    [Fact]
    public void ClassifyShareCrossTenantManagerShareNotCovering()
    {
        // Arrange
        var shareId = Guid.CreateVersion7();
        var managerShareId = Guid.CreateVersion7();
        var otherOrg = Guid.CreateVersion7();
        var share = CreateShareResource(shareId, Record);
        var shareEdges = CreateShareEdges(shareId, RecipientUser, RecipientKind.User, "reader");
        var ownerInfo = new OwnerInfoAtRegistration { OwnerUserId = OwnerUser, IsVerifiable = true };

        // Manager share from different organization
        var managerShare = new ActiveShareInfo
        {
            RecordShareId = managerShareId,
            OrganizationId = otherOrg, // Different org
            RecordId = Record,
            Role = "manager",
            DelegationDepth = 1,
            IsActive = true,
            EffectiveExpiresOn = null,
            FieldItemIds = new HashSet<Guid> { FieldItem1 },
            DocumentBindings = [],
            DirectUserRecipientIds = new HashSet<Guid> { ManagerUser },
            TeamRecipientIds = new HashSet<Guid>(),
            TeamMemberUserIds = new HashSet<Guid>()
        };

        var grantsSnapshot = new RecordGrantsSnapshot { ObservedOn = ObservedOn, ActiveShares = [managerShare] };
        var pseudonymizer = new LineagePseudonymizer(Org, TestKeyMaterial);

        // Act
        var result = LegacyLineageClassifier.ClassifyShare(
            share, null, shareEdges, new HashSet<Guid> { FieldItem1 }, [],
            grantorAtRegistration: ManagerUser, ownerAtRegistration: ownerInfo,
            grantsSnapshot, pseudonymizer);

        // Assert: cross-tenant share cannot be a covering parent
        Assert.Equal(LegacyShareClassificationOutcome.NoCoveringParent, result.Classification);
    }

    [Fact]
    public void ClassifyShareCrossRecordManagerShareNotCovering()
    {
        // Arrange
        var shareId = Guid.CreateVersion7();
        var managerShareId = Guid.CreateVersion7();
        var otherRecord = Guid.CreateVersion7();
        var share = CreateShareResource(shareId, Record);
        var shareEdges = CreateShareEdges(shareId, RecipientUser, RecipientKind.User, "reader");
        var ownerInfo = new OwnerInfoAtRegistration { OwnerUserId = OwnerUser, IsVerifiable = true };

        // Manager share from different record
        var managerShare = new ActiveShareInfo
        {
            RecordShareId = managerShareId,
            OrganizationId = Org,
            RecordId = otherRecord, // Different record
            Role = "manager",
            DelegationDepth = 1,
            IsActive = true,
            EffectiveExpiresOn = null,
            FieldItemIds = new HashSet<Guid> { FieldItem1 },
            DocumentBindings = [],
            DirectUserRecipientIds = new HashSet<Guid> { ManagerUser },
            TeamRecipientIds = new HashSet<Guid>(),
            TeamMemberUserIds = new HashSet<Guid>()
        };

        var grantsSnapshot = new RecordGrantsSnapshot { ObservedOn = ObservedOn, ActiveShares = [managerShare] };
        var pseudonymizer = new LineagePseudonymizer(Org, TestKeyMaterial);

        // Act
        var result = LegacyLineageClassifier.ClassifyShare(
            share, null, shareEdges, new HashSet<Guid> { FieldItem1 }, [],
            grantorAtRegistration: ManagerUser, ownerAtRegistration: ownerInfo,
            grantsSnapshot, pseudonymizer);

        // Assert: cross-record share cannot be a covering parent
        Assert.Equal(LegacyShareClassificationOutcome.NoCoveringParent, result.Classification);
    }

    [Fact]
    public void ClassifyShareInactiveManagerShareNotCovering()
    {
        // Arrange
        var shareId = Guid.CreateVersion7();
        var managerShareId = Guid.CreateVersion7();
        var share = CreateShareResource(shareId, Record);
        var shareEdges = CreateShareEdges(shareId, RecipientUser, RecipientKind.User, "reader");
        var ownerInfo = new OwnerInfoAtRegistration { OwnerUserId = OwnerUser, IsVerifiable = true };

        // Manager share that is not Active at snapshot instant
        var managerShare = new ActiveShareInfo
        {
            RecordShareId = managerShareId,
            OrganizationId = Org,
            RecordId = Record,
            Role = "manager",
            DelegationDepth = 1,
            IsActive = false, // Not active
            EffectiveExpiresOn = null,
            FieldItemIds = new HashSet<Guid> { FieldItem1 },
            DocumentBindings = [],
            DirectUserRecipientIds = new HashSet<Guid> { ManagerUser },
            TeamRecipientIds = new HashSet<Guid>(),
            TeamMemberUserIds = new HashSet<Guid>()
        };

        var grantsSnapshot = new RecordGrantsSnapshot { ObservedOn = ObservedOn, ActiveShares = [managerShare] };
        var pseudonymizer = new LineagePseudonymizer(Org, TestKeyMaterial);

        // Act
        var result = LegacyLineageClassifier.ClassifyShare(
            share, null, shareEdges, new HashSet<Guid> { FieldItem1 }, [],
            grantorAtRegistration: ManagerUser, ownerAtRegistration: ownerInfo,
            grantsSnapshot, pseudonymizer);

        // Assert: inactive share cannot be a covering parent
        Assert.Equal(LegacyShareClassificationOutcome.NoCoveringParent, result.Classification);
    }

    #endregion

    #region Self-Reference and Cyclic Tests

    [Fact]
    public void ClassifyShareSelfReferenceExcludedFromCandidates()
    {
        // Arrange
        var shareId = Guid.CreateVersion7();
        var share = CreateShareResource(shareId, Record);
        var shareEdges = CreateShareEdges(shareId, RecipientUser, RecipientKind.User, "reader");
        var ownerInfo = new OwnerInfoAtRegistration { OwnerUserId = OwnerUser, IsVerifiable = true };

        // Create a scenario where the share being classified exists in the snapshot
        var targetShare = new ActiveShareInfo
        {
            RecordShareId = shareId, // Same ID as target
            OrganizationId = Org,
            RecordId = Record,
            Role = "reader",
            DelegationDepth = 1,
            IsActive = true,
            EffectiveExpiresOn = null,
            FieldItemIds = new HashSet<Guid> { FieldItem1 },
            DocumentBindings = [],
            DirectUserRecipientIds = new HashSet<Guid> { ManagerUser },
            TeamRecipientIds = new HashSet<Guid>(),
            TeamMemberUserIds = new HashSet<Guid>()
        };

        var grantsSnapshot = new RecordGrantsSnapshot { ObservedOn = ObservedOn, ActiveShares = [targetShare] };
        var pseudonymizer = new LineagePseudonymizer(Org, TestKeyMaterial);

        // Act
        var result = LegacyLineageClassifier.ClassifyShare(
            share, null, shareEdges, new HashSet<Guid> { FieldItem1 }, [],
            grantorAtRegistration: ManagerUser, ownerAtRegistration: ownerInfo,
            grantsSnapshot, pseudonymizer);

        // Assert: self cannot be its own parent
        Assert.Equal(LegacyShareClassificationOutcome.NoCoveringParent, result.Classification);
    }

    [Fact]
    public void ClassifyShareCyclicParentReferenceExcludedFromCandidates()
    {
        // Arrange
        var shareId = Guid.CreateVersion7();
        var managerShareId = Guid.CreateVersion7();
        var share = CreateShareResource(shareId, Record);
        var shareEdges = CreateShareEdges(shareId, RecipientUser, RecipientKind.User, "reader");
        var ownerInfo = new OwnerInfoAtRegistration { OwnerUserId = OwnerUser, IsVerifiable = true };

        // Manager share that claims target as its parent (would create a cycle)
        var managerShare = new ActiveShareInfo
        {
            RecordShareId = managerShareId,
            OrganizationId = Org,
            RecordId = Record,
            Role = "manager",
            DelegationDepth = 2,
            ParentRecordShareId = shareId, // Claims target as parent
            IsActive = true,
            EffectiveExpiresOn = null,
            FieldItemIds = new HashSet<Guid> { FieldItem1 },
            DocumentBindings = [],
            DirectUserRecipientIds = new HashSet<Guid> { ManagerUser },
            TeamRecipientIds = new HashSet<Guid>(),
            TeamMemberUserIds = new HashSet<Guid>()
        };

        var grantsSnapshot = new RecordGrantsSnapshot { ObservedOn = ObservedOn, ActiveShares = [managerShare] };
        var pseudonymizer = new LineagePseudonymizer(Org, TestKeyMaterial);

        // Act
        var result = LegacyLineageClassifier.ClassifyShare(
            share, null, shareEdges, new HashSet<Guid> { FieldItem1 }, [],
            grantorAtRegistration: ManagerUser, ownerAtRegistration: ownerInfo,
            grantsSnapshot, pseudonymizer);

        // Assert: cyclic parent excluded
        Assert.Equal(LegacyShareClassificationOutcome.NoCoveringParent, result.Classification);
    }

    #endregion

    #region Unknown Depth Tests

    [Fact]
    public void ClassifyShareUnknownDepthExcludedFromCandidates()
    {
        // Arrange
        var shareId = Guid.CreateVersion7();
        var managerShareId = Guid.CreateVersion7();
        var share = CreateShareResource(shareId, Record);
        var shareEdges = CreateShareEdges(shareId, RecipientUser, RecipientKind.User, "reader");
        var ownerInfo = new OwnerInfoAtRegistration { OwnerUserId = OwnerUser, IsVerifiable = true };

        // Manager share with unknown/unverifiable depth
        var managerShare = new ActiveShareInfo
        {
            RecordShareId = managerShareId,
            OrganizationId = Org,
            RecordId = Record,
            Role = "manager",
            DelegationDepth = null, // Unknown depth
            IsActive = true,
            EffectiveExpiresOn = null,
            FieldItemIds = new HashSet<Guid> { FieldItem1 },
            DocumentBindings = [],
            DirectUserRecipientIds = new HashSet<Guid> { ManagerUser },
            TeamRecipientIds = new HashSet<Guid>(),
            TeamMemberUserIds = new HashSet<Guid>()
        };

        var grantsSnapshot = new RecordGrantsSnapshot { ObservedOn = ObservedOn, ActiveShares = [managerShare] };
        var pseudonymizer = new LineagePseudonymizer(Org, TestKeyMaterial);

        // Act
        var result = LegacyLineageClassifier.ClassifyShare(
            share, null, shareEdges, new HashSet<Guid> { FieldItem1 }, [],
            grantorAtRegistration: ManagerUser, ownerAtRegistration: ownerInfo,
            grantsSnapshot, pseudonymizer);

        // Assert: unknown depth cannot be assumed valid
        Assert.Equal(LegacyShareClassificationOutcome.NoCoveringParent, result.Classification);
    }

    #endregion

    #region Expiry and Version Cap Tests

    [Fact]
    public void ClassifyShareExpiredParentNotCovering()
    {
        // Arrange
        var shareId = Guid.CreateVersion7();
        var managerShareId = Guid.CreateVersion7();
        var share = CreateShareResource(shareId, Record);
        var shareEdges = CreateShareEdges(shareId, RecipientUser, RecipientKind.User, "reader");
        var ownerInfo = new OwnerInfoAtRegistration { OwnerUserId = OwnerUser, IsVerifiable = true };
        var targetExpiry = ObservedOn.AddDays(10);
        var parentExpiry = ObservedOn.AddDays(5); // Parent expires before target

        // Manager share that expires before target
        var managerShare = new ActiveShareInfo
        {
            RecordShareId = managerShareId,
            OrganizationId = Org,
            RecordId = Record,
            Role = "manager",
            DelegationDepth = 1,
            IsActive = true,
            EffectiveExpiresOn = parentExpiry,
            FieldItemIds = new HashSet<Guid> { FieldItem1 },
            DocumentBindings = [],
            DirectUserRecipientIds = new HashSet<Guid> { ManagerUser },
            TeamRecipientIds = new HashSet<Guid>(),
            TeamMemberUserIds = new HashSet<Guid>()
        };

        var grantsSnapshot = new RecordGrantsSnapshot { ObservedOn = ObservedOn, ActiveShares = [managerShare] };
        var pseudonymizer = new LineagePseudonymizer(Org, TestKeyMaterial);
        var shareMetadata = new ShareResourceMetadata { ShareExpiresOn = targetExpiry };

        // Act
        var result = LegacyLineageClassifier.ClassifyShare(
            share, shareMetadata, shareEdges, new HashSet<Guid> { FieldItem1 }, [],
            grantorAtRegistration: ManagerUser, ownerAtRegistration: ownerInfo,
            grantsSnapshot, pseudonymizer);

        // Assert: parent with earlier expiry cannot cover
        Assert.Equal(LegacyShareClassificationOutcome.NoCoveringParent, result.Classification);
    }

    [Fact]
    public void ClassifyShareIndependentCoverageManagerVsReaderManagerCovers()
    {
        // Arrange
        var shareId = Guid.CreateVersion7();
        var independentShareId = Guid.CreateVersion7();
        var share = CreateShareResource(shareId, Record);
        var shareEdges = CreateShareEdges(shareId, RecipientUser, RecipientKind.User, "reader");
        var ownerInfo = new OwnerInfoAtRegistration { OwnerUserId = OwnerUser, IsVerifiable = true };

        // Target is a Reader share
        var targetShare = new ActiveShareInfo
        {
            RecordShareId = shareId,
            OrganizationId = Org,
            RecordId = Record,
            Role = "reader",
            DelegationDepth = 1,
            IsActive = true,
            EffectiveExpiresOn = null,
            FieldItemIds = new HashSet<Guid> { FieldItem1 },
            DocumentBindings = [],
            DirectUserRecipientIds = new HashSet<Guid> { RecipientUser },
            TeamRecipientIds = new HashSet<Guid>(),
            TeamMemberUserIds = new HashSet<Guid>()
        };

        // Independent share is Manager (broader than Reader)
        var independentShare = new ActiveShareInfo
        {
            RecordShareId = independentShareId,
            OrganizationId = Org,
            RecordId = Record,
            Role = "manager", // Broader than reader
            DelegationDepth = 1,
            IsActive = true,
            EffectiveExpiresOn = null,
            FieldItemIds = new HashSet<Guid> { FieldItem1, FieldItem2 },
            DocumentBindings = [],
            DirectUserRecipientIds = new HashSet<Guid> { RecipientUser },
            TeamRecipientIds = new HashSet<Guid>(),
            TeamMemberUserIds = new HashSet<Guid>()
        };

        var grantsSnapshot = new RecordGrantsSnapshot { ObservedOn = ObservedOn, ActiveShares = [targetShare, independentShare] };
        var pseudonymizer = new LineagePseudonymizer(Org, TestKeyMaterial);

        // Act
        var result = LegacyLineageClassifier.ClassifyShare(
            share, null, shareEdges, new HashSet<Guid> { FieldItem1 }, [],
            grantorAtRegistration: OwnerUser, ownerAtRegistration: ownerInfo,
            grantsSnapshot, pseudonymizer,
            evaluateIndependentCoverage: true);

        // Assert: Manager is broader than Reader, so independent coverage exists
        Assert.Equal(IndependentCoverageAnnotation.IndependentlyCovered, result.IndependentCoverage);
    }

    [Fact]
    public void ClassifyShareIndependentCoverageReaderVsManagerReaderDoesNotCover()
    {
        // Arrange
        var shareId = Guid.CreateVersion7();
        var independentShareId = Guid.CreateVersion7();
        var share = CreateShareResource(shareId, Record);
        var shareEdges = CreateShareEdges(shareId, RecipientUser, RecipientKind.User, "manager");
        var ownerInfo = new OwnerInfoAtRegistration { OwnerUserId = OwnerUser, IsVerifiable = true };

        // Target is a Manager share
        var targetShare = new ActiveShareInfo
        {
            RecordShareId = shareId,
            OrganizationId = Org,
            RecordId = Record,
            Role = "manager",
            DelegationDepth = 1,
            IsActive = true,
            EffectiveExpiresOn = null,
            FieldItemIds = new HashSet<Guid> { FieldItem1 },
            DocumentBindings = [],
            DirectUserRecipientIds = new HashSet<Guid> { RecipientUser },
            TeamRecipientIds = new HashSet<Guid>(),
            TeamMemberUserIds = new HashSet<Guid>()
        };

        // Independent share is only Reader (narrower than Manager)
        var independentShare = new ActiveShareInfo
        {
            RecordShareId = independentShareId,
            OrganizationId = Org,
            RecordId = Record,
            Role = "reader", // Narrower than manager
            DelegationDepth = 1,
            IsActive = true,
            EffectiveExpiresOn = null,
            FieldItemIds = new HashSet<Guid> { FieldItem1, FieldItem2 },
            DocumentBindings = [],
            DirectUserRecipientIds = new HashSet<Guid> { RecipientUser },
            TeamRecipientIds = new HashSet<Guid>(),
            TeamMemberUserIds = new HashSet<Guid>()
        };

        var grantsSnapshot = new RecordGrantsSnapshot { ObservedOn = ObservedOn, ActiveShares = [targetShare, independentShare] };
        var pseudonymizer = new LineagePseudonymizer(Org, TestKeyMaterial);

        // Act
        var result = LegacyLineageClassifier.ClassifyShare(
            share, null, shareEdges, new HashSet<Guid> { FieldItem1 }, [],
            grantorAtRegistration: OwnerUser, ownerAtRegistration: ownerInfo,
            grantsSnapshot, pseudonymizer,
            evaluateIndependentCoverage: true);

        // Assert: Reader cannot replace Manager capability
        Assert.Equal(IndependentCoverageAnnotation.NotCovered, result.IndependentCoverage);
    }

    [Fact]
    public void ClassifyShareIndependentCoverageExpiringEarlierDoesNotCover()
    {
        // Arrange
        var shareId = Guid.CreateVersion7();
        var independentShareId = Guid.CreateVersion7();
        var share = CreateShareResource(shareId, Record);
        var shareEdges = CreateShareEdges(shareId, RecipientUser, RecipientKind.User, "reader");
        var ownerInfo = new OwnerInfoAtRegistration { OwnerUserId = OwnerUser, IsVerifiable = true };
        var targetExpiry = ObservedOn.AddDays(10);
        var independentExpiry = ObservedOn.AddDays(5); // Expires earlier

        // Target share expires at day 10
        var targetShare = new ActiveShareInfo
        {
            RecordShareId = shareId,
            OrganizationId = Org,
            RecordId = Record,
            Role = "reader",
            DelegationDepth = 1,
            IsActive = true,
            EffectiveExpiresOn = targetExpiry,
            FieldItemIds = new HashSet<Guid> { FieldItem1 },
            DocumentBindings = [],
            DirectUserRecipientIds = new HashSet<Guid> { RecipientUser },
            TeamRecipientIds = new HashSet<Guid>(),
            TeamMemberUserIds = new HashSet<Guid>()
        };

        // Independent share expires earlier
        var independentShare = new ActiveShareInfo
        {
            RecordShareId = independentShareId,
            OrganizationId = Org,
            RecordId = Record,
            Role = "reader",
            DelegationDepth = 1,
            IsActive = true,
            EffectiveExpiresOn = independentExpiry,
            FieldItemIds = new HashSet<Guid> { FieldItem1 },
            DocumentBindings = [],
            DirectUserRecipientIds = new HashSet<Guid> { RecipientUser },
            TeamRecipientIds = new HashSet<Guid>(),
            TeamMemberUserIds = new HashSet<Guid>()
        };

        var grantsSnapshot = new RecordGrantsSnapshot { ObservedOn = ObservedOn, ActiveShares = [targetShare, independentShare] };
        var pseudonymizer = new LineagePseudonymizer(Org, TestKeyMaterial);

        // Act
        var result = LegacyLineageClassifier.ClassifyShare(
            share, null, shareEdges, new HashSet<Guid> { FieldItem1 }, [],
            grantorAtRegistration: OwnerUser, ownerAtRegistration: ownerInfo,
            grantsSnapshot, pseudonymizer,
            evaluateIndependentCoverage: true);

        // Assert: independent share expires before target, so it doesn't cover the full version cap
        Assert.Equal(IndependentCoverageAnnotation.NotCovered, result.IndependentCoverage);
    }

    #endregion

    [Fact]
    public void MatchingButUnverifiedHistoricalOwnerDoesNotEstablishARoot()
    {
        var id = Guid.NewGuid();
        var result = LegacyLineageClassifier.ClassifyShare(CreateShareResource(id, Record), null,
            CreateShareEdges(id, RecipientUser, RecipientKind.User, "reader"), new HashSet<Guid> { FieldItem1 }, [],
            OwnerUser, new OwnerInfoAtRegistration { OwnerUserId = OwnerUser, IsVerifiable = false },
            new RecordGrantsSnapshot { ObservedOn = ObservedOn }, new LineagePseudonymizer(Org, TestKeyMaterial));

        Assert.Equal(LegacyShareClassificationOutcome.NoCoveringParent, result.Classification);
        Assert.Null(result.ProposedDelegationDepth);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RefusesRemovedOrContradictoryRecipientEvidence(bool contradictory)
    {
        var id = Guid.NewGuid();
        var edges = CreateShareEdges(id, RecipientUser, RecipientKind.User, "reader");
        if (contradictory)
        {
            edges.AddRange(CreateShareEdges(id, ManagerUser, RecipientKind.User, "contributor"));
        }
        else
        {
            edges[0].RemovedBySecurityCommandId = Guid.NewGuid();
        }

        Assert.Throws<InvalidOperationException>(() => LegacyLineageClassifier.ClassifyShare(CreateShareResource(id, Record), null,
            edges, new HashSet<Guid> { FieldItem1 }, [], OwnerUser,
            new OwnerInfoAtRegistration { OwnerUserId = OwnerUser, IsVerifiable = true },
            new RecordGrantsSnapshot { ObservedOn = ObservedOn }, new LineagePseudonymizer(Org, TestKeyMaterial)));
    }

    [Fact]
    public void AGrantWithIndirectDependenceCannotBeAParentOrIndependentAid()
    {
        var id = Guid.NewGuid();
        var root = SnapshotShare(id, 1, null);
        var middle = SnapshotShare(Guid.NewGuid(), 2, id);
        var leaf = SnapshotShare(Guid.NewGuid(), 3, middle.RecordShareId) with
        {
            DirectUserRecipientIds = new HashSet<Guid> { ManagerUser, RecipientUser },
        };
        var result = LegacyLineageClassifier.ClassifyShare(CreateShareResource(id, Record), null,
            CreateShareEdges(id, RecipientUser, RecipientKind.User, "reader"), new HashSet<Guid> { FieldItem1 }, [],
            ManagerUser, null, new RecordGrantsSnapshot { ObservedOn = ObservedOn, ActiveShares = [root, middle, leaf] },
            new LineagePseudonymizer(Org, TestKeyMaterial), evaluateIndependentCoverage: true);

        Assert.Empty(result.CandidateParentShareIds);
        Assert.Equal(IndependentCoverageAnnotation.NotCovered, result.IndependentCoverage);
    }

    [Fact]
    public void ReportIsScopedToOneRevisionAndUsesPseudonyms()
    {
        var id = Guid.NewGuid();
        var entry = LegacyLineageClassifier.ClassifyShare(CreateShareResource(id, Record), null,
            CreateShareEdges(id, RecipientUser, RecipientKind.User, "reader"), new HashSet<Guid> { FieldItem1 }, [],
            OwnerUser, new OwnerInfoAtRegistration { OwnerUserId = OwnerUser, IsVerifiable = true },
            new RecordGrantsSnapshot { ObservedOn = ObservedOn }, new LineagePseudonymizer(Org, TestKeyMaterial));
        var report = new LegacyLineageReportBuilder().WithAuthorizationRevision(12).WithTimestamp(ObservedOn).AddEntry(entry).Build();
        var json = JsonSerializer.Serialize(report);
        Assert.Equal(12, report.AuthorizationRevisionRead);
        Assert.DoesNotContain(OwnerUser.ToString("D"), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(RecipientUser.ToString("D"), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(FieldItem1.ToString("D"), json, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(entry.RecipientPseudonymousReference, entry.GrantorPseudonymousReference);

        var crossOrg = new LegacyLineageReportBuilder().WithAuthorizationRevision(12).AddEntry(entry)
            .AddEntry(entry with { OrganizationId = Guid.NewGuid() });
        Assert.Throws<InvalidOperationException>(() => crossOrg.Build());
    }

    private static List<ActiveShareInfo> WithVerifiedAncestors(ActiveShareInfo leaf)
    {
        var shares = new List<ActiveShareInfo>();
        Guid? parent = null;
        for (var depth = 1; depth < leaf.DelegationDepth; depth++)
        {
            var ancestor = SnapshotShare(Guid.NewGuid(), depth, parent);
            parent = ancestor.RecordShareId;
            shares.Add(ancestor);
        }

        shares.Add(leaf with { ParentRecordShareId = parent });
        return shares;
    }

    private static ActiveShareInfo SnapshotShare(Guid id, int depth, Guid? parent) => new()
    {
        RecordShareId = id,
        OrganizationId = Org,
        RecordId = Record,
        DelegationDepth = depth,
        ParentRecordShareId = parent,
        Role = "manager",
        IsActive = true,
        FieldItemIds = new HashSet<Guid> { FieldItem1 },
    };

    #region Helper Methods

    private static AuthorityResource CreateShareResource(Guid shareId, Guid recordId)
    {
        return new AuthorityResource
        {
            AuthorityResourceId = Guid.CreateVersion7(),
            OrganizationId = Org,
            ResourceTypeName = ResourceTypeNames.RecordShare,
            ResourceEntityId = shareId,
            ResourceLifecycleState = ResourceLifecycleStates.Active,
            ParentResourceEntityId = recordId,
            RegisteredBySecurityCommandId = Guid.CreateVersion7(),
            ResourceRegisteredOn = ObservedOn.AddDays(-30)
        };
    }

    private static List<AuthorityRelationshipEdge> CreateShareEdges(
        Guid shareId,
        Guid recipientId,
        RecipientKind recipientKind,
        string role)
    {
        var subjectType = recipientKind == RecipientKind.Team ? ResourceTypeNames.Team : ResourceTypeNames.User;
        var subjectRelation = recipientKind == RecipientKind.Team ? "participant" : "";

        return
        [
            new AuthorityRelationshipEdge
            {
                AuthorityRelationshipEdgeId = Guid.CreateVersion7(),
                OrganizationId = Org,
                ResourceTypeName = ResourceTypeNames.RecordShare,
                ResourceEntityId = shareId,
                RelationshipName = role,
                SubjectTypeName = subjectType,
                SubjectEntityId = recipientId,
                SubjectRelationName = subjectRelation,
                CreatedBySecurityCommandId = Guid.CreateVersion7(),
                RelationshipCreatedOn = ObservedOn.AddDays(-30)
            }
        ];
    }

    #endregion
}
