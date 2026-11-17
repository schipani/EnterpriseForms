using System.Data;
using System.Data.Common;
using System.Reflection;
using System.Text.RegularExpressions;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// E3-M2f-2 AM-F1 #1 (M2F-20, in-process half): the real EF store method, on a context whose connection is never opened, issues exactly one
/// command, <c>SELECT EXISTS (SELECT 1 FROM authority.content_data_key_bindings … WHERE organization_id = @p)</c>, with the organization as
/// its only parameter, materializes no binding column, tracks nothing, and returns the database's boolean. The PostgreSQL half (plan, index,
/// role privileges, lock behaviour) belongs to the disposable gate E3-M2f-2G and is not claimed here.
/// </summary>
public sealed class BindingExistenceQueryTests
{
    private sealed class NeverOpen : DbConnectionInterceptor
    {
        public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result) => InterceptionResult.Suppress();

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(InterceptionResult.Suppress());
    }

    private sealed class Capturing(bool answer) : DbCommandInterceptor
    {
        public List<(string Sql, List<object?> Parameters)> Commands { get; } = [];

        private void Capture(DbCommand command) => Commands.Add((command.CommandText, command.Parameters.Cast<DbParameter>().Select(p => p.Value).ToList()));

        private DataTableReader Answer()
        {
            using var table = new DataTable();
            table.Columns.Add("exists", typeof(bool));
            table.Rows.Add(answer);
            return table.CreateDataReader();
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Capture(command);
            return ValueTask.FromResult(InterceptionResult<DbDataReader>.SuppressWithResult(Answer()));
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Capture(command);
            return ValueTask.FromResult(InterceptionResult<object>.SuppressWithResult(answer));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task M2F20TheStoreIssuesOneBooleanExistsQueryKeyedOnTheOrganizationOnly(bool answer)
    {
        var commands = new Capturing(answer);
        var options = new DbContextOptionsBuilder<AuthorityDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=9;Database=none;Username=none;Password=none")
            .AddInterceptors(new NeverOpen(), commands)
            .Options;
        await using var context = new AuthorityDbContext(options);
        var scopeType = typeof(AuthorityStore).GetNestedType("AuthorityScope", BindingFlags.NonPublic)!;
        var scope = (IAuthorityScope)Activator.CreateInstance(scopeType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            [context, null, null, null, ContentAcceptancePolicy.Resolve("ReleaseA", null)], null)!;
        var organization = Guid.CreateVersion7();

        Assert.Equal(answer, await scope.OrganizationHasDataKeyBindingsAsync(organization, TestContext.Current.CancellationToken));

        var (sql, parameters) = Assert.Single(commands.Commands);
        var normalized = Regex.Replace(sql, @"\s+", " ").Trim();
        Assert.Matches(@"^SELECT EXISTS \( SELECT 1 FROM authority\.content_data_key_bindings AS (\w+) WHERE \1\.organization_id = @\w+\)$", normalized);
        Assert.Equal([organization], parameters);
        Assert.Empty(context.ChangeTracker.Entries());
    }
}
