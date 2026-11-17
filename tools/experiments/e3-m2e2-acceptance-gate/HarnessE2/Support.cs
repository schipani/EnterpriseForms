// E3-M2e-2 harness ports (test-only). Derived from the E3-M2e-1 HarnessE1 support types.
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Domain.Features.Keys;
using AuthorizationAuthority.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

/// <summary>
/// E3-M2f-2G (OA-M2f2-05, docs/security/E3_M2F_CONTRACT_CLOSURE.md §3): the business build a harness request models, and the envelope
/// capability that build declares (CAP-S5: a build without the header is level 1). The Authority parses the header in its Api and passes
/// the parsed value to the services; in-process requests pass that parsed value directly. The E3-M2e-2 scenarios test Authority rules
/// with a business that can represent signed schema 2 and Envelope v2 faithfully, so they model the CURRENT build (E3-M2f-1 and later,
/// level 2), named at every helper call by default and at every direct service call explicitly. Older builds are modelled only where a
/// check is about them (group G), with <see cref="Undeclared"/> or <see cref="DeclaresOne"/>; real older builds run as separate
/// processes (BusinessAgent) and send their own headers.
/// </summary>
internal sealed record BusinessBuild(string Name, AuthorizationAuthority.Application.Features.Content.ClientEnvelopeCapability? Declared)
{
    public static BusinessBuild Current { get; } = new("current business build (E3-M2f-1+), declares level 2", AuthorizationAuthority.Application.Features.Content.ClientEnvelopeCapability.Current);

    /// <summary>A build before E3-M2f-1: no header (the Authority Api passes level 1; in-process the argument is null, the same as no header).</summary>
    public static BusinessBuild Undeclared { get; } = new("pre-E3-M2f-1 business build, no header (level 1)", null);

    /// <summary>A build that declares level 1 explicitly.</summary>
    public static BusinessBuild DeclaresOne { get; } = new("business build declaring level 1", AuthorizationAuthority.Application.Features.Content.ClientEnvelopeCapability.Legacy);
}

/// <summary>Every DbContext the harness creates goes through <see cref="Target.Require"/>.</summary>
internal sealed class Factory(DbContextOptions<AuthorityDbContext> options) : IDbContextFactory<AuthorityDbContext>
{
    public static Factory For(string connectionString, params IInterceptor?[] interceptors)
    {
        var builder = new DbContextOptionsBuilder<AuthorityDbContext>().UseNpgsql(Target.Require(connectionString), n => n.MigrationsHistoryTable("ef_migrations_history", AuthorityDbContext.SchemaName));
        var present = interceptors.Where(i => i is not null).Select(i => i!).ToArray();
        return new(present.Length == 0 ? builder.Options : builder.AddInterceptors(present).Options);
    }

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
    private readonly List<SecurityAlert> raised = [];

    public int Count
    {
        get
        {
            lock (raised)
            {
                return raised.Count;
            }
        }
    }

    public string Since(int index)
    {
        lock (raised)
        {
            return string.Join(" || ", raised.Skip(index).Select(a => a.ToString()));
        }
    }

    public void Raise(SecurityAlert alert)
    {
        lock (raised)
        {
            raised.Add(alert);
        }
    }
}

/// <summary>A fault injector that throws once at the armed fault point (M2-CON-04: a crash after the Prepare commit).</summary>
internal sealed class ArmedFaults : IFaultInjector
{
    private string? armed;

    public void Arm(string faultName) => Volatile.Write(ref armed, faultName);

    public void CrashIfArmed(string faultName)
    {
        if (Interlocked.CompareExchange(ref armed, null, faultName) == faultName)
        {
            throw new SimulatedCrashException(faultName);
        }
    }
}

internal sealed class SimulatedCrashException(string point) : Exception("simulated crash at " + point);

/// <summary>Deterministic TEST-ONLY HMAC-SHA-256 shared with the frozen-build agent; counts signing calls (DK-4 is before signing).</summary>
internal sealed class TestMac : IIntegrityMacProvider
{
    private int verifications;
    private int signatures;

    public int Verifications => Volatile.Read(ref verifications);

    public int Signatures => Volatile.Read(ref signatures);

    public string ProviderName => "LocalDevelopment";

    private static byte[] Key(IntegrityKeyReference reference) => SHA256.HashData(Encoding.UTF8.GetBytes($"m2d-gate-test-key:{reference.ProviderKeyName}:{reference.KeyVersion}"));

    public Task<IntegrityMacResult> SignAsync(IntegrityKeyReference keyReference, ReadOnlyMemory<byte> canonicalPayload, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref signatures);
        return Task.FromResult(new IntegrityMacResult(HMACSHA256.HashData(Key(keyReference), canonicalPayload.Span), keyReference.KeyVersion, "HMAC-SHA-256"));
    }

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

/// <summary>
/// M2-CON-02 (deterministic cross-organization race). Instance A: after its batched INSERT that includes the ledger binding has
/// executed (row written, transaction still open), it signals and waits until instance B is about to send its own binding INSERT, so B's
/// DK-4 lookup ran before A committed. Instance B: signals just before its binding INSERT is sent; PostgreSQL then blocks it on A's
/// uncommitted unique-index entry until A commits, and refuses it (23505).
/// </summary>
internal sealed class BindingInsertGate(bool isFirst, TaskCompletionSource firstInserted, TaskCompletionSource secondAtInsert) : DbCommandInterceptor
{
    private static bool InsertsBinding(DbCommand command) => command.CommandText.Contains("INSERT INTO authority.content_data_key_bindings", StringComparison.Ordinal);

    public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        await AfterAsync(command);
        return result;
    }

    public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        await AfterAsync(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        Before(command);
        return ValueTask.FromResult(result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Before(command);
        return ValueTask.FromResult(result);
    }

    private void Before(DbCommand command)
    {
        if (!isFirst && InsertsBinding(command))
        {
            secondAtInsert.TrySetResult();
        }
    }

    private async Task AfterAsync(DbCommand command)
    {
        if (isFirst && InsertsBinding(command))
        {
            firstInserted.TrySetResult();
            await Task.WhenAny(secondAtInsert.Task, Task.Delay(TimeSpan.FromSeconds(20)));
            await Task.Delay(750); // let the second INSERT reach PostgreSQL and wait on the index entry
        }
    }
}
