// E3-M2b-2 R-M2b2-11 focused real-PostgreSQL gate for the Authority S-4 schema/envelope tie (contract §23.15).
//
// Runs the REAL Authority code of this working tree (Release build, S4Binaries) in-process against a disposable PostgreSQL 18
// container: this build's own --migrate and --apply-content-acceptance-floor 1 (separate processes, migration identity), and a
// Release B ContentService + EF AuthorityStore (RB-CARV-1 write fence active) connected as the non-superuser runtime role.
//
// Controlled test-only mechanism (the PostgreSQL counterpart of SeedPreparedWithoutPrepareRulesAsync in the Authority unit tests):
// a candidate is signed with the harness's deterministic TEST-ONLY HMAC provider (the same provider the in-process Authority
// verifies with) and recorded as a Prepared operation stamped at this build's CARV version, through an ordinary admin session, so
// every table constraint, the CARV state guard and the floor CHECK apply to the seeded rows. No production code, configuration,
// verification or validation is changed or bypassed: Commit verifies the staged bytes and signatures exactly as in production.
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuthorizationAuthority.Api.Composition;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Content;
using AuthorizationAuthority.Domain.Features.Keys;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using AuthorizationAuthority.Infrastructure.Persistence;
using EnterpriseForms.Integrity.Canonical;
using EnterpriseForms.Integrity.Merkle;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

var lab = new Lab(
    Environment.GetEnvironmentVariable("S4_NEWBIN") ?? throw new InvalidOperationException("S4_NEWBIN"),
    Environment.GetEnvironmentVariable("S4_ADMIN") ?? throw new InvalidOperationException("S4_ADMIN"),
    Environment.GetEnvironmentVariable("S4_RUNTIME") ?? throw new InvalidOperationException("S4_RUNTIME"),
    Environment.GetEnvironmentVariable("S4_EVIDENCE") ?? throw new InvalidOperationException("S4_EVIDENCE"));
return await lab.RunAsync();

internal sealed class Lab(string newBinary, string adminCs, string runtimeCs, string evidenceDir)
{
    private const string Issuer = "https://s4-harness.invalid/";
    private const string CarvMigrationId = "20261115014221_AuthorityContentAcceptanceRuleVersion";
    private const string FloorQuery = "SELECT coalesce(max(substring(conname FROM 'ck_content_operation_rule_floor_v([0-9]+)$')::int)::text, 'none') FROM pg_catalog.pg_constraint WHERE conrelid = 'authority.content_change_operations'::regclass";
    private const string Mismatch = "ENVELOPE_INVALID: " + EnvelopeSchemaTie.MismatchReason;
    private static readonly ContentAcceptancePolicy ReleaseB = ContentAcceptancePolicy.Resolve(ContentAcceptancePolicy.ReleaseBName, "1");
    private readonly List<Check> checks = [];
    private readonly List<string> log = [];
    private readonly TestMac mac = new();
    private readonly CapturingAlerts alerts = new();

    public async Task<int> RunAsync()
    {
        try
        {
            await ScenariosAsync();
        }
        catch (Exception ex)
        {
            Log("HARNESS ABORTED: " + Describe(ex) + " " + ex);
            checks.Add(new Check("HARNESS", "harness completed", "no unexpected exception", Describe(ex), false));
        }

        var failed = checks.Count(c => !c.Pass);
        var json = JsonSerializer.Serialize(new
        {
            harness = "E3-M2b-2 R-M2b2-11 S-4 focused real-PostgreSQL gate (real Authority build, Release B, runtime role)",
            build = "Release build of the working tree (new-sha256.txt); ContentAcceptanceRules.CurrentVersion = " + ContentAcceptanceRules.CurrentVersion
                    + "; EnvelopeSchemaTie.TiedPairs = " + string.Join(",", EnvelopeSchemaTie.TiedPairs.Select(p => $"({p.SchemaVersion},{p.EnvelopeVersion})"))
                    + "; EnvelopeRules.AllowedEnvelopeVersions = " + string.Join(",", EnvelopeRules.AllowedEnvelopeVersions),
            postgres = await ScalarAsync(adminCs, "SELECT version()"),
            checksTotal = checks.Count,
            checksFailed = failed,
            checks,
        }, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(evidenceDir, "s4-evidence.json"), json);
        await File.WriteAllLinesAsync(Path.Combine(evidenceDir, "s4-log.txt"), log);
        Console.WriteLine($"checks: {checks.Count}, failed: {failed}");
        foreach (var c in checks)
        {
            Console.WriteLine($"{(c.Pass ? "PASS" : "FAIL")} {c.Id}: {c.Description} | observed: {c.Observed}");
        }

        return failed == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------------------------------------ scenarios
    private async Task ScenariosAsync()
    {
        // ---------- setup: this build's ordinary migration, then the explicit floor command (Release B prerequisites)
        var migrated = await HostCommandAsync("--migrate", null, "s4-migration");
        var floor = await HostCommandAsync("--apply-content-acceptance-floor", "1", "s4-floor-command");
        Record("S4-00", "setup: this build's --migrate (CARV recorded) and its explicit floor command (floor 1)", "exit 0 | exit 0; CARV history 1; floor 1",
            $"exit {migrated.Code} | exit {floor.Code}; CARV history {await CountAsync($"SELECT count(*) FROM authority.ef_migrations_history WHERE \"MigrationId\" = '{CarvMigrationId}'")}; floor {await FloorAsync()}",
            migrated.Code == 0 && floor.Code == 0 && await CountAsync($"SELECT count(*) FROM authority.ef_migrations_history WHERE \"MigrationId\" = '{CarvMigrationId}'") == 1 && await FloorAsync() == "1");

        var org = await SeedOrgAsync(10);
        var markedRuntime = ContentAcceptanceRuleConnection.WithVersionedApplicationName(new NpgsqlConnectionStringBuilder(runtimeCs) { ApplicationName = "s4-guard" }.ConnectionString)!;
        var guard = new ContentAcceptanceRuleGuard(new ContentAcceptanceRuleSchemaReader(Factory.For(markedRuntime)), TimeProvider.System, NullLogger<ContentAcceptanceRuleGuard>.Instance, ReleaseB);
        var rb = Instance("RB");

        // ---------- CARV and floor enforcement active (owner item 9), before any S-4 case
        Record("S4-01", "Release B readiness of the real guard: ready with floor 1; the live catalog matches the write fence", "True content acceptance rules v1 (floor 1); phase=ReleaseB requiredFloor=1; verdict none",
            $"{await GuardAsync(guard)}; verdict {await LiveVerdictAsync()}", await GuardAsync(guard) == "True content acceptance rules v1 (floor 1); phase=ReleaseB requiredFloor=1" && await LiveVerdictAsync() == "none");
        Record("S4-02", "the runtime role is an ordinary role (no superuser, no BYPASSRLS, not the table owner)", "f f false",
            await ScalarAsync(adminCs, "SELECT rolsuper||' '||rolbypassrls||' '||(SELECT tableowner = 'authority_runtime' FROM pg_tables WHERE schemaname='authority' AND tablename='content_change_operations') FROM pg_roles WHERE rolname='authority_runtime'"),
            await ScalarAsync(adminCs, "SELECT rolsuper||' '||rolbypassrls||' '||(SELECT tableowner = 'authority_runtime' FROM pg_tables WHERE schemaname='authority' AND tablename='content_change_operations') FROM pg_roles WHERE rolname='authority_runtime'") == "false false false");

