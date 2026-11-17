using System.Text.RegularExpressions;
using EnterpriseForms.Worker.Composition;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EnterpriseForms.ArchitectureTests;

/// <summary>
/// E0-1: the Worker refuses the LocalDevelopment key provider outside Development and Testing, with the Business API's rule
/// (EncryptionProviderEnvironmentPolicy), and the refusal runs before any processing service starts. Here because this suite
/// is the one that references the Worker host.
/// </summary>
public sealed class WorkerEncryptionProviderGuardTests
{
    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "EnterpriseForms.Worker";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public bool StopRequested { get; private set; }

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => StopRequested = true;
    }

    private static (EncryptionProviderStartupGuard Guard, FakeLifetime Lifetime) Guard(string? keyProvider, string environment)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(keyProvider is null ? [] : [new KeyValuePair<string, string?>("Encryption:KeyProvider", keyProvider)]).Build();
        var lifetime = new FakeLifetime();
        return (new EncryptionProviderStartupGuard(configuration, new FakeEnvironment(environment), lifetime, NullLogger<EncryptionProviderStartupGuard>.Instance), lifetime);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("production")]
    [InlineData("")]
    public void TheWorkerRefusesToStartWithTheLocalProviderOutsideDevelopmentAndTesting(string environment)
    {
        var (guard, lifetime) = Guard("LocalDevelopment", environment);
        var refused = Assert.Throws<InvalidOperationException>(() => { _ = guard.StartAsync(CancellationToken.None); });
        Assert.Contains("Refusing to start", refused.Message, StringComparison.Ordinal);
        Assert.True(lifetime.StopRequested);
    }

    [Theory]
    [InlineData("LocalDevelopment", "Development")]
    [InlineData("LocalDevelopment", "Testing")]
    [InlineData("LocalDevelopment", "development")]
    [InlineData("Unconfigured", "Production")] // the fail-closed unavailable provider; nothing is substituted
    [InlineData(null, "Production")]
    public void TheWorkerStartsWhereTheBusinessApiWould(string? keyProvider, string environment)
    {
        var (guard, lifetime) = Guard(keyProvider, environment);
        Assert.True(guard.StartAsync(CancellationToken.None).IsCompletedSuccessfully);
        Assert.False(lifetime.StopRequested);
    }

    [Fact]
    public void TheGuardIsRegisteredBeforeTheBusinessInfrastructureAndEveryProcessingService()
    {
        var program = File.ReadAllText(RepositoryPaths.Combine("src", "backend", "EnterpriseForms.Worker", "Program.cs"));
        var hostedServices = Regex.Matches(program, @"AddHostedService<([A-Za-z0-9_.]+)>").Select(m => (Name: m.Groups[1].Value.Split('.')[^1], m.Index)).ToList();
        var guard = hostedServices.Single(h => h.Name == nameof(EncryptionProviderStartupGuard));
        Assert.True(guard.Index < program.IndexOf("AddBusinessInfrastructure(", StringComparison.Ordinal), "the guard must be registered before the business infrastructure");
        // Hosted services start in registration order; only the trust-configuration guard may start before it.
        Assert.Equal([nameof(StartupConfigurationGuard), nameof(EncryptionProviderStartupGuard)], hostedServices.Take(2).Select(h => h.Name).ToList());
    }
}
