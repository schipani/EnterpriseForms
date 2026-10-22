namespace EnterpriseForms.Application.Common;

/// <summary>
/// Protects list cursors so clients cannot read or alter them (PB-06 opaque, expiring cursors). A cursor is a
/// convenience, never an authorization: the server recomputes the authorized set for every page and only uses the
/// cursor to locate the requested position within it.
/// </summary>
public interface IOpaqueCursorProtector
{
    /// <summary>Authenticated-encrypts the payload with an embedded expiry.</summary>
    string Protect(string payload, TimeSpan lifetime);

    /// <summary>The payload, or null when the token is malformed, altered, issued by another key ring, or expired.</summary>
    string? Unprotect(string token);
}