        // ---------- valid schema 1 / envelope 1 writes through the real Prepare and Commit (field and document envelopes)
        var initial = new List<string>();
        foreach (var record in org.Records)
        {
            initial.Add(await Outcome(WriteAsync(rb, record, Variant.Valid)));
        }

        Record("S4-03", "every record receives valid schema 1 / envelope 1 content (text, encrypted field, encrypted document) through real Prepare + Commit, Release B", "10 x OK; all 1/1 Committed",
            $"{string.Join(",", initial.Distinct())}; committed 1/1: {await CountAsync($"SELECT count(*) FROM authority.content_change_operations WHERE organization_id='{org.Id}' AND operation_state_name='Committed' AND prepare_acceptance_rule_version=1 AND commit_acceptance_rule_version=1")}",
            initial.All(o => o == "OK") && await CountAsync($"SELECT count(*) FROM authority.content_change_operations WHERE organization_id='{org.Id}' AND operation_state_name='Committed' AND prepare_acceptance_rule_version=1 AND commit_acceptance_rule_version=1") == 10);

        // ---------- S-4 at Prepare on the real persistence path: refused before anything is stored
        foreach (var (id, variant, record) in new[] { ("S4-P1", Variant.FieldSchema2Envelope1, org.Records[8]), ("S4-P2", Variant.DocumentSchema1Envelope2, org.Records[9]) })
        {
            var before = await SnapshotAsync(org, record);
            var opsBefore = await CountAsync("SELECT count(*) FROM authority.content_change_operations");
            var leavesBefore = await CountAsync("SELECT count(*) FROM authority.content_candidate_leaves");
            var (outcome, message) = await OutcomeWithMessage(PrepareAsync(rb, record, variant));
            var after = await SnapshotAsync(org, record);
            Record(id, $"real Prepare of a {variant} candidate: refused by S-4; no operation, candidate leaf or accepted state written", $"AuthorityException INVALID_COMMAND 422 retryable=False; {Mismatch}; ops/leaves unchanged; snapshot unchanged",
                $"{outcome}; {message}; ops {opsBefore}->{await CountAsync("SELECT count(*) FROM authority.content_change_operations")}; leaves {leavesBefore}->{await CountAsync("SELECT count(*) FROM authority.content_candidate_leaves")}; {Diff(before, after)}",
                outcome == "AuthorityException INVALID_COMMAND 422 retryable=False" && message == Mismatch && opsBefore == await CountAsync("SELECT count(*) FROM authority.content_change_operations") && leavesBefore == await CountAsync("SELECT count(*) FROM authority.content_candidate_leaves") && before == after);
        }

        // ---------- S-4 at Commit (owner items 1-7): an otherwise valid Prepared operation holding a mismatched row
        var cases = new[]
        {
            ("S4-C1", Variant.FieldSchema2Envelope1, org.Records[0]),
            ("S4-C2", Variant.FieldSchema1Envelope2, org.Records[1]),
            ("S4-C3", Variant.DocumentSchema2Envelope1, org.Records[2]),
            ("S4-C4", Variant.DocumentSchema1Envelope2, org.Records[3]),
        };
        foreach (var (id, variant, record) in cases)
        {
            var (operation, staged, seeded) = await SeedPreparedAsync(org, record, variant, prepareRuleVersion: ContentAcceptanceRules.CurrentVersion);
            Record(id + "a", $"seeded {variant} operation: accepted by every database constraint, Prepared, stamped P = 1", "OK; 1/NULL Prepared", $"{seeded}; {await StampsAsync(operation)}", seeded == "OK" && await StampsAsync(operation) == "1/NULL Prepared");

            var before = await SnapshotAsync(org, record);
            var global = await GlobalSnapshotAsync(org);
            var verifications = mac.Verifications;
            var (outcome, message) = await OutcomeWithMessage(rb.Service.CommitAsync(Actor(org), operation, new CommitContentChangeRequest(org.Id, record.Id, staged), Guid.CreateVersion7(), CancellationToken.None));
            var after = await SnapshotAsync(org, record);
            Record(id + "b", $"real Release B Commit of the {variant} operation: the staged signatures verify, then S-4 refuses", $"AuthorityException INVALID_COMMAND 422 retryable=False; {Mismatch}; signatures verified > 0",
                $"{outcome}; {message}; signatures verified {mac.Verifications - verifications}", outcome == "AuthorityException INVALID_COMMAND 422 retryable=False" && message == Mismatch && mac.Verifications > verifications);
            Record(id + "c", "the operation is Aborted with the S-4 Commit reason; no commit rule version recorded", $"1/NULL Aborted {EnvelopeSchemaTie.MismatchAtCommitReason}",
                $"{await StampsAsync(operation)} {await ReasonAsync(operation)}", await StampsAsync(operation) == "1/NULL Aborted" && await ReasonAsync(operation) == EnvelopeSchemaTie.MismatchAtCommitReason);
            Record(id + "d", "no authoritative state advances: head, manifest commits, manifest leaves, item identities, integrity checkpoints, journal, content-integrity revision, committed operations", "record and organization snapshots unchanged",
                $"{Diff(before, after)} | {Diff(global, await GlobalSnapshotAsync(org))}", before == after && global == await GlobalSnapshotAsync(org));

            var (replay, replayMessage) = await OutcomeWithMessage(rb.Service.CommitAsync(Actor(org), operation, new CommitContentChangeRequest(org.Id, record.Id, staged), Guid.CreateVersion7(), CancellationToken.None));
            Record(id + "e", "a Commit replay of the rejected operation is refused and accepts nothing", "refused (AuthorityException); still 1/NULL Aborted with the S-4 reason; snapshots unchanged",
                $"{replay} {replayMessage}; {await StampsAsync(operation)} {await ReasonAsync(operation)}; {Diff(before, await SnapshotAsync(org, record))}",
                replay.StartsWith("AuthorityException", StringComparison.Ordinal) && await StampsAsync(operation) == "1/NULL Aborted" && await ReasonAsync(operation) == EnvelopeSchemaTie.MismatchAtCommitReason && before == await SnapshotAsync(org, record) && global == await GlobalSnapshotAsync(org));
        }

