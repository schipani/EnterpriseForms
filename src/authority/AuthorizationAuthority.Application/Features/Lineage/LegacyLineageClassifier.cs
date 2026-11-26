using AuthorizationAuthority.Domain.Features.Lineage;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using AuthorizationAuthority.Domain.Features.Sharing;

namespace AuthorizationAuthority.Application.Features.Lineage;

/// <summary>
/// C10 M1-WP10: classifies legacy shares without lineage tracking according to Section 5.1.
/// This is a pure read-only classification service that produces a report without modifying any state.
///
/// The classifier evaluates each share against:
/// - Ownership at grant time (not current ownership)
/// - Manager depth constraints (maximum depth 3, no Manager at depth 3)
/// - Exact scoped items and document flags coverage
/// - Single parent requirement (no union of multiple parents)
/// - Existing active shares and their expiry semantics
/// </summary>
public sealed class LegacyLineageClassifier
{
    /// <summary>Tool version for report tracking.</summary>
    public const string ToolVersion = "C10-M1-WP10-v1.0.0";

    /// <summary>Maximum delegation depth (platform maximum).</summary>
    public const int MaxDelegationDepth = 3;

    /// <summary>
    /// Classifies a single legacy share against the provided snapshot of the organization's authorization state.
    /// </summary>
    /// <param name="share">The share resource to classify.</param>
    /// <param name="shareMetadata">The share's metadata (if available).</param>
    /// <param name="shareEdges">All edges associated with this share.</param>
    /// <param name="shareFieldItems">Field item IDs in this share's scope.</param>
    /// <param name="shareDocumentBindings">Document bindings in this share's scope.</param>
    /// <param name="grantorAtRegistration">The authority user ID of the actor who registered this share.</param>
    /// <param name="ownerAtRegistration">The owner info at the time of registration (may be unknown/unverifiable).</param>
    /// <param name="grantsSnapshot">All active shares on the same record for lineage evaluation.</param>
    /// <param name="pseudonymizer">Pseudonymizer for safe output.</param>
    /// <param name="evaluateIndependentCoverage">Whether to evaluate independent coverage (more expensive).</param>
    /// <returns>The classification entry.</returns>
    public static LegacyShareClassificationEntry ClassifyShare(
        AuthorityResource share,
        ShareResourceMetadata? shareMetadata,
        IReadOnlyList<AuthorityRelationshipEdge> shareEdges,
        IReadOnlySet<Guid> shareFieldItems,
        IReadOnlyList<ShareDocumentBinding> shareDocumentBindings,
        Guid? grantorAtRegistration,
        OwnerInfoAtRegistration? ownerAtRegistration,
        RecordGrantsSnapshot grantsSnapshot,
        LineagePseudonymizer pseudonymizer,
        bool evaluateIndependentCoverage = false)
    {
        ArgumentNullException.ThrowIfNull(share);
        ArgumentNullException.ThrowIfNull(shareEdges);
        ArgumentNullException.ThrowIfNull(shareFieldItems);
        ArgumentNullException.ThrowIfNull(shareDocumentBindings);
        ArgumentNullException.ThrowIfNull(grantsSnapshot);
        ArgumentNullException.ThrowIfNull(pseudonymizer);

        // Extract recipient info from edges
        var (recipientId, recipientKind, shareRole) = ExtractRecipientInfo(shareEdges, share.ResourceEntityId, share.OrganizationId);

        // CRITICAL: Check for insufficient evidence BEFORE any classification attempt.
        // Unverified, null or ambiguous historical ownership cannot fall into verified non-owner.
        // We must refuse to make any assignment or invalidation claim on unverifiable inputs.
        var evidenceValidation = ValidateEvidenceForClassification(
            share.ResourceEntityId,
            share.OrganizationId,
            grantorAtRegistration,
            ownerAtRegistration);

        if (evidenceValidation is not null)
        {
            // Evidence is insufficient - refuse to classify.
            // This is NOT NoCoveringParent (which implies verified non-owner status).
            return CreateInsufficientEvidenceEntry(
                share,
                shareMetadata,
                recipientId,
                recipientKind,
                shareRole,
                grantorAtRegistration,
                shareFieldItems.Count,
                shareDocumentBindings.Count,
                evidenceValidation,
                pseudonymizer);
        }

        var coverage = evaluateIndependentCoverage
            ? EvaluateIndependentCoverage(recipientId, recipientKind, share, shareRole, shareMetadata?.ShareExpiresOn,
                shareFieldItems, shareDocumentBindings, grantsSnapshot)
            : IndependentCoverageAnnotation.NotEvaluated;

        // Evidence is verified: the grantor is known and historical ownership is verified and unambiguous.
        // IMPORTANT: ownership at grant time, NOT current ownership.
        if (WasGrantorOwnerAtRegistration(grantorAtRegistration!.Value, ownerAtRegistration!))
        {
            return CreateEntry(
                share,
                shareMetadata,
                recipientId,
                recipientKind,
                shareRole,
                grantorAtRegistration.Value,
                shareFieldItems.Count,
                shareDocumentBindings.Count,
                LegacyShareClassificationOutcome.OwnerGranted,
                proposedParentId: null,
                proposedDepth: 1,
                [],
                coverage,
                pseudonymizer);
        }

        // For non-owner grants, find covering Manager shares held by the grantor
        var coveringManagerShares = FindCoveringManagerShares(
            grantorAtRegistration.Value,
            share.ResourceEntityId,
            share.OrganizationId,
            share.ParentResourceEntityId ?? Guid.Empty,
            shareFieldItems,
            shareDocumentBindings,
            shareRole,
            shareMetadata?.ShareExpiresOn,
            grantsSnapshot);

        if (coveringManagerShares.Count == 0)
        {
            // No covering parent: grantor holds no Manager share that covers this share's scope
            return CreateEntry(
                share,
                shareMetadata,
                recipientId,
                recipientKind,
                shareRole,
                grantorAtRegistration.Value,
                shareFieldItems.Count,
                shareDocumentBindings.Count,
                LegacyShareClassificationOutcome.NoCoveringParent,
                null,
                null,
                [],
                coverage,
                pseudonymizer);
        }

        if (coveringManagerShares.Count > 1)
        {
            // Multiple candidate parents: cannot resolve automatically per Section 5.1
            return CreateEntry(
                share,
                shareMetadata,
                recipientId,
                recipientKind,
                shareRole,
                grantorAtRegistration.Value,
                shareFieldItems.Count,
                shareDocumentBindings.Count,
                LegacyShareClassificationOutcome.MultipleCandidateParents,
                null,
                null,
                coveringManagerShares.Select(s => s.RecordShareId).ToList(),
                coverage,
                pseudonymizer);
        }

        // Exactly one covering parent
        var parentShare = coveringManagerShares[0];
        var parentDepth = parentShare.DelegationDepth;
        var proposedDepth = parentDepth + 1;

        // Check depth constraints
        if (proposedDepth > MaxDelegationDepth)
        {
            return CreateEntry(
                share,
                shareMetadata,
                recipientId,
                recipientKind,
                shareRole,
                grantorAtRegistration.Value,
                shareFieldItems.Count,
                shareDocumentBindings.Count,
                LegacyShareClassificationOutcome.ExceedsDepth,
                null,
                null,
                [parentShare.RecordShareId],
                coverage,
                pseudonymizer);
        }

        // Check Manager at depth 3 constraint
        if (string.Equals(shareRole, "manager", StringComparison.OrdinalIgnoreCase) && proposedDepth == MaxDelegationDepth)
        {
            return CreateEntry(
                share,
                shareMetadata,
                recipientId,
                recipientKind,
                shareRole,
                grantorAtRegistration.Value,
                shareFieldItems.Count,
                shareDocumentBindings.Count,
                LegacyShareClassificationOutcome.ExceedsDepth,
                null,
                null,
                [parentShare.RecordShareId],
                coverage,
                pseudonymizer);
        }

        // Unambiguous parent found
        return CreateEntry(
            share,
            shareMetadata,
            recipientId,
            recipientKind,
            shareRole,
            grantorAtRegistration.Value,
            shareFieldItems.Count,
            shareDocumentBindings.Count,
            LegacyShareClassificationOutcome.UnambiguousParent,
            parentShare.RecordShareId,
            proposedDepth,
            [parentShare.RecordShareId],
            coverage,
            pseudonymizer);
    }

