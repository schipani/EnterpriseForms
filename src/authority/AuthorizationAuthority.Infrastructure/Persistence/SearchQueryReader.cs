using System.Data;
using System.Text;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.SearchIndex;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SearchIndex;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace AuthorizationAuthority.Infrastructure.Persistence;

/// <summary>
/// IX-3 (design §5.1, §8.2): the indexed query path's reads, on the <c>authority_search_reader</c> login (its own connection
/// string; SELECT-only, read-only by default, no parameter logging). Each session is one READ ONLY REPEATABLE READ transaction with
/// a statement timeout and the RLS organization binding. Every statement is parameterized; SQL text comes only from the fixed
/// templates below (no identifier or operator is ever taken from input).
/// </summary>
public sealed class SearchQueryReader(string? readerConnectionString, IndexedSearchOptions options) : ISearchQueryReader
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(readerConnectionString);

    public async Task<ISearchQuerySession> BeginAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("no search reader connection is configured");
        }

        var contextOptions = new DbContextOptionsBuilder<AuthorityDbContext>().UseNpgsql(readerConnectionString).Options;
        var context = new AuthorityDbContext(contextOptions);
        try
        {
            var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
            await context.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken).ConfigureAwait(false);
            var timeout = $"{Math.Clamp(options.StatementTimeoutSeconds, 1, 60)}s";
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT set_config('statement_timeout', {timeout}, true), set_config('lock_timeout', '2s', true), set_config('authority_search.organization_id', {organizationId.ToString("D")}, true)",
                cancellationToken).ConfigureAwait(false);
            return new Session(context, transaction, organizationId);
        }
        catch
        {
            await context.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class Session(AuthorityDbContext context, IDbContextTransaction transaction, Guid organizationId) : ISearchQuerySession
    {
        private static readonly string[] ScopeStates = [ResourceLifecycleStates.Active, ResourceLifecycleStates.Quarantined];

        public async Task<SearchQueryStateFacts> ReadStateAsync(Guid formDefinitionId, CancellationToken cancellationToken) => await Guard(async () =>
        {
            var state = await context.OrganizationStates.AsNoTracking().FirstOrDefaultAsync(o => o.OrganizationId == organizationId, cancellationToken).ConfigureAwait(false);
            var projection = await context.SearchProjectionStates.AsNoTracking().FirstOrDefaultAsync(p => p.OrganizationId == organizationId, cancellationToken).ConfigureAwait(false);
            var formState = await context.SearchFormStates.AsNoTracking().FirstOrDefaultAsync(f => f.OrganizationId == organizationId && f.FormDefinitionId == formDefinitionId, cancellationToken).ConfigureAwait(false);
            var coverages = await context.SearchFieldCoverages.AsNoTracking().Where(c => c.OrganizationId == organizationId && c.FormDefinitionId == formDefinitionId).ToListAsync(cancellationToken).ConfigureAwait(false);
            var approvals = await context.FieldSearchApprovals.AsNoTracking().Where(a => a.OrganizationId == organizationId && a.FormDefinitionId == formDefinitionId).ToListAsync(cancellationToken).ConfigureAwait(false);
            var form = await context.Resources.AsNoTracking().Where(r => r.OrganizationId == organizationId && r.ResourceTypeName == ResourceTypeNames.FormDefinition && r.ResourceEntityId == formDefinitionId)
                .Select(r => r.ResourceLifecycleState).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            return new SearchQueryStateFacts(state?.AuthorizationRevision ?? -1, state?.ContentIntegrityRevision ?? -1, state?.IntegrityQuarantineEnabled ?? true, projection, formState?.FormContentRevision ?? 0, coverages, approvals, form);
        }).ConfigureAwait(false);

        public async Task<IReadOnlyList<SearchScopeRecordFacts>> ReadScopeAsync(Guid formDefinitionId, IReadOnlyCollection<Guid> discoverableRecordIds, IReadOnlyCollection<Guid> predicateFieldIds, CancellationToken cancellationToken) => await Guard(async () =>
        {
            var discoverable = discoverableRecordIds.ToArray();
            var fields = predicateFieldIds.ToArray();
            var rows = await (
                from r in context.Resources.AsNoTracking()
                where r.OrganizationId == organizationId && r.ResourceTypeName == ResourceTypeNames.FormRecord && r.ParentResourceEntityId == formDefinitionId
                      && ScopeStates.Contains(r.ResourceLifecycleState) && discoverable.Contains(r.ResourceEntityId)
                join h in context.ContentHeads.AsNoTracking().Where(h => h.OrganizationId == organizationId) on r.ResourceEntityId equals h.FormRecordId into heads
                from h in heads.DefaultIfEmpty()
                join e in context.SearchRecordEntries.AsNoTracking().Where(e => e.OrganizationId == organizationId) on r.ResourceEntityId equals e.FormRecordId into entries
                from e in entries.DefaultIfEmpty()
                select new
                {
                    r.ResourceEntityId,
                    r.ResourceLifecycleState,
                    r.ResourceRegisteredOn,
                    Revision = h == null ? 0 : h.CommittedContentRevision,
                    Operation = h == null ? null : h.CommittedOperationId,
                    Quarantined = h != null && h.ContentHeadQuarantined,
                    Modified = h == null ? (DateTimeOffset?)null : h.ContentHeadModifiedOn,
                    EntryOperation = e == null ? (Guid?)null : e.IndexedOperationId,
                    EntryVersion = e == null ? (int?)null : e.EntryDerivationVersion,
                    EntryStatus = e == null ? null : e.RecordStatusName,
                    EntryParent = e == null ? null : e.ParentFormRecordId,
                    EntryCreator = e == null ? null : e.CreatedByBusinessUserId,
                    Encrypted = e != null && context.SearchRecordFieldAnomalies.Any(a => a.OrganizationId == organizationId && a.FormRecordId == r.ResourceEntityId
                                                                                         && a.IndexedOperationId == e.IndexedOperationId && fields.Contains(a.FieldDefinitionId)),
                }).ToListAsync(cancellationToken).ConfigureAwait(false);
            return rows.Select(x => new SearchScopeRecordFacts(x.ResourceEntityId, x.ResourceLifecycleState, x.ResourceRegisteredOn, x.Revision, x.Operation, x.Quarantined, x.Modified,
                x.EntryOperation, x.EntryVersion, x.EntryStatus, x.EntryParent, x.EntryCreator, x.Encrypted)).ToList();
        }).ConfigureAwait(false);

        public async Task<IReadOnlyList<(Guid FormRecordId, Guid FieldDefinitionId, Guid ManifestItemId)>> ReadPredicateItemsAsync(IReadOnlyCollection<Guid> formRecordIds, IReadOnlyCollection<Guid> predicateFieldIds, CancellationToken cancellationToken) => await Guard(async () =>
        {
            var ids = formRecordIds.ToArray();
            var fields = predicateFieldIds.ToArray();
            var result = new List<(Guid, Guid, Guid)>();
            if (ids.Length == 0)
            {
                return result;
            }

            // Ids only. The postings are bound to the record's current entry (its indexed operation).
            foreach (var table in Configurations.SearchIndexSchema.PostingTables)
            {
                await using var command = Command($"""
                    SELECT p.form_record_id, p.field_definition_id, p.manifest_item_id
                    FROM authority_search.{table} p
                    JOIN authority_search.search_record_entries e
                      ON e.organization_id = p.organization_id AND e.form_record_id = p.form_record_id AND e.indexed_operation_id = p.indexed_operation_id
                    WHERE p.organization_id = @org AND p.form_record_id = ANY(@ids) AND p.field_definition_id = ANY(@fields)
                    """);
                command.Parameters.AddWithValue("org", organizationId);
                command.Parameters.AddWithValue("ids", ids);
                command.Parameters.AddWithValue("fields", fields);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    result.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2)));
                }
            }

            return result;
        }).ConfigureAwait(false);

        public async Task<IReadOnlyList<Guid>> FindCandidatesAsync(Guid formDefinitionId, IReadOnlyCollection<Guid> scope, IReadOnlyList<PreparedIndexedCondition> conditions, SearchMetadataFilter filter, int limit, CancellationToken cancellationToken) => await Guard(async () =>
        {
            var result = new List<Guid>();
            if (scope.Count == 0)
            {
                return result;
            }

            await SetCandidatePlannerAsync(true, cancellationToken).ConfigureAwait(false);
            await using (var command = CandidateCommand(string.Empty, formDefinitionId, scope, conditions, filter, limit))
            {
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    result.Add(reader.GetGuid(0));
                }
            }

            await SetCandidatePlannerAsync(false, cancellationToken).ConfigureAwait(false);
            return result;
        }).ConfigureAwait(false);

        public async Task<string> ExplainCandidatesAsync(Guid formDefinitionId, IReadOnlyCollection<Guid> scope, IReadOnlyList<PreparedIndexedCondition> conditions, SearchMetadataFilter filter, int limit, CancellationToken cancellationToken) => await Guard(async () =>
        {
            await SetCandidatePlannerAsync(true, cancellationToken).ConfigureAwait(false);
            var plan = new StringBuilder();
            await using (var command = CandidateCommand("EXPLAIN (FORMAT JSON, COSTS FALSE) ", formDefinitionId, scope, conditions, filter, limit))
            {
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    plan.Append(reader.GetString(0));
                }
            }

            await SetCandidatePlannerAsync(false, cancellationToken).ConfigureAwait(false);
            return plan.ToString();
        }).ConfigureAwait(false);

        public async Task<IReadOnlyList<SearchRecordBindingData>> ReadBindingDataAsync(IReadOnlyCollection<Guid> formRecordIds, IReadOnlyCollection<Guid>? fieldIds, CancellationToken cancellationToken) => await Guard(async () =>
        {
            var ids = formRecordIds.ToArray();
            if (ids.Length == 0)
            {
                return (IReadOnlyList<SearchRecordBindingData>)[];
            }

            var fields = fieldIds?.ToArray();
            var entries = await context.SearchRecordEntries.AsNoTracking().Where(e => e.OrganizationId == organizationId && ids.Contains(e.FormRecordId)).ToListAsync(cancellationToken).ConfigureAwait(false);
            var postings = new List<SearchPostingBase>();
            postings.AddRange(await Postings(context.SearchStringPostings).ToListAsync(cancellationToken).ConfigureAwait(false));
            postings.AddRange(await Postings(context.SearchIntegerPostings).ToListAsync(cancellationToken).ConfigureAwait(false));
            postings.AddRange(await Postings(context.SearchDecimalPostings).ToListAsync(cancellationToken).ConfigureAwait(false));
            postings.AddRange(await Postings(context.SearchDatePostings).ToListAsync(cancellationToken).ConfigureAwait(false));
            postings.AddRange(await Postings(context.SearchDateTimePostings).ToListAsync(cancellationToken).ConfigureAwait(false));
            postings.AddRange(await Postings(context.SearchBooleanPostings).ToListAsync(cancellationToken).ConfigureAwait(false));
            postings.AddRange(await Postings(context.SearchOptionPostings).ToListAsync(cancellationToken).ConfigureAwait(false));
            var anomalies = fields is null
                ? await context.SearchRecordFieldAnomalies.AsNoTracking().Where(a => a.OrganizationId == organizationId && ids.Contains(a.FormRecordId)).ToListAsync(cancellationToken).ConfigureAwait(false)
                : [];
            var heads = await context.ContentHeads.AsNoTracking().Where(h => h.OrganizationId == organizationId && ids.Contains(h.FormRecordId)).ToListAsync(cancellationToken).ConfigureAwait(false);
            var commitIds = heads.Where(h => h.CurrentRecordManifestCommitId is not null).Select(h => h.CurrentRecordManifestCommitId!.Value).ToArray();
            var commits = await context.ManifestCommits.AsNoTracking().Where(c => commitIds.Contains(c.RecordManifestCommitId)).ToListAsync(cancellationToken).ConfigureAwait(false);
            var leaves = await context.ManifestLeaves.AsNoTracking().Where(l => commitIds.Contains(l.RecordManifestCommitId)).ToListAsync(cancellationToken).ConfigureAwait(false);
            var headByRecord = heads.ToDictionary(h => h.FormRecordId);
            var commitById = commits.ToDictionary(c => c.RecordManifestCommitId);
            var leavesByCommit = leaves.GroupBy(l => l.RecordManifestCommitId).ToDictionary(g => g.Key, g => g.Select(l => new SearchManifestLeafFacts(l.ManifestItemKey, l.ManifestItemKindCode, l.ManifestItemId, l.CanonicalPayloadDigest, l.RowSignature)).ToList());
            var postingsByRecord = postings.GroupBy(p => p.FormRecordId).ToDictionary(g => g.Key, g => g.ToList());
            var anomaliesByRecord = anomalies.GroupBy(a => a.FormRecordId).ToDictionary(g => g.Key, g => g.ToList());
            var result = new List<SearchRecordBindingData>(entries.Count);
            foreach (var entry in entries)
            {
                var head = headByRecord.GetValueOrDefault(entry.FormRecordId);
                var commit = head?.CurrentRecordManifestCommitId is { } commitId ? commitById.GetValueOrDefault(commitId) : null;
                result.Add(new SearchRecordBindingData(entry,
                    postingsByRecord.GetValueOrDefault(entry.FormRecordId) ?? [],
                    anomaliesByRecord.GetValueOrDefault(entry.FormRecordId) ?? [],
                    head?.CommittedContentRevision ?? 0, head?.CommittedOperationId, head?.CommittedManifestRoot ?? [], head?.ContentHeadQuarantined ?? false, head?.CurrentRecordManifestCommitId,
                    commit?.ManifestLeafCount, commit?.ManifestRootDigest,
                    commit is null ? [] : leavesByCommit.GetValueOrDefault(commit.RecordManifestCommitId) ?? []));
            }

            return (IReadOnlyList<SearchRecordBindingData>)result;

            IQueryable<TPosting> Postings<TPosting>(DbSet<TPosting> set)
                where TPosting : SearchPostingBase =>
                set.AsNoTracking().Where(p => p.OrganizationId == organizationId && ids.Contains(p.FormRecordId) && (fields == null || fields.Contains(p.FieldDefinitionId)));
        }).ConfigureAwait(false);

        public async ValueTask DisposeAsync()
        {
            try
            {
                await transaction.RollbackAsync().ConfigureAwait(false); // read only: nothing to keep
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
            {
                // the connection is gone; nothing was written
            }

            await transaction.DisposeAsync().ConfigureAwait(false);
            await context.DisposeAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// §8.2 plan fence: the candidate statement must be driven by the scope (nested loops with per-record index probes). Hash,
        /// merge and bitmap plans are disabled for that statement only, so no plan can scan a value index across records.
        /// </summary>
        private Task<int> SetCandidatePlannerAsync(bool fenced, CancellationToken cancellationToken)
        {
            var value = fenced ? "off" : "on";
            return context.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT set_config('enable_hashjoin', {value}, true), set_config('enable_mergejoin', {value}, true), set_config('enable_bitmapscan', {value}, true), set_config('enable_seqscan', {value}, true)",
                cancellationToken);
        }

        private NpgsqlCommand CandidateCommand(string prefix, Guid formDefinitionId, IReadOnlyCollection<Guid> scope, IReadOnlyList<PreparedIndexedCondition> conditions, SearchMetadataFilter filter, int limit)
        {
            var sql = new StringBuilder(prefix);
            sql.Append("""
                SELECT e.form_record_id
                FROM unnest(@scope::uuid[]) AS s(form_record_id)
                CROSS JOIN LATERAL (
                    SELECT x.form_record_id, x.form_definition_id, x.indexed_operation_id, x.record_status_name, x.parent_form_record_id
                    FROM authority_search.search_record_entries x
                    WHERE x.organization_id = @org AND x.form_record_id = s.form_record_id
                    OFFSET 0) e
                WHERE e.form_definition_id = @form
                """);
            if (!filter.IncludeChildren)
            {
                sql.Append("\n  AND e.parent_form_record_id IS NULL");
            }

            if (filter.RecordStatus is not null)
            {
                sql.Append("\n  AND e.record_status_name = @status");
            }
            else if (!filter.IncludeArchived)
            {
                sql.Append("\n  AND e.record_status_name <> 'Archived'");
            }

            var command = Command(string.Empty);
            for (var i = 0; i < conditions.Count; i++)
            {
                sql.Append('\n').Append(CandidateTemplates.Clause(conditions[i], i, command.Parameters));
            }

            sql.Append("\nLIMIT @limit");
            command.CommandText = sql.ToString();
            command.Parameters.AddWithValue("scope", scope.ToArray());
            command.Parameters.AddWithValue("org", organizationId);
            command.Parameters.AddWithValue("form", formDefinitionId);
            command.Parameters.AddWithValue("limit", limit);
            if (filter.RecordStatus is not null)
            {
                command.Parameters.AddWithValue("status", filter.RecordStatus);
            }

            return command;
        }

        private NpgsqlCommand Command(string sql)
        {
            var connection = (NpgsqlConnection)context.Database.GetDbConnection();
            return new NpgsqlCommand(sql, connection, (NpgsqlTransaction)transaction.GetDbTransaction());
        }

        private static async Task<T> Guard<T>(Func<Task<T>> read)
        {
            try
            {
                return await read().ConfigureAwait(false);
            }
            catch (Exception ex) when (IsTimeout(ex))
            {
                throw new SearchQueryTimeoutException("the search statement exceeded its time limit", ex);
            }
        }

        private static bool IsTimeout(Exception ex) => ex switch
        {
            PostgresException { SqlState: PostgresErrorCodes.QueryCanceled } => true,
            InvalidOperationException { InnerException: { } inner } => IsTimeout(inner),
            DbUpdateException { InnerException: { } inner } => IsTimeout(inner),
            _ => false,
        };
    }
}

