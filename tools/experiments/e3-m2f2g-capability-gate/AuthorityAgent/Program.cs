// E3-M2f-2G expected-gap demonstrations: AUTHORITY AGENT (docs/security/E3_M2F2G_VERIFICATION_RECORD.md). TEST-ONLY.
// Development/Testing only. Never part of the solution; built only in a temporary work directory by
// scripts/dev/e3-m2e2-acceptance-pg-gate.ps1 against ONE Authority Release build (AuthorityBinaries):
//   - 0be5f52 (E3-M2e-2, CARV 3, the baseline WITHOUT E3-M2f-2 capability enforcement): EXPECTED_GAP_G2 and EXPECTED_GAP_INVENTORY;
//   - this tree, as a control: the SAME source, with no capability argument (the services' default, null = level 1), is refused.
// The source compiles unchanged against both because it never names the E3-M2f-2 parameter.
//
// One call per process:   AuthorityAgent <kind> <input.json> <output.json>
//   kind: prepare | commit | verify | batch-verify | census | backfill | deep-verify
// It composes that build's own ContentService, ProtectionReconciliationService and SearchIndexBuilderService over that build's EF Core
// AuthorityStore on the least-privilege runtime role (Release B, RequiredFloor 3), with:
//   - the deterministic TEST-ONLY HMAC shared with the harness (the MAC provider is not under test; the capability floor is);
//   - a SpiceDB double that reports the marker the Authority expects for the organization's CURRENT database state and allows every
//     permission check (SpiceDB is not under test: the capability check precedes every SpiceDB call, and the demonstrations need the
//     verification that follows it). No real SpiceDB is reachable from the disposable run.
// The connection string arrives in M2F2G_AGENT_RUNTIME and was admitted by the harness's isolation guard (Target.Require).
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuthorizationAuthority.Api.Composition;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Catalogue;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Application.Features.SearchIndex;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Keys;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Infrastructure.Persistence;
using AuthorizationAuthority.Infrastructure.Signing;
using Microsoft.EntityFrameworkCore;
using Npgsql;

var kind = args[0];
var input = JsonDocument.Parse(await File.ReadAllTextAsync(args[1])).RootElement;
var runtime = Environment.GetEnvironmentVariable("M2F2G_AGENT_RUNTIME") ?? throw new InvalidOperationException("M2F2G_AGENT_RUNTIME");
var builder = new NpgsqlConnectionStringBuilder(runtime) { ApplicationName = "m2f2g-authority-agent", Pooling = false };
if (builder.Host != "127.0.0.1")
{
    throw new InvalidOperationException("the agent only connects to the loopback disposable database");
}

var cs = ContentAcceptanceRuleConnection.WithVersionedApplicationName(builder.ConnectionString)!;
var options = new DbContextOptionsBuilder<AuthorityDbContext>().UseNpgsql(cs, n => n.MigrationsHistoryTable("ef_migrations_history", AuthorityDbContext.SchemaName)).Options;
var policy = ContentAcceptancePolicy.Resolve(ContentAcceptancePolicy.ReleaseBName, "3");
var alerts = new NoAlerts();
var store = new AuthorityStore(new Factory(options), policy, null, null, alerts);
var mac = new TestMac();
var spice = DispatchProxy.Create<ISpiceDbProjection, AllowingSpiceDb>();
((AllowingSpiceDb)(object)spice).Options = options;
var transition = new ProjectionMarkerTransitionState();
transition.MarkCompleted();
var verifier = new ProjectionIntegrityVerifier(store, spice, transition, alerts, new ProjectionIntegrityMetrics(), TimeProvider.System);
var sealOptions = new SearchSealOptions
{
    Provider = SearchSealProviderKind.LocalDevelopment,
    LocalPrivateKeyPkcs8Base64 = input.GetProperty("SealPkcs8").GetString(),
    PinnedKeys = [new PinnedSealKey { KeyName = "search-entry-seal-v1", KeyVersion = 1, SubjectPublicKeyInfoBase64 = input.GetProperty("SealSpki").GetString()! }],
};
var searchOptions = new SearchIndexOptions { Mode = SearchIndexMode.Dark };
var writer = new SearchIndexWriter(searchOptions, new SearchSealSigningGate(new LocalSearchSealSigner(sealOptions), new SearchSealVerifier(sealOptions)));
var content = new ContentService(store, mac, verifier, new NoFaults(), TimeProvider.System, new ShareContributorWriteSwitch(false, false), new ContentRevisionCoordinator(writer, TimeProvider.System));
var census = new ProtectionReconciliationService(store, content, TimeProvider.System);
var index = new SearchIndexBuilderService(store, content, writer, searchOptions, TimeProvider.System, alerts);
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
VerifiedActor Actor() => new(input.GetProperty("Issuer").GetString()!, input.GetProperty("Subject").GetString()!, "m2f2g-authority-agent");