    private static (Guid recipientId, RecipientKind kind, string role) ExtractRecipientInfo(
        IReadOnlyList<AuthorityRelationshipEdge> shareEdges, Guid targetShareId, Guid targetOrganizationId)
    {
        var roles = shareEdges.Where(edge =>
                edge.ResourceTypeName == ResourceTypeNames.RecordShare &&
                edge.ResourceEntityId == targetShareId &&
                edge.OrganizationId == targetOrganizationId &&
                edge.RemovedBySecurityCommandId is null &&
                edge.RelationshipName is "reader" or "contributor" or "manager")
            .Take(2).ToArray();
        if (roles.Length != 1 || roles[0].SubjectEntityId == Guid.Empty)
        {
            throw new InvalidOperationException("share has no unique current recipient");
        }

        var recipient = roles[0];
        return (recipient.SubjectTypeName, recipient.SubjectRelationName) switch
        {
            (ResourceTypeNames.User, "") => (recipient.SubjectEntityId, RecipientKind.User, recipient.RelationshipName),
            (ResourceTypeNames.Team, "participant") => (recipient.SubjectEntityId, RecipientKind.Team, recipient.RelationshipName),
            _ => throw new InvalidOperationException("share recipient subject is malformed"),
        };
    }

    /// <summary>
    /// Determines if the grantor was the owner at registration time.
    /// PRECONDITION: ValidateEvidenceForClassification has already passed (returned null).
    /// This means: ownerInfo is non-null, IsVerifiable is true, and ownership is unambiguous
    /// (exactly one of OwnerUserId or OwnerTeamId is set, not both).
    /// </summary>
    private static bool WasGrantorOwnerAtRegistration(Guid grantorUserId, OwnerInfoAtRegistration ownerInfo)
    {
        // Note: Validation has already ensured:
        // - IsVerifiable == true
        // - Exactly one of OwnerUserId or OwnerTeamId is set (not both, not neither)
        // These preconditions are enforced by ValidateEvidenceForClassification.

        // Direct user owner
        if (ownerInfo.OwnerUserId.HasValue && ownerInfo.OwnerUserId == grantorUserId)
        {
            return true;
        }

        // Team owner: grantor must be in team member set
        if (ownerInfo.OwnerTeamId.HasValue && ownerInfo.TeamMemberUserIds.Contains(grantorUserId))
        {
            return true;
        }

        return false;
    }