        // ---------- positive controls (owner item 8)
        {
            var record = org.Records[4];
            var (operation, staged, seeded) = await SeedPreparedAsync(org, record, Variant.Valid, prepareRuleVersion: ContentAcceptanceRules.CurrentVersion);
            var before = await HeadAsync(record);
            var outcome = await Outcome(rb.Service.CommitAsync(Actor(org), operation, new CommitContentChangeRequest(org.Id, record.Id, staged), Guid.CreateVersion7(), CancellationToken.None));
            var after = await HeadAsync(record);
            Record("S4-V1", "control: the same seeding mechanism with a valid schema 1 / envelope 1 candidate commits (the refusals above are S-4, not the fixture)", "seed OK; commit OK; 1/1 Committed; head +1",
                $"{seeded}; {outcome}; {await StampsAsync(operation)}; head {before.Revision}->{after.Revision}", seeded == "OK" && outcome == "OK" && await StampsAsync(operation) == "1/1 Committed" && after.Revision == before.Revision + 1);
        }

        foreach (var (_, _, record) in cases)
        {
            var before = await HeadAsync(record);
            var outcome = await Outcome(WriteAsync(rb, record, Variant.Valid));
            Record("S4-V2-" + org.Records.IndexOf(record), "after the S-4 refusal, a valid schema 1 / envelope 1 operation on the same record commits normally (real Prepare + Commit)", "OK; head +1",
                $"{outcome}; head {before.Revision}->{(await HeadAsync(record)).Revision}", outcome == "OK" && (await HeadAsync(record)).Revision == before.Revision + 1);
        }

        // ---------- schema 2 / envelope 2 stays unaccepted at Commit (passes S-4, refused by the E3-M2a envelope rules)
        foreach (var (id, variant, record) in new[] { ("S4-T1", Variant.FieldSchema2Envelope2, org.Records[5]), ("S4-T2", Variant.DocumentSchema2Envelope2, org.Records[6]) })
        {
            var (operation, staged, seeded) = await SeedPreparedAsync(org, record, variant, prepareRuleVersion: ContentAcceptanceRules.CurrentVersion);
            var before = await SnapshotAsync(org, record);
            var (outcome, message) = await OutcomeWithMessage(rb.Service.CommitAsync(Actor(org), operation, new CommitContentChangeRequest(org.Id, record.Id, staged), Guid.CreateVersion7(), CancellationToken.None));
            Record(id, $"{variant}: not accepted at Commit (tied, so S-4 passes; EnvelopeRules refuses)", "ENVELOPE_INVALID: ENVELOPE_SCHEMA_UNSUPPORTED; 1/NULL Aborted ENVELOPE_INVALID_AT_COMMIT; snapshot unchanged",
                $"{seeded}; {message}; {await StampsAsync(operation)} {await ReasonAsync(operation)}; {Diff(before, await SnapshotAsync(org, record))}",
                seeded == "OK" && message == "ENVELOPE_INVALID: " + EnvelopeRules.Reasons.SchemaUnsupported && await StampsAsync(operation) == "1/NULL Aborted" && await ReasonAsync(operation) == "ENVELOPE_INVALID_AT_COMMIT" && before == await SnapshotAsync(org, record));
        }

        // ---------- CARV and floor enforcement still active after the S-4 cases (owner item 9)
        {
            var record = org.Records[7];
            var (_, _, unstamped) = await SeedPreparedAsync(org, record, Variant.Valid, prepareRuleVersion: null);
            Record("S4-F1", "database floor: an unstamped (pre-CARV) Prepared operation cannot be inserted", "23514 ck_content_operation_rule_floor_v1", unstamped, unstamped.Contains("23514", StringComparison.Ordinal) && unstamped.Contains("ck_content_operation_rule_floor_v1", StringComparison.Ordinal));

            var (other, staged, seeded) = await SeedPreparedAsync(org, record, Variant.Valid, prepareRuleVersion: ContentAcceptanceRules.CurrentVersion + 1);
            var before = await SnapshotAsync(org, record);
            var outcome = await Outcome(rb.Service.CommitAsync(Actor(org), other, new CommitContentChangeRequest(org.Id, record.Id, staged), Guid.CreateVersion7(), CancellationToken.None));
            Record("S4-F2", "CARV Commit rule: an operation prepared under other rules (P = 2) is refused and aborted; nothing advances", "seed OK; OPERATION_RULES_OUTDATED 409; 2/NULL Aborted OPERATION_RULES_OUTDATED; snapshot unchanged",
                $"{seeded}; {outcome}; {await StampsAsync(other)} {await ReasonAsync(other)}; {Diff(before, await SnapshotAsync(org, record))}",
                seeded == "OK" && outcome == "AuthorityException OPERATION_RULES_OUTDATED 409 retryable=False" && await StampsAsync(other) == "2/NULL Aborted" && await ReasonAsync(other) == ContentAcceptanceRules.OutdatedAbortReason && before == await SnapshotAsync(org, record));

            var (third, _, thirdSeeded) = await SeedPreparedAsync(org, record, Variant.Valid, prepareRuleVersion: ContentAcceptanceRules.CurrentVersion);
            var forced = await ExecAsync(adminCs, $"UPDATE authority.content_change_operations SET operation_state_name='Committed', commit_acceptance_rule_version=2 WHERE content_change_operation_id='{third}'");
            Record("S4-F3", "database CARV fence: a Committed state with C <> P is refused even for the migration identity", "refused (23514 commit-rule CHECK or state guard); still 1/NULL Prepared",
                $"{thirdSeeded}; {forced}; {await StampsAsync(third)}", thirdSeeded == "OK" && forced != "OK" && await StampsAsync(third) == "1/NULL Prepared");

            Record("S4-F4", "after every case: readiness still ready under Release B, floor 1, live verdict none; no write-fence alert was raised", "True ...(floor 1); phase=ReleaseB requiredFloor=1; floor 1; none; alerts 0",
                $"{await GuardAsync(guard)}; floor {await FloorAsync()}; verdict {await LiveVerdictAsync()}; alerts {alerts.Raised.Count}",
                await GuardAsync(guard) == "True content acceptance rules v1 (floor 1); phase=ReleaseB requiredFloor=1" && await FloorAsync() == "1" && await LiveVerdictAsync() == "none" && alerts.Raised.Count == 0);
        }

