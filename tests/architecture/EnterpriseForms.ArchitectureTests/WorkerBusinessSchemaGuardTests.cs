using EnterpriseForms.Infrastructure.Persistence;
using EnterpriseForms.Worker.Composition;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EnterpriseForms.ArchitectureTests;

/// <summary>
/// E3-M2b-2 (D-5): the Worker has no readiness endpoint, so it refuses to start unless the business required migrations are
/// verified (a missing migration and an unreadable history both refuse), and the check is registered before every processing
/// service. Here because this suite references the Worker host.
/// </summary>
public sealed class WorkerBusinessSchemaGuardTests
{
    private sealed class History(Func<IReadOnlyList<string>, IReadOnlySet<string>> answer) : IBusinessMigrationHistoryReader
    {
        public Task<IReadOnlySet<string>> ReadRecordedAsync(IReadOnlyList<string> migrationIds, CancellationToken cancellationToken) => Task.FromResult(answer(migrationIds));
    }

    private sealed class Lifetime : IHostApplicationLifetime
    {
        public bool StopRequested { get; private set; }

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => StopRequested = true;
    }

    private static (BusinessSchemaStartupGuard Guard, Lifetime Lifetime) Create(Func<IReadOnlyList<string>, IReadOnlySet<string>> answer)
    {
        var lifetime = new Lifetime();
        var guard = new BusinessSchemaGuard(new History(answer), TimeProvider.System, NullLogger<BusinessSchemaGuard>.Instance);
        return (new BusinessSchemaStartupGuard(guard, lifetime, NullLogger<BusinessSchemaStartupGuard>.Instance), lifetime);
    }

    [Fact]
    public async Task TheWorkerStartsOnlyOnAVerifiedSchema()
    {
        var (ready, readyLifetime) = Create(ids => ids.ToHashSet(StringComparer.Ordinal));
        await ready.StartAsync(CancellationToken.None);
        Assert.False(readyLifetime.StopRequested);

        var (missing, missingLifetime) = Create(_ => new HashSet<string>());
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => missing.StartAsync(CancellationToken.None));
        Assert.Contains(BusinessSchemaRequirements.MigrationMissingDetail, refused.Message, StringComparison.Ordinal);
        Assert.True(missingLifetime.StopRequested);

        var (unreadable, unreadableLifetime) = Create(_ => throw new InvalidOperationException("database unreachable"));
        refused = await Assert.ThrowsAsync<InvalidOperationException>(() => unreadable.StartAsync(CancellationToken.None));
        Assert.Contains(BusinessSchemaRequirements.UnreadableDetail, refused.Message, StringComparison.Ordinal);
        Assert.True(unreadableLifetime.StopRequested);
    }

    [Fact]
    public void TheSchemaGuardIsRegisteredBeforeEveryProcessingService()
    {
        var program = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "backend", "EnterpriseForms.Worker", "Program.cs"));
        var guard = program.IndexOf("AddHostedService<BusinessSchemaStartupGuard>()", StringComparison.Ordinal);
        Assert.True(guard > 0, "the Worker must register the business schema startup guard");
        foreach (var worker in new[] { "DocumentProcessingWorker", "FieldProtectionMigrationWorker", "FieldProtectionReconciliationWorker", "SearchIndexBuilderWorker", "TeamMembershipReconciliationWorker" })
        {
            var position = program.IndexOf(worker + ">()", StringComparison.Ordinal);
            Assert.True(position > guard, worker + " must be registered after the business schema startup guard");
        }
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
