using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Contracts.Features;

namespace EnterpriseForms.Application.Features.FormRecords.ListRecords;

/// <summary>
/// Quick record lookup on My Records (Phase C6). Which records match is decided by the authority alone:
/// <list type="bullet">
/// <item>an exact record id is one authority <c>Discover</c> check of that id;</item>
/// <item>an exact short reference is matched against the caller's complete discoverable set (every authority discover page
/// at one authorization revision, <see cref="AuthorizedRecordEnumerator"/>; fails closed above its bound), because the
/// reference is derived from the id and stored nowhere.</item>
/// </list>
/// The business database is read only to describe the matches (the same allowlisted, unverified metadata as My Records), never
/// to find them. A record that does not exist and a record the caller may not discover give the same empty answer. Archived
/// and related (child) records are included: the lookup is exact, so the listing's default filters do not apply.
/// Not a field-value search, and no statement about G06 search completeness.
/// </summary>
public sealed class LookupRecordHandler(IBusinessStore store, IAuthorizationAuthorityClient authority)
{
    public const int MaximumMatchesReturned = 25;

    public async Task<RecordLookupResponse> HandleAsync(RequestContext context, RecordLookupQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(query);
        var user = context.ToDelegatedUser();

        IReadOnlyList<Guid> matches;
        long revision;
        if (query.Kind == RecordLookupKind.RecordId)
        {
            var decision = await authority.CheckPermissionAsync(user,
                new PermissionCheckRequest(context.OrganizationId, new ResourceReference(ProtectedResourceKind.FormRecord, query.FormRecordId!.Value), ProtectedAction.Discover),
                cancellationToken).ConfigureAwait(false);
            matches = decision.Allowed ? [query.FormRecordId.Value] : [];
            revision = decision.AuthorizationRevision;
        }
        else
        {
            var authorized = await AuthorizedRecordEnumerator.EnumerateAsync(authority, user, context.OrganizationId, cancellationToken).ConfigureAwait(false);
            matches = query.Match(authorized.FormRecordIds);
            revision = authorized.AuthorizationRevision;
        }

        if (matches.Count == 0)
        {
            return new RecordLookupResponse(query.Normalized, query.Kind.ToString(), [], 0, [], revision);
        }

        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var records = await scope.GetFormRecordsAsync(context.OrganizationId, matches, cancellationToken).ConfigureAwait(false);
        var formNames = (await scope.GetFormDefinitionsAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false)).ToDictionary(d => d.FormDefinitionId, d => d.FormDefinitionName);
        var userNames = (await scope.GetUsersAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false)).ToDictionary(u => u.UserId, u => u.DisplayName);

        // Only rows for matched ids are described; an authorized match without business metadata is reported, not dropped.
        var described = records.Where(r => matches.Contains(r.FormRecordId)).OrderByDescending(r => r.ModifiedOn).ThenByDescending(r => r.FormRecordId).Take(MaximumMatchesReturned).ToList();
        var describedIds = records.Select(r => r.FormRecordId).ToHashSet();
        var unavailable = matches.Where(id => !describedIds.Contains(id)).Take(MaximumMatchesReturned).ToList();

        var editable = new HashSet<Guid>();
        if (described.Count > 0)
        {
            var decisions = await authority.CheckPermissionsBatchAsync(user,
                described.Select(r => new PermissionCheckRequest(context.OrganizationId, new ResourceReference(ProtectedResourceKind.FormRecord, r.FormRecordId), ProtectedAction.Edit)).ToList(),
                cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < described.Count; i++)
            {
                if (decisions[i].Allowed)
                {
                    editable.Add(described[i].FormRecordId);
                }
            }
        }

        var items = described.Select(r => new RecordListItemResponse(
                r.FormRecordId, r.FormDefinitionId, formNames.GetValueOrDefault(r.FormDefinitionId, string.Empty), r.RecordStatus, r.CreatedOn, r.ModifiedOn, r.ParentFormRecordId, false,
                RecordListQuery.ReferenceFor(r.FormRecordId), userNames.GetValueOrDefault(r.CreatedByUserId), r.CreatedByUserId == context.LocalUserId, editable.Contains(r.FormRecordId)))
            .ToList();
        return new RecordLookupResponse(query.Normalized, query.Kind.ToString(), items, matches.Count, unavailable, revision);
    }
}