        Record("S4-99", "operation inventory of the run (for the record)", "only Committed and Aborted, plus the one Prepared operation of S4-F3", await OpsSummaryAsync(),
            await CountAsync("SELECT count(*) FROM authority.content_change_operations WHERE operation_state_name='Prepared'") == 1);
    }

    // ------------------------------------------------------------------------------------------------ model
    private sealed record Check(string Id, string Description, string Expected, string Observed, bool Pass);

    internal enum Variant
    {
        Valid,
        FieldSchema2Envelope1,
        FieldSchema1Envelope2,
        FieldSchema2Envelope2,
        DocumentSchema2Envelope1,
        DocumentSchema1Envelope2,
        DocumentSchema2Envelope2,
    }

    private sealed class Org(Guid id, Guid owner, Guid ownerBinding)
    {
        public Guid Id { get; } = id;
        public Guid Owner { get; } = owner;
        public Guid OwnerBinding { get; } = ownerBinding;
        public List<RecordModel> Records { get; } = [];
    }

    private sealed class RecordModel(Org org, Guid id)
    {
        public Org Org { get; } = org;
        public Guid Id { get; } = id;
        public Guid TextField { get; } = Guid.CreateVersion7();
        public Guid TextItem { get; } = Guid.CreateVersion7();
        public Guid SealedField { get; } = Guid.CreateVersion7();
        public Guid SealedItem { get; } = Guid.CreateVersion7();
        public Guid DocumentField { get; } = Guid.CreateVersion7();
        public Guid Document { get; } = Guid.CreateVersion7();
        public List<(Staged Row, byte[] Signature)> Accepted { get; set; } = [];
        private int writes;

        public byte NextSeed() => (byte)(Interlocked.Increment(ref writes) % 200 + 10);

        public List<StagedContentItem> AcceptedStaged() => Accepted.Select(a => new StagedContentItem(a.Row.Kind, a.Row.ItemId, a.Row.Base64, Convert.ToBase64String(a.Signature))).ToList();
    }

    private sealed record AuthorityNode(string Name, ContentService Service);

    private AuthorityNode Instance(string name)
    {
        // The real build's versioned application name and Release B policy (RB-CARV-1 write fence in every content transaction).
        var cs = ContentAcceptanceRuleConnection.WithVersionedApplicationName(new NpgsqlConnectionStringBuilder(runtimeCs) { ApplicationName = "s4-runtime-" + name }.ConnectionString)!;
        var store = new AuthorityStore(Factory.For(cs), ReleaseB, null, null, alerts);
        var verifier = new ProjectionIntegrityVerifier(store, ThrowingProxy.Create<ISpiceDbProjection>(), new ProjectionMarkerTransitionState(), new NoAlerts(), new ProjectionIntegrityMetrics(), TimeProvider.System);
        return new AuthorityNode(name, new ContentService(store, mac, verifier, new NoFaults(), TimeProvider.System, new ShareContributorWriteSwitch(false, false)));
    }

    private async Task<Org> SeedOrgAsync(int records)
    {
        var owner = Guid.CreateVersion7();
        var binding = Guid.CreateVersion7();
        var org = new Org(Guid.CreateVersion7(), owner, binding);
        var keyName = "s4-key-" + org.Id.ToString("N");
        // Security state is seeded as the established gates do (replication role: no command journal for the fixture itself).
        var seedCs = new NpgsqlConnectionStringBuilder(adminCs) { Options = "-c session_replication_role=replica", ApplicationName = "s4-seed" }.ConnectionString;
        await using var context = new AuthorityDbContext(new DbContextOptionsBuilder<AuthorityDbContext>().UseNpgsql(seedCs).Options);
        var now = DateTimeOffset.UtcNow;
        context.OrganizationStates.Add(new OrganizationAuthorizationState { OrganizationAuthorizationStateId = Guid.CreateVersion7(), OrganizationId = org.Id, OrganizationDisplayName = "S4 gate", AuthorizationRevision = 7, ProjectionAppliedRevision = 7, ProjectionZedToken = "zed-7", CheckpointEnforcementModeName = CheckpointEnforcementModes.Asynchronous, StateModifiedOn = now });
        context.IdentityBindings.Add(new IdentityBinding { IdentityBindingId = binding, OrganizationId = org.Id, AuthorityUserId = owner, IdentityIssuerUri = Issuer, IdentitySubjectIdentifier = owner.ToString("D"), IdentityBindingEnabled = true, IdentityCreatedOn = now });
        context.KeyRegistrations.Add(new IntegrityKeyRegistration { IntegrityKeyRegistrationId = Guid.CreateVersion7(), OrganizationId = org.Id, ProviderName = "LocalDevelopment", ProviderKeyName = keyName, CurrentSigningKeyVersion = 1, MinimumVerificationKeyVersion = 1, KeyRegisteredOn = now, KeyModifiedOn = now });
        for (var i = 0; i < records; i++)
        {
            var record = new RecordModel(org, Guid.CreateVersion7());
            org.Records.Add(record);
            context.Resources.Add(new AuthorityResource { AuthorityResourceId = Guid.CreateVersion7(), OrganizationId = org.Id, ResourceTypeName = ResourceTypeNames.FormRecord, ResourceEntityId = record.Id, ParentResourceEntityId = Fixtures.Form, RegisteredBySecurityCommandId = Guid.CreateVersion7(), ResourceRegisteredOn = now });
            context.ContentHeads.Add(new RecordContentHead { RecordContentHeadId = Guid.CreateVersion7(), OrganizationId = org.Id, FormRecordId = record.Id, CommittedManifestRoot = MerkleManifestV1.EmptyRoot(), SignatureKeyVersion = 1, ContentHeadModifiedOn = now });
            context.RelationshipEdges.Add(new AuthorityRelationshipEdge { AuthorityRelationshipEdgeId = Guid.CreateVersion7(), OrganizationId = org.Id, ResourceTypeName = ResourceTypeNames.FormRecord, ResourceEntityId = record.Id, RelationshipName = "owner", SubjectTypeName = ResourceTypeNames.User, SubjectEntityId = owner, SubjectRelationName = string.Empty, CreatedBySecurityCommandId = Guid.CreateVersion7(), RelationshipCreatedOn = now });
        }

        await context.SaveChangesAsync();
        return org;
    }

    // ------------------------------------------------------------------------------------------------ protocol drivers
    private static VerifiedActor Actor(Org org) => new(Issuer, org.Owner.ToString("D"), "s4-harness");

    private async Task<(PrepareContentChangeRequest Request, List<Staged> Rows)> BuildRequestAsync(RecordModel record, Guid operation, Variant variant)
    {
        var head = await HeadAsync(record);
        var seed = record.NextSeed();
        var rows = Fixtures.Candidate(record.Org.Id, record.Id, record.TextField, record.TextItem, record.SealedField, record.SealedItem, record.DocumentField, record.Document, seed, variant)
            .Select(r => Fixtures.Encode(r, record.Org.Id, operation, head.Revision + 1)).ToList();
        var request = new PrepareContentChangeRequest(record.Org.Id, record.Id, operation, 7, head.Revision, Convert.ToBase64String(head.Root),
            rows.Select(c => new CandidateContentItem(c.Kind, c.ItemId, c.Base64)).ToList(), CurrentItems: record.Accepted.Count == 0 ? null : record.AcceptedStaged());
        return (request, rows);
    }

    private async Task<(PreparedContentOperation Prepared, List<Staged> Rows)> PrepareAsync(AuthorityNode instance, RecordModel record, Variant variant)
    {
        var operation = Guid.CreateVersion7();
        var (request, rows) = await BuildRequestAsync(record, operation, variant);
        var prepared = await instance.Service.PrepareAsync(Actor(record.Org), request, "s4-" + operation.ToString("N"), Guid.CreateVersion7(), CancellationToken.None);
        return (prepared, rows);
    }

    private async Task WriteAsync(AuthorityNode instance, RecordModel record, Variant variant)
    {
        var (prepared, rows) = await PrepareAsync(instance, record, variant);
        var signatures = prepared.SignedItems.ToDictionary(s => (s.ItemKind, s.ItemId), s => s.RowSignatureBase64);
        var staged = rows.Select(c => new StagedContentItem(c.Kind, c.ItemId, c.Base64, signatures[(c.Kind, c.ItemId)])).ToList();
        await instance.Service.CommitAsync(Actor(record.Org), prepared.OperationId, new CommitContentChangeRequest(record.Org.Id, record.Id, staged), Guid.CreateVersion7(), CancellationToken.None);
        record.Accepted = rows.Select(c => (c, Convert.FromBase64String(signatures[(c.Kind, c.ItemId)]))).ToList();
    }

    /// <summary>
    /// The controlled test-only mechanism: signs <paramref name="variant"/>'s candidate with the test HMAC provider and records it as
    /// a Prepared operation (stamp <paramref name="prepareRuleVersion"/>) plus its candidate leaves in one ordinary admin
    /// transaction, without running Prepare. Every constraint and trigger applies. Returns the operation, the staged items a
    /// business host would send to Commit, and the insert outcome.
    /// </summary>
    private async Task<(Guid Operation, List<StagedContentItem> Staged, string Seeded)> SeedPreparedAsync(Org org, RecordModel record, Variant variant, int? prepareRuleVersion)
    {
        var operation = Guid.CreateVersion7();
        var head = await HeadAsync(record);
        var revision = head.Revision + 1;
        var rows = Fixtures.Candidate(org.Id, record.Id, record.TextField, record.TextItem, record.SealedField, record.SealedItem, record.DocumentField, record.Document, record.NextSeed(), variant)
            .Select(r => Fixtures.Encode(r, org.Id, operation, revision)).ToList();
        var key = new IntegrityKeyReference(org.Id, IntegrityKeyPurposes.RowIntegrityV1, "s4-key-" + org.Id.ToString("N"), 1);
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
            await using var context = new AuthorityDbContext(new DbContextOptionsBuilder<AuthorityDbContext>().UseNpgsql(new NpgsqlConnectionStringBuilder(adminCs) { ApplicationName = "s4-seed-operation" }.ConnectionString).Options);
            context.ContentOperations.Add(new ContentChangeOperation
            {
                ContentChangeOperationId = operation,
                OrganizationId = org.Id,
                FormRecordId = record.Id,
                ActorIdentityBindingId = org.OwnerBinding,
                OperationIdempotencyKey = "s4-seeded-" + operation.ToString("N"),
                OperationRequestDigest = SHA256.HashData(operation.ToByteArray()),
                ExpectedAuthorizationRevision = 7,
                ExpectedContentRevision = head.Revision,
                CandidateContentRevision = revision,
                CandidateKeyVersion = 1,
                CandidateManifestRoot = manifest.MerkleRoot,
                CandidateLeafIndexDigest = manifest.LeafIndexDigest,
                CandidateLeafCount = (long)manifest.LeafCount,
                OperationStateName = ContentOperationStates.Prepared,
                CorrelationIdentifier = Guid.CreateVersion7(),
                OperationCreatedOn = now,
                OperationExpiresOn = now.AddMinutes(10),
                PrepareAcceptanceRuleVersion = prepareRuleVersion,
            });
            context.CandidateLeaves.AddRange(manifest.SortedLeaves.Select((leaf, i) => new ContentCandidateLeaf
            {
                ContentCandidateLeafId = Guid.CreateVersion7(),
                ContentChangeOperationId = operation,
                ManifestItemKey = leaf.ItemKey,
                ManifestItemKindCode = (byte)leaf.Kind,
                ManifestItemId = leaf.ItemId,
                MutationKindName = ContentMutationKinds.Update,
                CanonicalPayloadDigest = leaf.CanonicalPayloadDigest.ToArray(),
                RowSignature = leaf.RowSignature.ToArray(),
                LeafDigest = manifest.LeafHashes[i],
            }));
            await context.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            seeded = Describe(ex);
        }

        Log($"seed {variant} P={prepareRuleVersion?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL"} op={operation}: {seeded}");
        return (operation, signed.Select(s => new StagedContentItem(s.Item.Kind, s.Item.ItemId, s.Item.Base64, Convert.ToBase64String(s.Signature))).ToList(), seeded);
    }

    // ------------------------------------------------------------------------------------------------ evidence helpers
    private static async Task<string> GuardAsync(ContentAcceptanceRuleGuard guard)
    {
        var (ready, detail) = await guard.RefreshAsync(CancellationToken.None);
        return $"{ready} {detail}";
    }

    private async Task<string> LiveVerdictAsync()
    {
        await using var connection = new NpgsqlConnection(adminCs);
        await connection.OpenAsync();
        var facts = await ContentAcceptanceWriteFence.ReadAsync(connection, null, ContentAcceptancePolicy.ReleaseBFloor, CancellationToken.None);
        return ContentAcceptanceWriteFence.Evaluate(facts, ContentAcceptancePolicy.ReleaseBFloor) ?? "none";
    }

    private async Task<(int Code, string Output)> HostCommandAsync(string command, string? argument, string application)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(newBinary);
        start.ArgumentList.Add(command);
        if (argument is not null)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["ConnectionStrings__AuthorityDbMigration"] = new NpgsqlConnectionStringBuilder(adminCs) { ApplicationName = application }.ConnectionString;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("could not start " + command);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = ((await stdout) + " " + (await stderr)).Replace('\n', ' ').Replace('\r', ' ').Trim();
        Log($"{command} ({application}) exit {process.ExitCode}: {(output.Length > 400 ? output[^400..] : output)}");
        return (process.ExitCode, output);
    }

    private async Task<string> FloorAsync() => await ScalarAsync(adminCs, FloorQuery);

    private async Task<(long Revision, byte[] Root)> HeadAsync(RecordModel record)
    {
        await using var connection = new NpgsqlConnection(adminCs);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT committed_content_revision, committed_manifest_root FROM authority.record_content_heads WHERE organization_id = @o AND form_record_id = @r", connection);
        command.Parameters.AddWithValue("o", record.Org.Id);
        command.Parameters.AddWithValue("r", record.Id);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt64(0), (byte[])reader[1]);
    }

    /// <summary>The record's accepted state: head (revision, root, operation, quarantine) and its manifest commits and leaves, item identities and committed operations.</summary>
    private async Task<string> SnapshotAsync(Org org, RecordModel record) => await ScalarAsync(adminCs, $"""
        SELECT concat_ws('; ',
          'head='||(SELECT committed_content_revision||':'||encode(committed_manifest_root,'hex')||':'||coalesce(committed_operation_id::text,'none')||':'||content_head_quarantined FROM authority.record_content_heads WHERE form_record_id='{record.Id}'),
          'commits='||(SELECT count(*) FROM authority.record_manifest_commits WHERE form_record_id='{record.Id}'),
          'leaves='||(SELECT count(*) FROM authority.record_manifest_leaves l JOIN authority.record_manifest_commits c ON c.record_manifest_commit_id=l.record_manifest_commit_id WHERE c.form_record_id='{record.Id}'),
          'identities='||(SELECT count(*) FROM authority.content_item_identities WHERE organization_id='{org.Id}' AND form_record_id='{record.Id}'),
          'committedOps='||(SELECT count(*) FROM authority.content_change_operations WHERE form_record_id='{record.Id}' AND operation_state_name='Committed'))
        """);

    /// <summary>Organization-wide authoritative state: content-integrity revision, checkpoints, journal, all manifest commits and identities.</summary>
    private async Task<string> GlobalSnapshotAsync(Org org) => await ScalarAsync(adminCs, $"""
        SELECT concat_ws('; ',
          'contentIntegrityRevision='||(SELECT content_integrity_revision FROM authority.organization_authorization_states WHERE organization_id='{org.Id}'),
          'authorizationRevision='||(SELECT authorization_revision FROM authority.organization_authorization_states WHERE organization_id='{org.Id}'),
          'checkpoints='||(SELECT count(*) FROM authority.content_integrity_checkpoints WHERE organization_id='{org.Id}'),
          'journal='||(SELECT count(*) FROM authority.security_journal_entries WHERE organization_id='{org.Id}'),
          'commits='||(SELECT count(*) FROM authority.record_manifest_commits WHERE organization_id='{org.Id}'),
          'identities='||(SELECT count(*) FROM authority.content_item_identities WHERE organization_id='{org.Id}'))
        """);

    private static string Diff(string before, string after) => before == after ? "unchanged (" + before + ")" : "CHANGED " + before + " -> " + after;

    private async Task<string> StampsAsync(Guid operationId) => await ScalarAsync(adminCs, $"SELECT coalesce(prepare_acceptance_rule_version::text,'NULL')||'/'||coalesce(commit_acceptance_rule_version::text,'NULL')||' '||operation_state_name FROM authority.content_change_operations WHERE content_change_operation_id='{operationId}'");

    private async Task<string> ReasonAsync(Guid operationId) => await ScalarAsync(adminCs, $"SELECT coalesce(operation_quarantine_reason_code, 'NULL') FROM authority.content_change_operations WHERE content_change_operation_id='{operationId}'");

    private async Task<string> OpsSummaryAsync() => await ScalarAsync(adminCs, "SELECT coalesce(string_agg(s, ', ' ORDER BY s), 'none') FROM (SELECT operation_state_name||coalesce(' '||operation_quarantine_reason_code,'')||'='||count(*) AS s FROM authority.content_change_operations GROUP BY operation_state_name, operation_quarantine_reason_code) q");

    private async Task<long> CountAsync(string sql) => long.Parse(await ScalarAsync(adminCs, sql), System.Globalization.CultureInfo.InvariantCulture);

    private static async Task<string> ScalarAsync(string cs, string sql)
    {
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) ?? "NULL";
    }

    private static async Task<string> ExecAsync(string cs, string sql)
    {
        try
        {
            await using var connection = new NpgsqlConnection(cs);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
            return "OK";
        }
        catch (PostgresException ex)
        {
            return $"{ex.SqlState} {ex.ConstraintName ?? ex.Routine}: {ex.MessageText}";
        }
    }

    private static async Task<string> Outcome(Task task)
    {
        try
        {
            await task;
            return "OK";
        }
        catch (Exception ex)
        {
            return Describe(ex);
        }
    }

    private static async Task<(string Outcome, string Message)> OutcomeWithMessage(Task task)
    {
        try
        {
            await task;
            return ("OK", string.Empty);
        }
        catch (Exception ex)
        {
            return (Describe(ex), ex.Message);
        }
    }

    private static string Describe(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e is not null; e = e.InnerException)
        {
            parts.Add(e switch
            {
                PostgresException pg => $"PostgresException {pg.SqlState} {pg.ConstraintName}",
                AuthorityException a => $"AuthorityException {a.ErrorCode} {a.HttpStatusCode} retryable={a.Retryable}",
                _ => e.GetType().Name,
            });
        }

        return string.Join(" <- ", parts);
    }

    private void Record(string id, string description, string expected, string observed, bool pass)
    {
        checks.Add(new Check(id, description, expected, observed, pass));
        Log($"{(pass ? "PASS" : "FAIL")} {id}: {observed}");
    }

    private void Log(string line)
    {
        lock (log)
        {
            log.Add(DateTimeOffset.UtcNow.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture) + " " + line);
        }
    }
}

