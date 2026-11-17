// E3-M2e-2 controlled Envelope v2 acceptance verification (docs/security/E3_M2E_CONTRACT_CLOSURE.md §6.2; owner authorization of
// 2026-11-16, decisions D-1..D-12 of the readiness review). TEST-ONLY. Development/Testing only.
//
// Runs the REAL Authority code of this working tree (Release build; ContentAcceptanceRules.CurrentVersion = 3) in-process against
// disposable PostgreSQL 18.0 databases that this run created and verified (Isolation.cs), with Envelope v2 rows produced only by the
// real E3-M1 core and encoded by the business signed-column registry (Rows.cs). Envelope v2 acceptance is enabled ONLY on switch objects
// constructed here with the literal Testing configuration after floor 3 and Release B RequiredFloor 3 are verified, and only after
// the exact policy is recorded on the platform journal of the disposable database (in-memory archive, D-4). The ordinary --migrate runs
// before any runtime connection and never while runtime writers are active (F-E2 isolation). Frozen builds (a1127a1 CARV 2, 8915f77
// CARV 1) run as separate processes through the E3-M2e-1 LegacyAgent.
//
// Controlled test-only mechanisms (as in the E3-M2b-2, E3-M2d and E3-M2e-1 gates): forged Prepared operations and two synthetic v2 heads
// (C-14) are written through an ordinary admin session; catalog weakenings are made with the container superuser. They only ever touch
// this run's disposable databases. Expected security-gap demonstrations (BD-1 and the recovery limits) are recorded as
// EXPECTED_GAP / EXPECTED_LIMIT entries, never counted as passes.
using System.Diagnostics;
using System.Security.Cryptography;
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
using EnterpriseForms.Application.Features.Documents.Ports;
using EnterpriseForms.Integrity.Merkle;
using Microsoft.EntityFrameworkCore;
using Npgsql;

