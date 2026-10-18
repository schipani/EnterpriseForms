using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AuthorizationAuthority.Application.Common;

/// <summary>Deterministic JSON for digests, journal bodies and payload canonicalization (snake_case, no indentation, invariant).</summary>
public static class CanonicalJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static byte[] SerializeBytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static byte[] Digest(ReadOnlySpan<byte> utf8) => SHA256.HashData(utf8);

    public static byte[] Digest(string text) => SHA256.HashData(Encoding.UTF8.GetBytes(text));

    private static readonly JsonSerializerOptions StrictOptions = new(Options) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    /// <summary>Strict payload binding: unknown members are rejected (no mass assignment).</summary>
    public static T Deserialize<T>(JsonElement element)
    {
        try
        {
            return element.Deserialize<T>(StrictOptions) ?? throw new AuthorityException(AuthorityErrorCodes.InvalidCommand, 422, false, "command_payload missing");
        }
        catch (JsonException ex)
        {
            throw new AuthorityException(AuthorityErrorCodes.InvalidCommand, 422, false, "command_payload invalid: " + ex.Message);
        }
    }
}
