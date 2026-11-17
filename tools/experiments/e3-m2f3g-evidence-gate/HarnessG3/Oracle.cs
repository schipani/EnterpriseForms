// E3-M2f-3G independent read-only oracles (closure M2F3-72). Every check here is the harness's OWN SQL over durable journal state, in a
// read-only superuser session with index and bitmap scans disabled (so the count can never go through IX-B or any other index), and is
// independent of the recorder's result classification and of the product lookup and validation code.
using Npgsql;

internal sealed partial class Lab
{
    internal sealed record BoundResult(long Total, long MaxPerKeyBucket, long MaxPerBucket, long InvalidBodies, long TimeOrderViolations, long DigestMismatches, long ChainBreaks, long RevisionGaps, bool HeadMatches, string Detail)
    {
        /// <summary>The seven-event bound and the integrity of the audit stream (no invalid evidence body unless the scenario seeded one).</summary>
        public bool Holds(long allowedInvalid = 0) => MaxPerKeyBucket <= 1 && MaxPerBucket <= 7 && InvalidBodies <= allowedInvalid && TimeOrderViolations == 0 && DigestMismatches == 0 && ChainBreaks == 0 && RevisionGaps == 0 && HeadMatches;

        public override string ToString() => $"evidence {Total}; max per (key, bucket) {MaxPerKeyBucket}; max per bucket {MaxPerBucket}; invalid bodies {InvalidBodies}; time-order violations {TimeOrderViolations}; digest mismatches {DigestMismatches}; chain breaks {ChainBreaks}; revision gaps {RevisionGaps}; head matches {HeadMatches}";
    }

    private const string ApprovedEntryPoints = "'prepare','commit','verify','verify-batch','protection-census','index-backfill','index-deep-verify'";

    private const string WorkloadOfEntryPoint = "CASE ev.j->'body'->>'entry_point' WHEN 'protection-census' THEN 'field-protection-verifier' WHEN 'index-backfill' THEN 'search-index-builder' WHEN 'index-deep-verify' THEN 'search-index-builder' ELSE 'business-api' END";