/// <summary>
/// IX-3: the fixed predicate templates of the candidate statement, one per (value type, operator). Each is a correlated
/// <c>EXISTS</c> over a fenced (<c>OFFSET 0</c>) per-record probe of one field's postings; the value comparison is outside the fence.
/// Field ids and values are always parameters; LIKE metacharacters are escaped.
/// </summary>
public static class CandidateTemplates
{
    private static readonly Dictionary<string, (string Table, string Column)> Tables = new(StringComparer.Ordinal)
    {
        ["String"] = ("search_string_postings", "normalized_string_value"),
        ["Integer"] = ("search_integer_postings", "integer_value"),
        ["Decimal"] = ("search_decimal_postings", "decimal_value"),
        ["Date"] = ("search_date_postings", "date_value"),
        ["DateTime"] = ("search_date_time_postings", "date_time_value"),
        ["Boolean"] = ("search_boolean_postings", "boolean_value"),
        ["Option"] = ("search_option_postings", "field_option_id"),
    };

    public static string Clause(PreparedIndexedCondition condition, int index, NpgsqlParameterCollection parameters)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(parameters);
        var (table, column) = Tables[condition.ValueType];
        var field = $"f{index}";
        var value = $"v{index}";
        parameters.AddWithValue(field, condition.FieldDefinitionId);
        // The fenced inner query fetches only this record's postings of this field (record probe index); the value predicate is
        // applied outside the fence, so no plan can use a value index to enumerate matches across records (§8.2).
        string Probe(string predicate) =>
            $"EXISTS (SELECT 1 FROM (SELECT p.{column} AS posting_value FROM authority_search.{table} p WHERE p.organization_id = @org AND p.form_record_id = e.form_record_id AND p.field_definition_id = @{field} AND p.indexed_operation_id = e.indexed_operation_id OFFSET 0) q WHERE {predicate})";
        string Bound(string name, object bound)
        {
            parameters.AddWithValue(name, bound);
            return "@" + name;
        }

