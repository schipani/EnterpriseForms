using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// Regression coverage for <see cref="StartupRefusal"/> against the real Authority host, with both startup orderings forced by
/// synchronization (no delays): <see cref="StartOrdering.Direct"/> holds the application thread inside the host's "Hosting failed to
/// start" log call until the factory has completed its synchronous start steps, so the factory observes the refusal directly;
/// <see cref="StartOrdering.AfterDisposal"/> waits until the application thread has disposed the host before the factory starts it,
/// which is the CI failure of run 45158422231 (attempts 1 and 2).
/// </summary>
public sealed class StartupRefusalTests
{
    public enum StartOrdering
    {
        Direct,
        AfterDisposal,
    }

    private static readonly TimeSpan OrderingTimeout = TimeSpan.FromSeconds(60);

    private const string RequiredFloorMissing = "Content acceptance policy is invalid (RequiredFloorMissing)";

    /// <summary>Development, Release B without a required floor: the content acceptance startup guard refuses.</summary>
    private static Dictionary<string, string?> FloorMissing() => new() { ["ContentAcceptance:EnforcementPhase"] = "ReleaseB", ["ContentAcceptance:RequiredFloor"] = null };

    /// <summary>Development, an invalid deployment envelope capability floor: the envelope capability startup guard refuses.</summary>
    private static Dictionary<string, string?> InvalidEnvelopeFloor() => new() { ["Content:MinimumBusinessEnvelopeCapability"] = "two" };

    /// <summary>Development, an unconfigured signing provider: the signing startup guard refuses (unrelated to the content policy).</summary>
    private static Dictionary<string, string?> UnconfiguredSigning() => new() { ["Signing:Provider"] = "Unconfigured" };

    /// <summary>The Authority host with one startup ordering forced.</summary>
    private sealed class OrderedAuthorityFactory(StartOrdering ordering, Dictionary<string, string?> settings) : WebApplicationFactory<AuthorizationAuthority.Api.Program>
    {
        private readonly DisposalSentinel sentinel = new();
        private readonly StartupFailureGate gate = new();

        /// <summary>True when the factory's start call had not completed when the application thread was released (Direct only).</summary>
        public bool FactoryStartWasPendingAtRelease { get; private set; }

