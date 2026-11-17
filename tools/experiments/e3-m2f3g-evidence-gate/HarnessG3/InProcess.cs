// E3-M2f-3G in-process composition of the REAL recorder over the REAL AuthorityStore (approved runtime identity, this build's versioned
// application name) for scenarios that need an exact fault point. The only test-only pieces are: a shifted clock (it moves the
// recorder's in-process caches, attempt window and retry backoff; the T2 budget and every database time stay real), an outcome
// observer, EF interceptors that pause, signal, terminate or fail at one exact statement or commit (they never alter SQL text,
// parameters or results), a deterministic test MAC for in-process level-2 writers, and a SpiceDB double that throws if used.
using System.Collections.Concurrent;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Domain.Features.Keys;
using AuthorizationAuthority.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

/// <summary>System time plus an adjustable offset (in-process recorder state only).</summary>
internal sealed class ShiftedClock : TimeProvider
{
    private long offsetTicks;

    public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + TimeSpan.FromTicks(Interlocked.Read(ref offsetTicks));

    public override long GetTimestamp() => System.GetTimestamp() + (long)(TimeSpan.FromTicks(Interlocked.Read(ref offsetTicks)).TotalSeconds * TimestampFrequency);

    public void Advance(TimeSpan by) => Interlocked.Add(ref offsetTicks, by.Ticks);
}

internal sealed class OutcomeLog : ICapabilityRefusalEvidenceObserver
{
    private readonly ConcurrentQueue<(Guid Organization, string EntryPoint, CapabilityRefusalEvidenceOutcome Outcome, CapabilityRefusalEvidenceReason Reason)> seen = new();

    public void Observed(Guid organizationId, string entryPoint, CapabilityRefusalEvidenceOutcome outcome, CapabilityRefusalEvidenceReason reason) => seen.Enqueue((organizationId, entryPoint, outcome, reason));

    public IReadOnlyList<(Guid Organization, string EntryPoint, CapabilityRefusalEvidenceOutcome Outcome, CapabilityRefusalEvidenceReason Reason)> All => [.. seen];

    /// <summary>The host's log classification (D-3b-2): 9253 = a retry is scheduled; 9254 = the refusal will not be recorded; none otherwise.</summary>
    public static string LogClass(CapabilityRefusalEvidenceOutcome outcome, CapabilityRefusalEvidenceReason reason) => outcome switch
    {
        CapabilityRefusalEvidenceOutcome.Deferred or CapabilityRefusalEvidenceOutcome.Ambiguous => "9253",
        CapabilityRefusalEvidenceOutcome.Inconclusive or CapabilityRefusalEvidenceOutcome.QuotaSuppressed or CapabilityRefusalEvidenceOutcome.Abandoned or CapabilityRefusalEvidenceOutcome.NotAttempted => "9254",
        _ => "-",
    };

    public string Summary() => string.Join(",", All.GroupBy(o => o.Outcome + (o.Reason == CapabilityRefusalEvidenceReason.None ? string.Empty : "/" + o.Reason)).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key + "x" + g.Count()));
}

/// <summary>Runs <see cref="Before"/> immediately before the first EF command whose text matches (once unless <see cref="Repeat"/>).</summary>
internal sealed class StatementHook(Func<string, bool> match, Func<DbCommand, Task> before, bool repeat = false) : DbCommandInterceptor
{
    private int fired;

    public bool Repeat { get; } = repeat;

    public int Fired => Volatile.Read(ref fired);

    private async Task MaybeAsync(DbCommand command)
    {
        if (match(command.CommandText) && (Repeat || Interlocked.CompareExchange(ref fired, 1, 0) == 0))
        {
            if (Repeat)
            {
                Interlocked.Increment(ref fired);
            }

            await before(command);
        }
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        await MaybeAsync(command);
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        await MaybeAsync(command);
        return result;
    }

    public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        await MaybeAsync(command);
        return result;
    }
}

/// <summary>Observes (never alters) the EF-issued commands of one store, in order, with the time each was sent and completed.</summary>
internal sealed class StatementObserver : DbCommandInterceptor
{
    private readonly ConcurrentQueue<(DateTimeOffset At, string Text)> sent = new();
    private readonly ConcurrentQueue<(DateTimeOffset At, string Text)> done = new();

    public IReadOnlyList<string> Texts => [.. sent.Select(s => s.Text)];

    public IReadOnlyList<(DateTimeOffset At, string Text)> Sent => [.. sent];

    public IReadOnlyList<(DateTimeOffset At, string Text)> Completed => [.. done];

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        sent.Enqueue((DateTimeOffset.UtcNow, command.CommandText));
        return ValueTask.FromResult(result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        sent.Enqueue((DateTimeOffset.UtcNow, command.CommandText));
        return ValueTask.FromResult(result);
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        done.Enqueue((DateTimeOffset.UtcNow, command.CommandText));
        return ValueTask.FromResult(result);
    }

    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        done.Enqueue((DateTimeOffset.UtcNow, command.CommandText));
        return ValueTask.FromResult(result);
    }
}

