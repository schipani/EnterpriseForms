using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Authorization;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Organizations;
using EnterpriseForms.Integrity.Merkle;

namespace AuthorizationAuthority.Application.Features.SecurityCommands;

public static partial class CommandPlanner
{
    private static async Task<CommandPlan> UpdateShareScopeAsync(
        IAuthorityScope scope,
        Guid organizationId,
        BoundActor actor,
        SecurityCommandRequest request,
        DesiredStateGraph graph,
        HumanCommandStepUpGate humanGate,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        var payload = CanonicalJson.Deserialize<UpdateShareScopePayload>(request.CommandPayload);
        RequireResource(request, ResourceKind.FormRecord, payload.FormRecordId);
        humanGate.RequireStepUp(payload.StepUpEvidence);

        var owner = graph.IsRecordOwner(actor.AuthorityUserId, payload.FormRecordId);
        var sharesOnRecord = graph.ShareIdsOfRecord(payload.FormRecordId).ToHashSet();
        if (!owner)
        {
            if (payload.ParentRecordShareId is not { } namedParent || !sharesOnRecord.Contains(namedParent) ||
                !await HoldsActiveManagerShareAsync(scope, graph, organizationId, actor.AuthorityUserId, namedParent, cancellationToken).ConfigureAwait(false))
            {
                throw AuthorityException.Forbidden("actor cannot update this share");
            }

            var parentLineage = await ReadShareLineageAsync(scope, organizationId, sharesOnRecord, namedParent, cancellationToken).ConfigureAwait(false);
            if (ShareLineageExpiry.Evaluate(parentLineage, organizationId, payload.FormRecordId, namedParent, at).Status != ShareLineageStatus.Active)
            {
                throw AuthorityException.Forbidden("actor cannot update this share");
            }
        }

        var chain = await ReadShareLineageAsync(scope, organizationId, sharesOnRecord, payload.RecordShareId, cancellationToken).ConfigureAwait(false);
        if (!chain.TryGetValue(payload.RecordShareId, out var target) || target.Metadata is not { } metadata ||
            (!owner && metadata.ParentRecordShareId != payload.ParentRecordShareId))
        {
            throw AuthorityException.Forbidden("actor cannot update this share");
        }

        await RequireActiveResourceAsync(scope, organizationId, ResourceTypeNames.FormRecord, payload.FormRecordId, cancellationToken).ConfigureAwait(false);
        var current = ShareLineageExpiry.Evaluate(chain, organizationId, payload.FormRecordId, payload.RecordShareId, at);
        if (current.Status != ShareLineageStatus.Active || metadata.ShareInvalidationReasonCode is not null)
        {
            throw AuthorityException.Invalid("share must have active, resolved lineage");
        }

        if (payload.ParentRecordShareId is { } suppliedParent && metadata.ParentRecordShareId != suppliedParent)
        {
            throw AuthorityException.Invalid("an existing share keeps its committed parent");
        }

        if (metadata.ShareRevision != payload.ExpectedShareRevision)
        {
            throw new AuthorityException(AuthorityErrorCodes.StaleShareRevision, 412, false, "expected_share_revision is stale");
        }

        if (metadata.ShareRevision is <= 0 or long.MaxValue)
        {
            throw AuthorityException.Invalid("share revision cannot be advanced");
        }

        var role = payload.RecipientRole switch
        {
            RecipientRoles.Reader => "reader",
            RecipientRoles.Contributor => "contributor",
            RecipientRoles.Manager => "manager",
            _ => throw AuthorityException.Invalid("unknown recipient role"),
        };
        var policy = await scope.FindProtectionPolicyAsync(organizationId, cancellationToken).ConfigureAwait(false);
        if (metadata.DelegationDepth > (policy?.MaximumDelegationDepth ?? MaximumShareDelegationDepth))
        {
            throw new AuthorityException(AuthorityErrorCodes.DelegationDepthExceeded, 422, false, "share exceeds the organization's maximum depth");
        }

        if (role == "manager" && metadata.DelegationDepth == MaximumShareDelegationDepth)
        {
            throw new AuthorityException(AuthorityErrorCodes.ManagerAtMaximumDepth, 422, false, "Manager shares cannot be granted at depth three");
        }

        var requestedFields = payload.FieldItemIds;
        if (requestedFields is not { Count: > 0 and <= 500 } || requestedFields.Distinct().Count() != requestedFields.Count)
        {
            throw AuthorityException.Invalid("field_item_ids must be a non-empty distinct list");
        }

        var head = await scope.FindContentHeadAsync(organizationId, payload.FormRecordId, lockForUpdate: false, cancellationToken).ConfigureAwait(false);
        if (head?.CurrentRecordManifestCommitId is not { } manifestId)
        {
            throw AuthorityException.Invalid("record has no accepted content to share");
        }

        var leaves = await scope.GetManifestLeavesAsync(manifestId, cancellationToken).ConfigureAwait(false);
        var committedFields = leaves.Where(l => l.ManifestItemKindCode is (byte)MerkleItemKind.FieldValue or (byte)MerkleItemKind.FieldSelection)
            .Select(l => l.ManifestItemId).ToHashSet();
        if (!requestedFields.All(committedFields.Contains))
        {
            throw AuthorityException.Invalid("field_item_ids must reference items in the accepted manifest");
        }

        var expiresOn = payload.ShareExpiresOn?.ToUniversalTime();
        if (expiresOn is { } expiry && expiresOn != metadata.ShareExpiresOn &&
            (expiry - at < TimeSpan.FromHours(1) || expiry - at > TimeSpan.FromDays(365)))
        {
            throw AuthorityException.Invalid("changed share expiry must be between one hour and 365 days from the Authority clock");
        }

        if (metadata.ParentRecordShareId is { } parentId)
        {
            var parent = ShareLineageExpiry.Evaluate(chain, organizationId, payload.FormRecordId, parentId, at);
            if (parent.Status != ShareLineageStatus.Active ||
                (parent.EffectiveExpiry is { } cap && (expiresOn is null || expiresOn > cap)) ||
                chain.Keys.Where(id => id != payload.RecordShareId).Any(id => !requestedFields.All(graph.FieldItemsOfShare(id).Contains)))
            {
                throw AuthorityException.Invalid("updated scope exceeds the committed parent's effective scope");
            }
        }

        var roleEdges = graph.EdgesOf(ResourceTypeNames.RecordShare, payload.RecordShareId)
            .Where(e => e.RelationshipName is "reader" or "contributor" or "manager").Take(2).ToArray();
        if (roleEdges.Length != 1 || roleEdges[0].SubjectEntityId == Guid.Empty ||
            (roleEdges[0].SubjectTypeName, roleEdges[0].SubjectRelationName) is not ((ResourceTypeNames.User, "") or (ResourceTypeNames.Team, "participant")))
        {
            throw AuthorityException.Invalid("share must have one current direct or team recipient");
        }

        var fields = requestedFields.ToHashSet();
        var currentFields = graph.EdgesWithSubject(ResourceTypeNames.RecordShare, payload.RecordShareId)
            .Where(e => e.ResourceTypeName == ResourceTypeNames.FieldItem && e.RelationshipName == "shared_via").ToList();
        var recipient = roleEdges[0];
        if ((role != recipient.RelationshipName || expiresOn != metadata.ShareExpiresOn ||
             !fields.SetEquals(currentFields.Select(e => e.ResourceEntityId))) &&
            await HasDependentShareAsync(scope, organizationId, payload.RecordShareId, cancellationToken).ConfigureAwait(false))
        {
            throw new AuthorityException(AuthorityErrorCodes.ShareHasActiveDescendants, 422, false, "dependent shares require an atomic cascade");
        }

        var nextRevision = metadata.ShareRevision + 1;
        var plan = new CommandPlan
        {
            NormalizedPayloadJson = CanonicalJson.Serialize(new
            {
                payload.RecordShareId,
                payload.FormRecordId,
                payload.RecipientRole,
                payload.FieldItemIds,
                metadata.ParentRecordShareId,
                payload.ExpectedShareRevision,
                ShareRevision = nextRevision,
                ShareExpiresOn = expiresOn,
            }),
            ShareMetadataUpdate = (metadata, nextRevision, expiresOn),
        };

        var removedFields = currentFields.Where(e => !fields.Contains(e.ResourceEntityId)).ToList();
        plan.RemoveEdges.AddRange(removedFields);
        var fieldCount = fields.Count;
        fields.ExceptWith(currentFields.Select(e => e.ResourceEntityId));
        foreach (var itemId in fields)
        {
            plan.AddEdges.Add((ResourceTypeNames.FieldItem, itemId, "shared_via", ResourceTypeNames.RecordShare, payload.RecordShareId, string.Empty));
        }

        if (recipient.RelationshipName != role)
        {
            plan.RemoveEdges.Add(recipient);
            plan.AddEdges.Add((ResourceTypeNames.RecordShare, payload.RecordShareId, role, recipient.SubjectTypeName, recipient.SubjectEntityId, recipient.SubjectRelationName));
        }

        // C10 M1-WP9: journal evidence of the accepted change, in counts and flags (ShareLifecycleJournal).
        plan.ShareScopeChange = new ShareScopeChangeEvidence(payload.RecordShareId, payload.FormRecordId, metadata.ShareRevision, nextRevision, role,
            recipient.RelationshipName != role, fieldCount, fields.Count, removedFields.Count, expiresOn != metadata.ShareExpiresOn, expiresOn is not null);
        return plan;
    }

    private static async Task<bool> HasDependentShareAsync(IAuthorityScope scope, Guid organizationId, Guid shareId, CancellationToken cancellationToken)
    {
        foreach (var child in await scope.FindShareMetadataByParentAsync(organizationId, shareId, cancellationToken).ConfigureAwait(false))
        {
            var resource = await scope.FindResourceAsync(organizationId, ResourceTypeNames.RecordShare, child.RecordShareId, cancellationToken).ConfigureAwait(false);
            if (resource is null || resource.ResourceLifecycleState == ResourceLifecycleStates.Active ||
                (await scope.FindShareMetadataByParentAsync(organizationId, child.RecordShareId, cancellationToken).ConfigureAwait(false)).Count > 0)
            {
                return true;
            }
        }

        return false;
    }
}
