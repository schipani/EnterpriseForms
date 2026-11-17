// E3-M2e-2: setup, floors 1 -> 2 -> 3 (with the C-14 re-run), controlled acceptance activation and teardown checks.
using AuthorizationAuthority.Api.Composition;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Contracts.V1;
using System.Security.Cryptography;
using System.Text;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Domain.Features.Content;
using AuthorizationAuthority.Domain.Features.Keys;
using AuthorizationAuthority.Infrastructure.Persistence;
using EnterpriseForms.Integrity.Merkle;
using Microsoft.EntityFrameworkCore;
using Npgsql;

internal sealed partial class Lab
{
    private const string FloorQuery = "SELECT coalesce(string_agg(substring(conname FROM 'ck_content_operation_rule_floor_v([0-9]+)$'), ',' ORDER BY conname), 'none') FROM pg_catalog.pg_constraint WHERE conrelid = 'authority.content_change_operations'::regclass AND conname LIKE 'ck\\_content\\_operation\\_rule\\_floor\\_v%'";
    private const string FloorHistoryQuery = "SELECT coalesce(string_agg(\"MigrationId\", ',' ORDER BY \"MigrationId\"), 'none') FROM authority.ef_migrations_history WHERE \"MigrationId\" LIKE '%ContentAcceptanceRuleFloor%'";
    private const string LedgerHistoryQuery = "SELECT coalesce(string_agg(\"MigrationId\", ',' ORDER BY \"MigrationId\"), 'none') FROM authority.ef_migrations_history WHERE \"MigrationId\" LIKE '%ContentDataKeyBindings%'";
    private const string GrantsQuery = "SELECT coalesce(string_agg(table_schema||'.'||table_name||':'||privilege_type, ',' ORDER BY table_schema, table_name, privilege_type), 'none') FROM information_schema.role_table_grants WHERE grantee = 'authority_runtime'";
    private const string MaintenanceDatabase = "postgres";
    private const string Floor2CopyDatabase = "m2e2_floor2";

    private EnvelopeV2AcceptanceSwitch acceptance = EnvelopeV2AcceptanceSwitch.DarkForTests;
    private Node acc = null!;
    private string grantsBaseline = string.Empty;
    private Org setupOrg = null!;

    private async Task<string> FloorAsync(string? database = null) => await ScalarAsync(database is null ? adminCs : AdminFor(database), FloorQuery);

    // ------------------------------------------------------------------------------------------------ setup
    private async Task SetupAsync()
    {
        CurrentGroup = "setup";
        await Target.AdmitAsync(inputs.AdminCs, MaintenanceDatabase);
        Record("S-00", "Setup", "target identity: the primary and maintenance databases carry this run's nonce, the cluster's system identifier and port 5432 behind the run's loopback port",
            "admitted m2e2gate, postgres", string.Join(",", Target.Databases.Order(StringComparer.Ordinal)), Target.Databases.Contains(PrimaryDatabase) && Target.Databases.Contains(MaintenanceDatabase));
        var remaining = Target.ForbiddenEnvironmentRemaining();
        Record("S-01", "Setup", "inherited connection strings, content settings, Azure settings and environment overrides are removed from the harness process (and so from every child)",
            "none remaining", $"removed [{string.Join(",", scrubbed)}]; remaining [{string.Join(",", remaining)}]", remaining.Count == 0);

        // The ordinary --migrate runs once, before ANY runtime connection exists (F-E2 isolation, D-11).
        var runtimeSessions = await CountAsync("SELECT count(*) FROM pg_stat_activity WHERE usename = 'authority_runtime'");
        var migrated = await HostCommandAsync(adminCs, "m2e2-migrate", "--migrate");
        Record("S-02", "Setup", "the ordinary --migrate runs before any runtime connection: it applies the ledger migration and never a floor", $"runtime sessions 0; exit 0; floors none; ledger {ContentDataKeyLedgerSql.MigrationId}",
            $"runtime sessions {runtimeSessions}; exit {migrated.Code}; floors {await FloorAsync()}; ledger {await ScalarAsync(adminCs, LedgerHistoryQuery)}",
            runtimeSessions == 0 && migrated.Code == 0 && await FloorAsync() == "none" && await ScalarAsync(adminCs, LedgerHistoryQuery) == ContentDataKeyLedgerSql.MigrationId);
        grantsBaseline = await ScalarAsync(adminCs, GrantsQuery);
        Log("runtime grants baseline: " + grantsBaseline);
        var ledgerGrants = await ScalarAsync(adminCs, "SELECT 'select='||has_table_privilege('authority_runtime','authority.content_data_key_bindings','SELECT')::text||' insert='||has_table_privilege('authority_runtime','authority.content_data_key_bindings','INSERT')::text||' update='||has_table_privilege('authority_runtime','authority.content_data_key_bindings','UPDATE')::text||' delete='||has_table_privilege('authority_runtime','authority.content_data_key_bindings','DELETE')::text||' truncate='||has_table_privilege('authority_runtime','authority.content_data_key_bindings','TRUNCATE')::text");
        Record("S-03", "Setup", "runtime role on the ledger: SELECT and INSERT only (L-8); the role is ordinary", "select=true insert=true update=false delete=false truncate=false; false false",
            $"{ledgerGrants}; {await RoleFactsAsync()}", ledgerGrants == "select=true insert=true update=false delete=false truncate=false" && await RoleFactsAsync() == "false false");
        setupOrg = await SeedOrgAsync(30, localKeys!, 1);
    }

