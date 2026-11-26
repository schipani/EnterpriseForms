using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Catalogue;
using AuthorizationAuthority.Domain.Features.Content;
using AuthorizationAuthority.Domain.Features.FieldSearch;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Keys;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.PlatformOperators;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using AuthorizationAuthority.Domain.Features.Sessions;
using AuthorizationAuthority.Domain.Features.Sharing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AuthorizationAuthority.Infrastructure.Persistence;

/// <summary>
/// EF Core implementation of the authority store port. One DbContext + one transaction per scope.
/// C-150 R56-B: a scope that appended to the authorization stream notifies <see cref="AuthorityWorkOptions.NotificationChannel"/> inside its
/// transaction (PostgreSQL delivers it to every listening Authority instance only if the transaction commits) and signals this instance's
/// work loop after the commit returned. Neither is a statement that work exists or completed; the loop reads durable state.
/// <para>
/// RB-CARV-1 (contract §22): <paramref name="contentAcceptance"/> is required (no default, so no construction can silently be
/// Release A). In Release B, or with an invalid policy, every write scope applies <see cref="ContentAcceptanceWriteFence"/> before
/// it writes any content-acceptance entity.
/// </para>
/// </summary>
public sealed class AuthorityStore(IDbContextFactory<AuthorityDbContext> contextFactory, ContentAcceptancePolicy contentAcceptance, AuthorityWorkSignal? workSignal = null, AuthorityWorkOptions? workOptions = null, ISecurityAlertSink? alerts = null,
    IJournalReceiptVerifier? journalReceipts = null) : IAuthorityStore
{
    private readonly AuthorityWorkSignal? wakeup = workOptions?.CommitWakeupEnabled == true ? workSignal : null;
    private readonly ContentAcceptancePolicy policy = contentAcceptance ?? throw new ArgumentNullException(nameof(contentAcceptance));

    // SEC-BL-01 E-6/E-7 (A-7 §27): without a composed verifier nothing counts as delivered (fail closed).
    private readonly IJournalReceiptVerifier receipts = journalReceipts ?? NotComposedJournalReceiptVerifier.Instance;

    public async Task<IAuthorityScope> BeginScopeAsync(CancellationToken cancellationToken)
    {
        var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        return new AuthorityScope(context, transaction, wakeup, alerts, policy, receipts);
    }

    public async Task<IAuthorityScope> BeginReadOnlySnapshotScopeAsync(CancellationToken cancellationToken)
    {
        var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
        await context.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken).ConfigureAwait(false);
        return new AuthorityScope(context, transaction, null, alerts, policy, receipts);
    }

    public async Task<IReadOnlyList<Guid>> GetOrganizationsWithPendingWorkAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var projection = context.OrganizationStates.Where(o => o.ProjectionAppliedRevision < o.AuthorizationRevision && !o.IntegrityQuarantineEnabled).Select(o => o.OrganizationId);
        var archive = context.JournalEntries.Where(j => j.ArchiveDeliveryState == ArchiveDeliveryStates.Pending).Select(j => j.OrganizationId);
        return await projection.Union(archive).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Guid>> GetAllOrganizationIdsAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.OrganizationStates.AsNoTracking().OrderBy(o => o.OrganizationId).Select(o => o.OrganizationId).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<OrganizationChangeMarker>> GetOrganizationChangeMarkersAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await context.OrganizationStates.AsNoTracking().OrderBy(o => o.OrganizationId)
            .Select(o => new OrganizationChangeMarker(o.OrganizationId, o.AuthorizationRevision, o.ContentIntegrityRevision, o.AuditJournalRevision))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed class AuthorityScope(AuthorityDbContext context, IDbContextTransaction transaction, AuthorityWorkSignal? wakeup, ISecurityAlertSink? alerts, ContentAcceptancePolicy policy,
        IJournalReceiptVerifier journalReceiptVerifier) : IAuthorityScope, IAuditAccessScope, ISecurityCommandTimelineScope, ISecurityOverviewScope
    {
        private bool committed;
        private bool authorizationWorkAppended;
        private const string FenceSavepoint = "content_acceptance_fence";
        private bool contentFenceVerified;
        private bool dataKeyLedgerVerified;
        private Guid lockedOrganizationId;
        private SearchIndexStore? searchIndex;

        public async Task RequireContentAcceptanceFloorAsync(CancellationToken cancellationToken)
        {
            if (!policy.GuardsContentWrites || contentFenceVerified)
            {
                return; // Release A; or verified earlier in this transaction, whose lock still holds
            }

            string? failure;
            if (!policy.IsValid)
            {
                failure = ContentAcceptanceFloorFailures.ConfigurationPolicyInvalid;
            }
            else
            {
                // A savepoint keeps the transaction usable if the catalog cannot be read (so a caller can still record its refusal).
                // On success it stays open: the lock taken inside it belongs to the transaction until commit or rollback.
                await transaction.CreateSavepointAsync(FenceSavepoint, cancellationToken).ConfigureAwait(false);
                try
                {
                    // The locks first, then the facts in a later statement (READ COMMITTED: a fresh snapshot after the locks are granted).
                    // A-1 (F-E1): the history before the operations table, the order every EF migration uses, so no lock cycle can form.
                    await context.Database.ExecuteSqlRawAsync(ContentAcceptanceWriteFence.HistoryLockSql, cancellationToken).ConfigureAwait(false);
                    await context.Database.ExecuteSqlRawAsync(ContentAcceptanceWriteFence.LockSql, cancellationToken).ConfigureAwait(false);
                    var facts = await ContentAcceptanceWriteFence.ReadAsync(context.Database.GetDbConnection(), transaction.GetDbTransaction(), policy.RequiredFloor!.Value, cancellationToken).ConfigureAwait(false);
                    failure = ContentAcceptanceWriteFence.Evaluate(facts, policy.RequiredFloor.Value);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failure = ContentAcceptanceFloorFailures.FloorUnverifiable; // permission, connection, timeout, missing catalog object: fail closed
                    try
                    {
                        await transaction.RollbackToSavepointAsync(FenceSavepoint, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception rollback) when (rollback is not OperationCanceledException)
                    {
                        // The connection itself failed; the scope's disposal rolls back. The refusal below stands either way.
                    }
                }
            }

            if (failure is not null)
            {
                alerts?.Raise(new SecurityAlert(ContentAcceptanceFloorFailures.AlertKind, "Critical", lockedOrganizationId, Guid.Empty, null,
                    $"content write refused: reason={failure} category={ContentAcceptanceFloorFailures.CategoryOf(failure)} recovery={ContentAcceptanceFloorFailures.RecoveryOf(failure)} {policy.Describe()} build={ContentAcceptanceRules.CurrentVersion}",
                    Guid.Empty));
                throw ContentAcceptanceFloorFailures.Refusal(); // the transaction is rolled back when the scope is disposed
            }

            contentFenceVerified = true;
        }

        /// <summary>
        /// E3-M2e L-13: verifies the data-key ledger's unique index and its P-1/P-2 triggers in this transaction, after a ROW
        /// EXCLUSIVE lock on the ledger that conflicts with every statement able to drop, replace or disable them until the
        /// transaction ends. Once per transaction. Any failure (including a missing table or an unreadable catalog) refuses with
        /// <c>503 CONTENT_DATA_KEY_LEDGER_NOT_ENFORCED</c> and an alert; nothing is written (the transaction is rolled back on disposal).
        /// </summary>
        public async Task RequireDataKeyLedgerAsync(CancellationToken cancellationToken)
        {
            if (dataKeyLedgerVerified)
            {
                return;
            }

            string? failure;
            try
            {
                await context.Database.ExecuteSqlRawAsync(ContentDataKeyLedgerSql.LockSql, cancellationToken).ConfigureAwait(false);
                var facts = await ContentDataKeyLedgerSql.ReadAsync(context.Database.GetDbConnection(), transaction.GetDbTransaction(), cancellationToken).ConfigureAwait(false);
                failure = ContentDataKeyLedgerSql.Evaluate(facts);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failure = ContentDataKeyLedgerSql.Failures.Unverifiable;
            }

            if (failure is not null)
            {
                alerts?.Raise(new SecurityAlert(DataKeyLedgerFailures.AlertKind, "Critical", lockedOrganizationId, Guid.Empty, null,
                    $"data-key ledger not enforced: reason={failure} build={ContentAcceptanceRules.CurrentVersion}", Guid.Empty));
                throw DataKeyLedgerFailures.NotEnforced();
            }

            dataKeyLedgerVerified = true;
        }

        /// <summary>
        /// RB-CARV-1 catch-all: true when the tracked changes would write content-acceptance state. Only two protective changes are
        /// exempt, so a refused fence never stops an abort or a quarantine: a Prepared operation becoming Aborted (state and abort
        /// reason only) and a head becoming quarantined (quarantine fields and modification time only).
        /// </summary>
        private bool HasContentAcceptanceWrites()
        {
            context.ChangeTracker.DetectChanges();
            foreach (var entry in context.ChangeTracker.Entries())
            {
                if (entry.State is EntityState.Unchanged or EntityState.Detached)
                {
                    continue;
                }

                switch (entry.Entity)
                {
                    case ContentChangeOperation operation:
                        if (entry.State == EntityState.Modified
                            && operation.OperationStateName == ContentOperationStates.Aborted
                            && Equals(entry.Property(nameof(ContentChangeOperation.OperationStateName)).OriginalValue, ContentOperationStates.Prepared)
                            && OnlyModified(entry, nameof(ContentChangeOperation.OperationStateName), nameof(ContentChangeOperation.OperationQuarantineReasonCode)))
                        {
                            continue;
                        }

                        return true;
                    case RecordContentHead head:
                        if (entry.State == EntityState.Modified && head.ContentHeadQuarantined
                            && OnlyModified(entry, nameof(RecordContentHead.ContentHeadQuarantined), nameof(RecordContentHead.QuarantineReasonCode), nameof(RecordContentHead.ContentHeadModifiedOn)))
                        {
                            continue;
                        }

                        return true;
                    case RecordManifestCommit or RecordManifestLeaf or ContentItemIdentity or ContentCandidateLeaf or ContentIntegrityCheckpoint or ContentDataKeyBinding:
                        return true; // E3-M2e (L-11): a data-key binding is content-acceptance state
                }
            }

            return false;
        }

        private static bool OnlyModified(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry, params string[] allowed) =>
            entry.Properties.Where(p => p.IsModified).All(p => Array.IndexOf(allowed, p.Metadata.Name) >= 0);

        /// <summary>IX-1: the search index on this scope's context and transaction.</summary>
        public ISearchIndexStore SearchIndex => searchIndex ??= new SearchIndexStore(context);

        private SearchAccessStore? searchAccess;

        public ISearchAccessStore SearchAccess => searchAccess ??= new SearchAccessStore(context);

        public async Task<OrganizationAuthorizationState?> LockOrganizationAsync(Guid organizationId, CancellationToken cancellationToken)
        {
            lockedOrganizationId = organizationId; // diagnostics of a refused content write only
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var state = await context.OrganizationStates.FromSqlInterpolated($"SELECT * FROM authority.organization_authorization_states WHERE organization_id = {organizationId} FOR UPDATE")
                .AsTracking().FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            OrganizationLockMetrics.RecordWait(System.Diagnostics.Stopwatch.GetElapsedTime(started)); // C8 M4-2 (AC-M4-2.6): observe audit-read impact on commands
            return state;
        }

        /// <summary>C8 M4-2 (AC-8.3): bounds the audit-access transaction; transaction_timeout only where the server has it (PostgreSQL 17+).</summary>
        public Task ApplyAuditAccessTimeoutsAsync(int statementTimeoutMilliseconds, int transactionTimeoutMilliseconds, CancellationToken cancellationToken)
        {
            var statement = statementTimeoutMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + "ms";
            var transactionBound = transactionTimeoutMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + "ms";
            return context.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT set_config('statement_timeout', {statement}, true), CASE WHEN current_setting('server_version_num')::int >= 170000 THEN set_config('transaction_timeout', {transactionBound}, true) END",
                cancellationToken);
        }

        /// <summary>C8 M4-2 (AC-8.3): FOR UPDATE NOWAIT; lock_not_available (55P03) is reported as busy, never waited on.</summary>
        public async Task<OrganizationLockAttempt> TryLockOrganizationNoWaitAsync(Guid organizationId, CancellationToken cancellationToken)
        {
            lockedOrganizationId = organizationId;
            try
            {
                var state = await context.OrganizationStates.FromSqlInterpolated($"SELECT * FROM authority.organization_authorization_states WHERE organization_id = {organizationId} FOR UPDATE NOWAIT")
                    .AsTracking().FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                return state is null ? OrganizationLockAttempt.NotFound : OrganizationLockAttempt.Locked(state);
            }
            catch (Exception ex) when (IsLockNotAvailable(ex))
            {
                return OrganizationLockAttempt.LockBusy; // the transaction is aborted; the caller disposes the scope
            }
        }

        /// <summary>C8 M4-4 (AC-8.4): count and oldest creation time per state, aggregated in SQL for the given states only.</summary>
        public async Task<List<CommandStateTally>> CountCommandsByStateAsync(Guid organizationId, IReadOnlyCollection<string> states, CancellationToken cancellationToken)
        {
            var stateList = states.ToList();
            var rows = await context.SecurityCommands.AsNoTracking()
                .Where(c => c.OrganizationId == organizationId && stateList.Contains(c.CommandStateName))
                .GroupBy(c => c.CommandStateName)
                .Select(g => new { State = g.Key, Count = g.Count(), Oldest = g.Min(c => c.CommandCreatedOn) })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            return [.. rows.Select(r => new CommandStateTally(r.State, r.Count, r.Oldest))];
        }

        /// <summary>C8 M4-4 (AC-8.4): the oldest commands in the given states, ordered and limited in SQL.</summary>
        public Task<List<SecurityCommand>> GetOldestCommandsInStatesAsync(Guid organizationId, IReadOnlyCollection<string> states, int limit, CancellationToken cancellationToken)
        {
            var stateList = states.ToList();
            return context.SecurityCommands.AsNoTracking()
                .Where(c => c.OrganizationId == organizationId && stateList.Contains(c.CommandStateName))
                .OrderBy(c => c.CommandCreatedOn).ThenBy(c => c.SecurityCommandId).Take(limit)
                .ToListAsync(cancellationToken);
        }

        /// <summary>C8 M4-4 (AC-8.4): journal entries per (stream, delivery state), aggregated in SQL; no body, digest or receipt is read.</summary>
        public async Task<List<ArchiveDeliveryTally>> CountJournalArchiveStatesAsync(Guid organizationId, CancellationToken cancellationToken)
        {
            var rows = await context.JournalEntries.AsNoTracking()
                .Where(j => j.OrganizationId == organizationId)
                .GroupBy(j => new { j.JournalStreamName, j.ArchiveDeliveryState })
                .Select(g => new { g.Key.JournalStreamName, g.Key.ArchiveDeliveryState, Count = g.Count(), Oldest = g.Min(j => j.JournalCreatedOn), LatestArchived = g.Max(j => j.JournalArchivedOn) })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            return [.. rows.Select(r => new ArchiveDeliveryTally(r.JournalStreamName, r.ArchiveDeliveryState, r.Count, r.Oldest, r.LatestArchived))];
        }

        /// <summary>
        /// C8 M4-3 (AC-8.2): command rows newest first after the keyset position, filtered and limited in SQL
        /// (index <c>ix_security_commands_org_created</c>). A command-row position uses a row comparison on (time, id).
        /// </summary>
        public Task<List<SecurityCommand>> GetTimelineCommandsAsync(Guid organizationId, TimelineRange range, IReadOnlyCollection<string> kinds, IReadOnlyCollection<string>? states, int limit, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(range);
            var kindList = kinds.ToList();
            IQueryable<SecurityCommand> query = range.After is { IsCommand: true } commandPosition
                ? context.SecurityCommands.FromSqlInterpolated($"SELECT * FROM authority.security_commands WHERE organization_id = {organizationId} AND (command_created_on, security_command_id) < ({commandPosition.OccurredOn}, {commandPosition.CommandId})")
                : context.SecurityCommands.Where(c => c.OrganizationId == organizationId);
            query = query.AsNoTracking().Where(c => kindList.Contains(c.CommandKindName));
            if (range.After is { IsCommand: false } entryPosition)
            {
                var at = entryPosition.OccurredOn;
                query = query.Where(c => c.CommandCreatedOn < at); // command rows rank above audit entries at an equal time
            }

            if (states is not null)
            {
                var stateList = states.ToList();
                query = query.Where(c => stateList.Contains(c.CommandStateName));
            }

            if (range.From is { } from)
            {
                query = query.Where(c => c.CommandCreatedOn >= from);
            }

            if (range.To is { } to)
            {
                query = query.Where(c => c.CommandCreatedOn < to);
            }

            return query.OrderByDescending(c => c.CommandCreatedOn).ThenByDescending(c => c.SecurityCommandId).Take(limit).ToListAsync(cancellationToken);
        }

        /// <summary>C8 M4-3 (AC-8.2): allowlisted audit-stream entries newest first after the keyset position (index <c>ix_journal_org_created</c>).</summary>
        public Task<List<SecurityJournalEntry>> GetTimelineAuditEntriesAsync(Guid organizationId, TimelineRange range, IReadOnlyCollection<string> kinds, int limit, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(range);
            var kindList = kinds.ToList();
            var query = context.JournalEntries.AsNoTracking()
                .Where(e => e.OrganizationId == organizationId && e.JournalStreamName == JournalStreamNames.Audit && kindList.Contains(e.JournalEventKind));
            if (range.After is { IsCommand: true } commandPosition)
            {
                var at = commandPosition.OccurredOn;
                query = query.Where(e => e.JournalCreatedOn <= at); // audit entries rank below command rows at an equal time
            }
            else if (range.After is { } entryPosition)
            {
                var at = entryPosition.OccurredOn;
                var revision = entryPosition.StreamRevision;
                query = query.Where(e => e.JournalCreatedOn < at || (e.JournalCreatedOn == at && e.JournalStreamRevision < revision));
            }

            if (range.From is { } from)
            {
                query = query.Where(e => e.JournalCreatedOn >= from);
            }

            if (range.To is { } to)
            {
                query = query.Where(e => e.JournalCreatedOn < to);
            }

            return query.OrderByDescending(e => e.JournalCreatedOn).ThenByDescending(e => e.JournalStreamRevision).Take(limit).ToListAsync(cancellationToken);
        }

        /// <summary>C8 M4-3: authorization-stream entries at the given revisions (unique index <c>uq_journal_stream_revision</c>).</summary>
        public Task<List<SecurityJournalEntry>> GetAuthorizationEntriesAsync(Guid organizationId, IReadOnlyCollection<long> streamRevisions, CancellationToken cancellationToken)
        {
            var revisions = streamRevisions.ToList();
            return context.JournalEntries.AsNoTracking()
                .Where(e => e.OrganizationId == organizationId && e.JournalStreamName == JournalStreamNames.Authorization && revisions.Contains(e.JournalStreamRevision))
                .ToListAsync(cancellationToken);
        }

        /// <summary>
        /// C8 M4-3: rejection entries naming one of the commands, inside the bounded time window (index <c>ix_journal_org_created</c>);
        /// only the named command id (<c>body.security_command_id</c> of the journal envelope) is compared in SQL, the body is otherwise
        /// not interpreted here.
        /// </summary>
        public Task<List<SecurityJournalEntry>> GetRejectionEntriesAsync(Guid organizationId, IReadOnlyCollection<string> kinds, IReadOnlyCollection<Guid> commandIds, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken)
        {
            var kindArray = kinds.ToArray();
            var idArray = commandIds.Select(id => id.ToString("D")).ToArray();
            return context.JournalEntries.FromSqlInterpolated($"""
                SELECT * FROM authority.security_journal_entries
                WHERE organization_id = {organizationId} AND journal_stream_name = {JournalStreamNames.Audit}
                  AND journal_created_on >= {windowStart} AND journal_created_on <= {windowEnd}
                  AND journal_event_kind = ANY({kindArray})
                  AND (journal_event_body_json::jsonb -> 'body' ->> 'security_command_id') = ANY({idArray})
                """).AsNoTracking().Take(commandIds.Count * 2).ToListAsync(cancellationToken);
        }

        private static bool IsLockNotAvailable(Exception? exception)
        {
            for (var e = exception; e is not null; e = e.InnerException)
            {
                if (e is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.LockNotAvailable })
                {
                    return true;
                }
            }

            return false;
        }

        public Task<OrganizationAuthorizationState?> ReadOrganizationAsync(Guid organizationId, CancellationToken cancellationToken) =>
            context.OrganizationStates.AsNoTracking().FirstOrDefaultAsync(o => o.OrganizationId == organizationId, cancellationToken);

        public void AddOrganization(OrganizationAuthorizationState state) => context.OrganizationStates.Add(state);

        public Task<IdentityBinding?> FindIdentityBindingAsync(Guid organizationId, string issuerUri, string subjectIdentifier, CancellationToken cancellationToken) =>
            context.IdentityBindings.AsNoTracking().FirstOrDefaultAsync(b => b.OrganizationId == organizationId && b.IdentityIssuerUri == issuerUri && b.IdentitySubjectIdentifier == subjectIdentifier, cancellationToken);

        public Task<IdentityBinding?> FindIdentityBindingByUserAsync(Guid organizationId, Guid authorityUserId, CancellationToken cancellationToken) =>
            context.IdentityBindings.AsNoTracking().FirstOrDefaultAsync(b => b.OrganizationId == organizationId && b.AuthorityUserId == authorityUserId, cancellationToken);

        public Task<List<IdentityBinding>> GetIdentityBindingsAsync(Guid organizationId, CancellationToken cancellationToken) =>
            context.IdentityBindings.AsNoTracking().Where(b => b.OrganizationId == organizationId).ToListAsync(cancellationToken);

        public Task<IdentityBinding?> FindIdentityBindingByIdAsync(Guid identityBindingId, CancellationToken cancellationToken) =>
            context.IdentityBindings.AsNoTracking().FirstOrDefaultAsync(b => b.IdentityBindingId == identityBindingId, cancellationToken);

        public void AddIdentityBinding(IdentityBinding binding) => context.IdentityBindings.Add(binding);

        public Task<IdentityBinding?> FindIdentityBindingForUpdateAsync(Guid organizationId, Guid authorityUserId, CancellationToken cancellationToken) =>
            context.IdentityBindings.AsTracking().FirstOrDefaultAsync(b => b.OrganizationId == organizationId && b.AuthorityUserId == authorityUserId, cancellationToken);

        public Task<List<AuthenticatedSession>> GetUnrevokedSessionsAsync(Guid identityBindingId, CancellationToken cancellationToken) =>
            context.Sessions.AsTracking().Where(s => s.IdentityBindingId == identityBindingId && s.SessionRevokedOn == null).ToListAsync(cancellationToken);

        public Task<OrganizationRoleChangeProposal?> FindRoleChangeProposalAsync(Guid organizationId, Guid proposalId, CancellationToken cancellationToken) =>
            context.RoleChangeProposals.AsTracking().FirstOrDefaultAsync(p => p.OrganizationId == organizationId && p.OrganizationRoleChangeProposalId == proposalId, cancellationToken);

        public Task<List<OrganizationRoleChangeProposal>> GetRoleChangeProposalsAsync(Guid organizationId, DateTimeOffset createdSince, CancellationToken cancellationToken) =>
            context.RoleChangeProposals.AsNoTracking().Where(p => p.OrganizationId == organizationId && p.ProposalCreatedOn >= createdSince).OrderByDescending(p => p.ProposalCreatedOn).ToListAsync(cancellationToken);

        public void AddRoleChangeProposal(OrganizationRoleChangeProposal proposal) => context.RoleChangeProposals.Add(proposal);

        public Task<OrganizationMembershipRequest?> FindMembershipRequestAsync(Guid organizationId, Guid requestId, CancellationToken cancellationToken) =>
            context.MembershipRequests.AsTracking().FirstOrDefaultAsync(r => r.OrganizationId == organizationId && r.OrganizationMembershipRequestId == requestId, cancellationToken);

        public Task<List<OrganizationMembershipRequest>> GetMembershipRequestsAsync(Guid organizationId, DateTimeOffset createdSince, CancellationToken cancellationToken) =>
            context.MembershipRequests.AsNoTracking().Where(r => r.OrganizationId == organizationId && r.RequestCreatedOn >= createdSince).OrderByDescending(r => r.RequestCreatedOn).ToListAsync(cancellationToken);

        public void AddMembershipRequest(OrganizationMembershipRequest request) => context.MembershipRequests.Add(request);

        public Task<AuthorityResource?> FindResourceAsync(Guid organizationId, string resourceTypeName, Guid resourceEntityId, CancellationToken cancellationToken) =>
            context.Resources.FirstOrDefaultAsync(r => r.OrganizationId == organizationId && r.ResourceTypeName == resourceTypeName && r.ResourceEntityId == resourceEntityId, cancellationToken);

        public void AddResource(AuthorityResource resource) => context.Resources.Add(resource);

        public Task<List<AuthorityResource>> GetActiveResourcesByParentAsync(Guid organizationId, Guid parentResourceEntityId, IReadOnlyCollection<string> resourceTypeNames, CancellationToken cancellationToken) =>
            context.Resources.AsNoTracking().Where(r => r.OrganizationId == organizationId && r.ParentResourceEntityId == parentResourceEntityId && resourceTypeNames.Contains(r.ResourceTypeName) && r.ResourceLifecycleState == ResourceLifecycleStates.Active).ToListAsync(cancellationToken);

        // C10 M1-WP1: share-specific metadata stored alongside the generic AuthorityResource.

        public Task<ShareResourceMetadata?> FindShareMetadataAsync(Guid organizationId, Guid recordShareId, CancellationToken cancellationToken) =>
            context.ShareResourceMetadatas.FirstOrDefaultAsync(s => s.OrganizationId == organizationId && s.RecordShareId == recordShareId, cancellationToken);

        public void AddShareMetadata(ShareResourceMetadata metadata) => context.ShareResourceMetadatas.Add(metadata);

        // C10 M1-WP5: query descendants using the parent-lineage index for cascade invalidation.
        public Task<List<ShareResourceMetadata>> FindShareMetadataByParentAsync(Guid organizationId, Guid parentRecordShareId, CancellationToken cancellationToken) =>
            context.ShareResourceMetadatas.Where(s => s.OrganizationId == organizationId && s.ParentRecordShareId == parentRecordShareId).ToListAsync(cancellationToken);

        // C10 M1-WP2: the access-time expiry overlay's reads (no tracking: a decision never changes them).
        public Task<List<ShareResourceMetadata>> GetActiveShareMetadataWithExpiryAsync(Guid organizationId, CancellationToken cancellationToken) =>
            context.ShareResourceMetadatas.AsNoTracking()
                .Where(m => m.OrganizationId == organizationId && m.ShareExpiresOn != null &&
                            context.Resources.Any(r => r.OrganizationId == organizationId && r.ResourceTypeName == ResourceTypeNames.RecordShare &&
                                                       r.ResourceEntityId == m.RecordShareId && r.ResourceLifecycleState == ResourceLifecycleStates.Active))
                .ToListAsync(cancellationToken);

        public Task<List<AuthorityRelationshipEdge>> GetActiveShareEdgesAsync(Guid organizationId, IReadOnlyCollection<Guid> recordShareIds, CancellationToken cancellationToken) =>
            context.RelationshipEdges.AsNoTracking()
                .Where(e => e.OrganizationId == organizationId && e.RemovedBySecurityCommandId == null &&
                            ((e.ResourceTypeName == ResourceTypeNames.RecordShare && recordShareIds.Contains(e.ResourceEntityId)) ||
                             (e.SubjectTypeName == ResourceTypeNames.RecordShare && recordShareIds.Contains(e.SubjectEntityId))))
                .ToListAsync(cancellationToken);

        public Task<List<SecurityCommand>> GetRecordTargetedCommandsAsync(Guid organizationId, Guid formRecordId, int limit, CancellationToken cancellationToken) =>
            context.SecurityCommands.AsNoTracking()
                .Where(c => c.OrganizationId == organizationId && c.TargetFormRecordId == formRecordId && c.CommandStateName != SecurityCommandStates.Rejected)
                .OrderByDescending(c => c.CommandCreatedOn).ThenByDescending(c => c.SecurityCommandId)
                .Take(limit)
                .ToListAsync(cancellationToken);

        public async Task<HashSet<Guid>> GetAuthorizationJournalCommandIdsAsync(Guid organizationId, IReadOnlyCollection<Guid> securityCommandIds, CancellationToken cancellationToken) =>
            (await context.JournalEntries.AsNoTracking()
                .Where(e => e.OrganizationId == organizationId && e.JournalStreamName == JournalStreamNames.Authorization && e.SecurityCommandId != null && securityCommandIds.Contains(e.SecurityCommandId.Value))
                .Select(e => e.SecurityCommandId!.Value)
                .ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();

        public async Task<List<AuthorityRelationshipEdge>> GetRecordSecurityEdgeHistoryAsync(Guid organizationId, Guid formRecordId, CancellationToken cancellationToken)
        {
            var recordEdges = await context.RelationshipEdges.AsNoTracking()
                .Where(e => e.OrganizationId == organizationId && e.ResourceTypeName == ResourceTypeNames.FormRecord && e.ResourceEntityId == formRecordId &&
                            (e.RelationshipName == "owner" || e.RelationshipName == "shared_via"))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var shareIds = recordEdges.Where(e => e.RelationshipName == "shared_via" && e.SubjectTypeName == ResourceTypeNames.RecordShare).Select(e => e.SubjectEntityId).Distinct().ToList();
            if (shareIds.Count == 0)
            {
                return recordEdges;
            }

            var shareEdges = await context.RelationshipEdges.AsNoTracking()
                .Where(e => e.OrganizationId == organizationId &&
                            ((e.ResourceTypeName == ResourceTypeNames.RecordShare && shareIds.Contains(e.ResourceEntityId)) ||
                             (e.SubjectTypeName == ResourceTypeNames.RecordShare && shareIds.Contains(e.SubjectEntityId) && e.ResourceTypeName != ResourceTypeNames.FormRecord)))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            return [.. recordEdges, .. shareEdges];
        }

        public Task<List<AuthorityRelationshipEdge>> GetActiveTeamMemberEdgesAsync(Guid organizationId, IReadOnlyCollection<Guid> teamIds, CancellationToken cancellationToken) =>
            context.RelationshipEdges.AsNoTracking()
                .Where(e => e.OrganizationId == organizationId && e.RemovedBySecurityCommandId == null && e.ResourceTypeName == ResourceTypeNames.Team &&
                            e.RelationshipName == "member" && teamIds.Contains(e.ResourceEntityId))
                .ToListAsync(cancellationToken);

        public Task<OrganizationProtectionPolicy?> FindProtectionPolicyAsync(Guid organizationId, CancellationToken cancellationToken) =>
            context.ProtectionPolicies.FirstOrDefaultAsync(p => p.OrganizationId == organizationId, cancellationToken);

        public void AddProtectionPolicy(OrganizationProtectionPolicy policy) => context.ProtectionPolicies.Add(policy);

        public Task<List<AuthorityRelationshipEdge>> GetActiveEdgesAsync(Guid organizationId, CancellationToken cancellationToken) =>
            context.RelationshipEdges.Where(e => e.OrganizationId == organizationId && e.RemovedBySecurityCommandId == null).ToListAsync(cancellationToken);

        public void AddEdge(AuthorityRelationshipEdge edge) => context.RelationshipEdges.Add(edge);

        public Task<List<AuthorityRelationshipEdge>> GetRelationshipEdgeHistoryAsync(Guid organizationId, string resourceTypeName, Guid resourceEntityId, string relationshipName, string subjectTypeName, Guid subjectEntityId, CancellationToken cancellationToken) =>
            context.RelationshipEdges.AsNoTracking()
                .Where(e => e.OrganizationId == organizationId && e.ResourceTypeName == resourceTypeName && e.ResourceEntityId == resourceEntityId && e.RelationshipName == relationshipName && e.SubjectTypeName == subjectTypeName && e.SubjectEntityId == subjectEntityId)
                .ToListAsync(cancellationToken);

        public async Task<Dictionary<Guid, long?>> GetCommittedRevisionsAsync(Guid organizationId, IReadOnlyCollection<Guid> securityCommandIds, CancellationToken cancellationToken) =>
            await context.SecurityCommands.AsNoTracking()
                .Where(c => c.OrganizationId == organizationId && securityCommandIds.Contains(c.SecurityCommandId))
                .ToDictionaryAsync(c => c.SecurityCommandId, c => c.CommittedAuthorizationRevision, cancellationToken).ConfigureAwait(false);

        public Task<SecurityCommand?> FindCommandByIdempotencyKeyAsync(Guid organizationId, string idempotencyKey, CancellationToken cancellationToken) =>
            context.SecurityCommands.FirstOrDefaultAsync(c => c.OrganizationId == organizationId && c.CommandIdempotencyKey == idempotencyKey, cancellationToken);

        public Task<List<string>> GetCommandPayloadsAsync(Guid organizationId, IReadOnlyCollection<string> commandKinds, CancellationToken cancellationToken) =>
            context.SecurityCommands.AsNoTracking()
                .Where(c => c.OrganizationId == organizationId && commandKinds.Contains(c.CommandKindName) && c.CommandStateName != SecurityCommandStates.Rejected)
                .Select(c => c.CommandPayloadJson).ToListAsync(cancellationToken);

        public Task<SecurityCommand?> FindCommandAsync(Guid securityCommandId, CancellationToken cancellationToken) =>
            context.SecurityCommands.FirstOrDefaultAsync(c => c.SecurityCommandId == securityCommandId, cancellationToken);

        public void AddCommand(SecurityCommand command) => context.SecurityCommands.Add(command);

        public void AddOutboxEvent(ProjectionOutboxEvent outboxEvent) => context.ProjectionOutbox.Add(outboxEvent);

        public Task<ProjectionOutboxEvent?> FindOutboxEventAsync(Guid organizationId, long targetAuthorizationRevision, CancellationToken cancellationToken) =>
            context.ProjectionOutbox.FirstOrDefaultAsync(e => e.OrganizationId == organizationId && e.TargetAuthorizationRevision == targetAuthorizationRevision, cancellationToken);

        public void AddJournalEntry(SecurityJournalEntry entry)
        {
            context.JournalEntries.Add(entry);
            // C-150 R56-B: an authorization-stream entry is projection and archive work that keeps the organization gate closed until done.
            authorizationWorkAppended |= string.Equals(entry.JournalStreamName, JournalStreamNames.Authorization, StringComparison.Ordinal);
        }

        public Task<SecurityJournalEntry?> FindJournalEntryAsync(Guid organizationId, string streamName, long streamRevision, CancellationToken cancellationToken) =>
            context.JournalEntries.FirstOrDefaultAsync(j => j.OrganizationId == organizationId && j.JournalStreamName == streamName && j.JournalStreamRevision == streamRevision, cancellationToken);

        public Task<SecurityJournalEntry?> FindContentCommitJournalEntryAsync(Guid organizationId, Guid contentChangeOperationId, CancellationToken cancellationToken) =>
            context.JournalEntries
                .Where(j => j.OrganizationId == organizationId && j.JournalStreamName == JournalStreamNames.Content && j.ContentChangeOperationId == contentChangeOperationId && j.JournalEventKind == "ContentHeadCommitted")
                .OrderByDescending(j => j.JournalStreamRevision)
                .FirstOrDefaultAsync(cancellationToken);

        public Task<List<SecurityJournalEntry>> GetPendingJournalEntriesAsync(Guid organizationId, int limit, CancellationToken cancellationToken) =>
            context.JournalEntries.Where(j => j.OrganizationId == organizationId && j.ArchiveDeliveryState == ArchiveDeliveryStates.Pending)
                .OrderBy(j => j.JournalStreamName).ThenBy(j => j.JournalStreamRevision).Take(limit).ToListAsync(cancellationToken);

        // SEC-BL-01 WP-5a: the same query restricted to one stream or to every other stream (SELECT only; no grant change).
        public Task<List<SecurityJournalEntry>> GetPendingJournalEntriesByStreamAsync(Guid organizationId, string streamName, bool includeStream, int limit, CancellationToken cancellationToken) =>
            (includeStream
                ? context.JournalEntries.Where(j => j.OrganizationId == organizationId && j.ArchiveDeliveryState == ArchiveDeliveryStates.Pending && j.JournalStreamName == streamName)
                : context.JournalEntries.Where(j => j.OrganizationId == organizationId && j.ArchiveDeliveryState == ArchiveDeliveryStates.Pending && j.JournalStreamName != streamName))
            .OrderBy(j => j.JournalStreamName).ThenBy(j => j.JournalStreamRevision).Take(limit).ToListAsync(cancellationToken);

        public Task<int> CountPendingArchiveAsync(Guid organizationId, string streamName, CancellationToken cancellationToken) =>
            context.JournalEntries.CountAsync(j => j.OrganizationId == organizationId && j.JournalStreamName == streamName && j.ArchiveDeliveryState != ArchiveDeliveryStates.Anchored, cancellationToken);

        // SEC-BL-01 E-6 (A-7 §27 GT-1): the journal's own highest revision of the stream (index on organization, stream, revision); never a head column.
        public Task<SecurityJournalEntry?> FindLatestJournalEntryAsync(Guid organizationId, string streamName, CancellationToken cancellationToken) =>
            context.JournalEntries.Where(j => j.OrganizationId == organizationId && j.JournalStreamName == streamName).OrderByDescending(j => j.JournalStreamRevision).FirstOrDefaultAsync(cancellationToken);

        // SEC-BL-01 E-6/E-7: the composed trusted receipt verifier, or the fail-closed one.
        public IJournalReceiptVerifier JournalReceipts => journalReceiptVerifier;

        public Task<RecordContentHead?> FindContentHeadAsync(Guid organizationId, Guid formRecordId, bool lockForUpdate, CancellationToken cancellationToken) =>
            lockForUpdate
                ? context.ContentHeads.FromSqlInterpolated($"SELECT * FROM authority.record_content_heads WHERE organization_id = {organizationId} AND form_record_id = {formRecordId} FOR UPDATE").AsTracking().FirstOrDefaultAsync(cancellationToken)
                : context.ContentHeads.FirstOrDefaultAsync(h => h.OrganizationId == organizationId && h.FormRecordId == formRecordId, cancellationToken);

        public void AddContentHead(RecordContentHead head) => context.ContentHeads.Add(head);

        public Task<ContentChangeOperation?> FindOperationByIdempotencyKeyAsync(Guid organizationId, string idempotencyKey, CancellationToken cancellationToken) =>
            context.ContentOperations.FirstOrDefaultAsync(o => o.OrganizationId == organizationId && o.OperationIdempotencyKey == idempotencyKey, cancellationToken);

        public Task<ContentChangeOperation?> FindOperationAsync(Guid contentChangeOperationId, CancellationToken cancellationToken) =>
            context.ContentOperations.FirstOrDefaultAsync(o => o.ContentChangeOperationId == contentChangeOperationId, cancellationToken);

        public void AddOperation(ContentChangeOperation operation) => context.ContentOperations.Add(operation);

        public void AddCandidateLeaves(IEnumerable<ContentCandidateLeaf> leaves) => context.CandidateLeaves.AddRange(leaves);

        public Task<List<ContentCandidateLeaf>> GetCandidateLeavesAsync(Guid contentChangeOperationId, CancellationToken cancellationToken) =>
            context.CandidateLeaves.AsNoTracking().Where(l => l.ContentChangeOperationId == contentChangeOperationId).ToListAsync(cancellationToken);

        public void AddManifestCommit(RecordManifestCommit commit) => context.ManifestCommits.Add(commit);

        public void AddManifestLeaves(IEnumerable<RecordManifestLeaf> leaves) => context.ManifestLeaves.AddRange(leaves);

        public Task<List<ContentItemIdentity>> FindItemIdentitiesAsync(IReadOnlyCollection<Guid> manifestItemIds, CancellationToken cancellationToken)
        {
            var ids = manifestItemIds.Distinct().ToList();
            return context.ContentItemIdentities.AsNoTracking().Where(i => ids.Contains(i.ManifestItemId)).ToListAsync(cancellationToken);
        }

        public void AddItemIdentities(IEnumerable<ContentItemIdentity> identities) => context.ContentItemIdentities.AddRange(identities);

        public Task<List<ContentDataKeyBinding>> FindDataKeyBindingsAsync(IReadOnlyCollection<Guid> dataKeyIdentifiers, CancellationToken cancellationToken) =>
            dataKeyIdentifiers.Count == 0
                ? Task.FromResult(new List<ContentDataKeyBinding>())
                : context.ContentDataKeyBindings.AsNoTracking().Where(b => dataKeyIdentifiers.Contains(b.DataKeyIdentifier)).ToListAsync(cancellationToken);

        public Task<List<ContentDataKeyBinding>> GetDataKeyBindingsForOperationAsync(Guid contentChangeOperationId, CancellationToken cancellationToken) =>
            context.ContentDataKeyBindings.AsNoTracking().Where(b => b.BindingContentChangeOperationId == contentChangeOperationId).ToListAsync(cancellationToken);

        public void AddDataKeyBindings(IEnumerable<ContentDataKeyBinding> bindings) => context.ContentDataKeyBindings.AddRange(bindings);

        /// <summary>E3-M2f-2 AM-F1: <c>SELECT EXISTS (SELECT 1 FROM authority.content_data_key_bindings WHERE organization_id = @p)</c> on this scope's transaction (index <c>ix_content_data_key_bindings_record</c>).</summary>
        public Task<bool> OrganizationHasDataKeyBindingsAsync(Guid organizationId, CancellationToken cancellationToken) =>
            context.ContentDataKeyBindings.AnyAsync(b => b.OrganizationId == organizationId, cancellationToken);

        // ---- E3-M2f-3b capability-refusal evidence (T2 only; the SQL is defined and pinned in CapabilityRefusalEvidenceIndex) ----

        public Task SetCapabilityEvidenceLockTimeoutAsync(CancellationToken cancellationToken) =>
            context.Database.ExecuteSqlRawAsync(CapabilityRefusalEvidenceIndex.EvidenceLockTimeoutSql, cancellationToken);

        public async Task<DateTimeOffset> StartCapabilityEvidenceLookupAsync(CancellationToken cancellationToken)
        {
            await context.Database.ExecuteSqlRawAsync(CapabilityRefusalEvidenceIndex.EvidenceStatementTimeoutSql, cancellationToken).ConfigureAwait(false);
            await using var command = EvidenceCommand(CapabilityRefusalEvidenceIndex.DatabaseClockSql);
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return value switch
            {
                DateTime utc => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)),
                DateTimeOffset offset => offset.ToUniversalTime(),
                _ => throw new InvalidOperationException("clock_timestamp() returned no timestamp"),
            };
        }

        public async Task<bool> IsCapabilityRefusalEvidenceIndexUsableAsync(CancellationToken cancellationToken) =>
            (await CapabilityRefusalEvidenceIndex.InspectAsync(EvidenceConnection(), cancellationToken).ConfigureAwait(false)).State == CapabilityRefusalEvidenceIndexState.ValidMatching;

        public async Task<IReadOnlyList<CapabilityRefusalEvidenceRow>> GetNewestCapabilityRefusalEvidenceAsync(Guid organizationId, CancellationToken cancellationToken)
        {
            await using var command = EvidenceCommand(CapabilityRefusalEvidenceIndex.EvidenceLookupSql);
            command.Parameters.AddWithValue("organization_id", organizationId);
            var rows = new List<CapabilityRefusalEvidenceRow>(CapabilityRefusalEvidence.LookupLimit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new CapabilityRefusalEvidenceRow(reader.GetGuid(0), reader.GetInt64(1), reader.GetString(2), reader.GetFieldValue<byte[]>(3)));
            }

            return rows;
        }

        private Npgsql.NpgsqlConnection EvidenceConnection() => (Npgsql.NpgsqlConnection)context.Database.GetDbConnection();

        private Npgsql.NpgsqlCommand EvidenceCommand(string sql) =>
            new(sql, EvidenceConnection(), (Npgsql.NpgsqlTransaction)transaction.GetDbTransaction());

        public Task<List<Guid>> FindAcceptedItemIdsAsync(IReadOnlyCollection<Guid> manifestItemIds, CancellationToken cancellationToken)
        {
            var ids = manifestItemIds.Distinct().ToList();
            return context.ManifestLeaves.AsNoTracking().Where(l => ids.Contains(l.ManifestItemId)).Select(l => l.ManifestItemId).Distinct().ToListAsync(cancellationToken);
        }

        public Task<List<RecordManifestLeaf>> GetManifestLeavesAsync(Guid recordManifestCommitId, CancellationToken cancellationToken) =>
            context.ManifestLeaves.AsNoTracking().Where(l => l.RecordManifestCommitId == recordManifestCommitId).OrderBy(l => l.ManifestLeafPosition).ToListAsync(cancellationToken);

        public Task<RecordManifestCommit?> FindManifestCommitByOperationAsync(Guid organizationId, Guid formRecordId, Guid committedOperationId, CancellationToken cancellationToken) =>
            context.ManifestCommits.AsNoTracking().FirstOrDefaultAsync(c => c.OrganizationId == organizationId && c.FormRecordId == formRecordId && c.CommittedOperationId == committedOperationId, cancellationToken);

        public Task<List<RecordManifestCommit>> GetManifestCommitsAsync(Guid organizationId, Guid formRecordId, long afterRevision, int limit, CancellationToken cancellationToken) =>
            context.ManifestCommits.AsNoTracking()
                .Where(c => c.OrganizationId == organizationId && c.FormRecordId == formRecordId && c.CommittedContentRevision > afterRevision)
                .OrderBy(c => c.CommittedContentRevision)
                .Take(limit)
                .ToListAsync(cancellationToken);

        public Task<List<ContentChangeOperation>> GetOperationsAsync(IReadOnlyCollection<Guid> contentChangeOperationIds, CancellationToken cancellationToken) =>
            context.ContentOperations.AsNoTracking().Where(o => contentChangeOperationIds.Contains(o.ContentChangeOperationId)).ToListAsync(cancellationToken);

        public void AddContentCheckpoint(ContentIntegrityCheckpoint checkpoint) => context.ContentCheckpoints.Add(checkpoint);

        public Task<IntegrityKeyRegistration?> FindKeyRegistrationAsync(Guid organizationId, string keyPurposeName, CancellationToken cancellationToken) =>
            context.KeyRegistrations.AsNoTracking().FirstOrDefaultAsync(k => k.OrganizationId == organizationId && k.KeyPurposeName == keyPurposeName, cancellationToken);

        public Task<IntegrityKeyRegistration?> FindKeyRegistrationForUpdateAsync(Guid organizationId, string keyPurposeName, CancellationToken cancellationToken) =>
            context.KeyRegistrations.AsTracking().FirstOrDefaultAsync(k => k.OrganizationId == organizationId && k.KeyPurposeName == keyPurposeName, cancellationToken);

        public void AddKeyRegistration(IntegrityKeyRegistration registration) => context.KeyRegistrations.Add(registration);

        public Task<List<Guid>> GetActiveRecordIdsAsync(Guid organizationId, CancellationToken cancellationToken) =>
            context.Resources.AsNoTracking().Where(r => r.OrganizationId == organizationId && r.ResourceTypeName == ResourceTypeNames.FormRecord && r.ResourceLifecycleState == ResourceLifecycleStates.Active).Select(r => r.ResourceEntityId).ToListAsync(cancellationToken);

        public Task<List<AuthorityResource>> GetFormRecordResourcesAsync(Guid organizationId, Guid formDefinitionId, IReadOnlyCollection<string> lifecycleStates, CancellationToken cancellationToken) =>
            context.Resources.AsNoTracking()
                .Where(r => r.OrganizationId == organizationId && r.ResourceTypeName == ResourceTypeNames.FormRecord && r.ParentResourceEntityId == formDefinitionId && lifecycleStates.Contains(r.ResourceLifecycleState))
                .OrderBy(r => r.ResourceEntityId)
                .ToListAsync(cancellationToken);

        public Task<List<AuthorityResource>> GetResourcesAsync(Guid organizationId, string resourceTypeName, IReadOnlyCollection<Guid> resourceEntityIds, CancellationToken cancellationToken) =>
            context.Resources.AsNoTracking().Where(r => r.OrganizationId == organizationId && r.ResourceTypeName == resourceTypeName && resourceEntityIds.Contains(r.ResourceEntityId)).ToListAsync(cancellationToken);

        public Task<List<RecordContentHead>> GetContentHeadsAsync(Guid organizationId, IReadOnlyCollection<Guid> formRecordIds, CancellationToken cancellationToken) =>
            context.ContentHeads.AsNoTracking().Where(h => h.OrganizationId == organizationId && formRecordIds.Contains(h.FormRecordId)).ToListAsync(cancellationToken);

        public void AddCatalogueSnapshot(SearchCatalogueSnapshot snapshot, IEnumerable<SearchCatalogueSnapshotEntry> entries)
        {
            context.CatalogueSnapshots.Add(snapshot);
            context.CatalogueSnapshotEntries.AddRange(entries);
        }

        public Task<SearchCatalogueSnapshot?> FindCatalogueSnapshotAsync(Guid searchCatalogueSnapshotId, CancellationToken cancellationToken) =>
            context.CatalogueSnapshots.AsTracking().FirstOrDefaultAsync(s => s.SearchCatalogueSnapshotId == searchCatalogueSnapshotId, cancellationToken);

        public Task<List<SearchCatalogueSnapshotEntry>> GetCatalogueSnapshotEntriesAsync(Guid searchCatalogueSnapshotId, int offset, int? limit, CancellationToken cancellationToken)
        {
            var query = context.CatalogueSnapshotEntries.AsNoTracking().Where(e => e.SearchCatalogueSnapshotId == searchCatalogueSnapshotId).OrderBy(e => e.CatalogueEntryPosition).Skip(offset);
            return (limit is { } take ? query.Take(take) : query).ToListAsync(cancellationToken);
        }

        public Task<AuthenticatedSession?> FindSessionByDigestAsync(byte[] sessionSecretDigest, CancellationToken cancellationToken) =>
            context.Sessions.AsTracking().FirstOrDefaultAsync(s => s.SessionSecretDigest == sessionSecretDigest, cancellationToken);

        public void AddSession(AuthenticatedSession session) => context.Sessions.Add(session);

        public Task<PublishedFormVersion?> FindLatestPublishedFormVersionAsync(Guid organizationId, Guid formDefinitionId, CancellationToken cancellationToken) =>
            context.PublishedFormVersions.AsNoTracking().Where(v => v.OrganizationId == organizationId && v.FormDefinitionId == formDefinitionId).OrderByDescending(v => v.FormVersionNumber).FirstOrDefaultAsync(cancellationToken);

        public void AddPublishedFormVersion(PublishedFormVersion version) => context.PublishedFormVersions.Add(version);

        public async Task<List<PublishedFormVersion>> GetLatestPublishedFormVersionsAsync(Guid organizationId, CancellationToken cancellationToken) =>
            (await context.PublishedFormVersions.AsNoTracking().Where(v => v.OrganizationId == organizationId).ToListAsync(cancellationToken).ConfigureAwait(false))
                .GroupBy(v => v.FormDefinitionId).Select(g => g.MaxBy(v => v.FormVersionNumber)!).ToList();

        public Task<List<FieldSearchApproval>> GetFieldSearchApprovalsAsync(Guid organizationId, Guid? formDefinitionId, CancellationToken cancellationToken) =>
            context.FieldSearchApprovals.AsTracking().Where(a => a.OrganizationId == organizationId && (formDefinitionId == null || a.FormDefinitionId == formDefinitionId)).OrderBy(a => a.FieldSearchApprovalId).ToListAsync(cancellationToken);

        public void AddFieldSearchApproval(FieldSearchApproval approval) => context.FieldSearchApprovals.Add(approval);

        public Task<List<PublishedFormVersion>> GetPublishedFormVersionsAsync(Guid organizationId, Guid formDefinitionId, CancellationToken cancellationToken) =>
            context.PublishedFormVersions.AsNoTracking().Where(v => v.OrganizationId == organizationId && v.FormDefinitionId == formDefinitionId).OrderByDescending(v => v.FormVersionNumber).ToListAsync(cancellationToken);

        public Task<FieldFormBinding?> FindFieldFormBindingAsync(Guid organizationId, Guid fieldDefinitionId, CancellationToken cancellationToken) =>
            context.FieldFormBindings.AsNoTracking().FirstOrDefaultAsync(b => b.OrganizationId == organizationId && b.FieldDefinitionId == fieldDefinitionId, cancellationToken);

        public void AddFieldFormBinding(FieldFormBinding binding) => context.FieldFormBindings.Add(binding);

        public Task<FormVersionPublication?> FindPendingFormVersionPublicationAsync(Guid organizationId, Guid formDefinitionId, CancellationToken cancellationToken) =>
            context.FormVersionPublications.AsTracking().FirstOrDefaultAsync(p => p.OrganizationId == organizationId && p.FormDefinitionId == formDefinitionId && p.PublicationStateName == FormVersionPublicationStates.Pending, cancellationToken);

        public void AddFormVersionPublication(FormVersionPublication publication) => context.FormVersionPublications.Add(publication);

        public Task<List<SearchCatalogueSnapshotEntry>> GetCatalogueSnapshotEntriesForRecordsAsync(Guid searchCatalogueSnapshotId, IReadOnlyCollection<Guid> formRecordIds, CancellationToken cancellationToken) =>
            context.CatalogueSnapshotEntries.AsNoTracking().Where(e => e.SearchCatalogueSnapshotId == searchCatalogueSnapshotId && formRecordIds.Contains(e.FormRecordId)).ToListAsync(cancellationToken);

        public Task<List<AuthorityResource>> GetResourcesByTypeAsync(Guid organizationId, IReadOnlyCollection<string> resourceTypeNames, IReadOnlyCollection<string> lifecycleStates, CancellationToken cancellationToken) =>
            context.Resources.AsNoTracking()
                .Where(r => r.OrganizationId == organizationId && resourceTypeNames.Contains(r.ResourceTypeName) && lifecycleStates.Contains(r.ResourceLifecycleState))
                .OrderBy(r => r.ResourceEntityId)
                .ToListAsync(cancellationToken);

        public async Task<Dictionary<Guid, int>> CountManifestCommitsAsync(Guid organizationId, IReadOnlyCollection<Guid> formRecordIds, CancellationToken cancellationToken) =>
            formRecordIds.Count == 0
                ? []
                : await context.ManifestCommits.AsNoTracking()
                    .Where(c => c.OrganizationId == organizationId && formRecordIds.Contains(c.FormRecordId))
                    .GroupBy(c => c.FormRecordId)
                    .Select(g => new { FormRecordId = g.Key, Count = g.Count() })
                    .ToDictionaryAsync(g => g.FormRecordId, g => g.Count, cancellationToken).ConfigureAwait(false);

        public Task<List<ProtectionCensusResult>> GetProtectionCensusResultsAsync(Guid organizationId, Guid scopingSecurityCommandId, IReadOnlyCollection<Guid> formRecordIds, CancellationToken cancellationToken) =>
            context.ProtectionCensusResults.AsNoTracking()
                .Where(r => r.OrganizationId == organizationId && r.ScopingSecurityCommandId == scopingSecurityCommandId && formRecordIds.Contains(r.FormRecordId))
                .ToListAsync(cancellationToken);

        public void AddProtectionCensusResult(ProtectionCensusResult result) => context.ProtectionCensusResults.Add(result);

        public Task<ProtectionCensusResult?> FindProtectionCensusResultAsync(Guid scopingSecurityCommandId, Guid formRecordId, Guid committedOperationId, CancellationToken cancellationToken) =>
            context.ProtectionCensusResults.AsTracking().FirstOrDefaultAsync(r => r.ScopingSecurityCommandId == scopingSecurityCommandId && r.FormRecordId == formRecordId && r.CommittedOperationId == committedOperationId, cancellationToken);

        public void AddProtectionAttestation(ProtectionReconciliationAttestation attestation) => context.ProtectionAttestations.Add(attestation);

        public async Task<List<ProtectionReconciliationAttestation>> GetLatestProtectionAttestationsAsync(Guid organizationId, IReadOnlyCollection<Guid> scopingSecurityCommandIds, CancellationToken cancellationToken) =>
            scopingSecurityCommandIds.Count == 0
                ? []
                : (await context.ProtectionAttestations.AsNoTracking()
                    .Where(a => a.OrganizationId == organizationId && scopingSecurityCommandIds.Contains(a.ScopingSecurityCommandId))
                    .ToListAsync(cancellationToken).ConfigureAwait(false))
                    .GroupBy(a => a.ScopingSecurityCommandId)
                    .Select(g => g.MaxBy(a => a.AttestedAuditJournalRevision)!)
                    .ToList();

        // ---- C8 M2 Stages 1-2: Platform Operators ----

        public Task<PlatformJournalHead?> LockPlatformJournalAsync(CancellationToken cancellationToken) =>
            context.PlatformJournalHeads.FromSqlInterpolated($"SELECT * FROM authority.platform_journal_heads WHERE platform_journal_head_id = {PlatformJournalHead.SingletonId} FOR UPDATE")
                .AsTracking().FirstOrDefaultAsync(cancellationToken);

        public Task<PlatformJournalHead?> ReadPlatformJournalAsync(CancellationToken cancellationToken) =>
            context.PlatformJournalHeads.AsNoTracking().FirstOrDefaultAsync(h => h.PlatformJournalHeadId == PlatformJournalHead.SingletonId, cancellationToken);

        public void AddPlatformJournalHead(PlatformJournalHead head) => context.PlatformJournalHeads.Add(head);

        public void AddPlatformJournalEntry(PlatformJournalEntry entry) => context.PlatformJournalEntries.Add(entry);

        public Task<List<PlatformJournalEntry>> GetLatestPlatformJournalEntriesAsync(int limit, CancellationToken cancellationToken) =>
            context.PlatformJournalEntries.AsNoTracking().OrderByDescending(e => e.PlatformJournalRevision).Take(limit).ToListAsync(cancellationToken);

        public Task<List<PlatformJournalEntry>> GetPendingPlatformJournalEntriesAsync(int limit, CancellationToken cancellationToken) =>
            context.PlatformJournalEntries.AsTracking().Where(e => e.ArchiveDeliveryState == ArchiveDeliveryStates.Pending).OrderBy(e => e.PlatformJournalRevision).Take(limit).ToListAsync(cancellationToken);

        public Task<int> CountPendingPlatformArchiveAsync(CancellationToken cancellationToken) =>
            context.PlatformJournalEntries.CountAsync(e => e.ArchiveDeliveryState == ArchiveDeliveryStates.Pending, cancellationToken);

        public Task<PlatformOperator?> FindPlatformOperatorAsync(Guid platformOperatorId, CancellationToken cancellationToken) =>
            context.PlatformOperators.AsTracking().FirstOrDefaultAsync(o => o.PlatformOperatorId == platformOperatorId, cancellationToken);

        public Task<PlatformOperator?> FindPlatformOperatorByIdentityAsync(string issuerUri, string subjectIdentifier, CancellationToken cancellationToken) =>
            context.PlatformOperators.AsTracking().FirstOrDefaultAsync(o => o.OperatorIssuerUri == issuerUri && o.OperatorSubjectIdentifier == subjectIdentifier, cancellationToken);

        public Task<List<PlatformOperator>> GetPlatformOperatorsAsync(CancellationToken cancellationToken) =>
            context.PlatformOperators.AsTracking().OrderBy(o => o.OperatorProvisionedOn).ThenBy(o => o.PlatformOperatorId).ToListAsync(cancellationToken);

        public void AddPlatformOperator(PlatformOperator platformOperator) => context.PlatformOperators.Add(platformOperator);

        public Task<PlatformOperatorChangeProposal?> FindPlatformOperatorProposalAsync(Guid proposalId, CancellationToken cancellationToken) =>
            context.PlatformOperatorProposals.AsTracking().FirstOrDefaultAsync(p => p.PlatformOperatorChangeProposalId == proposalId, cancellationToken);

        public Task<List<PlatformOperatorChangeProposal>> GetPlatformOperatorProposalsAsync(DateTimeOffset createdSince, CancellationToken cancellationToken) =>
            context.PlatformOperatorProposals.AsTracking().Where(p => p.ProposalCreatedOn >= createdSince).OrderBy(p => p.ProposalCreatedOn).ToListAsync(cancellationToken);

        public void AddPlatformOperatorProposal(PlatformOperatorChangeProposal proposal) => context.PlatformOperatorProposals.Add(proposal);

        public Task<PlatformOperatorApproval?> FindPlatformApprovalAsync(Guid platformOperatorApprovalId, CancellationToken cancellationToken) =>
            context.PlatformOperatorApprovals.AsTracking().FirstOrDefaultAsync(a => a.PlatformOperatorApprovalId == platformOperatorApprovalId, cancellationToken);

        public Task<List<PlatformOperatorApproval>> GetPlatformApprovalsForRequestAsync(Guid organizationMembershipRequestId, CancellationToken cancellationToken) =>
            context.PlatformOperatorApprovals.AsTracking().Where(a => a.OrganizationMembershipRequestId == organizationMembershipRequestId).OrderBy(a => a.ApprovalRecordedOn).ToListAsync(cancellationToken);

        public Task<List<PlatformOperatorApproval>> GetPendingPlatformApprovalsForOperatorAsync(Guid platformOperatorId, CancellationToken cancellationToken) =>
            context.PlatformOperatorApprovals.AsTracking().Where(a => a.PlatformOperatorId == platformOperatorId && a.ApprovalStateName == PlatformApprovalStates.Pending).ToListAsync(cancellationToken);

        public async Task<List<PlatformOperatorApproval>> GetPlatformApprovalsForRequestsAsync(IReadOnlyCollection<Guid> organizationMembershipRequestIds, CancellationToken cancellationToken) =>
            organizationMembershipRequestIds.Count == 0
                ? []
                : await context.PlatformOperatorApprovals.AsNoTracking().Where(a => organizationMembershipRequestIds.Contains(a.OrganizationMembershipRequestId)).ToListAsync(cancellationToken).ConfigureAwait(false);

        public void AddPlatformApproval(PlatformOperatorApproval approval) => context.PlatformOperatorApprovals.Add(approval);

        public Task<bool> IsPlatformEvidenceUsedAsync(string evidenceUseIdentifier, byte[] evidenceUseSha256, CancellationToken cancellationToken) =>
            context.PlatformEvidenceUses.AnyAsync(e => e.EvidenceUseIdentifier == evidenceUseIdentifier || e.EvidenceUseSha256 == evidenceUseSha256, cancellationToken);

        public Task<PlatformOperatorEvidenceUse?> FindPlatformEvidenceUseAsync(Guid platformOperatorEvidenceUseId, CancellationToken cancellationToken) =>
            context.PlatformEvidenceUses.AsTracking().FirstOrDefaultAsync(e => e.PlatformOperatorEvidenceUseId == platformOperatorEvidenceUseId, cancellationToken);

        public void AddPlatformEvidenceUse(PlatformOperatorEvidenceUse evidenceUse) => context.PlatformEvidenceUses.Add(evidenceUse);

        public async Task<PlatformRequestStorePage> SearchMembershipRequestsAsync(PlatformMembershipRequestQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);
            var now = query.Now;
            var rows = from r in context.MembershipRequests.AsNoTracking()
                       join o in context.OrganizationStates.AsNoTracking() on r.OrganizationId equals o.OrganizationId
                       select new { Request = r, OrganizationDisplayName = o.OrganizationDisplayName };
            var pendingApprovals = context.PlatformOperatorApprovals.AsNoTracking().Where(a => a.ApprovalStateName == PlatformApprovalStates.Pending && a.ApprovalValidUntil > now);
            rows = query.Status switch
            {
                PlatformRequestFilters.NeedsDecision => rows.Where(x => x.Request.RequestStateName == MembershipRequestStates.Pending && x.Request.RequestExpiresOn > now),
                PlatformRequestFilters.AwaitingFirstApproval => rows.Where(x => x.Request.RequestStateName == MembershipRequestStates.Pending && x.Request.RequestExpiresOn > now
                                                                                     && !pendingApprovals.Any(a => a.OrganizationMembershipRequestId == x.Request.OrganizationMembershipRequestId)),
                PlatformRequestFilters.AwaitingSecondApproval => rows.Where(x => x.Request.RequestStateName == MembershipRequestStates.Pending && x.Request.RequestExpiresOn > now
                                                                                      && pendingApprovals.Any(a => a.OrganizationMembershipRequestId == x.Request.OrganizationMembershipRequestId)),
                PlatformRequestFilters.Approved => rows.Where(x => x.Request.RequestStateName == MembershipRequestStates.Approved),
                PlatformRequestFilters.Rejected => rows.Where(x => x.Request.RequestStateName == MembershipRequestStates.Rejected),
                PlatformRequestFilters.Expired => rows.Where(x => x.Request.RequestStateName == MembershipRequestStates.Expired || (x.Request.RequestStateName == MembershipRequestStates.Pending && x.Request.RequestExpiresOn <= now)),
                PlatformRequestFilters.CancelledOrReplaced => rows.Where(x => x.Request.RequestStateName == MembershipRequestStates.Cancelled || x.Request.RequestStateName == MembershipRequestStates.Replaced),
                _ => rows,
            };
            if (!string.IsNullOrWhiteSpace(query.Text))
            {
                var pattern = "%" + query.Text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
                var id = Guid.TryParse(query.Text, out var parsed) ? parsed : (Guid?)null;
                rows = rows.Where(x => EF.Functions.ILike(x.Request.RequestedDisplayNameHint, pattern, "\\") || EF.Functions.ILike(x.Request.RequestedSubjectIdentifier, pattern, "\\")
                                       || EF.Functions.ILike(x.Request.RequestedIssuerUri, pattern, "\\") || EF.Functions.ILike(x.OrganizationDisplayName, pattern, "\\")
                                       || (id != null && (x.Request.OrganizationMembershipRequestId == id || x.Request.OrganizationId == id)));
            }

            var total = await rows.CountAsync(cancellationToken).ConfigureAwait(false);
            if (query.After is { } after)
            {
                rows = rows.Where(x => x.Request.RequestCreatedOn < after.CreatedOn || (x.Request.RequestCreatedOn == after.CreatedOn && x.Request.OrganizationMembershipRequestId.CompareTo(after.RequestId) < 0));
            }

            var page = await rows.OrderByDescending(x => x.Request.RequestCreatedOn).ThenByDescending(x => x.Request.OrganizationMembershipRequestId).Take(query.Limit).ToListAsync(cancellationToken).ConfigureAwait(false);
            return new PlatformRequestStorePage(page.Select(x => new PlatformRequestStoreRow(x.Request, x.OrganizationDisplayName)).ToList(), total);
        }

        // ---- C8 onboarding ----

        public Task<OrganizationOnboardingRequest?> FindOnboardingRequestAsync(Guid organizationOnboardingRequestId, bool lockForUpdate, CancellationToken cancellationToken) =>
            lockForUpdate
                ? context.OnboardingRequests.FromSqlInterpolated($"SELECT * FROM authority.organization_onboarding_requests WHERE organization_onboarding_request_id = {organizationOnboardingRequestId} FOR UPDATE").AsTracking().FirstOrDefaultAsync(cancellationToken)
                : context.OnboardingRequests.AsTracking().FirstOrDefaultAsync(r => r.OrganizationOnboardingRequestId == organizationOnboardingRequestId, cancellationToken);

        public Task<OrganizationOnboardingRequest?> FindOnboardingRequestByIdempotencyKeyAsync(Guid platformOperatorId, string idempotencyKey, CancellationToken cancellationToken) =>
            context.OnboardingRequests.AsTracking().FirstOrDefaultAsync(r => r.CreatedByPlatformOperatorId == platformOperatorId && r.CreateIdempotencyKey == idempotencyKey, cancellationToken);

        public Task<OrganizationOnboardingRequest?> FindOnboardingRequestByOrganizationAsync(Guid organizationId, CancellationToken cancellationToken) =>
            context.OnboardingRequests.AsTracking().FirstOrDefaultAsync(r => r.ReservedOrganizationId == organizationId, cancellationToken);

        public Task<List<OrganizationOnboardingRequest>> GetOpenOnboardingRequestsByNameAsync(string normalizedName, CancellationToken cancellationToken) =>
            context.OnboardingRequests.AsTracking()
                .Where(r => r.OrganizationNormalizedName == normalizedName && (r.OnboardingStateName == OrganizationOnboardingStates.PendingApproval || r.OnboardingStateName == OrganizationOnboardingStates.AwaitingKeyCustody))
                .ToListAsync(cancellationToken);

        public void AddOnboardingRequest(OrganizationOnboardingRequest request) => context.OnboardingRequests.Add(request);

        public Task<List<OrganizationOnboardingOwner>> GetOnboardingOwnersAsync(Guid organizationOnboardingRequestId, CancellationToken cancellationToken) =>
            context.OnboardingOwners.AsTracking().Where(o => o.OrganizationOnboardingRequestId == organizationOnboardingRequestId).OrderBy(o => o.OwnerOrdinalNumber).ToListAsync(cancellationToken);

        public Task<OrganizationOnboardingOwner?> FindOnboardingOwnerAsync(Guid organizationOnboardingOwnerId, CancellationToken cancellationToken) =>
            context.OnboardingOwners.AsTracking().FirstOrDefaultAsync(o => o.OrganizationOnboardingOwnerId == organizationOnboardingOwnerId, cancellationToken);

        public void AddOnboardingOwner(OrganizationOnboardingOwner owner) => context.OnboardingOwners.Add(owner);

        public Task<List<OrganizationOnboardingApproval>> GetOnboardingApprovalsAsync(Guid organizationOnboardingRequestId, CancellationToken cancellationToken) =>
            context.OnboardingApprovals.AsTracking().Where(a => a.OrganizationOnboardingRequestId == organizationOnboardingRequestId).OrderBy(a => a.ApprovalRecordedOn).ThenBy(a => a.OrganizationOnboardingApprovalId).ToListAsync(cancellationToken);

        public async Task<List<OrganizationOnboardingApproval>> GetOnboardingApprovalsForRequestsAsync(IReadOnlyCollection<Guid> organizationOnboardingRequestIds, CancellationToken cancellationToken) =>
            organizationOnboardingRequestIds.Count == 0
                ? []
                : await context.OnboardingApprovals.AsNoTracking().Where(a => organizationOnboardingRequestIds.Contains(a.OrganizationOnboardingRequestId)).ToListAsync(cancellationToken).ConfigureAwait(false);

        public Task<OrganizationOnboardingApproval?> FindOnboardingApprovalAsync(Guid organizationOnboardingApprovalId, CancellationToken cancellationToken) =>
            context.OnboardingApprovals.AsTracking().FirstOrDefaultAsync(a => a.OrganizationOnboardingApprovalId == organizationOnboardingApprovalId, cancellationToken);

        public void AddOnboardingApproval(OrganizationOnboardingApproval approval) => context.OnboardingApprovals.Add(approval);

        public async Task<OnboardingRequestStorePage> SearchOnboardingRequestsAsync(OnboardingRequestQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);
            var now = query.Now;
            var rows = context.OnboardingRequests.AsNoTracking();
            rows = query.Status switch
            {
                OrganizationOnboardingFilters.NeedsApproval => rows.Where(r => r.OnboardingStateName == OrganizationOnboardingStates.PendingApproval && r.RequestExpiresOn > now),
                OrganizationOnboardingFilters.AwaitingKeyCustody => rows.Where(r => r.OnboardingStateName == OrganizationOnboardingStates.AwaitingKeyCustody && r.KeyCustodyExpiresOn > now),
                OrganizationOnboardingFilters.Initialized => rows.Where(r => r.OnboardingStateName == OrganizationOnboardingStates.Initialized),
                OrganizationOnboardingFilters.Closed => rows.Where(r => r.OnboardingStateName == OrganizationOnboardingStates.Rejected || r.OnboardingStateName == OrganizationOnboardingStates.Cancelled
                    || r.OnboardingStateName == OrganizationOnboardingStates.Expired || (r.OnboardingStateName == OrganizationOnboardingStates.PendingApproval && r.RequestExpiresOn <= now)
                    || (r.OnboardingStateName == OrganizationOnboardingStates.AwaitingKeyCustody && r.KeyCustodyExpiresOn <= now)),
                _ => rows,
            };
            if (!string.IsNullOrWhiteSpace(query.Text))
            {
                var pattern = "%" + query.Text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
                var id = Guid.TryParse(query.Text, out var parsed) ? parsed : (Guid?)null;
                rows = rows.Where(r => EF.Functions.ILike(r.OrganizationDisplayName, pattern, "\\") || (id != null && (r.OrganizationOnboardingRequestId == id || r.ReservedOrganizationId == id)));
            }

            var total = await rows.CountAsync(cancellationToken).ConfigureAwait(false);
            if (query.After is { } after)
            {
                rows = rows.Where(r => r.RequestCreatedOn < after.CreatedOn || (r.RequestCreatedOn == after.CreatedOn && r.OrganizationOnboardingRequestId.CompareTo(after.RequestId) < 0));
            }

            var page = await rows.OrderByDescending(r => r.RequestCreatedOn).ThenByDescending(r => r.OrganizationOnboardingRequestId).Take(query.Limit).ToListAsync(cancellationToken).ConfigureAwait(false);
            return new OnboardingRequestStorePage(page, total);
        }

        public async Task<List<OrganizationDirectoryRow>> GetOrganizationDirectoryRowsAsync(CancellationToken cancellationToken)
        {
            var enabledUsers = context.IdentityBindings.AsNoTracking().Where(b => b.IdentityBindingEnabled);
            var roleEdges = context.RelationshipEdges.AsNoTracking().Where(e => e.ResourceTypeName == ResourceTypeNames.Organization && e.SubjectTypeName == ResourceTypeNames.User && e.RelationshipRemovedOn == null);
            var rows = await context.OrganizationStates.AsNoTracking()
                .OrderBy(s => s.OrganizationDisplayName)
                .Select(s => new
                {
                    State = s,
                    Owners = roleEdges.Count(e => e.OrganizationId == s.OrganizationId && e.ResourceEntityId == s.OrganizationId && e.RelationshipName == OrganizationRoleNames.OrganizationOwner
                                                  && enabledUsers.Any(b => b.OrganizationId == s.OrganizationId && b.AuthorityUserId == e.SubjectEntityId)),
                    Administrators = roleEdges.Count(e => e.OrganizationId == s.OrganizationId && e.ResourceEntityId == s.OrganizationId && e.RelationshipName == OrganizationRoleNames.SystemAdministrator
                                                          && enabledUsers.Any(b => b.OrganizationId == s.OrganizationId && b.AuthorityUserId == e.SubjectEntityId)),
                    Members = enabledUsers.Count(b => b.OrganizationId == s.OrganizationId),
                })
                .Take(5000)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            return rows.Select(r => new OrganizationDirectoryRow(r.State, r.Owners, r.Administrators, r.Members)).ToList();
        }

        public Task<int> CountOrganizationsBindingIdentityAsync(string issuerUri, string subjectIdentifier, CancellationToken cancellationToken) =>
            context.IdentityBindings.AsNoTracking().Where(b => b.IdentityIssuerUri == issuerUri && b.IdentitySubjectIdentifier == subjectIdentifier).Select(b => b.OrganizationId).Distinct().CountAsync(cancellationToken);

        // ---- C8 owner recovery ----

        public Task<OrganizationOwnerRecoveryCase?> FindOwnerRecoveryCaseAsync(Guid organizationOwnerRecoveryCaseId, bool lockForUpdate, CancellationToken cancellationToken) =>
            lockForUpdate
                ? context.OwnerRecoveryCases.FromSqlInterpolated($"SELECT * FROM authority.organization_owner_recovery_cases WHERE organization_owner_recovery_case_id = {organizationOwnerRecoveryCaseId} FOR UPDATE").AsTracking().FirstOrDefaultAsync(cancellationToken)
                // Untracked: callers read the case only to find its organization before locking it; a tracked copy would make the later
                // FOR UPDATE read return this stale instance instead of the row committed by a concurrent decision.
                : context.OwnerRecoveryCases.AsNoTracking().FirstOrDefaultAsync(c => c.OrganizationOwnerRecoveryCaseId == organizationOwnerRecoveryCaseId, cancellationToken);

        public Task<OrganizationOwnerRecoveryCase?> FindOwnerRecoveryCaseByIdempotencyKeyAsync(Guid platformOperatorId, string idempotencyKey, CancellationToken cancellationToken) =>
            context.OwnerRecoveryCases.AsTracking().FirstOrDefaultAsync(c => c.OpenedByPlatformOperatorId == platformOperatorId && c.CreateIdempotencyKey == idempotencyKey, cancellationToken);

        public Task<OrganizationOwnerRecoveryCase?> FindOpenOwnerRecoveryCaseAsync(Guid organizationId, CancellationToken cancellationToken) =>
            context.OwnerRecoveryCases.AsTracking().FirstOrDefaultAsync(c => c.OrganizationId == organizationId && c.RecoveryStateName == OwnerRecoveryStates.Open, cancellationToken);

        public void AddOwnerRecoveryCase(OrganizationOwnerRecoveryCase recoveryCase) => context.OwnerRecoveryCases.Add(recoveryCase);

        public Task<List<OrganizationOwnerRecoverySubject>> GetOwnerRecoverySubjectsAsync(Guid organizationOwnerRecoveryCaseId, CancellationToken cancellationToken) =>
            context.OwnerRecoverySubjects.AsTracking().Where(s => s.OrganizationOwnerRecoveryCaseId == organizationOwnerRecoveryCaseId).OrderBy(s => s.RecoverySubjectRoleName).ThenBy(s => s.SubjectOrdinalNumber).ToListAsync(cancellationToken);

        public Task<OrganizationOwnerRecoverySubject?> FindOwnerRecoverySubjectAsync(Guid organizationOwnerRecoverySubjectId, CancellationToken cancellationToken) =>
            context.OwnerRecoverySubjects.AsTracking().FirstOrDefaultAsync(s => s.OrganizationOwnerRecoverySubjectId == organizationOwnerRecoverySubjectId, cancellationToken);

        public void AddOwnerRecoverySubject(OrganizationOwnerRecoverySubject subject) => context.OwnerRecoverySubjects.Add(subject);

        public Task<List<OrganizationOwnerRecoveryApproval>> GetOwnerRecoveryApprovalsAsync(Guid organizationOwnerRecoveryCaseId, CancellationToken cancellationToken) =>
            context.OwnerRecoveryApprovals.AsTracking().Where(a => a.OrganizationOwnerRecoveryCaseId == organizationOwnerRecoveryCaseId).OrderBy(a => a.ApprovalRecordedOn).ThenBy(a => a.OrganizationOwnerRecoveryApprovalId).ToListAsync(cancellationToken);

        public async Task<List<OrganizationOwnerRecoveryApproval>> GetOwnerRecoveryApprovalsForCasesAsync(IReadOnlyCollection<Guid> organizationOwnerRecoveryCaseIds, CancellationToken cancellationToken) =>
            organizationOwnerRecoveryCaseIds.Count == 0
                ? []
                : await context.OwnerRecoveryApprovals.AsNoTracking().Where(a => organizationOwnerRecoveryCaseIds.Contains(a.OrganizationOwnerRecoveryCaseId)).ToListAsync(cancellationToken).ConfigureAwait(false);

        public Task<OrganizationOwnerRecoveryApproval?> FindOwnerRecoveryApprovalAsync(Guid organizationOwnerRecoveryApprovalId, CancellationToken cancellationToken) =>
            context.OwnerRecoveryApprovals.AsTracking().FirstOrDefaultAsync(a => a.OrganizationOwnerRecoveryApprovalId == organizationOwnerRecoveryApprovalId, cancellationToken);

        public void AddOwnerRecoveryApproval(OrganizationOwnerRecoveryApproval approval) => context.OwnerRecoveryApprovals.Add(approval);

        public async Task<OwnerRecoveryStorePage> SearchOwnerRecoveryCasesAsync(OwnerRecoveryQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);
            var now = query.Now;
            var valid = context.OwnerRecoveryApprovals.AsNoTracking().Where(a => a.ApprovalStateName == OnboardingApprovalStates.Valid);
            var rows = from c in context.OwnerRecoveryCases.AsNoTracking()
                       join o in context.OrganizationStates.AsNoTracking() on c.OrganizationId equals o.OrganizationId
                       select new { Case = c, OrganizationDisplayName = o.OrganizationDisplayName, Approvals = valid.Count(a => a.OrganizationOwnerRecoveryCaseId == c.OrganizationOwnerRecoveryCaseId) };
            var open = rows.Where(x => x.Case.RecoveryStateName == OwnerRecoveryStates.Open && x.Case.CaseExpiresOn > now);
            rows = query.Status switch
            {
                OwnerRecoveryFilters.Open => open,
                OwnerRecoveryFilters.AwaitingApprovals => open.Where(x => x.Approvals < 3),
                OwnerRecoveryFilters.WaitingPeriod => open.Where(x => x.Approvals >= 3 && x.Case.WaitingPeriodEndsOn > now),
                OwnerRecoveryFilters.Ready => open.Where(x => x.Approvals >= 3 && x.Case.WaitingPeriodEndsOn <= now),
                OwnerRecoveryFilters.Objected => rows.Where(x => x.Case.RecoveryStateName == OwnerRecoveryStates.Objected),
                OwnerRecoveryFilters.Recovered => rows.Where(x => x.Case.RecoveryStateName == OwnerRecoveryStates.Recovered),
                OwnerRecoveryFilters.Closed => rows.Where(x => x.Case.RecoveryStateName == OwnerRecoveryStates.Cancelled || x.Case.RecoveryStateName == OwnerRecoveryStates.Expired
                    || x.Case.RecoveryStateName == OwnerRecoveryStates.Invalidated || (x.Case.RecoveryStateName == OwnerRecoveryStates.Open && x.Case.CaseExpiresOn <= now)),
                _ => rows,
            };
            if (!string.IsNullOrWhiteSpace(query.Text))
            {
                var pattern = "%" + query.Text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
                var id = Guid.TryParse(query.Text, out var parsed) ? parsed : (Guid?)null;
                rows = rows.Where(x => EF.Functions.ILike(x.OrganizationDisplayName, pattern, "\\") || EF.Functions.ILike(x.Case.IncidentReferenceText, pattern, "\\")
                                       || (id != null && (x.Case.OrganizationOwnerRecoveryCaseId == id || x.Case.OrganizationId == id)));
            }

            var total = await rows.CountAsync(cancellationToken).ConfigureAwait(false);
            if (query.After is { } after)
            {
                rows = rows.Where(x => x.Case.CaseOpenedOn < after.OpenedOn || (x.Case.CaseOpenedOn == after.OpenedOn && x.Case.OrganizationOwnerRecoveryCaseId.CompareTo(after.CaseId) < 0));
            }

            var page = await rows.OrderByDescending(x => x.Case.CaseOpenedOn).ThenByDescending(x => x.Case.OrganizationOwnerRecoveryCaseId).Take(query.Limit).ToListAsync(cancellationToken).ConfigureAwait(false);
            return new OwnerRecoveryStorePage(page.Select(x => new OwnerRecoveryStoreRow(x.Case, x.OrganizationDisplayName, x.Approvals)).ToList(), total);
        }

        public Task<List<OrganizationRecoveredOwnerProbation>> GetActiveRecoveredOwnerProbationsAsync(Guid organizationId, DateTimeOffset now, CancellationToken cancellationToken) =>
            context.RecoveredOwnerProbations.AsNoTracking().Where(p => p.OrganizationId == organizationId && p.ProbationEndsOn > now).ToListAsync(cancellationToken);

        public Task<OrganizationRecoveredOwnerProbation?> FindRecoveredOwnerProbationAsync(Guid organizationRecoveredOwnerProbationId, CancellationToken cancellationToken) =>
            context.RecoveredOwnerProbations.AsTracking().FirstOrDefaultAsync(p => p.OrganizationRecoveredOwnerProbationId == organizationRecoveredOwnerProbationId, cancellationToken);

        public void AddRecoveredOwnerProbation(OrganizationRecoveredOwnerProbation probation) => context.RecoveredOwnerProbations.Add(probation);

        public Task<int> CountSessionsCreatedSinceAsync(Guid identityBindingId, DateTimeOffset since, CancellationToken cancellationToken) =>
            context.Sessions.AsNoTracking().CountAsync(s => s.IdentityBindingId == identityBindingId && s.SessionCreatedOn >= since, cancellationToken);

        public Task FlushAsync(CancellationToken cancellationToken) => SaveAsync(cancellationToken);

        /// <summary>
        /// E3-M2b-1: every write of the scope goes through here, so a CARV fence refusal is always classified (and alerted).
        /// RB-CARV-1: in Release B the content write fence runs here, in this transaction, before any content-acceptance entity is
        /// written, whichever path staged it (defence in depth behind the explicit entry calls).
        /// </summary>
        private async Task SaveAsync(CancellationToken cancellationToken)
        {
            if (policy.GuardsContentWrites && !contentFenceVerified && HasContentAcceptanceWrites())
            {
                await RequireContentAcceptanceFloorAsync(cancellationToken).ConfigureAwait(false);
            }

            try
            {
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException ex) when (ContentAcceptanceRuleRefusals.Translate(ex, alerts) is { } refused)
            {
                throw refused;
            }
            catch (DbUpdateException ex) when (DataKeyLedgerRefusals.Translate(ex, alerts) is { } refused)
            {
                throw refused; // E3-M2e: 23505 on the ledger identifier (L-3) or a P-1/P-2 refusal (L-12); the transaction rolls back
            }
        }

        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            await SaveAsync(cancellationToken).ConfigureAwait(false);
            var notify = wakeup is not null && authorizationWorkAppended;
            if (notify)
            {
                // Transactional: queued now, delivered by PostgreSQL to every listener only when this transaction commits (never on rollback).
                await context.Database.ExecuteSqlRawAsync("SELECT pg_notify('" + AuthorityWorkOptions.NotificationChannel + "', '')", cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            committed = true;
            if (notify)
            {
                wakeup!.Signal(AuthorityWorkSignalSource.Commit); // after the commit returned: this instance looks at once
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (!committed)
            {
                try
                {
                    await transaction.RollbackAsync().ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    // already completed by the provider
                }
            }

            await transaction.DisposeAsync().ConfigureAwait(false);
            await context.DisposeAsync().ConfigureAwait(false);
        }
    }
}
