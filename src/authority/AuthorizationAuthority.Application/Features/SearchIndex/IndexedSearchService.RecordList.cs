using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.SecurityCommands;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.FieldSearch;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SearchIndex;

namespace AuthorizationAuthority.Application.Features.SearchIndex;

/// <summary>
/// IX-6 (IX-D12; design R1 §E.6): My Records on verified index entries. The same machinery as the Tier 1 search, without field predicates:
/// <list type="number">
/// <item>the organization gate and S1 (423 when quarantined), then the search projection (servable state, §17.10 deadlines);</item>
/// <item>the caller's discoverable records <c>D</c> from SpiceDB <c>LookupResources(discover)</c> at one S1 pin, exactly as today;</item>
/// <item>in one read-only reader snapshot: every registered record of <c>D</c>, with its accepted head and its index entry (any form). The
/// scope guard (I-1): every contentful record must have a current entry with verified header facts; otherwise the listing refuses with
/// <c>RECORD_LIST_INDEX_INCOMPLETE</c> (503, retryable) and the business API lists exactly as before (the IX-D8 pattern; one listing never
/// combines both);</item>
/// <item>the metadata filters (form, status, children, archived) and the sort (last change, created, status) run on the entries'
/// <b>verified header facts</b> and the Authority-held times, so the G06 caveat of the business listing (unverified business metadata)
/// does not apply to them; exact counts;</item>
/// <item>every listed record's entry is bound to its accepted manifest (I-2), the page's seals are verified, and edit flags come from SpiceDB
/// at the pin; the S1 pin must complete; a page request at another authorization revision is refused (the client restarts).</item>
/// </list>
/// Paging is by offset over the Authority's deterministic order at the cursor's authorization revision (nothing is stored). Form names and
/// the form-name sort, and the record-reference lookup (<c>q</c>), stay on the business path (cosmetic, or an exact lookup); they are never
/// sent here. Records registered without accepted content are reported by id (they have no header facts), not listed.
/// </summary>
public sealed partial class IndexedSearchService
{
    public const string RecordListResultBasis = "AuthorityVerifiedEntries";

    private static readonly string[] RecordListSortFields = ["modifiedOn", "createdOn", "recordStatus"];

    public bool RecordListAvailable => Available && options.RecordListEnabled;

    public async Task<IndexedRecordListResponse> ListRecordsAsync(VerifiedActor actor, IndexedRecordListRequest request, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(request);
        if (!RecordListAvailable)
        {
            throw new AuthorityException(IndexedSearchErrorCodes.IndexNotReady, 503, true, "the indexed record listing is not enabled");
        }

        var sort = string.IsNullOrEmpty(request.SortBy) ? "modifiedOn" : request.SortBy;
        if (!RecordListSortFields.Contains(sort, StringComparer.Ordinal) || request.SortDirection is not ("asc" or "desc") || request.PageSize is < 1 or > 100 || request.Offset < 0
            || (request.RecordStatus is not null && !RecordStatuses.Contains(request.RecordStatus, StringComparer.Ordinal)))
        {
            throw AuthorityException.Invalid("record list request out of range");
        }

        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var organizationId = request.OrganizationId;
        var state = await scope.ReadOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false) ?? throw AuthorityException.NotRegistered("organization is not registered");
        var bound = await ActorResolver.BindAsync(scope, organizationId, actor, cancellationToken).ConfigureAwait(false);
        OrganizationGate.RequireOpen(await OrganizationGate.EvaluateAsync(scope, state, cancellationToken).ConfigureAwait(false));
        await RequireServableProjectionAsync(scope, organizationId, cancellationToken).ConfigureAwait(false);
        if (request.ExpectedAuthorizationRevision is { } expected && expected != state.AuthorizationRevision)
        {
            throw Changed("authorization changed since the first page");
        }

        var pin = await verifier.PinAsync(scope, state, organizationLocked: false, correlationId, cancellationToken).ConfigureAwait(false);
        var subject = bound.AuthorityUserId.ToString("D");
        IReadOnlyList<string> discoverable;
        try
        {
            discoverable = await verifier.LookupResourcesAsync(pin, ResourceTypeNames.FormRecord, "discover", ResourceTypeNames.User, subject, Authorization.PermissionCheckService.MaximumDiscoverableRecords, cancellationToken).ConfigureAwait(false);
        }
        catch (SpiceDbLookupLimitExceededException)
        {
            throw Limit("discoverable");
        }