    private async Task<string> RoleFactsAsync() => await ScalarAsync(adminCs, "SELECT rolsuper||' '||rolbypassrls FROM pg_roles WHERE rolname='authority_runtime'");

    /// <summary>The runtime grants equal the setup baseline (F-E2 sentinel: the ordinary --migrate never ran during the scenarios).</summary>
    private async Task GrantSentinelAsync(string label)
    {
        var now = await ScalarAsync(adminCs, GrantsQuery);
        Record("G-" + label, "Setup", $"F-E2 sentinel ({label}): the runtime role's table grants equal the setup baseline", "unchanged", now == grantsBaseline ? "unchanged" : "CHANGED: " + now, now == grantsBaseline);
    }

    /// <summary>A disposable copy of <paramref name="source"/> (CREATE DATABASE ... TEMPLATE), commented with the nonce and admitted.</summary>
    private async Task CopyDatabaseAsync(string source, string name)
    {
        NpgsqlConnection.ClearAllPools();
        var maintenance = AdminFor(MaintenanceDatabase);
        await ExecCheckedAsync(maintenance, $"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '{source}' AND pid <> pg_backend_pid()");
        await ExecCheckedAsync(maintenance, $"CREATE DATABASE {name} TEMPLATE {source}");
        await ExecCheckedAsync(maintenance, $"COMMENT ON DATABASE {name} IS 'm2e2-nonce:{inputs.Nonce}'");
        await Target.AdmitAsync(inputs.AdminCs, name);
        Log($"database copy {name} of {source} created and admitted");
    }

    private async Task CreateEmptyDatabaseAsync(string name)
    {
        var maintenance = AdminFor(MaintenanceDatabase);
        await ExecCheckedAsync(maintenance, $"CREATE DATABASE {name}");
        await ExecCheckedAsync(maintenance, $"COMMENT ON DATABASE {name} IS 'm2e2-nonce:{inputs.Nonce}'");
        await Target.AdmitAsync(inputs.AdminCs, name);
    }

