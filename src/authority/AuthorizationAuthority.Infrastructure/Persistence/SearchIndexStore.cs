using System.Data.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Domain.Features.FieldSearch;
using AuthorizationAuthority.Domain.Features.SearchIndex;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AuthorizationAuthority.Infrastructure.Persistence;

/// <summary>
/// IX-1: EF Core implementation of <see cref="ISearchIndexStore"/> on the scope's own context and transaction. Deletes use
/// set-based <c>ExecuteDelete</c> inside the transaction (they roll back with it); inserts are tracked and written at commit.
/// Every statement is parameterized; no identifier is built from input.
/// </summary>
internal sealed class SearchIndexStore(AuthorityDbContext context) : ISearchIndexStore
{
    public Task BindOrganizationAsync(Guid organizationId, CancellationToken cancellationToken) =>
        context.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('authority_search.organization_id', {organizationId.ToString("D")}, true)", cancellationToken);

    public async Task<SearchProjectionState?> FindProjectionStateAsync(Guid organizationId, CancellationToken cancellationToken) =>
        context.SearchProjectionStates.Local.FirstOrDefault(s => s.OrganizationId == organizationId)
        ?? await context.SearchProjectionStates.AsTracking().FirstOrDefaultAsync(s => s.OrganizationId == organizationId, cancellationToken).ConfigureAwait(false);

    public void AddProjectionState(SearchProjectionState state) => context.SearchProjectionStates.Add(state);

    public Task<List<Guid>> GetIndexedOrganizationIdsAsync(CancellationToken cancellationToken) =>
        context.SearchProjectionStates.AsNoTracking().OrderBy(s => s.OrganizationId).Select(s => s.OrganizationId).ToListAsync(cancellationToken);

