// E3-M2b-1 implementation gate: LEGACY AGENT (scratchpad only; never part of the repository).
// Compiled against, and running, a frozen pre-CARV Authority Release build (..\oldbin-<commit>). One call per process:
//   LegacyAgent prepare|commit <input.json> <output.json>
// It drives that build's own ContentService and EF Core AuthorityStore on the least-privilege runtime role, exactly as the
// design-gate harness did, and reports the outcome plus the build's own HTTP classification (AuthorityExceptionHandler).
// The MAC port is a deterministic test HMAC shared with harness B (the provider is not under test; CARV is).
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
var runtime = Environment.GetEnvironmentVariable("CARV_RUNTIME") ?? throw new InvalidOperationException("CARV_RUNTIME");
var cs = new NpgsqlConnectionStringBuilder(runtime) { ApplicationName = "carv-runtime-LB" }.ConnectionString;
var options = new DbContextOptionsBuilder<AuthorityDbContext>().UseNpgsql(cs, n => n.MigrationsHistoryTable("ef_migrations_history", AuthorityDbContext.SchemaName)).Options;
var store = new AuthorityStore(new Factory(options));
var clock = TimeProvider.System;
var mac = new TestMac();
var verifier = new ProjectionIntegrityVerifier(store, ThrowingProxy.Create<ISpiceDbProjection>(), new ProjectionMarkerTransitionState(), new NoAlerts(), new ProjectionIntegrityMetrics(), clock);
var service = new ContentService(store, mac, verifier, new NoFaults(), clock, new ShareContributorWriteSwitch(false, false));
var actor = new VerifiedActor(input.GetProperty("Issuer").GetString()!, input.GetProperty("Subject").GetString()!, "carv-legacy-agent");

object? result = null;
string? error = null;
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
    var context = new DefaultHttpContext();
    context.Response.Body = new MemoryStream();
    var handled = await new AuthorityExceptionHandler().TryHandleAsync(context, ex, CancellationToken.None);
    context.Response.Body.Position = 0;
    mapped = handled ? $"{context.Response.StatusCode} {await new StreamReader(context.Response.Body).ReadToEndAsync()}" : "unhandled (500 by the default pipeline)";
}

await File.WriteAllTextAsync(args[2], JsonSerializer.Serialize(new { Ok = error is null, Result = result, Error = error, Mapped = mapped }));
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

/// <summary>Deterministic TEST-ONLY HMAC-SHA-256 (key = SHA-256 of a fixed label, the key name and version). Shared with harness B.</summary>
internal sealed class TestMac : IIntegrityMacProvider
{
    public string ProviderName => "LocalDevelopment";

    private static byte[] Key(IntegrityKeyReference reference) => SHA256.HashData(Encoding.UTF8.GetBytes($"carv-implementation-gate-test-key:{reference.ProviderKeyName}:{reference.KeyVersion}"));

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
