using System.Net;
using System.Text;
using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Harness;

/// <summary>
/// C8 M2 Stage 3 real-stack harness: registers the three synthetic console operators (whose ES256 keys live in Vault Transit and were
/// created by the stack's vault-console-init service) through the Authority's own two-operator provisioning, performed by the
/// harness operators of the Stage 1-2 tests; suspends and reinstates through the platform plane; reads Vault as the console's token
/// or the dev-mode root token. Disposable security-test stack only.
/// </summary>
public static class PlatformConsoleHarness
{
    public const string ConsoleVaultToken = "sectest-platform-console-vault-token-0001";
    private const string Justification = "Stage B harness: console operators of this disposable stack (C8 M2 Stage 3)";
    private static readonly (string Subject, string DisplayName, string Key)[] ConsoleOperators =
    [
        ("sectest-console-operator-dana", "Dana Console Operator (synthetic)", "sectest-console-key-dana"),
        ("sectest-console-operator-ellis", "Ellis Console Operator (synthetic)", "sectest-console-key-ellis"),
        ("sectest-console-operator-frankie", "Frankie Console Operator (synthetic)", "sectest-console-key-frankie"),
    ];

    private static readonly SemaphoreSlim Gate = new(1, 1);

    public sealed record HarnessOperator(Guid Id, string Subject, string KeyId, string PrivateKey)
    {
        public string Assertion => TestIdentity.PlatformOperatorAssertion(Subject);
    }

    /// <summary>Platform operator ids of the three console operators (registered on first use), after the console reports ready.</summary>
    public static async Task<IReadOnlyList<Guid>> EnsureConsoleOperatorsAsync(StageBFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        await Gate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            await WaitConsoleReadyAsync(fixture);
            var ops = await HarnessOperatorsAsync(fixture);
            var registered = await RegistryAsync(fixture);
            foreach (var (subject, name, key) in ConsoleOperators)
            {
                if (registered.Any(r => r.GetProperty("subject_identifier").GetString() == subject))
                {
                    continue;
                }

                var operationId = Guid.CreateVersion7();
                var candidate = new { issuer_uri = TestIdentity.PlatformOperatorIssuer, subject_identifier = subject, display_name = name, evidence_kind = "SyntheticDevelopmentJws", synthetic_key_identifier = key, synthetic_public_key_base64 = VaultPublicKey(key) };
                await TwoOperatorChangeAsync(fixture, ops, operationId, new { operation_id = operationId, change = "Provision", candidate, justification = Justification, dry_run = true });
            }

            registered = await RegistryAsync(fixture);
            return ConsoleOperators.Select(c =>
            {
                var row = registered.Single(r => r.GetProperty("subject_identifier").GetString() == c.Subject);
                Assert.Equal(c.Key, row.GetProperty("synthetic_key_identifier").GetString());
                Assert.Equal("Active", row.GetProperty("status").GetString());
                return row.GetProperty("platform_operator_id").GetGuid();
            }).ToList();
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task SuspendAsync(StageBFixture fixture, Guid target)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        var ops = await HarnessOperatorsAsync(fixture);
        var operationId = Guid.CreateVersion7();
        var preview = await fixture.Authority.PlatformAsync(HttpMethod.Post, $"/internal/v1/platform/operators/{target:D}/suspend", ops[0].Assertion, new { operation_id = operationId, justification = Justification, dry_run = true });
        Assert.True(preview.IsSuccess, preview.Body);
        var done = await fixture.Authority.PlatformAsync(HttpMethod.Post, $"/internal/v1/platform/operators/{target:D}/suspend", ops[0].Assertion, new
        {
            operation_id = operationId, justification = Justification, dry_run = false, evidence_kind = "SyntheticDevelopmentJws",
            evidence = TestIdentity.PlatformOperatorEvidence(ops[0].KeyId, ops[0].PrivateKey, ops[0].Id, "suspend", "PlatformOperatorSuspension", operationId, Guid.Empty, preview.Root.GetProperty("statement_sha256").GetString()!, Justification),
        });
        Assert.True(done.IsSuccess, done.Body);
    }

