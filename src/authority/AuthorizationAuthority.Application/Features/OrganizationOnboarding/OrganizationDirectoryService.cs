using System.Globalization;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Journal;
using AuthorizationAuthority.Application.Features.PlatformOperators;
using AuthorizationAuthority.Application.Features.SecurityCommands;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.PlatformOperators;
using AuthorizationAuthority.Domain.Features.SecurityCommands;

namespace AuthorizationAuthority.Application.Features.OrganizationOnboarding;

/// <summary>
/// PG-072 Organizations directory (C-104, D8-61 as proposed): organization metadata and counts only — no member list beyond owner
/// identities, no records, documents, teams, shares or audit content. A detail view discloses owner identities, so it is audited on the
/// organization's non-gating audit stream and committed before it is returned (the PF-8 rule).
/// </summary>
public sealed class OrganizationDirectoryService(IAuthorityStore store, PlatformOperatorPolicy platformPolicy, PlatformJournal journal, TimeProvider clock)
{
    private static readonly string[] DetailDisclosure = ["organization_metadata", "owner_identities", "counts"];

    public async Task<OrganizationDirectoryPage> SearchAsync(VerifiedActor actor, OrganizationDirectorySearchRequest input, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        platformPolicy.RequireOperational();
        var filter = string.IsNullOrWhiteSpace(input.Filter) ? OrganizationDirectoryFilters.All : input.Filter.Trim();
        if (!OrganizationDirectoryFilters.Values.Contains(filter))
        {
            throw AuthorityException.Invalid("unknown filter");
        }

        var text = input.Query?.Trim();
        if (text is { Length: > 200 })
        {
            throw AuthorityException.Invalid("the search text is at most 200 characters");
        }

        var limit = input.Limit ?? OrganizationOnboardingService.DefaultPageSize;
        if (limit is < 1 or > OrganizationOnboardingService.MaximumPageSize)
        {
            throw AuthorityException.Invalid($"limit must be 1..{OrganizationOnboardingService.MaximumPageSize}");
        }

        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var head = await scope.ReadPlatformJournalAsync(cancellationToken).ConfigureAwait(false) ?? new PlatformJournalHead();
        await journal.EnsureCurrentAsync(scope, head, requireAnchored: false, correlationId, cancellationToken).ConfigureAwait(false);
        var me = await PlatformOperatorAccess.ResolveActiveAsync(scope, actor, cancellationToken).ConfigureAwait(false);
        var binding = OrganizationOnboardingService.Binding("directory", me, filter, text, limit);
        var rows = (await scope.GetOrganizationDirectoryRowsAsync(cancellationToken).ConfigureAwait(false))
            .Select(r => (Row: r, Key: OrganizationOnboardingService.NormalizeName(r.State.OrganizationDisplayName)))
            .Where(x => filter != OrganizationDirectoryFilters.NeedsOwners || x.Row.EnabledOwnerCount < 2)
            .Where(x => string.IsNullOrEmpty(text) || x.Row.State.OrganizationDisplayName.Contains(text, StringComparison.OrdinalIgnoreCase)
                                                   || (Guid.TryParse(text, out var id) && x.Row.State.OrganizationId == id))
            .OrderBy(x => x.Key, StringComparer.Ordinal).ThenBy(x => x.Row.State.OrganizationId)
            .ToList();
        var total = rows.Count;
        if (input.Cursor is { Length: > 0 } cursor)
        {
            var (key, id) = OrganizationOnboardingService.ReadCursorParts(cursor, binding);
            rows = rows.Where(x => string.CompareOrdinal(x.Key, key) > 0 || (x.Key == key && x.Row.State.OrganizationId.CompareTo(id) > 0)).ToList();
        }

        var page = rows.Take(limit).ToList();
        var items = new List<OrganizationDirectoryItem>();
        foreach (var x in page)
        {
            items.Add(await ItemAsync(scope, x.Row, cancellationToken).ConfigureAwait(false));
        }

        var next = rows.Count > limit ? OrganizationOnboardingService.WriteCursor(page[^1].Key, page[^1].Row.State.OrganizationId, binding) : null;
        return new OrganizationDirectoryPage(items, total, next, limit, filter, correlationId);
    }

