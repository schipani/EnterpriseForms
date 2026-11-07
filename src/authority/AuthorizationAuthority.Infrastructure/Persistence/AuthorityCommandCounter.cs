using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AuthorizationAuthority.Infrastructure.Persistence;

/// <summary>
/// IX-1 measurement only (Testing environment): counts the AuthorityDb commands (database round trips) issued in the current
/// asynchronous flow, so the per-commit cost of the search index can be reported as a measured count rather than a code
/// reading. A count is collected only between <see cref="Start"/> and <see cref="Stop"/>, and the interceptor that feeds it
/// is registered only in the Testing environment. Transaction BEGIN and COMMIT are not commands and are not counted; they
/// are the same with and without the index.
/// </summary>
public static class AuthorityCommandCounter
{
    private static readonly AsyncLocal<CommandCount?> Current = new();

    /// <summary>Starts counting for the current flow and returns the count holder.</summary>
    public static CommandCount Start()
    {
        var count = new CommandCount();
        Current.Value = count;
        return count;
    }

    public static void Stop() => Current.Value = null;

    /// <summary>Counts one command issued outside EF (a raw <see cref="DbCommand"/> on the context's connection).</summary>
    internal static void CountRawCommand() => Current.Value?.Increment();

    internal static void CountEfCommand() => Current.Value?.Increment();

    public sealed class CommandCount
    {
        private int commands;

        public int Commands => Volatile.Read(ref commands);

        internal void Increment() => Interlocked.Increment(ref commands);
    }
}

/// <summary>IX-1 measurement only: feeds <see cref="AuthorityCommandCounter"/> with every command EF Core executes (queries, SaveChanges batches, ExecuteSql).</summary>
internal sealed class AuthorityCommandCountingInterceptor : DbCommandInterceptor
{
    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        AuthorityCommandCounter.CountEfCommand();
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        AuthorityCommandCounter.CountEfCommand();
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        AuthorityCommandCounter.CountEfCommand();
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        AuthorityCommandCounter.CountEfCommand();
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        AuthorityCommandCounter.CountEfCommand();
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        AuthorityCommandCounter.CountEfCommand();
        return ValueTask.FromResult(result);
    }
}
