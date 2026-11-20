using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Journal;
using AuthorizationAuthority.Application.Features.Journal.Reconciliation;
using Recovery = AuthorizationAuthority.Application.Features.Journal.Recovery;
using AuthorizationAuthority.Application.Features.Journal.Verification;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Infrastructure.Archive;
using AuthorizationAuthority.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// SEC-BL-01 WP-3/WP-4 (C-155) against a disposable PostgreSQL 18 (real migrations and runtime grants) and two separately keyed
/// throwaway Azurite instances, started by <c>scripts/dev/sec-bl-01-wp4-pg-azurite-gate.ps1</c>. The verifier reads as the Authority
/// runtime role in a server-enforced read-only snapshot; tampering is performed as the administrator (J2) with direct SQL. Synthetic data
/// only. Development/Testing evidence, not production evidence.
/// </summary>
public sealed partial class JournalVerificationPostgresIntegrationTests
{
    private static readonly string? Admin = Environment.GetEnvironmentVariable("SECBL01_PG_ADMIN");
    private static readonly string? Runtime = Environment.GetEnvironmentVariable("SECBL01_PG_RUNTIME");
    private static readonly string? RuntimeRole = Environment.GetEnvironmentVariable("SECBL01_PG_RUNTIME_ROLE");
    private static readonly string? RuntimePassword = Environment.GetEnvironmentVariable("SECBL01_PG_RUNTIME_PASSWORD");
    private static readonly SemaphoreSlim SchemaGate = new(1, 1);
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static bool schemaReady;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record PgBody(string NoteText, long CounterValue);

    private static void Require()
    {
        Wp2Azurite.Require();
        Assert.SkipWhen(new[] { Admin, Runtime, RuntimeRole, RuntimePassword }.Any(string.IsNullOrEmpty), "SECBL01_PG_* not set (run scripts/dev/sec-bl-01-wp4-pg-azurite-gate.ps1)");
    }

    private static AuthorityDbContext AdminContext() => new(new DbContextOptionsBuilder<AuthorityDbContext>()
        .UseNpgsql(Admin, n => n.MigrationsHistoryTable("ef_migrations_history", AuthorityDbContext.SchemaName)).Options);