    // ------------------------------------------------------------------------------------------------ floors (group F)
    private async Task FloorScenariosAsync()
    {
        CurrentGroup = "F";
        var early = await HostCommandAsync(adminCs, "m2e2-floor3-early", "--apply-content-acceptance-floor", "3");
        Record("F-01", "Floor", "floor 3 cannot be applied before floors 1 and 2 (one step at a time)", "exit 2; floors none", $"exit {early.Code}; floors {await FloorAsync()}", early.Code == 2 && await FloorAsync() == "none");
        var floor1 = await HostCommandAsync(adminCs, "m2e2-floor1", "--apply-content-acceptance-floor", "1");
        Record("F-02", "Floor", "floor 1 applies exactly floor 1", "exit 0; floors 1", $"exit {floor1.Code}; floors {await FloorAsync()}", floor1.Code == 0 && await FloorAsync() == "1");

        // C-14 re-run (D-10): two records whose accepted head holds a REAL M1-core v2 field row (controlled seeding: no build accepted it).
        var downgraded = setupOrg.Records[0];
        var protectedRecord = setupOrg.Records[1];
        var s1 = await SeedAcceptedHeadAsync(downgraded, await V2HeadSourcesAsync(downgraded));
        var s2 = await SeedAcceptedHeadAsync(protectedRecord, await V2HeadSourcesAsync(protectedRecord));
        Record("F-03", "Floor", "setup (controlled test-only mechanism): two records hold an accepted real M1-core Envelope v2 field row in their head", "OK OK", $"{s1} {s2}", s1 == "OK" && s2 == "OK");
        {
            var sources = DowngradeOf(downgraded);
            var v1 = await LegacyPrepareAsync(inputs.Carv1Agent, downgraded, sources);
            var commit = v1.Outcome == "OK" ? await LegacyCommitAsync(inputs.Carv1Agent, downgraded, v1.Operation, v1.Staged!, v1.Rows, v1.Sources) : "not prepared";
            Record("F-04", "Floor", "C-14 (expected historical demonstration, re-run): on floor 1 the CARV-1 build 8915f77 accepts an owner save rewriting the v2 item as v1 (no downgrade guard)",
                "Prepare OK; Commit OK; head revision 2", $"Prepare {v1.Outcome}; Commit {commit}; head revision {(await HeadAsync(downgraded)).Revision}", v1.Outcome == "OK" && commit == "OK" && (await HeadAsync(downgraded)).Revision == 2);
        }

        {
            var rb = Instance("RA-c14", ReleaseA, EnvelopeV2AcceptanceSwitch.Create(null, "Testing", ReleaseA));
            var before = await SnapshotAsync(protectedRecord);
            var prepared = await PrepareAsync(rb, protectedRecord, DowngradeOf(protectedRecord));
            Record("F-05", "Floor", "this build refuses the same downgrade at Prepare (the CARV-2+ guard), before anything is signed or stored", "ENVELOPE_INVALID: ENVELOPE_DOWNGRADE; record unchanged",
                $"{prepared.Message}; {Diff(before, await SnapshotAsync(protectedRecord))}", prepared.Message == "ENVELOPE_INVALID: " + EnvelopeV2Rules.Reasons.Downgrade && before == await SnapshotAsync(protectedRecord));
        }

        var floor2 = await HostCommandAsync(adminCs, "m2e2-floor2", "--apply-content-acceptance-floor", "2");
        Record("F-06", "Floor", "floor 2 applies exactly floor 2 (floor 1 dropped)", "exit 0; floors 2", $"exit {floor2.Code}; floors {await FloorAsync()}", floor2.Code == 0 && await FloorAsync() == "2");
        {
            var before = await SnapshotAsync(protectedRecord);
            var ops = await OpsAsync();
            var v1 = await LegacyPrepareAsync(inputs.Carv1Agent, protectedRecord, DowngradeOf(protectedRecord));
            Record("F-07", "Floor", "C-14 remedy (re-run): on floor 2 the CARV-1 build can no longer prepare the downgrade; nothing persisted", "refused (ck_content_operation_rule_floor_v2); no operation; record unchanged",
                $"{v1.Outcome}; ops {ops}->{await OpsAsync()}; {Diff(before, await SnapshotAsync(protectedRecord))}",
                v1.Outcome != "OK" && v1.Outcome.Contains("ck_content_operation_rule_floor_v2", StringComparison.Ordinal) && ops == await OpsAsync() && before == await SnapshotAsync(protectedRecord));
        }

        await CopyDatabaseAsync(PrimaryDatabase, Floor2CopyDatabase);
        var migratedAgain = await HostCommandAsync(adminCs, "m2e2-migrate-2", "--migrate");
        Record("F-08", "Floor", "the ordinary --migrate (run with no runtime writer active) never installs floor 3", "exit 0; floors 2", $"exit {migratedAgain.Code}; floors {await FloorAsync()}", migratedAgain.Code == 0 && await FloorAsync() == "2");

        {
            await using var held = new NpgsqlConnection(Target.Require(new NpgsqlConnectionStringBuilder(runtimeCs) { ApplicationName = "m2e2-held carv-v2", Pooling = false }.ConnectionString));
            await held.OpenAsync();
            var refused = await HostCommandAsync(adminCs, "m2e2-floor3-session", "--apply-content-acceptance-floor", "3");
            Record("F-09", "Floor", "floor 3 is refused while a session able to write operations carries a carv-v marker below 3", "exit 3; floors 2", $"exit {refused.Code}; floors {await FloorAsync()}", refused.Code == 3 && await FloorAsync() == "2");
        }

        {
            var lagging = await SeedPreparedAsync(setupOrg.Records[2], Owner(setupOrg.Records[2], Rows.V1Field(setupOrg.Id, setupOrg.Records[2].Id, Guid.CreateVersion7(), Guid.CreateVersion7(), 40)), 2);
            var refused = await HostCommandAsync(adminCs, "m2e2-floor3-inflight", "--apply-content-acceptance-floor", "3");
            await ExecCheckedAsync(adminCs, $"UPDATE authority.content_change_operations SET operation_state_name = 'Aborted' WHERE content_change_operation_id = '{lagging.Operation}'");
            Record("F-10", "Floor", "floor 3 is refused while an unexpired operation prepared below version 3 is in flight", "seed OK; exit 3; floors 2", $"seed {lagging.Seeded}; exit {refused.Code}; floors {await FloorAsync()}",
                lagging.Seeded == "OK" && refused.Code == 3 && await FloorAsync() == "2");
        }

        {
            await ExecCheckedAsync(adminCs, "ALTER TABLE authority.content_data_key_bindings DISABLE TRIGGER trg_content_data_key_binding_guard");
            var refused = await HostCommandAsync(adminCs, "m2e2-floor3-ledger", "--apply-content-acceptance-floor", "3");
            await ExecCheckedAsync(adminCs, "ALTER TABLE authority.content_data_key_bindings ENABLE TRIGGER trg_content_data_key_binding_guard");
            Record("F-11", "Floor", "floor 3 is refused while the data-key ledger objects are not exactly as created (guard disabled)", "exit 3; floors 2", $"exit {refused.Code}; floors {await FloorAsync()}", refused.Code == 3 && await FloorAsync() == "2");
        }

        {
            // Concurrent writers during the raise run THIS build (A-1 capable): no A-1-incompatible build runs concurrently with a raise (R-A1-1).
            var writers = Enumerable.Range(0, 4).Select(i => Instance("RB2-" + i, ReleaseB2, EnvelopeV2AcceptanceSwitch.Create(null, "Testing", ReleaseB2))).ToList();
            var stop = false;
            var outcomes = new System.Collections.Concurrent.ConcurrentBag<string>();
            var load = writers.Select((w, i) => Task.Run(async () =>
            {
                var record = setupOrg.Records[10 + i];
                var n = 0;
                while (!Volatile.Read(ref stop) && n < 8)
                {
                    outcomes.Add(await WriteAsync(w, record, Owner(record, Rows.V1Field(setupOrg.Id, record.Id, record.TextField, Guid.CreateVersion7(), (byte)(50 + n)))));
                    n++;
                }
            })).ToList();
            await Task.Delay(300);
            var applied = await HostCommandAsync(adminCs, "m2e2-floor3", "--apply-content-acceptance-floor", "3");
            Volatile.Write(ref stop, true);
            await Task.WhenAll(load);
            var live = await ScalarAsync(adminCs, "SELECT pg_get_constraintdef(oid) FROM pg_catalog.pg_constraint WHERE conrelid = 'authority.content_change_operations'::regclass AND conname = 'ck_content_operation_rule_floor_v3'");
            var unequal = await CountAsync("SELECT count(*) FROM authority.content_change_operations WHERE operation_state_name = 'Committed' AND prepare_acceptance_rule_version IS DISTINCT FROM commit_acceptance_rule_version AND prepare_acceptance_rule_version IS NOT NULL AND prepare_acceptance_rule_version >= 2");
            Record("F-12", "Floor", "floor 3 applied while four Release B (floor 2) writers of this build run: exit 0, exact definition, all three floors recorded, every write committed, committed P = C",
                $"exit 0; floors 3; definition exact; history 1,2,3; all OK; P<>C 0",
                $"exit {applied.Code}; floors {await FloorAsync()}; definition {(live == ContentAcceptanceFloor3Sql.ExpectedDefinition ? "exact" : live)}; history {await ScalarAsync(adminCs, FloorHistoryQuery)}; outcomes {string.Join(" / ", outcomes.Distinct())} x{outcomes.Count}; P<>C {unequal}",
                applied.Code == 0 && await FloorAsync() == "3" && live == ContentAcceptanceFloor3Sql.ExpectedDefinition && outcomes.Count > 0 && outcomes.All(o => o == "OK") && unequal == 0
                && await ScalarAsync(adminCs, FloorHistoryQuery) == $"{ContentAcceptanceFloorSql.MigrationId},{ContentAcceptanceFloor2Sql.MigrationId},{ContentAcceptanceFloor3Sql.MigrationId}");
        }

        {
            var again = await HostCommandAsync(adminCs, "m2e2-floor3-again", "--apply-content-acceptance-floor", "3");
            var lower = await HostCommandAsync(adminCs, "m2e2-floor2-after-3", "--apply-content-acceptance-floor", "2");
            Record("F-13", "Floor", "monotonic: re-applying floor 3 is a no-op; applying floor 2 after floor 3 is refused (never lowered)", "exit 0, floors 3; exit 2, floors 3",
                $"exit {again.Code}, floors {await FloorAsync()}; exit {lower.Code}, floors {await FloorAsync()}", again.Code == 0 && lower.Code == 2 && await FloorAsync() == "3");
        }

        {
            var before = await SnapshotAsync(setupOrg.Records[3]);
            var ops = await OpsAsync();
            var v2 = await LegacyPrepareAsync(inputs.Carv2Agent, setupOrg.Records[3], Owner(setupOrg.Records[3]));
            Record("F-14", "Floor", "the CARV-2 build a1127a1 cannot prepare after floor 3 (the database refuses its Prepare insert)", "refused (ck_content_operation_rule_floor_v3); nothing persisted",
                $"{v2.Outcome}; ops {ops}->{await OpsAsync()}; {Diff(before, await SnapshotAsync(setupOrg.Records[3]))}",
                v2.Outcome != "OK" && v2.Outcome.Contains("ck_content_operation_rule_floor_v3", StringComparison.Ordinal) && ops == await OpsAsync() && before == await SnapshotAsync(setupOrg.Records[3]));
        }

        await GrantSentinelAsync("after-floors");
    }

