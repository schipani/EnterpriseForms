// E3-M2e-1 data-key ledger real-PostgreSQL gate: LEGACY AGENT (built only in a temporary work directory; never part of the solution).
// The E3-M2d V1Agent, compiled twice by scripts/dev/e3-m2e1-ledger-pg-gate.ps1: against a Release build of 8915f77 (E3-M2c, CARV 1)
// and against a Release build of a1127a1 (E3-M2d, CARV 2), each passed as LegacyBinaries. One call per process:
//   LegacyAgent prepare|commit <input.json> <output.json>
// It drives that build's own ContentService and EF Core AuthorityStore (with that build's own versioned application name,
// "... carv-v1") on the least-privilege runtime role and reports the outcome plus that build's HTTP classification. The MAC port is
// the deterministic TEST-ONLY HMAC shared with HarnessD (the provider is not under test; CARV is). Optional input
// "ClockOffsetMinutes" models a version-1 instance whose clock lags the database (as harness F's legacy agent did).
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuthorizationAuthority.Api.Composition;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Keys;
using AuthorizationAuthority.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Npgsql;

var command = args[0];
var input = JsonDocument.Parse(await File.ReadAllTextAsync(args[1])).RootElement;
var runtime = Environment.GetEnvironmentVariable("M2E_LEGACY_RUNTIME") ?? throw new InvalidOperationException("M2E_LEGACY_RUNTIME");
var cs = ContentAcceptanceRuleConnection.WithVersionedApplicationName(new NpgsqlConnectionStringBuilder(runtime) { ApplicationName = "m2e-legacy-agent", Pooling = false }.ConnectionString)!;
var options = new DbContextOptionsBuilder<AuthorityDbContext>().UseNpgsql(cs, n => n.MigrationsHistoryTable("ef_migrations_history", AuthorityDbContext.SchemaName)).Options;
var policy = ContentAcceptancePolicy.Resolve(ContentAcceptancePolicy.ReleaseAName, null); // a version-1 instance still configured as Release A (R-CARV-21)
var alerts = new CapturingAlerts(); // that build's own operational alerts (they name a refusing fence constraint)
var store = new AuthorityStore(new Factory(options), policy, null, null, alerts);
var offsetMinutes = input.TryGetProperty("ClockOffsetMinutes", out var offset) ? offset.GetInt32() : 0;
TimeProvider clock = offsetMinutes == 0 ? TimeProvider.System : new OffsetClock(TimeSpan.FromMinutes(offsetMinutes));
var mac = new TestMac();
var verifier = new ProjectionIntegrityVerifier(store, ThrowingProxy.Create<ISpiceDbProjection>(), new ProjectionMarkerTransitionState(), new NoAlerts(), new ProjectionIntegrityMetrics(), clock);
var service = new ContentService(store, mac, verifier, new NoFaults(), clock, new ShareContributorWriteSwitch(false, false));
var actor = new VerifiedActor(input.GetProperty("Issuer").GetString()!, input.GetProperty("Subject").GetString()!, "m2e-legacy-agent");

object? result = null;
string? error = null;
string? message = null;
string? mapped = null;
try
{
    result = command switch
    {
        "prepare" => await service.PrepareAsync(actor, input.GetProperty("Request").Deserialize<PrepareContentChangeRequest>()!, input.GetProperty("Key").GetString()!, Guid.CreateVersion7(), CancellationToken.None),
        "commit" => await service.CommitAsync(actor, input.GetProperty("OperationId").GetGuid(), input.GetProperty("Request").Deserialize<CommitContentChangeRequest>()!, Guid.CreateVersion7(), CancellationToken.None),
        _ => throw new ArgumentOutOfRangeException(nameof(args), command),
    };
}
catch (Exception ex)
{
    error = Describe(ex);
    message = ex.Message;
    var context = new DefaultHttpContext();
    context.Response.Body = new MemoryStream();
    var handled = await new AuthorityExceptionHandler().TryHandleAsync(context, ex, CancellationToken.None);
    context.Response.Body.Position = 0;
    mapped = handled ? $"{context.Response.StatusCode} {await new StreamReader(context.Response.Body).ReadToEndAsync()}" : "unhandled (500 by the default pipeline)";
}

await File.WriteAllTextAsync(args[2], JsonSerializer.Serialize(new { Ok = error is null, Result = result, Error = error, Message = message, Mapped = mapped, Alerts = alerts.Summaries, BuildRuleVersion = ContentAcceptanceRules.CurrentVersion }));
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

internal sealed class OffsetClock(TimeSpan offset) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => base.GetUtcNow().Add(offset);
}

internal sealed class NoAlerts : ISecurityAlertSink
{
    public void Raise(SecurityAlert alert)
    {
    }
}

internal sealed class CapturingAlerts : ISecurityAlertSink
{
    public List<string> Summaries { get; } = [];

    public void Raise(SecurityAlert alert) => Summaries.Add(alert.AlertKind + ": " + alert.Summary);
}

internal sealed class NoFaults : IFaultInjector
{
    public void CrashIfArmed(string faultName)
    {
    }
}

/// <summary>Deterministic TEST-ONLY HMAC-SHA-256 (key = SHA-256 of a fixed label, the key name and version). Shared with HarnessD.</summary>
internal sealed class TestMac : IIntegrityMacProvider
{
    public string ProviderName => "LocalDevelopment";

    private static byte[] Key(IntegrityKeyReference reference) => SHA256.HashData(Encoding.UTF8.GetBytes($"m2d-gate-test-key:{reference.ProviderKeyName}:{reference.KeyVersion}"));

    public Task<IntegrityMacResult> SignAsync(IntegrityKeyReference keyReference, ReadOnlyMemory<byte> canonicalPayload, CancellationToken cancellationToken) =>
        Task.FromResult(new IntegrityMacResult(HMACSHA256.HashData(Key(keyReference), canonicalPayload.Span), keyReference.KeyVersion, "HMAC-SHA-256"));

    public Task<bool> VerifyAsync(IntegrityKeyReference keyReference, ReadOnlyMemory<byte> canonicalPayload, ReadOnlyMemory<byte> expectedMac, CancellationToken cancellationToken) =>
        Task.FromResult(expectedMac.Length == 32 && CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(Key(keyReference), canonicalPayload.Span), expectedMac.Span));

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