// ---------------------------------------------------------------------------------------------------- ports and fixtures
internal sealed class Factory(DbContextOptions<AuthorityDbContext> options) : IDbContextFactory<AuthorityDbContext>
{
    public static Factory For(string connectionString) => new(new DbContextOptionsBuilder<AuthorityDbContext>().UseNpgsql(connectionString, n => n.MigrationsHistoryTable("ef_migrations_history", AuthorityDbContext.SchemaName)).Options);

    public AuthorityDbContext CreateDbContext() => new(options);
}

internal sealed class NoAlerts : ISecurityAlertSink
{
    public void Raise(SecurityAlert alert)
    {
    }
}

internal sealed class CapturingAlerts : ISecurityAlertSink
{
    public List<SecurityAlert> Raised { get; } = [];

    public void Raise(SecurityAlert alert)
    {
        lock (Raised)
        {
            Raised.Add(alert);
        }
    }
}

internal sealed class NoFaults : IFaultInjector
{
    public void CrashIfArmed(string faultName)
    {
    }
}

/// <summary>Deterministic TEST-ONLY HMAC-SHA-256 (the harness and the in-process Authority share it; never a production provider).</summary>
internal sealed class TestMac : IIntegrityMacProvider
{
    private int verifications;

    public int Verifications => Volatile.Read(ref verifications);