    private async Task QuietFloorsAsync()
    {
        CurrentGroup = "setup";
        var f1 = await HostCommandAsync(adminCs, "m2e2-floor1", "--apply-content-acceptance-floor", "1");
        var f2 = await HostCommandAsync(adminCs, "m2e2-floor2", "--apply-content-acceptance-floor", "2");
        await CopyDatabaseAsync(PrimaryDatabase, Floor2CopyDatabase);
        var f3 = await HostCommandAsync(adminCs, "m2e2-floor3", "--apply-content-acceptance-floor", "3");
        Record("S-04", "Setup", "floors 1 -> 2 -> 3 applied in order (focused run without group F)", "0 0 0; floors 3", $"{f1.Code} {f2.Code} {f3.Code}; floors {await FloorAsync()}", f1.Code == 0 && f2.Code == 0 && f3.Code == 0 && await FloorAsync() == "3");
    }

    private async Task<List<object>> V2HeadSourcesAsync(Rec record)
    {
        var batch = await BatchAsync(record);
        return Owner(record, batch.Seal(Guid.CreateVersion7(), Guid.CreateVersion7(), "head-secret"));
    }

    /// <summary>The owner save that replaces every v2 field item of the head with a v1 envelope of the same item (the C-14 downgrade).</summary>
    private static List<object> DowngradeOf(Rec record) => Owner(record, [.. record.Items.Select(s => s is EnterpriseForms.Domain.Features.FormRecords.FieldEncryptedValue { EncryptionEnvelopeVersion: 2 } v
        ? (object)Rows.V1Field(v.TenantId, v.FormRecordId, v.FieldDefinitionId, v.FieldValueItemId, 77) : s)]);