    /// <summary>
    /// M2F3-72 for one organization: ≤ 1 evidence entry per (entry point, bucket), ≤ 7 per bucket, over the WHOLE audit stream (sequential
    /// scan). Also: every evidence body is closed and approved; evidence database times never decrease along revisions (I-1); every audit
    /// entry from <paramref name="chainFrom"/> on has digest = SHA-256(stored bytes) and chains to its predecessor; revisions are
    /// contiguous; the organization row's audit head (revision, digest) equals the newest entry.
    /// </summary>
    private async Task<BoundResult> OracleAsync(string database, Guid organization, long chainFrom = 1)
    {
        await using var c = await OpenAdminAsync(database, "g3-oracle");
        await using (var setup = new NpgsqlCommand("SET default_transaction_read_only = on; SET enable_indexscan = off; SET enable_bitmapscan = off; SET enable_indexonlyscan = off", c))
        {
            await setup.ExecuteNonQueryAsync();
        }

        var o = organization.ToString("D");
        var sql = $"""
            WITH audit AS (
                SELECT journal_stream_revision AS rev, journal_event_kind AS kind, journal_event_body_json AS body_text, journal_event_digest AS digest, journal_previous_digest AS previous
                FROM authority.security_journal_entries WHERE organization_id = '{o}' AND journal_stream_name = 'audit'),
            ev AS (SELECT rev, body_text::jsonb AS j FROM audit WHERE kind = 'EnvelopeCapabilityRefused'),
            per_key AS (SELECT ev.j->'body'->>'entry_point' AS ep, ev.j->'body'->>'evidence_bucket_start_utc' AS bucket, count(*) AS n FROM ev GROUP BY 1, 2),
            per_bucket AS (SELECT ev.j->'body'->>'evidence_bucket_start_utc' AS bucket, count(*) AS n FROM ev GROUP BY 1),
            invalid AS (
                SELECT count(*) AS n FROM ev WHERE NOT (
                    coalesce((SELECT array_agg(k ORDER BY k) FROM jsonb_object_keys(ev.j->'body') k) = ARRAY['client_capability_level','entry_point','evidence_bucket_start_utc','evidence_database_time_utc','evidence_schema_version','organization_id','refusal_code','refusal_observed_on_utc','workload_name'], false)
                    AND ev.j->>'event_kind' = 'EnvelopeCapabilityRefused' AND ev.j->>'stream_name' = 'audit' AND (ev.j->>'stream_revision')::bigint = ev.rev AND (ev.j->>'organization_id')::uuid = '{o}'
                    AND (ev.j->'body'->>'evidence_schema_version')::int = 1 AND ev.j->'body'->>'refusal_code' = 'CLIENT_ENVELOPE_CAPABILITY_INSUFFICIENT' AND (ev.j->'body'->>'client_capability_level')::int = 1
                    AND (ev.j->'body'->>'organization_id')::uuid = '{o}' AND ev.j->'body'->>'entry_point' IN ({ApprovedEntryPoints}) AND ev.j->'body'->>'workload_name' = {WorkloadOfEntryPoint}
                    AND date_trunc('hour', (ev.j->'body'->>'evidence_database_time_utc')::timestamptz) = (ev.j->'body'->>'evidence_bucket_start_utc')::timestamptz)),
            ordered AS (SELECT rev, (ev.j->'body'->>'evidence_database_time_utc')::timestamptz AS t, lag((ev.j->'body'->>'evidence_database_time_utc')::timestamptz) OVER (ORDER BY rev) AS p FROM ev),
            chain AS (SELECT rev, digest, previous, body_text, lag(digest) OVER (ORDER BY rev) AS prior_digest, lag(rev) OVER (ORDER BY rev) AS prior_rev FROM audit)
            SELECT (SELECT count(*) FROM ev)
              || '|' || coalesce((SELECT max(n) FROM per_key), 0)
              || '|' || coalesce((SELECT max(n) FROM per_bucket), 0)
              || '|' || (SELECT n FROM invalid)
              || '|' || (SELECT count(*) FROM ordered WHERE p IS NOT NULL AND t < p)
              || '|' || (SELECT count(*) FROM chain WHERE rev >= {chainFrom} AND digest <> sha256(convert_to(body_text, 'UTF8')))
              || '|' || (SELECT count(*) FROM chain WHERE rev > {chainFrom} AND previous IS DISTINCT FROM prior_digest)
              || '|' || (SELECT count(*) FROM chain WHERE prior_rev IS NOT NULL AND rev <> prior_rev + 1) + (SELECT CASE WHEN min(rev) IS NULL OR min(rev) = 1 THEN 0 ELSE 1 END FROM audit)
              || '|' || (SELECT (s.audit_journal_revision = coalesce((SELECT max(rev) FROM audit), 0))
                                AND (s.latest_audit_journal_digest IS NOT DISTINCT FROM (SELECT digest FROM audit ORDER BY rev DESC LIMIT 1)) FROM authority.organization_authorization_states s WHERE s.organization_id = '{o}')
            """;
        await using var cmd = new NpgsqlCommand(sql, c) { CommandTimeout = 0 };
        var text = Convert.ToString(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)!;
        var p = text.Split('|');
        long L(int i) => long.Parse(p[i], System.Globalization.CultureInfo.InvariantCulture);
        return new BoundResult(L(0), L(1), L(2), L(3), L(4), L(5), L(6), L(7), p[8] == "true", text);
    }

    /// <summary>The oracle over every organization of <paramref name="database"/>: the fleet bound (≤ 7 × organizations per bucket) and each organization's bound.</summary>
    private async Task<(bool Holds, string Detail)> FleetOracleAsync(string database, IReadOnlyList<Org> organizations, long allowedInvalid = 0)
    {
        var failing = new List<string>();
        long total = 0;
        foreach (var org in organizations)
        {
            var o = org.Id;
            var r = await OracleAsync(database, o);
            total += r.Total;
            if (!r.Holds(allowedInvalid))
            {
                failing.Add(o.ToString("N")[..8] + ": " + r);
            }
        }

        var perBucket = await PerBucketAsync(database, organizations);
        var fleetOk = perBucket.All(l => long.Parse(l[(l.LastIndexOf('=') + 1)..], System.Globalization.CultureInfo.InvariantCulture) <= 7L * organizations.Count);
        return (failing.Count == 0 && fleetOk, $"organizations {organizations.Count}; evidence {total}; per bucket [{string.Join(", ", perBucket)}]; failing [{string.Join(" || ", failing)}]");
    }

