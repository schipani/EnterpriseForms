namespace AuthorizationAuthority.Domain.Features.Sessions;

/// <summary>
/// G23: the server-side state of one browser session, held by the Authority (outside the business database, so a business
/// administrator cannot revive a revoked session). The business API keeps a random 256-bit session secret inside its
/// encrypted session cookie and registers only the secret's SHA-256 digest here; the raw secret is never stored or logged.
/// A session is valid only while it is not revoked, within its absolute expiry, within its idle timeout, and bound to an
/// enabled identity of its organization.
/// </summary>
public sealed class AuthenticatedSession
{
    public Guid AuthenticatedSessionId { get; set; }

    public Guid OrganizationId { get; set; }

    public Guid IdentityBindingId { get; set; }

    /// <summary>SHA-256 of the session secret (32 bytes); unique.</summary>
    public byte[] SessionSecretDigest { get; set; } = [];

    public DateTimeOffset SessionCreatedOn { get; set; }

    public DateTimeOffset SessionExpiresOn { get; set; }

    public DateTimeOffset SessionLastSeenOn { get; set; }

    public int SessionIdleTimeoutSeconds { get; set; }

    public DateTimeOffset? SessionRevokedOn { get; set; }

    public string? SessionRevocationReason { get; set; }

    /// <summary>The workload that created the session (from its credential).</summary>
    public string CreatedByWorkloadName { get; set; } = string.Empty;
}

public static class SessionRevocationReasons
{
    public const string Logout = "LOGOUT";
    public const string Replaced = "REPLACED_BY_NEW_SIGN_IN";

    /// <summary>AC-7.2: the identity's membership was disabled by an owner command.</summary>
    public const string MembershipDisabled = "MEMBERSHIP_DISABLED";
}

/// <summary>Why a session does not authenticate (returned to the trusted business API only).</summary>
public static class SessionInvalidReasons
{
    public const string Unknown = "SESSION_UNKNOWN";
    public const string Revoked = "SESSION_REVOKED";
    public const string Expired = "SESSION_EXPIRED";
    public const string Idle = "SESSION_IDLE_TIMEOUT";
    public const string IdentityChanged = "SESSION_IDENTITY_MISMATCH";
}
