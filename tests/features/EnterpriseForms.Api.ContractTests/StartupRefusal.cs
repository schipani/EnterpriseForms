using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;
using Xunit.Sdk;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// Observes a host that must refuse to start, without depending on the thread timing of <c>Microsoft.AspNetCore.Mvc.Testing</c>.
/// <para>
/// The race: for a minimal-hosting entry point the factory runs the application's <c>Main</c> on its own thread, and that thread's
/// <c>app.Run()</c> disposes the host once <c>Host.StartAsync</c> throws. The factory's <c>DeferredHost.StartAsync</c> asks the host's
/// service provider for <c>IHostApplicationLifetime</c> before it waits for the start result. If the application thread has already
/// failed and disposed the provider, the factory throws <see cref="ObjectDisposedException"/> ("IServiceProvider") and the original
/// refusal is never surfaced. It is lost only in the test's view; the host still refused to start.
/// </para>
/// <para>
/// Normal path: the exception the factory throws must itself be the expected refusal. Race path: a disposed-service-provider exception
/// thrown by <c>DeferredHost.StartAsync</c> is accepted only together with independent evidence, namely the exception the host logged
/// with its own "Hosting failed to start" event (logged before the host is disposed), and that exception must be the expected refusal.
/// Anything else fails: another startup failure, a different refusal, missing evidence, or a host that starts.
/// </para>
/// </summary>
internal static class StartupRefusal
{
    /// <summary>The category and event of <c>Microsoft.Extensions.Hosting.Internal.Host</c>'s "Hosting failed to start" entry.</summary>
    internal const string HostLoggerCategory = "Microsoft.Extensions.Hosting.Internal.Host";
    internal const int HostedServiceStartupFaultedEventId = 11;
    internal const string HostedServiceStartupFaultedEventName = "HostedServiceStartupFaulted";

    private const string DeferredHostStartFrame = "Microsoft.AspNetCore.Mvc.Testing.DeferredHostBuilder.DeferredHost.StartAsync";

    /// <summary>The refusal that was proven, the exception the factory threw, and whether the documented framework race was resolved.</summary>
    internal sealed record Observation(Exception Refusal, Exception Thrown, bool ResolvedFrameworkRace);

    /// <summary>Asserts the host refuses to start with a failure whose message chain contains <paramref name="expectedText"/>.</summary>
    internal static Observation Assert<TEntryPoint>(WebApplicationFactory<TEntryPoint> factory, string expectedText)
        where TEntryPoint : class =>
        Assert(factory, e => Flatten(e).Contains(expectedText, StringComparison.Ordinal), $"a startup refusal whose message chain contains \"{expectedText}\"");

    /// <summary>Asserts the host refuses to start with a failure that satisfies <paramref name="isExpectedRefusal"/>.</summary>
    internal static Observation Assert<TEntryPoint>(WebApplicationFactory<TEntryPoint> factory, Func<Exception, bool> isExpectedRefusal, string expectation)
        where TEntryPoint : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        var capture = new HostStartupFailureCapture();
        var observed = factory.WithWebHostBuilder(b => b.ConfigureLogging(logging =>
        {
            logging.AddProvider(capture);
            // Provider-specific rule: deployment log levels cannot filter out the host's own startup-failure entry for this provider.
            logging.AddFilter<HostStartupFailureCapture>((category, _) => string.Equals(category, HostLoggerCategory, StringComparison.Ordinal));
        }));

        Exception thrown;
        try
        {
            using var client = observed.CreateClient();
            throw new StartupRefusalNotProvenException($"Expected {expectation}, but the host started.");
        }
        catch (StartupRefusalNotProvenException)
        {
            throw;
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        return Evaluate(thrown, capture.Failures, isExpectedRefusal, expectation);
    }

    /// <summary>The decision, separated from the host so the evidence rules can be tested directly.</summary>
    internal static Observation Evaluate(Exception thrown, IReadOnlyCollection<Exception> loggedStartupFailures, Func<Exception, bool> isExpectedRefusal, string expectation)
    {
        ArgumentNullException.ThrowIfNull(thrown);
        ArgumentNullException.ThrowIfNull(loggedStartupFailures);
        ArgumentNullException.ThrowIfNull(isExpectedRefusal);

        if (isExpectedRefusal(thrown))
        {
            // Normal path. When the host also logged its startup failure, that is the same failure and must agree.
            if (loggedStartupFailures.Count > 0 && !loggedStartupFailures.All(isExpectedRefusal))
            {
                throw new StartupRefusalNotProvenException($"Expected {expectation}. The thrown exception matches, but the host logged a different startup failure.{Describe(thrown, loggedStartupFailures)}");
            }

            return new Observation(thrown, thrown, ResolvedFrameworkRace: false);
        }

        if (!IsDeferredHostDisposedProvider(thrown))
        {
            throw new StartupRefusalNotProvenException($"Expected {expectation}, but the host failed differently.{Describe(thrown, loggedStartupFailures)}");
        }

        if (loggedStartupFailures.Count == 0)
        {
            throw new StartupRefusalNotProvenException($"Expected {expectation}. The factory reported a disposed service provider and the host logged no startup failure, so the refusal is not proven.{Describe(thrown, loggedStartupFailures)}");
        }

        if (loggedStartupFailures.Count != 1 || !isExpectedRefusal(loggedStartupFailures.First()))
        {
            throw new StartupRefusalNotProvenException($"Expected {expectation}. The factory reported a disposed service provider, but the startup failure the host logged is not the expected refusal.{Describe(thrown, loggedStartupFailures)}");
        }

        return new Observation(loggedStartupFailures.First(), thrown, ResolvedFrameworkRace: true);
    }

    /// <summary>Exactly the documented race: the disposed root provider, thrown from the factory's deferred host start.</summary>
    internal static bool IsDeferredHostDisposedProvider(Exception exception) =>
        exception is ObjectDisposedException { ObjectName: "IServiceProvider", InnerException: null } disposed
        && disposed.StackTrace?.Contains(DeferredHostStartFrame, StringComparison.Ordinal) == true;

    internal static string Flatten(Exception exception)
    {
        var messages = new List<string>();
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            messages.Add(current.Message);
        }

        return string.Join(" | ", messages);
    }

    private static string Describe(Exception thrown, IReadOnlyCollection<Exception> logged) =>
        $"{Environment.NewLine}Thrown: {thrown}{Environment.NewLine}Logged startup failures ({logged.Count}):{string.Concat(logged.Select(e => Environment.NewLine + e))}";

    /// <summary>Records the exception of each "Hosting failed to start" entry; written on the application thread before disposal.</summary>
    internal sealed class HostStartupFailureCapture : ILoggerProvider
    {
        private readonly ConcurrentQueue<Exception> failures = new();

        public IReadOnlyCollection<Exception> Failures => failures.ToArray();

        public ILogger CreateLogger(string categoryName) => new CaptureLogger(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class CaptureLogger(HostStartupFailureCapture owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (exception is not null && string.Equals(category, HostLoggerCategory, StringComparison.Ordinal)
                    && eventId.Id == HostedServiceStartupFaultedEventId && string.Equals(eventId.Name, HostedServiceStartupFaultedEventName, StringComparison.Ordinal))
                {
                    owner.failures.Enqueue(exception);
                }
            }
        }
    }
}

/// <summary>A startup refusal was expected but is not proven; carries the thrown and logged exceptions.</summary>
internal sealed class StartupRefusalNotProvenException(string message) : XunitException(message);