    public async Task<OrganizationDirectoryDetail> GetDetailAsync(VerifiedActor actor, Guid organizationId, Guid correlationId, CancellationToken cancellationToken)
    {
        platformPolicy.RequireOperational();
        OrganizationDirectoryDetail detail;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            var head = await scope.ReadPlatformJournalAsync(cancellationToken).ConfigureAwait(false) ?? new PlatformJournalHead();
            await journal.EnsureCurrentAsync(scope, head, requireAnchored: false, correlationId, cancellationToken).ConfigureAwait(false);
            var me = await PlatformOperatorAccess.ResolveActiveAsync(scope, actor, cancellationToken).ConfigureAwait(false);
            var state = await scope.LockOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false)
                        ?? throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false, "no such organization");
            var row = (await scope.GetOrganizationDirectoryRowsAsync(cancellationToken).ConfigureAwait(false)).Single(r => r.State.OrganizationId == organizationId);
            var graph = new DesiredStateGraph(await scope.GetActiveEdgesAsync(organizationId, cancellationToken).ConfigureAwait(false));
            var owners = (await scope.GetIdentityBindingsAsync(organizationId, cancellationToken).ConfigureAwait(false))
                .Where(b => graph.HasOrganizationRole(organizationId, b.AuthorityUserId, OrganizationRoleNames.OrganizationOwner))
                .OrderBy(b => b.IdentitySubjectIdentifier, StringComparer.Ordinal)
                .Select(b => new OrganizationDirectoryOwner(b.AuthorityUserId, b.IdentityIssuerUri, b.IdentitySubjectIdentifier, b.IdentityBindingEnabled)).ToList();
            var approvedBy = new List<string>();
            if (state.OrganizationOnboardingRequestId is { } requestId)
            {
                var operators = (await scope.GetPlatformOperatorsAsync(cancellationToken).ConfigureAwait(false)).ToDictionary(o => o.PlatformOperatorId);
                approvedBy = (await scope.GetOnboardingApprovalsAsync(requestId, cancellationToken).ConfigureAwait(false))
                    .Where(a => a.ApprovalStateName == OnboardingApprovalStates.Consumed)
                    .Select(a => operators.TryGetValue(a.PlatformOperatorId, out var o) ? o.OperatorDisplayName : a.PlatformOperatorId.ToString("D", CultureInfo.InvariantCulture)).ToList();
            }

            detail = new OrganizationDirectoryDetail(await ItemAsync(scope, row, cancellationToken).ConfigureAwait(false), state.AuthorizationRevision, state.ProjectionAppliedRevision, owners, approvedBy, correlationId);
            AuditJournal.Append(scope, state, "PlatformOrganizationDirectoryViewed", new
            {
                platform_operator_id = me.PlatformOperatorId,
                operator_issuer_uri = me.OperatorIssuerUri,
                operator_subject_identifier = me.OperatorSubjectIdentifier,
                disclosed = DetailDisclosure,
                correlation_id = correlationId,
            }, clock);
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false); // fail closed: no committed audit event, no detail
        }

        return detail;
    }

    private static async Task<OrganizationDirectoryItem> ItemAsync(IAuthorityScope scope, OrganizationDirectoryRow row, CancellationToken cancellationToken)
    {
        var state = row.State;
        var lifecycle = "Active";
        if (state.IntegrityQuarantineEnabled)
        {
            lifecycle = "Quarantined";
        }
        else if (state.OrganizationOnboardingRequestId is { } requestId
                 && await scope.FindOnboardingRequestAsync(requestId, lockForUpdate: false, cancellationToken).ConfigureAwait(false) is { InitializedBySecurityCommandId: { } commandId }
                 && await scope.FindCommandAsync(commandId, cancellationToken).ConfigureAwait(false) is { } command
                 && command.CommandStateName != nameof(CommandStatus.Effective))
        {
            lifecycle = "Activating";
        }

        return new OrganizationDirectoryItem(state.OrganizationId, state.OrganizationDisplayName, lifecycle, row.EnabledOwnerCount, row.SecurityAdministratorCount, row.EnabledMemberCount,
            row.EnabledOwnerCount < 2, state.OrganizationOnboardingRequestId);
    }
}
