using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using EnterpriseForms.PlatformConsole.Composition;
using Microsoft.Extensions.Options;

namespace EnterpriseForms.PlatformConsole.Features.Session;

/// <summary>
/// One signed-in Platform Operator. Bound at sign-in to exactly one registered operator (id, issuer, subject) and that operator's
/// own signing key; nothing a request carries can change these values.
/// </summary>
public sealed record OperatorSession(
    string SessionDigest,
    Guid PlatformOperatorId,
    string IssuerUri,
    string SubjectIdentifier,
    string DisplayName,
    string SigningKeyName,
    string CsrfToken,
    DateTimeOffset CreatedOn,
    DateTimeOffset LastActivityOn,
    DateTimeOffset AbsoluteExpiresOn)
{
    public DateTimeOffset IdleExpiresOn(TimeSpan idle) => LastActivityOn + idle < AbsoluteExpiresOn ? LastActivityOn + idle : AbsoluteExpiresOn;
}

/// <summary>
/// Server-side session state. The cookie carries only a random 256-bit session id; this store keeps the session under the id's
/// SHA-256 digest, so the id itself is never held at rest. Idle (15 min) and absolute (2 h) expiry are enforced here on every request,
/// not by cookie attributes. Sign-out, a new sign-in from the same browser, and an operator found inactive by the Authority remove
/// sessions immediately. The store is in memory: a console restart ends every session (fail safe). A multi-instance deployment would
/// need a shared store; there is none in Development/Testing and production is blocked on PB-08 (documented).
/// </summary>
public sealed class OperatorSessionStore(IOptions<PlatformConsoleOptions> options, TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, OperatorSession> sessions = new(StringComparer.Ordinal);

    public TimeSpan IdleTimeout => TimeSpan.FromMinutes(options.Value.Session.IdleTimeoutMinutes);

    public TimeSpan AbsoluteLifetime => TimeSpan.FromMinutes(options.Value.Session.AbsoluteLifetimeMinutes);

    public int Count => sessions.Count;

    /// <summary>Creates a session and returns the raw id for the cookie (the only copy outside this call).</summary>
    public (string SessionId, OperatorSession Session) Create(Guid platformOperatorId, string issuerUri, string subjectIdentifier, string displayName, string signingKeyName)
    {
        Sweep();
        if (sessions.Count >= options.Value.Session.MaximumSessions)
        {
            throw new ConsoleException("SESSION_CAPACITY_REACHED", 503, "too many open console sessions; try again later", retryable: true);
        }

        var now = clock.GetUtcNow();
        var id = Base64Url(RandomNumberGenerator.GetBytes(32));
        var session = new OperatorSession(Digest(id), platformOperatorId, issuerUri, subjectIdentifier, displayName, signingKeyName, Base64Url(RandomNumberGenerator.GetBytes(32)), now, now, now + AbsoluteLifetime);
        sessions[session.SessionDigest] = session;
        return (id, session);
    }

    /// <summary>The live session for a presented id, with its idle clock reset; null (and removed) when unknown, idle-expired or past its absolute lifetime.</summary>
    public OperatorSession? Touch(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId) || sessionId.Length > 128)
        {
            return null;
        }

        var digest = Digest(sessionId);
        while (sessions.TryGetValue(digest, out var current))
        {
            var now = clock.GetUtcNow();
            if (now >= current.AbsoluteExpiresOn || now >= current.LastActivityOn + IdleTimeout)
            {
                sessions.TryRemove(new KeyValuePair<string, OperatorSession>(digest, current));
                return null;
            }

            var touched = current with { LastActivityOn = now };
            if (sessions.TryUpdate(digest, touched, current))
            {
                return touched;
            }
        }

        return null;
    }

    public bool Revoke(string? sessionId) => !string.IsNullOrEmpty(sessionId) && sessionId.Length <= 128 && sessions.TryRemove(Digest(sessionId), out _);

    /// <summary>Ends every session of one operator (the Authority reported them not active: suspended, removed or unknown).</summary>
    public int RevokeOperator(Guid platformOperatorId)
    {
        var removed = 0;
        foreach (var (digest, session) in sessions)
        {
            if (session.PlatformOperatorId == platformOperatorId && sessions.TryRemove(new KeyValuePair<string, OperatorSession>(digest, session)))
            {
                removed++;
            }
        }

        return removed;
    }

    public void Sweep()
    {
        var now = clock.GetUtcNow();
        foreach (var (digest, session) in sessions)
        {
            if (now >= session.AbsoluteExpiresOn || now >= session.LastActivityOn + IdleTimeout)
            {
                sessions.TryRemove(new KeyValuePair<string, OperatorSession>(digest, session));
            }
        }
    }

    private static string Digest(string sessionId) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
