using System.Security.Cryptography;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Authorization;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Application.Features.Journal;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Catalogue;
using AuthorizationAuthority.Domain.Features.Content;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Organizations;

namespace AuthorizationAuthority.Application.Features.Catalogue;

/// <summary>Configuration section <c>Catalogue</c> (bound and validated by the host). Provisional engineering limits (C-56 D4), not production capacity promises.</summary>
public sealed class CatalogueOptions
{
    public const string SectionName = "Catalogue";

    /// <summary>Ceiling for a user-scoped catalogue (the business API passes its own, never larger, limit).</summary>
    public int MaximumUserEntries { get; set; } = 500;

    /// <summary>
    /// Phase C7 M3: ceiling for registered records without accepted content (pending creations) listed in a user catalogue.
    /// They need no verification and can match nothing, so they do not count against <see cref="MaximumUserEntries"/>; this
    /// separate bound keeps abandoned creations from making a form unsearchable while still listing every one of them.
    /// </summary>
    public int MaximumPendingUserEntries { get; set; } = 2000;

    /// <summary>Ceiling for the verifier's complete population of one form (paged).</summary>
    public int MaximumMigrationEntries { get; set; } = 50_000;

    public int MigrationPageSize { get; set; } = 1000;

    public int UserSnapshotLifetimeSeconds { get; set; } = 300;

    public int MigrationSnapshotLifetimeSeconds { get; set; } = 900;
}

/// <summary>
/// AC-5 (Phase C7 M1, decisions C-56 D1, C-60 OD-1/OD-5/OD-8): the Authority-owned record catalogue.
/// <para>
/// The expected record population comes only from the Authority's registry (form_record resources whose registered parent
/// is the form) and its accepted content heads — never from the business database. Two boundaries:
/// <list type="bullet">
/// <item>user-scoped: the registered records of the form that the delegated user may discover (SpiceDB, complete lookup at
/// the Authority-held token), for trusted search;</item>
/// <item>migration verification: the complete population of the form (records no user can discover included), only for the
/// separate verifier workload and only when an effective protection command of that form scopes the request.</item>
/// </list>
/// Each catalogue is persisted as an immutable snapshot (entries with their exact heads) fixed at one authorization,
/// projection and content-integrity revision, read in one transaction and re-checked under the organization lock. A
/// snapshot id is only a reference: validity is decided against current state (§3.4) — any authorization or projection
/// revision change invalidates (OD-5); a content-integrity change invalidates only if an entry's head, quarantine or
/// registry state changed. Issuance and denials are journaled on the non-gating audit stream (OD-8).
/// </para>
/// </summary>
public sealed class SearchCatalogueService(IAuthorityStore store, ISpiceDbProjection spiceDb, TimeProvider clock, CatalogueOptions options)
{
    private static readonly string[] UserScopeStates = [ResourceLifecycleStates.Active, ResourceLifecycleStates.Quarantined];

    /// <summary>The verifier's population includes records whose registration is not yet effective (they hold no content).</summary>
    private static readonly string[] MigrationScopeStates = [ResourceLifecycleStates.Active, ResourceLifecycleStates.Quarantined, ResourceLifecycleStates.Registered];