    public static async Task ReinstateAsync(StageBFixture fixture, Guid target)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        var ops = await HarnessOperatorsAsync(fixture);
        var operationId = Guid.CreateVersion7();
        await TwoOperatorChangeAsync(fixture, ops, operationId, new { operation_id = operationId, change = "Reinstate", target_platform_operator_id = target, justification = Justification, dry_run = true });
    }

    /// <summary>Waits until every platform journal event is in the independent archive (a later rollback check depends on it).</summary>
    public static async Task WaitAnchoredAsync(StageBFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        for (var attempt = 0; attempt < 60; attempt++)
        {
            if (fixture.Stack.AuthorityObserverSql("SELECT count(*) FROM platform_journal_entries WHERE archive_delivery_state <> 'Anchored';").Trim() == "0")
            {
                return;
            }

            await Task.Delay(1000, TestContext.Current.CancellationToken);
        }

        Assert.Fail("platform journal events were not anchored within 60 s");
    }

    /// <summary>Runs a Vault CLI command in the vault container with the given token (synthetic tokens of this disposable stack only).</summary>
    public static string Vault(string command, string token, bool throwOnError = true)
    {
        var shell = Convert.ToBase64String(Encoding.UTF8.GetBytes($"VAULT_ADDR=http://127.0.0.1:8200 VAULT_TOKEN='{token}' {command} 2>&1"));
        return new ComposeStack().Compose($"exec -T vault sh -c \"echo {shell} | base64 -d | sh\"", 60, throwOnError);
    }

    public static string RootToken()
    {
        foreach (var file in new[] { ".env", "env.example" })
        {
            var path = Path.Combine(ComposeStack.DockerDirectory, file);
            if (File.Exists(path))
            {
                var line = File.ReadAllLines(path).FirstOrDefault(l => l.StartsWith("VAULT_DEV_ROOT_TOKEN=", StringComparison.Ordinal));
                if (line is not null)
                {
                    return line["VAULT_DEV_ROOT_TOKEN=".Length..].Trim();
                }
            }
        }

        throw new InvalidOperationException("VAULT_DEV_ROOT_TOKEN not found in infrastructure/docker/.env or env.example");
    }

    private static string VaultPublicKey(string key)
    {
        var data = JsonDocument.Parse(Vault($"vault read -format=json transit/keys/{key}", RootToken())).RootElement.GetProperty("data");
        Assert.Equal("ecdsa-p256", data.GetProperty("type").GetString());
        Assert.False(data.GetProperty("exportable").GetBoolean());
        var pem = data.GetProperty("keys").GetProperty("1").GetProperty("public_key").GetString()!;
        return string.Concat(pem.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("-----", StringComparison.Ordinal)));
    }

    private static async Task TwoOperatorChangeAsync(StageBFixture fixture, IReadOnlyList<HarnessOperator> ops, Guid operationId, object dryRunBody)
    {
        var preview = await fixture.Authority.PlatformAsync(HttpMethod.Post, "/internal/v1/platform/operators/proposals", ops[0].Assertion, dryRunBody);
        Assert.True(preview.IsSuccess && preview.Root.GetProperty("eligible").GetBoolean(), preview.Body);
        var sha = preview.Root.GetProperty("statement_sha256").GetString()!;
        var body = JsonSerializer.SerializeToNode(dryRunBody)!.AsObject();
        body["dry_run"] = false;
        body["evidence_kind"] = "SyntheticDevelopmentJws";
        body["evidence"] = TestIdentity.PlatformOperatorEvidence(ops[0].KeyId, ops[0].PrivateKey, ops[0].Id, "propose", "PlatformOperatorChange", operationId, Guid.Empty, sha, Justification);
        var proposed = await fixture.Authority.PlatformAsync(HttpMethod.Post, "/internal/v1/platform/operators/proposals", ops[0].Assertion, body);
        Assert.True(proposed.IsSuccess, proposed.Body);
        var confirmed = await fixture.Authority.PlatformAsync(HttpMethod.Post, $"/internal/v1/platform/operators/proposals/{operationId:D}/confirm", ops[1].Assertion, new
        {
            expected_statement_sha256 = sha, justification = Justification, dry_run = false, evidence_kind = "SyntheticDevelopmentJws",
            evidence = TestIdentity.PlatformOperatorEvidence(ops[1].KeyId, ops[1].PrivateKey, ops[1].Id, "confirm", "PlatformOperatorChange", operationId, Guid.Empty, sha, Justification),
        });
        Assert.True(confirmed.IsSuccess, confirmed.Body);
    }

    private static async Task<List<JsonElement>> RegistryAsync(StageBFixture fixture)
    {
        var status = await fixture.Authority.OperatorAsync(HttpMethod.Get, "/internal/v1/operator/platform-operators", null);
        Assert.True(status.IsSuccess, status.Body);
        return status.Root.GetProperty("operators").EnumerateArray().Select(e => e.Clone()).ToList();
    }

    /// <summary>The harness trio of the Stage 1-2 tests (registered by the recovery ceremony on a fresh stack).</summary>
    private static async Task<IReadOnlyList<HarnessOperator>> HarnessOperatorsAsync(StageBFixture fixture)
    {
        var status = await fixture.Authority.OperatorAsync(HttpMethod.Get, "/internal/v1/operator/platform-operators", null);
        Assert.True(status.IsSuccess, status.Body);
        var registered = status.Root.GetProperty("operators").EnumerateArray().ToList();
        var missing = TestIdentity.SyntheticPlatformOperators.Where(t => registered.All(r => r.GetProperty("subject_identifier").GetString() != t.Subject)).ToList();
        if (missing.Count > 0)
        {
            Assert.True(status.Root.GetProperty("active_operator_count").GetInt32() < 2, "the registry has other active operators but not the harness trio: " + status.Body);
            var body = new
            {
                operation_id = Guid.CreateVersion7(),
                operators = missing.Select(t => new { issuer_uri = TestIdentity.PlatformOperatorIssuer, subject_identifier = t.Subject, display_name = t.DisplayName, evidence_kind = "SyntheticDevelopmentJws", synthetic_key_identifier = t.KeyId, synthetic_public_key_base64 = TestIdentity.PlatformOperatorPublicKey(t.PrivateKeyBase64) }).ToArray(),
                justification = "Stage B harness: first platform operators of this disposable stack",
                incident_reference = "SECTEST-C8-PO",
                dry_run = true,
            };
            var preview = await fixture.Authority.OperatorAsync(HttpMethod.Post, "/internal/v1/operator/platform-operators/bootstrap", body);
            Assert.True(preview.IsSuccess && preview.Root.GetProperty("eligible").GetBoolean(), preview.Body);
            var done = await fixture.Authority.OperatorAsync(HttpMethod.Post, "/internal/v1/operator/platform-operators/bootstrap", new
            {
                body.operation_id, body.operators, body.justification, body.incident_reference, dry_run = false,
                approvals = new[] { TestIdentity.OperatorApproval(preview.Root, "test-synthetic-approver-1"), TestIdentity.OperatorApproval(preview.Root, "test-synthetic-approver-2") },
            });
            Assert.True(done.IsSuccess, done.Body);
            status = await fixture.Authority.OperatorAsync(HttpMethod.Get, "/internal/v1/operator/platform-operators", null);
            registered = status.Root.GetProperty("operators").EnumerateArray().ToList();
        }

        return TestIdentity.SyntheticPlatformOperators.Select(t =>
        {
            var row = registered.Single(r => r.GetProperty("subject_identifier").GetString() == t.Subject);
            Assert.Equal("Active", row.GetProperty("status").GetString());
            return new HarnessOperator(row.GetProperty("platform_operator_id").GetGuid(), t.Subject, t.KeyId, t.PrivateKeyBase64);
        }).ToList();
    }

    private static async Task WaitConsoleReadyAsync(StageBFixture fixture)
    {
        using var client = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:5192"), Timeout = TimeSpan.FromSeconds(5) };
        var last = string.Empty;
        for (var attempt = 0; attempt < 60; attempt++)
        {
            try
            {
                using var response = await client.GetAsync(new Uri("/console-api/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);
                last = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (HttpRequestException ex)
            {
                last = ex.Message;
            }

            await Task.Delay(1000, TestContext.Current.CancellationToken);
        }

        throw new InvalidOperationException("The Platform Console (127.0.0.1:5192) is not ready: " + last + ". Start it with the security-test stack: docker compose -f compose.yaml -f compose.security-test.yaml --env-file .env up -d --build --wait platform-console. Stack: " + fixture.CommitSha);
    }
}
