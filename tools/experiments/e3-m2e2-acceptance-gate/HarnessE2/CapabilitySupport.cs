// E3-M2f-2G support (docs/security/E3_M2F2G_VERIFICATION_RECORD.md; closure docs/security/E3_M2F_CONTRACT_CLOSURE.md §12.3). TEST-ONLY.
// Statement and transaction tracing of one Authority composition, a pause interceptor for real-PostgreSQL ordering scenarios, and the
// SpiceDB double. SpiceDB is not under test: the capability check precedes every SpiceDB call, and the user Verify paths need a projection
// marker and permission answers to reach the verification that the G-2 demonstrations are about. No real SpiceDB is reachable from the run.
using System.Data.Common;
using System.Reflection;
using System.Text.RegularExpressions;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

/// <summary>Every SQL statement and transaction outcome of one composition, in order (shared by its command and transaction interceptors).</summary>
internal sealed partial class StatementTrace
{
    private readonly List<string> events = [];

    public StatementTrace()
    {
        Commands = new CommandSide(this);
        Transactions = new TransactionSide(this);
    }

    public CommandSide Commands { get; }

    public TransactionSide Transactions { get; }

    public void Reset()
    {
        lock (events)
        {
            events.Clear();
        }
    }

    public List<string> Events
    {
        get
        {
            lock (events)
            {
                return [.. events];
            }
        }
    }

    /// <summary>The statements only (normalized, one line each).</summary>
    public List<string> Statements => [.. Events.Where(e => e.StartsWith("SQL ", StringComparison.Ordinal)).Select(e => e[4..])];

    public bool Committed => Events.Contains("TX commit");

    /// <summary>The statements that touch the data-key ledger.</summary>
    public List<string> LedgerStatements => [.. Statements.Where(s => s.Contains("content_data_key_bindings", StringComparison.Ordinal))];

    /// <summary>How many statements ran after the first ledger statement (the existence query, for a capability decision).</summary>
    public int StatementsAfterLedger
    {
        get
        {
            var all = Statements;
            var first = all.FindIndex(s => s.Contains("content_data_key_bindings", StringComparison.Ordinal));
            return first < 0 ? -1 : all.Count - first - 1;
        }
    }

    /// <summary>A short, comparable summary: the kind of each statement (lock, fence, ledger, ...).</summary>
    public string Shape() => string.Join(" > ", Events.Select(Classify));

    public static string Classify(string e)
    {
        if (!e.StartsWith("SQL ", StringComparison.Ordinal))
        {
            return e.Replace("TX ", "tx-", StringComparison.Ordinal);
        }

        var s = e[4..];
        if (s.Contains("content_data_key_bindings", StringComparison.Ordinal))
        {
            return s.Contains("EXISTS", StringComparison.OrdinalIgnoreCase) ? "ledger-exists" : "ledger-other";
        }

        if (s.Contains("organization_authorization_states", StringComparison.Ordinal) && s.Contains("FOR UPDATE", StringComparison.OrdinalIgnoreCase))
        {
            return "org-lock";
        }

        if (s.StartsWith("LOCK TABLE", StringComparison.OrdinalIgnoreCase) || s.Contains("ef_migrations_history", StringComparison.Ordinal) || s.Contains("pg_constraint", StringComparison.Ordinal))
        {
            return "fence";
        }

        var table = TableName().Match(s);
        return table.Success ? "read:" + table.Groups[1].Value : "sql";
    }

    [GeneratedRegex(@"authority\.""?([a-z_]+)""?")]
    private static partial Regex TableName();

    private void Add(string entry)
    {
        lock (events)
        {
            events.Add(entry);
        }
    }

    internal sealed class CommandSide(StatementTrace owner) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            owner.Add("SQL " + Normalize(command));
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            owner.Add("SQL " + Normalize(command));
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            owner.Add("SQL " + Normalize(command));
            return ValueTask.FromResult(result);
        }

        private static string Normalize(DbCommand command) => Regex.Replace(command.CommandText, @"\s+", " ").Trim() + " [params " + command.Parameters.Count + "]";
    }

    internal sealed class TransactionSide(StatementTrace owner) : DbTransactionInterceptor
    {
        public override ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
        {
            owner.Add("TX begin");
            return ValueTask.FromResult(result);
        }

        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            owner.Add("TX commit");
            return Task.CompletedTask;
        }

        public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            owner.Add("TX rollback");
            return Task.CompletedTask;
        }
    }
}

/// <summary>
/// Real-PostgreSQL ordering control: after the first statement matching <see cref="Match"/> has EXECUTED (so its locks are held and its
/// effects are in the open transaction), signals <see cref="Reached"/> and waits for <see cref="Release"/>; then, if <see cref="FailAfterRelease"/>,
/// throws, so the open transaction rolls back. Fires once.
/// </summary>
internal sealed class PauseAfter(Func<string, bool> match) : DbCommandInterceptor
{
    private int fired;

    public Func<string, bool> Match { get; } = match;

    public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool FailAfterRelease { get; set; }

    public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    {
        await HoldAsync(command);
        return result;
    }

    public override async ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        await HoldAsync(command);
        return result;
    }

    public override async ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
    {
        await HoldAsync(command);
        return result;
    }

    private async Task HoldAsync(DbCommand command)
    {
        if (!Match(command.CommandText) || Interlocked.Exchange(ref fired, 1) == 1)
        {
            return;
        }

        Reached.TrySetResult();
        if (await Task.WhenAny(Release.Task, Task.Delay(TimeSpan.FromSeconds(60))) != Release.Task)
        {
            throw new TimeoutException("the paused transaction was never released (harness bug)");
        }

        if (FailAfterRelease)
        {
            throw new InvalidOperationException("controlled failure after the paused statement (the transaction rolls back)");
        }
    }
}

/// <summary>SpiceDB double: the marker the Authority expects for the organization's CURRENT database state; every permission allowed.</summary>
public class AllowingSpiceDb : DispatchProxy
{
    public string AdminConnectionString { get; set; } = string.Empty;

    public static ISpiceDbProjection Create(string adminConnectionString)
    {
        var proxy = Create<ISpiceDbProjection, AllowingSpiceDb>();
        ((AllowingSpiceDb)(object)proxy).AdminConnectionString = Target.Require(adminConnectionString);
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        switch (targetMethod?.Name)
        {
            case nameof(ISpiceDbProjection.ReadMarkerAsync):
            {
                using var context = new AuthorityDbContext(new DbContextOptionsBuilder<AuthorityDbContext>().UseNpgsql(AdminConnectionString).Options);
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

/// <summary>Real-PostgreSQL control: makes the transaction's COMMIT fail (before it is sent), so the open transaction rolls back cleanly.</summary>
internal sealed class FailCommit : DbTransactionInterceptor
{
    public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("controlled commit failure (the transaction rolls back)");
}