        List<(Guid Id, Guid Form, DerivedHeaderFacts Header, DateTimeOffset Created, DateTimeOffset Modified)> listed = [];
        List<Guid> withoutContent = [];
        List<Guid> availableForms = [];
        List<IndexedSearchItem> page = [];
        var contradiction = false;
        try
        {
            await using (var read = await reader.BeginAsync(organizationId, cancellationToken).ConfigureAwait(false))
            {
                var facts = await read.ReadListingScopeAsync(Ids(discoverable), cancellationToken).ConfigureAwait(false);
                withoutContent = facts.Where(f => f.Record.CommittedContentRevision == 0).Select(f => f.Record.FormRecordId).Order().ToList();
                var contentful = facts.Where(f => f.Record.CommittedContentRevision > 0).ToList();

                // I-1 for the listing: every contentful discoverable record has a current entry with verified header facts, else no listing.
                if (contentful.Any(f => f.Record.ContentHeadQuarantined || f.Record.CommittedOperationId is null || f.Record.EntryIndexedOperationId != f.Record.CommittedOperationId
                                        || f.Record.EntryDerivationVersion is null || f.Record.EntryDerivationVersion < SearchPostingDerivationV1.HeaderFactsEntryDerivationVersion || f.Record.EntryRecordStatusName is null))
                {
                    throw new AuthorityException(IndexedSearchErrorCodes.RecordListIndexIncomplete, 503, true, "not every listed record has a current verified index entry");
                }

                var scoped = contentful.Where(f => request.IncludeChildren || f.Record.EntryParentFormRecordId is null).ToList();
                availableForms = scoped.Select(f => f.FormDefinitionId).Distinct().Order().ToList();
                var filter = new IndexedSearchQuery(Guid.Empty, [], request.RecordStatus, request.IncludeChildren, request.IncludeArchived, sort, request.SortDirection, request.PageSize);
                var candidates = scoped.Where(f => request.FormDefinitionId is null || f.FormDefinitionId == request.FormDefinitionId)
                    .Select(f => (f.Record.FormRecordId, f.FormDefinitionId, Header: new DerivedHeaderFacts(f.FormDefinitionId, f.Record.EntryRecordStatusName!, f.Record.EntryParentFormRecordId, f.Record.EntryCreatedByBusinessUserId ?? Guid.Empty),
                        Created: f.Record.RecordRegisteredOn, Modified: f.Record.ContentHeadModifiedOn ?? f.Record.RecordRegisteredOn))
                    .Where(c => MetadataMatches(filter, c.Header))
                    .ToList();
                if (candidates.Count > options.MaximumResults)
                {
                    throw Limit("results");
                }

                // I-2: every listed record's entry is bound to its accepted manifest; its decoded header must be the one filtered on.
                var bindings = (await read.ReadBindingDataAsync(candidates.Select(c => c.FormRecordId).ToList(), [], cancellationToken).ConfigureAwait(false)).ToDictionary(b => b.Entry.FormRecordId);
                foreach (var c in candidates)
                {
                    var verified = bindings.TryGetValue(c.FormRecordId, out var binding) ? IndexedSearchBinding.VerifyMatch(binding, [], new Dictionary<Guid, string>()) : new IndexedMatchVerification(false, "MATCH_BINDING_DATA_MISSING", null);
                    if (!verified.Bound || verified.Header != c.Header)
                    {
                        throw await QuarantineIndexAsync(organizationId, verified.FindingCode ?? "LIST_HEADER_MISMATCH", cancellationToken).ConfigureAwait(false);
                    }
                }

                var forms = candidates.ToDictionary(c => c.FormRecordId, c => c.FormDefinitionId);
                listed = Sort(candidates.Select(c => (c.FormRecordId, c.Header, c.Created, c.Modified)).ToList(), sort, request.SortDirection)
                    .Select(m => (m.Id, forms[m.Id], m.Header, m.Created, m.Modified)).ToList();
                var offset = Math.Min(request.Offset, listed.Count);
                var pageIds = listed.Skip(offset).Take(request.PageSize).Select(m => m.Id).ToList();
                // The page's seals cover each entry's derived approvals: the approvals of the page's forms, read at this reader snapshot,
                // whose state must be the pinned one (the search's consistency rule: same authorization revision, servable projection at
                // the content revision).
                var approvals = new List<FieldSearchApproval>();
                foreach (var form in pageIds.Select(id => forms[id]).Distinct())
                {
                    var formState = await read.ReadStateAsync(form, cancellationToken).ConfigureAwait(false);
                    await RequireConsistentStateAsync(organizationId, formState, state, cancellationToken).ConfigureAwait(false);
                    approvals.AddRange(formState.Approvals);
                }

                page = await VerifyPageAsync(read, pin, subject, organizationId, pageIds, [], new Dictionary<Guid, string>(), approvals, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (ProjectionContradictionException)
        {
            contradiction = true; // classified only after this pin's single completion (as the search does, C-142)
        }

        if (!await verifier.CompleteAsync(pin, "indexed-record-list", cancellationToken).ConfigureAwait(false))
        {
            throw Changed("authorization state moved during the listing");
        }

        if (contradiction)
        {
            throw await QuarantineIndexAsync(organizationId, ProjectionContradictionException.FindingCode, cancellationToken).ConfigureAwait(false);
        }

        var byId = listed.ToDictionary(m => m.Id);
        var items = page.Select(p => byId[p.FormRecordId]).Select((m, i) => new IndexedRecordListItem(m.Id, m.Form, m.Header.RecordStatusName, m.Header.ParentFormRecordId, m.Header.CreatedByUserId, m.Created, m.Modified, page[i].Editable)).ToList();
        return new IndexedRecordListResponse(state.AuthorizationRevision, listed.Count, Math.Min(request.Offset, listed.Count), request.PageSize, items, availableForms, withoutContent.Count, withoutContent.Take(50).ToList(), RecordListResultBasis, correlationId);
    }
}