/// <summary>Acts at the commit of an EF transaction: before COMMIT is sent, or after it returned.</summary>
internal sealed class CommitHook : DbTransactionInterceptor
{
    public Func<DbTransaction, Task>? BeforeCommit { get; set; }

    public Func<Task>? AfterCommit { get; set; }

    public int Commits;

    public DateTimeOffset? LastCommitted { get; private set; }

    public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        if (BeforeCommit is { } before)
        {
            await before(transaction);
        }

        return result;
    }

    public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref Commits);
        LastCommitted = DateTimeOffset.UtcNow;
        if (AfterCommit is { } after)
        {
            await after();
        }
    }
}

internal sealed class SimulatedCrashException(string point) : Exception("simulated crash at " + point);

internal sealed class Factory(DbContextOptions<AuthorityDbContext> options) : IDbContextFactory<AuthorityDbContext>
{
    public static Factory For(string connectionString, IReadOnlyList<IInterceptor> interceptors)
    {
        var builder = new DbContextOptionsBuilder<AuthorityDbContext>().UseNpgsql(Target.Require(connectionString), n => n.MigrationsHistoryTable("ef_migrations_history", AuthorityDbContext.SchemaName));
        return new(interceptors.Count == 0 ? builder.Options : builder.AddInterceptors(interceptors).Options);
    }

    public AuthorityDbContext CreateDbContext() => new(options);
}

/// <summary>Deterministic TEST-ONLY HMAC-SHA-256 for in-process level-2 writers (never the host's signer).</summary>
internal sealed class TestMac : IIntegrityMacProvider
{
    public string ProviderName => "LocalDevelopment";

    private static byte[] Key(IntegrityKeyReference reference) => SHA256.HashData(Encoding.UTF8.GetBytes($"m2f3g-gate-test-key:{reference.ProviderKeyName}:{reference.KeyVersion}"));

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

    protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args) => throw new NotSupportedException(targetMethod?.Name + " is not used by this gate");
}

internal sealed class NoFaults : IFaultInjector
{
    public void CrashIfArmed(string faultName)
    {
    }
}

internal sealed partial class Lab
{
    private static readonly ContentAcceptancePolicy ReleaseB3 = ContentAcceptancePolicy.Resolve(ContentAcceptancePolicy.ReleaseBName, "3");

    internal sealed record RecorderNode(CapabilityRefusalEvidenceRecorder Recorder, OutcomeLog Outcomes, AuthorityStore Store, ShiftedClock Clock, string Application);

    /// <summary>The real store with the approved runtime identity and a unique, versioned application name (statement traces filter on it).</summary>
    private AuthorityStore Store(string database, string application, IReadOnlyList<IInterceptor>? interceptors = null, AuthorityWorkSignal? signal = null) =>
        new(Factory.For(RuntimeFor(database, application), interceptors ?? []), ReleaseB3, signal, signal is null ? null : new AuthorityWorkOptions(), null);

    private RecorderNode Recorder(string database, string application, CapabilityRefusalEvidenceLimits? limits = null, IReadOnlyList<IInterceptor>? interceptors = null)
    {
        var clock = new ShiftedClock();
        var outcomes = new OutcomeLog();
        var store = Store(database, application, interceptors);
        return new RecorderNode(new CapabilityRefusalEvidenceRecorder(store, clock, outcomes, limits), outcomes, store, clock, application);
    }

    private static CapabilityRefusalContext Refusal(Org org) => new(org.Id, 1);

    /// <summary>The content service as the host composes it for this gate: deployment floor D = 2, Release B 3, Envelope v2 acceptance OFF.</summary>
    private ContentService Service(string database, string application, IReadOnlyList<IInterceptor>? interceptors = null)
    {
        var store = Store(database, application, interceptors);
        var verifier = new ProjectionIntegrityVerifier(store, ThrowingProxy.Create<ISpiceDbProjection>(), new ProjectionMarkerTransitionState(), new NoAlertSink(), new ProjectionIntegrityMetrics(), TimeProvider.System);
        return new ContentService(store, new TestMac(), verifier, new NoFaults(), TimeProvider.System, new ShareContributorWriteSwitch(false, false),
            envelopeV2Acceptance: EnvelopeV2AcceptanceSwitch.Create(null, "Testing", ReleaseB3), envelopeCapabilityFloor: EnvelopeCapabilityFloor.Create("2"));
    }

    private sealed class NoAlertSink : ISecurityAlertSink
    {
        public void Raise(SecurityAlert alert)
        {
        }
    }