    public async Task<SearchCatalogueResponse> IssueUserCatalogueAsync(VerifiedActor actor, SearchCatalogueRequest request, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var settings = options;
        if (request.FormDefinitionId == Guid.Empty || request.MaximumEntries < 1 || request.MaximumEntries > settings.MaximumUserEntries)
        {
            throw AuthorityException.Invalid($"form_definition_id required; maximum_entries must be 1..{settings.MaximumUserEntries}");
        }

        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var state = await scope.ReadOrganizationAsync(request.OrganizationId, cancellationToken).ConfigureAwait(false)
                    ?? throw AuthorityException.NotRegistered("organization is not registered");
        var bound = await ActorResolver.BindAsync(scope, state.OrganizationId, actor, cancellationToken).ConfigureAwait(false);
        OrganizationGate.RequireOpen(await OrganizationGate.EvaluateAsync(scope, state, cancellationToken).ConfigureAwait(false));

        IReadOnlyList<string> lookedUp;
        try
        {
            lookedUp = await spiceDb.LookupResourcesAsync(ResourceTypeNames.FormRecord, "discover", ResourceTypeNames.User, bound.AuthorityUserId.ToString("D"), state.ProjectionZedToken!, PermissionCheckService.MaximumDiscoverableRecords, cancellationToken).ConfigureAwait(false);
        }
        catch (SpiceDbLookupLimitExceededException)
        {
            await JournalRefusalAsync(state.OrganizationId, "SearchCatalogueRefused", s => new CatalogueAuditBody(null, CatalogueScopeKinds.UserSearch, request.FormDefinitionId, bound.AuthorityUserId, null, null, 0, null, s.AuthorizationRevision, s.ContentIntegrityRevision, "DISCOVERY_CEILING", correlationId), cancellationToken).ConfigureAwait(false);
            throw new AuthorityException(AuthorityErrorCodes.LimitExceeded, 422, false, "the discoverable set exceeds the lookup ceiling; nothing partial is returned");
        }

        var discoverable = lookedUp.Select(v => Guid.TryParse(v, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).ToHashSet();
        var resources = (await scope.GetFormRecordResourcesAsync(state.OrganizationId, request.FormDefinitionId, UserScopeStates, cancellationToken).ConfigureAwait(false))
            .Where(r => discoverable.Contains(r.ResourceEntityId))
            .ToList();
        var heads = await ReadHeadsAsync(scope, state.OrganizationId, resources, cancellationToken).ConfigureAwait(false);
        var contentful = resources.Count(r => heads.GetValueOrDefault(r.ResourceEntityId) is { CommittedContentRevision: > 0 });
        if (contentful > request.MaximumEntries || resources.Count - contentful > settings.MaximumPendingUserEntries)
        {
            // D3/D4: a scope that cannot be verified completely is refused before any work — no entries and no count. Records
            // without accepted content (pending creations) need no verification and have their own ceiling (Phase C7 M3).
            var reason = contentful > request.MaximumEntries ? "VERIFICATION_LIMIT" : "PENDING_CEILING";
            await JournalRefusalAsync(state.OrganizationId, "SearchCatalogueRefused", s => new CatalogueAuditBody(null, CatalogueScopeKinds.UserSearch, request.FormDefinitionId, bound.AuthorityUserId, null, null, 0, null, s.AuthorizationRevision, s.ContentIntegrityRevision, reason, correlationId), cancellationToken).ConfigureAwait(false);
            throw new AuthorityException(AuthorityErrorCodes.LimitExceeded, 422, false, "the form's eligible records exceed the verification limit");
        }

        var entries = BuildEntries(state.OrganizationId, resources, heads);
        var locked = await LockUnchangedAsync(scope, state, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        var snapshot = NewSnapshot(locked, CatalogueScopeKinds.UserSearch, request.FormDefinitionId, entries, now, settings.UserSnapshotLifetimeSeconds);
        snapshot.RequestingAuthorityUserId = bound.AuthorityUserId;
        Audit(scope, locked, "SearchCatalogueIssued", new CatalogueAuditBody(snapshot.SearchCatalogueSnapshotId, CatalogueScopeKinds.UserSearch, request.FormDefinitionId, bound.AuthorityUserId, null, null, entries.Count, Convert.ToHexStringLower(snapshot.CatalogueDigest), locked.AuthorizationRevision, locked.ContentIntegrityRevision, null, correlationId), now);
        scope.AddCatalogueSnapshot(snapshot, entries);
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        // Authority-held times (Phase C7 M3) are returned for sorting and display; the snapshot itself fixes heads, not times.
        var registeredOn = resources.ToDictionary(r => r.ResourceEntityId, r => r.ResourceRegisteredOn);
        return new SearchCatalogueResponse(snapshot.SearchCatalogueSnapshotId, state.OrganizationId, request.FormDefinitionId, locked.AuthorizationRevision, locked.ProjectionAppliedRevision, locked.ContentIntegrityRevision,
            entries.Select(e => ToContract(e) with
            {
                RecordRegisteredOn = registeredOn[e.FormRecordId],
                ContentHeadModifiedOn = heads.GetValueOrDefault(e.FormRecordId) is { CommittedContentRevision: > 0 } head ? head.ContentHeadModifiedOn : null,
            }).ToList(), snapshot.SnapshotExpiresOn, correlationId);
    }

    /// <summary>A user's snapshot, validated for that same user only (anyone else gets the not-found answer).</summary>
    public async Task<CatalogueSnapshotValidation> ValidateUserSnapshotAsync(VerifiedActor actor, Guid snapshotId, Guid correlationId, CancellationToken cancellationToken)
    {
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = await scope.FindCatalogueSnapshotAsync(snapshotId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null || snapshot.SnapshotScopeKind != CatalogueScopeKinds.UserSearch)
        {
            throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false);
        }

        var bound = await ActorResolver.BindAsync(scope, snapshot.OrganizationId, actor, cancellationToken).ConfigureAwait(false);
        if (bound.AuthorityUserId != snapshot.RequestingAuthorityUserId)
        {
            throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false);
        }

        return await ValidateAsync(scope, snapshot, correlationId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<MigrationCataloguePage> IssueMigrationCatalogueAsync(string workloadName, MigrationCatalogueRequest request, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(workloadName);
        var settings = options;
        if (request.FormDefinitionId == Guid.Empty || request.SecurityCommandId == Guid.Empty)
        {
            throw AuthorityException.Invalid("form_definition_id and security_command_id are required");
        }

        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var state = await scope.ReadOrganizationAsync(request.OrganizationId, cancellationToken).ConfigureAwait(false)
                    ?? throw AuthorityException.NotRegistered("organization is not registered");
        OrganizationGate.RequireOpen(await OrganizationGate.EvaluateAsync(scope, state, cancellationToken).ConfigureAwait(false));

        ProtectionCommandScope decision;
        try
        {
            decision = await ProtectionCommandResolver.ResolveAsync(scope, state.OrganizationId, request.SecurityCommandId, cancellationToken).ConfigureAwait(false);
            if (decision.FormDefinitionId != request.FormDefinitionId)
            {
                throw AuthorityException.Forbidden("the command does not scope this form");
            }
        }
        catch (AuthorityException denied) when (denied.ErrorCode == AuthorityErrorCodes.Forbidden)
        {
            // OD-8: a refused privileged enumeration is evidence too.
            await JournalRefusalAsync(state.OrganizationId, "MigrationCatalogueDenied", s => new CatalogueAuditBody(null, CatalogueScopeKinds.MigrationVerification, request.FormDefinitionId, null, workloadName, request.SecurityCommandId, 0, null, s.AuthorizationRevision, s.ContentIntegrityRevision, "COMMAND_NOT_SCOPING", correlationId), cancellationToken).ConfigureAwait(false);
            throw;
        }

        var resources = await scope.GetFormRecordResourcesAsync(state.OrganizationId, request.FormDefinitionId, MigrationScopeStates, cancellationToken).ConfigureAwait(false);
        if (resources.Count > settings.MaximumMigrationEntries)
        {
            await JournalRefusalAsync(state.OrganizationId, "MigrationCatalogueRefused", s => new CatalogueAuditBody(null, CatalogueScopeKinds.MigrationVerification, request.FormDefinitionId, null, workloadName, decision.CommandId, 0, null, s.AuthorizationRevision, s.ContentIntegrityRevision, "MIGRATION_CEILING", correlationId), cancellationToken).ConfigureAwait(false);
            throw new AuthorityException(AuthorityErrorCodes.LimitExceeded, 422, false, "the form's population exceeds the migration catalogue ceiling; nothing partial is returned");
        }

        var entries = await BuildEntriesAsync(scope, state.OrganizationId, resources, cancellationToken).ConfigureAwait(false);
        var locked = await LockUnchangedAsync(scope, state, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        var snapshot = NewSnapshot(locked, CatalogueScopeKinds.MigrationVerification, request.FormDefinitionId, entries, now, settings.MigrationSnapshotLifetimeSeconds);
        snapshot.RequestingWorkloadName = workloadName;
        snapshot.ScopingSecurityCommandId = decision.CommandId;
        Audit(scope, locked, "MigrationCatalogueIssued", new CatalogueAuditBody(snapshot.SearchCatalogueSnapshotId, CatalogueScopeKinds.MigrationVerification, request.FormDefinitionId, null, workloadName, decision.CommandId, entries.Count, Convert.ToHexStringLower(snapshot.CatalogueDigest), locked.AuthorizationRevision, locked.ContentIntegrityRevision, null, correlationId), now);
        scope.AddCatalogueSnapshot(snapshot, entries);
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToPage(snapshot, entries, 0, settings.MigrationPageSize, correlationId);
    }

    public async Task<MigrationCataloguePage> ReadMigrationCatalogueAsync(string workloadName, Guid snapshotId, int offset, Guid correlationId, CancellationToken cancellationToken)
    {
        if (offset < 0)
        {
            throw AuthorityException.Invalid("offset must not be negative");
        }

        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = await RequireMigrationSnapshotAsync(scope, workloadName, snapshotId, cancellationToken).ConfigureAwait(false);
        if (snapshot.SnapshotExpiresOn <= clock.GetUtcNow())
        {
            throw new AuthorityException(AuthorityErrorCodes.ContinuationInvalidated, 409, false, "the migration catalogue expired; request a new one");
        }

        var entries = await scope.GetCatalogueSnapshotEntriesAsync(snapshotId, offset, options.MigrationPageSize, cancellationToken).ConfigureAwait(false);
        return new MigrationCataloguePage(snapshot.SearchCatalogueSnapshotId, snapshot.OrganizationId, snapshot.FormDefinitionId, snapshot.ScopingSecurityCommandId ?? Guid.Empty,
            snapshot.SnapshotAuthorizationRevision, snapshot.SnapshotContentIntegrityRevision, snapshot.CatalogueEntryCount, entries.Select(ToContract).ToList(),
            offset + entries.Count < snapshot.CatalogueEntryCount ? offset + entries.Count : null, correlationId);
    }

    public async Task<CatalogueSnapshotValidation> ValidateMigrationSnapshotAsync(string workloadName, Guid snapshotId, Guid correlationId, CancellationToken cancellationToken)
    {
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = await RequireMigrationSnapshotAsync(scope, workloadName, snapshotId, cancellationToken).ConfigureAwait(false);
        return await ValidateAsync(scope, snapshot, correlationId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<SearchCatalogueSnapshot> RequireMigrationSnapshotAsync(IAuthorityScope scope, string workloadName, Guid snapshotId, CancellationToken cancellationToken)
    {
        var snapshot = await scope.FindCatalogueSnapshotAsync(snapshotId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null || snapshot.SnapshotScopeKind != CatalogueScopeKinds.MigrationVerification || !string.Equals(snapshot.RequestingWorkloadName, workloadName, StringComparison.Ordinal))
        {
            throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false);
        }

        return snapshot;
    }

    /// <summary>
    /// Re-reads current state; never trusts the snapshot row beyond what it fixed. Invalidations are recorded on the snapshot
    /// (an invalid snapshot never becomes valid again).
    /// </summary>
    private async Task<CatalogueSnapshotValidation> ValidateAsync(IAuthorityScope scope, SearchCatalogueSnapshot snapshot, Guid correlationId, CancellationToken cancellationToken)
    {
        var state = await scope.ReadOrganizationAsync(snapshot.OrganizationId, cancellationToken).ConfigureAwait(false)
                    ?? throw AuthorityException.NotRegistered("organization is not registered");
        var reason = snapshot.SnapshotInvalidationReason;
        if (reason is null && snapshot.SnapshotExpiresOn <= clock.GetUtcNow())
        {
            reason = CatalogueInvalidationReasons.Expired;
        }

        if (reason is null && await OrganizationGate.EvaluateAsync(scope, state, cancellationToken).ConfigureAwait(false) != ProjectionGateState.Ready)
        {
            // A closed gate is transient: report it without recording an invalidation.
            return new CatalogueSnapshotValidation(snapshot.SearchCatalogueSnapshotId, false, CatalogueInvalidationReasons.GateClosed, state.AuthorizationRevision, state.ContentIntegrityRevision, correlationId);
        }

        if (reason is null && state.AuthorizationRevision != snapshot.SnapshotAuthorizationRevision)
        {
            reason = CatalogueInvalidationReasons.AuthorizationChanged;
        }

        if (reason is null && state.ProjectionAppliedRevision != snapshot.SnapshotProjectionRevision)
        {
            reason = CatalogueInvalidationReasons.ProjectionChanged;
        }

        if (reason is null && snapshot.ScopingSecurityCommandId is { } commandId)
        {
            try
            {
                var decision = await ProtectionCommandResolver.ResolveAsync(scope, snapshot.OrganizationId, commandId, cancellationToken).ConfigureAwait(false);
                if (decision.FormDefinitionId != snapshot.FormDefinitionId)
                {
                    reason = CatalogueInvalidationReasons.CommandSuperseded;
                }
            }
            catch (AuthorityException ex) when (ex.ErrorCode == AuthorityErrorCodes.Forbidden)
            {
                reason = CatalogueInvalidationReasons.CommandSuperseded;
            }
        }

        if (reason is null && state.ContentIntegrityRevision != snapshot.SnapshotContentIntegrityRevision)
        {
            // Slow path: something in the organization's content changed; the snapshot survives only if none of its entries did.
            var entries = await scope.GetCatalogueSnapshotEntriesAsync(snapshot.SearchCatalogueSnapshotId, 0, null, cancellationToken).ConfigureAwait(false);
            var ids = entries.Select(e => e.FormRecordId).ToList();
            var heads = (await scope.GetContentHeadsAsync(snapshot.OrganizationId, ids, cancellationToken).ConfigureAwait(false)).ToDictionary(h => h.FormRecordId);
            var registry = (await scope.GetResourcesAsync(snapshot.OrganizationId, ResourceTypeNames.FormRecord, ids, cancellationToken).ConfigureAwait(false)).ToDictionary(r => r.ResourceEntityId);
            foreach (var entry in entries)
            {
                if (!registry.TryGetValue(entry.FormRecordId, out var resource) || resource.ResourceLifecycleState != entry.ResourceLifecycleState || resource.ParentResourceEntityId != snapshot.FormDefinitionId)
                {
                    reason = CatalogueInvalidationReasons.RecordUnavailable;
                    break;
                }

                var head = heads.GetValueOrDefault(entry.FormRecordId);
                if ((head?.CommittedContentRevision ?? 0) != entry.CommittedContentRevision || head?.CommittedOperationId != entry.CommittedOperationId || (head?.ContentHeadQuarantined ?? false) != entry.ContentHeadQuarantined)
                {
                    reason = CatalogueInvalidationReasons.ContentChanged;
                    break;
                }
            }
        }

        if (reason is not null && snapshot.SnapshotInvalidationReason is null)
        {
            snapshot.SnapshotInvalidationReason = reason;
            snapshot.SnapshotInvalidatedOn = clock.GetUtcNow();
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return new CatalogueSnapshotValidation(snapshot.SearchCatalogueSnapshotId, reason is null, reason, state.AuthorizationRevision, state.ContentIntegrityRevision, correlationId);
    }

    private static async Task<List<SearchCatalogueSnapshotEntry>> BuildEntriesAsync(IAuthorityScope scope, Guid organizationId, List<AuthorityResource> resources, CancellationToken cancellationToken) =>
        BuildEntries(organizationId, resources, await ReadHeadsAsync(scope, organizationId, resources, cancellationToken).ConfigureAwait(false));

    private static async Task<Dictionary<Guid, Domain.Features.Content.RecordContentHead>> ReadHeadsAsync(IAuthorityScope scope, Guid organizationId, List<AuthorityResource> resources, CancellationToken cancellationToken)
    {
        var ids = resources.Select(r => r.ResourceEntityId).ToList();
        return ids.Count == 0 ? [] : (await scope.GetContentHeadsAsync(organizationId, ids, cancellationToken).ConfigureAwait(false)).ToDictionary(h => h.FormRecordId);
    }

    private static List<SearchCatalogueSnapshotEntry> BuildEntries(Guid organizationId, List<AuthorityResource> resources, Dictionary<Guid, Domain.Features.Content.RecordContentHead> heads)
    {
        return resources.OrderBy(r => r.ResourceEntityId).Select((r, index) =>
        {
            var head = heads.GetValueOrDefault(r.ResourceEntityId);
            return new SearchCatalogueSnapshotEntry
            {
                SearchCatalogueSnapshotEntryId = Guid.CreateVersion7(),
                OrganizationId = organizationId,
                FormRecordId = r.ResourceEntityId,
                CatalogueEntryPosition = index,
                CommittedContentRevision = head?.CommittedContentRevision ?? 0,
                CommittedOperationId = head?.CommittedOperationId,
                ContentHeadQuarantined = head?.ContentHeadQuarantined ?? false,
                ResourceLifecycleState = r.ResourceLifecycleState,
            };
        }).ToList();
    }

    /// <summary>The registry and head reads above ran without the lock; under the lock nothing they depend on may have moved.</summary>
    private static async Task<OrganizationAuthorizationState> LockUnchangedAsync(IAuthorityScope scope, OrganizationAuthorizationState read, CancellationToken cancellationToken)
    {
        var locked = await scope.LockOrganizationAsync(read.OrganizationId, cancellationToken).ConfigureAwait(false);
        if (locked is null || locked.AuthorizationRevision != read.AuthorizationRevision || locked.ProjectionAppliedRevision != read.ProjectionAppliedRevision
            || locked.ContentIntegrityRevision != read.ContentIntegrityRevision || locked.IntegrityQuarantineEnabled)
        {
            throw AuthorityException.NotReady("authorization or content state moved while the catalogue was read");
        }

        return locked;
    }

    private static SearchCatalogueSnapshot NewSnapshot(OrganizationAuthorizationState state, string scopeKind, Guid formDefinitionId, List<SearchCatalogueSnapshotEntry> entries, DateTimeOffset now, int lifetimeSeconds)
    {
        var id = Guid.CreateVersion7();
        foreach (var entry in entries)
        {
            entry.SearchCatalogueSnapshotId = id;
        }

        var canonical = CanonicalJson.SerializeBytes(entries.Select(e => new CanonicalEntry(e.FormRecordId, e.CommittedContentRevision, e.CommittedOperationId, e.ContentHeadQuarantined, e.ResourceLifecycleState)).ToList());
        return new SearchCatalogueSnapshot
        {
            SearchCatalogueSnapshotId = id,
            OrganizationId = state.OrganizationId,
            SnapshotScopeKind = scopeKind,
            FormDefinitionId = formDefinitionId,
            SnapshotAuthorizationRevision = state.AuthorizationRevision,
            SnapshotProjectionRevision = state.ProjectionAppliedRevision,
            SnapshotContentIntegrityRevision = state.ContentIntegrityRevision,
            CatalogueEntryCount = entries.Count,
            CatalogueDigest = SHA256.HashData(canonical),
            SnapshotIssuedOn = now,
            SnapshotExpiresOn = now.AddSeconds(Math.Clamp(lifetimeSeconds, 1, 900)),
        };
    }

    /// <summary>
    /// A refusal is journaled in its own committed transaction before the refusal is returned (the request's own scope is
    /// rolled back). If the journal entry cannot be committed the caller gets that failure instead of the refusal: a
    /// refused sensitive enumeration is never reported without its evidence.
    /// </summary>
    private async Task JournalRefusalAsync(Guid organizationId, string eventKind, Func<OrganizationAuthorizationState, CatalogueAuditBody> body, CancellationToken cancellationToken)
    {
        await using var auditScope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var auditState = await auditScope.LockOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false);
        if (auditState is null)
        {
            return;
        }

        Audit(auditScope, auditState, eventKind, body(auditState), clock.GetUtcNow());
        await auditScope.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void Audit(IAuthorityScope scope, OrganizationAuthorizationState locked, string eventKind, CatalogueAuditBody body, DateTimeOffset now)
    {
        _ = now;
        AuditJournal.Append(scope, locked, eventKind, body, clock);
    }

    /// <summary>
    /// Phase C7 M3 (OD-8): records one field-search execution over the caller's own user snapshot on the audit stream — field
    /// ids, operators, counts, outcome and duration only. Anyone else's snapshot: not found.
    /// </summary>
    public async Task<SearchExecutionRecorded> RecordExecutionAsync(VerifiedActor actor, Guid snapshotId, SearchExecutionEvidence evidence, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.FieldDefinitionIds is null || evidence.Operators is null || evidence.FieldDefinitionIds.Count > 5 || evidence.Operators.Count > 5 || evidence.ConditionCount is < 0 or > 5
            || !ExecutionOutcomes.Contains(evidence.Outcome ?? string.Empty) || evidence.Operators.Any(o => o is null || o.Length > 40) || evidence.EvaluatedRecordCount < 0 || evidence.MatchedRecordCount < 0 || evidence.ElapsedMilliseconds < 0)
        {
            throw AuthorityException.Invalid("execution evidence: at most 5 field ids and operators, a known outcome and non-negative counts");
        }

        Guid organizationId;
        Guid formDefinitionId;
        Guid userId;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            var snapshot = await scope.FindCatalogueSnapshotAsync(snapshotId, cancellationToken).ConfigureAwait(false);
            if (snapshot is null || snapshot.SnapshotScopeKind != CatalogueScopeKinds.UserSearch)
            {
                throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false);
            }

            var bound = await ActorResolver.BindAsync(scope, snapshot.OrganizationId, actor, cancellationToken).ConfigureAwait(false);
            if (bound.AuthorityUserId != snapshot.RequestingAuthorityUserId)
            {
                throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false);
            }

            (organizationId, formDefinitionId, userId) = (snapshot.OrganizationId, snapshot.FormDefinitionId, bound.AuthorityUserId);
        }

        await using var auditScope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var locked = await auditScope.LockOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false)
                     ?? throw AuthorityException.NotRegistered("organization is not registered");
        AuditJournal.Append(auditScope, locked, "FieldSearchExecuted", new SearchExecutionAuditBody(snapshotId, formDefinitionId, userId, evidence.FieldDefinitionIds, evidence.Operators, evidence.ConditionCount,
            evidence.Outcome!, evidence.EvaluatedRecordCount, evidence.MatchedRecordCount, evidence.ElapsedMilliseconds, locked.AuthorizationRevision, correlationId), clock);
        await auditScope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SearchExecutionRecorded(snapshotId, correlationId);
    }

