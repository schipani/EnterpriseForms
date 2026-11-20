using System.Security.Cryptography;
using AuthorizationAuthority.Application.Features.Journal;
using AuthorizationAuthority.Application.Features.Journal.Reconciliation;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Infrastructure.Persistence;
using Npgsql;
using Xunit;
using GateState = AuthorizationAuthority.Contracts.V1.ProjectionGateState;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// SEC-BL-01 G08 / E-6 / E-7 (C-158, contract amendment A-7 §27) on real stores (disposable PostgreSQL 18 with the real migrations and grants,
/// Azurite archive and journal-verification store). The same attacks that succeeded before G08 (docs/evidence/sec-bl-01-g08/before/) now fail:
/// the runtime credential (J1) can no longer rewrite immutable journal fields, reset delivery, renumber revisions, delete or truncate; the
/// administrator is stopped by the triggers unless it bypasses them (J2, superuser only, out of G08's reach); and forged delivery state, by J1
/// or J2, never reopens the real gate, because the gate needs a trusted receipt. Legitimate transitions keep working. The dedicated verifier
/// login can read and cannot write. Synthetic data only.
/// </summary>
public sealed partial class JournalVerificationPostgresIntegrationTests
{
    private static async Task<int> RuntimeSqlAsync(string sql, Guid org, string stream)
    {
        await using var connection = new NpgsqlConnection(Runtime);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("org", org);
        command.Parameters.AddWithValue("stream", stream);
        return await command.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>The administrator WITHOUT the trigger bypass.</summary>
    private static async Task<int> AdminNoBypassSqlAsync(string sql, Guid org, string stream)
    {
        await using var connection = new NpgsqlConnection(Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("org", org);
        command.Parameters.AddWithValue("stream", stream);
        return await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<string> RefusalAsync(Func<Task<int>> attack)
    {
        var refused = await Assert.ThrowsAsync<PostgresException>(attack);
        return refused.SqlState + " " + refused.MessageText;
    }

    private const string Where = " WHERE organization_id = @org AND journal_stream_name = @stream";

    private static readonly string[] WriteRefusals = ["42501", "25006"];

    /// <summary>G08-PG-01: every J1 write outside the delivery columns, every invalid transition, and every delete or truncate is refused.</summary>
    [Fact]
    public async Task G08TheRuntimeCredentialCannotRewriteResetRenumberDeleteOrTruncateTheJournal()
    {
        Require();
        using var approvers = Approvers();
        var rig = await StreamRigAsync(JournalStreamNames.Audit, approvers);
        await AppendStreamAsync(rig.Org, JournalStreamNames.Audit, 3);
        Assert.Equal(3, await rig.Processor().ProcessOrganizationAsync(rig.Org, Ct));
        var before = await FingerprintAsync(rig.Org);
        var audit = JournalStreamNames.Audit;

        Assert.StartsWith("42501", await RefusalAsync(() => RuntimeSqlAsync("UPDATE authority.security_journal_entries SET journal_event_body_json = replace(journal_event_body_json, 'synthetic', 'SYNTHETIC')" + Where, rig.Org, audit)));
        Assert.StartsWith("42501", await RefusalAsync(() => RuntimeSqlAsync("UPDATE authority.security_journal_entries SET journal_stream_revision = journal_stream_revision + 100" + Where, rig.Org, audit)));
        Assert.StartsWith("42501", await RefusalAsync(() => RuntimeSqlAsync("UPDATE authority.security_journal_entries SET journal_event_digest = decode(repeat('00', 32), 'hex')" + Where, rig.Org, audit)));
        Assert.StartsWith("42501", await RefusalAsync(() => RuntimeSqlAsync("DELETE FROM authority.security_journal_entries" + Where, rig.Org, audit)));
        Assert.StartsWith("42501", await RefusalAsync(() => RuntimeSqlAsync("TRUNCATE authority.security_journal_entries", rig.Org, audit)));
        Assert.Contains("Anchored security journal entry is final", await RefusalAsync(() => RuntimeSqlAsync(
            "UPDATE authority.security_journal_entries SET archive_delivery_state = 'Pending'" + Where + " AND journal_stream_revision = 3", rig.Org, audit)), StringComparison.Ordinal);
        Assert.Contains("Anchored security journal entry is final", await RefusalAsync(() => RuntimeSqlAsync(
            "UPDATE authority.security_journal_entries SET archive_receipt_reference = 'replaced'" + Where + " AND journal_stream_revision = 3", rig.Org, audit)), StringComparison.Ordinal);
        Assert.Equal(before, await FingerprintAsync(rig.Org)); // nothing changed
    }

    /// <summary>G08-PG-02: the administrator is stopped by the triggers on immutable fields, deletion and truncation; only an explicit bypass (J2) gets through.</summary>
    [Fact]
    public async Task G08TheTriggersStopTheAdministratorUntilItBypassesThemWhichIsJ2()
    {
        Require();
        using var approvers = Approvers();
        var rig = await StreamRigAsync(JournalStreamNames.Audit, approvers);
        await AppendStreamAsync(rig.Org, JournalStreamNames.Audit, 3);
        Assert.Equal(3, await rig.Processor().ProcessOrganizationAsync(rig.Org, Ct));
        var audit = JournalStreamNames.Audit;
        Assert.Contains("immutable security journal fields", await RefusalAsync(() => AdminNoBypassSqlAsync(
            "UPDATE authority.security_journal_entries SET journal_event_body_json = journal_event_body_json || ' '" + Where, rig.Org, audit)), StringComparison.Ordinal);
        Assert.Contains("never deleted", await RefusalAsync(() => AdminNoBypassSqlAsync("DELETE FROM authority.security_journal_entries" + Where, rig.Org, audit)), StringComparison.Ordinal);
        // TRUNCATE is refused before the trigger by the foreign keys that reference the journal (0A000); TRUNCATE ... CASCADE, which would empty the
        // referencing tables too, is stopped by the statement trigger.
        Assert.StartsWith("0A000", await RefusalAsync(() => AdminNoBypassSqlAsync("TRUNCATE authority.security_journal_entries", rig.Org, audit)));
        Assert.Contains("never truncated", await RefusalAsync(() => AdminNoBypassSqlAsync("TRUNCATE authority.security_journal_entries CASCADE", rig.Org, audit)), StringComparison.Ordinal);
        Assert.Equal(3, (await AuditRowsAsync(rig.Org)).Count);

        // J2: the superuser bypass is outside G08 (documented); the change is then detected by D-R, never accepted.
        await AdminSqlAsync("DELETE FROM authority.security_journal_entries" + Where + " AND journal_stream_revision = 2", rig.Org, audit);
        Assert.Equal(2, (await AuditRowsAsync(rig.Org)).Count);
    }

    /// <summary>G08-PG-03: the legitimate transitions — delivery, refusal, the recovery hint — work as the runtime; invalid ones are refused.</summary>
    [Fact]
    public async Task G08LegitimateDeliveryStateTransitionsWorkAndInvalidOnesAreRefused()
    {
        Require();
        using var approvers = Approvers();
        var rig = await StreamRigAsync(JournalStreamNames.Audit, approvers);
        await AppendStreamAsync(rig.Org, JournalStreamNames.Audit, 4);
        var audit = JournalStreamNames.Audit;
        string Row(long revision) => Where + " AND journal_stream_revision = " + revision;

        Assert.Equal(1, await RuntimeSqlAsync("UPDATE authority.security_journal_entries SET archive_attempt_count = archive_attempt_count + 1, last_archive_error_code = 'retry'" + Row(1), rig.Org, audit));
        Assert.Contains("never decreases", await RefusalAsync(() => RuntimeSqlAsync("UPDATE authority.security_journal_entries SET archive_attempt_count = 0" + Row(1), rig.Org, audit)), StringComparison.Ordinal);
        Assert.Contains("only with its receipt", await RefusalAsync(() => RuntimeSqlAsync("UPDATE authority.security_journal_entries SET archive_delivery_state = 'Anchored'" + Row(1), rig.Org, audit)), StringComparison.Ordinal);
        Assert.Contains("only with the Anchored state", await RefusalAsync(() => RuntimeSqlAsync("UPDATE authority.security_journal_entries SET archive_receipt_reference = 'r'" + Row(1), rig.Org, audit)), StringComparison.Ordinal);
        Assert.Equal(1, await RuntimeSqlAsync("UPDATE authority.security_journal_entries SET archive_delivery_state = 'VerificationFailed', last_archive_error_code = 'DPLUS_X'" + Row(4), rig.Org, audit));
        Assert.Contains("recovery scheduling hint", await RefusalAsync(() => RuntimeSqlAsync("UPDATE authority.security_journal_entries SET archive_delivery_state = 'Pending', last_archive_error_code = NULL" + Row(4), rig.Org, audit)), StringComparison.Ordinal);
        Assert.Contains("recovery scheduling hint", await RefusalAsync(() => RuntimeSqlAsync("UPDATE authority.security_journal_entries SET archive_delivery_state = 'Anchored', archive_receipt_reference = 'r', archive_receipt_digest = decode(repeat('00',32),'hex'), journal_archived_on = now()" + Row(4), rig.Org, audit)), StringComparison.Ordinal);
        Assert.Equal(1, await RuntimeSqlAsync("UPDATE authority.security_journal_entries SET archive_delivery_state = 'Pending', last_archive_error_code = 'DPLUS_RECOVERY_AUTHORIZED:" + Guid.CreateVersion7().ToString("D") + "'" + Row(4), rig.Org, audit));

        // The real delivery path (Pending -> Anchored with its receipt) runs through the triggers as the runtime role.
        Assert.Equal(4, await rig.Processor().ProcessOrganizationAsync(rig.Org, Ct));
        Assert.All(await AuditRowsAsync(rig.Org), r => Assert.Equal(ArchiveDeliveryStates.Anchored, r.State));
    }

    /// <summary>
    /// G08-PG-04 / E-6: J1 can still write the delivery columns of a Pending row (forged Anchored with a fake receipt passes the triggers: the
    /// database cannot tell a fake receipt), and J2 can write anything; neither reopens the real gate, because the gate needs a trusted receipt.
    /// A genuine delivery reopens it.
    /// </summary>
    [Fact]
    public async Task G08ForgedDeliveryStateByJ1OrJ2NeverReopensTheRealGate()
    {
        Require();
        using var approvers = Approvers();
        const string stream = JournalStreamNames.Authorization;
        var genuine = await StreamRigAsync(stream, approvers);
        await AppendStreamAsync(genuine.Org, stream, 2);
        Assert.Equal(GateState.AwaitingArchive, await RealGateAsync(genuine));
        Assert.Equal(2, await genuine.Processor().ProcessOrganizationAsync(genuine.Org, Ct));
        Assert.Equal(GateState.Ready, await RealGateAsync(genuine)); // the positive control

        var (rig, _) = await StoppedStreamAsync(stream, 3, approvers);
        await AppendStreamAsync(rig.Org, stream, 1); // a revocation committed during containment (Pending)
        Assert.Equal(1, await RuntimeSqlAsync("UPDATE authority.security_journal_entries SET archive_delivery_state = 'Anchored', archive_receipt_reference = 'forged', " +
            "archive_receipt_digest = decode(repeat('ab', 32), 'hex'), journal_archived_on = now()" + Where + " AND archive_delivery_state = 'Pending'", rig.Org, stream));
        Assert.Equal(GateState.AwaitingArchive, await RealGateAsync(rig)); // J1 forgery: still closed (VerificationFailed row 4 also still counts)

        await AdminSqlAsync("UPDATE authority.security_journal_entries SET archive_delivery_state = 'Anchored', archive_receipt_reference = 'forged', " +
            "archive_receipt_digest = decode(repeat('ab', 32), 'hex'), journal_archived_on = now()" + Where + " AND archive_delivery_state <> 'Anchored'", rig.Org, stream);
        await AdminSqlAsync("UPDATE authority.organization_authorization_states SET authorization_revision = 3, projection_applied_revision = 3, checkpoint_enforcement_mode_name = 'Asynchronous' WHERE organization_id = @org", rig.Org);
        Assert.Equal(0, (await AuditRowsAsync(rig.Org, stream)).Count(r => r.State != ArchiveDeliveryStates.Anchored)); // nothing "pending" in AuthorityDb
        Assert.Equal(GateState.AwaitingArchive, await RealGateAsync(rig)); // J2 forgery of state, receipt fields, position and mode: still closed
        Assert.Equal(0, await rig.Processor().ProcessOrganizationAsync(rig.Org, Ct)); // and D+ delivers nothing
    }

    /// <summary>G08-PG-05 (OI-DR-2): the dedicated verifier login reads what the verifier needs and can write nothing; the real verifier runs under it.</summary>
    [Fact]
    public async Task G08TheDedicatedVerifierLoginIsReadOnlyAndRunsTheVerifier()
    {
        Require();
        await EnsureSchemaAsync(); // WP-7A: the grants below need the schema; under a method filter no earlier test has created it
        const string login = "authority_journal_verifier_login";
        var password = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        await using (var connection = new NpgsqlConnection(Admin))
        {
            await connection.OpenAsync(Ct);
            await using var exists = new NpgsqlCommand($"SELECT count(*) FROM pg_roles WHERE rolname = '{login}'", connection);
            var sql = (long)(await exists.ExecuteScalarAsync(Ct))! == 0
                ? $"CREATE ROLE {login} LOGIN PASSWORD '{password}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS"
                : $"ALTER ROLE {login} PASSWORD '{password}'";
            await using var create = new NpgsqlCommand(sql + $"; GRANT CONNECT ON DATABASE {connection.Database} TO {login}", connection);
            await create.ExecuteNonQueryAsync(Ct);
        }

        await using (var context = AdminContext())
        {
            AuthorityRuntimeGrants.Apply(context, RuntimeRole!, null, login);
        }

        using var approvers = Approvers();
        var rig = await StreamRigAsync(JournalStreamNames.Content, approvers);
        await AppendStreamAsync(rig.Org, JournalStreamNames.Content, 3);
        Assert.Equal(3, await rig.Processor().ProcessOrganizationAsync(rig.Org, Ct));
        var verifierConnection = new NpgsqlConnectionStringBuilder(Admin) { Username = login, Password = password }.ConnectionString;

        async Task<string> AsVerifierAsync(string sql, bool readWrite = false)
        {
            await using var connection = new NpgsqlConnection(verifierConnection);
            await connection.OpenAsync(Ct);
            // NOINHERIT login: the role is assumed explicitly. default_transaction_read_only is a session default the login can turn off itself,
            // so it is defense in depth only; readWrite turns it off to prove the grants alone refuse writes (mutation MV-G8-DB6, attempt 1).
            await using var set = new NpgsqlCommand($"SET ROLE {AuthorityRuntimeGrants.JournalVerifierRole}" + (readWrite ? "; SET default_transaction_read_only = off" : string.Empty), connection);
            await set.ExecuteNonQueryAsync(Ct);
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("org", rig.Org);
            try
            {
                return "ok " + await command.ExecuteScalarAsync(Ct);
            }
            catch (PostgresException ex)
            {
                return ex.SqlState;
            }
        }

        Assert.Equal("ok 3", await AsVerifierAsync("SELECT count(*) FROM authority.security_journal_entries WHERE organization_id = @org"));
        Assert.StartsWith("ok", await AsVerifierAsync("SELECT content_integrity_revision FROM authority.organization_authorization_states WHERE organization_id = @org"), StringComparison.Ordinal);
        foreach (var write in new[]
                 {
                     "INSERT INTO authority.security_journal_entries (security_journal_entry_id) VALUES (gen_random_uuid())",
                     "UPDATE authority.security_journal_entries SET last_archive_error_code = 'x' WHERE organization_id = @org",
                     "DELETE FROM authority.security_journal_entries WHERE organization_id = @org",
                     "UPDATE authority.organization_authorization_states SET integrity_quarantine_enabled = true WHERE organization_id = @org",
                 })
        {
            Assert.Contains(await AsVerifierAsync(write), WriteRefusals); // permission denied, or read-only transaction
            Assert.Equal("42501", await AsVerifierAsync(write, readWrite: true)); // the grants alone: permission denied
        }

        await using (var connection = new NpgsqlConnection(Admin))
        {
            await connection.OpenAsync(Ct);
            foreach (var (table, privilege, expected) in new[]
                     {
                         ("authority.security_journal_entries", "SELECT", true), ("authority.security_journal_entries", "INSERT", false),
                         ("authority.security_journal_entries", "UPDATE", false), ("authority.security_journal_entries", "DELETE", false),
                         ("authority.security_journal_entries", "TRUNCATE", false), ("authority.security_journal_entries", "TRIGGER", false),
                         ("authority.platform_journal_entries", "INSERT", false), ("authority.platform_journal_entries", "UPDATE", false),
                         ("authority.organization_authorization_states", "UPDATE", false), ("authority.organization_authorization_states", "INSERT", false),
                     })
            {
                await using var check = new NpgsqlCommand($"SELECT has_table_privilege('{AuthorityRuntimeGrants.JournalVerifierRole}', '{table}', '{privilege}')", connection);
                Assert.True(expected == (bool)(await check.ExecuteScalarAsync(Ct))!, table + " " + privilege);
            }

            await using var anyUpdate = new NpgsqlCommand($"SELECT has_any_column_privilege('{AuthorityRuntimeGrants.JournalVerifierRole}', 'authority.security_journal_entries', 'UPDATE')", connection);
            Assert.False((bool)(await anyUpdate.ExecuteScalarAsync(Ct))!);
        }

        Assert.Equal("42501", await AsVerifierAsync("SELECT count(*) FROM authority.authority_relationship_edges"));
        Assert.Equal("42501", await AsVerifierAsync("SELECT projection_zed_token FROM authority.organization_authorization_states WHERE organization_id = @org"));

        // The verifier itself, as this login (role assumed per connection through the reader's connection string Options).
        await using var reader = new JournalVerificationReader(verifierConnection + ";Options=-c role=" + AuthorityRuntimeGrants.JournalVerifierRole);
        var baseline = await new JournalBaselineService(reader, rig.Archive, Wp2Azurite.NewCheckpointStore(rig.StoreContainer), Wp2Azurite.NewExceptionStore(rig.StoreContainer), TimeProvider.System)
            .RunAsync(JournalVerificationWorld.BaselineRequest(rig.Org, JournalStreamNames.Content, createSentinel: false), Ct);
        Assert.Equal(nameof(JournalBaselineOutcome.Clean), baseline.Outcome);
    }
}