    // ------------------------------------------------------------------------------------------------ controlled seeding (admin session; disposable database only)
    private async Task<string> SeedAcceptedHeadAsync(Rec record, List<object> sources)
    {
        try
        {
            var head = await HeadAsync(record);
            var operation = Guid.CreateVersion7();
            var revision = head.Revision + 1;
            var rows = sources.Select(s => Rows.Encode(s, operation, revision)).ToList();
            var key = new IntegrityKeyReference(record.Org.Id, IntegrityKeyPurposes.RowIntegrityV1, "m2d-key-" + record.Org.Id.ToString("N"), 1);
            var signed = new List<(Staged Item, byte[] Signature)>();
            foreach (var item in rows)
            {
                signed.Add((item, (await mac.SignAsync(key, item.Bytes, CancellationToken.None)).Mac));
            }

            var manifest = MerkleManifestV1.Build(signed.Select(s => new MerkleLeafInput((MerkleItemKind)(byte)s.Item.Kind, s.Item.ItemId, s.Signature, SHA256.HashData(s.Item.Bytes), (ulong)revision, operation)));
            var now = DateTimeOffset.UtcNow;
            var seedCs = Target.Require(new NpgsqlConnectionStringBuilder(adminCs) { Options = "-c session_replication_role=replica", ApplicationName = "m2e2-seed-head" }.ConnectionString);
            await using var context = new AuthorityDbContext(new DbContextOptionsBuilder<AuthorityDbContext>().UseNpgsql(seedCs).Options);
            context.ContentOperations.Add(new ContentChangeOperation
            {
                ContentChangeOperationId = operation, OrganizationId = record.Org.Id, FormRecordId = record.Id, ActorIdentityBindingId = record.Org.OwnerBinding,
                OperationIdempotencyKey = "m2e2-head-" + operation.ToString("N"), OperationRequestDigest = SHA256.HashData(operation.ToByteArray()),
                ExpectedAuthorizationRevision = 7, ExpectedContentRevision = head.Revision, CandidateContentRevision = revision, CandidateKeyVersion = 1,
                CandidateManifestRoot = manifest.MerkleRoot, CandidateLeafIndexDigest = manifest.LeafIndexDigest, CandidateLeafCount = (long)manifest.LeafCount,
                OperationStateName = ContentOperationStates.Committed, CorrelationIdentifier = Guid.CreateVersion7(), OperationCreatedOn = now, OperationExpiresOn = now.AddMinutes(10),
                OperationCommittedOn = now, PrepareAcceptanceRuleVersion = 1, CommitAcceptanceRuleVersion = 1,
            });
            var commit = new RecordManifestCommit
            {
                RecordManifestCommitId = Guid.CreateVersion7(), OrganizationId = record.Org.Id, FormRecordId = record.Id, CommittedOperationId = operation, CommittedContentRevision = revision,
                ManifestSchemaVersion = 1, ManifestAlgorithmVersion = 1, RowSignatureKeyVersion = 1, ManifestLeafCount = (long)manifest.LeafCount, ManifestRootDigest = manifest.MerkleRoot,
                ManifestLeafIndexDigest = manifest.LeafIndexDigest, ManifestCommittedOn = now,
            };
            context.ManifestCommits.Add(commit);
            context.ManifestLeaves.AddRange(manifest.SortedLeaves.Select((leaf, position) => new RecordManifestLeaf
            {
                RecordManifestLeafId = Guid.CreateVersion7(), RecordManifestCommitId = commit.RecordManifestCommitId, ManifestLeafPosition = position, ManifestItemKey = leaf.ItemKey,
                ManifestItemKindCode = (byte)leaf.Kind, ManifestItemId = leaf.ItemId, CanonicalPayloadDigest = leaf.CanonicalPayloadDigest.ToArray(), RowSignature = leaf.RowSignature.ToArray(),
                ManifestLeafDigest = manifest.LeafHashes[position],
            }));
            var stored = await context.ContentHeads.AsTracking().SingleAsync(h => h.OrganizationId == record.Org.Id && h.FormRecordId == record.Id);
            stored.CommittedContentRevision = revision;
            stored.CommittedOperationId = operation;
            stored.CurrentRecordManifestCommitId = commit.RecordManifestCommitId;
            stored.CommittedManifestRoot = manifest.MerkleRoot;
            stored.SignatureKeyVersion = 1;
            stored.ContentHeadModifiedOn = now;
            await context.SaveChangesAsync();
            record.Accepted = [.. signed.Select(s => (s.Item, s.Signature))];
            record.Sources = sources;
            return "OK";
        }
        catch (Exception ex)
        {
            Log("seed head failed: " + ex);
            return Describe(ex);
        }
    }