    /// <summary>Outcome codes of an execution (success or the business search error code).</summary>
    public static readonly IReadOnlySet<string> ExecutionOutcomes = new HashSet<string>(StringComparer.Ordinal)
    {
        "COMPLETED", "SEARCH_SNAPSHOT_CHANGED", "SEARCH_VERIFICATION_LIMIT", "SEARCH_VERIFICATION_UNAVAILABLE", "SEARCH_FIELD_NOT_ALLOWED",
    };

    private static CatalogueEntry ToContract(SearchCatalogueSnapshotEntry e) =>
        new(e.FormRecordId, e.CommittedContentRevision, e.CommittedOperationId, e.ContentHeadQuarantined, e.ResourceLifecycleState == ResourceLifecycleStates.Quarantined);

    private static MigrationCataloguePage ToPage(SearchCatalogueSnapshot snapshot, List<SearchCatalogueSnapshotEntry> all, int offset, int pageSize, Guid correlationId)
    {
        var page = all.Skip(offset).Take(pageSize).ToList();
        return new MigrationCataloguePage(snapshot.SearchCatalogueSnapshotId, snapshot.OrganizationId, snapshot.FormDefinitionId, snapshot.ScopingSecurityCommandId ?? Guid.Empty,
            snapshot.SnapshotAuthorizationRevision, snapshot.SnapshotContentIntegrityRevision, snapshot.CatalogueEntryCount, page.Select(ToContract).ToList(),
            offset + page.Count < snapshot.CatalogueEntryCount ? offset + page.Count : null, correlationId);
    }

