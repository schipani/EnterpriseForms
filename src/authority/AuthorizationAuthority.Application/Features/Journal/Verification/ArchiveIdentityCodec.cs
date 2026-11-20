using System.Text.Json;
using System.Text.Json.Serialization;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;

namespace AuthorizationAuthority.Application.Features.Journal.Verification;

/// <summary>
/// SEC-BL-01 WP-2 (contract §6.1, AS-1): canonical bytes of the archive-identity sentinel, shared by every archive adapter. A sentinel
/// identifies the archive instance being read; it proves nothing about completeness or freshness (AS-4).
/// </summary>
public static class ArchiveIdentityCodec
{
    private static readonly JsonSerializerOptions Strict = new(CanonicalJson.Options) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    /// <summary>A new identity with a random instance identifier (UUIDv7, hex) and UTC creation time.</summary>
    public static ArchiveIdentity NewIdentity(DateTimeOffset createdOnUtc) =>
        new(ArchiveIdentity.SchemaV1, "archive-" + Guid.CreateVersion7(createdOnUtc).ToString("N"), createdOnUtc.ToUniversalTime());

    public static byte[] Serialize(ArchiveIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (Validate(identity) is { } problem)
        {
            throw new ArgumentException("invalid archive identity: " + problem, nameof(identity));
        }

        return CanonicalJson.SerializeBytes(identity);
    }

    /// <summary>The identity in <paramref name="bytes"/>, or null when they are not a canonical v1 sentinel (the caller treats that as a mismatch, never as a match).</summary>
    public static ArchiveIdentity? TryParse(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var identity = JsonSerializer.Deserialize<ArchiveIdentity>(bytes, Strict);
            return identity is not null && Validate(identity) is null && CanonicalJson.SerializeBytes(identity).AsSpan().SequenceEqual(bytes) ? identity : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string? Validate(ArchiveIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!string.Equals(identity.IdentitySchema, ArchiveIdentity.SchemaV1, StringComparison.Ordinal))
        {
            return "unsupported identity schema";
        }

        if (string.IsNullOrEmpty(identity.ArchiveInstanceId) || identity.ArchiveInstanceId.Length > 128 || !identity.ArchiveInstanceId.All(c => c is >= '!' and <= '~'))
        {
            return "archive instance id must be 1..128 printable ASCII characters";
        }

        return identity.IdentityCreatedOnUtc.Offset == TimeSpan.Zero ? null : "creation time must be UTC";
    }
}
