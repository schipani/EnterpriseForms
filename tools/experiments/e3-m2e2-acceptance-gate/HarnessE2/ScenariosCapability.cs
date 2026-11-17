// E3-M2f-2G group G (docs/security/E3_M2F2G_VERIFICATION_RECORD.md; closure docs/security/E3_M2F_CONTRACT_CLOSURE.md §4, §5, §6, §12.3):
// the E3-M2f-2 capability floor of this tree's REAL Authority (in-process, Release build) on the disposable PostgreSQL 18.0, with Envelope v2
// acceptance on only inside this run (owner authorization of E3-M2f-2G). Level-1 bytes come from REAL historical business builds
// (BusinessAgent: 11e15ee re-encodes schema-2 rows as schema 1, finding G-2), never from a harness re-implementation.
//
// Check kinds: "Security" (a capability property), "Premise" (a fact a security check depends on, e.g. that the legacy bytes really
// would quarantine if they reached verification), "Control" (the level-2 path proceeds normally). Expected-gap demonstrations
// (EXPECTED_GAP_G2, EXPECTED_GAP_INVENTORY) are recorded through Demonstrate and never counted.
using System.Security.Cryptography;
using System.Text.Json;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.Catalogue;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Application.Features.SearchIndex;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Catalogue;
using AuthorizationAuthority.Domain.Features.FieldSearch;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using AuthorizationAuthority.Infrastructure.Persistence;
using AuthorizationAuthority.Infrastructure.Signing;
using EnterpriseForms.Domain.Features.FormRecords;
using EnterpriseForms.Integrity.Canonical;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

internal sealed partial class Lab
{
    internal sealed record GNode(string Name, ContentService Content, ProtectionReconciliationService Census, SearchIndexBuilderService Builder, StatementTrace Trace, string ApplicationName, string Database);

    internal enum Entry
    {
        Prepare,
        Commit,
        Verify,
        BatchVerify,
        Census,
        Backfill,
        DeepVerify,
    }

    private const string VerifierWorkload = "field-protection-verifier";
    private const string BuilderWorkload = "search-index-builder";
    private const string CounterfactualDatabase = "m2f2gcf";
    private const string BaselineDatabase = "m2f2gbase";
    private const string Refused = "AuthorityException CLIENT_ENVELOPE_CAPABILITY_INSUFFICIENT 409 retryable=False";

    private readonly ECDsa sealKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly SearchIndexOptions searchOptions = new() { Mode = SearchIndexMode.Dark };
    private SearchSealOptions? sealOptions;
    private SearchIndexWriter? indexWriter;

    private SearchSealOptions SealOptions => sealOptions ??= new SearchSealOptions
    {
        Provider = SearchSealProviderKind.LocalDevelopment,
        LocalPrivateKeyPkcs8Base64 = Convert.ToBase64String(sealKey.ExportPkcs8PrivateKey()),
        PinnedKeys = [new PinnedSealKey { KeyName = "search-entry-seal-v1", KeyVersion = 1, SubjectPublicKeyInfoBase64 = Convert.ToBase64String(sealKey.ExportSubjectPublicKeyInfo()) }],
    };

    private SearchIndexWriter IndexWriter => indexWriter ??= new SearchIndexWriter(searchOptions, new SearchSealSigningGate(new LocalSearchSealSigner(SealOptions), new SearchSealVerifier(SealOptions)));

    /// <summary>
    /// This tree's Authority composed as the host composes it for the seven content entry points (one shared capability floor, CAP-O7),
    /// traced statement by statement. <paramref name="deploymentFloor"/> is the configured <c>Content:MinimumBusinessEnvelopeCapability</c>.
    /// </summary>
    private GNode GInstance(string name, string? deploymentFloor = null, string? database = null, IInterceptor? pause = null, string? applicationName = null, TimeProvider? clock = null, IInterceptor? extra = null)
    {
        var app = applicationName ?? "m2e2-runtime-" + name;
        var db = database ?? PrimaryDatabase;
        var cs = ContentAcceptanceRuleConnection.WithVersionedApplicationName(new NpgsqlConnectionStringBuilder(runtimeCs) { ApplicationName = app, Database = db }.ConnectionString)!;
        var trace = new StatementTrace();
        var store = new AuthorityStore(Factory.For(cs, trace.Commands, trace.Transactions, pause, extra), ReleaseB3, null, null, alerts);
        var time = clock ?? TimeProvider.System;
        var transition = new ProjectionMarkerTransitionState();
        transition.MarkCompleted();
        var verifier = new ProjectionIntegrityVerifier(store, AllowingSpiceDb.Create(AdminFor(db)), transition, new NoAlerts(), new ProjectionIntegrityMetrics(), time);
        var content = new ContentService(store, mac, verifier, new ArmedFaults(), time, new ShareContributorWriteSwitch(false, false), new ContentRevisionCoordinator(IndexWriter, time),
            envelopeV2Acceptance: acceptance, envelopeCapabilityFloor: EnvelopeCapabilityFloor.Create(deploymentFloor));
        return new GNode(name, content, new ProtectionReconciliationService(store, content, time), new SearchIndexBuilderService(store, content, IndexWriter, searchOptions, time, alerts), trace, app, db);
    }

    private Node AsNode(GNode g) => new(g.Name, g.Content, acceptance, new ArmedFaults());

    // ------------------------------------------------------------------------------------------------ one case per entry point
    /// <summary>The fixed inputs of one entry point's G-2 case (record, the head's committed operation, the census snapshot, the pending Commit).</summary>
    internal sealed record GCase(Entry Entry, Rec Record, Guid HeadOperation, long HeadRevision, Guid? SnapshotId = null, Prepared? Pending = null);

    /// <summary>The accepted head of <paramref name="record"/> as a business build would read and encode it: this harness's bytes for the
    /// current registry (<paramref name="agent"/> null) or <paramref name="agent"/>'s own registry, with the stored signatures.</summary>
    private async Task<List<StagedContentItem>> ObservedAsync(Rec record, string? agent, string? database = null)
    {
        if (agent is null)
        {
            return record.AcceptedStaged();
        }

        var head = await HeadIdentityAsync(record, database);
        var encoded = await EncodeWithAsync(agent, [.. record.Accepted.Select(a => (a.Row, head.Operation, head.Revision))]);
        return [.. record.Accepted.Select((a, i) => new StagedContentItem(a.Row.Kind, a.Row.ItemId, encoded[i], Convert.ToBase64String(a.Signature)))];
    }

    /// <summary>Staged rows re-encoded by <paramref name="agent"/>'s registry (the header keeps the harness encoding: it is not a business row here).</summary>
    private async Task<List<string>> EncodeWithAsync(string agent, List<(Staged Row, Guid Operation, long Revision)> rows)
    {
        var stored = rows.Select(r => Rows.StoredRow(r.Row.Source, r.Row.StorageKey, r.Operation, r.Revision)).ToList();
        var request = stored.Where(s => s is not null).Select(s => new { Type = s!.GetType().Name, Properties = PropertiesOf(s), KeyVersion = 1 }).ToList();
        var output = await RunAgentAsync(agent, "encode", new { Rows = request }, environment: null);
        if (!output.GetProperty("Ok").GetBoolean())
        {
            throw new InvalidOperationException("business agent encode failed: " + output.GetProperty("Error").GetString());
        }

        var results = output.GetProperty("Results").EnumerateArray().ToList();
        var bytes = new List<string>();
        var next = 0;
        for (var i = 0; i < rows.Count; i++)
        {
            bytes.Add(stored[i] is null ? rows[i].Row.Base64 : results[next++].GetProperty("Base64").GetString()!);
        }

        return bytes;
    }