    /// <summary>Once per run: the runtime role (as the cluster bootstrap creates it), the real migrations and the real runtime grants.</summary>
    private static async Task EnsureSchemaAsync()
    {
        await SchemaGate.WaitAsync(Ct);
        try
        {
            if (schemaReady)
            {
                return;
            }

            await using (var connection = new NpgsqlConnection(Admin))
            {
                await connection.OpenAsync(Ct);
                var role = RuntimeRole!;
                Assert.Matches("^[a-z_][a-z0-9_]{0,62}$", role);
                await using var exists = new NpgsqlCommand("SELECT count(*) FROM pg_roles WHERE rolname = @r", connection);
                exists.Parameters.AddWithValue("r", role);
                if ((long)(await exists.ExecuteScalarAsync(Ct))! == 0)
                {
                    await using var create = new NpgsqlCommand($"CREATE ROLE {role} LOGIN PASSWORD '{RuntimePassword!.Replace("'", "''", StringComparison.Ordinal)}' NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS", connection);
                    await create.ExecuteNonQueryAsync(Ct);
                    await using var grant = new NpgsqlCommand($"GRANT CONNECT ON DATABASE {connection.Database} TO {role}", connection);
                    await grant.ExecuteNonQueryAsync(Ct);
                }
            }

            await using var context = AdminContext();
            await context.Database.MigrateAsync(Ct);
            AuthorityRuntimeGrants.Apply(context, RuntimeRole!);
            schemaReady = true;
        }
        finally
        {
            SchemaGate.Release();
        }
    }

    /// <summary>A synthetic organization with <paramref name="rows"/> audit rows, the first <paramref name="delivered"/> archived and Anchored.</summary>
    private static async Task<(Guid Org, OrganizationAuthorizationState State, string ArchiveContainer, string CheckpointContainer)> SeedAsync(int rows, int delivered)
    {
        await EnsureSchemaAsync();
        var org = Guid.CreateVersion7();
        var state = new OrganizationAuthorizationState
        {
            OrganizationAuthorizationStateId = Guid.CreateVersion7(), OrganizationId = org, OrganizationDisplayName = "sec-bl-01 synthetic", StateModifiedOn = DateTimeOffset.UtcNow,
            CheckpointEnforcementModeName = CheckpointEnforcementModes.RequiredBeforeEffective,
        };
        var archiveContainer = Wp2Azurite.NewContainerName();
        var checkpointContainer = Wp2Azurite.NewContainerName();
        await using (var context = AdminContext())
        {
            context.OrganizationStates.Add(state);
            await context.SaveChangesAsync(Ct);
        }

        await AppendAsync(org, rows, delivered, archiveContainer);
        return (org, state, archiveContainer, checkpointContainer);
    }

    /// <summary>Appends rows after the current audit head (as the Authority does) and archives the first <paramref name="deliver"/> of them.</summary>
    private static async Task AppendAsync(Guid org, int rows, int deliver, string archiveContainer, int batch = 2000)
    {
        var archive = Wp2Azurite.NewArchive(archiveContainer);
        for (var offset = 0; offset < rows; offset += batch)
        {
            await using var context = AdminContext();
            var state = await context.OrganizationStates.SingleAsync(s => s.OrganizationId == org, Ct);
            var clock = TimeProvider.System;
            var added = new List<SecurityJournalEntry>();
            for (var i = offset; i < Math.Min(rows, offset + batch); i++)
            {
                state.AuditJournalRevision++;
                var entry = JournalChain.Append(state, JournalStreamNames.Audit, state.AuditJournalRevision, "SyntheticEvent", new PgBody("synthetic " + state.AuditJournalRevision, state.AuditJournalRevision), null, null, clock);
                if (i < deliver)
                {
                    var receipt = await archive.AppendAsync(org, JournalStreamNames.Audit, entry.JournalStreamRevision, Encoding.UTF8.GetBytes(entry.JournalEventBodyJson), entry.JournalEventDigest, Ct);
                    entry.ArchiveDeliveryState = ArchiveDeliveryStates.Anchored;
                    entry.ArchiveReceiptReference = receipt.ReceiptReference;
                    entry.ArchiveReceiptDigest = receipt.ReceiptDigest;
                }

                added.Add(entry);
            }

            context.JournalEntries.AddRange(added);
            await context.SaveChangesAsync(Ct);
        }
    }

    /// <summary>
    /// J2 (privileged AuthorityDb administrator): G08's triggers do not constrain J2, which can bypass them; this helper does so explicitly
    /// (<c>session_replication_role = replica</c>, superuser only) so the detection tests keep exercising J2. G08's own tests use the runtime role
    /// and the administrator without the bypass.
    /// </summary>
    private static async Task AdminSqlAsync(string sql, Guid org)
    {
        await using var connection = new NpgsqlConnection(Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SET session_replication_role = replica; " + sql, connection);
        command.Parameters.AddWithValue("org", org);
        await command.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>md5 over every journal row and the organization row: a run must leave it unchanged (no AuthorityDb write).</summary>
    private static async Task<string> FingerprintAsync(Guid org)
    {
        await using var connection = new NpgsqlConnection(Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT md5(coalesce((SELECT string_agg(j::text, '|' ORDER BY j.journal_stream_name, j.journal_stream_revision) FROM authority.security_journal_entries j WHERE j.organization_id = @org), '') " +
            "|| (SELECT s::text FROM authority.organization_authorization_states s WHERE s.organization_id = @org))", connection);
        command.Parameters.AddWithValue("org", org);
        return (string)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static JournalVerificationReader RuntimeReader(Action<string>? observer = null) => new(Runtime!, observer);

    /// <summary>
    /// WP-7A: the reader shared by the tests that keep one across helpers. Each reader owns a connection pool; the WP-7 helpers created one per
    /// test and never disposed it, so idle pooled connections accumulated until PostgreSQL refused new ones (53300) once the run became faster.
    /// </summary>
    private static readonly Lazy<JournalVerificationReader> SharedRuntimeReader = new(() => new JournalVerificationReader(Runtime!));

    private static async Task<JournalBaselineReport> BaselineAsync(JournalVerificationReader reader, Guid org, string archiveContainer, string checkpointContainer) =>
        await new JournalBaselineService(reader, Wp2Azurite.NewArchive(archiveContainer), Wp2Azurite.NewCheckpointStore(checkpointContainer), Wp2Azurite.NewExceptionStore(checkpointContainer),
            TimeProvider.System)
            .RunAsync(JournalVerificationWorld.BaselineRequest(org, JournalStreamNames.Audit), Ct);

    private static async Task<JournalBaselineReport> BaselineAsync(JournalVerificationReader reader, Guid org, string archiveContainer, string checkpointContainer,
        IReadOnlyList<JournalBaselineExceptionDisposition> dispositions, bool rebaseline = false) =>
        await new JournalBaselineService(reader, Wp2Azurite.NewArchive(archiveContainer), Wp2Azurite.NewCheckpointStore(checkpointContainer), Wp2Azurite.NewExceptionStore(checkpointContainer),
            TimeProvider.System)
            .RunAsync(JournalVerificationWorld.BaselineRequest(org, JournalStreamNames.Audit, dispositions: dispositions, rebaseline: rebaseline), Ct);

    private static JournalVerifier Verifier(JournalVerificationReader reader, string archiveContainer, string checkpointContainer, RecordingAlerts? alerts = null, JournalVerifierOptions? options = null) =>
        new(reader, Wp2Azurite.NewArchive(archiveContainer), Wp2Azurite.NewCheckpointStore(checkpointContainer), new InMemoryJournalVerificationFindingStore(),
            Wp2Azurite.NewExceptionStore(checkpointContainer), alerts ?? new RecordingAlerts(),
            TimeProvider.System, options ?? new JournalVerifierOptions());

    /// <summary>WP-4A (A-1) on real stores: an accepted legacy gap is a Blob record; sweeps cross it as an observation; a re-baseline continues the chain.</summary>
    [Fact]
    public async Task AnAcceptedGapAndARebaselineWorkOnRealStoresWithoutWrites()
    {
        Require();
        var (org, _, archiveContainer, checkpointContainer) = await SeedAsync(30, 30);
        await Wp2Azurite.RawArchiveContainer(archiveContainer).DeleteBlobAsync($"org/{org:D}/audit/{7.ToString("D12", System.Globalization.CultureInfo.InvariantCulture)}.json", cancellationToken: Ct);
        await using var reader = RuntimeReader();
        var disposition = new JournalBaselineExceptionDisposition("BX-PG-GAP-7", "LEGACY_ARCHIVE_GAP", org, JournalStreamNames.Audit, JournalFindingCodes.ArchiveDiscontinuity, null, 7, 7, "C-TEST-PG",
            DateTimeOffset.UtcNow.AddMinutes(-5));
        var baseline = await BaselineAsync(reader, org, archiveContainer, checkpointContainer, [disposition]);
        Assert.Equal((nameof(JournalBaselineOutcome.ExceptionAccepted), "Reduced", 30L), (baseline.Outcome, baseline.AssuranceLevel, baseline.ArchiveContiguousRevision));
        Assert.Equal(["BX-PG-GAP-7"], baseline.AcceptedExceptionRecordsWritten);
        Assert.Equal(StoredObjectStatus.Present, (await Wp2Azurite.NewExceptionStore(checkpointContainer).ReadAsync(org, JournalStreamNames.Audit, "BX-PG-GAP-7", Ct)).Status);

        await AppendAsync(org, 10, 10, archiveContainer);
        var before = await FingerprintAsync(org);
        var verifier = Verifier(reader, archiveContainer, checkpointContainer);
        var incremental = await verifier.SweepAsync(org, JournalStreamNames.Audit, JournalSweepKind.Incremental, Ct);
        Assert.Equal((JournalSweepOutcome.Clean, true, 40L), (incremental.Outcome, incremental.BoundedListing, incremental.NewCheckpoint!.VerifiedRevision));
        var full = await verifier.SweepAsync(org, JournalStreamNames.Audit, JournalSweepKind.Full, Ct);
        Assert.Equal(JournalSweepOutcome.Clean, full.Outcome);
        Assert.Equal("BX-PG-GAP-7", Assert.Single(full.AcceptedObservations!).ExceptionId);

        var rebaseline = await BaselineAsync(reader, org, archiveContainer, checkpointContainer, [], rebaseline: true);
        Assert.Equal((nameof(JournalBaselineOutcome.ExceptionAccepted), 4L, true), (rebaseline.Outcome, rebaseline.Checkpoint!.CheckpointSequence, rebaseline.Rebaseline));
        Assert.Equal(["BX-PG-GAP-7"], rebaseline.CarriedExceptionIds);
        var chain = await JournalVerificationCheckpoints.DiscoverAsync(Wp2Azurite.NewCheckpointStore(checkpointContainer), org, JournalStreamNames.Audit, CheckpointDiscoveryMode.FullChain, 0, 100, Ct);
        Assert.Equal((CheckpointDiscoveryOutcome.Head, 4L, CheckpointAssuranceLevel.Reduced), (chain.Outcome, chain.Head!.CheckpointSequence, chain.Head.AssuranceLevel));
        Assert.Equal(before, await FingerprintAsync(org)); // sweeps and re-baseline never write AuthorityDb
    }

    [Fact]
    public async Task TheSnapshotIsServerEnforcedReadOnlyAndIssuesOnlySelects()
    {
        Require();
        var (org, _, archiveContainer, checkpointContainer) = await SeedAsync(20, 20);
        var statements = new List<string>();
        await using var reader = RuntimeReader(statements.Add);
        await using (var snapshot = (JournalVerificationReader.Snapshot)await reader.OpenSnapshotAsync(Ct))
        {
            Assert.Equal("on", await snapshot.TransactionReadOnlyAsync(Ct));
            var write = await Assert.ThrowsAsync<PostgresException>(() => snapshot.ExecuteForTestingAsync("UPDATE authority.organization_authorization_states SET state_modified_on = now()", Ct));
            Assert.Equal("25006", write.SqlState); // read_only_sql_transaction
        }

        statements.Clear();
        var before = await FingerprintAsync(org);
        Assert.Equal(nameof(JournalBaselineOutcome.Clean), (await BaselineAsync(reader, org, archiveContainer, checkpointContainer)).Outcome);
        Assert.Equal(JournalSweepOutcome.Clean, (await Verifier(reader, archiveContainer, checkpointContainer).SweepAsync(org, JournalStreamNames.Audit, JournalSweepKind.Full, Ct)).Outcome);
        Assert.Equal(before, await FingerprintAsync(org));
        Assert.NotEmpty(statements);
        Assert.All(statements, s => Assert.True(s.StartsWith("SET TRANSACTION READ ONLY", StringComparison.Ordinal) || s.StartsWith("SELECT ", StringComparison.Ordinal), s));
    }

    [Fact]
    public async Task BaselineIncrementalAndFullSweepsOnRealStoresAdvanceTheChainWithoutWrites()
    {
        Require();
        var (org, _, archiveContainer, checkpointContainer) = await SeedAsync(50, 50);
        await using var reader = RuntimeReader();
        var baseline = await BaselineAsync(reader, org, archiveContainer, checkpointContainer);
        Assert.Equal((nameof(JournalBaselineOutcome.Clean), 50L), (baseline.Outcome, baseline.ArchiveContiguousRevision));

        await AppendAsync(org, 20, 20, archiveContainer);
        var before = await FingerprintAsync(org);
        var verifier = Verifier(reader, archiveContainer, checkpointContainer);
        var incremental = await verifier.SweepAsync(org, JournalStreamNames.Audit, JournalSweepKind.Incremental, Ct);
        Assert.Equal((JournalSweepOutcome.Clean, 2L, 70L), (incremental.Outcome, incremental.NewCheckpoint!.CheckpointSequence, incremental.NewCheckpoint.VerifiedRevision));
        var full = await verifier.SweepAsync(org, JournalStreamNames.Audit, JournalSweepKind.Full, Ct);
        Assert.Equal((JournalSweepOutcome.Clean, 3L, 70L), (full.Outcome, full.NewCheckpoint!.CheckpointSequence, full.Metrics.DatabaseRowsInspected));
        Assert.Equal(before, await FingerprintAsync(org));
    }

    [Theory]
    [InlineData("redigest")]
    [InlineData("delete")]
    [InlineData("forged-anchored")]
    [InlineData("truncate")]
    public async Task AdministratorSqlTamperingIsDetected(string attack)
    {
        Require();
        var (org, _, archiveContainer, checkpointContainer) = await SeedAsync(30, 30);
        await using var reader = RuntimeReader();
        Assert.Equal(nameof(JournalBaselineOutcome.Clean), (await BaselineAsync(reader, org, archiveContainer, checkpointContainer)).Outcome);
        await AppendAsync(org, 10, 10, archiveContainer);

        switch (attack)
        {
            case "redigest": // body and digest rewritten consistently for one delivered row (J1/J2)
                await AdminSqlAsync(
                    "UPDATE authority.security_journal_entries SET journal_event_body_json = replace(journal_event_body_json, 'synthetic 35', 'synthetic XX'), " +
                    "journal_event_digest = sha256(convert_to(replace(journal_event_body_json, 'synthetic 35', 'synthetic XX'), 'UTF8')) " +
                    "WHERE organization_id = @org AND journal_stream_name = 'audit' AND journal_stream_revision = 35", org);
                break;
            case "delete": // a delivered interior row removed (J2)
                await AdminSqlAsync("DELETE FROM authority.security_journal_entries WHERE organization_id = @org AND journal_stream_name = 'audit' AND journal_stream_revision = 33", org);
                break;
            case "forged-anchored": // a new row marked as delivered that the archive never received (J1)
                await using (var context = AdminContext())
                {
                    var state = await context.OrganizationStates.SingleAsync(s => s.OrganizationId == org, Ct);
                    state.AuditJournalRevision++;
                    var forged = JournalChain.Append(state, JournalStreamNames.Audit, state.AuditJournalRevision, "SyntheticEvent", new PgBody("forged", state.AuditJournalRevision), null, null, TimeProvider.System);
                    forged.ArchiveDeliveryState = ArchiveDeliveryStates.Anchored;
                    forged.ArchiveReceiptReference = "forged-receipt";
                    context.JournalEntries.Add(forged);
                    await context.SaveChangesAsync(Ct);
                }

                break;
            case "truncate": // restore or deletion below the checkpoint, head reset (J2/J3)
                await AdminSqlAsync("DELETE FROM authority.security_journal_entries WHERE organization_id = @org AND journal_stream_name = 'audit' AND journal_stream_revision > 20", org);
                await AdminSqlAsync("UPDATE authority.organization_authorization_states SET audit_journal_revision = 20, latest_audit_journal_digest = " +
                    "(SELECT journal_event_digest FROM authority.security_journal_entries WHERE organization_id = @org AND journal_stream_name = 'audit' AND journal_stream_revision = 20) " +
                    "WHERE organization_id = @org", org);
                break;
        }

        var alerts = new RecordingAlerts();
        var run = await Verifier(reader, archiveContainer, checkpointContainer, alerts).SweepAsync(org, JournalStreamNames.Audit, JournalSweepKind.Incremental, Ct);
        Assert.Equal(JournalSweepOutcome.Findings, run.Outcome);
        var expected = attack switch
        {
            "redigest" => (JournalFindingCodes.AnchorMismatch, 35L),
            "delete" => (JournalFindingCodes.ChainIntegrityFailure, 33L),
            "forged-anchored" => (JournalFindingCodes.FakeReceipt, 41L),
            _ => (JournalFindingCodes.BehindArchive, 21L),
        };
        Assert.Contains(run.Findings, f => f.FindingCode == expected.Item1 && f.FirstRevision == expected.Item2);
        Assert.NotEmpty(alerts.Raised);
        await using var check = new NpgsqlConnection(Admin);
        await check.OpenAsync(Ct);
        await using var quarantine = new NpgsqlCommand("SELECT integrity_quarantine_enabled FROM authority.organization_authorization_states WHERE organization_id = @org", check);
        quarantine.Parameters.AddWithValue("org", org);
        Assert.False((bool)(await quarantine.ExecuteScalarAsync(Ct))!); // alert-only
    }

    [Fact]
    public async Task TheSweepLockExcludesASecondSnapshotUntilTheFirstEnds()
    {
        Require();
        var (org, _, _, _) = await SeedAsync(1, 1);
        await using var reader = RuntimeReader();
        await using (var first = await reader.OpenSnapshotAsync(Ct))
        {
            Assert.True(await first.TryAcquireSweepLockAsync(org, JournalStreamNames.Audit, Ct));
            await using var second = await reader.OpenSnapshotAsync(Ct);
            Assert.False(await second.TryAcquireSweepLockAsync(org, JournalStreamNames.Audit, Ct));
            Assert.True(await second.TryAcquireSweepLockAsync(org, JournalStreamNames.Content, Ct)); // another stream is independent
        }

        await using var third = await reader.OpenSnapshotAsync(Ct);
        Assert.True(await third.TryAcquireSweepLockAsync(org, JournalStreamNames.Audit, Ct));
    }

    /// <summary>Owner WP-4 §10 measurement (not WP-7 acceptance): baseline, incremental and full sweep on a synthetic organization.</summary>
    [Fact]
    public async Task MeasureBaselineIncrementalAndFullSweep()
    {
        Require();
        var rows = int.TryParse(Environment.GetEnvironmentVariable("SECBL01_PERF_ROWS"), out var configured) ? configured : 20_000;
        var increment = 500;
        var seeding = Stopwatch.StartNew();
        var (org, _, archiveContainer, checkpointContainer) = await SeedAsync(rows, rows);
        seeding.Stop();
        await using var reader = RuntimeReader();
        var process = Process.GetCurrentProcess();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);

        var baselineWatch = Stopwatch.StartNew();
        var baseline = await BaselineAsync(reader, org, archiveContainer, checkpointContainer);
        baselineWatch.Stop();
        Assert.Equal(nameof(JournalBaselineOutcome.Clean), baseline.Outcome);

        // WP-4A (A-1) before/after on the same organization: three equal increments, swept bounded, unbounded (the WP-4 behaviour), bounded.
        var verifier = Verifier(reader, archiveContainer, checkpointContainer);
        var unboundedVerifier = Verifier(reader, archiveContainer, checkpointContainer, options: new JournalVerifierOptions { BoundedIncrementalListing = false });
        var sweeps = new List<(string Label, JournalVerificationRun Run)>();
        foreach (var (label, sweeper) in new[] { ("BoundedA", verifier), ("Unbounded", unboundedVerifier), ("BoundedB", verifier) })
        {
            await AppendAsync(org, increment, increment, archiveContainer);
            var run = await sweeper.SweepAsync(org, JournalStreamNames.Audit, JournalSweepKind.Incremental, Ct);
            Assert.Equal(JournalSweepOutcome.Clean, run.Outcome);
            Assert.Equal(label != "Unbounded", run.BoundedListing);
            sweeps.Add((label, run));
        }

        var full = await verifier.SweepAsync(org, JournalStreamNames.Audit, JournalSweepKind.Full, Ct);
        Assert.Equal(JournalSweepOutcome.Clean, full.Outcome);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        process.Refresh();

        var measurement = new
        {
            Rows = rows,
            Increment = increment,
            SeedingSecondsIncludingArchiveUploads = Math.Round(seeding.Elapsed.TotalSeconds, 1),
            Baseline = new { Milliseconds = baselineWatch.ElapsedMilliseconds, baseline.Metrics.DatabaseRowsInspected, baseline.Metrics.ArchiveObjectsListed, baseline.Metrics.ArchiveObjectsRead, baseline.Metrics.RowBatches },
            Incremental = sweeps.Select(s => new
            {
                s.Label, s.Run.BoundedListing, s.Run.Metrics.ElapsedMilliseconds, s.Run.Metrics.DatabaseRowsInspected, s.Run.Metrics.ArchiveObjectsListed, s.Run.Metrics.ArchiveObjectsRead,
                VerifiedRevision = s.Run.NewCheckpoint!.VerifiedRevision,
            }).ToList(),
            Full = new { full.Metrics.ElapsedMilliseconds, full.Metrics.DatabaseRowsInspected, full.Metrics.ArchiveObjectsListed, full.Metrics.ArchiveObjectsRead },
            ManagedBytesAllocatedDuringRuns = allocated,
            PeakWorkingSetBytes = process.PeakWorkingSet64,
            DetectionLatencyNote = "A-1: newly delivered rows <= T_inc + incremental duration; any change at or below the checkpoint other than the anchor <= T_full + full-sweep duration",
        };
        var json = JsonSerializer.Serialize(measurement, Indented);
        TestContext.Current.TestOutputHelper?.WriteLine(json);
        if (Environment.GetEnvironmentVariable("SECBL01_EVIDENCE_DIR") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "measurement.json"), json.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n", Ct);
        }
    }

    /// <summary>
    /// WP-4A §F measurement (no deletion, compaction or retention): size of checkpoints and the cost of discovering a chain of
    /// <c>SECBL01_CHECKPOINT_COUNT</c> checkpoints (default 2016, one week at the 5-minute incremental interval) in the Blob store.
    /// </summary>
    [Fact]
    public async Task MeasureCheckpointGrowth()
    {
        Require();
        var count = int.TryParse(Environment.GetEnvironmentVariable("SECBL01_CHECKPOINT_COUNT"), out var configured) ? configured : 2016;
        var org = Guid.CreateVersion7();
        var store = Wp2Azurite.NewCheckpointStore(Wp2Azurite.NewContainerName());
        var at = DateTimeOffset.UtcNow.AddDays(-7);
        var digest = new byte[32];
        var writing = Stopwatch.StartNew();
        var discovery = await JournalVerificationCheckpoints.DiscoverAsync(store, org, JournalStreamNames.Audit, CheckpointDiscoveryMode.FullChain, 0, 1000, Ct);
        var sizes = new List<int>();
        for (var sequence = 1; sequence <= count; sequence++)
        {
            System.Security.Cryptography.RandomNumberGenerator.Fill(digest);
            var checkpoint = new JournalVerificationCheckpoint(org, JournalStreamNames.Audit, sequence, sequence * 10L, [.. digest], discovery.HeadDigest, "archive-measure",
                sequence == 1 ? CheckpointWalkKind.Baseline : CheckpointWalkKind.Incremental, CheckpointAssuranceLevel.Reduced, ["BX-MEASURE-0001"], "journal-verifier-dev", at.AddMinutes(5 * sequence));
            sizes.Add(JournalVerificationCheckpoints.Serialize(checkpoint).Length);
            var appended = await JournalVerificationCheckpoints.CreateNextAsync(store, discovery, checkpoint, Ct);
            Assert.Equal(CheckpointAppendOutcome.Created, appended.Outcome);
            discovery = new CheckpointDiscoveryResult(CheckpointDiscoveryOutcome.Head, null, null, checkpoint, appended.CheckpointDigest, sequence, false);
        }

        writing.Stop();
        var headWatch = Stopwatch.StartNew();
        var head = await JournalVerificationCheckpoints.DiscoverAsync(store, org, JournalStreamNames.Audit, CheckpointDiscoveryMode.HeadAndPredecessor, 0, 1000, Ct);
        headWatch.Stop();
        var fullWatch = Stopwatch.StartNew();
        var chain = await JournalVerificationCheckpoints.DiscoverAsync(store, org, JournalStreamNames.Audit, CheckpointDiscoveryMode.FullChain, 0, 1000, Ct);
        fullWatch.Stop();
        Assert.Equal((CheckpointDiscoveryOutcome.Head, (long)count), (chain.Outcome, chain.Head!.CheckpointSequence));
        Assert.Equal(CheckpointDiscoveryOutcome.Head, head.Outcome);

        var measurement = new
        {
            CheckpointCount = count,
            MeanCheckpointBytes = Math.Round(sizes.Average(), 1),
            MaximumCheckpointBytes = sizes.Max(),
            TotalCheckpointBytes = sizes.Sum(),
            WriteSeconds = Math.Round(writing.Elapsed.TotalSeconds, 1),
            HeadAndPredecessorDiscoveryMilliseconds = headWatch.ElapsedMilliseconds,
            FullChainDiscoveryMilliseconds = fullWatch.ElapsedMilliseconds,
            ListedObjects = chain.ListedObjectCount,
            DefaultRatePerStreamPerDay = 288 + 1,
            Note = "Synthetic chain written directly through CreateNextAsync (no sweeps); measures storage and discovery only. Nothing was deleted or compacted.",
        };
        var json = JsonSerializer.Serialize(measurement, Indented);
        TestContext.Current.TestOutputHelper?.WriteLine(json);
        if (Environment.GetEnvironmentVariable("SECBL01_EVIDENCE_DIR") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "checkpoint-growth.json"), json.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n", Ct);
        }
    }

    // ---- SEC-BL-01 WP-5a: D+ on the audit stream through the real AuthorityStore (runtime role) and the Blob archive (Azurite) ----

    private sealed class RuntimeContextFactory(string connection) : IDbContextFactory<AuthorityDbContext>
    {
        public AuthorityDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AuthorityDbContext>().UseNpgsql(connection).Options);
    }

    /// <summary>SEC-BL-01 E-6/E-7: the runtime store composed with the trusted receipts of an archive container (as the Authority host composes it).</summary>
    private static AuthorityStore ReceiptStore(string container, ISecurityEventArchive archive) =>
        new(new RuntimeContextFactory(Runtime!), AuthorizationAuthority.Application.Features.Content.ContentAcceptancePolicy.Resolve(null, null),
            journalReceipts: new TrustedJournalReceipts(archive, Durable(container)));

    private static AuthorityStore RuntimeStore() => new(new RuntimeContextFactory(Runtime!), AuthorizationAuthority.Application.Features.Content.ContentAcceptancePolicy.Resolve(null, null));

    /// <summary>A synthetic organization whose audit stream has <paramref name="rows"/> rows, all Pending, and an archive with a sentinel.</summary>
    private static async Task<(Guid Org, string Container, BlobSecurityEventArchive Archive)> SeedPendingAuditAsync(int rows)
    {
        var (org, _, container, _) = await SeedAsync(rows, 0);
        var archive = Wp2Azurite.NewArchive(container);
        var identity = ArchiveIdentityCodec.NewIdentity(DateTimeOffset.UtcNow);
        Assert.Equal(ArchiveIdentityCreateStatus.Created, (await archive.CreateIdentityAsync(identity, Ct)).Status);
        await PinAsync(container, identity.ArchiveInstanceId, Recovery.DPlusRecoveryApprovalVerifier.Unavailable);
        return (org, container, archive);
    }

    /// <summary>G14-R2 (A-4 §24): the real-store D+ durability of each archive container (verification-store container, pin, recovery keys).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (Recovery.DPlusDurability Durability, string StoreContainer)> Durabilities = new(StringComparer.Ordinal);

    /// <summary>A provisioning record pinning <paramref name="instance"/> in a new verification-store container, and that container's record and checkpoint stores.</summary>
    private static async Task<string> PinAsync(string archiveContainer, string instance, Recovery.DPlusRecoveryApprovalVerifier approvals)
    {
        var storeContainer = Wp2Azurite.NewContainerName();
        var provisioning = Wp2Azurite.NewProvisioningStore(storeContainer);
        Assert.Equal(CheckpointCreateStatus.Created, (await provisioning.CreateRecordAsync(ArchiveProvisioningRecords.Serialize(
            new ArchiveProvisioningRecord(ArchiveProvisioningRecord.SchemaV1, instance, DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()), "gate")), Ct)).Status);
        Durabilities[archiveContainer] = (new Recovery.DPlusDurability(Wp2Azurite.NewRecordStore(storeContainer), provisioning, Wp2Azurite.NewCheckpointStore(storeContainer), approvals), storeContainer);
        return storeContainer;
    }