    public string ProviderName => "LocalDevelopment";

    private static byte[] Key(IntegrityKeyReference reference) => SHA256.HashData(Encoding.UTF8.GetBytes($"s4-gate-test-key:{reference.ProviderKeyName}:{reference.KeyVersion}"));

    public Task<IntegrityMacResult> SignAsync(IntegrityKeyReference keyReference, ReadOnlyMemory<byte> canonicalPayload, CancellationToken cancellationToken) =>
        Task.FromResult(new IntegrityMacResult(HMACSHA256.HashData(Key(keyReference), canonicalPayload.Span), keyReference.KeyVersion, "HMAC-SHA-256"));

    public Task<bool> VerifyAsync(IntegrityKeyReference keyReference, ReadOnlyMemory<byte> canonicalPayload, ReadOnlyMemory<byte> expectedMac, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref verifications);
        return Task.FromResult(expectedMac.Length == 32 && CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(Key(keyReference), canonicalPayload.Span), expectedMac.Span));
    }

    public Task<int> ProvisionKeyAsync(Guid organizationId, string keyPurpose, string providerKeyName, CancellationToken cancellationToken) => Task.FromResult(1);

    public Task<(bool Ready, string Detail)> ProbeAsync(CancellationToken cancellationToken) => Task.FromResult((true, "test"));

    public Task<IntegrityKeyInspection> InspectKeyAsync(string providerKeyName, CancellationToken cancellationToken) => throw new NotSupportedException();
}

