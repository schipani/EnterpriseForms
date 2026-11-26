using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Journal;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;

namespace AuthorizationAuthority.Application.Features.SecurityCommands;

/// <summary>
/// C10 M1-WP9 (WP-0 Sections 3.4, 3.5 and 9): named, record-scoped share lifecycle evidence on the hash-chained, archived audit
/// stream. Called only from <see cref="SecurityCommandService.ApplyPlan"/>, so each entry is staged in the accepted command's own
/// locked transaction and becomes durable with it or not at all; a rejected or replayed command never reaches it. The canonical
/// <c>SecurityCommandCommitted</c> body on the authorization stream is unchanged.
/// <para>
/// Every body is an allowlist of identifiers, codes, counts and flags: it names the exact command and the authorization revision it
/// committed, and records the state the command had when it committed (<see cref="SecurityCommandStates.CommittedAwaitingProjection"/>),
/// never Effective — whether the change is effective is the command's current state, read from the command row. No field item ids,
/// document ids, expiry values, step-up evidence, claims, reasons or record values are written.
/// </para>
/// </summary>
internal static class ShareLifecycleJournal
{
    public const string ShareScopeUpdated = "ShareScopeUpdated";

    public const string ShareInvalidated = "ShareInvalidated";

    public const string ShareExpired = "ShareExpired";

    /// <summary>Appends the scope-change entry, then one entry per invalidated dependent share (by share id), to the audit stream.</summary>
    public static void Append(IAuthorityScope scope, OrganizationAuthorizationState locked, SecurityCommand command, CommandPlan plan, long committedRevision)
    {
        if (plan.ShareScopeChange is { } change)
        {
            AuditJournal.Append(scope, locked, ShareScopeUpdated, new
            {
                security_command_id = command.SecurityCommandId,
                command_kind = command.CommandKindName,
                form_record_id = change.FormRecordId,
                record_share_id = change.RecordShareId,
                previous_share_revision = change.PreviousShareRevision,
                share_revision = change.ShareRevision,
                authorization_revision = committedRevision,
                recipient_role = change.RecipientRole,
                role_changed = change.RoleChanged,
                field_item_count = change.FieldItemCount,
                field_items_added = change.FieldItemsAdded,
                field_items_removed = change.FieldItemsRemoved,
                expiry_changed = change.ExpiryChanged,
                has_expiry = change.HasExpiry,
                command_state_at_commit = SecurityCommandStates.CommittedAwaitingProjection,
                correlation_id = command.CorrelationIdentifier,
            }, TimeProvider.System);
        }

        if (plan.ShareExpiry is { } expiry)
        {
            // WP-0 Section 4 "Journal attribution and evidence": attributed to the human command that set the expiry.
            AuditJournal.Append(scope, locked, ShareExpired, new
            {
                security_command_id = command.SecurityCommandId,
                command_kind = command.CommandKindName,
                form_record_id = expiry.FormRecordId,
                record_share_id = expiry.RecordShareId,
                share_expires_on = expiry.ShareExpiresOn,
                evaluated_on = expiry.EvaluatedOn,
                share_revision = expiry.ShareRevision,
                expiry_source_security_command_id = expiry.ExpirySourceSecurityCommandId,
                workload_name = CommandPlanner.ShareExpiryWorkloadName,
                invalidated_descendant_share_ids = plan.InvalidatedDescendantShares.Select(d => d.Metadata.RecordShareId).Order().ToArray(),
                authorization_revision = committedRevision,
                command_state_at_commit = SecurityCommandStates.CommittedAwaitingProjection,
                correlation_id = command.CorrelationIdentifier,
            }, TimeProvider.System);
        }

        if (plan.InvalidatedDescendantShares.Count == 0)
        {
            return;
        }

        if (plan.InvalidationSourceShareId is not { } source)
        {
            throw new InvalidOperationException("an invalidation plan must name the share whose change caused it");
        }

        foreach (var (resource, metadata, reason) in plan.InvalidatedDescendantShares.OrderBy(d => d.Metadata.RecordShareId))
        {
            if (resource.OrganizationId != locked.OrganizationId || metadata.OrganizationId != locked.OrganizationId ||
                resource.ResourceEntityId != metadata.RecordShareId || resource.ParentResourceEntityId is not { } record)
            {
                throw new InvalidOperationException("an invalidated share is not bound to this organization and a record");
            }

            AuditJournal.Append(scope, locked, ShareInvalidated, new
            {
                security_command_id = command.SecurityCommandId,
                command_kind = command.CommandKindName,
                form_record_id = record,
                record_share_id = metadata.RecordShareId,
                upstream_record_share_id = source,
                invalidation_reason = reason,
                authorization_revision = committedRevision,
                command_state_at_commit = SecurityCommandStates.CommittedAwaitingProjection,
                correlation_id = command.CorrelationIdentifier,
            }, TimeProvider.System);
        }
    }
}
