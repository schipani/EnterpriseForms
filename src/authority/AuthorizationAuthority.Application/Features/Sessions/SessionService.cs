using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Sessions;

namespace AuthorizationAuthority.Application.Features.Sessions;

/// <summary>Configuration section <c>Sessions</c> (bound and validated by the host).</summary>
public sealed class SessionOptions
{
    public const string SectionName = "Sessions";

    /// <summary>A session unused for longer than this no longer authenticates (inactivity expiry).</summary>
    public int IdleTimeoutMinutes { get; set; } = 30;

    /// <summary>Upper bound for any session's absolute expiry.</summary>
    public int MaximumAbsoluteHours { get; set; } = 12;

    /// <summary>Last-seen is written at most this often (validation is a read otherwise).</summary>
    public int LastSeenWriteIntervalSeconds { get; set; } = 60;

    public static void Validate(SessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.IdleTimeoutMinutes is < 1 or > 720 || options.MaximumAbsoluteHours is < 1 or > 24 || options.LastSeenWriteIntervalSeconds < 1
            || options.LastSeenWriteIntervalSeconds >= options.IdleTimeoutMinutes * 60)
        {
            throw new InvalidOperationException("Sessions: idle timeout 1..720 min, absolute maximum 1..24 h, last-seen interval shorter than the idle timeout");
        }
    }
}

/// <summary>
/// G23 (C-63): server-side session state in the Authority. The business API registers a session at sign-in (digest of a
/// random secret it keeps only inside its encrypted cookie), asks the Authority to validate it on every cookie-authenticated
/// request, and revokes it at sign-out. Nothing the business database holds can make a revoked session valid again, and a
/// copied cookie stops working the moment its session is revoked — on every API instance, because the state is here.
/// </summary>
public sealed class SessionService(IAuthorityStore store, TimeProvider clock, SessionOptions options)
{
    public async Task<SessionStatus> CreateAsync(VerifiedActor actor, CreateSessionRequest request, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var digest = Digest(request.SessionDigestBase64);
        var now = clock.GetUtcNow();
        if (request.AbsoluteExpiresOn <= now || request.AbsoluteExpiresOn > now.AddHours(options.MaximumAbsoluteHours))
        {
            throw AuthorityException.Invalid($"absolute_expires_on must be in the future and at most {options.MaximumAbsoluteHours} hours away");
        }

        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        _ = await scope.ReadOrganizationAsync(request.OrganizationId, cancellationToken).ConfigureAwait(false)
            ?? throw AuthorityException.NotRegistered("organization is not registered");
        var bound = await ActorResolver.BindAsync(scope, request.OrganizationId, actor, cancellationToken).ConfigureAwait(false);
        if (await scope.FindSessionByDigestAsync(digest, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw AuthorityException.Invalid("session digest already registered");
        }

        if (request.ReplacedSessionDigestBase64 is { Length: > 0 } replacedText
            && await scope.FindSessionByDigestAsync(Digest(replacedText), cancellationToken).ConfigureAwait(false) is { SessionRevokedOn: null } replaced)
        {
            // Session fixation/replay: the session the browser presented does not survive a new sign-in, whoever it belonged to.
            replaced.SessionRevokedOn = now;
            replaced.SessionRevocationReason = SessionRevocationReasons.Replaced;
        }

        scope.AddSession(new AuthenticatedSession
        {
            AuthenticatedSessionId = Guid.CreateVersion7(),
            OrganizationId = request.OrganizationId,
            IdentityBindingId = bound.Binding.IdentityBindingId,
            SessionSecretDigest = digest,
            SessionCreatedOn = now,
            SessionExpiresOn = request.AbsoluteExpiresOn,
            SessionLastSeenOn = now,
            SessionIdleTimeoutSeconds = options.IdleTimeoutMinutes * 60,
            CreatedByWorkloadName = actor.WorkloadName,
        });
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new SessionStatus(true, null, request.AbsoluteExpiresOn, correlationId);
    }

    /// <summary>
    /// Valid only if the session exists for this organization and this delegated identity, is not revoked, is within its
    /// absolute expiry and idle timeout, and the identity is still enabled. Nothing is ever renewed past those limits.
    /// </summary>
    public async Task<SessionStatus> ValidateAsync(VerifiedActor actor, SessionDigestRequest request, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var digest = Digest(request.SessionDigestBase64);
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var session = await scope.FindSessionByDigestAsync(digest, cancellationToken).ConfigureAwait(false);
        if (session is null || session.OrganizationId != request.OrganizationId)
        {
            return Invalid(SessionInvalidReasons.Unknown, correlationId);
        }

        var binding = await scope.FindIdentityBindingAsync(request.OrganizationId, actor.IssuerUri, actor.SubjectIdentifier, cancellationToken).ConfigureAwait(false);
        if (binding is null || !binding.IdentityBindingEnabled || binding.IdentityBindingId != session.IdentityBindingId)
        {
            return Invalid(SessionInvalidReasons.IdentityChanged, correlationId);
        }

        var now = clock.GetUtcNow();
        if (session.SessionRevokedOn is not null)
        {
            return Invalid(SessionInvalidReasons.Revoked, correlationId);
        }

        if (now >= session.SessionExpiresOn)
        {
            return Invalid(SessionInvalidReasons.Expired, correlationId);
        }

        if (now - session.SessionLastSeenOn > TimeSpan.FromSeconds(session.SessionIdleTimeoutSeconds))
        {
            return Invalid(SessionInvalidReasons.Idle, correlationId);
        }

        if (now - session.SessionLastSeenOn >= TimeSpan.FromSeconds(options.LastSeenWriteIntervalSeconds))
        {
            session.SessionLastSeenOn = now;
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return new SessionStatus(true, null, session.SessionExpiresOn, correlationId);
    }

    /// <summary>Idempotent. Only the session's own identity (the cookie holder) can revoke it through this route.</summary>
    public async Task<SessionStatus> RevokeAsync(VerifiedActor actor, SessionDigestRequest request, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var digest = Digest(request.SessionDigestBase64);
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var session = await scope.FindSessionByDigestAsync(digest, cancellationToken).ConfigureAwait(false);
        if (session is null || session.OrganizationId != request.OrganizationId)
        {
            return Invalid(SessionInvalidReasons.Unknown, correlationId);
        }

        var binding = await scope.FindIdentityBindingAsync(request.OrganizationId, actor.IssuerUri, actor.SubjectIdentifier, cancellationToken).ConfigureAwait(false);
        if (binding is null || binding.IdentityBindingId != session.IdentityBindingId)
        {
            return Invalid(SessionInvalidReasons.IdentityChanged, correlationId);
        }

        if (session.SessionRevokedOn is null)
        {
            session.SessionRevokedOn = clock.GetUtcNow();
            session.SessionRevocationReason = SessionRevocationReasons.Logout;
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return Invalid(SessionInvalidReasons.Revoked, correlationId);
    }

    private static SessionStatus Invalid(string reason, Guid correlationId) => new(false, reason, null, correlationId);

    private static byte[] Digest(string base64)
    {
        byte[] digest;
        try
        {
            digest = Convert.FromBase64String(base64 ?? string.Empty);
        }
        catch (FormatException)
        {
            throw AuthorityException.Invalid("session digest must be base64");
        }

        return digest.Length == 32 ? digest : throw AuthorityException.Invalid("session digest must be 32 bytes (SHA-256)");
    }
}