    /// <summary>A signed Prepared operation recorded without running Prepare (forged operations; admin session; every CHECK applies).</summary>
    private async Task<(Guid Operation, List<StagedContentItem> Staged, string Seeded)> SeedPreparedAsync(Rec record, List<object> sources, int? prepareRuleVersion)
    {
        var operation = Guid.CreateVersion7();
        var head = await HeadAsync(record);
        var revision = head.Revision + 1;
        var rows = sources.Select(s => Rows.Encode(s, operation, revision)).ToList();
        var key = new IntegrityKeyReference(record.Org.Id, IntegrityKeyPurposes.RowIntegrityV1, "m2d-key-" + record.Org.Id.ToString("N"), 1);
        var signed = new List<(Staged Item, byte[] Signature)>();
        foreach (var item in rows)
        {
            signed.Add((item, (await mac.SignAsync(key, item.Bytes, CancellationToken.None)).Mac));
        }

        var manifest = MerkleManifestV1.Build(signed.Select(s => new MerkleLeafInput((MerkleItemKind)(byte)s.Item.Kind, s.Item.ItemId, s.Signature, SHA256.HashData(s.Item.Bytes), (ulong)revision, operation)));
        var now = DateTimeOffset.UtcNow;
        var seeded = "OK";
        try
        {
            await using var context = new AuthorityDbContext(new DbContextOptionsBuilder<AuthorityDbContext>().UseNpgsql(Target.Require(new NpgsqlConnectionStringBuilder(adminCs) { ApplicationName = "m2e2-seed-operation" }.ConnectionString)).Options);
            context.ContentOperations.Add(new ContentChangeOperation
            {
                ContentChangeOperationId = operation, OrganizationId = record.Org.Id, FormRecordId = record.Id, ActorIdentityBindingId = record.Org.OwnerBinding,
                OperationIdempotencyKey = "m2e2-seeded-" + operation.ToString("N"), OperationRequestDigest = SHA256.HashData(operation.ToByteArray()),
                ExpectedAuthorizationRevision = 7, ExpectedContentRevision = head.Revision, CandidateContentRevision = revision, CandidateKeyVersion = 1,
                CandidateManifestRoot = manifest.MerkleRoot, CandidateLeafIndexDigest = manifest.LeafIndexDigest, CandidateLeafCount = (long)manifest.LeafCount,
                OperationStateName = ContentOperationStates.Prepared, CorrelationIdentifier = Guid.CreateVersion7(), OperationCreatedOn = now, OperationExpiresOn = now.AddMinutes(10),
                PrepareAcceptanceRuleVersion = prepareRuleVersion,
            });
            context.CandidateLeaves.AddRange(manifest.SortedLeaves.Select((leaf, i) => new ContentCandidateLeaf
            {
                ContentCandidateLeafId = Guid.CreateVersion7(), ContentChangeOperationId = operation, ManifestItemKey = leaf.ItemKey, ManifestItemKindCode = (byte)leaf.Kind,
                ManifestItemId = leaf.ItemId, MutationKindName = ContentMutationKinds.Update, CanonicalPayloadDigest = leaf.CanonicalPayloadDigest.ToArray(),
                RowSignature = leaf.RowSignature.ToArray(), LeafDigest = manifest.LeafHashes[i],
            }));
            await context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            seeded = Describe(ex);
        }

        Log($"seed P={prepareRuleVersion?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL"} op={operation}: {seeded}");
        return (operation, [.. signed.Select(s => new StagedContentItem(s.Item.Kind, s.Item.ItemId, s.Item.Base64, Convert.ToBase64String(s.Signature)))], seeded);
    }

