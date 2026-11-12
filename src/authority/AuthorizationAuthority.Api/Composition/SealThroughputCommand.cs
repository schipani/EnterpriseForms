using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.SearchIndex;

namespace AuthorizationAuthority.Api.Composition;

/// <summary>
/// IX-7 ST-PERF-7 harness: <c>dotnet AuthorizationAuthority.Api.dll --measure-seal-throughput [--count N] [--concurrency C]</c>. Builds the
/// configured seal signer exactly as the Authority does (the same composition, provider policy and pinned key), signs N synthetic messages
/// with C concurrent callers (Development/Testing only), verifies every signature locally against the pinned public key, and prints one JSON report: operations per
/// second, latency p50/p95/p99/max, failures by kind (throttling counted separately), the provider and key version. Synthetic messages only
/// (random bytes with a fixed domain prefix that no seal message uses); nothing is stored and no organization is touched. Runs against
/// Vault Transit locally and against Azure Managed HSM in staging; the staging run is the evidence ST-PERF-7 asks for and has not been made.
/// </summary>
public static class SealThroughputCommand
{
    private static readonly byte[] Prefix = "DynamicForms/SealThroughputProbe/v1\n"u8.ToArray();

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public const string Flag = "--measure-seal-throughput";

    /// <summary>Whether the harness was requested; removes its arguments so the host's command-line configuration never sees them.</summary>
    public static bool Requested(string[] args, out int count, out int concurrency, out string[] hostArguments)
    {
        ArgumentNullException.ThrowIfNull(args);
        count = Argument(args, "--count", 200, 1, 100_000);
        concurrency = Argument(args, "--concurrency", 4, 1, 64);
        var requested = Array.IndexOf(args, Flag) >= 0;
        var remaining = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == Flag)
            {
                continue;
            }

            if (args[i] is "--count" or "--concurrency")
            {
                i++;
                continue;
            }

            remaining.Add(args[i]);
        }

        hostArguments = requested ? remaining.ToArray() : args;
        return requested;
    }

    /// <summary>Runs the measurement on the built host's services. Development/Testing only (a staging run needs its own approval).</summary>
    public static int Run(IServiceProvider services, IHostEnvironment environment, int count, int concurrency)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (!(environment.IsDevelopment() || environment.IsEnvironment("Testing")))
        {
            Console.Error.WriteLine("--measure-seal-throughput is a Development/Testing measurement; refusing in " + environment.EnvironmentName);
            return 3;
        }

        return RunAsync(services, count, concurrency).GetAwaiter().GetResult();
    }

    private static int Argument(string[] args, string name, int fallback, int minimum, int maximum)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? Math.Clamp(value, minimum, maximum) : fallback;
    }

    public static async Task<int> RunAsync(IServiceProvider services, int count, int concurrency)
    {
        ArgumentNullException.ThrowIfNull(services);
        // Through the same pin-checking gate as every seal (the provider's public key must equal the pin; every signature is verified
        // against the pin), plus an independent local verification here.
        var gate = services.GetRequiredService<SearchSealSigningGate>();
        var seal = services.GetRequiredService<SearchSealOptions>();
        var provider = seal.Provider.ToString();
        var pinned = seal.PinnedKeys.SingleOrDefault(k => k.KeyName == gate.KeyName && k.KeyVersion == gate.KeyVersion);
        using var verifier = ECDsa.Create();
        if (pinned is null)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { outcome = "NO_PINNED_KEY", provider }));
            return 2;
        }

        verifier.ImportSubjectPublicKeyInfo(Convert.FromBase64String(pinned.SubjectPublicKeyInfoBase64), out _);
        var latencies = new System.Collections.Concurrent.ConcurrentBag<double>();
        var failures = new System.Collections.Concurrent.ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var next = -1;
        var clock = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, concurrency).Select(async _ =>
        {
            while (Interlocked.Increment(ref next) < count)
            {
                var message = Prefix.Concat(RandomNumberGenerator.GetBytes(64)).ToArray();
                var started = Stopwatch.GetTimestamp();
                try
                {
                    var signature = await gate.SignAsync(message, CancellationToken.None).ConfigureAwait(false);
                    latencies.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    if (!verifier.VerifyData(message, signature.Signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                    {
                        failures.AddOrUpdate("SIGNATURE_DOES_NOT_VERIFY", 1, (_, n) => n + 1);
                    }
                }
                catch (SearchSealUnavailableException ex)
                {
                    failures.AddOrUpdate(ex.Message.Contains("429", StringComparison.Ordinal) ? "THROTTLED" : "UNAVAILABLE", 1, (_, n) => n + 1);
                }
            }
        })).ConfigureAwait(false);
        var elapsed = clock.Elapsed.TotalSeconds;
        var sorted = latencies.Order().ToList();
        double P(double p) => sorted.Count == 0 ? 0 : Math.Round(sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)], 3);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            kind = "IX-7 ST-PERF-7 seal signing throughput (synthetic messages)",
            provider,
            key_name = gate.KeyName,
            key_version = gate.KeyVersion,
            requested = count,
            concurrency,
            signed = sorted.Count,
            failures,
            seconds = Math.Round(elapsed, 3),
            operations_per_second = elapsed > 0 ? Math.Round(sorted.Count / elapsed, 1) : 0,
            latency_ms = new { p50 = P(0.5), p95 = P(0.95), p99 = P(0.99), max = sorted.Count == 0 ? 0 : Math.Round(sorted[^1], 3) },
        }, Indented));
        return failures.IsEmpty ? 0 : 1;
    }
}