public class ThrowingProxy : System.Reflection.DispatchProxy
{
    public static T Create<T>()
        where T : class => Create<T, ThrowingProxy>();

    protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args) => throw new NotSupportedException(targetMethod?.Name + " is not used by the content protocol");
}

internal sealed record Staged(Fixtures.Row Row, Guid StorageKey, byte[] Bytes, ManifestItemKind Kind, Guid ItemId)
{
    public string Base64 => Convert.ToBase64String(Bytes);
}

/// <summary>Canonical rows shaped like the business registry (schema 1 and the E3-M2b-2 schema 2 of both envelope kinds).</summary>
internal static class Fixtures
{
    public static readonly Guid Form = Guid.CreateVersion7();
    private static readonly Guid Creator = Guid.CreateVersion7();
    private static readonly Guid Version = Guid.CreateVersion7();
    private static readonly DateTimeOffset Fixed = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly RowSchemaDefinition HeaderV3 = new("form_records", 3,
    [
        new("content_change_operation_id", RowColumnType.Uuid, false), new("created_by_user_id", RowColumnType.Uuid, false), new("created_form_version_id", RowColumnType.Uuid, false),
        new("form_definition_id", RowColumnType.Uuid, false), new("form_relationship_definition_id", RowColumnType.Uuid, true), new("parent_form_record_id", RowColumnType.Uuid, true),
        new("record_status", RowColumnType.Text, false), new("relationship_sort_order", RowColumnType.Int64, true), new("submitted_on", RowColumnType.TimestampUtc, true), new("tenant_id", RowColumnType.Uuid, false),
    ]);

    private static readonly RowSchemaDefinition StringValues = new("field_string_values", 1, new[]
    {
        new RowSignedColumn("content_change_operation_id", RowColumnType.Uuid, false), new RowSignedColumn("field_definition_id", RowColumnType.Uuid, false),
        new RowSignedColumn("field_value_item_id", RowColumnType.Uuid, false), new RowSignedColumn("form_record_group_id", RowColumnType.Uuid, true),
        new RowSignedColumn("form_record_id", RowColumnType.Uuid, false), new RowSignedColumn("tenant_id", RowColumnType.Uuid, false), new RowSignedColumn("string_value", RowColumnType.Text, false),
    }.OrderBy(c => c.ColumnName, StringComparer.Ordinal).ToList());

    private static readonly RowSchemaDefinition EncryptedValues = new("field_encrypted_values", 1,
    [
        new("content_change_operation_id", RowColumnType.Uuid, false), new("encrypted_logical_type", RowColumnType.Text, false), new("encrypted_value_bytes", RowColumnType.Bytes, false),
        new("encryption_algorithm_name", RowColumnType.Text, false), new("encryption_auth_tag_bytes", RowColumnType.Bytes, false), new("encryption_envelope_version", RowColumnType.Int32, false),
        new("encryption_key_reference", RowColumnType.Text, false), new("encryption_nonce_bytes", RowColumnType.Bytes, false), new("field_definition_id", RowColumnType.Uuid, false),
        new("field_value_item_id", RowColumnType.Uuid, false), new("form_record_group_id", RowColumnType.Uuid, true), new("form_record_id", RowColumnType.Uuid, false),
        new("tenant_id", RowColumnType.Uuid, false), new("wrapped_data_key_bytes", RowColumnType.Bytes, false),
    ]);

    private static readonly RowSchemaDefinition Attachments = new("file_attachment_versions", 1,
    [
        new("ciphertext_sha256", RowColumnType.Bytes, false), new("ciphertext_size_bytes", RowColumnType.Int64, false), new("content_change_operation_id", RowColumnType.Uuid, false),
        new("detected_content_type", RowColumnType.Text, false), new("encryption_algorithm_name", RowColumnType.Text, false), new("encryption_envelope_version", RowColumnType.Int32, false),
        new("encryption_key_reference", RowColumnType.Text, false), new("encryption_nonce_prefix", RowColumnType.Bytes, false), new("encryption_segment_size_bytes", RowColumnType.Int32, false),
        new("field_definition_id", RowColumnType.Uuid, false), new("file_attachment_id", RowColumnType.Uuid, false), new("form_record_id", RowColumnType.Uuid, false),
        new("original_file_name", RowColumnType.Text, false), new("plaintext_sha256", RowColumnType.Bytes, false), new("plaintext_size_bytes", RowColumnType.Int64, false),
        new("scan_signature_name", RowColumnType.Text, true), new("scan_verdict", RowColumnType.Text, false), new("scanned_on", RowColumnType.TimestampUtc, false),
        new("scanner_name", RowColumnType.Text, false), new("storage_object_key", RowColumnType.Text, false), new("storage_provider_name", RowColumnType.Text, false),
        new("tenant_id", RowColumnType.Uuid, false), new("uploaded_by_user_id", RowColumnType.Uuid, false), new("uploaded_on", RowColumnType.TimestampUtc, false),
        new("version_number", RowColumnType.Int32, false), new("wrapped_data_key", RowColumnType.Bytes, false),
    ]);

    private static readonly RowSchemaDefinition EncryptedValuesV2 = WithV2Columns(EncryptedValues);
    private static readonly RowSchemaDefinition AttachmentsV2 = WithV2Columns(Attachments);

    private static RowSchemaDefinition WithV2Columns(RowSchemaDefinition schema1) => new(schema1.EntityKind, 2,
        schema1.Columns.Concat([
            new RowSignedColumn("data_key_commitment_bytes", RowColumnType.Bytes, false),
            new RowSignedColumn("data_key_identifier", RowColumnType.Uuid, false),
            new RowSignedColumn("key_wrap_algorithm_code", RowColumnType.Int32, false),
        ]).OrderBy(c => c.ColumnName, StringComparer.Ordinal).ToList());

    public sealed record Row(ManifestItemKind Kind, Guid ItemId, RowSchemaDefinition Schema, Dictionary<string, RowValue?> Values, bool IdentityPrimaryKey);

    public static Staged Encode(Row row, Guid organizationId, Guid operation, long revision)
    {
        var values = new Dictionary<string, RowValue?>(row.Values, StringComparer.Ordinal) { ["content_change_operation_id"] = RowValue.Uuid(operation) };
        var primaryKey = row.IdentityPrimaryKey ? row.ItemId : Guid.CreateVersion7();
        var bytes = RowCanonicalizerV1.Encode(row.Schema, new RowSigningContext(1, organizationId, primaryKey, (ulong)revision), values);
        return new Staged(row, primaryKey, bytes, row.Kind, row.ItemId);
    }

