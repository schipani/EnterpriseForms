using System.Security.Cryptography;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Journal;
using AuthorizationAuthority.Application.Features.SecurityCommands;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.FieldSearch;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.Sessions;
using AuthorizationAuthority.Domain.Features.SearchIndex;

namespace AuthorizationAuthority.Application.Features.SearchIndex;

/// <summary>
/// IX-3 (design §8, §17.2, §17.3, §17.9.2, §18.1.4, §18.4; R1 §E): Tier 1 indexed field search, in the Authority.
/// <para>
/// <b>Search:</b> bind the actor and the session → organization gate and S1 (Q1 step 1: 423 when quarantined) → projection state
/// (Q1 step 2) → S1 pin → predicate authorization at the pin (query_fields, policy, Active approvals, operators, frozen options,
/// coverage Complete) → population D (discover) and O (read) at the pin → in ONE read-only REPEATABLE READ reader transaction:
/// scope guard I-1 over D ∩ form, value-independent field_item checks for share-only records, the scope-driven candidate
/// statement, I-2 on every match → sort (V1 comparator) → seal verification of the first page's records → edit flags → S1
/// verify → under the organization lock: revisions, projection, form state and session re-checked, eviction, capacity, the
/// result snapshot and its execution evidence committed together. Nothing is released without evidence.
/// </para>
/// <para>
/// <b>Page:</b> every check of §17.3 again (snapshot, user, session, digest; gate and S1; authorization revision; approvals,
/// operators, coverage and query_fields; projection and form state; a fresh SpiceDB decision for each page record; I-2 and the
/// seal; edit flags; S1 verify), then a short transaction that locks the snapshot row and re-checks the database facts. The lock
/// is never held across SpiceDB or S1 calls (D-IX3-2). Any failure returns no page; non-retryable failures delete the snapshot.
/// </para>
/// Values are never stored, logged or journaled. A search-side integrity failure quarantines the search index only (IX-D7),
/// never the organization; authorization damage is S1's (Q4, raised by the S1 verifier itself).
/// </summary>
public sealed partial class IndexedSearchService(
    IAuthorityStore store,
    ISearchQueryReader reader,
    ProjectionIntegrityVerifier verifier,
    SearchSealVerifier sealVerifier,
    SearchIndexSweepService sweep,
    SearchIndexOptions indexOptions,
    IndexedSearchOptions options,
    ISecurityAlertSink alerts,
    TimeProvider clock,
    IIndexedSearchProbe? probe = null,
    IIndexedSearchShadowSink? shadow = null,
    SecurityCommands.FieldSearchApprovalGate? approvalGate = null)
{
    /// <summary>C-188: retained-field approvals are honoured only when enabled (absent gate: disabled, fail closed).</summary>
    private readonly bool retainedFieldApprovalsEnabled = approvalGate?.RetainedFieldApprovalsEnabled ?? false;

    public const string Engine = "Indexed";

    private static readonly string[] RecordStatuses = ["Draft", "Submitted", "Archived"];
    private static readonly string[] SortFields = ["modifiedOn", "createdOn", "formName", "recordStatus"];
    private static readonly string[] SortDirections = ["asc", "desc"];

    public static string InstanceName { get; } = Environment.MachineName;

    public bool Available => options.QueryEnabled && indexOptions.Mode == SearchIndexMode.Dark && reader.IsConfigured;

    public async Task<IndexedSearchResponse> SearchAsync(VerifiedActor actor, IndexedSearchRequest request, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        RequireAvailable();
        var (query, conditions, nonce, sessionDigest) = Validate(request?.Query, request?.QueryNonceBase64, request?.SessionDigestBase64);
        var organizationId = request!.OrganizationId;
        var started = clock.GetTimestamp();
        var fields = conditions.Select(c => c.FieldDefinitionId).Distinct().ToList();
        Guid? userId = null;
        try
        {
            await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
            var state = await scope.ReadOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false) ?? throw AuthorityException.NotRegistered("organization is not registered");
            var bound = await ActorResolver.BindAsync(scope, organizationId, actor, cancellationToken).ConfigureAwait(false);
            userId = bound.AuthorityUserId;
            var session = await RequireSessionAsync(scope, organizationId, sessionDigest, bound, cancellationToken).ConfigureAwait(false);

            // Q1 step 1: organization gate and S1 (423 INTEGRITY_QUARANTINE, never reached by any search-index state).
            OrganizationGate.RequireOpen(await OrganizationGate.EvaluateAsync(scope, state, cancellationToken).ConfigureAwait(false));
            // Q1 step 2: the search projection (its state and the §17.10 deadlines, recomputed read-only).
            await RequireServableProjectionAsync(scope, organizationId, cancellationToken).ConfigureAwait(false);

            var pin = await verifier.PinAsync(scope, state, organizationLocked: false, correlationId, cancellationToken).ConfigureAwait(false);
            var subject = bound.AuthorityUserId.ToString("D");
            var mayQuery = (await verifier.CheckBulkStrictAsync(pin, [new SpiceDbCheck(ResourceTypeNames.FormDefinition, query.FormDefinitionId.ToString("D"), "query_fields", ResourceTypeNames.User, subject)], cancellationToken).ConfigureAwait(false))[0] == SpiceDbCheckOutcome.Allowed;
            var policy = await scope.FindProtectionPolicyAsync(organizationId, cancellationToken).ConfigureAwait(false);

            IReadOnlyList<string> discoverable;
            IReadOnlyList<string> owned;
            try
            {
                discoverable = await verifier.LookupResourcesAsync(pin, ResourceTypeNames.FormRecord, "discover", ResourceTypeNames.User, subject, Authorization.PermissionCheckService.MaximumDiscoverableRecords, cancellationToken).ConfigureAwait(false);
                owned = await verifier.LookupResourcesAsync(pin, ResourceTypeNames.FormRecord, "read", ResourceTypeNames.User, subject, Authorization.PermissionCheckService.MaximumDiscoverableRecords, cancellationToken).ConfigureAwait(false);
            }
            catch (SpiceDbLookupLimitExceededException)
            {
                throw Limit("discoverable");
            }

            var discoverableIds = Ids(discoverable);
            var ownedIds = Ids(owned).ToHashSet();
            // Assigned in the reader block; read only after it completed without a projection contradiction (which throws below).
            SearchQueryStateFacts facts = null!;
            List<(Guid Id, DerivedHeaderFacts Header, DateTimeOffset Created, DateTimeOffset Modified)> matches = [];
            Dictionary<Guid, Guid> operations = [];
            int scopeCount = 0, withoutContent = 0;
            List<IndexedSearchItem> firstPage = [];
            Guid[] approvalIds = [];
            var contradiction = false;
            try
            {
                await using (var read = await reader.BeginAsync(organizationId, cancellationToken).ConfigureAwait(false))
                {
                    facts = await read.ReadStateAsync(query.FormDefinitionId, cancellationToken).ConfigureAwait(false);
                    await RequireConsistentStateAsync(organizationId, facts, state, cancellationToken).ConfigureAwait(false);
                    var ineffective = await SecurityCommands.RetainedFieldApprovals.IneffectiveAsync(scope, organizationId, facts.Approvals, policy, retainedFieldApprovalsEnabled, cancellationToken).ConfigureAwait(false);
                    approvalIds = AuthorizePredicates(conditions, facts, mayQuery, policy?.PlaintextSearchPermitted ?? false, ineffective);

                    var scopeFacts = await read.ReadScopeAsync(query.FormDefinitionId, discoverableIds, fields, cancellationToken).ConfigureAwait(false);
                    await GuardScopeAsync(organizationId, scopeFacts, cancellationToken).ConfigureAwait(false);
                    await RequireNoDerivationDowngradeAsync(scope, organizationId, query.FormDefinitionId, scopeFacts, "SCOPE_", cancellationToken).ConfigureAwait(false);
                    var contentful = scopeFacts.Where(r => r.CommittedContentRevision > 0).ToList();
                    scopeCount = scopeFacts.Count;
                    withoutContent = scopeFacts.Count - contentful.Count;

                    // R1 §E.3: the readable-for-predicate scope. Owned records: every item readable. Share-only records: every indexed item
                    // of every predicate field must be readable. C-141 (UF-25, U-1b): decided from the caller's own grants and the accepted
                    // per-field item counts, so no item the caller cannot read is enumerated, checked or counted against the limit.
                    var predicateFields = PredicateFields(conditions, approvalIds);
                    var approvalByField = ApprovalByField(predicateFields);
                    var shareOnly = contentful.Where(r => !ownedIds.Contains(r.FormRecordId)).ToList();
                    var shareDecision = await EvaluateShareOnlyAsync(read, pin, subject, bound.AuthorityUserId, organizationId, shareOnly, predicateFields, enforceLimit: true, "indexed-search", correlationId, cancellationToken).ConfigureAwait(false);
                    var readableScope = contentful.Where(r => ownedIds.Contains(r.FormRecordId) || shareDecision.Evaluable.Contains(r.FormRecordId)).Select(r => r.FormRecordId).ToList();
                    var filter = new SearchMetadataFilter(query.RecordStatus, query.IncludeChildren, query.IncludeArchived);
                    var candidates = await read.FindCandidatesAsync(query.FormDefinitionId, readableScope, conditions, filter, options.MaximumResults + 1, cancellationToken).ConfigureAwait(false);
                    if (candidates.Count > options.MaximumResults)
                    {
                        throw Limit("results");
                    }

                    var valueTypes = conditions.GroupBy(c => c.FieldDefinitionId).ToDictionary(g => g.Key, g => g.First().ValueType);
                    var bindings = (await read.ReadBindingDataAsync(candidates, fields, cancellationToken).ConfigureAwait(false)).ToDictionary(b => b.Entry.FormRecordId);
                    var facts1 = scopeFacts.ToDictionary(r => r.FormRecordId);
                    operations = contentful.ToDictionary(r => r.FormRecordId, r => r.CommittedOperationId!.Value);
                    matches = new(candidates.Count);
                    foreach (var id in candidates)
                    {
                        var verified = bindings.TryGetValue(id, out var binding) ? IndexedSearchBinding.VerifyMatch(binding, conditions, valueTypes) : new IndexedMatchVerification(false, "MATCH_BINDING_DATA_MISSING", null);
                        if (!verified.Bound || !MetadataMatches(query, verified.Header!))
                        {
                            throw await QuarantineIndexAsync(organizationId, verified.FindingCode ?? "MATCH_METADATA_MISMATCH", cancellationToken).ConfigureAwait(false);
                        }

                        // C-141: a share-only match must have been decided on bound data: its bound predicate postings are exactly its stored
                        // count and every one was confirmed readable. For an honest match these are readable items only.
                        if (!ownedIds.Contains(id) && ShareOnlyReadability.VerifyBoundRecord(binding!, approvalByField, shareDecision.ConfirmedItemsByRecord.GetValueOrDefault(id)) is { } shareFinding)
                        {
                            throw await QuarantineIndexAsync(organizationId, shareFinding, cancellationToken).ConfigureAwait(false);
                        }

                        var record = facts1[id];
                        matches.Add((id, verified.Header!, record.RecordRegisteredOn, record.ContentHeadModifiedOn ?? record.RecordRegisteredOn));
                    }

                    matches = Sort(matches, query.SortBy, query.SortDirection);
                    firstPage = await VerifyPageAsync(read, pin, subject, organizationId, matches.Take(query.PageSize).Select(m => m.Id).ToList(), conditions, valueTypes, facts.Approvals, cancellationToken).ConfigureAwait(false);
                    var headers = matches.ToDictionary(m => m.Id);
                    firstPage = firstPage.Select(item => item with
                    {
                        RecordStatusName = headers[item.FormRecordId].Header.RecordStatusName,
                        ParentFormRecordId = headers[item.FormRecordId].Header.ParentFormRecordId,
                        CreatedByBusinessUserId = headers[item.FormRecordId].Header.CreatedByUserId,
                        RecordRegisteredOn = headers[item.FormRecordId].Created,
                        ContentHeadModifiedOn = headers[item.FormRecordId].Modified,
                    }).ToList();
                }
            }
            catch (ProjectionContradictionException)
            {
                contradiction = true; // C-142: classified only after this pin's single completion below (Q4)
            }

            if (!await verifier.CompleteAsync(pin, "indexed-search", cancellationToken).ConfigureAwait(false))
            {
                throw Changed("authorization state moved during the search");
            }

            if (contradiction)
            {
                throw await QuarantineIndexAsync(organizationId, ProjectionContradictionException.FindingCode, cancellationToken).ConfigureAwait(false);
            }

            // Under the organization lock: nothing the result depends on may have moved; then the snapshot and its evidence.
            var locked = await scope.LockOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false);
            if (locked is null || locked.IntegrityQuarantineEnabled)
            {
                throw AuthorityException.Quarantined("organization quarantined");
            }

            if (locked.AuthorizationRevision != pin.ExpectedRevision || locked.ContentIntegrityRevision != facts.ContentIntegrityRevision)
            {
                throw Changed("authorization or content changed during the search");
            }

            await RequireUnchangedIndexStateAsync(scope, organizationId, query.FormDefinitionId, facts, cancellationToken).ConfigureAwait(false);
            await RequireSessionAsync(scope, organizationId, sessionDigest, bound, cancellationToken).ConfigureAwait(false);
            var now = clock.GetUtcNow();
            if (await scope.SearchIndex.CountLiveResultEntriesAsync(InstanceName, now, cancellationToken).ConfigureAwait(false) + matches.Count > options.MaximumLiveEntriesPerInstance)
            {
                throw new AuthorityException(IndexedSearchErrorCodes.CapacityExceeded, 503, true, "stored search results are at capacity; try again shortly");
            }

            await scope.SearchIndex.EvictSessionSnapshotsAsync(session.AuthenticatedSessionId, options.MaximumSnapshotsPerSession - 1, cancellationToken).ConfigureAwait(false);
            var snapshot = new SearchResultSnapshot
            {
                SearchResultSnapshotId = Guid.CreateVersion7(),
                OrganizationId = organizationId,
                RequestingAuthorityUserId = bound.AuthorityUserId,
                AuthenticatedSessionId = session.AuthenticatedSessionId,
                FormDefinitionId = query.FormDefinitionId,
                SnapshotAuthorizationRevision = locked.AuthorizationRevision,
                SnapshotContentIntegrityRevision = locked.ContentIntegrityRevision,
                SnapshotFormContentRevision = facts.FormContentRevision,
                PredicateFieldDefinitionIds = conditions.Select(c => c.FieldDefinitionId).ToArray(),
                PredicateFieldSearchApprovalIds = approvalIds,
                PredicateOperatorNames = conditions.Select(c => c.Operator).ToArray(),
                QueryDigest = QueryDigest(nonce, query),
                ResultEntryCount = matches.Count,
                SnapshotCreatedOn = now,
                SnapshotExpiresOn = now.AddSeconds(options.SnapshotLifetimeSeconds),
                CreatingInstanceName = InstanceName,
            };
            var entries = matches.Select((m, position) => new SearchResultSnapshotEntry
            {
                SearchResultSnapshotEntryId = Guid.CreateVersion7(),
                SearchResultSnapshotId = snapshot.SearchResultSnapshotId,
                OrganizationId = organizationId,
                ResultEntryPosition = position,
                FormRecordId = m.Id,
                IndexedOperationId = operations[m.Id],
            }).ToList();
            scope.SearchIndex.AddResultSnapshot(snapshot, entries);
            AuditJournal.Append(scope, locked, "FieldSearchExecuted", Evidence(snapshot.SearchResultSnapshotId, query.FormDefinitionId, bound.AuthorityUserId, conditions, "COMPLETED", scopeCount - withoutContent, matches.Count, started, locked.AuthorizationRevision, correlationId), clock);
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            // C-150 IX-5c shadow preparation: after the commit, hand the committed answer to the off-path comparison (when enabled). It
            // never changes this response, its status or any decision; a full queue or a failing sink is ignored.
            OfferShadow(new IndexedSearchShadowObservation(organizationId, query.FormDefinitionId, bound.AuthorityUserId, conditions, approvalIds, query.RecordStatus, query.IncludeChildren, query.IncludeArchived,
                matches.Select(m => m.Id).ToList(), locked.AuthorizationRevision, locked.ContentIntegrityRevision, facts.FormContentRevision, correlationId));
            return new IndexedSearchResponse(snapshot.SearchResultSnapshotId, query.FormDefinitionId, matches.Count, 0, query.PageSize, firstPage, scopeCount, withoutContent,
                locked.AuthorizationRevision, locked.ContentIntegrityRevision, facts.FormContentRevision, snapshot.SnapshotExpiresOn, IndexedSearchErrorCodes.ResultBasisAuthorityAcceptedContent, correlationId);
        }
        catch (SearchQueryTimeoutException)
        {
            await JournalFailureAsync(organizationId, query.FormDefinitionId, userId, conditions, IndexedSearchErrorCodes.VerificationLimit, started, correlationId, cancellationToken).ConfigureAwait(false);
            throw Limit("time");
        }
        catch (AuthorityException ex) when (userId is not null && ex.ErrorCode != AuthorityErrorCodes.IntegrityQuarantine)
        {
            // OD-8 parity: a failed search's evidence is best effort; the failure is returned regardless.
            await JournalFailureAsync(organizationId, query.FormDefinitionId, userId, conditions, ex.ErrorCode, started, correlationId, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IndexedSearchResponse> PageAsync(VerifiedActor actor, Guid searchResultSnapshotId, IndexedSearchPageRequest request, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        RequireAvailable();
        var (query, conditions, nonce, sessionDigest) = Validate(request?.Query, request?.QueryNonceBase64, request?.SessionDigestBase64);
        var organizationId = request!.OrganizationId;
        if (request.Offset < 0 || request.Offset > options.MaximumResults)
        {
            throw AuthorityException.Invalid($"offset must be 0..{options.MaximumResults}");
        }

        SearchResultSnapshot snapshot;
        List<IndexedSearchItem> page = [];
        Guid sessionId;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            var state = await scope.ReadOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false) ?? throw AuthorityException.NotRegistered("organization is not registered");
            var bound = await ActorResolver.BindAsync(scope, organizationId, actor, cancellationToken).ConfigureAwait(false);
            // Check 2: the snapshot exists, is not expired and belongs to this user, session, organization and query.
            var session = await RequireSessionAsync(scope, organizationId, sessionDigest, bound, cancellationToken).ConfigureAwait(false);
            sessionId = session.AuthenticatedSessionId;
            // Check 3 first (Q1): the organization gate and S1 answer before anything about the snapshot (423 even after the purge).
            OrganizationGate.RequireOpen(await OrganizationGate.EvaluateAsync(scope, state, cancellationToken).ConfigureAwait(false));
            snapshot = await scope.SearchIndex.FindResultSnapshotAsync(searchResultSnapshotId, cancellationToken).ConfigureAwait(false) ?? throw NotFound();
            if (snapshot.OrganizationId != organizationId || snapshot.RequestingAuthorityUserId != bound.AuthorityUserId || snapshot.AuthenticatedSessionId != session.AuthenticatedSessionId
                || snapshot.FormDefinitionId != query.FormDefinitionId || !CryptographicOperations.FixedTimeEquals(snapshot.QueryDigest, QueryDigest(nonce, query)))
            {
                throw NotFound();
            }

            if (snapshot.SnapshotExpiresOn <= clock.GetUtcNow())
            {
                throw await InvalidateAsync(snapshot.SearchResultSnapshotId, Changed("the search result expired; run the search again"), cancellationToken).ConfigureAwait(false);
            }

            // Check 4: OD-5, any authorization change invalidates.
            if (state.AuthorizationRevision != snapshot.SnapshotAuthorizationRevision)
            {
                throw await InvalidateAsync(snapshot.SearchResultSnapshotId, Changed("access changed since the search"), cancellationToken).ConfigureAwait(false);
            }

            await RequireServableProjectionAsync(scope, organizationId, cancellationToken, snapshot.SearchResultSnapshotId).ConfigureAwait(false);
            var pin = await verifier.PinAsync(scope, state, organizationLocked: false, correlationId, cancellationToken).ConfigureAwait(false);
            var subject = bound.AuthorityUserId.ToString("D");
            var mayQuery = (await verifier.CheckBulkStrictAsync(pin, [new SpiceDbCheck(ResourceTypeNames.FormDefinition, query.FormDefinitionId.ToString("D"), "query_fields", ResourceTypeNames.User, subject)], cancellationToken).ConfigureAwait(false))[0] == SpiceDbCheckOutcome.Allowed;
            var policy = await scope.FindProtectionPolicyAsync(organizationId, cancellationToken).ConfigureAwait(false);
            var positions = await scope.SearchIndex.GetResultSnapshotEntriesAsync(snapshot.SearchResultSnapshotId, request.Offset, query.PageSize, cancellationToken).ConfigureAwait(false);
            var contradiction = false;
            try
            {
                await using (var read = await reader.BeginAsync(organizationId, cancellationToken).ConfigureAwait(false))
                {
                    var facts = await read.ReadStateAsync(query.FormDefinitionId, cancellationToken).ConfigureAwait(false);
                    await RequireConsistentStateAsync(organizationId, facts, state, cancellationToken).ConfigureAwait(false);
                    // Check 5: searchability revalidated explicitly (approval ids still Active, operators, coverage, query_fields).
                    Guid[] approvals;
                    try
                    {
                        var ineffective = await SecurityCommands.RetainedFieldApprovals.IneffectiveAsync(scope, organizationId, facts.Approvals, policy, retainedFieldApprovalsEnabled, cancellationToken).ConfigureAwait(false);
                        approvals = AuthorizePredicates(conditions, facts, mayQuery, policy?.PlaintextSearchPermitted ?? false, ineffective);
                    }
                    catch (AuthorityException ex) when (ex.ErrorCode is IndexedSearchErrorCodes.FieldNotAllowed or IndexedSearchErrorCodes.OperatorNotSupported or IndexedSearchErrorCodes.IndexNotReady)
                    {
                        throw await InvalidateAsync(snapshot.SearchResultSnapshotId, Changed("searchability changed since the search"), cancellationToken).ConfigureAwait(false);
                    }

                    if (!approvals.SequenceEqual(snapshot.PredicateFieldSearchApprovalIds) || !conditions.Select(c => c.Operator).SequenceEqual(snapshot.PredicateOperatorNames, StringComparer.Ordinal))
                    {
                        throw await InvalidateAsync(snapshot.SearchResultSnapshotId, Changed("searchability changed since the search"), cancellationToken).ConfigureAwait(false);
                    }

                    // Check 6: projection revision and form state.
                    if (facts.FormContentRevision != snapshot.SnapshotFormContentRevision)
                    {
                        throw await InvalidateAsync(snapshot.SearchResultSnapshotId, Changed("records of the form changed since the search"), cancellationToken).ConfigureAwait(false);
                    }

                    // Checks 7-9: fresh SpiceDB decisions, I-2 and the seal on the page's records, edit flags.
                    var ids = positions.Select(p => p.FormRecordId).ToList();
                    var valueTypes = conditions.GroupBy(c => c.FieldDefinitionId).ToDictionary(g => g.Key, g => g.First().ValueType);
                    IReadOnlyList<SpiceDbCheckOutcome> discover = ids.Count == 0 ? [] : await verifier.CheckBulkStrictAsync(pin, ids.Select(id => new SpiceDbCheck(ResourceTypeNames.FormRecord, id.ToString("D"), "discover", ResourceTypeNames.User, subject)).ToList(), cancellationToken).ConfigureAwait(false);
                    IReadOnlyList<SpiceDbCheckOutcome> readAll = ids.Count == 0 ? [] : await verifier.CheckBulkStrictAsync(pin, ids.Select(id => new SpiceDbCheck(ResourceTypeNames.FormRecord, id.ToString("D"), "read", ResourceTypeNames.User, subject)).ToList(), cancellationToken).ConfigureAwait(false);
                    if (discover.Any(o => o != SpiceDbCheckOutcome.Allowed))
                    {
                        throw await InvalidateAsync(snapshot.SearchResultSnapshotId, Changed("access changed since the search"), cancellationToken).ConfigureAwait(false);
                    }

                    // C-141 (UF-25, U-1b): the page's share-only records are re-decided exactly as the search decided them, from the caller's
                    // own grants and the accepted item counts (the same rule; no limit on a page, as before).
                    var shareOnlyIds = ids.Where((_, i) => readAll[i] != SpiceDbCheckOutcome.Allowed).ToList();
                    var times = (await read.ReadScopeAsync(query.FormDefinitionId, ids, [], cancellationToken).ConfigureAwait(false)).ToDictionary(r => r.FormRecordId);
                    if (ids.Any(id => !times.ContainsKey(id)))
                    {
                        throw await InvalidateAsync(snapshot.SearchResultSnapshotId, Changed("records of the form changed since the search"), cancellationToken).ConfigureAwait(false);
                    }

                    await RequireNoDerivationDowngradeAsync(scope, organizationId, query.FormDefinitionId, [.. times.Values], "PAGE_", cancellationToken).ConfigureAwait(false);
                    var predicateFields = PredicateFields(conditions, approvals);
                    var approvalByField = ApprovalByField(predicateFields);
                    ShareOnlyDecision pageDecision;
                    try
                    {
                        pageDecision = await EvaluateShareOnlyAsync(read, pin, subject, bound.AuthorityUserId, organizationId, shareOnlyIds.Select(id => times[id]).ToList(), predicateFields, enforceLimit: false, "indexed-search-page", correlationId, cancellationToken).ConfigureAwait(false);
                    }
                    catch (AuthorityException ex) when (ex.ErrorCode == IndexedSearchErrorCodes.IndexNotReady)
                    {
                        throw await InvalidateAsync(snapshot.SearchResultSnapshotId, ex, cancellationToken).ConfigureAwait(false);
                    }

                    if (shareOnlyIds.Any(id => !pageDecision.Evaluable.Contains(id)))
                    {
                        throw await InvalidateAsync(snapshot.SearchResultSnapshotId, Changed("access changed since the search"), cancellationToken).ConfigureAwait(false);
                    }

                    var bindings = (await read.ReadBindingDataAsync(ids, valueTypes.Keys.ToList(), cancellationToken).ConfigureAwait(false)).ToDictionary(b => b.Entry.FormRecordId);
                    var headers = new Dictionary<Guid, DerivedHeaderFacts>();
                    foreach (var id in ids)
                    {
                        if (bindings.GetValueOrDefault(id) is { HeadQuarantined: true })
                        {
                            throw await InvalidateAsync(snapshot.SearchResultSnapshotId, new AuthorityException(IndexedSearchErrorCodes.RecordQuarantined, 503, false, "a record on this page is quarantined"), cancellationToken).ConfigureAwait(false);
                        }

                        var verified = bindings.TryGetValue(id, out var binding) ? IndexedSearchBinding.VerifyMatch(binding, conditions, valueTypes) : new IndexedMatchVerification(false, "MATCH_BINDING_DATA_MISSING", null);
                        if (!verified.Bound || !MetadataMatches(query, verified.Header!))
                        {
                            throw await QuarantineIndexAsync(organizationId, verified.FindingCode ?? "MATCH_METADATA_MISMATCH", cancellationToken).ConfigureAwait(false);
                        }

                        if (shareOnlyIds.Contains(id) && ShareOnlyReadability.VerifyBoundRecord(binding!, approvalByField, pageDecision.ConfirmedItemsByRecord.GetValueOrDefault(id)) is { } shareFinding)
                        {
                            throw await QuarantineIndexAsync(organizationId, shareFinding, cancellationToken).ConfigureAwait(false);
                        }

                        headers[id] = verified.Header!;
                    }

                    page = await VerifyPageAsync(read, pin, subject, organizationId, ids, conditions, valueTypes, facts.Approvals, cancellationToken).ConfigureAwait(false);
                    page = page.Select(item => item with
                    {
                        RecordStatusName = headers[item.FormRecordId].RecordStatusName,
                        ParentFormRecordId = headers[item.FormRecordId].ParentFormRecordId,
                        CreatedByBusinessUserId = headers[item.FormRecordId].CreatedByUserId,
                        RecordRegisteredOn = times[item.FormRecordId].RecordRegisteredOn,
                        ContentHeadModifiedOn = times[item.FormRecordId].ContentHeadModifiedOn ?? times[item.FormRecordId].RecordRegisteredOn,
                    }).ToList();
                }
            }
            catch (ProjectionContradictionException)
            {
                contradiction = true; // C-142: classified only after this pin's single completion below (Q4)
            }

            if (!await verifier.CompleteAsync(pin, "indexed-search-page", cancellationToken).ConfigureAwait(false))
            {
                throw Changed("authorization state moved during the page");
            }

            if (contradiction)
            {
                throw await QuarantineIndexAsync(organizationId, ProjectionContradictionException.FindingCode, cancellationToken).ConfigureAwait(false);
            }
        }

        // D-IX3-2: a short transaction serializes this page with any deletion of the snapshot (§18.4): the row is locked and every
        // database fact re-checked. The lock is never held across SpiceDB or S1 calls, so it cannot wait on the organization lock.
        await using (var final = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            var locked = await final.SearchIndex.MarkResultSnapshotServedAsync(snapshot.SearchResultSnapshotId, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false) ?? throw Changed("the search result was invalidated");
            var state = await final.ReadOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false);
            var projection = await final.SearchIndex.FindProjectionStateAsync(organizationId, cancellationToken).ConfigureAwait(false);
            var formState = await final.SearchIndex.FindFormStateAsync(organizationId, query.FormDefinitionId, cancellationToken).ConfigureAwait(false);
            var session = await final.FindSessionByDigestAsync(sessionDigest, cancellationToken).ConfigureAwait(false);
            if (state is null || state.IntegrityQuarantineEnabled)
            {
                throw AuthorityException.Quarantined("organization quarantined");
            }

            if (state.AuthorizationRevision != locked.SnapshotAuthorizationRevision || projection is not { ProjectionStatusName: SearchProjectionStatuses.Active, VerificationSuspended: false }
                || (formState?.FormContentRevision ?? 0) != locked.SnapshotFormContentRevision || session is null || session.AuthenticatedSessionId != sessionId || !SessionLive(session))
            {
                throw Changed("the search result was invalidated");
            }

            await final.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return new IndexedSearchResponse(snapshot.SearchResultSnapshotId, query.FormDefinitionId, snapshot.ResultEntryCount, request.Offset, query.PageSize, page, 0, 0,
            snapshot.SnapshotAuthorizationRevision, snapshot.SnapshotContentIntegrityRevision, snapshot.SnapshotFormContentRevision, snapshot.SnapshotExpiresOn, IndexedSearchErrorCodes.ResultBasisAuthorityAcceptedContent, correlationId);
    }

    /// <summary>
    /// Testing only (plan-shape test, design §8.2): the PostgreSQL plan of the candidate statement for a given scope and query, on
    /// the reader connection, without executing it. Mapped only in the Testing environment; synthetic data only.
    /// </summary>
    public async Task<string> ExplainCandidatePlanAsync(Guid organizationId, IReadOnlyList<Guid> scope, IndexedSearchQuery query, CancellationToken cancellationToken, bool analyze = false)
    {
        RequireAvailable();
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);
        var conditions = IndexedSearchPredicates.Prepare(query.Conditions, options);
        await using var read = await reader.BeginAsync(organizationId, cancellationToken).ConfigureAwait(false);
        return await read.ExplainCandidatesAsync(query.FormDefinitionId, scope, conditions, new SearchMetadataFilter(query.RecordStatus, query.IncludeChildren, query.IncludeArchived), options.MaximumResults + 1, analyze, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>§18.4 purge job: expired and invalidated snapshots, bounded batches. Returns the number deleted.</summary>
    public async Task<int> PurgeAsync(CancellationToken cancellationToken)
    {
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var deleted = await scope.SearchIndex.PurgeResultSnapshotsAsync(clock.GetUtcNow(), options.PurgeBatchSize, cancellationToken).ConfigureAwait(false);
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return deleted;
    }

    /// <summary>
    /// The page's records (§18.1.4): every entry's ES256 seal verified with the pinned key (any result other than Valid fails the
    /// whole page; Invalid quarantines the index, an unknown key is a 503 without quarantine), the predicate re-evaluated on the
    /// sealed values, then fresh edit decisions at the pin.
    /// </summary>
    private async Task<List<IndexedSearchItem>> VerifyPageAsync(ISearchQuerySession read, VerifiedProjectionSnapshot pin, string subject, Guid organizationId, List<Guid> ids,
        IReadOnlyList<PreparedIndexedCondition> conditions, IReadOnlyDictionary<Guid, string> valueTypes, IReadOnlyList<FieldSearchApproval> approvals, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var full = (await read.ReadBindingDataAsync(ids, null, cancellationToken).ConfigureAwait(false)).ToDictionary(b => b.Entry.FormRecordId);
        var approvalById = approvals.ToDictionary(a => a.FieldSearchApprovalId);
        foreach (var id in ids)
        {
            if (!full.TryGetValue(id, out var data))
            {
                throw await QuarantineIndexAsync(organizationId, "PAGE_ENTRY_MISSING", cancellationToken).ConfigureAwait(false);
            }

            var leaves = data.Leaves.ToDictionary(l => Convert.ToHexStringLower(l.ManifestItemKey), l => l.CanonicalPayloadDigest, StringComparer.Ordinal);
            var (outcome, code) = SearchEntryVerification.Verify(data.Entry, data.Postings, data.Anomalies, data.HeadOperationId, data.HeadContentRevision, data.HeadManifestRoot, leaves, approvalById, sealVerifier);
            if (outcome == SweepUnitOutcome.Inconclusive)
            {
                throw new AuthorityException(IndexedSearchErrorCodes.IndexNotReady, 503, true, "the seal of a page record cannot be verified now (" + code + ")");
            }

            if (outcome != SweepUnitOutcome.Pass)
            {
                throw await QuarantineIndexAsync(organizationId, "PAGE_" + code, cancellationToken).ConfigureAwait(false);
            }

            // §18.1.4 step 3: the sealed approvals must include the current Active approval of every predicate field.
            foreach (var field in valueTypes.Keys)
            {
                var active = approvals.Where(a => a.FieldDefinitionId == field && a.ApprovalStateName == FieldSearchApprovalStates.Active).Select(a => a.FieldSearchApprovalId).SingleOrDefault();
                if (active == Guid.Empty || !data.Entry.DerivedFieldSearchApprovalIds.Contains(active))
                {
                    throw await QuarantineIndexAsync(organizationId, "PAGE_SEALED_APPROVAL_MISMATCH", cancellationToken).ConfigureAwait(false);
                }
            }

            if (!IndexedSearchBinding.PredicateHoldsOnSealedValues(data, conditions, valueTypes))
            {
                throw await QuarantineIndexAsync(organizationId, "PAGE_PREDICATE_NOT_SATISFIED_BY_SEALED_VALUE", cancellationToken).ConfigureAwait(false);
            }
        }

        var edit = await verifier.CheckBulkStrictAsync(pin, ids.Select(id => new SpiceDbCheck(ResourceTypeNames.FormRecord, id.ToString("D"), "edit", ResourceTypeNames.User, subject)).ToList(), cancellationToken).ConfigureAwait(false);
        return ids.Select((id, i) => new IndexedSearchItem(id, string.Empty, null, Guid.Empty, default, default, edit[i] == SpiceDbCheckOutcome.Allowed)).ToList();
    }

    /// <summary>
    /// C-141 (UF-25, U-1b): the share-only readability step (R1 §E.3 semantics, unchanged). Validates the accepted item counts of
    /// every share-only record, reads the candidates from the caller's own grants, enforces the item limit on those candidates only,
    /// confirms every candidate with SpiceDB at the pin and decides evaluability by distinct-item count equality. Any failure refuses
    /// the whole search with nothing released: a version 2 entry is 503 not ready (retryable); a contradiction is an index finding;
    /// a SpiceDB denial of a candidate is escalated by Q4 (FD-4): the S1 decisive verification first (it quarantines the organization
    /// on a projection mismatch), then, with S1 consistent, a search-index quarantine.
    /// </summary>
    private async Task<ShareOnlyDecision> EvaluateShareOnlyAsync(ISearchQuerySession read, VerifiedProjectionSnapshot pin, string subject, Guid authorityUserId, Guid organizationId,
        IReadOnlyList<SearchScopeRecordFacts> shareOnly, IReadOnlyList<PredicateFieldApproval> predicateFields, bool enforceLimit, string path, Guid correlationId, CancellationToken cancellationToken)
    {
        var approvalByField = ApprovalByField(predicateFields);
        var counts = shareOnly.Select(r => new ShareOnlyRecordCounts(r.FormRecordId, r.EntryIndexedOperationId is null ? null : r.EntryDerivationVersion, r.EntryDerivedFieldSearchApprovalIds, r.EntryDerivedFieldItemCounts)).ToList();
        if (counts.Count == 0)
        {
            probe?.ShareOnlyStep(correlationId, new ShareOnlyProbeObservation(path, 0, 0, 0, 0, "DECIDED"));
            return ShareOnlyReadability.Decide(counts, approvalByField, []);
        }

        if (ShareOnlyReadability.ValidateCounts(counts, approvalByField) is { } refused)
        {
            probe?.ShareOnlyStep(correlationId, new ShareOnlyProbeObservation(path, counts.Count, 0, 0, 0, refused.FindingCode!));
            throw refused.Outcome == ShareOnlyOutcome.NotReady
                ? NotReady("the search index is being upgraded; try again shortly")
                : await QuarantineIndexAsync(organizationId, refused.FindingCode!, cancellationToken).ConfigureAwait(false);
        }

        var candidates = await read.ReadShareReadableItemsAsync(authorityUserId, counts.Select(c => c.FormRecordId).ToList(), predicateFields, options.MaximumShareOnlyItems, cancellationToken).ConfigureAwait(false);
        if (enforceLimit && candidates.Count > options.MaximumShareOnlyItems)
        {
            probe?.ShareOnlyStep(correlationId, new ShareOnlyProbeObservation(path, counts.Count, candidates.Count, 0, 0, "LIMIT_ITEMS"));
            throw Limit("items");
        }

        var checks = 0;
        foreach (var chunk in candidates.Chunk(1000))
        {
            var outcomes = await verifier.CheckBulkStrictAsync(pin, chunk.Select(i => new SpiceDbCheck(ResourceTypeNames.FieldItem, i.ManifestItemId.ToString("D"), "read", ResourceTypeNames.User, subject)).ToList(), cancellationToken).ConfigureAwait(false);
            checks += chunk.Length;
            if (outcomes.Any(o => o != SpiceDbCheckOutcome.Allowed))
            {
                probe?.ShareOnlyStep(correlationId, new ShareOnlyProbeObservation(path, counts.Count, candidates.Count, checks, 0, "CANDIDATE_DENIED"));
                // Q4 (§17.2, FD-4): a SpiceDB decision contradicts an access row of the same authorization revision. The caller leaves the
                // reader, runs this pin's one S1 verification (which quarantines the organization on a projection mismatch, or reports a
                // move), and only then, with S1 consistent, quarantines the search index (C-142: one completion per pin).
                throw new ProjectionContradictionException();
            }
        }

        var decision = ShareOnlyReadability.Decide(counts, approvalByField, candidates);
        probe?.ShareOnlyStep(correlationId, new ShareOnlyProbeObservation(path, counts.Count, candidates.Count, checks, decision.Evaluable.Count, decision.FindingCode ?? "DECIDED"));
        if (decision.Outcome != ShareOnlyOutcome.Decided)
        {
            throw await QuarantineIndexAsync(organizationId, decision.FindingCode!, cancellationToken).ConfigureAwait(false);
        }

        return decision;
    }

    /// <summary>
    /// C-142 (R-48): an entry older than the current derivation version in a form that the journal shows was already attested
    /// Complete at the version with item counts cannot be legitimate (a forged downgrade): the index is quarantined, never answered
    /// as "not ready" and never left for an ordinary re-derivation. Genuine legacy entries (no such attestation) continue to the
    /// existing rules (version 1: I-1 finding; version 2: not ready for a share-only decision, upgraded by S-A).
    /// </summary>
    private async Task RequireNoDerivationDowngradeAsync(IAuthorityScope scope, Guid organizationId, Guid formDefinitionId, IReadOnlyList<SearchScopeRecordFacts> records, string prefix, CancellationToken cancellationToken)
    {
        if (!records.Any(r => r.EntryIndexedOperationId is not null && r.EntryDerivationVersion < SearchPostingDerivationV1.CurrentEntryDerivationVersion))
        {
            return;
        }

        var attested = await scope.SearchIndex.GetFormsAttestedAtDerivationVersionAsync(organizationId, SearchPostingDerivationV1.FieldItemCountEntryDerivationVersion, cancellationToken).ConfigureAwait(false);
        if (attested.Contains(formDefinitionId))
        {
            throw await QuarantineIndexAsync(organizationId, prefix + SearchIndexSweepService.DowngradeFinding, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>C-142: raised inside a reader block when SpiceDB denies a granted candidate; classified after the pin's completion (Q4).</summary>
    private sealed class ProjectionContradictionException : Exception
    {
        public const string FindingCode = "SHARE_CANDIDATE_DENIED_BY_PROJECTION";

        public ProjectionContradictionException()
            : base("a SpiceDB decision contradicts the Authority's access rows at the pinned revision")
        {
        }
    }

    /// <summary>The distinct predicate fields with their value type and the condition's current Active approval.</summary>
    private static List<PredicateFieldApproval> PredicateFields(IReadOnlyList<PreparedIndexedCondition> conditions, Guid[] approvalIds) =>
        conditions.Select((c, i) => new PredicateFieldApproval(c.FieldDefinitionId, c.ValueType, approvalIds[i])).DistinctBy(f => f.FieldDefinitionId).ToList();

    private static Dictionary<Guid, Guid> ApprovalByField(IReadOnlyList<PredicateFieldApproval> predicateFields) =>
        predicateFields.ToDictionary(f => f.FieldDefinitionId, f => f.FieldSearchApprovalId);

    /// <summary>I-1 (§6.3, R1 §B.3) over the in-scope set D ∩ form, at the reader snapshot.</summary>
    private async Task GuardScopeAsync(Guid organizationId, IReadOnlyList<SearchScopeRecordFacts> scopeFacts, CancellationToken cancellationToken)
    {
        // D3 parity (and D-IX2-7 rule 1): a quarantined record in scope fails the whole search; the index is not quarantined for it.
        if (scopeFacts.Any(r => r.ContentHeadQuarantined || r.ResourceLifecycleState == ResourceLifecycleStates.Quarantined))
        {
            throw new AuthorityException(IndexedSearchErrorCodes.RecordQuarantined, 503, false, "a record in scope is quarantined");
        }

        if (scopeFacts.Any(r => r.PredicateFieldEncrypted))
        {
            throw new AuthorityException(IndexedSearchErrorCodes.PredicateValueEncrypted, 422, false, "a searched field holds an encrypted value on a record in scope");
        }

        foreach (var record in scopeFacts)
        {
            var contentful = record.CommittedContentRevision > 0;
            string? finding = null;
            if (contentful && record.EntryIndexedOperationId is null)
            {
                finding = "SCOPE_CONTENTFUL_RECORD_WITHOUT_ENTRY";
            }
            else if (contentful && record.EntryIndexedOperationId != record.CommittedOperationId)
            {
                finding = "SCOPE_ENTRY_NOT_AT_HEAD";
            }
            else if (contentful && record.EntryDerivationVersion < SearchPostingDerivationV1.HeaderFactsEntryDerivationVersion)
            {
                // Version 2 entries (no item counts, C-141) are not a finding here: only a share-only decision needs the counts, and it
                // refuses them as not ready (ShareOnlyReadability.ValidateCounts) until the derivation upgrade has re-derived them.
                finding = "SCOPE_ENTRY_DERIVATION_OUTDATED";
            }
            else if (!contentful && record.EntryIndexedOperationId is not null)
            {
                finding = "SCOPE_ENTRY_WITHOUT_CONTENT";
            }

            if (finding is not null)
            {
                throw await QuarantineIndexAsync(organizationId, finding, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Predicate authorization (R1 §E.1 step 2) on the reader snapshot's approvals and coverages: the same indistinguishable denials
    /// as V1 (the caller's query_fields permission and the organization policy, the field's Active approval and value type, the
    /// approved operator set, the frozen option snapshot), then coverage Complete. Returns the approval id per condition.
    /// </summary>
    private static Guid[] AuthorizePredicates(IReadOnlyList<PreparedIndexedCondition> conditions, SearchQueryStateFacts facts, bool mayQuery, bool plaintextPermitted, HashSet<Guid> ineffectiveRetained)
    {
        // C-188: an Active retained-field approval that is not effective now is treated exactly like no approval (same denial).
        var active = mayQuery && plaintextPermitted && facts.FormLifecycleState == ResourceLifecycleStates.Active
            ? facts.Approvals.Where(a => a.ApprovalStateName == FieldSearchApprovalStates.Active && !ineffectiveRetained.Contains(a.FieldSearchApprovalId)).GroupBy(a => a.FieldDefinitionId).ToDictionary(g => g.Key, g => g.ToList())
            : [];
        // Pass 1, every condition (V1 order): field, value type, operator, option. Only then pass 2: coverage, so an authorization
        // refusal is never masked by a not-ready answer for an earlier condition.
        var result = new Guid[conditions.Count];
        for (var i = 0; i < conditions.Count; i++)
        {
            var c = conditions[i];
            if (!active.TryGetValue(c.FieldDefinitionId, out var list) || list.Count != 1 || list[0].ApprovedFieldDataType != c.ValueType)
            {
                throw new AuthorityException(IndexedSearchErrorCodes.FieldNotAllowed, 422, false, $"condition {c.Number}: the field cannot be searched");
            }

            var approval = list[0];
            if (!FieldSearchRules.ReadOperators(approval).Contains(c.Operator, StringComparer.Ordinal)
                || (c.ValueType == "Option" && !FieldSearchRules.ReadOptions(approval).Any(o => o.FieldOptionId == (Guid)c.Value!)))
            {
                throw new AuthorityException(IndexedSearchErrorCodes.OperatorNotSupported, 422, false, $"condition {c.Number}: the operator is not supported");
            }

            result[i] = approval.FieldSearchApprovalId;
        }

        if (result.Any(id => !facts.Coverages.Any(cv => cv.FieldSearchApprovalId == id && cv.CoverageStateName == SearchCoverageStates.Complete)))
        {
            throw new AuthorityException(IndexedSearchErrorCodes.IndexNotReady, 503, true, "the index of a searched field is still being built");
        }

        return result;
    }

    /// <summary>The reader snapshot must agree with what was gated and pinned; an Active projection behind the content revision is an I-1 finding.</summary>
    private async Task RequireConsistentStateAsync(Guid organizationId, SearchQueryStateFacts facts, OrganizationAuthorizationState pinned, CancellationToken cancellationToken)
    {
        if (facts.IntegrityQuarantineEnabled)
        {
            throw AuthorityException.Quarantined("organization quarantined");
        }

        if (facts.AuthorizationRevision != pinned.AuthorizationRevision)
        {
            throw Changed("authorization changed during the search");
        }

        if (facts.Projection is not { } projection)
        {
            throw NotReady("the organization has no search index");
        }

        RequireServable(projection);
        if (projection.ProjectionContentIntegrityRevision != facts.ContentIntegrityRevision)
        {
            // INV-R keeps an Active projection at the content revision within every transaction; at one snapshot a difference is an
            // Authority-domain inconsistency (I-1), never a race.
            throw await QuarantineIndexAsync(organizationId, "PROJECTION_REVISION_BEHIND", cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RequireServableProjectionAsync(IAuthorityScope scope, Guid organizationId, CancellationToken cancellationToken, Guid? invalidateSnapshot = null)
    {
        await scope.SearchIndex.BindOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false);
        var projection = await scope.SearchIndex.FindProjectionStateAsync(organizationId, cancellationToken).ConfigureAwait(false) ?? throw NotReady("the organization has no search index");
        try
        {
            RequireServable(projection);
            // §17.10 D3: an overdue scope suspends indexed search before the scheduled evaluation records it (read-only here).
            var timestamps = await scope.SearchIndex.GetScopeTimestampsAsync(organizationId, cancellationToken).ConfigureAwait(false);
            var now = clock.GetUtcNow();
            if (sweep.Overdue(projection, timestamps, now, projection.LastEvaluationOn is { } last && last > now ? last : now).Count > 0)
            {
                throw new AuthorityException(IndexedSearchErrorCodes.IndexVerificationOverdue, 503, false, "search index verification is overdue; indexed search is suspended");
            }
        }
        catch (AuthorityException ex) when (invalidateSnapshot is { } id && !ex.Retryable)
        {
            throw await InvalidateAsync(id, ex, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void RequireServable(SearchProjectionState projection)
    {
        switch (projection.ProjectionStatusName)
        {
            case SearchProjectionStatuses.Quarantined:
                throw new AuthorityException(IndexedSearchErrorCodes.IndexInconsistent, 503, false, "the search index is quarantined");
            case SearchProjectionStatuses.Active when projection.VerificationSuspended:
                throw new AuthorityException(IndexedSearchErrorCodes.IndexVerificationOverdue, 503, false, "search index verification is overdue; indexed search is suspended");
            case SearchProjectionStatuses.Active:
                return;
            default:
                throw NotReady("the search index is " + projection.ProjectionStatusName);
        }
    }

    /// <summary>Under the organization lock: the projection, form state and coverages read by the search are unchanged.</summary>
    private static async Task RequireUnchangedIndexStateAsync(IAuthorityScope scope, Guid organizationId, Guid formDefinitionId, SearchQueryStateFacts facts, CancellationToken cancellationToken)
    {
        var projection = await scope.SearchIndex.FindProjectionStateAsync(organizationId, cancellationToken).ConfigureAwait(false);
        var formState = await scope.SearchIndex.FindFormStateAsync(organizationId, formDefinitionId, cancellationToken).ConfigureAwait(false);
        var coverages = await scope.SearchIndex.GetCoveragesAsync(organizationId, formDefinitionId, cancellationToken).ConfigureAwait(false);
        if (projection is not { ProjectionStatusName: SearchProjectionStatuses.Active, VerificationSuspended: false } || projection.ProjectionContentIntegrityRevision != facts.ContentIntegrityRevision
            || (formState?.FormContentRevision ?? 0) != facts.FormContentRevision
            || coverages.Count != facts.Coverages.Count || coverages.Any(c => facts.Coverages.FirstOrDefault(f => f.SearchFieldCoverageId == c.SearchFieldCoverageId)?.CoverageStateName != c.CoverageStateName))
        {
            throw Changed("the search index changed during the search");
        }
    }

    private async Task<AuthenticatedSession> RequireSessionAsync(IAuthorityScope scope, Guid organizationId, byte[] sessionDigest, BoundActor bound, CancellationToken cancellationToken)
    {
        var session = await scope.FindSessionByDigestAsync(sessionDigest, cancellationToken).ConfigureAwait(false);
        if (session is null || session.OrganizationId != organizationId || session.IdentityBindingId != bound.Binding.IdentityBindingId || !SessionLive(session))
        {
            throw new AuthorityException(IndexedSearchErrorCodes.SessionInvalid, 401, false, "the session is not valid");
        }

        return session;
    }

    /// <summary>§18.4: unrevoked, unexpired and within its idle timeout at this moment (time-based, so checked at every request).</summary>
    private bool SessionLive(AuthenticatedSession session)
    {
        var now = clock.GetUtcNow();
        return session.SessionRevokedOn is null && now < session.SessionExpiresOn && now - session.SessionLastSeenOn <= TimeSpan.FromSeconds(session.SessionIdleTimeoutSeconds);
    }

    private async Task<AuthorityException> QuarantineIndexAsync(Guid organizationId, string code, CancellationToken cancellationToken)
    {
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            var locked = await scope.LockOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false);
            var projection = locked is null ? null : await scope.SearchIndex.FindProjectionStateAsync(organizationId, cancellationToken).ConfigureAwait(false);
            if (locked is not null && projection is not null && projection.ProjectionStatusName != SearchProjectionStatuses.Quarantined)
            {
                SearchIndexStateTransitions.Quarantine(scope, locked, projection, code, clock.GetUtcNow(), clock);
                await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
                SearchIndexStateTransitions.RaiseQuarantine(alerts, organizationId, code);
            }
        }

        return new AuthorityException(IndexedSearchErrorCodes.IndexInconsistent, 503, false, "the search index failed an integrity check; it is quarantined");
    }

    private async Task<AuthorityException> InvalidateAsync(Guid snapshotId, AuthorityException failure, CancellationToken cancellationToken)
    {
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        await scope.SearchIndex.DeleteResultSnapshotAsync(snapshotId, cancellationToken).ConfigureAwait(false);
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return failure;
    }

    private async Task JournalFailureAsync(Guid organizationId, Guid formDefinitionId, Guid? userId, IReadOnlyList<PreparedIndexedCondition> conditions, string outcome, long started, Guid correlationId, CancellationToken cancellationToken)
    {
        if (userId is not { } user)
        {
            return;
        }

        try
        {
            await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
            var locked = await scope.LockOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false);
            if (locked is null)
            {
                return;
            }

            AuditJournal.Append(scope, locked, "FieldSearchExecuted", Evidence(null, formDefinitionId, user, conditions, outcome, null, null, started, locked.AuthorizationRevision, correlationId), clock);
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // best effort, as V1: the failure is returned regardless
        }
    }

    private object Evidence(Guid? snapshotId, Guid formDefinitionId, Guid userId, IReadOnlyList<PreparedIndexedCondition> conditions, string outcome, int? evaluated, int? matched, long started, long revision, Guid correlationId) => new
    {
        search_result_snapshot_id = snapshotId,
        form_definition_id = formDefinitionId,
        authority_user_id = userId,
        search_engine_name = Engine,
        field_definition_ids = conditions.Select(c => c.FieldDefinitionId).Distinct().ToList(),
        operators = conditions.Select(c => c.Operator).ToList(),
        condition_count = conditions.Count,
        outcome,
        evaluated_record_count = evaluated,
        matched_record_count = matched,
        elapsed_milliseconds = (long)clock.GetElapsedTime(started).TotalMilliseconds,
        authorization_revision = revision,
        correlation_id = correlationId,
    };

    private (IndexedSearchQuery Query, IReadOnlyList<PreparedIndexedCondition> Conditions, byte[] Nonce, byte[] SessionDigest) Validate(IndexedSearchQuery? query, string? nonceBase64, string? sessionDigestBase64)
    {
        if (query is null || query.FormDefinitionId == Guid.Empty)
        {
            throw AuthorityException.Invalid("form_definition_id is required");
        }

        if (!IndexedSearchOptions.PageSizes.Contains(query.PageSize) || (query.SortBy is not null && !SortFields.Contains(query.SortBy, StringComparer.Ordinal))
            || (query.SortDirection is not null && !SortDirections.Contains(query.SortDirection, StringComparer.Ordinal))
            || (query.RecordStatus is not null && !RecordStatuses.Contains(query.RecordStatus, StringComparer.Ordinal)))
        {
            throw AuthorityException.Invalid("page size 10/25/50/100; known sort field, direction and record status only");
        }

        var nonce = Bytes32(nonceBase64, "query nonce");
        var session = Bytes32(sessionDigestBase64, "session digest");
        return (query, IndexedSearchPredicates.Prepare(query.Conditions, options), nonce, session);
    }

    /// <summary>§17.3: SHA-256 over the per-snapshot nonce and the canonical query (the nonce never reaches AuthorityDb).</summary>
    public static byte[] QueryDigest(byte[] nonce, IndexedSearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(nonce);
        ArgumentNullException.ThrowIfNull(query);
        return SHA256.HashData([.. nonce, .. CanonicalJson.SerializeBytes(query)]);
    }

    private static bool MetadataMatches(IndexedSearchQuery query, DerivedHeaderFacts header) =>
        (query.IncludeChildren || header.ParentFormRecordId is null)
        && (query.RecordStatus is null || string.Equals(header.RecordStatusName, query.RecordStatus, StringComparison.Ordinal))
        && (query.RecordStatus is not null || query.IncludeArchived || !string.Equals(header.RecordStatusName, "Archived", StringComparison.Ordinal));

    /// <summary>The V1 sort exactly (C7 <c>FieldSearchHandler.Sort</c>): metadata only, last change then record id make it total.</summary>
    public static List<(Guid Id, DerivedHeaderFacts Header, DateTimeOffset Created, DateTimeOffset Modified)> Sort(List<(Guid Id, DerivedHeaderFacts Header, DateTimeOffset Created, DateTimeOffset Modified)> matches, string? sortBy, string? sortDirection)
    {
        ArgumentNullException.ThrowIfNull(matches);
        var sort = string.IsNullOrEmpty(sortBy) ? "modifiedOn" : sortBy;
        var descending = (string.IsNullOrEmpty(sortDirection) ? (sort is "formName" or "recordStatus" ? "asc" : "desc") : sortDirection) == "desc";
        IOrderedEnumerable<(Guid Id, DerivedHeaderFacts Header, DateTimeOffset Created, DateTimeOffset Modified)> ordered = sort switch
        {
            "createdOn" => descending ? matches.OrderByDescending(m => m.Created) : matches.OrderBy(m => m.Created),
            "recordStatus" => descending ? matches.OrderByDescending(m => m.Header.RecordStatusName, StringComparer.Ordinal) : matches.OrderBy(m => m.Header.RecordStatusName, StringComparer.Ordinal),
            "formName" => matches.OrderBy(_ => 0),
            _ => descending ? matches.OrderByDescending(m => m.Modified) : matches.OrderBy(m => m.Modified),
        };
        return (descending ? ordered.ThenByDescending(m => m.Modified).ThenByDescending(m => m.Id) : ordered.ThenBy(m => m.Modified).ThenBy(m => m.Id)).ToList();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "C-150: the shadow comparison must never affect a committed Tier 1 answer.")]
    private void OfferShadow(IndexedSearchShadowObservation observation)
    {
        if (shadow is null)
        {
            return;
        }

        try
        {
            shadow.Offer(observation);
        }
        catch (Exception)
        {
            // Off-path by construction: the search already committed and its answer is returned unchanged.
        }
    }

    private void RequireAvailable()
    {
        if (!Available)
        {
            throw NotReady("indexed search is not enabled in this deployment");
        }
    }

    private static List<Guid> Ids(IReadOnlyList<string> values) => values.Select(v => Guid.TryParse(v, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).Distinct().ToList();

    private static byte[] Bytes32(string? base64, string name)
    {
        try
        {
            var bytes = Convert.FromBase64String(base64 ?? string.Empty);
            return bytes.Length == 32 ? bytes : throw AuthorityException.Invalid(name + " must be 32 bytes");
        }
        catch (FormatException)
        {
            throw AuthorityException.Invalid(name + " must be base64");
        }
    }

    private static AuthorityException Limit(string kind) => new(IndexedSearchErrorCodes.VerificationLimit, 422, false, kind);

    private static AuthorityException Changed(string detail) => new(IndexedSearchErrorCodes.SnapshotChanged, 409, true, detail);

    private static AuthorityException NotReady(string detail) => new(IndexedSearchErrorCodes.IndexNotReady, 503, true, detail);

    private static AuthorityException NotFound() => new(IndexedSearchErrorCodes.SnapshotNotFound, 404, false, "no such search result");
}