string Need(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : throw new InvalidOperationException(name);

var inputs = new Inputs(
    Need("M2E2_NEWBIN"), Need("M2E2_HOSTBIN"), Need("M2E2_CARV1AGENT"), Need("M2E2_CARV2AGENT"), Need("M2E2_ADMIN"), Need("M2E2_RUNTIME"), Need("M2E2_EVIDENCE"),
    int.Parse(Need("M2E2_PORT"), System.Globalization.CultureInfo.InvariantCulture), Need("M2E2_NONCE"), Need("M2E2_SYSID"),
    Need("M2E2_VAULT_ADDRESS"), Need("M2E2_VAULT_TOKEN"), int.Parse(Need("M2E2_VAULT_PORT"), System.Globalization.CultureInfo.InvariantCulture),
    [.. Need("M2E2_VAULT_ORGS").Split(',').Select(Guid.Parse)], Environment.GetEnvironmentVariable("M2E2_GROUPS") ?? "all",
    Environment.GetEnvironmentVariable("M2E2_DBMUT_APPLY"), Environment.GetEnvironmentVariable("M2E2_DBMUT_RESTORE"),
    Agents("M2F2G_BUSINESS_AGENTS"), Agents("M2F2G_AUTHORITY_AGENTS"), Environment.GetEnvironmentVariable("M2F2G_VAULT_AUTHORITY_TOKEN"),
    Environment.GetEnvironmentVariable("M2F2G_AGENT_WORK") is { Length: > 0 } agentWork ? agentWork : Path.Combine(Path.GetTempPath(), "m2f2g-agent-calls"));

// E3-M2f-2G: "label=path;label=path" (the BusinessAgent per business build and the AuthorityAgent per Authority build); empty when not built.
static IReadOnlyDictionary<string, string> Agents(string name) =>
    (Environment.GetEnvironmentVariable(name) ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p[1], StringComparer.Ordinal);
var removed = Target.ScrubProcessEnvironment();
return await new Lab(inputs, removed).RunAsync();

internal sealed record Inputs(string NewBinary, string HostBinaries, string Carv1Agent, string Carv2Agent, string AdminCs, string RuntimeCs, string EvidenceDir, int Port, string Nonce, string SystemIdentifier,
    string VaultAddress, string VaultToken, int VaultPort, IReadOnlyList<Guid> VaultOrganizations, string Groups, string? DatabaseMutationApply, string? DatabaseMutationRestore,
    IReadOnlyDictionary<string, string> BusinessAgents, IReadOnlyDictionary<string, string> AuthorityAgents, string? VaultAuthorityToken, string AgentWorkDir);

internal sealed partial class Lab(Inputs inputs, IReadOnlyList<string> scrubbed)
{
    private const string Issuer = "https://m2e2-harness.invalid/";
    private const string PrimaryDatabase = "m2e2gate";
    private static readonly ContentAcceptancePolicy ReleaseA = ContentAcceptancePolicy.Resolve(ContentAcceptancePolicy.ReleaseAName, null);
    private static readonly ContentAcceptancePolicy ReleaseB2 = ContentAcceptancePolicy.Resolve(ContentAcceptancePolicy.ReleaseBName, "2");
    private static readonly ContentAcceptancePolicy ReleaseB3 = ContentAcceptancePolicy.Resolve(ContentAcceptancePolicy.ReleaseBName, "3");

    private readonly List<Check> checks = [];
    private readonly List<string> log = [];
    private readonly TestMac mac = new();
    private readonly CapturingAlerts alerts = new();
    private readonly List<IDisposable> batches = [];
    private readonly byte[] localKek = RandomNumberGenerator.GetBytes(32);
    private readonly InMemorySecurityEventArchiveHolder archive = new();
    private readonly HashSet<string> groups = new(StringComparer.Ordinal);
    private string adminCs = string.Empty;
    private string runtimeCs = string.Empty;
    private IEncryptionKeyProvider? localKeys;
    private IEncryptionKeyProvider? vaultKeys;

    private bool Run(string group) => groups.Contains("all") || groups.Contains(group);

    public async Task<int> RunAsync()
    {
        foreach (var g in inputs.Groups.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            groups.Add(g);
        }

        var stopped = false;
        try
        {
            Target.Configure(inputs.Port, inputs.Nonce, inputs.SystemIdentifier, inputs.VaultPort, PrimaryDatabase);
            await Target.AdmitAsync(inputs.AdminCs, PrimaryDatabase);
            adminCs = Target.Require(new NpgsqlConnectionStringBuilder(inputs.AdminCs) { Database = PrimaryDatabase, Pooling = false }.ConnectionString);
            runtimeCs = Target.Require(new NpgsqlConnectionStringBuilder(inputs.RuntimeCs) { Database = PrimaryDatabase }.ConnectionString);
            localKeys = Rows.LocalKeys(localKek);
            vaultKeys = Rows.VaultKeys(inputs.VaultAddress, inputs.VaultToken);
            await ScenariosAsync();
        }
        catch (IsolationViolation ex)
        {
            stopped = true;
            Log("STOP: " + ex.Message);
            checks.Add(new Check("STOP", "Isolation", "isolation", "every connection stays inside the verified disposable environment", "no isolation violation", ex.Message, false, null));
        }
        catch (Exception ex)
        {
            Log("HARNESS ABORTED: " + Describe(ex) + " " + ex);
            checks.Add(new Check("HARNESS", "Harness", "harness", "harness completed", "no unexpected exception", Describe(ex), false, null));
        }
        finally
        {
            foreach (var b in batches)
            {
                b.Dispose();
            }

            NpgsqlConnection.ClearAllPools();
        }

        var counted = checks.Where(c => c.Gap is null).ToList();
        var failed = counted.Count(c => !c.Pass);
        var byKind = counted.GroupBy(c => c.Kind).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => new { total = g.Count(), passed = g.Count(c => c.Pass), failed = g.Count(c => !c.Pass) });
        var gaps = checks.Where(c => c.Gap is not null).ToList();
        // E3-M2f-2G: the E3-M2e-2 regression groups and the M2f-2G groups are reported separately (never merged into one security total).
        static bool IsM2f2g(string group) => group.StartsWith('G') && !group.StartsWith("G-", StringComparison.Ordinal);
        var byGroup = counted.GroupBy(c => c.Group).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => new { total = g.Count(), passed = g.Count(c => c.Pass), failed = g.Count(c => !c.Pass) });
        var m2e2 = counted.Where(c => !IsM2f2g(c.Group)).ToList();
        var m2f2g = counted.Where(c => IsM2f2g(c.Group)).ToList();
        var json = JsonSerializer.Serialize(new
        {
            harness = "E3-M2e-2 controlled Envelope v2 acceptance verification (real Authority build in-process; real E3-M1 core; business signed-column registry; disposable PostgreSQL 18.0 and Vault 1.20.4)",
            build = "ContentAcceptanceRules.CurrentVersion = " + ContentAcceptanceRules.CurrentVersion + "; RecognizedFloors = " + string.Join(",", ContentAcceptanceRules.RecognizedFloors)
                    + "; EnvelopeRules.AllowedEnvelopeVersions = " + string.Join(",", EnvelopeRules.AllowedEnvelopeVersions),
            groups = inputs.Groups,
            isolation = new { databases = Target.Databases.Order(StringComparer.Ordinal).ToList(), scrubbedEnvironmentVariables = scrubbed, stopped },
            postgres = stopped ? "not queried after a stop" : await SafeScalarAsync("SELECT version()"),
            checksTotal = counted.Count,
            checksFailed = failed,
            byKind,
            byGroup,
            m2e2Regression = new { total = m2e2.Count, failed = m2e2.Count(c => !c.Pass) },
            m2f2g = new
            {
                total = m2f2g.Count, failed = m2f2g.Count(c => !c.Pass),
                byKind = m2f2g.GroupBy(c => c.Kind).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => new { total = g.Count(), passed = g.Count(c => c.Pass), failed = g.Count(c => !c.Pass) }),
                gapDemonstrations = gaps.Where(c => IsM2f2g(c.Group)).GroupBy(c => c.Gap!).ToDictionary(g => g.Key, g => new { total = g.Count(), asExpected = g.Count(c => c.Pass) }),
            },
            expectedGapDemonstrations = gaps.Count,
            checks = counted,
            gapDemonstrations = gaps,
        }, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(inputs.EvidenceDir, "m2e2-evidence.json"), json);
        await File.WriteAllLinesAsync(Path.Combine(inputs.EvidenceDir, "m2e2-log.txt"), log);
        Console.WriteLine($"checks: {counted.Count}, failed: {failed}, expected-gap demonstrations (not counted): {gaps.Count}");
        foreach (var c in checks)
        {
            Console.WriteLine($"{(c.Gap is not null ? c.Gap : c.Pass ? "PASS" : "FAIL")} {c.Id} [{c.Kind}]: {c.Description} | observed: {c.Observed}");
        }

        return stopped || failed > 0 ? 1 : 0;
    }

    private async Task<string> SafeScalarAsync(string sql)
    {
        try
        {
            return await ScalarAsync(adminCs, sql);
        }
        catch (Exception ex)
        {
            return "unavailable: " + Describe(ex);
        }
    }

    private async Task ScenariosAsync()
    {
        await SetupAsync();
        if (Run("F"))
        {
            await FloorScenariosAsync();
        }
        else
        {
            await QuietFloorsAsync();
        }

        await AcceptanceSetupAsync();
        string? mutationBaseline = null;
        if (inputs.DatabaseMutationApply is { Length: > 0 } apply)
        {
            // Enforcement-removal run (database mode): weaken one database control in THIS disposable database only; restored below.
            mutationBaseline = await DefinitionsDigestAsync() + "|" + await ScalarAsync(adminCs, GrantsQuery);
            var applied = await ExecAsync(adminCs, apply);
            Log("DATABASE MUTATION applied: " + applied + " :: " + apply);
            if (applied != "OK")
            {
                throw new InvalidOperationException("database mutation could not be applied: " + applied);
            }
        }

        await LedgerSqlScenariosAsync();
        if (Run("P"))
        {
            await PositiveScenariosAsync();
        }

        if (Run("N"))
        {
            await NegativeScenariosAsync();
        }

        if (Run("C"))
        {
            await ConcurrencyScenariosAsync();
        }

        if (Run("X"))
        {
            await CrossBuildScenariosAsync();
        }

        if (Run("D"))
        {
            await DatabaseEnforcementScenariosAsync();
        }

        if (Run("R"))
        {
            await RecoveryScenariosAsync();
        }

        // E3-M2f-2G (docs/security/E3_M2F2G_VERIFICATION_RECORD.md): the capability floor on real PostgreSQL.
        if (Run("G"))
        {
            await CapabilityScenariosAsync();
        }

        if (Run("GS"))
        {
            await CapabilityShortCircuitScenariosAsync();
        }

        if (Run("GC"))
        {
            await CapabilityConcurrencyScenariosAsync();
        }

        if (Run("GR"))
        {
            await CapabilityRecoveryScenariosAsync();
        }

        if (Run("GH"))
        {
            await CapabilityHostScenariosAsync();
        }

        if (Run("H"))
        {
            await HostScenariosAsync();
        }

        if (mutationBaseline is not null)
        {
            CurrentGroup = "mutation";
            var restored = await ExecAsync(adminCs, inputs.DatabaseMutationRestore ?? string.Empty);
            var now = await DefinitionsDigestAsync() + "|" + await ScalarAsync(adminCs, GrantsQuery);
            Record("M-RESTORE", "Setup", "the database mutation is restored and the ledger/floor catalog digest and runtime grants equal their pre-mutation values", "restore OK; digest and grants equal",
                $"restore {restored}; {(now == mutationBaseline ? "equal" : "DIFFER")}", restored == "OK" && now == mutationBaseline);
            grantsBaseline = await ScalarAsync(adminCs, GrantsQuery);
        }

        await WriteDiagnosticsAsync();
        await TeardownChecksAsync();
    }

    // ------------------------------------------------------------------------------------------------ model
    internal sealed record Check(string Id, string Kind, string Group, string Description, string Expected, string Observed, bool Pass, string? Gap)
    {
        public string Result => Gap ?? (Pass ? "PASS" : "FAIL");
    }

    internal sealed class Org(Guid id, Guid owner, Guid ownerBinding, IEncryptionKeyProvider keys, int wrapCode)
    {
        public Guid Id { get; } = id;

        public Guid Owner { get; } = owner;

        public Guid OwnerBinding { get; } = ownerBinding;

        public IEncryptionKeyProvider Keys { get; } = keys;

        public int WrapCode { get; } = wrapCode;

        public List<Rec> Records { get; } = [];

        public Guid Editor { get; set; }
    }

    internal sealed class Rec(Org org, Guid id)
    {
        public Org Org { get; } = org;

        public Guid Id { get; } = id;

        public Guid TextField { get; } = Guid.CreateVersion7();

        public Guid TextItem { get; } = Guid.CreateVersion7();

        /// <summary>The sources of the current accepted head (header and text first), and their staged bytes and signatures.</summary>
        public List<object> Sources { get; set; } = [];

        public List<(Staged Row, byte[] Signature)> Accepted { get; set; } = [];

        private int writes;

        public int NextWrite() => Interlocked.Increment(ref writes);

        public List<StagedContentItem> AcceptedStaged() => [.. Accepted.Select(a => new StagedContentItem(a.Row.Kind, a.Row.ItemId, a.Row.Base64, Convert.ToBase64String(a.Signature)))];

        /// <summary>The current sources other than the header and the text value (the encrypted items).</summary>
        public List<object> Items => [.. Sources.Where(s => s is not HeaderSource && s is not EnterpriseForms.Domain.Features.FormRecords.FieldStringValue)];
    }

    internal sealed record Node(string Name, ContentService Service, EnvelopeV2AcceptanceSwitch Switch, ArmedFaults Faults);

    internal sealed record Prepared(PreparedContentOperation? Operation, Guid OperationId, PrepareContentChangeRequest Request, string Key, List<Staged> Rows, List<object> Sources, string Outcome, string Message);

    // ------------------------------------------------------------------------------------------------ composition
    private Node Instance(string name, ContentAcceptancePolicy policy, EnvelopeV2AcceptanceSwitch acceptance, string? database = null, Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor? interceptor = null,
        TimeProvider? clock = null, bool contributorWrites = false)
    {
        var cs = ContentAcceptanceRuleConnection.WithVersionedApplicationName(new NpgsqlConnectionStringBuilder(runtimeCs) { ApplicationName = "m2e2-runtime-" + name, Database = database ?? PrimaryDatabase }.ConnectionString)!;
        var store = new AuthorityStore(Factory.For(cs, interceptor), policy, null, null, alerts);
        var time = clock ?? TimeProvider.System;
        var verifier = new ProjectionIntegrityVerifier(store, ThrowingProxy.Create<ISpiceDbProjection>(), new ProjectionMarkerTransitionState(), new NoAlerts(), new ProjectionIntegrityMetrics(), time);
        var faults = new ArmedFaults();
        return new Node(name, new ContentService(store, mac, verifier, faults, time, new ShareContributorWriteSwitch(contributorWrites, false), envelopeV2Acceptance: acceptance), acceptance, faults);
    }

    private AuthorityStore Store(string name, ContentAcceptancePolicy policy, string? database = null) =>
        new(Factory.For(ContentAcceptanceRuleConnection.WithVersionedApplicationName(new NpgsqlConnectionStringBuilder(runtimeCs) { ApplicationName = "m2e2-runtime-" + name, Database = database ?? PrimaryDatabase }.ConnectionString)!), policy, null, null, alerts);

    private ContentAcceptanceRuleGuard Guard(ContentAcceptancePolicy policy, string? database = null)
    {
        var cs = ContentAcceptanceRuleConnection.WithVersionedApplicationName(new NpgsqlConnectionStringBuilder(runtimeCs) { ApplicationName = "m2e2-guard", Database = database ?? PrimaryDatabase }.ConnectionString)!;
        return new ContentAcceptanceRuleGuard(new ContentAcceptanceRuleSchemaReader(Factory.For(cs)), TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<ContentAcceptanceRuleGuard>.Instance, policy);
    }

    /// <summary>Records the switch's exact policy on the platform journal of <paramref name="database"/> (the observer, in-memory archive).</summary>
    private async Task<string> ObserveAsync(EnvelopeV2AcceptanceSwitch acceptance, string? database = null)
    {
        var store = Store("observer", ReleaseB3, database);
        var journal = new AuthorizationAuthority.Application.Features.PlatformOperators.PlatformJournal(store, archive.For(database ?? PrimaryDatabase), alerts, TimeProvider.System);
        return await new EnvelopeAcceptancePolicyObserver(store, journal, acceptance, TimeProvider.System).RecordAsync(Guid.CreateVersion7(), CancellationToken.None);
    }

    // ------------------------------------------------------------------------------------------------ organizations and records
    /// <param name="journalDigest">E3-M2f-2G: give the state the journal digest every projected revision has in production (the
    /// projection pin of the user Verify paths refuses a state without one). The E3-M2e-2 scenarios never pin and seed without it.</param>
    private async Task<Org> SeedOrgAsync(int records, IEncryptionKeyProvider keys, int wrapCode, Guid? organizationId = null, string? database = null, bool journalDigest = false)
    {
        var owner = Guid.CreateVersion7();
        var binding = Guid.CreateVersion7();
        var org = new Org(organizationId ?? Guid.CreateVersion7(), owner, binding, keys, wrapCode);
        var keyName = "m2d-key-" + org.Id.ToString("N");
        var seedCs = Target.Require(new NpgsqlConnectionStringBuilder(adminCs) { Options = "-c session_replication_role=replica", ApplicationName = "m2e2-seed", Database = database ?? PrimaryDatabase }.ConnectionString);
        await using var context = new AuthorityDbContext(new DbContextOptionsBuilder<AuthorityDbContext>().UseNpgsql(seedCs).Options);
        var now = DateTimeOffset.UtcNow;
        context.OrganizationStates.Add(new OrganizationAuthorizationState
        {
            OrganizationAuthorizationStateId = Guid.CreateVersion7(), OrganizationId = org.Id, OrganizationDisplayName = "M2e2 gate", AuthorizationRevision = 7, ProjectionAppliedRevision = 7, ProjectionZedToken = "zed-7",
            CheckpointEnforcementModeName = CheckpointEnforcementModes.Asynchronous, StateModifiedOn = now, LatestSecurityJournalDigest = journalDigest ? SHA256.HashData(org.Id.ToByteArray()) : null,
        });
        context.IdentityBindings.Add(new IdentityBinding { IdentityBindingId = binding, OrganizationId = org.Id, AuthorityUserId = owner, IdentityIssuerUri = Issuer, IdentitySubjectIdentifier = owner.ToString("D"), IdentityBindingEnabled = true, IdentityCreatedOn = now });
        context.KeyRegistrations.Add(new IntegrityKeyRegistration { IntegrityKeyRegistrationId = Guid.CreateVersion7(), OrganizationId = org.Id, ProviderName = "LocalDevelopment", ProviderKeyName = keyName, CurrentSigningKeyVersion = 1, MinimumVerificationKeyVersion = 1, KeyRegisteredOn = now, KeyModifiedOn = now });
        for (var i = 0; i < records; i++)
        {
            var record = new Rec(org, Guid.CreateVersion7());
            org.Records.Add(record);
            context.Resources.Add(new AuthorityResource { AuthorityResourceId = Guid.CreateVersion7(), OrganizationId = org.Id, ResourceTypeName = ResourceTypeNames.FormRecord, ResourceEntityId = record.Id, ParentResourceEntityId = Rows.Form, RegisteredBySecurityCommandId = Guid.CreateVersion7(), ResourceRegisteredOn = now });
            context.ContentHeads.Add(new RecordContentHead { RecordContentHeadId = Guid.CreateVersion7(), OrganizationId = org.Id, FormRecordId = record.Id, CommittedManifestRoot = MerkleManifestV1.EmptyRoot(), SignatureKeyVersion = 1, ContentHeadModifiedOn = now });
            context.RelationshipEdges.Add(new AuthorityRelationshipEdge { AuthorityRelationshipEdgeId = Guid.CreateVersion7(), OrganizationId = org.Id, ResourceTypeName = ResourceTypeNames.FormRecord, ResourceEntityId = record.Id, RelationshipName = "owner", SubjectTypeName = ResourceTypeNames.User, SubjectEntityId = owner, SubjectRelationName = string.Empty, CreatedBySecurityCommandId = Guid.CreateVersion7(), RelationshipCreatedOn = now });
        }

        await context.SaveChangesAsync();
        return org;
    }

    /// <summary>A contributor share of <paramref name="items"/> of <paramref name="record"/> to a second user of the organization (Phase C5 graph edges).</summary>
    private async Task SeedShareAsync(Rec record, params Guid[] items)
    {
        var org = record.Org;
        var share = Guid.CreateVersion7();
        var seedCs = Target.Require(new NpgsqlConnectionStringBuilder(adminCs) { Options = "-c session_replication_role=replica", ApplicationName = "m2e2-seed-share" }.ConnectionString);
        await using var context = new AuthorityDbContext(new DbContextOptionsBuilder<AuthorityDbContext>().UseNpgsql(seedCs).Options);
        var now = DateTimeOffset.UtcNow;
        if (org.Editor == Guid.Empty)
        {
            org.Editor = Guid.CreateVersion7();
            context.IdentityBindings.Add(new IdentityBinding { IdentityBindingId = Guid.CreateVersion7(), OrganizationId = org.Id, AuthorityUserId = org.Editor, IdentityIssuerUri = Issuer, IdentitySubjectIdentifier = org.Editor.ToString("D"), IdentityBindingEnabled = true, IdentityCreatedOn = now });
        }

        AuthorityRelationshipEdge Edge(string type, Guid resource, string relation, string subjectType, Guid subject) => new()
        {
            AuthorityRelationshipEdgeId = Guid.CreateVersion7(), OrganizationId = org.Id, ResourceTypeName = type, ResourceEntityId = resource, RelationshipName = relation, SubjectTypeName = subjectType,
            SubjectEntityId = subject, SubjectRelationName = string.Empty, CreatedBySecurityCommandId = Guid.CreateVersion7(), RelationshipCreatedOn = now,
        };
        context.RelationshipEdges.Add(Edge(ResourceTypeNames.FormRecord, record.Id, "shared_via", ResourceTypeNames.RecordShare, share));
        context.RelationshipEdges.Add(Edge(ResourceTypeNames.RecordShare, share, "contributor", ResourceTypeNames.User, org.Editor));
        foreach (var item in items)
        {
            context.RelationshipEdges.Add(Edge(ResourceTypeNames.FieldItem, item, "shared_via", ResourceTypeNames.RecordShare, share));
        }

        await context.SaveChangesAsync();
    }

    private static VerifiedActor Actor(Org org) => new(Issuer, org.Owner.ToString("D"), "m2e2-harness");

    private static VerifiedActor EditorActor(Org org) => new(Issuer, org.Editor.ToString("D"), "m2e2-harness-editor");

    // ------------------------------------------------------------------------------------------------ candidate construction
    private static object HeaderOf(Rec r) => new HeaderSource(r.Org.Id, r.Id);

    private static object TextOf(Rec r, string value) => Rows.Text(r.Org.Id, r.Id, r.TextField, r.TextItem, value);

    /// <summary>Header + a changed text value + <paramref name="items"/> (owner write).</summary>
    private static List<object> Owner(Rec r, params object[] items) => [HeaderOf(r), TextOf(r, "value-" + r.NextWrite()), .. items];

    /// <summary>The current head's sources, unchanged, with <paramref name="replace"/> applied (share-editor write: header and text carried).</summary>
    private static List<object> Carry(Rec r, Func<object, object?>? replace = null)
    {
        var result = new List<object>();
        foreach (var s in r.Sources)
        {
            var n = replace is null ? s : replace(s);
            if (n is not null)
            {
                result.Add(n);
            }
        }

        return result;
    }

    private async Task<FieldSealBatchHandle> BatchAsync(Rec r)
    {
        var batch = await M1Core.Batch.CreateAsync(r.Org.Keys, r.Org.Id, r.Id, EncryptionKeyPurpose.FieldValue);
        batches.Add(batch);
        return new FieldSealBatchHandle(batch);
    }

    internal sealed record FieldSealBatchHandle(M1Core.Batch Batch)
    {
        public EnterpriseForms.Domain.Features.FormRecords.FieldEncryptedValue Seal(Guid field, Guid item, string plaintext, string logicalType = "String") => Rows.V2Field(Batch, field, item, logicalType, plaintext);

        public EnterpriseForms.Domain.Features.FormRecords.FieldEncryptedValue Seal(string plaintext) => Seal(Guid.CreateVersion7(), Guid.CreateVersion7(), plaintext);
    }

    private Task<EnterpriseForms.Domain.Features.Documents.FileAttachmentVersion> DocumentAsync(Rec r, int size = 5000, Guid? attachment = null, int version = 1) =>
        Rows.V2DocumentAsync(r.Org.Keys, r.Org.Id, r.Id, Guid.CreateVersion7(), attachment ?? Guid.CreateVersion7(), version, size);

    // ------------------------------------------------------------------------------------------------ protocol drivers
    private async Task<(long Revision, byte[] Root)> HeadAsync(Rec record, string? database = null)
    {
        await using var connection = new NpgsqlConnection(Target.Require(new NpgsqlConnectionStringBuilder(adminCs) { Database = database ?? PrimaryDatabase }.ConnectionString));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT committed_content_revision, committed_manifest_root FROM authority.record_content_heads WHERE organization_id = @o AND form_record_id = @r", connection);
        command.Parameters.AddWithValue("o", record.Org.Id);
        command.Parameters.AddWithValue("r", record.Id);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt64(0), (byte[])reader[1]);
    }

    private async Task<(PrepareContentChangeRequest Request, List<Staged> Rows, Guid Operation)> BuildAsync(Rec record, List<object> sources, string? database = null)
    {
        var operation = Guid.CreateVersion7();
        var head = await HeadAsync(record, database);
        var rows = sources.Select(s => Rows.Encode(s, operation, head.Revision + 1)).ToList();
        var request = new PrepareContentChangeRequest(record.Org.Id, record.Id, operation, 7, head.Revision, Convert.ToBase64String(head.Root),
            [.. rows.Select(c => new CandidateContentItem(c.Kind, c.ItemId, c.Base64))], CurrentItems: record.Accepted.Count == 0 ? null : record.AcceptedStaged());
        return (request, rows, operation);
    }

    private async Task<Prepared> PrepareAsync(Node node, Rec record, List<object> sources, VerifiedActor? actor = null, string? database = null, BusinessBuild? build = null)
    {
        var (request, rows, operation) = await BuildAsync(record, sources, database);
        var key = "m2e2-" + operation.ToString("N");
        try
        {
            var prepared = await node.Service.PrepareAsync(actor ?? Actor(record.Org), request, key, Guid.CreateVersion7(), CancellationToken.None, (build ?? BusinessBuild.Current).Declared);
            return new Prepared(prepared, prepared.OperationId, request, key, rows, sources, "OK", string.Empty);
        }
        catch (Exception ex)
        {
            return new Prepared(null, operation, request, key, rows, sources, Describe(ex), ex.Message);
        }
    }

    private static List<StagedContentItem> StagedOf(PreparedContentOperation prepared, List<Staged> rows)
    {
        var signatures = prepared.SignedItems.ToDictionary(s => (s.ItemKind, s.ItemId), s => s.RowSignatureBase64);
        return [.. rows.Select(c => new StagedContentItem(c.Kind, c.ItemId, c.Base64, signatures[(c.Kind, c.ItemId)]))];
    }

    private async Task<(string Outcome, string Message)> CommitAsync(Node node, Rec record, Prepared prepared, VerifiedActor? actor = null, List<StagedContentItem>? stagedOverride = null, BusinessBuild? build = null)
    {
        var staged = stagedOverride ?? StagedOf(prepared.Operation!, prepared.Rows);
        try
        {
            await node.Service.CommitAsync(actor ?? Actor(record.Org), prepared.OperationId, new CommitContentChangeRequest(record.Org.Id, record.Id, staged), Guid.CreateVersion7(), CancellationToken.None, (build ?? BusinessBuild.Current).Declared);
            if (stagedOverride is null)
            {
                record.Accepted = [.. prepared.Rows.Select(c => (c, Convert.FromBase64String(staged.Single(s => s.ItemKind == c.Kind && s.ItemId == c.ItemId).RowSignatureBase64)))];
                record.Sources = prepared.Sources;
            }

            return ("OK", string.Empty);
        }
        catch (Exception ex)
        {
            return (Describe(ex), ex.Message);
        }
    }

    /// <summary>Prepare then Commit; returns "OK" or the first refusal (Prepare refusals are prefixed "prepare:").</summary>
    private async Task<string> WriteAsync(Node node, Rec record, List<object> sources, VerifiedActor? actor = null, BusinessBuild? build = null)
    {
        var prepared = await PrepareAsync(node, record, sources, actor, build: build);
        if (prepared.Outcome != "OK")
        {
            return "prepare: " + prepared.Outcome + " | " + prepared.Message;
        }

        var (outcome, message) = await CommitAsync(node, record, prepared, actor, build: build);
        return outcome == "OK" ? "OK" : "commit: " + outcome + " | " + message;
    }

    // ------------------------------------------------------------------------------------------------ the frozen builds (separate processes)
    private async Task<(string Outcome, Guid Operation, List<StagedContentItem>? Staged, List<Staged> Rows, List<object> Sources)> LegacyPrepareAsync(string agent, Rec record, List<object> sources)
    {
        var (request, rows, operation) = await BuildAsync(record, sources);
        var key = "m2e2-legacy-" + operation.ToString("N");
        var output = await RunLegacyAsync(agent, "prepare", new { Issuer, Subject = record.Org.Owner.ToString("D"), Request = request, Key = key });
        if (!output.GetProperty("Ok").GetBoolean())
        {
            return (LegacyFailure(output), operation, null, rows, sources);
        }

        var prepared = output.GetProperty("Result").Deserialize<PreparedContentOperation>()!;
        return ("OK", prepared.OperationId, StagedOf(prepared, rows), rows, sources);
    }

    private async Task<string> LegacyCommitAsync(string agent, Rec record, Guid operation, List<StagedContentItem> staged, List<Staged>? rows = null, List<object>? sources = null)
    {
        var output = await RunLegacyAsync(agent, "commit", new { Issuer, Subject = record.Org.Owner.ToString("D"), OperationId = operation, Request = new CommitContentChangeRequest(record.Org.Id, record.Id, staged) });
        var outcome = output.GetProperty("Ok").GetBoolean() ? "OK" : LegacyFailure(output);
        if (outcome == "OK" && rows is not null && sources is not null)
        {
            record.Accepted = [.. rows.Select(c => (c, Convert.FromBase64String(staged.Single(s => s.ItemKind == c.Kind && s.ItemId == c.ItemId).RowSignatureBase64)))];
            record.Sources = sources;
        }

        return outcome;
    }

    private static string LegacyFailure(JsonElement output)
    {
        var raised = output.TryGetProperty("Alerts", out var a) ? string.Join(" | ", a.EnumerateArray().Select(x => x.GetString())) : string.Empty;
        return (output.GetProperty("Error").GetString() ?? "error") + " :: " + (output.TryGetProperty("Message", out var m) ? m.GetString() : string.Empty) + (raised.Length == 0 ? string.Empty : " ## alerts: " + raised);
    }

    private async Task<JsonElement> RunLegacyAsync(string agent, string command, object input)
    {
        var dir = Path.Combine(inputs.EvidenceDir, "legacy-calls");
        Directory.CreateDirectory(dir);
        var name = $"{DateTime.UtcNow:HHmmssfff}-{command}-{Guid.CreateVersion7():N}";
        var inPath = Path.Combine(dir, name + ".in.json");
        var outPath = Path.Combine(dir, name + ".out.json");
        await File.WriteAllTextAsync(inPath, JsonSerializer.Serialize(input));
        var start = new ProcessStartInfo(agent) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(command);
        start.ArgumentList.Add(inPath);
        start.ArgumentList.Add(outPath);
        start.Environment["M2E_LEGACY_RUNTIME"] = Target.Require(runtimeCs);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("could not start the legacy agent");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = JsonDocument.Parse(await File.ReadAllTextAsync(outPath)).RootElement;
        Log($"legacy {Path.GetFileName(Path.GetDirectoryName(agent))} {command} exit {process.ExitCode}: ok={output.GetProperty("Ok")} error={(output.TryGetProperty("Error", out var e) ? e.ToString() : "")} build={(output.TryGetProperty("BuildRuleVersion", out var b) ? b.ToString() : "")} {(await stderr).Trim()}");
        _ = await stdout;
        return output;
    }

    /// <summary>The ordinary migration, a floor command or another host command of this build (migration identity, Production environment, no settings file).</summary>
    private async Task<(int Code, string Output)> HostCommandAsync(string migrationCs, string application, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(inputs.NewBinary);
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment["ConnectionStrings__AuthorityDbMigration"] = Target.Require(new NpgsqlConnectionStringBuilder(migrationCs) { ApplicationName = application }.ConnectionString);
        start.Environment["ContentAcceptance__EnforcementPhase"] = "ReleaseA";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("could not start " + string.Join(' ', arguments));
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = ((await stdout) + " " + (await stderr)).Replace('\n', ' ').Replace('\r', ' ').Trim();
        Log($"{string.Join(' ', arguments)} ({application}) exit {process.ExitCode}: {(output.Length > 600 ? output[^600..] : output)}");
        return (process.ExitCode, output);
    }

    // ------------------------------------------------------------------------------------------------ evidence helpers
    private string AdminFor(string database) => Target.Require(new NpgsqlConnectionStringBuilder(adminCs) { Database = database }.ConnectionString);

    private async Task<string> ScalarAsync(string cs, string sql)
    {
        await using var connection = new NpgsqlConnection(Target.Require(cs));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) ?? "NULL";
    }

    private async Task<long> CountAsync(string sql, string? database = null) => long.Parse(await ScalarAsync(database is null ? adminCs : AdminFor(database), sql), System.Globalization.CultureInfo.InvariantCulture);

    private async Task<string> ExecAsync(string cs, string sql)
    {
        try
        {
            await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(Target.Require(cs)) { Pooling = false }.ConnectionString);
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

    private async Task ExecCheckedAsync(string cs, string sql)
    {
        var outcome = await ExecAsync(cs, sql);
        if (outcome != "OK")
        {
            throw new InvalidOperationException("setup statement failed: " + outcome);
        }
    }

    private async Task<List<string[]>> RowsAsync(string cs, string sql)
    {
        await using var connection = new NpgsqlConnection(Target.Require(cs));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string[]>();
        while (await reader.ReadAsync())
        {
            var row = new string[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                row[i] = reader.IsDBNull(i) ? "NULL" : Convert.ToString(reader.GetValue(i) is byte[] b ? Convert.ToHexStringLower(b) : reader.GetValue(i) is DateTime dt ? dt.ToString("O", System.Globalization.CultureInfo.InvariantCulture) : reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture) ?? "NULL";
            }

            rows.Add(row);
        }

        return rows;
    }

    private async Task<string> SnapshotAsync(Rec record, string? database = null) => await ScalarAsync(database is null ? adminCs : AdminFor(database), $"""
        SELECT concat_ws('; ',
          'head='||(SELECT committed_content_revision||':'||encode(committed_manifest_root,'hex')||':'||coalesce(committed_operation_id::text,'none')||':'||content_head_quarantined FROM authority.record_content_heads WHERE form_record_id='{record.Id}'),
          'commits='||(SELECT count(*) FROM authority.record_manifest_commits WHERE form_record_id='{record.Id}'),
          'identities='||(SELECT count(*) FROM authority.content_item_identities WHERE organization_id='{record.Org.Id}' AND form_record_id='{record.Id}'),
          'committedOps='||(SELECT count(*) FROM authority.content_change_operations WHERE form_record_id='{record.Id}' AND operation_state_name='Committed'))
        """);

    private static string Diff(string before, string after) => before == after ? "unchanged" : "CHANGED " + before + " -> " + after;

    private async Task<string> StateAsync(Guid operationId, string? database = null) => await ScalarAsync(database is null ? adminCs : AdminFor(database), $"SELECT coalesce(prepare_acceptance_rule_version::text,'NULL')||'/'||coalesce(commit_acceptance_rule_version::text,'NULL')||' '||operation_state_name||' '||coalesce(operation_quarantine_reason_code,'-') FROM authority.content_change_operations WHERE content_change_operation_id='{operationId}'");

    private Task<long> BindingCountAsync(string? where = null, string? database = null) => CountAsync("SELECT count(*) FROM authority.content_data_key_bindings" + (where is null ? string.Empty : " WHERE " + where), database);

    private Task<long> IncidentsAsync(Org org) => CountAsync($"SELECT count(*) FROM authority.security_journal_entries WHERE organization_id = '{org.Id}' AND journal_event_kind = 'IntegrityVerificationFailed'");

    private Task<long> IntegrityRevisionAsync(Org org) => CountAsync($"SELECT content_integrity_revision FROM authority.organization_authorization_states WHERE organization_id = '{org.Id}'");

    private Task<long> OpsAsync(string? database = null) => CountAsync("SELECT count(*) FROM authority.content_change_operations", database);

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

    private void Record(string id, string kind, string description, string expected, string observed, bool pass)
    {
        checks.Add(new Check(id, kind, CurrentGroup, description, expected, observed, pass, null));
        Log($"{(pass ? "PASS" : "FAIL")} {id}: {observed}");
    }

    /// <summary>An expected security-gap or limit demonstration: recorded with its label, never counted as a pass or a failure.</summary>
    private void Demonstrate(string id, string label, string description, string expected, string observed, bool asExpected)
    {
        checks.Add(new Check(id, "Recovery", CurrentGroup, description, expected, observed, asExpected, label));
        Log($"{label} {id} (as expected: {asExpected}): {observed}");
    }

    private string CurrentGroup { get; set; } = "setup";

    private void Log(string line)
    {
        lock (log)
        {
            log.Add(DateTimeOffset.UtcNow.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture) + " " + line);
        }
    }
}

/// <summary>One in-memory security-event archive per disposable database (D-4; the production InMemorySecurityEventArchive class).</summary>
internal sealed class InMemorySecurityEventArchiveHolder
{
    private readonly Dictionary<string, AuthorizationAuthority.Infrastructure.Archive.InMemorySecurityEventArchive> archives = new(StringComparer.Ordinal);

    public AuthorizationAuthority.Infrastructure.Archive.InMemorySecurityEventArchive For(string database)
    {
        lock (archives)
        {
            if (!archives.TryGetValue(database, out var archive))
            {
                archive = new AuthorizationAuthority.Infrastructure.Archive.InMemorySecurityEventArchive();
                archives[database] = archive;
            }

            return archive;
        }
    }
}
