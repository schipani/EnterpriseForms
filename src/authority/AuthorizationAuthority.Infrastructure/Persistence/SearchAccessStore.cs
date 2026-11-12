using System.Data.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Domain.Features.SearchAccess;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AuthorizationAuthority.Infrastructure.Persistence;

/// <summary>
/// IX-5a: EF Core implementation of <see cref="ISearchAccessStore"/> on the scope's own context and transaction. Every statement runs
/// after <see cref="BindOrganizationAsync"/> has set the transaction-local organization the forced RLS policies check.
/// </summary>
internal sealed class SearchAccessStore(AuthorityDbContext context) : ISearchAccessStore
{
    /// <summary>Binds both RLS contexts: Tier 2's own and Tier 1's (the value rows are derived from Tier 1 postings).</summary>
    public Task BindOrganizationAsync(Guid organizationId, CancellationToken cancellationToken) =>
        context.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('authority_search_access.organization_id', {organizationId.ToString("D")}, true), set_config('authority_search.organization_id', {organizationId.ToString("D")}, true)", cancellationToken);

    public async Task<SearchAccessCheckpoint?> FindCheckpointForUpdateAsync(Guid organizationId, CancellationToken cancellationToken) =>
        (await context.SearchAccessCheckpoints
            .FromSqlInterpolated($"SELECT * FROM authority_search_access.search_access_checkpoints WHERE organization_id = {organizationId} FOR UPDATE")
            .AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false)).SingleOrDefault();

    public Task<SearchAccessCheckpoint?> FindCheckpointAsync(Guid organizationId, CancellationToken cancellationToken) =>
        context.SearchAccessCheckpoints.AsNoTracking().SingleOrDefaultAsync(c => c.OrganizationId == organizationId, cancellationToken);

    public async Task InsertCheckpointAsync(SearchAccessCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        context.SearchAccessCheckpoints.Add(checkpoint);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        context.Entry(checkpoint).State = EntityState.Detached;
    }

    public async Task<bool> TryUpdateCheckpointAsync(Guid organizationId, long expectedCheckpointRevision, SearchAccessCheckpointValues values, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(values);
        return await context.SearchAccessCheckpoints
            .Where(c => c.OrganizationId == organizationId && c.CheckpointRevision == expectedCheckpointRevision)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.ConsumedAuthorizationRevision, values.ConsumedAuthorizationRevision)
                .SetProperty(c => c.ConsumedJournalDigest, values.ConsumedJournalDigest)
                .SetProperty(c => c.ConsumedContentIntegrityRevision, values.ConsumedContentIntegrityRevision)
                .SetProperty(c => c.ConsumedContentJournalDigest, values.ConsumedContentJournalDigest)
                .SetProperty(c => c.ConsumedCoverageDigest, values.ConsumedCoverageDigest)
                .SetProperty(c => c.AccessProjectionStateName, values.StateName)
                .SetProperty(c => c.QuarantineReasonCode, values.QuarantineReasonCode)
                .SetProperty(c => c.AccessProjectionProtocolVersion, values.ProtocolVersion)
                .SetProperty(c => c.DerivedSchemaDigest, values.DerivedSchemaDigest)
                .SetProperty(c => c.CheckpointModifiedOn, now)
                .SetProperty(c => c.CheckpointRevision, expectedCheckpointRevision + 1), cancellationToken).ConfigureAwait(false) == 1;
    }

    public Task<List<SearchAccessEdge>> GetEdgesByResourceAsync(Guid organizationId, string resourceTypeName, IReadOnlyCollection<Guid> resourceEntityIds, CancellationToken cancellationToken)
    {
        var ids = resourceEntityIds.ToList();
        return context.SearchAccessEdges.AsNoTracking()
            .Where(e => e.OrganizationId == organizationId && e.ResourceTypeName == resourceTypeName && ids.Contains(e.ResourceEntityId))
            .ToListAsync(cancellationToken);
    }

    public Task<List<SearchAccessEdge>> GetEdgesBySubjectAsync(Guid organizationId, string subjectTypeName, IReadOnlyCollection<Guid> subjectEntityIds, string resourceTypeName, string relationshipName, CancellationToken cancellationToken)
    {
        var ids = subjectEntityIds.ToList();
        return context.SearchAccessEdges.AsNoTracking()
            .Where(e => e.OrganizationId == organizationId && e.SubjectTypeName == subjectTypeName && ids.Contains(e.SubjectEntityId) && e.ResourceTypeName == resourceTypeName && e.RelationshipName == relationshipName)
            .ToListAsync(cancellationToken);
    }

    public Task<SearchAccessEdge?> FindEdgeAsync(Guid organizationId, string resourceTypeName, Guid resourceEntityId, string relationshipName, string subjectTypeName, Guid subjectEntityId, string subjectRelationName, CancellationToken cancellationToken) =>
        context.SearchAccessEdges.AsTracking().SingleOrDefaultAsync(e => e.OrganizationId == organizationId && e.ResourceTypeName == resourceTypeName && e.ResourceEntityId == resourceEntityId
            && e.RelationshipName == relationshipName && e.SubjectTypeName == subjectTypeName && e.SubjectEntityId == subjectEntityId && e.SubjectRelationName == subjectRelationName, cancellationToken);

    public void AddEdge(SearchAccessEdge edge) => context.SearchAccessEdges.Add(edge);

    public void RemoveEdge(SearchAccessEdge edge) => context.SearchAccessEdges.Remove(edge);

    public Task FlushAsync(CancellationToken cancellationToken) => context.SaveChangesAsync(cancellationToken);

    public async Task DeleteGrantsOfRecordsAsync(Guid organizationId, IReadOnlyCollection<Guid> formRecordIds, CancellationToken cancellationToken)
    {
        var ids = formRecordIds.ToList();
        await context.SearchAccessRecordGrants.Where(g => g.OrganizationId == organizationId && ids.Contains(g.FormRecordId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await context.SearchAccessFieldItemGrants.Where(g => g.OrganizationId == organizationId && ids.Contains(g.FormRecordId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    public void AddRecordGrants(IEnumerable<SearchAccessRecordGrant> grants) => context.SearchAccessRecordGrants.AddRange(grants);

    public void AddFieldItemGrants(IEnumerable<SearchAccessFieldItemGrant> grants) => context.SearchAccessFieldItemGrants.AddRange(grants);

    public async Task PurgeOrganizationAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        await context.SearchAccessValuePostings.Where(v => v.OrganizationId == organizationId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await context.SearchAccessFieldItemGrants.Where(g => g.OrganizationId == organizationId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await context.SearchAccessRecordGrants.Where(g => g.OrganizationId == organizationId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await context.SearchAccessEdges.Where(e => e.OrganizationId == organizationId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<SearchAccessVerificationState?> FindVerificationStateAsync(Guid organizationId, CancellationToken cancellationToken) =>
        context.SearchAccessVerificationStates.AsNoTracking().SingleOrDefaultAsync(v => v.OrganizationId == organizationId, cancellationToken);

    public async Task SaveVerificationStateAsync(SearchAccessVerificationState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        var updated = await context.SearchAccessVerificationStates.Where(v => v.OrganizationId == state.OrganizationId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(v => v.AccessVerifiedRevision, state.AccessVerifiedRevision)
                .SetProperty(v => v.AccessVerifiedJournalDigest, state.AccessVerifiedJournalDigest)
                .SetProperty(v => v.FullVerificationCompletedOn, state.FullVerificationCompletedOn)
                .SetProperty(v => v.FullVerificationAuthorizationRevision, state.FullVerificationAuthorizationRevision)
                .SetProperty(v => v.SpiceDbSweepCursorRecordId, state.SpiceDbSweepCursorRecordId)
                .SetProperty(v => v.SpiceDbSweepStartedOn, state.SpiceDbSweepStartedOn)
                .SetProperty(v => v.SpiceDbSweepCompletedOn, state.SpiceDbSweepCompletedOn)
                .SetProperty(v => v.SpiceDbSweepRecordsChecked, state.SpiceDbSweepRecordsChecked)
                .SetProperty(v => v.LastVerificationOutcomeCode, state.LastVerificationOutcomeCode)
                .SetProperty(v => v.VerificationStateModifiedOn, state.VerificationStateModifiedOn), cancellationToken).ConfigureAwait(false);
        if (updated == 0)
        {
            context.SearchAccessVerificationStates.Add(state);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            context.Entry(state).State = EntityState.Detached;
        }
    }

    public Task DeleteVerificationStateAsync(Guid organizationId, CancellationToken cancellationToken) =>
        context.SearchAccessVerificationStates.Where(v => v.OrganizationId == organizationId).ExecuteDeleteAsync(cancellationToken);

    public void AddShadowSample(SearchAccessShadowSample sample) => context.SearchAccessShadowSamples.Add(sample);

    public Task<List<SearchAccessShadowSample>> GetShadowSamplesAsync(Guid organizationId, DateTimeOffset comparedFrom, DateTimeOffset comparedBefore, CancellationToken cancellationToken) =>
        context.SearchAccessShadowSamples.AsNoTracking().Where(s => s.OrganizationId == organizationId && s.SampleComparedOn >= comparedFrom && s.SampleComparedOn < comparedBefore).OrderBy(s => s.SampleComparedOn).ToListAsync(cancellationToken);

    public Task<int> PurgeShadowSamplesAsync(Guid organizationId, DateTimeOffset before, CancellationToken cancellationToken) =>
        context.SearchAccessShadowSamples.Where(s => s.OrganizationId == organizationId && s.SampleComparedOn < before).ExecuteDeleteAsync(cancellationToken);

    public Task<List<SearchAccessEdge>> GetAllEdgesAsync(Guid organizationId, CancellationToken cancellationToken) =>
        context.SearchAccessEdges.AsNoTracking().Where(e => e.OrganizationId == organizationId).ToListAsync(cancellationToken);

    public Task<List<SearchAccessRecordGrant>> GetRecordGrantsAsync(Guid organizationId, IReadOnlyCollection<Guid>? formRecordIds, CancellationToken cancellationToken)
    {
        var ids = formRecordIds?.ToList();
        return context.SearchAccessRecordGrants.AsNoTracking().Where(g => g.OrganizationId == organizationId && (ids == null || ids.Contains(g.FormRecordId))).ToListAsync(cancellationToken);
    }

    public Task<List<SearchAccessFieldItemGrant>> GetFieldItemGrantsAsync(Guid organizationId, IReadOnlyCollection<Guid>? formRecordIds, CancellationToken cancellationToken)
    {
        var ids = formRecordIds?.ToList();
        return context.SearchAccessFieldItemGrants.AsNoTracking().Where(g => g.OrganizationId == organizationId && (ids == null || ids.Contains(g.FormRecordId))).ToListAsync(cancellationToken);
    }

    // ---- IX-5b ----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The seven Tier 1 posting tables, each joined to its record's entry at the accepted head (entry operation = head committed
    /// operation) and to a Complete coverage of a Public or Internal field. Parameterized; the RLS contexts are bound.
    /// </summary>
    public async Task<List<SearchablePosting>> GetSearchablePostingsAsync(Guid organizationId, IReadOnlyCollection<Guid>? formRecordIds, CancellationToken cancellationToken)
    {
        if (formRecordIds is { Count: 0 })
        {
            return [];
        }

        var tables = new (string Table, string Type, string Column)[]
        {
            ("search_string_postings", "String", "normalized_string_value"), ("search_integer_postings", "Integer", "integer_value"), ("search_decimal_postings", "Decimal", "decimal_value"),
            ("search_date_postings", "Date", "date_value"), ("search_date_time_postings", "DateTime", "date_time_value"), ("search_boolean_postings", "Boolean", "boolean_value"),
            ("search_option_postings", "Option", "field_option_id"),
        };
        var columns = new[] { "normalized_string_value", "integer_value", "decimal_value", "date_value", "date_time_value", "boolean_value", "field_option_id" };
        var casts = new[] { "text", "bigint", "numeric", "date", "timestamptz", "boolean", "uuid" };
        var parts = tables.Select(t =>
        {
            var select = string.Join(", ", columns.Select((col, i) => col == t.Column ? $"p.{col}" : $"NULL::{casts[i]}"));
            return $"""
                SELECT p.form_definition_id, p.field_definition_id, p.field_search_approval_id, p.form_record_id, p.manifest_item_id, p.indexed_operation_id, '{t.Type}'::text, {select}
                FROM authority_search.{t.Table} p
                JOIN authority_search.search_record_entries e ON e.organization_id = p.organization_id AND e.form_record_id = p.form_record_id AND e.indexed_operation_id = p.indexed_operation_id
                JOIN authority.record_content_heads h ON h.organization_id = e.organization_id AND h.form_record_id = e.form_record_id AND h.committed_operation_id = e.indexed_operation_id
                JOIN authority_search.search_field_coverages c ON c.organization_id = p.organization_id AND c.field_search_approval_id = p.field_search_approval_id
                     AND c.coverage_state_name = 'Complete' AND c.covered_field_classification IN ('Public', 'Internal')
                WHERE p.organization_id = @organization_id{(formRecordIds is null ? string.Empty : " AND p.form_record_id = ANY(@records)")}
                """;
        });
        await using var command = CreateCommand(string.Join("\nUNION ALL\n", parts) + ";");
        AddParameter(command, "organization_id", organizationId);
        if (formRecordIds is not null)
        {
            var records = command.CreateParameter();
            records.ParameterName = "records";
            records.Value = formRecordIds.Distinct().ToArray();
            command.Parameters.Add(records);
        }

        var result = new List<SearchablePosting>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new SearchablePosting(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetGuid(3), reader.GetGuid(4), reader.GetGuid(5), reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetInt64(8),
                reader.IsDBNull(9) ? null : reader.GetDecimal(9),
                reader.IsDBNull(10) ? null : reader.GetFieldValue<DateOnly>(10),
                reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11),
                reader.IsDBNull(12) ? null : reader.GetBoolean(12),
                reader.IsDBNull(13) ? null : reader.GetGuid(13)));
        }

        return result;
    }

    public Task<List<SearchableCoverage>> GetSearchableCoveragesAsync(Guid organizationId, CancellationToken cancellationToken) =>
        context.SearchFieldCoverages.AsNoTracking()
            .Where(c => c.OrganizationId == organizationId && c.CoverageStateName == "Complete" && (c.CoveredFieldClassification == "Public" || c.CoveredFieldClassification == "Internal"))
            .Select(c => new SearchableCoverage(c.FieldSearchApprovalId, c.FieldDefinitionId, c.FormDefinitionId)).ToListAsync(cancellationToken);

    public Task<string?> GetTier1ProjectionStatusAsync(Guid organizationId, CancellationToken cancellationToken) =>
        context.SearchProjectionStates.AsNoTracking().Where(s => s.OrganizationId == organizationId).Select(s => (string?)s.ProjectionStatusName).SingleOrDefaultAsync(cancellationToken);

    public async Task<long> GetTier1FormContentRevisionAsync(Guid organizationId, Guid formDefinitionId, CancellationToken cancellationToken) =>
        await context.SearchFormStates.AsNoTracking().Where(f => f.OrganizationId == organizationId && f.FormDefinitionId == formDefinitionId).Select(f => (long?)f.FormContentRevision).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? 0;

    public Task<List<Tier1RecordHeader>> GetTier1RecordHeadersAsync(Guid organizationId, Guid formDefinitionId, IReadOnlyCollection<Guid> formRecordIds, CancellationToken cancellationToken)
    {
        var ids = formRecordIds.ToList();
        return context.SearchRecordEntries.AsNoTracking().Where(e => e.OrganizationId == organizationId && e.FormDefinitionId == formDefinitionId && ids.Contains(e.FormRecordId))
            .Select(e => new Tier1RecordHeader(e.FormRecordId, e.RecordStatusName, e.ParentFormRecordId)).ToListAsync(cancellationToken);
    }

    public async Task<Dictionary<(Guid FormRecordId, Guid FieldSearchApprovalId), int>> GetFieldItemCountsAsync(Guid organizationId, IReadOnlyCollection<Guid> formRecordIds, CancellationToken cancellationToken)
    {
        var ids = formRecordIds.ToList();
        var entries = await context.SearchRecordEntries.AsNoTracking().Where(e => e.OrganizationId == organizationId && ids.Contains(e.FormRecordId))
            .Select(e => new { e.FormRecordId, e.DerivedFieldSearchApprovalIds, e.DerivedFieldItemCounts }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<(Guid, Guid), int>();
        foreach (var entry in entries.Where(e => e.DerivedFieldItemCounts is not null && e.DerivedFieldItemCounts.Length == e.DerivedFieldSearchApprovalIds.Length))
        {
            for (var i = 0; i < entry.DerivedFieldSearchApprovalIds.Length; i++)
            {
                result[(entry.FormRecordId, entry.DerivedFieldSearchApprovalIds[i])] = entry.DerivedFieldItemCounts![i];
            }
        }

        return result;
    }

    public async Task DeleteValuePostingsOfRecordsAsync(Guid organizationId, IReadOnlyCollection<Guid>? formRecordIds, CancellationToken cancellationToken)
    {
        var ids = formRecordIds?.ToList();
        await context.SearchAccessValuePostings.Where(v => v.OrganizationId == organizationId && (ids == null || ids.Contains(v.FormRecordId))).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    public void AddValuePostings(IEnumerable<SearchAccessValuePosting> postings) => context.SearchAccessValuePostings.AddRange(postings);

    public Task<List<SearchAccessValuePosting>> GetValuePostingsAsync(Guid organizationId, IReadOnlyCollection<Guid>? formRecordIds, CancellationToken cancellationToken)
    {
        var ids = formRecordIds?.ToList();
        return context.SearchAccessValuePostings.AsNoTracking().Where(v => v.OrganizationId == organizationId && (ids == null || ids.Contains(v.FormRecordId))).ToListAsync(cancellationToken);
    }

    private DbCommand CreateCommand(string sql)
    {
        var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql;
        command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction() ?? throw new InvalidOperationException("the search access store requires the scope transaction");
        return command;
    }

    private static void AddParameter(DbCommand command, string name, Guid value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
