using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// C8 M4 S1 PERF-01 (Design B revision 2 §2.10, B-Q9): end-to-end Authority authorization latency through the HTTP check
/// endpoints, open loop, on the security-test stack, reported as p50/p95/p99 per load level. It includes every Authority cost
/// (gate, AuthorityDb reads, the S1 marker reads, SpiceDB). The report is written to
/// <c>artifacts/perf/s1-authorization-latency-{label}.txt</c> (label from <c>S1_PERF_LABEL</c>, default "current"). The test fails only
/// on errors or quarantines, never on a latency figure: the figures are evidence for the owner's acceptance, measured on a
/// small local dataset and not a substitute for the Azure staging benchmark.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class AuthorizationLatencyTests(StageBFixture fixture)
{
    [Fact]
    public async Task AuthorizationCheckLatencyIsMeasuredAtFiftyAndOneHundredFiftyRequestsPerSecond()
    {
        var s = new StageBScenario(fixture, "perf01-latency");
        await s.BootstrapAsync();
        var label = Environment.GetEnvironmentVariable("S1_PERF_LABEL") is { Length: > 0 } l ? l : "current";
        var lines = new List<string>
        {
            $"# PERF-01 {label} - commit {ComposeStack.GitCommit()} - stack {fixture.Stack.ImageFingerprint()} - {DateTimeOffset.UtcNow:O}",
            "# open loop, Authority HTTP check from the host; single = one check per request, batch25 = /check-batch with 25 checks",
        };
        var single = new { organization_id = s.OrganizationId, resource = new { resource_kind = "Organization", resource_id = s.OrganizationId }, requested_action = "AdministerSecurity" };
        var batch = new { checks = Enumerable.Range(0, 25).Select(_ => single).ToArray() };
        var token = s.Token(s.Administrator);

        // Warm-up.
        for (var i = 0; i < 50; i++)
        {
            Assert.True((await fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/authorization/check", TestIdentity.WorkloadToken(), single, token)).IsSuccess);
        }

        foreach (var (name, path, body, rps, seconds) in new (string, string, object, int, int)[]
        {
            ("single@50rps", "/internal/v1/authorization/check", single, 50, 30),
            ("single@150rps", "/internal/v1/authorization/check", single, 150, 30),
            ("batch25@50rps", "/internal/v1/authorization/check-batch", batch, 50, 30),
        })
        {
            var latencies = new ConcurrentBag<double>();
            var failures = new ConcurrentDictionary<string, int>();
            var inflight = new List<Task>();
            var clock = Stopwatch.StartNew();
            var interval = TimeSpan.FromSeconds(1.0 / rps);
            long issued = 0;
            while (clock.Elapsed < TimeSpan.FromSeconds(seconds))
            {
                var wait = TimeSpan.FromTicks(interval.Ticks * issued) - clock.Elapsed;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, TestContext.Current.CancellationToken);
                }

                inflight.Add(Task.Run(async () =>
                {
                    var sw = Stopwatch.StartNew();
                    var result = await fixture.Authority.SendAsync(HttpMethod.Post, path, TestIdentity.WorkloadToken(), body, token);
                    latencies.Add(sw.Elapsed.TotalMilliseconds);
                    if (!result.IsSuccess)
                    {
                        failures.AddOrUpdate(((int)result.Status).ToString(CultureInfo.InvariantCulture) + ":" + result.ErrorCode, 1, (_, c) => c + 1);
                    }
                }, TestContext.Current.CancellationToken));
                issued++;
            }

            await Task.WhenAll(inflight);
            var sorted = latencies.Order().ToList();
            double P(double q) => sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(q * sorted.Count) - 1)];
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"{name,-14} n={sorted.Count,6} achieved_rps={issued / clock.Elapsed.TotalSeconds,6:F1} p50={P(0.50),7:F2} p95={P(0.95),7:F2} p99={P(0.99),7:F2} ms failures={(failures.IsEmpty ? "none" : string.Join(",", failures.Select(f => f.Key + "=" + f.Value)))}"));
            Assert.True(failures.IsEmpty, name + ": " + string.Join(", ", failures.Select(f => f.Key + "=" + f.Value)));
        }

        var gate = await fixture.Authority.GateAsync(s.OrganizationId);
        Assert.Equal("Ready", gate.GetProperty("projection_gate_state").GetString());
        var directory = Path.Combine(ComposeStack.RepositoryRoot, "artifacts", "perf");
        Directory.CreateDirectory(directory);
        await File.WriteAllLinesAsync(Path.Combine(directory, $"s1-authorization-latency-{label}.txt"), lines, TestContext.Current.CancellationToken);
    }
}
