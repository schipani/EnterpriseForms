using System.Globalization;
using System.Text.Json;
using Xunit;

namespace EnterpriseForms.SecurityTests.Harness;

/// <summary>
/// Shared stack fixture: verifies the security-test stack is up and ready, records commit/image fingerprints, and on
/// disposal assembles artifacts/security/gate-a-evidence.json from the per-test evidence files (missing or failed tests
/// are recorded as such — a missing test can never read as PASS).
/// </summary>
public sealed class StageAFixture : IAsyncLifetime
{
    public const string ApiBaseAddress = "http://127.0.0.1:5180";
    public const string AuthorityBaseAddress = "http://127.0.0.1:5190";

    public ComposeStack Stack { get; } = new();

    public BusinessApiClient Api { get; } = new(ApiBaseAddress);

    public AuthorityClient Authority { get; } = new(AuthorityBaseAddress);

    public string CommitSha { get; private set; } = string.Empty;

    public string ImageFingerprint { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        CommitSha = ComposeStack.GitCommit();
        var status = Stack.Compose("ps --format json", 60, throwOnError: false);
        if (!status.Contains("\"authority\"", StringComparison.Ordinal) || !status.Contains("\"api\"", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The security-test stack is not running. Start it with: docker compose -f compose.yaml -f compose.security-test.yaml --env-file .env up -d --build --wait (infrastructure/docker).");
        }

        await Authority.WaitReadyAsync(TimeSpan.FromSeconds(90)).ConfigureAwait(false);
        var apiReady = await Api.GetAsync("/health/ready", "x").ConfigureAwait(false);
        if (!apiReady.IsSuccess)
        {
            throw new InvalidOperationException("business API is not ready: " + apiReady.Body);
        }

        ImageFingerprint = Stack.ImageFingerprint();
    }

    public ValueTask DisposeAsync()
    {
        WriteManifest();
        return ValueTask.CompletedTask;
    }

    private void WriteManifest()
    {
        var evidenceDirectory = Path.Combine(ComposeStack.RepositoryRoot, "artifacts", "security", "evidence");
        var results = new List<object>();
        for (var i = 1; i <= 12; i++)
        {
            var testId = "SEC-P" + i.ToString("00", CultureInfo.InvariantCulture);
            var path = Path.Combine(evidenceDirectory, testId + ".json");
            if (!File.Exists(path))
            {
                results.Add(new { test_id = testId, outcome = "NOT_RUN", evidence_path = "evidence/" + testId + ".json", evidence_sha256 = new string('0', 64), commit_sha = CommitSha, image_fingerprint = ImageFingerprint, executed_at_utc = Evidence.Utc(DateTimeOffset.UtcNow) });
                continue;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            results.Add(new
            {
                test_id = testId,
                outcome = root.GetProperty("outcome").GetString(),
                evidence_path = "evidence/" + testId + ".json",
                evidence_sha256 = Evidence.Sha256Hex(path),
                commit_sha = root.GetProperty("commit_sha").GetString(),
                image_fingerprint = root.GetProperty("image_fingerprint").GetString(),
                executed_at_utc = Evidence.Utc(DateTimeOffset.Parse(root.GetProperty("executed_at_utc").GetString()!, CultureInfo.InvariantCulture)),
                authority_revision = root.TryGetProperty("authority_revision", out var ar) && ar.ValueKind == JsonValueKind.Number ? ar.GetInt64() : (long?)null,
                content_revision = root.TryGetProperty("content_revision", out var cr) && cr.ValueKind == JsonValueKind.Number ? cr.GetInt64() : (long?)null,
            });
        }

        var manifest = new
        {
            schema_version = "1.0",
            gate_id = "PB-03-STAGE-A",
            commit_sha = CommitSha,
            image_fingerprint = ImageFingerprint,
            environment_kind = "disposable-docker",
            executed_at_utc = Evidence.Utc(DateTimeOffset.UtcNow),
            results,
        };
        var manifestPath = Path.Combine(ComposeStack.RepositoryRoot, "artifacts", "security", "gate-a-evidence.json");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class StageACollection : ICollectionFixture<StageAFixture>
{
    public const string Name = "stage-a-stack";
}