object? result = null;
string? error = null;
string? message = null;
try
{
    var body = input.GetProperty("Request");
    result = kind switch
    {
        "prepare" => await content.PrepareAsync(Actor(), body.Deserialize<PrepareContentChangeRequest>(json)!, input.GetProperty("Key").GetString()!, Guid.CreateVersion7(), CancellationToken.None),
        "commit" => await content.CommitAsync(Actor(), input.GetProperty("OperationId").GetGuid(), body.Deserialize<CommitContentChangeRequest>(json)!, Guid.CreateVersion7(), CancellationToken.None),
        "verify" => await content.VerifyProjectionAsync(Actor(), body.Deserialize<VerifyRecordProjectionRequest>(json)!, Guid.CreateVersion7(), CancellationToken.None),
        "batch-verify" => await content.VerifyProjectionsAsync(Actor(), body.Deserialize<VerifyRecordProjectionsRequest>(json)!, Guid.CreateVersion7(), CancellationToken.None),
        "census" => await census.CensusAsync(input.GetProperty("Workload").GetString()!, input.GetProperty("SnapshotId").GetGuid(), body.Deserialize<ProtectionCensusRequest>(json)!, Guid.CreateVersion7(), CancellationToken.None),
        "backfill" => await index.BackfillAsync(input.GetProperty("Workload").GetString()!, body.Deserialize<SearchIndexBackfillRequest>(json)!, Guid.CreateVersion7(), CancellationToken.None),
        "deep-verify" => await index.DeepVerifyAsync(input.GetProperty("Workload").GetString()!, body.Deserialize<SearchIndexDeepVerifyRequest>(json)!, Guid.CreateVersion7(), CancellationToken.None),
        _ => throw new ArgumentOutOfRangeException(nameof(args), kind),
    };
}
catch (Exception ex)
{
    error = Describe(ex);
    message = ex.Message;
}

await File.WriteAllTextAsync(args[2], JsonSerializer.Serialize(new { Ok = error is null, Result = result, Error = error, Message = message, BuildRuleVersion = ContentAcceptanceRules.CurrentVersion, MacVerifications = mac.Verifications }));
return 0;

static string Describe(Exception ex)
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

internal sealed class Factory(DbContextOptions<AuthorityDbContext> options) : IDbContextFactory<AuthorityDbContext>
{
    public AuthorityDbContext CreateDbContext() => new(options);
}

internal sealed class NoAlerts : ISecurityAlertSink
{
    public void Raise(SecurityAlert alert)
    {
    }
}

internal sealed class NoFaults : IFaultInjector
{
    public void CrashIfArmed(string faultName)
    {
    }
}

/// <summary>The deterministic TEST-ONLY HMAC shared with the harness (same derivation as its TestMac).</summary>
internal sealed class TestMac : IIntegrityMacProvider
{
    private int verifications;

    public int Verifications => Volatile.Read(ref verifications);

    public string ProviderName => "LocalDevelopment";

    private static byte[] Key(IntegrityKeyReference keyReference) => SHA256.HashData(Encoding.UTF8.GetBytes($"m2d-gate-test-key:{keyReference.ProviderKeyName}:{keyReference.KeyVersion}"));

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

/// <summary>SpiceDB double: the marker the Authority expects for the organization's current database state; every check allowed.</summary>
public class AllowingSpiceDb : DispatchProxy
{
    public DbContextOptions<AuthorityDbContext> Options { get; set; } = null!;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        switch (targetMethod?.Name)
        {
            case nameof(ISpiceDbProjection.ReadMarkerAsync):
                using (var context = new AuthorityDbContext(Options))
                {
                    var state = context.OrganizationStates.AsNoTracking().Single(s => s.OrganizationId == (Guid)args![0]!);
                    return Task.FromResult(new MarkerObservation([ProjectionMarker.ExpectedFor(state)], "pin-" + state.ProjectionAppliedRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                }

            case nameof(ISpiceDbProjection.CheckAsync):
                return Task.FromResult(SpiceDbCheckOutcome.Allowed);
            case nameof(ISpiceDbProjection.CheckBulkAsync):
            case nameof(ISpiceDbProjection.CheckBulkStrictAsync):
                return Task.FromResult<IReadOnlyList<SpiceDbCheckOutcome>>([.. ((IReadOnlyList<SpiceDbCheck>)args![0]!).Select(_ => SpiceDbCheckOutcome.Allowed)]);
            default:
                throw new NotSupportedException("unexpected SpiceDB call " + targetMethod?.Name);
        }
    }
}