    /// <summary>The columns of a stored business row as the business reads them (value types, strings and byte arrays only).</summary>
    private static Dictionary<string, JsonElement> PropertiesOf(object row)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var p in row.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance).Where(p => p.CanRead && p.CanWrite))
        {
            var t = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            if (t.IsValueType || t == typeof(string) || t == typeof(byte[]))
            {
                result[p.Name] = JsonSerializer.SerializeToElement(p.GetValue(row), p.PropertyType);
            }
        }

        return result;
    }

    /// <summary>Which signed columns of an accepted row a business build does not know (it would not read them from BusinessDb).</summary>
    private async Task<string> IgnoredColumnsAsync(string agent, Rec record)
    {
        var head = await HeadIdentityAsync(record);
        var stored = record.Accepted.Select(a => Rows.StoredRow(a.Row.Source, a.Row.StorageKey, head.Operation, head.Revision)).Where(s => s is not null).ToList();
        var output = await RunAgentAsync(agent, "encode", new { Rows = stored.Select(s => new { Type = s!.GetType().Name, Properties = PropertiesOf(s), KeyVersion = 1 }) }, environment: null);
        return string.Join(" | ", output.GetProperty("Results").EnumerateArray().Select((r, i) => stored[i]!.GetType().Name + ":" + string.Join(",", r.GetProperty("Ignored").EnumerateArray().Select(x => x.GetString()).Order(StringComparer.Ordinal))));
    }

    private async Task<(long Revision, Guid Operation)> HeadIdentityAsync(Rec record, string? database = null)
    {
        var rows = await RowsAsync(database is null ? adminCs : AdminFor(database), $"SELECT committed_content_revision, committed_operation_id FROM authority.record_content_heads WHERE organization_id = '{record.Org.Id}' AND form_record_id = '{record.Id}'");
        return (long.Parse(rows[0][0], System.Globalization.CultureInfo.InvariantCulture), Guid.Parse(rows[0][1]));
    }

    /// <summary>One call of <paramref name="entry"/> on <paramref name="node"/> with the given bytes and declaration; "OK: ..." or the failure.</summary>
    private async Task<string> CallAsync(GNode node, GCase c, List<StagedContentItem> observed, BusinessBuild build, List<StagedContentItem>? candidatesFor = null)
    {
        var record = c.Record;
        var org = record.Org;
        var capability = build.Declared;
        try
        {
            switch (c.Entry)
            {
                case Entry.Prepare:
                {
                    // An owner write: header + changed text, the current encrypted items carried; the CURRENT head as the client observed it.
                    var head = await HeadAsync(record, node.Database);
                    var operation = Guid.CreateVersion7();
                    var candidates = candidatesFor ?? throw new InvalidOperationException("prepare needs candidates");
                    var request = new PrepareContentChangeRequest(org.Id, record.Id, operation, 7, head.Revision, Convert.ToBase64String(head.Root),
                        [.. candidates.Select(s => new CandidateContentItem(s.ItemKind, s.ItemId, s.CanonicalBytesBase64))], CurrentItems: observed);
                    var prepared = await node.Content.PrepareAsync(Actor(org), request, "m2f2g-" + operation.ToString("N"), Guid.CreateVersion7(), CancellationToken.None, capability);
                    return "OK: prepared " + prepared.SignedItems.Count;
                }

                case Entry.Commit:
                    var revision = await node.Content.CommitAsync(Actor(org), c.Pending!.OperationId, new CommitContentChangeRequest(org.Id, record.Id, observed), Guid.CreateVersion7(), CancellationToken.None, capability);
                    return "OK: committed " + JsonSerializer.Serialize(revision);
                case Entry.Verify:
                {
                    var result = await node.Content.VerifyProjectionAsync(Actor(org), new VerifyRecordProjectionRequest(org.Id, record.Id, RequestedAction.Read, observed), Guid.CreateVersion7(), CancellationToken.None, capability);
                    return $"OK: verified {result.Verified} {result.FailureReasonCode}";
                }

                case Entry.BatchVerify:
                {
                    var result = await node.Content.VerifyProjectionsAsync(Actor(org), new VerifyRecordProjectionsRequest(org.Id, [new BatchVerificationItem(record.Id, c.HeadOperation, observed)]), Guid.CreateVersion7(), CancellationToken.None, capability);
                    var r = result.Results.Single();
                    return $"OK: verified {r.Verified} {r.FailureReasonCode}";
                }

                case Entry.Census:
                {
                    var result = await node.Census.CensusAsync(VerifierWorkload, c.SnapshotId!.Value, new ProtectionCensusRequest([new ProtectionCensusItem(record.Id, c.HeadOperation, observed)]), Guid.CreateVersion7(), CancellationToken.None, capability);
                    return "OK: census " + result.Results.Single().Outcome;
                }

                case Entry.Backfill:
                {
                    var result = await node.Builder.BackfillAsync(BuilderWorkload, new SearchIndexBackfillRequest(org.Id, Rows.Form, [new SearchIndexBackfillItem(record.Id, c.HeadOperation, observed)]), Guid.CreateVersion7(), CancellationToken.None, capability);
                    var r = result.Results.Single();
                    return $"OK: backfill {r.Outcome} quarantined {r.RecordQuarantined}";
                }

                default:
                {
                    var result = await node.Builder.DeepVerifyAsync(BuilderWorkload, new SearchIndexDeepVerifyRequest(org.Id, [new SearchIndexBackfillItem(record.Id, c.HeadOperation, observed)]), Guid.CreateVersion7(), CancellationToken.None, capability);
                    var r = result.Results.Single();
                    return $"OK: deep {r.UnitOutcome} {r.OutcomeCode} quarantined {r.RecordQuarantined}";
                }
            }
        }
        catch (Exception ex)
        {
            return Describe(ex);
        }
    }

    // ------------------------------------------------------------------------------------------------ state evidence
    /// <summary>
    /// A digest of every Authority row of <paramref name="org"/> (every table of the authority schema that has an organization_id column,
    /// row by row) plus the row count of every other authority table: any write of any kind changes it.
    /// </summary>
    private async Task<string> OrgFingerprintAsync(Org org, string? database = null)
    {
        var cs = database is null ? adminCs : AdminFor(database);
        var tables = await RowsAsync(cs, "SELECT c.table_name, bool_or(c.column_name = 'organization_id') FROM information_schema.columns c JOIN information_schema.tables t ON t.table_schema = c.table_schema AND t.table_name = c.table_name AND t.table_type = 'BASE TABLE' WHERE c.table_schema = 'authority' GROUP BY c.table_name ORDER BY c.table_name");
        var parts = new List<string>();
        foreach (var t in tables)
        {
            var sql = t[1] == "true" || t[1] == "True"
                ? $"SELECT count(*)||':'||md5(coalesce(string_agg(x::text, '|' ORDER BY x::text), '')) FROM authority.{t[0]} x WHERE x.organization_id = '{org.Id}'"
                : $"SELECT count(*)::text FROM authority.{t[0]}";
            parts.Add(t[0] + "=" + await ScalarAsync(cs, sql));
        }

        return string.Join(";", parts);
    }

    private static string FingerprintDiff(string before, string after)
    {
        if (before == after)
        {
            return "unchanged";
        }

        var a = before.Split(';').ToHashSet(StringComparer.Ordinal);
        return "CHANGED: " + string.Join(", ", after.Split(';').Where(x => !a.Contains(x)).Select(x => x.Split('=')[0]));
    }

    private async Task<string> HeadStateAsync(Rec record, string? database = null) => await ScalarAsync(database is null ? adminCs : AdminFor(database),
        $"SELECT committed_content_revision||':'||content_head_quarantined FROM authority.record_content_heads WHERE organization_id = '{record.Org.Id}' AND form_record_id = '{record.Id}'");

    private async Task<long> IncidentCountAsync(Org org, string? database = null) => await CountAsync(
        $"SELECT count(*) FROM authority.security_journal_entries WHERE organization_id = '{org.Id}' AND journal_event_kind IN ('IntegrityVerificationFailed', 'CurrentSnapshotMismatch')", database);

    /// <summary>Locks still held or awaited by sessions of <paramref name="applicationName"/> (none after a completed call: its transaction ended).</summary>
    private async Task<long> LingeringLocksAsync(string applicationName) => await CountAsync(
        $"SELECT count(*) FROM pg_locks l JOIN pg_stat_activity a ON a.pid = l.pid WHERE a.application_name LIKE '{applicationName}%' AND l.locktype <> 'virtualxid' AND NOT (l.locktype = 'relation' AND l.relation::regclass::text LIKE 'pg_%')");

    private async Task<string> SessionStateAsync(string applicationName) => await ScalarAsync(adminCs,
        $"SELECT coalesce(string_agg(state||'/'||coalesce(wait_event_type,'-'), ','), 'no session') FROM pg_stat_activity WHERE application_name LIKE '{applicationName}%'");

    // ------------------------------------------------------------------------------------------------ the group
    private Org gOrg = null!;
    private Org gUnbound = null!;
    private readonly Dictionary<Entry, GCase> gCases = [];
    private Guid gSnapshot;

    private async Task CapabilityScenariosAsync()
    {
        CurrentGroup = "G";
        var g2 = GInstance("G2");
        gOrg = await SeedOrgAsync(12, localKeys!, 1, journalDigest: true);
        gUnbound = await SeedOrgAsync(8, localKeys!, 1, journalDigest: true);
        var gn = AsNode(g2);

        // ---- G-00 setup: schema-2 heads (real M1 core, current registry) in gOrg, v1-only heads in gUnbound, all through the current build (level 2).
        var writes = new List<string>();
        foreach (var i in Enumerable.Range(0, 8))
        {
            var r = gOrg.Records[i];
            var batch = await BatchAsync(r);
            List<object> items = i == 2 ? [batch.Seal("g-" + i), await DocumentAsync(r, 3000)] : [batch.Seal("g-" + i), batch.Seal("g-b-" + i)];
            writes.Add(await WriteAsync(gn, r, Owner(r, [.. items])));
        }

        foreach (var r in gUnbound.Records)
        {
            writes.Add(await WriteAsync(gn, r, Owner(r, Rows.V1Field(gUnbound.Id, r.Id, Guid.CreateVersion7(), Guid.CreateVersion7(), (byte)(10 + gUnbound.Records.IndexOf(r))))));
        }

        // Commit case: an operation of the current build prepared on gOrg r1 and left Prepared (a level-1 client then tries to commit it).
        var pending = await PrepareAsync(gn, gOrg.Records[1], Owner(gOrg.Records[1], [.. gOrg.Records[1].Items]));

        // E3-M2f-2G follow-up (MG-17): the fixtures above are made by the current build's level-2 writes. If they fail, every check below
        // would run against missing fixtures; record the failure as a SETUP failure (never a security result) and execute nothing else in
        // this group, instead of aborting the whole harness. On a correct build this branch is never taken.
        if (writes.Any(w => w != "OK") || pending.Outcome != "OK")
        {
            Record("G-00", "Setup", "SETUP FAILED: the current build's level-2 fixture writes of group G did not all succeed; no security check of group G was executed (setup failure, not a security result)",
                "writes OK x16; pending OK", $"writes {string.Join(" | ", writes.Where(w => w != "OK").Distinct().Take(3))} ({writes.Count(w => w != "OK")} of {writes.Count} failed); pending {pending.Outcome} {pending.Message}", false);
            return;
        }

        // Census case: an effective protection decision for r4's encrypted field and a migration-verification snapshot fixing r4's head.
        var census = await SeedCensusAsync(gOrg, gOrg.Records[4]);

        // Search index: approvals for the text fields of r5 (backfill case) and r6 (deep-sweep case, whose entry the current build backfills).
        var approvals = await SeedApprovalsAsync(gOrg, [gOrg.Records[5], gOrg.Records[6]]);
        var anchored = new List<string>();
        foreach (var o in new[] { gOrg, gUnbound })
        {
            anchored.Add((await VerifyArchivedContentStreamAsync(o, PrimaryDatabase)).Summary);
        }

        var r6Observed = await ObservedAsync(gOrg.Records[6], null);
        var r6Head = await HeadIdentityAsync(gOrg.Records[6]);
        var r6Backfill = await CallAsync(g2, new GCase(Entry.Backfill, gOrg.Records[6], r6Head.Operation, r6Head.Revision), r6Observed, BusinessBuild.Current);

        var bindings = await BindingCountAsync($"organization_id = '{gOrg.Id}'");
        var unboundBindings = await BindingCountAsync($"organization_id = '{gUnbound.Id}'");
        Record("G-00", "Setup", "setup: 8 schema-2 heads in a bound organization (real M1 core; field batches and one document), 8 v1-only heads in an unbound organization, a Prepared operation of the current build, an effective protection decision with a migration-verification snapshot, two search approvals and one backfilled entry; every content entry anchored",
            "writes OK x16; pending Prepared; census seeded; approvals 2; backfill Indexed; bindings > 0 / 0; chains verified",
            $"writes {string.Join(",", writes.Distinct())}; pending {pending.Outcome}; census {census}; approvals {approvals}; backfill {r6Backfill}; bindings {bindings}/{unboundBindings}; {string.Join(" / ", anchored)}",
            writes.All(w => w == "OK") && pending.Outcome == "OK" && census.StartsWith("OK", StringComparison.Ordinal) && approvals == 2 && r6Backfill == "OK: backfill Indexed quarantined False"
            && bindings > 0 && unboundBindings == 0 && anchored.All(a => a.Contains("chain verified", StringComparison.Ordinal)));
        gSnapshot = Guid.Parse(census.Split(' ')[1]);

        foreach (var (entry, index) in new[] { (Entry.Prepare, 0), (Entry.Commit, 1), (Entry.Verify, 2), (Entry.BatchVerify, 3), (Entry.Census, 4), (Entry.Backfill, 5), (Entry.DeepVerify, 6) })
        {
            var record = gOrg.Records[index];
            var head = await HeadIdentityAsync(record);
            gCases[entry] = new GCase(entry, record, head.Operation, head.Revision, entry == Entry.Census ? gSnapshot : null, entry == Entry.Commit ? pending : null);
        }

        await EncoderFidelityChecksAsync();

        // Disposable copies of the bound state: one for the counterfactual (level 2 with legacy bytes), one for the baseline Authority.
        await CopyDatabaseAsync(PrimaryDatabase, CounterfactualDatabase);
        await CopyDatabaseAsync(PrimaryDatabase, BaselineDatabase);

        await G2EntryPointChecksAsync();
        await G2CounterfactualAsync();
        await BaselineDemonstrationsAsync();
        await FloorMatrixChecksAsync();
        await AnyStateBindingChecksAsync();
        await LedgerQueryChecksAsync();
        await GrantSentinelAsync("after-capability");
    }

    // ------------------------------------------------------------------------------------------------ setup helpers
    /// <summary>
    /// An effective PublishFormSecurityRevision for the record's encrypted field, its Active protection-requirement resource, and a
    /// migration-verification snapshot fixing the record's head, written through the real runtime store in one locked transaction
    /// (as the security-command and catalogue services would). Returns "OK &lt;snapshot id&gt;".
    /// </summary>
    private async Task<string> SeedCensusAsync(Org org, Rec record, string? database = null)
    {
        var field = record.Items.OfType<FieldEncryptedValue>().First().FieldDefinitionId;
        var head = await HeadIdentityAsync(record, database);
        var store = Store("g-census-seed", ReleaseB3, database);
        await using var scope = await store.BeginScopeAsync(CancellationToken.None);
        var state = await scope.LockOrganizationAsync(org.Id, CancellationToken.None) ?? throw new InvalidOperationException("org");
        var now = DateTimeOffset.UtcNow;
        var command = new SecurityCommand
        {
            SecurityCommandId = Guid.CreateVersion7(), OrganizationId = org.Id, ActorIdentityBindingId = org.OwnerBinding, CommandKindName = SecurityCommandKinds.PublishFormSecurityRevision,
            CommandIdempotencyKey = "m2f2g-census-" + Guid.CreateVersion7().ToString("N"), CommandRequestDigest = RandomNumberGenerator.GetBytes(32), ExpectedAuthorizationRevision = state.AuthorizationRevision,
            CommittedAuthorizationRevision = state.AuthorizationRevision, CommandStateName = SecurityCommandStates.Effective,
            CommandPayloadJson = CanonicalJson.Serialize(new PublishFormSecurityRevisionPayload(Rows.Form, field, "ApplicationEncrypted", 1)), CorrelationIdentifier = Guid.CreateVersion7(),
            CommandCreatedOn = now, CommandModifiedOn = now, CommandEffectiveOn = now,
        };
        scope.AddCommand(command);
        scope.AddResource(new AuthorityResource
        {
            AuthorityResourceId = Guid.CreateVersion7(), OrganizationId = org.Id, ResourceTypeName = ResourceTypeNames.FieldProtectionRequirement, ResourceEntityId = field,
            ParentResourceEntityId = Rows.Form, ResourceLifecycleState = ResourceLifecycleStates.Active, RegisteredBySecurityCommandId = command.SecurityCommandId, ResourceRegisteredOn = now,
        });
        var snapshot = new SearchCatalogueSnapshot
        {
            SearchCatalogueSnapshotId = Guid.CreateVersion7(), OrganizationId = org.Id, SnapshotScopeKind = CatalogueScopeKinds.MigrationVerification, RequestingWorkloadName = VerifierWorkload,
            ScopingSecurityCommandId = command.SecurityCommandId, FormDefinitionId = Rows.Form, SnapshotAuthorizationRevision = state.AuthorizationRevision, SnapshotProjectionRevision = state.ProjectionAppliedRevision,
            SnapshotContentIntegrityRevision = state.ContentIntegrityRevision, CatalogueEntryCount = 1, CatalogueDigest = RandomNumberGenerator.GetBytes(32), SnapshotIssuedOn = now, SnapshotExpiresOn = now.AddHours(3),
        };
        scope.AddCatalogueSnapshot(snapshot, [new SearchCatalogueSnapshotEntry
        {
            SearchCatalogueSnapshotEntryId = Guid.CreateVersion7(), SearchCatalogueSnapshotId = snapshot.SearchCatalogueSnapshotId, OrganizationId = org.Id, FormRecordId = record.Id,
            CatalogueEntryPosition = 0, CommittedContentRevision = head.Revision, CommittedOperationId = head.Operation, ContentHeadQuarantined = false, ResourceLifecycleState = ResourceLifecycleStates.Active,
        }]);
        await scope.CommitAsync(CancellationToken.None);
        return "OK " + snapshot.SearchCatalogueSnapshotId.ToString("D");
    }

    /// <summary>Active Internal/Standard String approvals of each record's text field, applied to the index as the approval command would apply them.</summary>
    private async Task<int> SeedApprovalsAsync(Org org, IReadOnlyList<Rec> records, string? database = null)
    {
        var store = Store("g-approval-seed", ReleaseB3, database);
        await using var scope = await store.BeginScopeAsync(CancellationToken.None);
        var state = await scope.LockOrganizationAsync(org.Id, CancellationToken.None) ?? throw new InvalidOperationException("org");
        var now = DateTimeOffset.UtcNow;
        // The approving command (an Effective PublishFormSecurityRevision, as SecurityCommandService records it for new approvals).
        var command = new SecurityCommand
        {
            SecurityCommandId = Guid.CreateVersion7(), OrganizationId = org.Id, ActorIdentityBindingId = org.OwnerBinding, CommandKindName = SecurityCommandKinds.PublishFormSecurityRevision,
            CommandIdempotencyKey = "m2f2g-approval-" + Guid.CreateVersion7().ToString("N"), CommandRequestDigest = RandomNumberGenerator.GetBytes(32), ExpectedAuthorizationRevision = state.AuthorizationRevision,
            CommittedAuthorizationRevision = state.AuthorizationRevision, CommandStateName = SecurityCommandStates.Effective,
            CommandPayloadJson = CanonicalJson.Serialize(new PublishFormSecurityRevisionPayload(Rows.Form, records[0].TextField, "Standard", 1)), CorrelationIdentifier = Guid.CreateVersion7(),
            CommandCreatedOn = now, CommandModifiedOn = now, CommandEffectiveOn = now,
        };
        scope.AddCommand(command);
        var added = records.Select(r => new FieldSearchApproval
        {
            FieldSearchApprovalId = Guid.CreateVersion7(), OrganizationId = org.Id, FormDefinitionId = Rows.Form, FieldDefinitionId = r.TextField, ApprovedFormVersionNumber = 1, ApprovedLayoutDigest = RandomNumberGenerator.GetBytes(32),
            ApprovedFieldDataType = "String", ApprovedFieldClassification = "Internal", ApprovedProtectionMode = "Standard", ApprovedOperatorsJson = "[\"equals\"]", ApprovedFieldLabelSnapshot = "Text",
            ApprovalReferenceText = "m2f2g", ApprovalStateName = FieldSearchApprovalStates.Active, ApprovedBySecurityCommandId = command.SecurityCommandId, ApprovedOn = now,
        }).ToList();
        foreach (var a in added)
        {
            scope.AddFieldSearchApproval(a);
        }

        await IndexWriter.ApplyApprovalChangesAsync(scope, state, added, [], now, CancellationToken.None);
        await scope.CommitAsync(CancellationToken.None);
        return added.Count;
    }

    // ------------------------------------------------------------------------------------------------ encoder fidelity (premises)
    private async Task EncoderFidelityChecksAsync()
    {
        var record = gCases[Entry.Verify].Record; // field batch + document: both v2 entity kinds
        var accepted = record.AcceptedStaged();
        var current = await ObservedAsync(record, inputs.BusinessAgents["current"]);
        var m2e2 = await ObservedAsync(record, inputs.BusinessAgents["0be5f52"]);
        var pre = await ObservedAsync(record, inputs.BusinessAgents["11e15ee"]);
        static bool IsV2(StagedContentItem s) => s.ItemKind is ManifestItemKind.FieldValue or ManifestItemKind.Attachment && RowCanonicalDecoderSchema(s) == 2;
        var v2Items = accepted.Where(IsV2).Select(s => s.ItemId).ToHashSet();
        string Compare(List<StagedContentItem> other) => $"{other.Count(o => accepted.Single(a => a.ItemId == o.ItemId && a.ItemKind == o.ItemKind).CanonicalBytesBase64 == o.CanonicalBytesBase64)}/{accepted.Count} identical";
        Record("G-01", "Premise", "encoder fidelity: this tree's business build (E3-M2f-1+, BusinessAgent) re-encodes every stored row of a schema-2 head (field batch and document) byte-identically",
            $"{accepted.Count}/{accepted.Count} identical", Compare(current), Compare(current) == $"{accepted.Count}/{accepted.Count} identical");
        Record("G-02", "Premise", "encoder fidelity: the 0be5f52 business build (E3-M2e-2; faithful schema-2 encoding, no capability header) re-encodes every stored row byte-identically",
            $"{accepted.Count}/{accepted.Count} identical", Compare(m2e2), Compare(m2e2) == $"{accepted.Count}/{accepted.Count} identical");
        var preDiffers = pre.Where(p => accepted.Single(a => a.ItemId == p.ItemId && a.ItemKind == p.ItemKind).CanonicalBytesBase64 != p.CanonicalBytesBase64).Select(p => p.ItemId).ToHashSet();
        var ignored = await IgnoredColumnsAsync(inputs.BusinessAgents["11e15ee"], record);
        Record("G-03", "Premise", "finding G-2 is real in the historical code: the 11e15ee business build (before E3-M2b-2) re-encodes exactly the schema-2 rows differently (it does not know the v2 columns and always uses signed schema 1); the other rows are identical",
            "differing rows = the v2 rows (>= 2); unknown columns include DataKeyIdentifier, DataKeyCommitmentBytes, KeyWrapAlgorithmCode",
            $"v2 rows {v2Items.Count}; differing {preDiffers.Count}; same set {preDiffers.SetEquals(v2Items)}; ignored [{ignored}]",
            v2Items.Count >= 2 && preDiffers.SetEquals(v2Items) && ignored.Contains("DataKeyIdentifier", StringComparison.Ordinal) && ignored.Contains("DataKeyCommitmentBytes", StringComparison.Ordinal) && ignored.Contains("KeyWrapAlgorithmCode", StringComparison.Ordinal));
    }

    private static int RowCanonicalDecoderSchema(StagedContentItem s) => RowCanonicalDecoder.Decode(Convert.FromBase64String(s.CanonicalBytesBase64)).Header.SchemaVersion;

    // ------------------------------------------------------------------------------------------------ M2F-10..16 on real PostgreSQL
    /// <summary>
    /// The level-1 request of each entry point, as the 11e15ee business build would send it (bytes from its own registry). Prepare has two
    /// shapes: <paramref name="removeEncrypted"/> false re-stages every carried row (its schema-2 rows re-encoded as schema 1, which candidate
    /// validation S-4 refuses before snapshot binding); true removes the encrypted values (a valid candidate) while its observed head is
    /// re-encoded, which is the path to snapshot binding and CurrentSnapshotMismatch (closure §6.3).
    /// </summary>
    private async Task<(List<StagedContentItem> Observed, List<StagedContentItem>? Candidates)> LegacyRequestAsync(GCase c, string agent, bool removeEncrypted = true, string? database = null)
    {
        if (c.Entry == Entry.Commit)
        {
            // The pending operation's staged rows, re-encoded by the legacy registry, with the signatures its Prepare returned.
            var p = c.Pending!;
            var encoded = await EncodeWithAsync(agent, [.. p.Rows.Select(r => (r, p.OperationId, c.HeadRevision + 1))]);
            var signatures = p.Operation!.SignedItems.ToDictionary(s => (s.ItemKind, s.ItemId), s => s.RowSignatureBase64);
            return ([.. p.Rows.Select((r, i) => new StagedContentItem(r.Kind, r.ItemId, encoded[i], signatures[(r.Kind, r.ItemId)]))], null);
        }

        var observed = await ObservedAsync(c.Record, agent, database);
        if (c.Entry != Entry.Prepare)
        {
            return (observed, null);
        }

        // Prepare: the legacy client stages (with its own registry, under a new operation) either the carried rows or a removal of them.
        var operation = Guid.CreateVersion7();
        var sources = removeEncrypted ? Owner(c.Record) : Owner(c.Record, [.. c.Record.Items]);
        var staged = sources.Select(s => Rows.Encode(s, operation, c.HeadRevision + 1)).ToList();
        var legacy = await EncodeWithAsync(agent, [.. staged.Select(s => (s, operation, c.HeadRevision + 1))]);
        return (observed, [.. staged.Select((s, i) => new StagedContentItem(s.Kind, s.ItemId, legacy[i], string.Empty))]);
    }

    private async Task<(List<StagedContentItem> Observed, List<StagedContentItem>? Candidates)> CurrentRequestAsync(GCase c)
    {
        if (c.Entry == Entry.Commit)
        {
            return (StagedOf(c.Pending!.Operation!, c.Pending.Rows), null);
        }

        var observed = c.Record.AcceptedStaged();
        if (c.Entry != Entry.Prepare)
        {
            return (observed, null);
        }

        var operation = Guid.CreateVersion7();
        var head = await HeadAsync(c.Record);
        var staged = Owner(c.Record, [.. c.Record.Items]).Select(s => Rows.Encode(s, operation, head.Revision + 1)).ToList();
        return (observed, [.. staged.Select(s => new StagedContentItem(s.Kind, s.ItemId, s.Base64, string.Empty))]);
    }

    private async Task G2EntryPointChecksAsync()
    {
        var n = 10;
        foreach (var (entry, c) in gCases.OrderBy(k => k.Key))
        {
            var node = GInstance("G2-" + entry);
            var legacy = await LegacyRequestAsync(c, inputs.BusinessAgents["11e15ee"]);
            var outcomes = new List<string>();
            var before = await OrgFingerprintAsync(gOrg);
            var macBefore = (mac.Verifications, mac.Signatures);
            var alertsBefore = alerts.Count;
            var incidentsBefore = await IncidentCountAsync(gOrg);
            var integrityBefore = await IntegrityRevisionAsync(gOrg);
            var headBefore = await HeadStateAsync(c.Record);
            var pendingBefore = c.Pending is null ? "-" : await StateAsync(c.Pending.OperationId);
            var shapes = new List<string>();
            var committed = false;
            var after = 0;
            foreach (var build in new[] { BusinessBuild.Undeclared, BusinessBuild.DeclaresOne })
            {
                node.Trace.Reset();
                outcomes.Add(await CallAsync(node, c, legacy.Observed, build, legacy.Candidates));
                shapes.Add(node.Trace.Shape());
                committed |= node.Trace.Committed;
                after = Math.Max(after, node.Trace.StatementsAfterLedger);
            }

            var afterPrint = await OrgFingerprintAsync(gOrg);
            var lingering = await LingeringLocksAsync(node.ApplicationName);
            var verifications = mac.Verifications - macBefore.Verifications;
            var signatures = mac.Signatures - macBefore.Signatures;
            var pendingAfter = c.Pending is null ? "-" : await StateAsync(c.Pending.OperationId);
            var id = "G-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var carryRefused = "-";
            if (entry == Entry.Prepare)
            {
                // The other Prepare shape (every carried row re-staged by the legacy registry) is refused the same way.
                var carry = await LegacyRequestAsync(c, inputs.BusinessAgents["11e15ee"], removeEncrypted: false);
                var macCarry = mac.Verifications;
                carryRefused = await CallAsync(node, c, carry.Observed, BusinessBuild.Undeclared, carry.Candidates) + (mac.Verifications == macCarry ? " (no MAC verification)" : " (MAC VERIFIED)");
                afterPrint = await OrgFingerprintAsync(gOrg);
            }

            Record(id, "Security", $"M2F-{n} G-2 ({entry}, real PostgreSQL): the request of the 11e15ee business build (no header, and the same bytes declaring level 1) in an organization with a binding and a schema-2 head is refused with the fixed 409 BEFORE any signed-content verification: no HMAC verification or signing ran, the transaction executed nothing after the boolean existence query and committed nothing, no lock lingers, the head is not quarantined, no incident is journaled, the content-integrity revision is unchanged, {(c.Pending is null ? string.Empty : "the pending operation is still Prepared, ")}and every Authority row of the organization is unchanged",
                $"{Refused} x2{(entry == Entry.Prepare ? " (removal shape) and once more for the carry shape" : string.Empty)}; MAC verify/sign 0/0; 0 statements after the existence query; no commit; 0 locks; head unchanged; incidents unchanged; integrity unchanged; organization rows unchanged",
                $"{string.Join(" / ", outcomes.Distinct())}{(entry == Entry.Prepare ? "; carry shape " + carryRefused : string.Empty)}; MAC {verifications}/{signatures}; after-existence {after}; commit {committed}; locks {lingering}; head {Diff(headBefore, await HeadStateAsync(c.Record))}; incidents {incidentsBefore}->{await IncidentCountAsync(gOrg)}; integrity {integrityBefore}->{await IntegrityRevisionAsync(gOrg)}; pending {Diff(pendingBefore, pendingAfter)}; rows {FingerprintDiff(before, afterPrint)}; alerts +{alerts.Count - alertsBefore}; shape [{shapes[0]}]",
                outcomes.All(o => o == Refused) && (entry != Entry.Prepare || carryRefused == Refused + " (no MAC verification)") && verifications == 0 && signatures == 0 && after == 0 && !committed && lingering == 0 && headBefore == await HeadStateAsync(c.Record)
                && incidentsBefore == await IncidentCountAsync(gOrg) && integrityBefore == await IntegrityRevisionAsync(gOrg) && pendingBefore == pendingAfter && before == afterPrint);

            // Control: the current build's request (level 2, faithful bytes) proceeds through the normal authorized path.
            var current = await CurrentRequestAsync(c);
            node.Trace.Reset();
            var control = await CallAsync(node, c, current.Observed, BusinessBuild.Current, current.Candidates);
            var expected = entry switch
            {
                Entry.Prepare => "OK: prepared",
                Entry.Commit => "OK: committed",
                Entry.Verify or Entry.BatchVerify => "OK: verified True",
                Entry.Census => "OK: census Verified",
                Entry.Backfill => "OK: backfill Indexed quarantined False",
                _ => "OK: deep Pass",
            };
            Record(id + "c", "Control", $"{entry}: the current build's request (level 2, faithful bytes) proceeds through the normal authorized path and never queries the ledger for the capability decision",
                $"{expected}...; ledger statements: {(entry == Entry.Prepare ? "DK rules only (no EXISTS)" : "none")}",
                $"{control}; ledger statements [{string.Join(" | ", node.Trace.LedgerStatements.Select(StatementTrace.Classify))}]",
                control.StartsWith(expected, StringComparison.Ordinal) && !node.Trace.Statements.Any(s => s.Contains("content_data_key_bindings", StringComparison.Ordinal) && s.Contains("EXISTS", StringComparison.OrdinalIgnoreCase)));
            if (entry == Entry.Commit && control.StartsWith("OK", StringComparison.Ordinal))
            {
                c.Record.Accepted = [.. c.Pending!.Rows.Select(r => (r, Convert.FromBase64String(c.Pending.Operation!.SignedItems.Single(s => s.ItemKind == r.Kind && s.ItemId == r.ItemId).RowSignatureBase64)))];
                c.Record.Sources = c.Pending.Sources;
            }

            n++;
        }
    }

    /// <summary>What the legacy request of <paramref name="entry"/> does once it is past the capability decision (closure §6.3, as observed).</summary>
    private static string ExpectedConsequence(Entry entry) => entry switch
    {
        Entry.Prepare => "CurrentSnapshotMismatch journaled and the content-integrity revision advanced (removal shape: valid candidate, re-encoded observed head)",
        Entry.Commit => "rejected by staged verification (CANDIDATE_MISMATCH 409); the operation stays Prepared; no incident (no false integrity outcome at Commit)",
        _ => "the head is falsely quarantined and IntegrityVerificationFailed is journaled",
    };

    private static bool ConsequenceObserved(Entry entry, string outcome, string head, string headAfter, long incidents, long incidentsAfter, long integrity, long integrityAfter, string pending, string pendingAfter) => entry switch
    {
        Entry.Prepare => incidentsAfter > incidents && integrityAfter > integrity && head == headAfter,
        Entry.Commit => outcome.Contains("CANDIDATE_MISMATCH 409", StringComparison.Ordinal) && pending == pendingAfter && incidents == incidentsAfter && head == headAfter,
        _ => headAfter.EndsWith(":true", StringComparison.OrdinalIgnoreCase) && incidentsAfter > incidents,
    };

    /// <summary>
    /// Premise of M2F-10..16, on a disposable copy only: the SAME legacy bytes, declared as level 2 (so the floor admits them), reach
    /// signed-content verification. For the verification and workload entry points that is a false quarantine; for Prepare a journaled
    /// snapshot mismatch; for Commit a refusal by staged verification. This is what the capability refusal of G-10..G-16 runs ahead of.
    /// </summary>
    private async Task G2CounterfactualAsync()
    {
        var n = 20;
        foreach (var (entry, c) in gCases.OrderBy(k => k.Key))
        {
            var node = GInstance("G2CF-" + entry, database: CounterfactualDatabase);
            var legacy = await LegacyRequestAsync(c, inputs.BusinessAgents["11e15ee"], database: CounterfactualDatabase);
            var head = await HeadStateAsync(c.Record, CounterfactualDatabase);
            var incidents = await IncidentCountAsync(gOrg, CounterfactualDatabase);
            var integrity = await CountAsync($"SELECT content_integrity_revision FROM authority.organization_authorization_states WHERE organization_id = '{gOrg.Id}'", CounterfactualDatabase);
            var pending = c.Pending is null ? "-" : await StateAsync(c.Pending.OperationId, CounterfactualDatabase);
            var macBefore = mac.Verifications;
            var outcome = await CallAsync(node, c, legacy.Observed, BusinessBuild.Current, legacy.Candidates);
            var verified = mac.Verifications - macBefore;
            var headAfter = await HeadStateAsync(c.Record, CounterfactualDatabase);
            var incidentsAfter = await IncidentCountAsync(gOrg, CounterfactualDatabase);
            var integrityAfter = await CountAsync($"SELECT content_integrity_revision FROM authority.organization_authorization_states WHERE organization_id = '{gOrg.Id}'", CounterfactualDatabase);
            var pendingAfter = c.Pending is null ? "-" : await StateAsync(c.Pending.OperationId, CounterfactualDatabase);
            Record("G-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture), "Premise", $"{entry} counterfactual (disposable copy only): the same 11e15ee request declared as level 2 passes the floor and reaches signed-content verification: {ExpectedConsequence(entry)}. This is what the refusal of G-{n - 10} runs ahead of",
                ExpectedConsequence(entry), $"{outcome}; MAC verifications {verified}; head {head}->{headAfter}; incidents {incidents}->{incidentsAfter}; integrity {integrity}->{integrityAfter}; pending {pending}->{pendingAfter}",
                ConsequenceObserved(entry, outcome, head, headAfter, incidents, incidentsAfter, integrity, integrityAfter, pending, pendingAfter) && (entry == Entry.Prepare || verified > 0));
            if (entry == Entry.Prepare)
            {
                // The carry shape stops earlier: candidate validation (S-4) refuses its schema-1-encoded schema-2 rows before snapshot binding.
                var carry = await LegacyRequestAsync(c, inputs.BusinessAgents["11e15ee"], removeEncrypted: false, database: CounterfactualDatabase);
                var before = await IncidentCountAsync(gOrg, CounterfactualDatabase);
                var carried = await CallAsync(node, c, carry.Observed, BusinessBuild.Current, carry.Candidates);
                Record("G-20a", "Premise", "Prepare counterfactual, carry shape (disposable copy only): every carried row re-staged by the 11e15ee registry is refused by candidate validation (S-4 ENVELOPE_SCHEMA_MISMATCH, 422) before snapshot binding: no incident. Only the removal shape (G-20) reaches the snapshot-mismatch journal",
                    "INVALID_COMMAND 422; incidents unchanged", $"{carried}; incidents {before}->{await IncidentCountAsync(gOrg, CounterfactualDatabase)}",
                    carried.StartsWith("AuthorityException INVALID_COMMAND 422", StringComparison.Ordinal) && before == await IncidentCountAsync(gOrg, CounterfactualDatabase));
            }

            n++;
        }
    }

    // ------------------------------------------------------------------------------------------------ the baseline Authority (expected gaps)
    private async Task BaselineDemonstrationsAsync()
    {
        // Control: the agent source compiled against THIS tree, with no capability argument (null = level 1), is refused.
        {
            var c = gCases[Entry.Verify];
            var legacy = await LegacyRequestAsync(c, inputs.BusinessAgents["11e15ee"], database: BaselineDatabase);
            var before = await OrgFingerprintAsync(gOrg, BaselineDatabase);
            var output = await AuthorityAgentAsync(inputs.AuthorityAgents["current"], c, legacy.Observed, legacy.Candidates, BaselineDatabase);
            Record("G-30", "Control", "the AuthorityAgent source compiled against this tree (no capability argument: level 1) is refused on the same bound copy: the demonstrations below differ only in the Authority build",
                $"{Refused}; copy unchanged", $"{AgentOutcome(output)}; copy {FingerprintDiff(before, await OrgFingerprintAsync(gOrg, BaselineDatabase))}",
                AgentOutcome(output) == Refused && before == await OrgFingerprintAsync(gOrg, BaselineDatabase));
        }

        // M2F-40 / R-M2f-3: the CARV-3 baseline 0be5f52 admits a level-1 request in a bound organization (here with faithful 0be5f52 bytes).
        {
            var record = gOrg.Records[7];
            var head = await HeadIdentityAsync(record, BaselineDatabase);
            var c = new GCase(Entry.Verify, record, head.Operation, head.Revision);
            var observed = await ObservedAsync(record, inputs.BusinessAgents["0be5f52"], BaselineDatabase);
            var output = await AuthorityAgentAsync(inputs.AuthorityAgents["0be5f52"], c, observed, null, BaselineDatabase);
            Demonstrate("GG-INV", "EXPECTED_GAP_INVENTORY", "M2F-40 / R-M2f-3 (disposable copy): the baseline Authority 0be5f52 (CARV 3, floor 3 admits it) has no capability floor: the level-1 request of the 0be5f52 business build is ADMITTED in an organization with a binding and verifies. Floor 3 and CARV markers cannot tell it from an E3-M2f-2 build (INV-3): only the deployment inventory (INV-1/INV-2) can",
                "admitted: verified True (rule version 3)", AgentOutcome(output) + " (rule version " + output.GetProperty("BuildRuleVersion") + ")", AgentOutcome(output) == "OK: verified True");
        }

        // EXPECTED_GAP_G2: the same 11e15ee requests on the baseline Authority (no capability floor) reach signed-content verification.
        var n = 1;
        foreach (var (entry, c) in gCases.OrderBy(k => k.Key))
        {
            var legacy = await LegacyRequestAsync(c, inputs.BusinessAgents["11e15ee"], database: BaselineDatabase);
            var head = await HeadStateAsync(c.Record, BaselineDatabase);
            var incidents = await IncidentCountAsync(gOrg, BaselineDatabase);
            var integrity = await CountAsync($"SELECT content_integrity_revision FROM authority.organization_authorization_states WHERE organization_id = '{gOrg.Id}'", BaselineDatabase);
            var pending = c.Pending is null ? "-" : await StateAsync(c.Pending.OperationId, BaselineDatabase);
            var output = await AuthorityAgentAsync(inputs.AuthorityAgents["0be5f52"], c, legacy.Observed, legacy.Candidates, BaselineDatabase);
            var outcome = AgentOutcome(output);
            var headAfter = await HeadStateAsync(c.Record, BaselineDatabase);
            var incidentsAfter = await IncidentCountAsync(gOrg, BaselineDatabase);
            var integrityAfter = await CountAsync($"SELECT content_integrity_revision FROM authority.organization_authorization_states WHERE organization_id = '{gOrg.Id}'", BaselineDatabase);
            var pendingAfter = c.Pending is null ? "-" : await StateAsync(c.Pending.OperationId, BaselineDatabase);
            Demonstrate("GG-0" + n, "EXPECTED_GAP_G2", $"{entry} on the baseline Authority 0be5f52 (disposable copy): the 11e15ee business build's request is ADMITTED (no capability floor) and reaches signed-content verification: {ExpectedConsequence(entry)}. This is finding G-2 on a build without E3-M2f-2; it is not a passing property",
                ExpectedConsequence(entry), $"{outcome}; MAC verifications {output.GetProperty("MacVerifications")}; head {head}->{headAfter}; incidents {incidents}->{incidentsAfter}; integrity {integrity}->{integrityAfter}; pending {pending}->{pendingAfter}",
                ConsequenceObserved(entry, outcome, head, headAfter, incidents, incidentsAfter, integrity, integrityAfter, pending, pendingAfter));
            n++;
        }
    }

    private static string AgentOutcome(JsonElement output)
    {
        if (!output.GetProperty("Ok").GetBoolean())
        {
            return output.GetProperty("Error").GetString() ?? "error";
        }

        var r = output.GetProperty("Result");
        string P(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.ToString() : "?";
        if (r.TryGetProperty("Results", out var results))
        {
            var first = results.EnumerateArray().First();
            return first.TryGetProperty("UnitOutcome", out _) ? $"OK: deep {P(first, "UnitOutcome")} {P(first, "OutcomeCode")}"
                : first.TryGetProperty("Verified", out _) ? $"OK: verified {P(first, "Verified")}"
                : $"OK: {P(first, "Outcome")}";
        }

        return r.TryGetProperty("Verified", out var v) ? "OK: verified " + v : "OK";
    }

    private async Task<JsonElement> AuthorityAgentAsync(string agent, GCase c, List<StagedContentItem> observed, List<StagedContentItem>? candidates, string database)
    {
        var org = c.Record.Org;
        object request = c.Entry switch
        {
            Entry.Prepare => new PrepareContentChangeRequest(org.Id, c.Record.Id, Guid.CreateVersion7(), 7, c.HeadRevision, Convert.ToBase64String((await HeadAsync(c.Record, database)).Root),
                [.. candidates!.Select(s => new CandidateContentItem(s.ItemKind, s.ItemId, s.CanonicalBytesBase64))], CurrentItems: observed),
            Entry.Commit => new CommitContentChangeRequest(org.Id, c.Record.Id, observed),
            Entry.Verify => new VerifyRecordProjectionRequest(org.Id, c.Record.Id, RequestedAction.Read, observed),
            Entry.BatchVerify => new VerifyRecordProjectionsRequest(org.Id, [new BatchVerificationItem(c.Record.Id, c.HeadOperation, observed)]),
            Entry.Census => new ProtectionCensusRequest([new ProtectionCensusItem(c.Record.Id, c.HeadOperation, observed)]),
            Entry.Backfill => new SearchIndexBackfillRequest(org.Id, Rows.Form, [new SearchIndexBackfillItem(c.Record.Id, c.HeadOperation, observed)]),
            _ => new SearchIndexDeepVerifyRequest(org.Id, [new SearchIndexBackfillItem(c.Record.Id, c.HeadOperation, observed)]),
        };
        var kind = c.Entry switch
        {
            Entry.Prepare => "prepare", Entry.Commit => "commit", Entry.Verify => "verify", Entry.BatchVerify => "batch-verify", Entry.Census => "census", Entry.Backfill => "backfill", _ => "deep-verify",
        };
        var runtime = Target.Require(new NpgsqlConnectionStringBuilder(runtimeCs) { Database = database }.ConnectionString);
        return await RunAgentAsync(agent, kind, new
        {
            Issuer, Subject = org.Owner.ToString("D"), Request = request, Key = "m2f2g-agent-" + Guid.CreateVersion7().ToString("N"), OperationId = c.Pending?.OperationId,
            SnapshotId = c.SnapshotId, Workload = c.Entry == Entry.Census ? VerifierWorkload : BuilderWorkload,
            SealPkcs8 = SealOptions.LocalPrivateKeyPkcs8Base64, SealSpki = SealOptions.PinnedKeys[0].SubjectPublicKeyInfoBase64,
        }, new Dictionary<string, string> { ["M2F2G_AGENT_RUNTIME"] = runtime });
    }

    /// <summary>Runs one agent call in its own process (input/output files under the evidence folder); returns its JSON output.</summary>
    private async Task<JsonElement> RunAgentAsync(string agent, string command, object input, Dictionary<string, string>? environment)
    {
        // Agent inputs carry test-only credentials and the run's seal key: they stay in the work directory, never in the evidence.
        var dir = inputs.AgentWorkDir;
        Directory.CreateDirectory(dir);
        var name = $"{DateTime.UtcNow:HHmmssfff}-{Path.GetFileNameWithoutExtension(agent)}-{command}-{Guid.CreateVersion7():N}";
        var inPath = Path.Combine(dir, name + ".in.json");
        var outPath = Path.Combine(dir, name + ".out.json");
        await File.WriteAllTextAsync(inPath, JsonSerializer.Serialize(input));
        var start = new System.Diagnostics.ProcessStartInfo(agent) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(command);
        start.ArgumentList.Add(inPath);
        start.ArgumentList.Add(outPath);
        foreach (var (k, v) in environment ?? [])
        {
            start.Environment[k] = v;
        }

        using var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("could not start " + agent);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var text = File.Exists(outPath) ? await File.ReadAllTextAsync(outPath) : "{\"Ok\":false,\"Error\":\"no output: " + (await stderr).Replace("\"", "'", StringComparison.Ordinal).ReplaceLineEndings(" ") + "\"}";
        _ = await stdout;
        var output = JsonDocument.Parse(text).RootElement;
        Log($"agent {Path.GetFileName(Path.GetDirectoryName(agent))} {command} exit {process.ExitCode}: ok={output.GetProperty("Ok")} {(output.TryGetProperty("Error", out var e) ? e.ToString() : string.Empty)}");
        return output;
    }

    // ------------------------------------------------------------------------------------------------ CAP-F1 matrix, D = 2, isolation
    private async Task FloorMatrixChecksAsync()
    {
        // Verify of a bound record (schema-2 head) and of an unbound record (v1 head), with faithful bytes, for C x D.
        var bound = gOrg.Records[9];
        var batch = await BatchAsync(bound);
        var setup = await WriteAsync(AsNode(GInstance("G4-setup")), bound, Owner(bound, batch.Seal("matrix")));
        await VerifyArchivedContentStreamAsync(gOrg, PrimaryDatabase);
        var unbound = gUnbound.Records[0];
        var n = 40;
        foreach (var d in new[] { "1", "2" })
        {
            var node = GInstance("G4-D" + d, deploymentFloor: d);
            foreach (var (record, organizationBound) in new[] { (bound, true), (unbound, false) })
            {
                foreach (var build in new[] { BusinessBuild.Undeclared, BusinessBuild.DeclaresOne, BusinessBuild.Current })
                {
                    var head = await HeadIdentityAsync(record);
                    node.Trace.Reset();
                    var before = await OrgFingerprintAsync(record.Org);
                    var outcome = await CallAsync(node, new GCase(Entry.Verify, record, head.Operation, head.Revision), record.AcceptedStaged(), build);
                    var level = build.Declared?.Level ?? 1;
                    var floor = Math.Max(int.Parse(d, System.Globalization.CultureInfo.InvariantCulture), organizationBound ? 2 : 1);
                    var expectRefusal = level < floor;
                    var queried = node.Trace.LedgerStatements.Count;
                    var expectQuery = level < 2 && level >= int.Parse(d, System.Globalization.CultureInfo.InvariantCulture) ? 1 : 0;
                    var unchanged = before == await OrgFingerprintAsync(record.Org);
                    Record("G-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture), "Security", $"M2F-07/38 CAP-F1/F3 on real PostgreSQL: Verify with C = {(build.Declared is null ? "none" : build.Declared.Level)}, D = {d}, O = {(organizationBound ? 2 : 1)} ({(organizationBound ? "bound" : "unbound")} organization): {(expectRefusal ? "refused" : "admitted and verified")}; ledger existence queries {expectQuery} (short-circuit)",
                        $"{(expectRefusal ? Refused : "OK: verified True")}; existence queries {expectQuery}{(expectRefusal ? "; organization rows unchanged" : string.Empty)}",
                        $"{outcome}; existence queries {queried}; rows {(unchanged ? "unchanged" : "changed")}",
                        (expectRefusal ? outcome == Refused && unchanged : outcome.StartsWith("OK: verified True", StringComparison.Ordinal)) && queried == expectQuery);
                    n++;
                }
            }
        }

        Record("G-39", "Setup", "matrix setup: one more schema-2 head in the bound organization", "OK", setup, setup == "OK");
    }

    // ------------------------------------------------------------------------------------------------ CAP-F2: a binding in any operation state
    private async Task AnyStateBindingChecksAsync()
    {
        var n = 55;
        foreach (var state in new[] { "Prepared", "Aborted", "Expired" })
        {
            var org = await SeedOrgAsync(2, localKeys!, 1, journalDigest: true);
            var node = GInstance("G5-" + state);
            var gnode = AsNode(node);
            var plain = org.Records[0];
            var setup = await WriteAsync(gnode, plain, Owner(plain, Rows.V1Field(org.Id, plain.Id, Guid.CreateVersion7(), Guid.CreateVersion7(), 33)));
            await VerifyArchivedContentStreamAsync(org, PrimaryDatabase);
            var head = await HeadIdentityAsync(plain);
            var c = new GCase(Entry.Verify, plain, head.Operation, head.Revision);
            var beforeBinding = await CallAsync(node, c, plain.AcceptedStaged(), BusinessBuild.Undeclared);

            var target = org.Records[1];
            var prepared = await PrepareAsync(gnode, target, Owner(target, (await BatchAsync(target)).Seal("reservation-" + state)));
            var settled = state switch
            {
                "Aborted" => (await CommitAsync(Instance("G5-OFF", ReleaseB3, EnvelopeV2AcceptanceSwitch.Create(null, "Testing", ReleaseB3)), target, prepared)).Outcome,
                "Expired" => (await CommitAsync(Instance("G5-LATE", ReleaseB3, acceptance, clock: new OffsetClock(TimeSpan.FromMinutes(11))), target, prepared)).Outcome,
                _ => "left Prepared",
            };
            var operationState = await StateAsync(prepared.OperationId);
            var after = await CallAsync(node, c, plain.AcceptedStaged(), BusinessBuild.Undeclared);
            Record("G-" + n.ToString(System.Globalization.CultureInfo.InvariantCulture), "Security", $"M2F-08 CAP-F2 on real PostgreSQL: a binding whose operation is {state} (never committed) raises the organization floor: a level-1 Verify of an untouched v1 record, admitted before the reservation, is refused after it",
                $"before OK: verified True; operation {state}; after {Refused}; binding 1",
                $"before {beforeBinding}; setup {setup}; prepare {prepared.Outcome}; settle {settled}; operation {operationState}; after {after}; bindings {await BindingCountAsync($"organization_id = '{org.Id}'")}",
                setup == "OK" && beforeBinding.StartsWith("OK: verified True", StringComparison.Ordinal) && prepared.Outcome == "OK" && operationState.Contains(state == "Expired" ? "Aborted" : state, StringComparison.Ordinal)
                && after == Refused && await BindingCountAsync($"organization_id = '{org.Id}'") == 1);
            n++;
        }
    }

    // ------------------------------------------------------------------------------------------------ AM-F1 on real PostgreSQL
    private async Task LedgerQueryChecksAsync()
    {
        // The exact statement, its plan and the runtime role's privileges.
        var node = GInstance("G6-shape");
        var record = gUnbound.Records[1];
        var head = await HeadIdentityAsync(record);
        node.Trace.Reset();
        var admitted = await CallAsync(node, new GCase(Entry.Verify, record, head.Operation, head.Revision), record.AcceptedStaged(), BusinessBuild.Undeclared);
        var ledger = node.Trace.LedgerStatements;
        var statement = ledger.SingleOrDefault() ?? "none";
        Record("G-60", "Security", "AM-F1 #1 on real PostgreSQL: an admitted level-1 Verify in an unbound organization ran exactly ONE ledger statement: a parameterized boolean EXISTS keyed on the organization (no binding column selected), after the organization lock, and continued to verification",
            "OK: verified True; 1 ledger statement: SELECT EXISTS ... content_data_key_bindings ... organization_id = @p ... [params 1]; lock before it",
            $"{admitted}; {ledger.Count} ledger statement(s): {statement}; order [{node.Trace.Shape()}]",
            admitted.StartsWith("OK: verified True", StringComparison.Ordinal) && ledger.Count == 1 && statement.StartsWith("SELECT EXISTS", StringComparison.OrdinalIgnoreCase)
            && statement.Contains("organization_id = @", StringComparison.Ordinal) && statement.EndsWith("[params 1]", StringComparison.Ordinal)
            && !statement.Contains("data_key_identifier", StringComparison.Ordinal) && !statement.Contains("data_key_commitment", StringComparison.Ordinal)
            && node.Trace.Shape().IndexOf("org-lock", StringComparison.Ordinal) < node.Trace.Shape().IndexOf("ledger-exists", StringComparison.Ordinal));

        var explain = await ExplainAsync($"SELECT EXISTS (SELECT 1 FROM authority.content_data_key_bindings AS c WHERE c.organization_id = '{gOrg.Id}')", forceIndex: false);
        var explainIndex = await ExplainAsync($"SELECT EXISTS (SELECT 1 FROM authority.content_data_key_bindings AS c WHERE c.organization_id = '{gOrg.Id}')", forceIndex: true);
        Log("DIAGNOSTIC existence plan (default): " + explain);
        Log("DIAGNOSTIC existence plan (seqscan off): " + explainIndex);
        Record("G-61", "Security", "AM-F1 #5 index: with sequential scans disabled the planner answers the existence query from ix_content_data_key_bindings_record (organization_id leading); the default plan is recorded as a diagnostic (a small table may be scanned)",
            "an index scan on ix_content_data_key_bindings_record", $"seqscan off: {explainIndex} | default: {explain}", explainIndex.Contains("ix_content_data_key_bindings_record", StringComparison.Ordinal));

        var privileges = await ScalarAsync(adminCs, "SELECT has_table_privilege('authority_runtime','authority.content_data_key_bindings','SELECT')||','||has_table_privilege('authority_runtime','authority.content_data_key_bindings','INSERT')||','||has_table_privilege('authority_runtime','authority.content_data_key_bindings','UPDATE')||','||has_table_privilege('authority_runtime','authority.content_data_key_bindings','DELETE')||','||has_table_privilege('authority_runtime','authority.content_data_key_bindings','TRUNCATE')");
        Record("G-62", "Security", "AM-F1 #8: no grant change: the runtime role keeps exactly SELECT and INSERT on the ledger (closure L-8)", "true,true,false,false,false", privileges, privileges == "true,true,false,false,false");

        // CAP-F5: an unreadable ledger refuses a level-1 request (never admits) and does not affect level 2 (it never queries).
        {
            var expectedNode = GInstance("G6-unreadable", applicationName: "m2e2-expected-42501-capability");
            var c = new GCase(Entry.Verify, record, head.Operation, head.Revision);
            var revoke = await ExecAsync(adminCs, "REVOKE SELECT ON authority.content_data_key_bindings FROM authority_runtime");
            var level1 = await CallAsync(expectedNode, c, record.AcceptedStaged(), BusinessBuild.Undeclared);
            var level2 = await CallAsync(expectedNode, c, record.AcceptedStaged(), BusinessBuild.Current);
            var grant = await ExecAsync(adminCs, "GRANT SELECT ON authority.content_data_key_bindings TO authority_runtime");
            Record("G-63", "Security", "M2F-24 / CAP-F5 on real PostgreSQL: with the runtime role's SELECT on the ledger revoked (superuser, restored at once), a level-1 Verify in an unbound organization is REFUSED as not ready (42501 -> fail closed), never admitted; a level-2 Verify is unaffected (it never reads the ledger)",
                "revoke OK; level 1 AUTHORITY_NOT_READY 503; level 2 OK: verified True; grant OK", $"revoke {revoke}; level 1 {level1}; level 2 {level2}; grant {grant}",
                revoke == "OK" && level1.StartsWith("AuthorityException AUTHORITY_NOT_READY 503", StringComparison.Ordinal) && level2.StartsWith("OK: verified True", StringComparison.Ordinal) && grant == "OK");
        }
    }

    private async Task<string> ExplainAsync(string sql, bool forceIndex)
    {
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(adminCs) { Pooling = false }.ConnectionString);
        await connection.OpenAsync();
        if (forceIndex)
        {
            await using var off = new NpgsqlCommand("SET enable_seqscan = off", connection);
            await off.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand("EXPLAIN (COSTS OFF) " + sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var lines = new List<string>();
        while (await reader.ReadAsync())
        {
            lines.Add(reader.GetString(0).Trim());
        }

        return string.Join(" / ", lines);
    }
}