    private sealed record CanonicalEntry(Guid FormRecordId, long CommittedContentRevision, Guid? CommittedOperationId, bool ContentHeadQuarantined, string ResourceLifecycleState);

    /// <summary>Execution audit body (OD-8): field ids, operators, counts and outcome only — never predicate values or record ids.</summary>
    private sealed record SearchExecutionAuditBody(Guid SearchCatalogueSnapshotId, Guid FormDefinitionId, Guid AuthorityUserId, IReadOnlyList<Guid> FieldDefinitionIds, IReadOnlyList<string> Operators, int ConditionCount,
        string Outcome, int? EvaluatedRecordCount, int? MatchedRecordCount, long ElapsedMilliseconds, long AuthorizationRevision, Guid CorrelationId);

    /// <summary>Audit journal body: identifiers, counts and revisions only — never record contents, field values or predicates.</summary>
    private sealed record CatalogueAuditBody(Guid? SearchCatalogueSnapshotId, string ScopeKind, Guid FormDefinitionId, Guid? AuthorityUserId, string? WorkloadName, Guid? SecurityCommandId, int EntryCount, string? CatalogueDigestHex, long AuthorizationRevision, long ContentIntegrityRevision, string? DenialReason, Guid CorrelationId);
}

public static class CatalogueOptionsValidation
{
    /// <summary>Fail closed at startup on an unusable configuration.</summary>
    public static void Validate(CatalogueOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaximumUserEntries < 1 || options.MaximumPendingUserEntries < 1 || options.MaximumMigrationEntries < 1 || options.MigrationPageSize is < 1 or > 5000
            || options.UserSnapshotLifetimeSeconds is < 1 or > 900 || options.MigrationSnapshotLifetimeSeconds is < 1 or > 900)
        {
            throw new InvalidOperationException("Catalogue limits must be positive; page size 1..5000; snapshot lifetimes 1..900 seconds");
        }
    }
}
