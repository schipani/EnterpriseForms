// E3-M2f-2G PostgreSQL diagnostics (docs/security/E3_M2F2G_VERIFICATION_RECORD.md §13): written to the evidence folder at the end of every
// run. Read-only queries of this run's disposable cluster: per-database transaction, rollback, deadlock and conflict counters, the lock
// settings in force, and the locks still held or awaited at the end (there must be none from the harness).
internal sealed partial class Lab
{
    private async Task WriteDiagnosticsAsync()
    {
        var lines = new List<string> { "E3-M2f-2G PostgreSQL diagnostics (disposable cluster of this run; read-only queries)" };
        try
        {
            lines.Add("settings: " + await ScalarAsync(adminCs, "SELECT string_agg(name || '=' || setting, ', ' ORDER BY name) FROM pg_settings WHERE name IN ('deadlock_timeout', 'lock_timeout', 'log_lock_waits', 'default_transaction_isolation', 'max_connections', 'server_version')"));
            foreach (var row in await RowsAsync(adminCs, "SELECT datname, xact_commit, xact_rollback, deadlocks, conflicts, temp_files FROM pg_stat_database WHERE datname IS NOT NULL ORDER BY datname"))
            {
                lines.Add($"database {row[0]}: commits {row[1]}, rollbacks {row[2]}, deadlocks {row[3]}, conflicts {row[4]}, temp files {row[5]}");
            }

            lines.Add("locks held or awaited at the end by harness sessions (application_name m2e2-*/m2f2g-*): " + await ScalarAsync(adminCs,
                "SELECT count(*) FROM pg_locks l JOIN pg_stat_activity a ON a.pid = l.pid WHERE (a.application_name LIKE 'm2e2-%' OR a.application_name LIKE 'm2f2g-%') AND l.locktype <> 'virtualxid' AND NOT (l.locktype = 'relation' AND l.relation::regclass::text LIKE 'pg_%')"));
            lines.Add("harness sessions idle in transaction at the end: " + await ScalarAsync(adminCs,
                "SELECT count(*) FROM pg_stat_activity WHERE (application_name LIKE 'm2e2-%' OR application_name LIKE 'm2f2g-%') AND state LIKE 'idle in transaction%'"));
            lines.Add("data-key bindings per database are listed in the check evidence; existence-query plans are in m2e2-log.txt (DIAGNOSTIC lines)");
        }
        catch (Exception ex)
        {
            lines.Add("diagnostics unavailable: " + Describe(ex));
        }

        await File.WriteAllLinesAsync(Path.Combine(inputs.EvidenceDir, "postgres-diagnostics.txt"), lines);
    }
}
