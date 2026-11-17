using System.Security.Cryptography;
using System.Text;
using AuthorizationAuthority.Domain.Features.SearchAccess;

namespace AuthorizationAuthority.Application.Features.SearchAccess;

/// <summary>
/// IX-39 coupling guard (review package T2-09): the Tier 2 derivation mirrors the SpiceDB schema relation by relation, so it is bound
/// to one exact schema text.
/// <list type="bullet">
/// <item><b>CI:</b> a unit test fails when <c>authority.schema.zed</c> changes without this digest (and the derivation) being updated.</item>
/// <item><b>Startup:</b> the host compares the embedded schema it writes to SpiceDB with this digest; a different schema disables Tier 2.</item>
/// <item><b>Rows:</b> every checkpoint records the digest its rows were derived under. Rows of another digest are not ready and are
/// rebuilt (as an older protocol is), and a rebuild resets verification, so a complete M2 must follow before Tier 2 counts as verified.</item>
/// </list>
/// The digest is SHA-256 over the schema text with line endings normalized to LF (so a Windows checkout and a Linux build agree).
/// </summary>
public static class SearchAccessSchemaCoupling
{
    /// <summary>SHA-256 (lower-case hex) of the <c>authority.schema.zed</c> this derivation mirrors (LF line endings).</summary>
    public const string MirroredSchemaSha256Hex = "4ee4e26a5a81d369df2a310c526aba5c64e8710be32a3975445943ad7aae4c85";

    public static byte[] MirroredSchemaDigest { get; } = Convert.FromHexString(MirroredSchemaSha256Hex);

    /// <summary>The coupling digest of a schema text.</summary>
    public static byte[] DigestOf(string schemaText)
    {
        ArgumentNullException.ThrowIfNull(schemaText);
        return SHA256.HashData(Encoding.UTF8.GetBytes(schemaText.Replace("\r\n", "\n", StringComparison.Ordinal)));
    }

    public static bool Coupled(string schemaText) => DigestOf(schemaText).AsSpan().SequenceEqual(MirroredSchemaDigest);

    /// <summary>The checkpoint's rows were derived under the mirrored schema.</summary>
    public static bool SchemaCoupled(SearchAccessCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        return checkpoint.DerivedSchemaDigest is { } digest && digest.AsSpan().SequenceEqual(MirroredSchemaDigest);
    }

    /// <summary>The checkpoint's rows are of the current derivation: this protocol version and the mirrored schema.</summary>
    public static bool CurrentDerivation(SearchAccessCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        return checkpoint.AccessProjectionProtocolVersion == SearchAccessDerivation.ProtocolVersion && SchemaCoupled(checkpoint);
    }
}