        public bool ApplicationReachedTheStartupFailureLog => gate.Reached;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(settings));
            builder.ConfigureServices(services => services.Insert(0, ServiceDescriptor.Singleton<IHostedService>(_ => sentinel)));
            if (ordering == StartOrdering.Direct)
            {
                builder.ConfigureLogging(logging =>
                {
                    logging.AddProvider(gate);
                    logging.AddFilter<StartupFailureGate>((category, _) => string.Equals(category, StartupRefusal.HostLoggerCategory, StringComparison.Ordinal));
                });
            }
        }

        protected override IHost CreateHost(IHostBuilder builder)
        {
            var host = builder.Build();
            if (ordering == StartOrdering.AfterDisposal)
            {
                if (!sentinel.Disposed.Wait(OrderingTimeout, TestContext.Current.CancellationToken))
                {
                    throw new TimeoutException("The application thread did not dispose the host (did the startup fail?).");
                }

                host.Start();
                return host;
            }

            // Direct: the factory's start runs synchronously up to its wait for the start result (it has then resolved the host's
            // lifetime while the provider is alive, because the application thread cannot pass the gate). Then release it.
            var start = host.StartAsync(TestContext.Current.CancellationToken);
            FactoryStartWasPendingAtRelease = !start.IsCompleted;
            gate.Release();
            start.GetAwaiter().GetResult();
            return host;
        }
    }

    /// <summary>A hosted service owned by the host's container: disposing the host's provider disposes it.</summary>
    private sealed class DisposalSentinel : IHostedService, IDisposable
    {
        public ManualResetEventSlim Disposed { get; } = new();

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose() => Disposed.Set();
    }

    /// <summary>Holds the application thread in the host's "Hosting failed to start" log call (before the host is disposed).</summary>
    private sealed class StartupFailureGate : ILoggerProvider
    {
        private readonly ManualResetEventSlim released = new();

        public bool Reached { get; private set; }

        public void Release() => released.Set();

        public ILogger CreateLogger(string categoryName) => new GateLogger(this, categoryName);

        public void Dispose() => released.Set();

        private sealed class GateLogger(StartupFailureGate owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (string.Equals(category, StartupRefusal.HostLoggerCategory, StringComparison.Ordinal) && eventId.Id == StartupRefusal.HostedServiceStartupFaultedEventId)
                {
                    owner.Reached = true;
                    owner.released.Wait(OrderingTimeout);
                }
            }
        }
    }

    // ---- both orderings resolve to the expected refusal ----

    [Fact]
    public void RequiredFloorMissingIsObservedDirectlyWhenTheFactoryStartsFirst()
    {
        using var factory = new OrderedAuthorityFactory(StartOrdering.Direct, FloorMissing());
        var observation = StartupRefusal.Assert(factory, RequiredFloorMissing);
        Assert.False(observation.ResolvedFrameworkRace);
        Assert.Same(observation.Refusal, observation.Thrown);
        Assert.IsType<InvalidOperationException>(observation.Refusal);
        Assert.True(factory.ApplicationReachedTheStartupFailureLog);
        Assert.True(factory.FactoryStartWasPendingAtRelease);
    }

    [Fact]
    public void RequiredFloorMissingIsProvenFromTheHostLogWhenTheHostIsDisposedFirst()
    {
        using var factory = new OrderedAuthorityFactory(StartOrdering.AfterDisposal, FloorMissing());
        var observation = StartupRefusal.Assert(factory, RequiredFloorMissing);
        Assert.True(observation.ResolvedFrameworkRace);
        Assert.True(StartupRefusal.IsDeferredHostDisposedProvider(observation.Thrown)); // the forced ordering is the CI failure
        Assert.DoesNotContain(RequiredFloorMissing, StartupRefusal.Flatten(observation.Thrown), StringComparison.Ordinal); // the old assertion would fail
        Assert.IsType<InvalidOperationException>(observation.Refusal);
        Assert.Contains(RequiredFloorMissing, observation.Refusal.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(StartOrdering.Direct, false)]
    [InlineData(StartOrdering.AfterDisposal, true)]
    public void AnInvalidEnvelopeCapabilityFloorIsProvenInBothOrderings(StartOrdering ordering, bool race)
    {
        using var factory = new OrderedAuthorityFactory(ordering, InvalidEnvelopeFloor());
        var observation = StartupRefusal.Assert(factory, "Content:MinimumBusinessEnvelopeCapability");
        Assert.Equal(race, observation.ResolvedFrameworkRace);
        Assert.IsType<InvalidOperationException>(observation.Refusal);
    }

    // ---- everything else is rejected, in both orderings ----

    [Theory]
    [InlineData(StartOrdering.Direct)]
    [InlineData(StartOrdering.AfterDisposal)]
    public void AnUnrelatedStartupFailureIsRejected(StartOrdering ordering)
    {
        using var factory = new OrderedAuthorityFactory(ordering, UnconfiguredSigning());
        var rejected = Assert.Throws<StartupRefusalNotProvenException>(() => StartupRefusal.Assert(factory, RequiredFloorMissing));
        Assert.Contains("Signing:Provider is not configured", rejected.Message, StringComparison.Ordinal); // the diagnostics keep the real failure
    }

    [Theory]
    [InlineData(StartOrdering.Direct)]
    [InlineData(StartOrdering.AfterDisposal)]
    public void AMismatchedRefusalCodeIsRejected(StartOrdering ordering)
    {
        using var factory = new OrderedAuthorityFactory(ordering, FloorMissing());
        var rejected = Assert.Throws<StartupRefusalNotProvenException>(() => StartupRefusal.Assert(factory, "Content acceptance policy is invalid (RequiredFloorBelowMinimum)"));
        Assert.Contains("RequiredFloorMissing", rejected.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnexpectedSuccessfulStartIsRejected()
    {
        using var factory = new WebApplicationFactory<AuthorizationAuthority.Api.Program>().WithWebHostBuilder(b => b.UseEnvironment("Development")); // declares Release A: starts
        var rejected = Assert.Throws<StartupRefusalNotProvenException>(() => StartupRefusal.Assert(factory, RequiredFloorMissing));
        Assert.Contains("the host started", rejected.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADisposedProviderWithoutLoggedEvidenceIsRejected()
    {
        // A real disposed-provider exception from the forced ordering, without the helper's capture: no evidence, no proof.
        using var factory = new OrderedAuthorityFactory(StartOrdering.AfterDisposal, FloorMissing());
        var disposed = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.True(StartupRefusal.IsDeferredHostDisposedProvider(disposed));
        var rejected = Assert.Throws<StartupRefusalNotProvenException>(() => StartupRefusal.Evaluate(disposed, [], e => StartupRefusal.Flatten(e).Contains(RequiredFloorMissing, StringComparison.Ordinal), "the floor refusal"));
        Assert.Contains("logged no startup failure", rejected.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADisposedProviderThatIsNotTheFrameworkRaceIsRejectedEvenWithMatchingEvidence()
    {
        var refusal = new InvalidOperationException(RequiredFloorMissing);
        bool Matches(Exception e) => StartupRefusal.Flatten(e).Contains(RequiredFloorMissing, StringComparison.Ordinal);

        // Not thrown from the factory's deferred host start (no such frame), another object name, or with an inner cause.
        Assert.Throws<StartupRefusalNotProvenException>(() => StartupRefusal.Evaluate(new ObjectDisposedException("IServiceProvider"), [refusal], Matches, "the floor refusal"));
        Assert.Throws<StartupRefusalNotProvenException>(() => StartupRefusal.Evaluate(new ObjectDisposedException("HttpClient"), [refusal], Matches, "the floor refusal"));
        Assert.Throws<StartupRefusalNotProvenException>(() => StartupRefusal.Evaluate(new ObjectDisposedException("IServiceProvider", new IOException("x")), [refusal], Matches, "the floor refusal"));
    }

    [Fact]
    public void ADirectMatchThatDisagreesWithTheLoggedFailureIsRejected()
    {
        var thrown = new InvalidOperationException(RequiredFloorMissing);
        var logged = new InvalidOperationException("Signing:Provider is not configured.");
        Assert.Throws<StartupRefusalNotProvenException>(() => StartupRefusal.Evaluate(thrown, [logged], e => StartupRefusal.Flatten(e).Contains(RequiredFloorMissing, StringComparison.Ordinal), "the floor refusal"));
    }
}
