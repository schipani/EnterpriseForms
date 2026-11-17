using System.Net;
using System.Reflection;
using EnterpriseForms.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// E3-M2b-2 business required-migration guard (D-5, M2-FEN-07): never evaluated, stale, unreadable or missing ⇒ not ready; a
/// definitive missing migration stops the host, an unreadable history does not; the API's /health/ready reports it. The
/// real-PostgreSQL behaviour (migrated and unmigrated databases) is in the E3-M2b-2 PostgreSQL gate.
/// </summary>
public sealed class BusinessSchemaGuardTests
{
    internal sealed class FakeHistory(Func<IReadOnlyList<string>, IReadOnlySet<string>> answer) : IBusinessMigrationHistoryReader
    {
        public int Reads { get; private set; }

        public Task<IReadOnlySet<string>> ReadRecordedAsync(IReadOnlyList<string> migrationIds, CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(answer(migrationIds));
        }

        public static FakeHistory Migrated() => new(ids => ids.ToHashSet(StringComparer.Ordinal));

        public static FakeHistory Unmigrated() => new(_ => new HashSet<string>(StringComparer.Ordinal) { "20261103054837_PhaseC8M3RegionalPreferences" });

        public static FakeHistory Unreadable() => new(_ => throw new InvalidOperationException("database unreachable"));
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static BusinessSchemaGuard Guard(IBusinessMigrationHistoryReader reader, TimeProvider? clock = null) => new(reader, clock ?? TimeProvider.System, NullLogger<BusinessSchemaGuard>.Instance);

    [Fact]
    public void TheRequiredMigrationIsTheCompiledEnvelopeV2Migration()
    {
        var ids = typeof(BusinessDbContext).Assembly.GetTypes()
            .Where(t => typeof(Migration).IsAssignableFrom(t) && t.GetCustomAttribute<MigrationAttribute>() is not null)
            .Select(t => t.GetCustomAttribute<MigrationAttribute>()!.Id).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(["20261115125042_EnvelopeV2SignedColumns"], BusinessSchemaRequirements.RequiredMigrationIds);
        Assert.All(BusinessSchemaRequirements.RequiredMigrationIds, id => Assert.Contains(id, ids));
        Assert.Equal(TimeSpan.FromSeconds(30), BusinessSchemaRequirements.ReadinessRecheckInterval);
    }

    [Fact]
    public async Task OnlyAVerifiedFreshDecisionIsReady()
    {
        var clock = new ManualClock();
        var guard = Guard(FakeHistory.Migrated(), clock);
        Assert.Equal((false, BusinessSchemaRequirements.UnverifiedDetail), guard.Current);

        Assert.Equal(BusinessSchemaState.Ready, await guard.RefreshAsync(TestContext.Current.CancellationToken));
        Assert.Equal((true, BusinessSchemaRequirements.ReadyDetail), guard.Current);

        clock.Now += BusinessSchemaRequirements.MaximumDecisionAge + TimeSpan.FromSeconds(1);
        Assert.Equal((false, BusinessSchemaRequirements.StaleDetail), guard.Current);
    }

    [Fact]
    public async Task AnUnmigratedOrUnreadableDatabaseIsNotReady()
    {
        var missing = Guard(FakeHistory.Unmigrated());
        Assert.Equal(BusinessSchemaState.MigrationMissing, await missing.RefreshAsync(TestContext.Current.CancellationToken));
        Assert.Equal((false, BusinessSchemaRequirements.MigrationMissingDetail), missing.Current);

        var unreadable = Guard(FakeHistory.Unreadable());
        Assert.Equal(BusinessSchemaState.Unreadable, await unreadable.RefreshAsync(TestContext.Current.CancellationToken));
        Assert.Equal((false, BusinessSchemaRequirements.UnreadableDetail), unreadable.Current);
    }

    [Fact]
    public async Task AReadyDecisionIsWithdrawnWhenALaterReadFindsTheMigrationMissing()
    {
        var migrated = true;
        var guard = Guard(new FakeHistory(ids => migrated ? ids.ToHashSet(StringComparer.Ordinal) : new HashSet<string>()));
        await guard.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.True(guard.Current.Ready);
        migrated = false; // e.g. a restore of the business database to before the migration
        await guard.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.Equal((false, BusinessSchemaRequirements.MigrationMissingDetail), guard.Current);
    }

    [Fact]
    public async Task TheMonitorStopsTheHostOnAMissingMigrationButNotOnAnUnreadableHistory()
    {
        var stopped = 0;
        using (var bounded = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken))
        {
            bounded.CancelAfter(TimeSpan.FromSeconds(10)); // a monitor that never stops would otherwise loop forever
            await Guard(FakeHistory.Unmigrated()).MonitorAsync(() => stopped++, bounded.Token);
        }

        Assert.Equal(1, stopped);

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var reads = 0;
        var unreadable = Guard(new FakeHistory(_ =>
        {
            reads++;
            cancel.Cancel(); // end the loop after the first evaluation
            throw new InvalidOperationException("database unreachable");
        }));
        await unreadable.MonitorAsync(() => stopped++, cancel.Token);
        Assert.Equal(1, reads);
        Assert.Equal(1, stopped);
        Assert.False(unreadable.Current.Ready);
    }

    [Fact]
    public async Task ApiReadinessIs503UntilTheBusinessSchemaIsVerified()
    {
        using var authority = new WebApplicationFactory<AuthorizationAuthority.Api.Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        var authorityHandler = authority.Server.CreateHandler();
        using var api = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?> { ["TrustAuthority:BaseAddress"] = "http://authority.test" }));
            b.ConfigureServices(services =>
            {
                services.AddHttpClient(Composition.TrustAuthorityReadinessProbe.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => authorityHandler);
                services.RemoveAll<IBusinessMigrationHistoryReader>();
                services.AddSingleton<IBusinessMigrationHistoryReader>(FakeHistory.Unreadable());
            });
        });

        using var client = api.CreateClient();
        var response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(body.Contains(BusinessSchemaRequirements.UnreadableDetail, StringComparison.Ordinal) || body.Contains(BusinessSchemaRequirements.UnverifiedDetail, StringComparison.Ordinal), body);
    }
}