    private static bool HasValidAncestorChain(
        Guid shareId, Guid targetShareId, Guid organizationId, Guid recordId, RecordGrantsSnapshot snapshot)
    {
        var currentId = shareId;
        int? requiredDepth = null;
        for (var level = 0; level < MaxDelegationDepth; level++)
        {
            if (currentId == Guid.Empty || currentId == targetShareId)
            {
                return false;
            }

            var matches = snapshot.ActiveShares.Where(s =>
                s.RecordShareId == currentId && s.OrganizationId == organizationId && s.RecordId == recordId).Take(2).ToArray();
            if (matches.Length != 1 || !IsActiveAtSnapshot(matches[0], snapshot) ||
                matches[0].DelegationDepth is not (>= 1 and <= MaxDelegationDepth) ||
                (requiredDepth.HasValue && matches[0].DelegationDepth != requiredDepth))
            {
                return false;
            }

            var currentShare = matches[0];
            if (currentShare.DelegationDepth == 1)
            {
                return currentShare.ParentRecordShareId is null;
            }

            if (currentShare.ParentRecordShareId is not { } parentId)
            {
                return false;
            }

            currentId = parentId;
            requiredDepth = currentShare.DelegationDepth - 1;
        }

        return false;
    }

    /// <summary>
    /// Finds all Active Manager shares held by the grantor that cover the target share's scope.
    /// Implements the single-parent cap: returns all candidates, caller must handle multiple.
    /// Only accepts preverified candidate evidence; unknown/unverifiable depth is not defaulted to valid.
    /// </summary>
    private static List<CandidateParentShare> FindCoveringManagerShares(
        Guid grantorUserId,
        Guid targetShareId,
        Guid targetOrganizationId,
        Guid targetRecordId,
        IReadOnlySet<Guid> targetFieldItems,
        IReadOnlyList<ShareDocumentBinding> targetDocuments,
        string targetRole,
        DateTimeOffset? targetExpiry,
        RecordGrantsSnapshot grantsSnapshot)
    {
        var candidates = new List<CandidateParentShare>();

        foreach (var share in grantsSnapshot.ActiveShares)
        {
            // Skip self
            if (share.RecordShareId == targetShareId) continue;

            // Must be same organization and same record (proven Active at snapshot instant)
            if (share.OrganizationId != targetOrganizationId) continue;
            if (share.RecordId != targetRecordId) continue;

            if (!IsActiveAtSnapshot(share, grantsSnapshot)) continue;

            // Check if grantor holds Manager role on this share (directly or via team)
            if (!share.HoldsRole(grantorUserId, "manager"))
            {
                continue;
            }

            // Check if this share covers all target field items
            if (!targetFieldItems.IsSubsetOf(share.FieldItemIds))
            {
                continue;
            }

            // Check if this share covers all target documents with appropriate flags
            if (!CoversDocuments(targetDocuments, share.DocumentBindings))
            {
                continue;
            }

            // Check role hierarchy: Manager can delegate Reader, Contributor, or Manager (but not Manager at depth 3)
            if (!CanDelegateRole(share.Role, targetRole))
            {
                continue;
            }

            // Check expiry: target share's effective expiry must not exceed parent's
            // Use verified effective expiry at the same snapshot instant
            if (!ExpiryCompatible(targetExpiry, share.EffectiveExpiresOn))
            {
                continue;
            }

            // Unknown or unverifiable depth cannot be assumed valid; exclude from candidates
            if (!share.DelegationDepth.HasValue)
            {
                continue;
            }

            // Validate bounded full chain from this candidate to explicit depth-1 root
            // Ensures: same org/record, active, unique parent for depth>1, decreasing depth 1..3, no cycles, target not in lineage
            if (!HasValidAncestorChain(share.RecordShareId, targetShareId, targetOrganizationId, targetRecordId, grantsSnapshot))
            {
                continue;
            }

            candidates.Add(new CandidateParentShare(
                share.RecordShareId,
                share.DelegationDepth.Value));
        }

        return candidates;
    }