    /// <summary>Evidence entries per bucket over the whole journal (sequential scan; read-only session), as "bucket=count" lines.</summary>
    private async Task<List<string>> PerBucketAsync(string database, IReadOnlyList<Org> organizations)
    {
        await using var c = await OpenAdminAsync(database, "g3-oracle");
        await using (var setup = new NpgsqlCommand("SET default_transaction_read_only = on; SET enable_indexscan = off; SET enable_bitmapscan = off; SET enable_indexonlyscan = off", c))
        {
            await setup.ExecuteNonQueryAsync();
        }

        var lines = new List<string>();
        await using var cmd = new NpgsqlCommand("SELECT bucket || '=' || n FROM (SELECT journal_event_body_json::jsonb->'body'->>'evidence_bucket_start_utc' AS bucket, count(*) AS n FROM authority.security_journal_entries WHERE journal_stream_name = 'audit' AND journal_event_kind = 'EnvelopeCapabilityRefused' AND organization_id IN (" + string.Join(",", organizations.Select(o => "'" + o.Id.ToString("D") + "'")) + ") GROUP BY 1) s ORDER BY bucket", c) { CommandTimeout = 0 };
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            lines.Add(r.GetString(0));
        }

        return lines;
    }

    /// <summary>The evidence entry points recorded for an organization (by revision), from the stored bodies.</summary>
    private async Task<List<string>> EvidenceEntryPointsAsync(string database, Guid organization) =>
        await LinesAsync(database, $"SELECT journal_event_body_json::jsonb->'body'->>'entry_point' FROM authority.security_journal_entries WHERE organization_id = '{organization:D}' AND journal_stream_name = 'audit' AND journal_event_kind = 'EnvelopeCapabilityRefused' ORDER BY journal_stream_revision");

    private async Task<long> EvidenceCountAsync(string database, Guid organization) =>
        await CountAsync(database, $"SELECT count(*) FROM authority.security_journal_entries WHERE organization_id = '{organization:D}' AND journal_stream_name = 'audit' AND journal_event_kind = 'EnvelopeCapabilityRefused'");

    /// <summary>
    /// Everything of an organization except its audit head (audit_journal_revision, latest_audit_journal_digest, state_modified_on, which
    /// T2 alone may change) and the audit stream: the organization row's other columns, per-stream journal counts and digests, and the
    /// row count and content hash of every Authority table that has an organization_id column. Used to show that a refused T1 committed
    /// nothing and that T2 wrote only its approved footprint.
    /// </summary>
    private async Task<string> FingerprintAsync(string database, Guid organization)
    {
        var o = organization.ToString("D");
        await using var c = await OpenAdminAsync(database, "g3-fingerprint");
        var tables = new List<string>();
        await using (var cmd = new NpgsqlCommand("SELECT quote_ident(c.table_schema) || '.' || quote_ident(c.table_name) FROM information_schema.columns c JOIN information_schema.tables t ON t.table_schema = c.table_schema AND t.table_name = c.table_name WHERE c.table_schema LIKE 'authority%' AND c.column_name = 'organization_id' AND t.table_type = 'BASE TABLE' AND c.table_name NOT IN ('organization_authorization_states', 'security_journal_entries') ORDER BY 1", c))
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
            {
                tables.Add(r.GetString(0));
            }
        }

        var parts = new List<string>();
        await using (var cmd = new NpgsqlCommand($"SELECT (to_jsonb(s) - 'audit_journal_revision' - 'latest_audit_journal_digest' - 'state_modified_on')::text FROM authority.organization_authorization_states s WHERE organization_id = '{o}'", c))
        {
            parts.Add("state=" + Convert.ToString(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
        }

        await using (var cmd = new NpgsqlCommand($"SELECT coalesce(string_agg(journal_stream_name || ':' || n || ':' || h, ',' ORDER BY journal_stream_name), '-') FROM (SELECT journal_stream_name, count(*) n, sum(hashtextextended(x::text, 0)) h FROM authority.security_journal_entries x WHERE organization_id = '{o}' AND journal_stream_name <> 'audit' GROUP BY 1) s", c))
        {
            parts.Add("streams=" + Convert.ToString(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
        }

        await using (var cmd = new NpgsqlCommand($"SELECT count(*) || ':' || coalesce(sum(hashtextextended(x::text, 0)), 0) FROM authority.security_journal_entries x WHERE organization_id = '{o}' AND journal_stream_name = 'audit' AND journal_event_kind <> 'EnvelopeCapabilityRefused'", c))
        {
            parts.Add("audit-other=" + Convert.ToString(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
        }

        foreach (var t in tables)
        {
            await using var cmd = new NpgsqlCommand($"SELECT count(*) || ':' || coalesce(sum(hashtextextended(x::text, 0)), 0) FROM {t} x WHERE organization_id = '{o}'", c) { CommandTimeout = 0 };
            parts.Add(t + "=" + Convert.ToString(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture));
        }

        return string.Join("; ", parts);
    }

    private static string FingerprintDiff(string before, string after)
    {
        if (before == after)
        {
            return "unchanged";
        }

        var a = before.Split("; ").ToHashSet();
        var b = after.Split("; ").ToHashSet();
        return "changed: " + string.Join(" | ", b.Except(a).Select(x => Short(x, 160)));
    }

    /// <summary>The organization's revisions and quarantine (M2F3-56).</summary>
    private async Task<string> RevisionsAsync(string database, Guid organization) =>
        await ScalarAsync(database, $"SELECT 'audit ' || audit_journal_revision || '; authorization ' || authorization_revision || '; projection ' || projection_applied_revision || '; content ' || content_integrity_revision || '; quarantine ' || CASE WHEN integrity_quarantine_enabled THEN coalesce(integrity_quarantine_reason_code, '?') ELSE 'none' END FROM authority.organization_authorization_states WHERE organization_id = '{organization:D}'");

    /// <summary>The runtime role's table, sequence, schema and function privileges and role attributes (sentinel: never changed by this gate).</summary>
    private async Task<string> PrivilegeSentinelAsync(string database) =>
        await ScalarAsync(database, $"""
            SELECT md5(string_agg(x, '|' ORDER BY x)) || ' (' || count(*) || ' entries)' FROM (
                SELECT 'tbl ' || table_schema || '.' || table_name || ' ' || privilege_type AS x FROM information_schema.role_table_grants WHERE grantee = '{RuntimeRole}'
                UNION ALL SELECT 'col ' || table_schema || '.' || table_name || '.' || column_name || ' ' || privilege_type FROM information_schema.role_column_grants WHERE grantee = '{RuntimeRole}'
                UNION ALL SELECT 'fn ' || p.oid::regprocedure || ' ' || has_function_privilege('{RuntimeRole}', p.oid, 'EXECUTE')::text FROM pg_proc p WHERE p.pronamespace::regnamespace::text LIKE 'authority%'
                UNION ALL SELECT 'nsp ' || nspname || ' ' || has_schema_privilege('{RuntimeRole}', oid, 'USAGE')::text || has_schema_privilege('{RuntimeRole}', oid, 'CREATE')::text FROM pg_namespace WHERE nspname LIKE 'authority%' OR nspname = 'public'
                UNION ALL SELECT 'role ' || rolname || ' ' || rolsuper::text || rolcreaterole::text || rolcreatedb::text || rolbypassrls::text || rolinherit::text || rolreplication::text || coalesce(array_to_string(rolconfig, ','), '-') FROM pg_roles WHERE rolname = '{RuntimeRole}'
                UNION ALL SELECT 'member ' || roleid::regrole || ' ' || member::regrole FROM pg_auth_members WHERE member = '{RuntimeRole}'::regrole OR roleid = '{RuntimeRole}'::regrole) s
            """);
}