    /// <summary>Header, a text value, an encrypted field value (fresh envelope per seed) and an encrypted document; the variant re-encodes one envelope row.</summary>
    public static List<Row> Candidate(Guid org, Guid record, Guid textField, Guid textItem, Guid sealedField, Guid sealedItem, Guid documentField, Guid document, byte seed, Lab.Variant variant)
    {
        var sealedRow = Sealed(org, record, sealedField, sealedItem, seed);
        var documentRow = Attachment(org, record, documentField, document);
        switch (variant)
        {
            case Lab.Variant.FieldSchema2Envelope1: sealedRow = AsSchema2(sealedRow, EncryptedValuesV2, 1); break;
            case Lab.Variant.FieldSchema2Envelope2: sealedRow = AsSchema2(sealedRow, EncryptedValuesV2, 2); break;
            case Lab.Variant.FieldSchema1Envelope2: sealedRow = With(sealedRow, "encryption_envelope_version", RowValue.Int32(2)); break;
            case Lab.Variant.DocumentSchema2Envelope1: documentRow = AsSchema2(documentRow, AttachmentsV2, 1); break;
            case Lab.Variant.DocumentSchema2Envelope2: documentRow = AsSchema2(documentRow, AttachmentsV2, 2); break;
            case Lab.Variant.DocumentSchema1Envelope2: documentRow = With(documentRow, "encryption_envelope_version", RowValue.Int32(2)); break;
        }

        return [Header(org, record), Text(org, record, textField, textItem, "value-" + seed), sealedRow, documentRow];
    }

    private static Row With(Row row, string column, RowValue? value) => row with { Values = new Dictionary<string, RowValue?>(row.Values, StringComparer.Ordinal) { [column] = value } };

    /// <summary>A well-formed schema-2 fixture of an envelope row (well-formed v2 metadata; the envelope version as given).</summary>
    private static Row AsSchema2(Row row, RowSchemaDefinition schema2, int envelopeVersion)
    {
        var values = new Dictionary<string, RowValue?>(row.Values, StringComparer.Ordinal)
        {
            ["encryption_envelope_version"] = RowValue.Int32(envelopeVersion),
            ["data_key_commitment_bytes"] = RowValue.Bytes(Fill(32, 0xdd)),
            ["data_key_identifier"] = RowValue.Uuid(Guid.CreateVersion7()),
            ["key_wrap_algorithm_code"] = RowValue.Int32(1),
        };
        return row with { Schema = schema2, Values = values };
    }

    private static Row Header(Guid org, Guid record) => new(ManifestItemKind.RecordHeader, record, HeaderV3, new Dictionary<string, RowValue?>(StringComparer.Ordinal)
    {
        ["created_by_user_id"] = RowValue.Uuid(Creator), ["created_form_version_id"] = RowValue.Uuid(Version), ["form_definition_id"] = RowValue.Uuid(Form),
        ["form_relationship_definition_id"] = null, ["parent_form_record_id"] = null, ["record_status"] = RowValue.Text("Draft"),
        ["relationship_sort_order"] = null, ["submitted_on"] = null, ["tenant_id"] = RowValue.Uuid(org),
    }, IdentityPrimaryKey: true);

    private static Row Text(Guid org, Guid record, Guid field, Guid item, string value) => new(ManifestItemKind.FieldValue, item, StringValues, new Dictionary<string, RowValue?>(StringComparer.Ordinal)
    {
        ["field_definition_id"] = RowValue.Uuid(field), ["field_value_item_id"] = RowValue.Uuid(item), ["form_record_group_id"] = null,
        ["form_record_id"] = RowValue.Uuid(record), ["tenant_id"] = RowValue.Uuid(org), ["string_value"] = RowValue.Text(value),
    }, IdentityPrimaryKey: false);

    /// <summary>A well-formed v1 field envelope; every seed yields a distinct nonce and wrapped key.</summary>
    private static Row Sealed(Guid org, Guid record, Guid field, Guid item, byte seed) => new(ManifestItemKind.FieldValue, item, EncryptedValues, new Dictionary<string, RowValue?>(StringComparer.Ordinal)
    {
        ["encrypted_logical_type"] = RowValue.Text("String"), ["encrypted_value_bytes"] = RowValue.Bytes(Fill(24, seed)), ["encryption_algorithm_name"] = RowValue.Text("AES-256-GCM"),
        ["encryption_auth_tag_bytes"] = RowValue.Bytes(Fill(16, (byte)(seed + 1))), ["encryption_envelope_version"] = RowValue.Int32(1), ["encryption_key_reference"] = RowValue.Text("local-dev-kek-v1"),
        ["encryption_nonce_bytes"] = RowValue.Bytes(Fill(12, (byte)(seed + 2))), ["field_definition_id"] = RowValue.Uuid(field), ["field_value_item_id"] = RowValue.Uuid(item),
        ["form_record_group_id"] = null, ["form_record_id"] = RowValue.Uuid(record), ["tenant_id"] = RowValue.Uuid(org), ["wrapped_data_key_bytes"] = RowValue.Bytes(Fill(60, (byte)(seed + 3))),
    }, IdentityPrimaryKey: false);

    /// <summary>A well-formed v1 document envelope (identical across revisions: a carried document).</summary>
    private static Row Attachment(Guid org, Guid record, Guid field, Guid attachment) => new(ManifestItemKind.Attachment, attachment, Attachments, new Dictionary<string, RowValue?>(StringComparer.Ordinal)
    {
        ["ciphertext_sha256"] = RowValue.Bytes(Fill(32, 7)), ["ciphertext_size_bytes"] = RowValue.Int64(1024), ["detected_content_type"] = RowValue.Text("application/pdf"),
        ["encryption_algorithm_name"] = RowValue.Text("AES-256-GCM"), ["encryption_envelope_version"] = RowValue.Int32(1), ["encryption_key_reference"] = RowValue.Text("local-dev-kek-v1"),
        ["encryption_nonce_prefix"] = RowValue.Bytes(Fill(8, 3)), ["encryption_segment_size_bytes"] = RowValue.Int32(1048576), ["field_definition_id"] = RowValue.Uuid(field),
        ["file_attachment_id"] = RowValue.Uuid(attachment), ["form_record_id"] = RowValue.Uuid(record), ["original_file_name"] = RowValue.Text("statement.pdf"),
        ["plaintext_sha256"] = RowValue.Bytes(Fill(32, 9)), ["plaintext_size_bytes"] = RowValue.Int64(1000), ["scan_signature_name"] = null, ["scan_verdict"] = RowValue.Text("Clean"),
        ["scanned_on"] = RowValue.Timestamp(Fixed), ["scanner_name"] = RowValue.Text("clamav"), ["storage_object_key"] = RowValue.Text("objects/" + attachment.ToString("N")),
        ["storage_provider_name"] = RowValue.Text("azurite"), ["tenant_id"] = RowValue.Uuid(org), ["uploaded_by_user_id"] = RowValue.Uuid(Creator),
        ["uploaded_on"] = RowValue.Timestamp(Fixed), ["version_number"] = RowValue.Int32(1), ["wrapped_data_key"] = RowValue.Bytes(Fill(60, 5)),
    }, IdentityPrimaryKey: false);

    private static byte[] Fill(int length, byte value) => Enumerable.Repeat(value, length).ToArray();
}