    /// <summary>
    /// Checks if a parent share's documents cover all target documents with required flags.
    /// </summary>
    private static bool CoversDocuments(
        IReadOnlyList<ShareDocumentBinding> targetDocuments,
        IReadOnlyList<ShareDocumentBinding> parentDocuments)
    {
        foreach (var target in targetDocuments)
        {
            var parent = parentDocuments.FirstOrDefault(d => d.FileAttachmentId == target.FileAttachmentId);
            if (parent is null)
            {
                return false;
            }

            if (parent.OrganizationId != target.OrganizationId ||
                parent.SharedThroughVersionNumber < target.SharedThroughVersionNumber)
            {
                return false;
            }

            // Parent must have at least the same permissions
            if (target.CanViewDocument && !parent.CanViewDocument) return false;
            if (target.CanDownloadDocument && !parent.CanDownloadDocument) return false;
            if (target.CanReplaceDocument && !parent.CanReplaceDocument) return false;
        }

        return true;
    }

    /// <summary>
    /// Checks if a parent role can delegate to a target role.
    /// Manager can delegate Reader, Contributor, or Manager.
    /// Reader and Contributor cannot delegate.
    /// </summary>
    private static bool CanDelegateRole(string parentRole, string targetRole)
    {
        // Only Manager can delegate
        if (!string.Equals(parentRole, "manager", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Manager can delegate to any role
        return string.Equals(targetRole, "reader", StringComparison.OrdinalIgnoreCase)
            || string.Equals(targetRole, "contributor", StringComparison.OrdinalIgnoreCase)
            || string.Equals(targetRole, "manager", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks if target expiry is compatible with parent expiry.
    /// Target's effective expiry must not exceed parent's.
    /// </summary>
    private static bool ExpiryCompatible(DateTimeOffset? targetExpiry, DateTimeOffset? parentExpiry)
    {
        // If parent has no expiry, any target expiry is valid
        if (parentExpiry is null)
        {
            return true;
        }

        // If target has no expiry but parent does, incompatible
        if (targetExpiry is null)
        {
            return false;
        }

        // Target must expire no later than parent
        return targetExpiry.Value <= parentExpiry.Value;
    }

    private static bool IsActiveAtSnapshot(ActiveShareInfo share, RecordGrantsSnapshot snapshot) =>
        share.IsActive && (share.EffectiveExpiresOn is not { } expiry ||
                           (snapshot.ObservedOn is { } observed && expiry > observed));

    /// <summary>Checks whether a separate eligible grant would preserve equivalent recipient access.</summary>
    private static IndependentCoverageAnnotation EvaluateIndependentCoverage(
        Guid recipientId,
        RecipientKind recipientKind,
        AuthorityResource target,
        string targetRole,
        DateTimeOffset? targetExpiry,
        IReadOnlySet<Guid> targetFieldItems,
        IReadOnlyList<ShareDocumentBinding> targetDocuments,
        RecordGrantsSnapshot grantsSnapshot)
    {
        if (recipientId == Guid.Empty)
        {
            return IndependentCoverageAnnotation.NotEvaluated;
        }

        foreach (var share in grantsSnapshot.ActiveShares)
        {
            // Skip the share we're evaluating coverage for
            if (share.RecordShareId == target.ResourceEntityId ||
                share.OrganizationId != target.OrganizationId || share.RecordId != target.ParentResourceEntityId ||
                share.ParentRecordShareId == target.ResourceEntityId)
            {
                continue;
            }

            if (!IsActiveAtSnapshot(share, grantsSnapshot)) continue;

            // Check if recipient holds a role on this share
            var hasRole = recipientKind == RecipientKind.Team
                ? share.TeamRecipientIds.Contains(recipientId)
                : share.DirectUserRecipientIds.Contains(recipientId) || share.TeamMemberUserIds.Contains(recipientId);

            if (!hasRole) continue;

            // Check if this share's role is equivalent or broader than target
            // Manager > Contributor > Reader
            if (!IsEquivalentOrBroaderRole(share.Role, targetRole))
            {
                continue;
            }

            // Check if this share covers all target field items
            if (!targetFieldItems.IsSubsetOf(share.FieldItemIds))
            {
                continue;
            }

            // Check if this share covers all target documents with equivalent or broader flags
            if (!CoversDocuments(targetDocuments, share.DocumentBindings))
            {
                continue;
            }

            if (!ExpiryCompatible(targetExpiry, share.EffectiveExpiresOn))
            {
                continue;
            }

            // Validate bounded full chain from this independent share to explicit depth-1 root
            // Ensures: same org/record, active, unique parent for depth>1, decreasing depth 1..3, no cycles, target not in lineage
            if (!HasValidAncestorChain(share.RecordShareId, target.ResourceEntityId, target.OrganizationId, target.ParentResourceEntityId ?? Guid.Empty, grantsSnapshot))
            {
                continue;
            }

            // Found independent coverage
            return IndependentCoverageAnnotation.IndependentlyCovered;
        }

        return IndependentCoverageAnnotation.NotCovered;
    }

    /// <summary>
    /// Checks if a role is equivalent to or broader than another role.
    /// Manager > Contributor > Reader.
    /// </summary>
    private static bool IsEquivalentOrBroaderRole(string candidateRole, string targetRole)
    {
        var roleHierarchy = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            { "reader", 1 },
            { "contributor", 2 },
            { "manager", 3 }
        };

        if (!roleHierarchy.TryGetValue(candidateRole, out var candidateLevel))
        {
            return false;
        }

        if (!roleHierarchy.TryGetValue(targetRole, out var targetLevel))
        {
            return false;
        }

        return candidateLevel >= targetLevel;
    }

    /// <summary>
    /// Validates whether sufficient evidence exists to perform classification.
    /// Returns null if evidence is sufficient; returns a validation error otherwise.
    /// CRITICAL: Unverified, null or ambiguous historical ownership cannot fall into verified non-owner.
    /// </summary>
    private static LineageEvidenceValidationError? ValidateEvidenceForClassification(
        Guid shareId,
        Guid organizationId,
        Guid? grantorAtRegistration,
        OwnerInfoAtRegistration? ownerAtRegistration)
    {
        // Case 1: Unknown grantor - we cannot determine who created the share
        if (grantorAtRegistration is not { } grantor || grantor == Guid.Empty)
        {
            return new LineageEvidenceValidationError
            {
                RecordShareId = shareId,
                OrganizationId = organizationId,
                Reason = InsufficientEvidenceReason.UnknownGrantor
            };
        }

        // Case 2: No historical ownership evidence at all
        if (ownerAtRegistration is null)
        {
            return new LineageEvidenceValidationError
            {
                RecordShareId = shareId,
                OrganizationId = organizationId,
                Reason = InsufficientEvidenceReason.MissingOwnershipEvidence
            };
        }

        // Case 3: Historical ownership exists but is not verifiable
        if (!ownerAtRegistration.IsVerifiable)
        {
            return new LineageEvidenceValidationError
            {
                RecordShareId = shareId,
                OrganizationId = organizationId,
                Reason = InsufficientEvidenceReason.UnverifiableOwnership
            };
        }

        // Case 4: Ambiguous ownership - both user and team owner present
        var hasUserOwner = ownerAtRegistration.OwnerUserId.HasValue;
        var hasTeamOwner = ownerAtRegistration.OwnerTeamId.HasValue;
        if (hasUserOwner && hasTeamOwner)
        {
            return new LineageEvidenceValidationError
            {
                RecordShareId = shareId,
                OrganizationId = organizationId,
                Reason = InsufficientEvidenceReason.AmbiguousOwnership
            };
        }

        // Case 5: Verifiable but no actual ownership evidence (neither user nor team)
        // This should not happen with proper data, but we must handle it safely
        if (!hasUserOwner && !hasTeamOwner)
        {
            return new LineageEvidenceValidationError
            {
                RecordShareId = shareId,
                OrganizationId = organizationId,
                Reason = InsufficientEvidenceReason.MissingOwnershipEvidence
            };
        }

        // Evidence is sufficient for classification
        return null;
    }

    /// <summary>
    /// Creates a classification entry for InsufficientEvidence outcome.
    /// No assignment or invalidation claim is made.
    /// </summary>
    private static LegacyShareClassificationEntry CreateInsufficientEvidenceEntry(
        AuthorityResource share,
        ShareResourceMetadata? metadata,
        Guid recipientId,
        RecipientKind recipientKind,
        string shareRole,
        Guid? grantorAtRegistration,
        int fieldItemCount,
        int documentItemCount,
        LineageEvidenceValidationError validationError,
        LineagePseudonymizer pseudonymizer)
    {
        // The registering actor is reported only when it is known; an unknown grantor has no reference rather than a
        // placeholder that could be read as an identity.
        return new LegacyShareClassificationEntry
        {
            RecordShareId = share.ResourceEntityId,
            FormRecordId = share.ParentResourceEntityId ?? Guid.Empty,
            OrganizationId = share.OrganizationId,
            RecipientKind = recipientKind,
            RecipientPseudonymousReference = pseudonymizer.PseudonymizeRecipient(recipientId),
            GrantorPseudonymousReference = grantorAtRegistration is { } grantor && grantor != Guid.Empty
                ? pseudonymizer.PseudonymizeGrantor(grantor)
                : null,
            Classification = LegacyShareClassificationOutcome.InsufficientEvidence,
            FieldItemCount = fieldItemCount,
            DocumentItemCount = documentItemCount,
            CandidateParentShareIds = [],
            ProposedParentShareId = null, // No assignment claim
            ProposedDelegationDepth = null, // No depth assignment
            ShareRole = shareRole,
            IndependentCoverage = IndependentCoverageAnnotation.NotEvaluated, // Cannot evaluate without valid classification
            HasExpiry = metadata?.ShareExpiresOn is not null,
            IsLegacyShare = metadata?.LegacyLineageUnresolved ?? true,
            EvidenceValidationError = validationError
        };
    }

    private static LegacyShareClassificationEntry CreateEntry(
        AuthorityResource share,
        ShareResourceMetadata? metadata,
        Guid recipientId,
        RecipientKind recipientKind,
        string shareRole,
        Guid grantorUserId,
        int fieldItemCount,
        int documentItemCount,
        LegacyShareClassificationOutcome classification,
        Guid? proposedParentId,
        int? proposedDepth,
        IReadOnlyList<Guid> candidateParentIds,
        IndependentCoverageAnnotation independentCoverage,
        LineagePseudonymizer pseudonymizer)
    {
        return new LegacyShareClassificationEntry
        {
            RecordShareId = share.ResourceEntityId,
            FormRecordId = share.ParentResourceEntityId ?? Guid.Empty,
            OrganizationId = share.OrganizationId,
            RecipientKind = recipientKind,
            RecipientPseudonymousReference = pseudonymizer.PseudonymizeRecipient(recipientId),
            GrantorPseudonymousReference = pseudonymizer.PseudonymizeGrantor(grantorUserId),
            Classification = classification,
            FieldItemCount = fieldItemCount,
            DocumentItemCount = documentItemCount,
            CandidateParentShareIds = candidateParentIds,
            ProposedParentShareId = proposedParentId,
            ProposedDelegationDepth = proposedDepth,
            ShareRole = shareRole,
            IndependentCoverage = independentCoverage,
            HasExpiry = metadata?.ShareExpiresOn is not null,
            IsLegacyShare = metadata?.LegacyLineageUnresolved ?? true
        };
    }
}

/// <summary>
/// Represents ownership information at the time a share was registered.
/// Used to determine if a share was owner-granted.
/// </summary>
public sealed record OwnerInfoAtRegistration
{
    /// <summary>The user ID of the direct owner at registration time, or null if team-owned.</summary>
    public Guid? OwnerUserId { get; init; }

    /// <summary>The team ID of the owning team at registration time, or null if user-owned.</summary>
    public Guid? OwnerTeamId { get; init; }

    /// <summary>The member user IDs of the owning team at registration time (for team ownership).</summary>
    public IReadOnlySet<Guid> TeamMemberUserIds { get; init; } = new HashSet<Guid>();

    /// <summary>
    /// Indicates whether the historical owner information is verifiable: the owner principal and, for a team owner, the
    /// complete team member set at the registration instant. If false, the classifier refuses the share
    /// (<see cref="LegacyShareClassificationOutcome.InsufficientEvidence"/>): it is neither owner-granted nor a verified non-owner.
    /// </summary>
    public bool IsVerifiable { get; init; }
}

/// <summary>
/// A snapshot of all active grants on a record for lineage evaluation.
/// All shares must be from the same REPEATABLE READ snapshot instant with preverified effective state.
/// </summary>
public sealed record RecordGrantsSnapshot
{
    /// <summary>The Authority clock instant recorded for the read-only snapshot.</summary>
    public DateTimeOffset? ObservedOn { get; init; }

    /// <summary>All active shares on the record at the snapshot instant.</summary>
    public IReadOnlyList<ActiveShareInfo> ActiveShares { get; init; } = [];
}

/// <summary>
/// Information about an active share for lineage evaluation.
/// All properties must be preverified by the future read-only adapter from a REPEATABLE READ snapshot.
/// Unknown/unverifiable values must be represented as null, not defaulted to valid assumptions.
/// </summary>
public sealed record ActiveShareInfo
{
    /// <summary>The share's ID.</summary>
    public required Guid RecordShareId { get; init; }

    /// <summary>The organization ID (must match target for coverage).</summary>
    public required Guid OrganizationId { get; init; }

    /// <summary>The record ID (must match target for coverage).</summary>
    public required Guid RecordId { get; init; }

    /// <summary>The share's role (reader, contributor, manager).</summary>
    public required string Role { get; init; }

    /// <summary>The share's delegation depth (1 for owner-granted, 2+ for delegated). Null if unknown/unverifiable.</summary>
    public int? DelegationDepth { get; init; }

    /// <summary>The parent share ID if this is a delegated share. Null for owner-granted shares.</summary>
    public Guid? ParentRecordShareId { get; init; }

    /// <summary>Whether this share is Active at the snapshot instant (must be preverified).</summary>
    public required bool IsActive { get; init; }

    /// <summary>The verified effective expiry time at the same snapshot instant. Null if no expiry.</summary>
    public DateTimeOffset? EffectiveExpiresOn { get; init; }

    /// <summary>Field item IDs in this share's scope.</summary>
    public IReadOnlySet<Guid> FieldItemIds { get; init; } = new HashSet<Guid>();

    /// <summary>Document bindings in this share's scope.</summary>
    public IReadOnlyList<ShareDocumentBinding> DocumentBindings { get; init; } = [];

    /// <summary>Direct user recipient IDs (users who hold roles on this share).</summary>
    public IReadOnlySet<Guid> DirectUserRecipientIds { get; init; } = new HashSet<Guid>();

    /// <summary>Team recipient IDs.</summary>
    public IReadOnlySet<Guid> TeamRecipientIds { get; init; } = new HashSet<Guid>();

    /// <summary>Team member user IDs (for evaluating team-based access).</summary>
    public IReadOnlySet<Guid> TeamMemberUserIds { get; init; } = new HashSet<Guid>();

    /// <summary>
    /// Checks if a user holds the specified role on this share (directly or via team).
    /// </summary>
    public bool HoldsRole(Guid userId, string role)
    {
        if (!string.Equals(Role, role, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return DirectUserRecipientIds.Contains(userId) || TeamMemberUserIds.Contains(userId);
    }
}

/// <summary>
/// A candidate parent share for lineage assignment.
/// </summary>
public sealed record CandidateParentShare(Guid RecordShareId, int DelegationDepth);