    /// <summary>A ledger row in literal SQL (test-only values generated here; no external input).</summary>
    private static string BindingSql(Guid id, Guid organization, Guid record, Guid operation, short kind, int count, byte[] commitment) =>
        "INSERT INTO authority.content_data_key_bindings (content_data_key_binding_id, data_key_identifier, organization_id, form_record_id, envelope_payload_kind_code, data_key_commitment_bytes, binding_item_count, binding_content_change_operation_id, data_key_bound_on) "
        + $"VALUES ('{Guid.CreateVersion7()}', '{id}', '{organization}', '{record}', {kind}, '\\x{Convert.ToHexStringLower(commitment)}', {count}, '{operation}', now())";

    // ------------------------------------------------------------------------------------------------ controlled acceptance activation (S)
    private async Task AcceptanceSetupAsync()
    {
        CurrentGroup = "S";
        await Target.ReverifyAllAsync(inputs.AdminCs);
        Record("S-10", "Setup", "target identity re-verified for every admitted database immediately before acceptance is enabled", "all admitted", string.Join(",", Target.Databases.Order(StringComparer.Ordinal)), true);
        var guard = Guard(ReleaseB3);
        var (ready, detail) = await guard.RefreshAsync(CancellationToken.None);
        Record("S-11", "Setup", "Release B with RequiredFloor = 3: the readiness guard is ready on floor 3 with the ledger migration", "True content acceptance rules v3 (floor 3); phase=ReleaseB requiredFloor=3",
            $"{ready} {detail}", ready && detail == "content acceptance rules v3 (floor 3); phase=ReleaseB requiredFloor=3");

        string Refusal(Func<object> create)
        {
            try
            {
                create();
                return "started";
            }
            catch (InvalidOperationException ex)
            {
                return ex.Message.Contains("must not be enabled outside Development/Testing", StringComparison.Ordinal) ? "refused: environment"
                    : ex.Message.Contains("requires ContentAcceptance:EnforcementPhase", StringComparison.Ordinal) ? "refused: policy" : "refused: " + ex.Message;
            }
        }

        var staging = Refusal(() => EnvelopeV2AcceptanceSwitch.Create("true", "Staging", ReleaseB3));
        var production = Refusal(() => EnvelopeV2AcceptanceSwitch.Create("true", "Production", ReleaseB3));
        var floor2 = Refusal(() => EnvelopeV2AcceptanceSwitch.Create("true", "Testing", ReleaseB2));
        var releaseA = Refusal(() => EnvelopeV2AcceptanceSwitch.Create("true", "Testing", ReleaseA));
        Record("S-12", "Setup", "the switch refuses to be configured on outside Development/Testing (Staging, Production) and without Release B RequiredFloor 3 (floor 2, Release A)",
            "refused: environment x2; refused: policy x2", $"{staging}; {production}; {floor2}; {releaseA}",
            staging == "refused: environment" && production == "refused: environment" && floor2 == "refused: policy" && releaseA == "refused: policy");

        // The approved literal configuration, inside this disposable process only.
        acceptance = EnvelopeV2AcceptanceSwitch.Create("true", "Testing", ReleaseB3);
        acc = Instance("ACC", ReleaseB3, acceptance);
        Record("S-13", "Setup", "the Testing switch is configured on with its static prerequisites; acceptance is NOT effective before the exact policy is recorded", "configured True; prerequisites True; enabled False",
            $"configured {acceptance.ConfiguredEnabled}; prerequisites {acceptance.StaticPrerequisitesHold}; enabled {acceptance.Enabled}", acceptance.ConfiguredEnabled && acceptance.StaticPrerequisitesHold && !acceptance.Enabled);

        {
            var record = setupOrg.Records[20];
            var batch = await BatchAsync(record);
            var signatures = mac.Signatures;
            var bindings = await BindingCountAsync();
            var ops = await OpsAsync();
            var prepared = await PrepareAsync(acc, record, Owner(record, batch.Seal("not yet")));
            Record("S-14", "Negative", "missing policy observation: with the switch configured on but the policy unrecorded, a New v2 group is refused at Prepare (nothing signed, no operation, no binding)",
                "ENVELOPE_INVALID: ENVELOPE_VERSION_NOT_ENABLED; unchanged", $"{prepared.Message}; signatures {signatures}->{mac.Signatures}; bindings {bindings}->{await BindingCountAsync()}; ops {ops}->{await OpsAsync()}",
                prepared.Message == "ENVELOPE_INVALID: " + EnvelopeV2Rules.Reasons.VersionNotEnabled && signatures == mac.Signatures && bindings == await BindingCountAsync() && ops == await OpsAsync());
        }

        var recorded = await ObserveAsync(acceptance);
        var entry = await ScalarAsync(adminCs, $"SELECT coalesce((SELECT journal_event_body_json FROM authority.platform_journal_entries WHERE journal_event_kind = '{EnvelopeAcceptancePolicyObserver.EventKind}' ORDER BY platform_journal_revision DESC LIMIT 1), 'none')");
        Record("S-15", "Setup", "the effective policy is recorded on the disposable platform journal (in-memory archive); acceptance becomes effective only then",
            $"{EnvelopeAcceptancePolicyObserver.Recorded}; body records the exact digest and acceptance requested; enabled True",
            $"{recorded}; body {(entry.Contains(acceptance.Describe().PolicyDigestHex, StringComparison.Ordinal) ? "has the digest" : "MISSING the digest")}, {(entry.Contains("\"acceptance_requested\":true", StringComparison.Ordinal) ? "requested on" : "not requested")}; enabled {acceptance.Enabled}",
            recorded == EnvelopeAcceptancePolicyObserver.Recorded && entry.Contains(acceptance.Describe().PolicyDigestHex, StringComparison.Ordinal) && entry.Contains("\"acceptance_requested\":true", StringComparison.Ordinal) && acceptance.Enabled);
        await GrantSentinelAsync("after-activation");
    }

    // ------------------------------------------------------------------------------------------------ teardown checks
    private async Task TeardownChecksAsync()
    {
        CurrentGroup = "teardown";
        acceptance.SetTestingOverride(false);
        Record("T-01", "Setup", "teardown: the Testing override disables acceptance on the in-process switch (instances are discarded with the process)", "enabled False", $"enabled {acceptance.Enabled}", !acceptance.Enabled);
        await GrantSentinelAsync("end");
        await Target.ReverifyAllAsync(inputs.AdminCs);
        Record("T-02", "Setup", "end: every admitted database still carries this run's nonce and system identifier; the runtime role is ordinary",
            "verified; false false", $"verified {Target.Databases.Count}; {await RoleFactsAsync()}", await RoleFactsAsync() == "false false");
    }
}
