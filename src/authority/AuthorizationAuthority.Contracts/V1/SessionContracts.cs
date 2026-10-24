namespace AuthorizationAuthority.Contracts.V1;

// G23 — server-side browser session state (private internal contract, business workload + delegated user assertion).
// Only SHA-256 digests of session secrets cross this boundary; the raw secret stays inside the business API's encrypted cookie.

/// <summary>
/// Registers a new session for the delegated user. <c>ReplacedSessionDigestBase64</c>: the session the browser presented
/// when it signed in again (revoked in the same transaction — no session survives a new sign-in).
/// </summary>
public sealed record CreateSessionRequest(Guid OrganizationId, string SessionDigestBase64, DateTimeOffset AbsoluteExpiresOn, string? ReplacedSessionDigestBase64 = null);

public sealed record SessionDigestRequest(Guid OrganizationId, string SessionDigestBase64);

/// <summary><c>Valid</c> false ⇒ the session must not authenticate; <c>InvalidReason</c> is for the trusted caller only.</summary>
public sealed record SessionStatus(bool Valid, string? InvalidReason, DateTimeOffset? ExpiresOn, Guid CorrelationId);
