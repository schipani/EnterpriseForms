using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EnterpriseForms.SecurityTests.Harness;

/// <summary>
/// Per-test evidence recorder. Writes artifacts/security/evidence/SEC-Pxx.json with ordered, sanitized observations
/// (status codes, error codes, authority revisions, gate states, SQL rows affected) and the final verdict. The fixture
/// assembles artifacts/security/gate-a-evidence.json (security-evidence.schema.json) from these files. Tokens, keys and
/// field plaintext are never recorded.
/// </summary>
public sealed class Evidence(string testId, string scenario)
{
    private readonly List<object> observations = [];
    private readonly DateTimeOffset startedAt = DateTimeOffset.UtcNow;

    public string TestId { get; } = testId;

    public long? AuthorityRevision { get; set; }

    public long? ContentRevision { get; set; }

    public void Observe(string step, object detail)
    {
        observations.Add(new { at = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), step, detail });
    }

    public void ObserveHttp(string step, HttpResult result, params string[] fields)
    {
        var extracted = new Dictionary<string, object?>();
        if (result.Json is not null && result.Json.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var field in fields)
            {
                if (result.Json.RootElement.TryGetProperty(field, out var value))
                {
                    extracted[field] = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
                }
            }
        }

        observations.Add(new { at = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), step, status = (int)result.Status, error_code = result.ErrorCode, fields = extracted });
    }

    public string Write(string outcome, string commitSha, string imageFingerprint, string? failureDetail = null)
    {
        var directory = Path.Combine(ComposeStack.RepositoryRoot, "artifacts", "security", "evidence");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, TestId + ".json");
        var document = new
        {
            test_id = TestId,
            scenario,
            outcome,
            commit_sha = commitSha,
            image_fingerprint = imageFingerprint,
            started_at_utc = startedAt.ToString("O", CultureInfo.InvariantCulture),
            executed_at_utc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            authority_revision = AuthorityRevision,
            content_revision = ContentRevision,
            failure_detail = failureDetail,
            observations,
        };
        File.WriteAllText(path, JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    public static string Sha256Hex(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    public static string Utc(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static string Sanitize(string text) => text.Length <= 400 ? text : text[..400] + "…";

    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
}