    private static Recovery.DPlusDurability Durable(string archiveContainer) => Durabilities[archiveContainer].Durability;

    /// <summary>The archive processor of a seeded archive container, with its real-store durability (G14-R2).</summary>
    private static ArchiveProcessor NewProcessor(string container, ISecurityEventArchive archive, TimeProvider clock, ISecurityAlertSink? alerts = null, IJournalVerificationFindingStore? findings = null) =>
        new(RuntimeStore(), archive, clock, alerts, findings, Durable(container));

    private static async Task<List<(long Revision, string State, string? Code)>> AuditRowsAsync(Guid org, string stream = "audit")
    {
        await using var connection = new NpgsqlConnection(Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT journal_stream_revision, archive_delivery_state, last_archive_error_code FROM authority.security_journal_entries " +
            "WHERE organization_id = @org AND journal_stream_name = @stream ORDER BY journal_stream_revision", connection);
        command.Parameters.AddWithValue("org", org);
        command.Parameters.AddWithValue("stream", stream);
        var rows = new List<(long, string, string?)>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
        {
            rows.Add((reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return rows;
    }

    private static async Task<bool> QuarantinedAsync(Guid org)
    {
        await using var connection = new NpgsqlConnection(Admin);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT integrity_quarantine_enabled FROM authority.organization_authorization_states WHERE organization_id = @org", connection);
        command.Parameters.AddWithValue("org", org);
        return (bool)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static string Key(Guid org, long revision) => $"org/{org:D}/audit/{revision.ToString("D12", System.Globalization.CultureInfo.InvariantCulture)}.json";

    [Fact]
    public async Task DPlusAValidAuditStreamKeepsDeliveringOnRealStores()
    {
        Require();
        var (org, container, archive) = await SeedPendingAuditAsync(5);
        var alerts = new RecordingAlerts();
        var processor = NewProcessor(container, archive, TimeProvider.System, alerts);
        Assert.Equal(5, await processor.ProcessOrganizationAsync(org, Ct)); // genesis, then each predecessor confirmed in the same pass
        await AppendAsync(org, 3, 0, container);
        Assert.Equal(3, await processor.ProcessOrganizationAsync(org, Ct)); // predecessor 5 read back from Azurite
        Assert.All(await AuditRowsAsync(org), r => Assert.Equal(ArchiveDeliveryStates.Anchored, r.State));
        Assert.Empty(alerts.Raised);
        Assert.False(await QuarantinedAsync(org));
    }

    [Theory]
    [InlineData("corrupted")]
    [InlineData("missing")]
    public async Task DPlusACorruptedOrMissingArchivedPredecessorPreventsTheAppendAndStopsTheStream(string damage)
    {
        Require();
        var (org, container, archive) = await SeedPendingAuditAsync(3);
        var alerts = new RecordingAlerts();
        var findings = new InMemoryJournalVerificationFindingStore();
        var processor = NewProcessor(container, archive, TimeProvider.System, alerts, findings);
        Assert.Equal(3, await processor.ProcessOrganizationAsync(org, Ct));
        var raw = Wp2Azurite.RawArchiveContainer(container).GetBlobClient(Key(org, 3));
        if (damage == "corrupted")
        {
            var original = (await raw.DownloadContentAsync(Ct)).Value.Content.ToString();
            await raw.UploadAsync(BinaryData.FromString(original.Replace("synthetic 3", "synthetic X", StringComparison.Ordinal)), overwrite: true, Ct); // J4/J5
        }
        else
        {
            await raw.DeleteAsync(cancellationToken: Ct); // J4/J5
        }

        await AppendAsync(org, 3, 0, container); // revisions 4..6
        for (var pass = 0; pass < 3; pass++)
        {
            Assert.Equal(0, await processor.ProcessOrganizationAsync(org, Ct));
        }

        var rows = await AuditRowsAsync(org);
        var expected = damage == "corrupted" ? "DPLUS_LINK_MISMATCH" : "DPLUS_PREDECESSOR_MISSING";
        Assert.Equal((ArchiveDeliveryStates.VerificationFailed, expected), (rows[3].State, rows[3].Code));
        Assert.Equal([(5L, ArchiveDeliveryStates.Pending), (6L, ArchiveDeliveryStates.Pending)], rows.Skip(4).Select(r => (r.Revision, r.State))); // later entries cannot bypass
        Assert.Equal(ArchiveProcessor.StreamStoppedCode, rows[4].Code);
        foreach (var revision in new long[] { 4, 5, 6 })
        {
            Assert.False(await Wp2Azurite.RawArchiveContainer(container).GetBlobClient(Key(org, revision)).ExistsAsync(Ct));
        }

        Assert.Single(findings.Records);
        Assert.Single(alerts.Raised);
        Assert.False(await QuarantinedAsync(org)); // no automatic quarantine

        // J1 resets the refused row to Pending: refused again on the next pass, still nothing archived.
        await AdminSqlAsync("UPDATE authority.security_journal_entries SET archive_delivery_state = 'Pending' WHERE organization_id = @org AND journal_stream_name = 'audit' AND journal_stream_revision = 4", org);
        Assert.Equal(0, await processor.ProcessOrganizationAsync(org, Ct));
        Assert.Equal(ArchiveDeliveryStates.VerificationFailed, (await AuditRowsAsync(org))[3].State);
        Assert.False(await Wp2Azurite.RawArchiveContainer(container).GetBlobClient(Key(org, 4)).ExistsAsync(Ct));
    }

    [Fact]
    public async Task DPlusAnArchiveOutageLeavesTheEntryPendingAndRecoveryDeliversIt()
    {
        Require();
        var (org, container, archive) = await SeedPendingAuditAsync(2);
        var unreachable = new BlobSecurityEventArchive(Microsoft.Extensions.Options.Options.Create(Wp2Azurite.Archive(container, uri: "http://127.0.0.1:9/efarchiveit")));
        Assert.Equal(0, await NewProcessor(container, unreachable, TimeProvider.System, new RecordingAlerts()).ProcessOrganizationAsync(org, Ct));
        var rows = await AuditRowsAsync(org);
        Assert.All(rows, r => Assert.Equal(ArchiveDeliveryStates.Pending, r.State));
        Assert.NotNull(rows[0].Code);
        Assert.False(await QuarantinedAsync(org));
        Assert.Equal(2, await NewProcessor(container, archive, TimeProvider.System).ProcessOrganizationAsync(org, Ct));
        Assert.All(await AuditRowsAsync(org), r => Assert.Equal(ArchiveDeliveryStates.Anchored, r.State));
    }

    [Fact]
    public async Task DPlusAStoppedAuditStreamLeavesAuthorizationAndContentDeliveryUnchanged()
    {
        Require();
        var (org, container, archive) = await SeedPendingAuditAsync(40); // more than one batch of audit rows
        // J1 sets a previous-digest column on the genesis row (body and digest untouched): D+ refuses it (R-4) and the audit stream stops.
        await AdminSqlAsync("UPDATE authority.security_journal_entries SET journal_previous_digest = sha256('forged'::bytea) WHERE organization_id = @org AND journal_stream_name = 'audit' AND journal_stream_revision = 1", org);
        await using (var context = AdminContext())
        {
            var state = await context.OrganizationStates.SingleAsync(s => s.OrganizationId == org, Ct);
            foreach (var stream in new[] { JournalStreamNames.Authorization, JournalStreamNames.Content })
            {
                long revision;
                if (stream == JournalStreamNames.Authorization)
                {
                    revision = ++state.AuthorizationRevision;
                }
                else
                {
                    revision = state.ContentIntegrityRevision + 1;
                    typeof(OrganizationAuthorizationState).GetProperty(nameof(OrganizationAuthorizationState.ContentIntegrityRevision))!.SetValue(state, revision);
                }

                context.JournalEntries.Add(JournalChain.Append(state, stream, revision, "SyntheticEvent", new PgBody(stream, revision), null, null, TimeProvider.System));
            }

            await context.SaveChangesAsync(Ct);
        }

        var processor = NewProcessor(container, archive, TimeProvider.System, new RecordingAlerts(), new InMemoryJournalVerificationFindingStore()); // WP-5a.1: D+ needs a durable store to mark
        Assert.Equal(2, await processor.ProcessOrganizationAsync(org, Ct));
        var audit = await AuditRowsAsync(org);
        Assert.Equal((ArchiveDeliveryStates.VerificationFailed, "DPLUS_CHAIN_INTEGRITY_FAILURE:ENVELOPE_COLUMN_MISMATCH"), (audit[0].State, audit[0].Code)); // R-4 at genesis
        Assert.All(audit.Skip(1), r => Assert.Equal(ArchiveDeliveryStates.Pending, r.State));
        Assert.All(await AuditRowsAsync(org, JournalStreamNames.Authorization), r => Assert.Equal(ArchiveDeliveryStates.Anchored, r.State));
        Assert.All(await AuditRowsAsync(org, JournalStreamNames.Content), r => Assert.Equal(ArchiveDeliveryStates.Anchored, r.State));
        Assert.False(await QuarantinedAsync(org));
    }

    // ---- SEC-BL-01 WP-5a.1: provisioning and durable D+ findings on real stores ----

    /// <summary>A new, empty Authority database in the disposable server (real migrations and runtime grants): zero journal rows.</summary>
    private static async Task<(string AdminConnection, string RuntimeConnection)> CreateFreshDatabaseAsync()
    {
        Require();
        await EnsureSchemaAsync(); // the runtime role exists
        var name = "authority_prov_" + Guid.NewGuid().ToString("N")[..12];
        Assert.Matches("^[a-z0-9_]+$", name);
        await using (var connection = new NpgsqlConnection(Admin))
        {
            await connection.OpenAsync(Ct);
            await using var create = new NpgsqlCommand($"CREATE DATABASE {name}", connection);
            await create.ExecuteNonQueryAsync(Ct);
            await using var grant = new NpgsqlCommand($"GRANT CONNECT ON DATABASE {name} TO {RuntimeRole}", connection);
            await grant.ExecuteNonQueryAsync(Ct);
        }

        var admin = new NpgsqlConnectionStringBuilder(Admin) { Database = name }.ConnectionString;
        await using (var context = new AuthorityDbContext(new DbContextOptionsBuilder<AuthorityDbContext>().UseNpgsql(admin, n => n.MigrationsHistoryTable("ef_migrations_history", AuthorityDbContext.SchemaName)).Options))
        {
            await context.Database.MigrateAsync(Ct);
            AuthorityRuntimeGrants.Apply(context, RuntimeRole!);
        }

        return (admin, new NpgsqlConnectionStringBuilder(Runtime) { Database = name }.ConnectionString);
    }

    private static ArchiveProvisioningService Provisioning(string runtimeConnection, string archiveContainer, string storeContainer) =>
        new(new JournalVerificationReader(runtimeConnection), Wp2Azurite.NewArchive(archiveContainer), Wp2Azurite.NewProvisioningStore(storeContainer), TimeProvider.System);

    [Fact]
    public async Task ProvisioningAFreshDisposableArchiveOnRealStoresAndRefusingEveryEstablishedOrLostState()
    {
        var (admin, runtime) = await CreateFreshDatabaseAsync();
        var archiveContainer = Wp2Azurite.NewContainerName();
        var storeContainer = Wp2Azurite.NewContainerName();

        // Fresh: provisioned; the sentinel carries the record's instance identifier; a rerun is idempotent.
        var first = await Provisioning(runtime, archiveContainer, storeContainer).ProvisionAsync("gate", Ct);
        Assert.Equal(ArchiveProvisioningOutcome.Provisioned, first.Outcome);
        Assert.Equal(first.ArchiveInstanceId, (await Wp2Azurite.NewArchive(archiveContainer).ReadIdentityAsync(Ct)).Identity!.ArchiveInstanceId);
        Assert.Equal(ArchiveProvisioningOutcome.AlreadyProvisioned, (await Provisioning(runtime, archiveContainer, storeContainer).ProvisionAsync("gate", Ct)).Outcome);

        // Journal activity starts, then the whole archive container is deleted (J5): never re-stamped.
        await using (var context = new AuthorityDbContext(new DbContextOptionsBuilder<AuthorityDbContext>().UseNpgsql(admin).Options))
        {
            var state = new OrganizationAuthorizationState
            {
                OrganizationAuthorizationStateId = Guid.CreateVersion7(), OrganizationId = Guid.CreateVersion7(), OrganizationDisplayName = "provisioning gate", StateModifiedOn = DateTimeOffset.UtcNow,
                CheckpointEnforcementModeName = CheckpointEnforcementModes.RequiredBeforeEffective,
            };
            context.OrganizationStates.Add(state);
            state.AuditJournalRevision++;
            context.JournalEntries.Add(JournalChain.Append(state, JournalStreamNames.Audit, 1, "SyntheticEvent", new PgBody("first", 1), null, null, TimeProvider.System));
            await context.SaveChangesAsync(Ct);
        }

        await Wp2Azurite.RawArchiveContainer(archiveContainer).DeleteAsync(cancellationToken: Ct);
        var lost = await Provisioning(runtime, archiveContainer, storeContainer).ProvisionAsync("gate", Ct);
        Assert.Equal((ArchiveProvisioningOutcome.Refused, ArchiveProvisioningReasons.ArchiveLostAfterProvisioning), (lost.Outcome, lost.ReasonCode));
        Assert.False(await Wp2Azurite.RawArchiveContainer(archiveContainer).ExistsAsync(Ct));

        // A database with journal rows (the shared gate database) and brand-new containers: refused, nothing written.
        var activity = await Provisioning(Runtime!, Wp2Azurite.NewContainerName(), Wp2Azurite.NewContainerName()).ProvisionAsync("gate", Ct);
        Assert.Equal(ArchiveProvisioningReasons.JournalActivityPresent, activity.ReasonCode);

        // A fresh database but an archive container that already exists (empty): refused.
        var (_, freshRuntime) = await CreateFreshDatabaseAsync();
        var existing = Wp2Azurite.NewContainerName();
        await Wp2Azurite.RawArchiveContainer(existing).CreateAsync(cancellationToken: Ct);
        var established = await Provisioning(freshRuntime, existing, Wp2Azurite.NewContainerName()).ProvisionAsync("gate", Ct);
        Assert.Equal(ArchiveProvisioningReasons.ArchiveContainerExists, established.ReasonCode);
        Assert.Equal(StoredObjectStatus.Absent, (await Wp2Azurite.NewArchive(existing).ReadIdentityAsync(Ct)).Status);
    }

    [Fact]
    public async Task DPlusAnUnreachableFindingStoreHoldsTheEntryPendingAndTheRealStoreRecordsItBeforeMarking()
    {
        Require();
        var (org, container, archive) = await SeedPendingAuditAsync(2);
        Assert.Equal(2, await NewProcessor(container, archive, TimeProvider.System).ProcessOrganizationAsync(org, Ct));
        await Wp2Azurite.RawArchiveContainer(container).GetBlobClient(Key(org, 2)).DeleteAsync(cancellationToken: Ct); // J4/J5
        await AppendAsync(org, 1, 0, container);

        var storeContainer = Wp2Azurite.NewContainerName();
        var unreachable = Wp2Azurite.NewFindingStore(storeContainer, uri: "http://127.0.0.1:9/" + Wp2Azurite.CheckpointAccount);
        var alerts = new RecordingAlerts();
        Assert.Equal(0, await NewProcessor(container, archive, TimeProvider.System, alerts, unreachable).ProcessOrganizationAsync(org, Ct));
        var rows = await AuditRowsAsync(org);
        Assert.Equal((ArchiveDeliveryStates.Pending, ArchiveProcessor.FindingStoreUnavailableCode), (rows[2].State, rows[2].Code));
        // WP-5a.2 (A-3 SM-5): with the finding store unreachable, discovery is inconclusive and D+ is not evaluated: the stream waits and
        // nothing is fabricated (no refusal alert, no marker).
        Assert.Empty(alerts.Raised);
        Assert.False(await Wp2Azurite.RawArchiveContainer(container).GetBlobClient($"meta/dplus-stops/{org:D}/audit/{3.ToString("D12", System.Globalization.CultureInfo.InvariantCulture)}.json").ExistsAsync(Ct));
        Assert.False(await QuarantinedAsync(org));

        var real = Wp2Azurite.NewFindingStore(storeContainer);
        Assert.Equal(0, await NewProcessor(container, archive, TimeProvider.System, new RecordingAlerts(), real).ProcessOrganizationAsync(org, Ct));
        rows = await AuditRowsAsync(org);
        Assert.Equal((ArchiveDeliveryStates.VerificationFailed, "DPLUS_PREDECESSOR_MISSING"), (rows[2].State, rows[2].Code));
        var listed = new List<string>();
        await foreach (var blob in Wp2Azurite.RawCheckpointContainer(storeContainer).GetBlobsAsync(cancellationToken: Ct))
        {
            listed.Add(blob.Name);
        }

        Assert.Single(listed, n => n.StartsWith($"findings/{org:D}/audit/", StringComparison.Ordinal)); // one durable, create-only finding
        Assert.False(await QuarantinedAsync(org));
    }

    // ---- SEC-BL-01 WP-5a.2 (A-3): durable stop marker on real stores ----

    [Fact]
    public async Task DPlusTheStopMarkerSurvivesRestartRowResetEvidenceReplacementAndMarkerDeletionOnRealStores()
    {
        Require();
        var (org, container, archive) = await SeedPendingAuditAsync(3);
        var storeContainer = Wp2Azurite.NewContainerName();
        Assert.Equal(3, await NewProcessor(container, archive, TimeProvider.System).ProcessOrganizationAsync(org, Ct));
        var raw = Wp2Azurite.RawArchiveContainer(container);
        var genuine = (await raw.GetBlobClient(Key(org, 3)).DownloadContentAsync(Ct)).Value.Content;
        await raw.GetBlobClient(Key(org, 3)).DeleteAsync(cancellationToken: Ct); // J4/J5
        await AppendAsync(org, 2, 0, container); // revisions 4, 5

        var store = Wp2Azurite.NewFindingStore(storeContainer);
        Assert.Equal(0, await NewProcessor(container, archive, TimeProvider.System, new RecordingAlerts(), store).ProcessOrganizationAsync(org, Ct));
        var markerName = $"meta/dplus-stops/{org:D}/audit/{4.ToString("D12", System.Globalization.CultureInfo.InvariantCulture)}.json";
        Assert.True(await raw.GetBlobClient(markerName).ExistsAsync(Ct)); // durable stop in the archive
        Assert.Equal(ArchiveDeliveryStates.VerificationFailed, (await AuditRowsAsync(org))[3].State);

        // Restart, J1 resets the row, J4 restores the genuine predecessor: still stopped (the marker and the finding decide, not the row).
        await AdminSqlAsync("UPDATE authority.security_journal_entries SET archive_delivery_state = 'Pending' WHERE organization_id = @org AND journal_stream_name = 'audit' AND journal_stream_revision = 4", org);
        await raw.GetBlobClient(Key(org, 3)).UploadAsync(genuine, Ct);
        Assert.Equal(0, await NewProcessor(container, archive, TimeProvider.System, new RecordingAlerts(), store).ProcessOrganizationAsync(org, Ct));
        var rows = await AuditRowsAsync(org);
        Assert.Equal((ArchiveDeliveryStates.VerificationFailed, ArchiveDeliveryStates.Pending), (rows[3].State, rows[4].State));
        Assert.False(await raw.GetBlobClient(Key(org, 4)).ExistsAsync(Ct));

        // J4 deletes the marker: the recorded finding names it, so it is reported and recreated; delivery stays stopped.
        await raw.GetBlobClient(markerName).DeleteAsync(cancellationToken: Ct);
        Assert.Equal(0, await NewProcessor(container, archive, TimeProvider.System, new RecordingAlerts(), store).ProcessOrganizationAsync(org, Ct));
        Assert.True(await raw.GetBlobClient(markerName).ExistsAsync(Ct));
        Assert.False(await raw.GetBlobClient(Key(org, 4)).ExistsAsync(Ct));
        var findings = new List<string>();
        await foreach (var blob in Wp2Azurite.RawCheckpointContainer(storeContainer).GetBlobsAsync(cancellationToken: Ct))
        {
            findings.Add(Encoding.UTF8.GetString((await Wp2Azurite.RawCheckpointContainer(storeContainer).GetBlobClient(blob.Name).DownloadContentAsync(Ct)).Value.Content.ToArray()));
        }

        Assert.Contains(findings, f => f.Contains(JournalFindingCodes.DPlusStopRecordIncomplete, StringComparison.Ordinal));
        Assert.False(await QuarantinedAsync(org));
    }

    [Fact]
    public async Task DPlusTheMarkerAloneStopsTheStreamWhileTheFindingStoreIsUnreachableAcrossARestart()
    {
        Require();
        var (org, container, archive) = await SeedPendingAuditAsync(2);
        Assert.Equal(2, await NewProcessor(container, archive, TimeProvider.System).ProcessOrganizationAsync(org, Ct));
        var raw = Wp2Azurite.RawArchiveContainer(container);
        var genuine = (await raw.GetBlobClient(Key(org, 2)).DownloadContentAsync(Ct)).Value.Content;
        await raw.GetBlobClient(Key(org, 2)).DeleteAsync(cancellationToken: Ct);
        await AppendAsync(org, 1, 0, container);

        // Production shape (no finding store): the marker is the durable stop.
        Assert.Equal(0, await NewProcessor(container, archive, TimeProvider.System, new RecordingAlerts()).ProcessOrganizationAsync(org, Ct));
        Assert.Equal((ArchiveDeliveryStates.Pending, ArchiveProcessor.FindingStoreUnavailableCode), ((await AuditRowsAsync(org))[2].State, (await AuditRowsAsync(org))[2].Code));
        await raw.GetBlobClient(Key(org, 2)).UploadAsync(genuine, Ct); // evidence replaced
        Assert.Equal(0, await NewProcessor(container, archive, TimeProvider.System, new RecordingAlerts()).ProcessOrganizationAsync(org, Ct)); // restart
        Assert.False(await raw.GetBlobClient(Key(org, 3)).ExistsAsync(Ct));
        Assert.False(await QuarantinedAsync(org));
    }
}