    public async Task<List<SearchFieldCoverage>> GetCoveragesAsync(Guid organizationId, Guid? formDefinitionId, CancellationToken cancellationToken)
    {
        var stored = await context.SearchFieldCoverages.AsTracking()
            .Where(c => c.OrganizationId == organizationId && (formDefinitionId == null || c.FormDefinitionId == formDefinitionId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var pending = context.ChangeTracker.Entries<SearchFieldCoverage>().Where(e => e.State == EntityState.Added).Select(e => e.Entity)
            .Where(c => c.OrganizationId == organizationId && (formDefinitionId == null || c.FormDefinitionId == formDefinitionId));
        return stored.Concat(pending).DistinctBy(c => c.SearchFieldCoverageId).ToList();
    }

    public void AddCoverage(SearchFieldCoverage coverage) => context.SearchFieldCoverages.Add(coverage);

    public async Task ReplaceRecordIndexAsync(DerivedRecordIndex derived, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(derived);
        await DeleteRecordIndexAsync(derived.Entry.OrganizationId, derived.Entry.FormRecordId, cancellationToken).ConfigureAwait(false);
        context.SearchRecordEntries.Add(derived.Entry);
        context.SearchStringPostings.AddRange(derived.StringPostings);
        context.SearchIntegerPostings.AddRange(derived.IntegerPostings);
        context.SearchDecimalPostings.AddRange(derived.DecimalPostings);
        context.SearchDatePostings.AddRange(derived.DatePostings);
        context.SearchDateTimePostings.AddRange(derived.DateTimePostings);
        context.SearchBooleanPostings.AddRange(derived.BooleanPostings);
        context.SearchOptionPostings.AddRange(derived.OptionPostings);
        context.SearchRecordFieldAnomalies.AddRange(derived.Anomalies);
    }

    public async Task<List<FieldSearchApproval>> GetCoveredActiveApprovalsAsync(Guid organizationId, Guid formDefinitionId, CancellationToken cancellationToken)
    {
        // Unsaved coverage or approval changes for this form in the scope (never the case in a content commit) would not be seen
        // by the SQL predicate below: use the two reads that merge tracked state instead, so the result is the same in every case.
        var pendingCoverage = context.ChangeTracker.Entries<SearchFieldCoverage>().Any(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
            && e.Entity.OrganizationId == organizationId && e.Entity.FormDefinitionId == formDefinitionId);
        var pendingApproval = context.ChangeTracker.Entries<FieldSearchApproval>().Any(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
            && e.Entity.OrganizationId == organizationId && e.Entity.FormDefinitionId == formDefinitionId);
        if (pendingCoverage || pendingApproval)
        {
            var coverages = (await GetCoveragesAsync(organizationId, formDefinitionId, cancellationToken).ConfigureAwait(false)).Where(c => c.CoverageStateName != SearchCoverageStates.Withdrawn).ToList();
            var approvals = await context.FieldSearchApprovals.AsTracking().Where(a => a.OrganizationId == organizationId && a.FormDefinitionId == formDefinitionId)
                .OrderBy(a => a.FieldSearchApprovalId).ToListAsync(cancellationToken).ConfigureAwait(false);
            return approvals.Where(a => a.ApprovalStateName == FieldSearchApprovalStates.Active && coverages.Any(c => c.FieldSearchApprovalId == a.FieldSearchApprovalId)).ToList();
        }

        return await context.FieldSearchApprovals.AsTracking()
            .Where(a => a.OrganizationId == organizationId && a.FormDefinitionId == formDefinitionId && a.ApprovalStateName == FieldSearchApprovalStates.Active
                        && context.SearchFieldCoverages.Any(c => c.OrganizationId == organizationId && c.FormDefinitionId == formDefinitionId
                                                                 && c.FieldSearchApprovalId == a.FieldSearchApprovalId && c.CoverageStateName != SearchCoverageStates.Withdrawn))
            .OrderBy(a => a.FieldSearchApprovalId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> ResetRecordIndexAsync(Guid organizationId, Guid formDefinitionId, Guid formRecordId, bool alwaysAdvanceFormState, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // One round trip (IX-1 remediation): the transaction-local organization binding first (the RLS policies of the delete
        // statements and of the rows the commit inserts afterwards read it), then the nine deletes, then the form-state
        // increment in a data-modifying CTE (executed exactly once, whether or not its output is read).
        const string sql = """
            SELECT set_config('authority_search.organization_id', @organization_text, true) IS NOT NULL;
            DELETE FROM authority_search.search_string_postings WHERE organization_id = @organization_id AND form_record_id = @form_record_id;
            DELETE FROM authority_search.search_integer_postings WHERE organization_id = @organization_id AND form_record_id = @form_record_id;
            DELETE FROM authority_search.search_decimal_postings WHERE organization_id = @organization_id AND form_record_id = @form_record_id;
            DELETE FROM authority_search.search_date_postings WHERE organization_id = @organization_id AND form_record_id = @form_record_id;
            DELETE FROM authority_search.search_date_time_postings WHERE organization_id = @organization_id AND form_record_id = @form_record_id;
            DELETE FROM authority_search.search_boolean_postings WHERE organization_id = @organization_id AND form_record_id = @form_record_id;
            DELETE FROM authority_search.search_option_postings WHERE organization_id = @organization_id AND form_record_id = @form_record_id;
            DELETE FROM authority_search.search_record_field_anomalies WHERE organization_id = @organization_id AND form_record_id = @form_record_id;
            WITH deleted AS (DELETE FROM authority_search.search_record_entries WHERE organization_id = @organization_id AND form_record_id = @form_record_id RETURNING 1),
                 counted AS (SELECT count(*)::int AS deleted_entries FROM deleted),
                 advanced AS (
                     INSERT INTO authority_search.search_form_states (search_form_state_id, organization_id, form_definition_id, form_content_revision, form_state_modified_on)
                     SELECT @search_form_state_id, @organization_id, @form_definition_id, 1, @form_state_modified_on FROM counted WHERE @always_advance OR counted.deleted_entries > 0
                     ON CONFLICT (organization_id, form_definition_id)
                     DO UPDATE SET form_content_revision = authority_search.search_form_states.form_content_revision + 1, form_state_modified_on = EXCLUDED.form_state_modified_on
                     RETURNING 1)
            SELECT deleted_entries FROM counted;
            """;
        await using var command = CreateCommand(sql);
        var text = command.CreateParameter();
        text.ParameterName = "organization_text";
        text.Value = organizationId.ToString("D");
        command.Parameters.Add(text);
        AddParameter(command, "organization_id", organizationId);
        AddParameter(command, "form_record_id", formRecordId);
        AddParameter(command, "form_definition_id", formDefinitionId);
        AddParameter(command, "search_form_state_id", Guid.CreateVersion7());
        var always = command.CreateParameter();
        always.ParameterName = "always_advance";
        always.Value = alwaysAdvanceFormState;
        command.Parameters.Add(always);
        var modified = command.CreateParameter();
        modified.ParameterName = "form_state_modified_on";
        modified.Value = now.ToUniversalTime();
        command.Parameters.Add(modified);
        var deleted = -1;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        do
        {
            // Result sets: the binding (boolean), eight DELETEs (no columns), the entry count (integer).
            if (reader.FieldCount > 0 && reader.GetFieldType(0) == typeof(int) && await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                deleted = reader.GetInt32(0);
            }
        }
        while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));
        return deleted >= 0 ? deleted : throw new InvalidOperationException("the record index reset returned no entry count");
    }

    public void AddRecordIndex(DerivedRecordIndex derived)
    {
        ArgumentNullException.ThrowIfNull(derived);
        context.SearchRecordEntries.Add(derived.Entry);
        context.SearchStringPostings.AddRange(derived.StringPostings);
        context.SearchIntegerPostings.AddRange(derived.IntegerPostings);
        context.SearchDecimalPostings.AddRange(derived.DecimalPostings);
        context.SearchDatePostings.AddRange(derived.DatePostings);
        context.SearchDateTimePostings.AddRange(derived.DateTimePostings);
        context.SearchBooleanPostings.AddRange(derived.BooleanPostings);
        context.SearchOptionPostings.AddRange(derived.OptionPostings);
        context.SearchRecordFieldAnomalies.AddRange(derived.Anomalies);
    }

    public async Task<int> DeleteRecordIndexAsync(Guid organizationId, Guid formRecordId, CancellationToken cancellationToken)
    {
        // One round trip for the nine tables (IX-1 measurement: per-table deletes dominated the commit-time cost).
        const string sql = """
            DELETE FROM authority_search.search_string_postings WHERE organization_id = @organization_id AND form_record_id = @form_record_id;
            DELETE FROM authority_search.search_integer_postings WHERE organization_id = @organization_id AND form_record_id = @form_record_id;
            DELETE FROM authority_search.search_decimal_postings WHERE organization_id = @organization_id AND form_record_id = @form_record_id;
            DELETE FROM authority_search.search_date_postings WHERE organization_id = @organization_id AND form_record_id = @form_record_id;
            DELETE FROM authority_search.search_date_time_postings WHERE organization_id = @organization_id AND form_record_id = @form_record_id;
            DELETE FROM authority_search.search_boolean_postings WHERE organization_id = @organization_id AND form_record_id = @form_record_id;
            DELETE FROM authority_search.search_option_postings WHERE organization_id = @organization_id AND form_record_id = @form_record_id;
            DELETE FROM authority_search.search_record_field_anomalies WHERE organization_id = @organization_id AND form_record_id = @form_record_id;
            WITH deleted AS (DELETE FROM authority_search.search_record_entries WHERE organization_id = @organization_id AND form_record_id = @form_record_id RETURNING 1) SELECT count(*)::int FROM deleted;
            """;
        await using var command = CreateCommand(sql);
        AddParameter(command, "organization_id", organizationId);
        AddParameter(command, "form_record_id", formRecordId);
        var deleted = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        do
        {
            // Only the final statement returns a row (the entry count); the DELETE statements return no result columns.
            if (reader.FieldCount > 0 && await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                deleted = reader.GetInt32(0);
            }
        }
        while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));
        return deleted;
    }