    private static VerifiedActor Actor(Org org) => new(HostUserIssuer, org.Owner.ToString("D"), "m2f3g-harness");

    /// <summary>
    /// The refusal pipeline exactly as the host's endpoint filter runs it: the refused entry point (T1, level 1, D = 2) throws and
    /// disposes its transaction; the refusal context is read from the exception; the recorder runs T2. Returns (refusal, outcome, ms).
    /// </summary>
    private async Task<(string Refusal, CapabilityRefusalEvidenceOutcome Outcome, long Milliseconds)> RefusePrepareInProcessAsync(ContentService service, RecorderNode node, WRec record)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var request = new AuthorizationAuthority.Contracts.V1.PrepareContentChangeRequest(record.Org.Id, record.Id, Guid.CreateVersion7(), 7, 0, Convert.ToBase64String(EnterpriseForms.Integrity.Merkle.MerkleManifestV1.EmptyRoot()),
            [new AuthorizationAuthority.Contracts.V1.CandidateContentItem(AuthorizationAuthority.Contracts.V1.ManifestItemKind.RecordHeader, record.Id, Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)))]);
        try
        {
            await service.PrepareAsync(Actor(record.Org), request, "m2f3g-" + Guid.CreateVersion7().ToString("N"), Guid.CreateVersion7(), CancellationToken.None, ClientEnvelopeCapability.Legacy);
            return ("ADMITTED", CapabilityRefusalEvidenceOutcome.NotAttempted, sw.ElapsedMilliseconds);
        }
        catch (Exception ex) when (CapabilityRefusalContext.From(ex) is { } context)
        {
            var outcome = await node.Recorder.RecordAsync(context, CapabilityRefusalEvidence.EntryPoints.Prepare, CancellationToken.None);
            return (Describe(ex), outcome, sw.ElapsedMilliseconds);
        }
    }

    // ------------------------------------------------------------------------------------------------ level-2 v1 writers (Release B, D = 2)
    private static string Root(byte[] root) => Convert.ToBase64String(root);

    /// <summary>One owner Prepare + Commit of a v1 change (header, text and a v1 encrypted field) by a level-2 client. Returns "OK" or the refusal.</summary>
    private async Task<string> WriteV1Async(ContentService service, WRec record)
    {
        var (revision, root) = await HeadAsync(record);
        var operation = Guid.CreateVersion7();
        var n = record.NextWrite();
        List<object> sources = [new HeaderSource(record.Org.Id, record.Id), Rows.Text(record.Org.Id, record.Id, record.TextField, record.TextItem, "value-" + n),
            Rows.V1Field(record.Org.Id, record.Id, record.SecretField, record.SecretItem, (byte)(n % 200))];
        var rows = sources.Select(s => Rows.Encode(s, operation, revision + 1)).ToList();
        var request = new AuthorizationAuthority.Contracts.V1.PrepareContentChangeRequest(record.Org.Id, record.Id, operation, 7, revision, Root(root),
            [.. rows.Select(c => new AuthorizationAuthority.Contracts.V1.CandidateContentItem(c.Kind, c.ItemId, c.Base64))], CurrentItems: record.Accepted.Count == 0 ? null : record.AcceptedStaged());
        try
        {
            var prepared = await service.PrepareAsync(Actor(record.Org), request, "m2f3g-w-" + operation.ToString("N"), Guid.CreateVersion7(), CancellationToken.None, ClientEnvelopeCapability.Current);
            var signatures = prepared.SignedItems.ToDictionary(s => (s.ItemKind, s.ItemId), s => s.RowSignatureBase64);
            var staged = rows.Select(c => new AuthorizationAuthority.Contracts.V1.StagedContentItem(c.Kind, c.ItemId, c.Base64, signatures[(c.Kind, c.ItemId)])).ToList();
            await service.CommitAsync(Actor(record.Org), prepared.OperationId, new AuthorizationAuthority.Contracts.V1.CommitContentChangeRequest(record.Org.Id, record.Id, staged), Guid.CreateVersion7(), CancellationToken.None, ClientEnvelopeCapability.Current);
            record.Accepted = [.. rows.Select(c => (c, Convert.FromBase64String(signatures[(c.Kind, c.ItemId)])))];
            return "OK";
        }
        catch (Exception ex)
        {
            return Describe(ex);
        }
    }

    private async Task<(long Revision, byte[] Root)> HeadAsync(WRec record)
    {
        await using var c = await OpenAdminAsync(record.Org.Database, "g3-head");
        await using var command = new Npgsql.NpgsqlCommand("SELECT committed_content_revision, committed_manifest_root FROM authority.record_content_heads WHERE organization_id = @o AND form_record_id = @r", c);
        command.Parameters.AddWithValue("o", record.Org.Id);
        command.Parameters.AddWithValue("r", record.Id);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt64(0), (byte[])reader[1]);
    }
}
