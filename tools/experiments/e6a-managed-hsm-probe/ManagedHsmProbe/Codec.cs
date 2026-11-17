namespace EnterpriseForms.Experiments.ManagedHsmProbe;

/// <summary>Hex and RFC 4648 §5 base64url helpers (the Managed HSM REST API carries bytes as unpadded base64url).</summary>
internal static class Codec
{
    public static string ToHex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(bytes);

    public static byte[] FromHex(string hex)
    {
        ArgumentNullException.ThrowIfNull(hex);
        return Convert.FromHexString(hex);
    }

    public static string ToBase64Url(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Strict base64url decode: rejects characters outside the base64url alphabet and impossible lengths.</summary>
    public static bool TryFromBase64Url(string? value, out byte[] bytes)
    {
        bytes = [];
        if (value is null || value.Length % 4 == 1 || value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
        {
            return false;
        }

        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
        try
        {
            bytes = Convert.FromBase64String(padded);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