    public async Task<int> PurgeFormIndexAsync(Guid organizationId, Guid formDefinitionId, CancellationToken cancellationToken)
    {
        await context.SearchStringPostings.Where(p => p.OrganizationId == organizationId && p.FormDefinitionId == formDefinitionId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await context.SearchIntegerPostings.Where(p => p.OrganizationId == organizationId && p.FormDefinitionId == formDefinitionId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await context.SearchDecimalPostings.Where(p => p.OrganizationId == organizationId && p.FormDefinitionId == formDefinitionId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await context.SearchDatePostings.Where(p => p.OrganizationId == organizationId && p.FormDefinitionId == formDefinitionId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await context.SearchDateTimePostings.Where(p => p.OrganizationId == organizationId && p.FormDefinitionId == formDefinitionId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await context.SearchBooleanPostings.Where(p => p.OrganizationId == organizationId && p.FormDefinitionId == formDefinitionId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await context.SearchOptionPostings.Where(p => p.OrganizationId == organizationId && p.FormDefinitionId == formDefinitionId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await context.SearchRecordFieldAnomalies.Where(p => p.OrganizationId == organizationId && p.FormDefinitionId == formDefinitionId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        return await context.SearchRecordEntries.Where(e => e.OrganizationId == organizationId && e.FormDefinitionId == formDefinitionId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task BumpFormStateAsync(Guid organizationId, Guid formDefinitionId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // One round trip: insert at 1 or increment (the row is never tracked by the context, so no cached copy goes stale).
        const string sql = """
            INSERT INTO authority_search.search_form_states (search_form_state_id, organization_id, form_definition_id, form_content_revision, form_state_modified_on)
            VALUES (@search_form_state_id, @organization_id, @form_definition_id, 1, @form_state_modified_on)
            ON CONFLICT (organization_id, form_definition_id)
            DO UPDATE SET form_content_revision = authority_search.search_form_states.form_content_revision + 1, form_state_modified_on = EXCLUDED.form_state_modified_on;
            """;
        await using var command = CreateCommand(sql);
        AddParameter(command, "search_form_state_id", Guid.CreateVersion7());
        AddParameter(command, "organization_id", organizationId);
        AddParameter(command, "form_definition_id", formDefinitionId);
        var modified = command.CreateParameter();
        modified.ParameterName = "form_state_modified_on";
        modified.Value = now.ToUniversalTime();
        command.Parameters.Add(modified);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<SearchFormState?> FindFormStateAsync(Guid organizationId, Guid formDefinitionId, CancellationToken cancellationToken) =>
        context.SearchFormStates.AsNoTracking().FirstOrDefaultAsync(s => s.OrganizationId == organizationId && s.FormDefinitionId == formDefinitionId, cancellationToken);

    public Task<List<SearchRecordEntry>> GetEntriesForSealCheckAsync(Guid organizationId, int limit, IReadOnlyCollection<Guid> excluding, CancellationToken cancellationToken)
    {
        var excluded = excluding.ToArray();
        return context.SearchRecordEntries.AsNoTracking().Where(e => e.OrganizationId == organizationId && !excluded.Contains(e.SearchRecordEntryId))
            .OrderBy(e => e.LastSealVerifiedOn).ThenBy(e => e.SearchRecordEntryId).Take(limit).ToListAsync(cancellationToken);
    }

    public async Task<StoredRecordIndex?> GetStoredRecordIndexAsync(Guid organizationId, Guid formRecordId, CancellationToken cancellationToken)
    {
        var entry = await context.SearchRecordEntries.AsNoTracking().FirstOrDefaultAsync(e => e.OrganizationId == organizationId && e.FormRecordId == formRecordId, cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return null;
        }

        var postings = new List<SearchPostingBase>();
        postings.AddRange(await context.SearchStringPostings.AsNoTracking().Where(p => p.OrganizationId == organizationId && p.FormRecordId == formRecordId).ToListAsync(cancellationToken).ConfigureAwait(false));
        postings.AddRange(await context.SearchIntegerPostings.AsNoTracking().Where(p => p.OrganizationId == organizationId && p.FormRecordId == formRecordId).ToListAsync(cancellationToken).ConfigureAwait(false));
        postings.AddRange(await context.SearchDecimalPostings.AsNoTracking().Where(p => p.OrganizationId == organizationId && p.FormRecordId == formRecordId).ToListAsync(cancellationToken).ConfigureAwait(false));
        postings.AddRange(await context.SearchDatePostings.AsNoTracking().Where(p => p.OrganizationId == organizationId && p.FormRecordId == formRecordId).ToListAsync(cancellationToken).ConfigureAwait(false));
        postings.AddRange(await context.SearchDateTimePostings.AsNoTracking().Where(p => p.OrganizationId == organizationId && p.FormRecordId == formRecordId).ToListAsync(cancellationToken).ConfigureAwait(false));
        postings.AddRange(await context.SearchBooleanPostings.AsNoTracking().Where(p => p.OrganizationId == organizationId && p.FormRecordId == formRecordId).ToListAsync(cancellationToken).ConfigureAwait(false));
        postings.AddRange(await context.SearchOptionPostings.AsNoTracking().Where(p => p.OrganizationId == organizationId && p.FormRecordId == formRecordId).ToListAsync(cancellationToken).ConfigureAwait(false));
        var anomalies = await context.SearchRecordFieldAnomalies.AsNoTracking().Where(p => p.OrganizationId == organizationId && p.FormRecordId == formRecordId).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new StoredRecordIndex(entry, postings, anomalies);
    }

    public Task RecordSealCheckAsync(Guid searchRecordEntryId, DateTimeOffset? verifiedOn, DateTimeOffset attemptedOn, string outcomeCode, CancellationToken cancellationToken)
    {
        // Column-limited: the runtime role holds UPDATE on these three columns of search_record_entries only.
        var code = outcomeCode.Length > 64 ? outcomeCode[..64] : outcomeCode;
        var rows = context.SearchRecordEntries.Where(e => e.SearchRecordEntryId == searchRecordEntryId);
        return verifiedOn is { } verified
            ? rows.ExecuteUpdateAsync(s => s.SetProperty(e => e.LastSealVerifiedOn, verified).SetProperty(e => e.LastSealAttemptedOn, attemptedOn).SetProperty(e => e.LastSealAttemptOutcomeCode, code), cancellationToken)
            : rows.ExecuteUpdateAsync(s => s.SetProperty(e => e.LastSealAttemptedOn, attemptedOn).SetProperty(e => e.LastSealAttemptOutcomeCode, code), cancellationToken);
    }

    public async Task<CheapSweepFindings> RunCheapChecksAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        const string sql = """
            WITH entries AS (
                SELECT * FROM authority_search.search_record_entries WHERE organization_id = @organization_id
            ), rows AS (
                SELECT organization_id, form_definition_id, form_record_id, indexed_operation_id, field_search_approval_id FROM authority_search.search_string_postings WHERE organization_id = @organization_id
                UNION ALL SELECT organization_id, form_definition_id, form_record_id, indexed_operation_id, field_search_approval_id FROM authority_search.search_integer_postings WHERE organization_id = @organization_id
                UNION ALL SELECT organization_id, form_definition_id, form_record_id, indexed_operation_id, field_search_approval_id FROM authority_search.search_decimal_postings WHERE organization_id = @organization_id
                UNION ALL SELECT organization_id, form_definition_id, form_record_id, indexed_operation_id, field_search_approval_id FROM authority_search.search_date_postings WHERE organization_id = @organization_id
                UNION ALL SELECT organization_id, form_definition_id, form_record_id, indexed_operation_id, field_search_approval_id FROM authority_search.search_date_time_postings WHERE organization_id = @organization_id
                UNION ALL SELECT organization_id, form_definition_id, form_record_id, indexed_operation_id, field_search_approval_id FROM authority_search.search_boolean_postings WHERE organization_id = @organization_id
                UNION ALL SELECT organization_id, form_definition_id, form_record_id, indexed_operation_id, field_search_approval_id FROM authority_search.search_option_postings WHERE organization_id = @organization_id
                UNION ALL SELECT organization_id, form_definition_id, form_record_id, indexed_operation_id, NULL::uuid FROM authority_search.search_record_field_anomalies WHERE organization_id = @organization_id
            )
            SELECT
              (SELECT count(*) FROM entries e LEFT JOIN authority.record_content_heads h ON h.organization_id = e.organization_id AND h.form_record_id = e.form_record_id
                 WHERE h.committed_operation_id IS DISTINCT FROM e.indexed_operation_id OR h.committed_content_revision IS DISTINCT FROM e.indexed_content_revision)::int,
              (SELECT count(*) FROM entries e LEFT JOIN authority.authority_resources r ON r.organization_id = e.organization_id AND r.resource_type_name = 'form_record' AND r.resource_entity_id = e.form_record_id
                 WHERE r.resource_lifecycle_state IS NULL OR r.resource_lifecycle_state NOT IN ('Active','Quarantined') OR r.parent_resource_entity_id IS DISTINCT FROM e.form_definition_id)::int,
              (SELECT count(*) FROM rows p WHERE NOT EXISTS (SELECT 1 FROM entries e WHERE e.form_record_id = p.form_record_id AND e.indexed_operation_id = p.indexed_operation_id AND e.form_definition_id = p.form_definition_id))::int,
              (SELECT count(*) FROM rows p WHERE p.field_search_approval_id IS NOT NULL AND NOT EXISTS (
                 SELECT 1 FROM authority_search.search_field_coverages c WHERE c.organization_id = @organization_id AND c.field_search_approval_id = p.field_search_approval_id AND c.coverage_state_name <> 'Withdrawn'))::int,
              (SELECT count(*) FROM entries e WHERE EXISTS (SELECT 1 FROM unnest(e.derived_field_search_approval_ids) a(approval_id) WHERE NOT EXISTS (
                 SELECT 1 FROM authority_search.search_field_coverages c WHERE c.organization_id = @organization_id AND c.form_definition_id = e.form_definition_id AND c.field_search_approval_id = a.approval_id AND c.coverage_state_name <> 'Withdrawn')))::int,
              (SELECT count(*) FROM rows p LEFT JOIN authority.field_search_approvals a ON a.field_search_approval_id = p.field_search_approval_id
                 WHERE p.field_search_approval_id IS NOT NULL AND (a.organization_id IS DISTINCT FROM p.organization_id OR a.form_definition_id IS DISTINCT FROM p.form_definition_id))::int,
              (SELECT count(*) FROM entries e WHERE octet_length(e.entry_seal_signature) <> 64)::int,
              (SELECT count(*) FROM authority_search.search_field_coverages c
                 JOIN authority.authority_resources r ON r.organization_id = c.organization_id AND r.resource_type_name = 'form_record'
                      AND r.parent_resource_entity_id = c.form_definition_id AND r.resource_lifecycle_state IN ('Active','Quarantined')
                 JOIN authority.record_content_heads h ON h.organization_id = r.organization_id AND h.form_record_id = r.resource_entity_id
                      AND h.committed_content_revision > 0 AND h.committed_operation_id IS NOT NULL
                WHERE c.organization_id = @organization_id AND c.coverage_state_name = 'Complete'
                  AND NOT EXISTS (SELECT 1 FROM entries e WHERE e.form_record_id = r.resource_entity_id AND e.indexed_operation_id = h.committed_operation_id
                                    AND c.field_search_approval_id = ANY (e.derived_field_search_approval_ids)))::int
            """;
        await using var command = CreateCommand(sql);
        AddParameter(command, "organization_id", organizationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("cheap sweep query returned no row");
        }

        return new CheapSweepFindings(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7));
    }

    public async Task<IReadOnlyList<Guid>> DeleteEntriesNotAtHeadAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        var stale = await (from e in context.SearchRecordEntries.AsNoTracking()
                           join h in context.ContentHeads.AsNoTracking() on new { e.OrganizationId, e.FormRecordId } equals new { h.OrganizationId, h.FormRecordId } into heads
                           from h in heads.DefaultIfEmpty()
                           where e.OrganizationId == organizationId && (h == null || h.CommittedOperationId != e.IndexedOperationId)
                           select new { e.FormRecordId, e.FormDefinitionId }).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var entry in stale)
        {
            await DeleteRecordIndexAsync(organizationId, entry.FormRecordId, cancellationToken).ConfigureAwait(false);
        }

        return stale.Select(s => s.FormDefinitionId).Distinct().ToList();
    }

    public async Task<SearchScopeTimestamps> GetScopeTimestampsAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        var rows = context.SearchRecordEntries.AsNoTracking().Where(e => e.OrganizationId == organizationId);
        var count = await rows.CountAsync(cancellationToken).ConfigureAwait(false);
        if (count == 0)
        {
            return new SearchScopeTimestamps(0, null, null, null, null, 0, 0);
        }

        var aggregate = await rows.GroupBy(_ => 1).Select(g => new
        {
            OldestSeal = g.Min(e => e.LastSealVerifiedOn),
            NewestSeal = g.Max(e => e.LastSealVerifiedOn),
        }).FirstAsync(cancellationToken).ConfigureAwait(false);

        // D-IX2-7: the S-C deadline counts only entries whose record is not quarantined in the Authority (current head and
        // registry rows of AuthorityDb, never business data). Exclusion writes nothing and is reported as a count.
        var counted = DeepCountedEntries(organizationId);
        var included = await counted.CountAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset? oldestDeep = null, newestDeep = null;
        if (included > 0)
        {
            var deep = await counted.GroupBy(_ => 1).Select(g => new { Oldest = g.Min(e => e.LastDeepVerifiedOn), Newest = g.Max(e => e.LastDeepVerifiedOn) }).FirstAsync(cancellationToken).ConfigureAwait(false);
            (oldestDeep, newestDeep) = (deep.Oldest, deep.Newest);
        }

        return new SearchScopeTimestamps(count, aggregate.OldestSeal, aggregate.NewestSeal, oldestDeep, newestDeep, included, count - included);
    }

    /// <summary>D-IX2-7: entries of records that are neither head-quarantined nor registry-quarantined in the Authority.</summary>
    private IQueryable<SearchRecordEntry> DeepCountedEntries(Guid organizationId) =>
        context.SearchRecordEntries.AsNoTracking().Where(e => e.OrganizationId == organizationId
            && !context.ContentHeads.Any(h => h.OrganizationId == e.OrganizationId && h.FormRecordId == e.FormRecordId && h.ContentHeadQuarantined)
            && !context.Resources.Any(r => r.OrganizationId == e.OrganizationId && r.ResourceTypeName == "form_record" && r.ResourceEntityId == e.FormRecordId && r.ResourceLifecycleState == "Quarantined"));

    public Task<List<SearchBuildingForm>> GetBuildingFormsAsync(CancellationToken cancellationToken) =>
        (from c in context.SearchFieldCoverages.AsNoTracking()
         join p in context.SearchProjectionStates.AsNoTracking() on c.OrganizationId equals p.OrganizationId
         where c.CoverageStateName == SearchCoverageStates.Building && p.ProjectionStatusName != SearchProjectionStatuses.Quarantined
         group c by new { c.OrganizationId, c.FormDefinitionId, p.ProjectionStatusName } into g
         orderby g.Key.OrganizationId, g.Key.FormDefinitionId
         select new SearchBuildingForm(g.Key.OrganizationId, g.Key.FormDefinitionId, g.Count(), g.Key.ProjectionStatusName)).ToListAsync(cancellationToken);

    public Task<List<SearchRecordEntry>> GetFormEntriesAsync(Guid organizationId, Guid formDefinitionId, IReadOnlyCollection<Guid>? formRecordIds, CancellationToken cancellationToken)
    {
        var query = context.SearchRecordEntries.AsNoTracking().Where(e => e.OrganizationId == organizationId && e.FormDefinitionId == formDefinitionId);
        if (formRecordIds is not null)
        {
            var ids = formRecordIds.ToList();
            query = query.Where(e => ids.Contains(e.FormRecordId));
        }

        return query.OrderBy(e => e.FormRecordId).ToListAsync(cancellationToken);
    }

    public async Task<int> PurgeOrganizationIndexAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        await context.SearchStringPostings.Where(p => p.OrganizationId == organizationId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await context.SearchIntegerPostings.Where(p => p.OrganizationId == organizationId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await context.SearchDecimalPostings.Where(p => p.OrganizationId == organizationId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await context.SearchDatePostings.Where(p => p.OrganizationId == organizationId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await context.SearchDateTimePostings.Where(p => p.OrganizationId == organizationId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await context.SearchBooleanPostings.Where(p => p.OrganizationId == organizationId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await context.SearchOptionPostings.Where(p => p.OrganizationId == organizationId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await context.SearchRecordFieldAnomalies.Where(p => p.OrganizationId == organizationId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        return await context.SearchRecordEntries.Where(e => e.OrganizationId == organizationId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<List<SearchRecordEntry>> GetEntriesForDeepCheckAsync(Guid organizationId, int limit, CancellationToken cancellationToken) =>
        DeepCountedEntries(organizationId).OrderBy(e => e.LastDeepVerifiedOn).ThenBy(e => e.SearchRecordEntryId).Take(limit).ToListAsync(cancellationToken);

    public Task<List<SearchRecordEntry>> GetRandomEntriesAsync(Guid organizationId, int limit, CancellationToken cancellationToken) =>
        DeepCountedEntries(organizationId).OrderBy(_ => EF.Functions.Random()).Take(limit).ToListAsync(cancellationToken);

    public void AddResultSnapshot(SearchResultSnapshot snapshot, IReadOnlyList<SearchResultSnapshotEntry> entries)
    {
        context.SearchResultSnapshots.Add(snapshot);
        context.SearchResultSnapshotEntries.AddRange(entries);
    }

    public Task<SearchResultSnapshot?> FindResultSnapshotAsync(Guid searchResultSnapshotId, CancellationToken cancellationToken) =>
        context.SearchResultSnapshots.AsNoTracking().FirstOrDefaultAsync(s => s.SearchResultSnapshotId == searchResultSnapshotId, cancellationToken);

    public async Task<SearchResultSnapshot?> MarkResultSnapshotServedAsync(Guid searchResultSnapshotId, DateTimeOffset servedOn, CancellationToken cancellationToken)
    {
        var updated = await context.SearchResultSnapshots.Where(s => s.SearchResultSnapshotId == searchResultSnapshotId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastPageServedOn, servedOn), cancellationToken).ConfigureAwait(false);
        return updated == 0 ? null : await FindResultSnapshotAsync(searchResultSnapshotId, cancellationToken).ConfigureAwait(false);
    }

    public Task<List<SearchResultSnapshotEntry>> GetResultSnapshotEntriesAsync(Guid searchResultSnapshotId, int offset, int count, CancellationToken cancellationToken) =>
        context.SearchResultSnapshotEntries.AsNoTracking().Where(e => e.SearchResultSnapshotId == searchResultSnapshotId)
            .OrderBy(e => e.ResultEntryPosition).Skip(offset).Take(count).ToListAsync(cancellationToken);

    public Task<int> DeleteResultSnapshotAsync(Guid searchResultSnapshotId, CancellationToken cancellationToken) =>
        context.SearchResultSnapshots.Where(s => s.SearchResultSnapshotId == searchResultSnapshotId).ExecuteDeleteAsync(cancellationToken);

    public async Task<int> EvictSessionSnapshotsAsync(Guid authenticatedSessionId, int keep, CancellationToken cancellationToken)
    {
        var evicted = await context.SearchResultSnapshots.AsNoTracking().Where(s => s.AuthenticatedSessionId == authenticatedSessionId)
            .OrderByDescending(s => s.SnapshotCreatedOn).ThenByDescending(s => s.SearchResultSnapshotId).Skip(Math.Max(0, keep)).Select(s => s.SearchResultSnapshotId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return evicted.Count == 0 ? 0 : await context.SearchResultSnapshots.Where(s => evicted.Contains(s.SearchResultSnapshotId)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<long> CountLiveResultEntriesAsync(string creatingInstanceName, DateTimeOffset now, CancellationToken cancellationToken) =>
        await context.SearchResultSnapshots.AsNoTracking().Where(s => s.CreatingInstanceName == creatingInstanceName && s.SnapshotExpiresOn > now)
            .SumAsync(s => (long)s.ResultEntryCount, cancellationToken).ConfigureAwait(false);

    public Task<int> PurgeResultSnapshotsAsync(DateTimeOffset now, int batchSize, CancellationToken cancellationToken) =>
        context.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM authority_search.search_result_snapshots
            WHERE search_result_snapshot_id IN (
                SELECT s.search_result_snapshot_id
                FROM authority_search.search_result_snapshots s
                LEFT JOIN authority.organization_authorization_states o ON o.organization_id = s.organization_id
                LEFT JOIN authority_search.search_projection_states p ON p.organization_id = s.organization_id
                LEFT JOIN authority.authenticated_sessions a ON a.authenticated_session_id = s.authenticated_session_id
                WHERE s.snapshot_expires_on <= {now}
                   OR o.organization_id IS NULL OR o.integrity_quarantine_enabled OR o.authorization_revision > s.snapshot_authorization_revision
                   OR p.projection_status_name IS DISTINCT FROM 'Active' OR p.verification_suspended
                   OR a.authenticated_session_id IS NULL OR a.session_revoked_on IS NOT NULL OR a.session_expires_on <= {now}
                   OR {now} - a.session_last_seen_on > make_interval(secs => a.session_idle_timeout_seconds)
                ORDER BY s.snapshot_expires_on
                LIMIT {batchSize}
                FOR UPDATE OF s SKIP LOCKED)
            """, cancellationToken);

    public Task RecordDeepCheckAsync(Guid searchRecordEntryId, DateTimeOffset? verifiedOn, DateTimeOffset attemptedOn, string outcomeCode, CancellationToken cancellationToken)
    {
        // Column-limited: the runtime role holds UPDATE on the S-B and S-C timestamp columns of search_record_entries only.
        var code = outcomeCode.Length > 64 ? outcomeCode[..64] : outcomeCode;
        var rows = context.SearchRecordEntries.Where(e => e.SearchRecordEntryId == searchRecordEntryId);
        return verifiedOn is { } verified
            ? rows.ExecuteUpdateAsync(s => s.SetProperty(e => e.LastDeepVerifiedOn, verified).SetProperty(e => e.LastDeepAttemptedOn, attemptedOn).SetProperty(e => e.LastDeepAttemptOutcomeCode, code), cancellationToken)
            : rows.ExecuteUpdateAsync(s => s.SetProperty(e => e.LastDeepAttemptedOn, attemptedOn).SetProperty(e => e.LastDeepAttemptOutcomeCode, code), cancellationToken);
    }

    private DbCommand CreateCommand(string sql)
    {
        var connection = context.Database.GetDbConnection();
        var command = connection.CreateCommand();
        command.CommandText = sql;
        AuthorityCommandCounter.CountRawCommand(); // each command created here is executed once (Testing-only measurement)
        command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction() ?? throw new InvalidOperationException("the search index store requires the scope transaction");
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