        switch (condition.ValueType, condition.Operator)
        {
            case ("String", IndexedSearchVocabulary.EqualsTo):
                return "  AND " + Probe($"q.posting_value = {Bound(value, condition.Value!)}");
            case ("String", IndexedSearchVocabulary.StartsWith):
                return "  AND " + Probe($"q.posting_value LIKE {Bound(value, EscapeLike((string)condition.Value!) + "%")} ESCAPE '\\'");
            case ("String", IndexedSearchVocabulary.Contains):
                return "  AND " + Probe($"q.posting_value LIKE {Bound(value, "%" + EscapeLike((string)condition.Value!) + "%")} ESCAPE '\\'");
            case ("Boolean", IndexedSearchVocabulary.IsTrue):
                return "  AND " + Probe($"q.posting_value = TRUE");
            case ("Boolean", IndexedSearchVocabulary.IsFalse):
                return "  AND " + Probe($"q.posting_value = FALSE");
            case ("Option", IndexedSearchVocabulary.Includes):
                return "  AND " + Probe($"q.posting_value = {Bound(value, condition.Value!)}");
            case ("Option", IndexedSearchVocabulary.Is):
                var wanted = Bound(value, condition.Value!);
                return "  AND " + Probe($"q.posting_value = {wanted}") + "\n  AND NOT " + Probe($"q.posting_value <> {wanted}");
            case (_, IndexedSearchVocabulary.Between):
                return "  AND " + Probe($"q.posting_value BETWEEN {Bound($"l{index}", condition.Lower!)} AND {Bound($"u{index}", condition.Upper!)}");
            case (_, IndexedSearchVocabulary.EqualsTo):
                return "  AND " + Probe($"q.posting_value = {Bound(value, condition.Value!)}");
            case (_, IndexedSearchVocabulary.GreaterThan or IndexedSearchVocabulary.After):
                return "  AND " + Probe($"q.posting_value > {Bound(value, condition.Value!)}");
            case (_, IndexedSearchVocabulary.GreaterThanOrEqual):
                return "  AND " + Probe($"q.posting_value >= {Bound(value, condition.Value!)}");
            case (_, IndexedSearchVocabulary.LessThan or IndexedSearchVocabulary.Before):
                return "  AND " + Probe($"q.posting_value < {Bound(value, condition.Value!)}");
            case (_, IndexedSearchVocabulary.LessThanOrEqual):
                return "  AND " + Probe($"q.posting_value <= {Bound(value, condition.Value!)}");
            default:
                throw new InvalidOperationException("no candidate template for this value type and operator");
        }
    }

    public static string EscapeLike(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
    }
}
