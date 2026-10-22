using System.Text.Json;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.Audit;
using EnterpriseForms.Application.Features.FormDefinitions.Layout;
using EnterpriseForms.Application.Features.FormRecords.Projection;
using EnterpriseForms.Contracts.Features;
using EnterpriseForms.Domain.Features.Audit;

namespace EnterpriseForms.Application.Features.RelatedForms;

/// <summary>
/// Child collections (PG-018/019, B-04): the list is sourced from the local link projection but every child shown is
/// re-authorized and verified through the authority (verified read per child; unauthorized or quarantined children are
/// omitted without a count). Unlink is an authority command; the projection follows.
/// </summary>
public sealed class ChildRecordHandlers(IBusinessStore store, IAuthorizationAuthorityClient authority, AuthorityCommandAwaiter awaiter, VerifiedRecordReader reader, TimeProvider clock)
{
    public async Task<ChildRecordListResponse> ListAsync(RequestContext context, Guid parentFormRecordId, Guid formRelationshipDefinitionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var parent = await reader.ReadAsync(context, parentFormRecordId, ProtectedAction.Read, cancellationToken).ConfigureAwait(false);
        Domain.Features.RelatedForms.FormRelationshipDefinition relationship;
        List<Domain.Features.RelatedForms.FormRecordRelationship> links;
        string childName;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            relationship = await scope.FindRelationshipDefinitionAsync(context.OrganizationId, formRelationshipDefinitionId, cancellationToken).ConfigureAwait(false)
                           ?? throw new BusinessException("NOT_FOUND", 404, "relationship not found");
            if (relationship.ParentFormDefinitionId != parent.Definition.FormDefinitionId)
            {
                throw new BusinessException("NOT_FOUND", 404, "relationship not found");
            }

            links = await scope.GetActiveChildLinksAsync(parentFormRecordId, formRelationshipDefinitionId, cancellationToken).ConfigureAwait(false);
            childName = (await scope.FindFormDefinitionAsync(context.OrganizationId, relationship.ChildFormDefinitionId, cancellationToken).ConfigureAwait(false))?.FormDefinitionName ?? string.Empty;
        }

        var element = parent.Layout.Elements.FirstOrDefault(e => e.Kind == LayoutElementKinds.RelatedFormList && e.FormRelationshipDefinitionId == formRelationshipDefinitionId);
        var summaryKeys = element?.SummaryFieldKeys ?? [];
        var items = new List<ChildRecordSummaryResponse>();
        foreach (var link in links.Take(relationship.MaximumChildCount))
        {
            try
            {
                var child = await reader.ReadAsync(context, link.ChildFormRecordId, ProtectedAction.Read, cancellationToken).ConfigureAwait(false);
                // The signed child header must agree with the link projection; a forged local link is ignored.
                if (child.Header.ParentFormRecordId != parentFormRecordId || child.Header.FormRelationshipDefinitionId != formRelationshipDefinitionId)
                {
                    continue;
                }

                var values = child.ToValueResponses();
                var summary = summaryKeys.Count == 0 ? values : values.Where(v => summaryKeys.Contains(v.FieldKey, StringComparer.Ordinal)).ToList();
                items.Add(new ChildRecordSummaryResponse(link.ChildFormRecordId, child.Header.RelationshipSortOrder ?? link.ListSortOrder, child.Header.RecordStatus, child.Head.CommittedContentRevision, summary, child.IsComplete ? "Verified" : "Verified(partial)"));
            }
            catch (BusinessException ex) when (ex.HttpStatusCode is 404 or 423)
            {
                // not discoverable by this caller, or quarantined: omitted without disclosure (no hidden counts)
            }
            catch (AuthorityClientException ex) when (ex.HttpStatusCode is 404 or 403)
            {
            }
        }

        return new ChildRecordListResponse(
            new RelationshipDefinitionResponse(relationship.FormRelationshipDefinitionId, relationship.ParentFormDefinitionId, relationship.ChildFormDefinitionId, childName, relationship.RelationshipDefinitionKey, relationship.RelationshipDisplayName, relationship.MinimumChildCount, relationship.MaximumChildCount, relationship.LifecycleStatus),
            items.OrderBy(i => i.ListSortOrder).ToList(),
            parent.Verification.AuthorizationRevision);
    }

    public async Task<CommandStatusResponse> UnlinkAsync(RequestContext context, Guid parentFormRecordId, Guid childFormRecordId, UnlinkChildRecordRequest request, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.RemovalReason) || request.RemovalReason.Length > 1000)
        {
            throw new BusinessException("INVALID_REQUEST", 422, "a removal reason is required");
        }

        var user = context.ToDelegatedUser();
        var decision = await authority.CheckPermissionAsync(user, new PermissionCheckRequest(context.OrganizationId, new ResourceReference(ProtectedResourceKind.FormRecord, parentFormRecordId), ProtectedAction.Edit), cancellationToken).ConfigureAwait(false);
        if (!decision.Allowed)
        {
            throw new BusinessException("NOT_FOUND", 404, "parent record not found");
        }

        // Phase C6 M1: removing a child changes the parent's content, so a Submitted or Archived parent is locked.
        FormRecords.Lifecycle.RecordLifecycle.RequireContentEditable(await reader.ReadAsync(context, parentFormRecordId, ProtectedAction.Edit, cancellationToken).ConfigureAwait(false));

        var payload = JsonSerializer.SerializeToElement(new { form_record_id = childFormRecordId, parent_form_record_id = parentFormRecordId });
        var command = await awaiter.SubmitAndAwaitAsync(user, new SecurityCommandRequest(context.OrganizationId, SecurityCommandKind.UnlinkChildRecord, new ResourceReference(ProtectedResourceKind.FormRecord, childFormRecordId), decision.AuthorizationRevision, payload, request.RemovalReason.Trim()), "unlink:" + idempotencyKey, cancellationToken).ConfigureAwait(false);
        var outcome = new CommandOutcome(command.CommandId, command.CommandStatus.ToString(), command.AuthorizationRevision, command.CommandStatus == SecurityCommandStatus.Effective);
        if (outcome.Effective)
        {
            await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
            var link = await scope.FindActiveChildLinkAsync(childFormRecordId, cancellationToken).ConfigureAwait(false);
            if (link is not null && link.ParentFormRecordId == parentFormRecordId)
            {
                link.LinkLifecycleState = "Removed";
                link.LinkRemovedOn = clock.GetUtcNow();
                link.LinkRemovedByUserId = context.LocalUserId;
            }

            var child = await scope.FindFormRecordAsync(context.OrganizationId, childFormRecordId, cancellationToken).ConfigureAwait(false);
            if (child is not null)
            {
                child.ParentFormRecordId = null;
                child.FormRelationshipDefinitionId = null;
                child.OwnerUserId = context.LocalUserId;
                child.OwnerTeamId = null;
                child.ModifiedOn = clock.GetUtcNow();
                child.ModifiedByUserId = context.LocalUserId;
            }

            AuditRecorder.Record(scope, context, clock, AuditCategories.Relationship, AuditActions.ChildRecordUnlinked, "child record unlinked (data retained; remover becomes owner)", childFormRecordId, targetEntityType: "form_record", targetEntityId: parentFormRecordId, metadata: new { reason = request.RemovalReason.Trim(), commandId = command.CommandId });
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return new CommandStatusResponse(outcome);
    }
}
