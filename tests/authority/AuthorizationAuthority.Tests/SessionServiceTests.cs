using System.Security.Cryptography;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.Sessions;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.Sessions;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>G23: server-side session state rules (unit level; the copied-cookie and multi-instance proofs run against Docker).</summary>
public sealed class SessionServiceTests
{
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly VerifiedActor Alice = new("https://issuer.test", "alice", "business-api");
    private static readonly VerifiedActor Bob = new("https://issuer.test", "bob", "business-api");

    private readonly InMemoryAuthorityStore store = new();
    private readonly ManualClock clock = new(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
    private readonly SessionService service;

    public SessionServiceTests()
    {
        store.Organizations.Add(new OrganizationAuthorizationState { OrganizationId = Org });
        store.Bindings.Add(Binding(Alice));
        store.Bindings.Add(Binding(Bob));
        service = new SessionService(store, clock, new SessionOptions());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CreatedSessionValidatesAndRevocationIsImmediateAndPermanent()
    {
        var digest = NewDigest();
        Assert.True((await service.CreateAsync(Alice, Create(digest), Guid.NewGuid(), Ct)).Valid);
        Assert.True((await Validate(Alice, digest)).Valid);

        var revoked = await service.RevokeAsync(Alice, new SessionDigestRequest(Org, digest), Guid.NewGuid(), Ct);
        Assert.False(revoked.Valid);
        var after = await Validate(Alice, digest);
        Assert.False(after.Valid);
        Assert.Equal(SessionInvalidReasons.Revoked, after.InvalidReason);

        // Revocation is idempotent and cannot be undone by a second revoke or a re-create of the same digest.
        Assert.False((await service.RevokeAsync(Alice, new SessionDigestRequest(Org, digest), Guid.NewGuid(), Ct)).Valid);
        await Assert.ThrowsAsync<AuthorityException>(() => service.CreateAsync(Alice, Create(digest), Guid.NewGuid(), Ct));
        Assert.Equal(SessionRevocationReasons.Logout, store.Sessions.Single().SessionRevocationReason);
    }

    [Fact]
    public async Task AbsoluteExpiryIsNeverExtendedByActivity()
    {
        var digest = NewDigest();
        await service.CreateAsync(Alice, Create(digest, TimeSpan.FromHours(1)), Guid.NewGuid(), Ct);
        for (var i = 0; i < 5; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(11));
            Assert.True((await Validate(Alice, digest)).Valid);
        }

        clock.Advance(TimeSpan.FromMinutes(6)); // 61 minutes after creation, active the whole time
        var expired = await Validate(Alice, digest);
        Assert.False(expired.Valid);
        Assert.Equal(SessionInvalidReasons.Expired, expired.InvalidReason);
    }

    [Fact]
    public async Task IdleSessionStopsAuthenticatingAndDoesNotRecover()
    {
        var digest = NewDigest();
        await service.CreateAsync(Alice, Create(digest), Guid.NewGuid(), Ct);
        clock.Advance(TimeSpan.FromMinutes(31));
        var idle = await Validate(Alice, digest);
        Assert.False(idle.Valid);
        Assert.Equal(SessionInvalidReasons.Idle, idle.InvalidReason);
        Assert.False((await Validate(Alice, digest)).Valid); // the failed check did not refresh last-seen
    }

    [Fact]
    public async Task LastSeenIsWrittenOnlyAfterTheInterval()
    {
        var digest = NewDigest();
        await service.CreateAsync(Alice, Create(digest), Guid.NewGuid(), Ct);
        var commitsAfterCreate = store.ScopeCommitCount;
        clock.Advance(TimeSpan.FromSeconds(30));
        await Validate(Alice, digest);
        Assert.Equal(commitsAfterCreate, store.ScopeCommitCount);
        clock.Advance(TimeSpan.FromSeconds(31));
        await Validate(Alice, digest);
        Assert.Equal(commitsAfterCreate + 1, store.ScopeCommitCount);
    }

    [Fact]
    public async Task ANewSignInRevokesTheSessionTheBrowserPresented()
    {
        var planted = NewDigest();
        await service.CreateAsync(Bob, Create(planted), Guid.NewGuid(), Ct);
        var fresh = NewDigest();
        await service.CreateAsync(Alice, Create(fresh, replaced: planted), Guid.NewGuid(), Ct);

        var old = await Validate(Bob, planted);
        Assert.False(old.Valid);
        Assert.Equal(SessionInvalidReasons.Revoked, old.InvalidReason);
        Assert.True((await Validate(Alice, fresh)).Valid);
    }

    [Fact]
    public async Task SessionIsBoundToItsIdentityOrganizationAndEnabledBinding()
    {
        var digest = NewDigest();
        await service.CreateAsync(Alice, Create(digest), Guid.NewGuid(), Ct);

        Assert.Equal(SessionInvalidReasons.IdentityChanged, (await Validate(Bob, digest)).InvalidReason);
        Assert.Equal(SessionInvalidReasons.Unknown, (await service.ValidateAsync(Alice, new SessionDigestRequest(Guid.CreateVersion7(), digest), Guid.NewGuid(), Ct)).InvalidReason);
        Assert.Equal(SessionInvalidReasons.Unknown, (await Validate(Alice, NewDigest())).InvalidReason);

        // Another identity cannot sign the victim out either.
        Assert.False((await service.RevokeAsync(Bob, new SessionDigestRequest(Org, digest), Guid.NewGuid(), Ct)).Valid);
        Assert.Null(store.Sessions.Single().SessionRevokedOn);

        store.Bindings.Single(b => b.IdentitySubjectIdentifier == "alice").IdentityBindingEnabled = false;
        Assert.Equal(SessionInvalidReasons.IdentityChanged, (await Validate(Alice, digest)).InvalidReason);
    }

    [Fact]
    public async Task UnboundIdentityCannotCreateASession()
    {
        var stranger = new VerifiedActor("https://issuer.test", "mallory", "business-api");
        await Assert.ThrowsAsync<AuthorityException>(() => service.CreateAsync(stranger, Create(NewDigest()), Guid.NewGuid(), Ct));
        Assert.Empty(store.Sessions);
    }

    [Theory]
    [InlineData("not base64!")]
    [InlineData("AAAA")]
    public async Task MalformedDigestIsRejected(string digest)
    {
        await Assert.ThrowsAsync<AuthorityException>(() => service.CreateAsync(Alice, Create(digest), Guid.NewGuid(), Ct));
        await Assert.ThrowsAsync<AuthorityException>(() => Validate(Alice, digest));
    }

    [Fact]
    public async Task AbsoluteExpiryOutsideTheAllowedWindowIsRejected()
    {
        await Assert.ThrowsAsync<AuthorityException>(() => service.CreateAsync(Alice, Create(NewDigest(), TimeSpan.FromHours(13)), Guid.NewGuid(), Ct));
        await Assert.ThrowsAsync<AuthorityException>(() => service.CreateAsync(Alice, Create(NewDigest(), TimeSpan.FromSeconds(-1)), Guid.NewGuid(), Ct));
    }

    [Fact]
    public void OptionsValidationRejectsUnsafeSettings()
    {
        Assert.Throws<InvalidOperationException>(() => SessionOptions.Validate(new SessionOptions { IdleTimeoutMinutes = 0 }));
        Assert.Throws<InvalidOperationException>(() => SessionOptions.Validate(new SessionOptions { MaximumAbsoluteHours = 48 }));
        Assert.Throws<InvalidOperationException>(() => SessionOptions.Validate(new SessionOptions { IdleTimeoutMinutes = 1, LastSeenWriteIntervalSeconds = 60 }));
        SessionOptions.Validate(new SessionOptions());
    }

    private Task<SessionStatus> Validate(VerifiedActor actor, string digest) => service.ValidateAsync(actor, new SessionDigestRequest(Org, digest), Guid.NewGuid(), Ct);

    private CreateSessionRequest Create(string digest, TimeSpan? lifetime = null, string? replaced = null) =>
        new(Org, digest, clock.GetUtcNow() + (lifetime ?? TimeSpan.FromHours(8)), replaced);

    private static string NewDigest() => Convert.ToBase64String(SHA256.HashData(RandomNumberGenerator.GetBytes(32)));

    private static IdentityBinding Binding(VerifiedActor actor) => new()
    {
        IdentityBindingId = Guid.CreateVersion7(),
        OrganizationId = Org,
        AuthorityUserId = Guid.CreateVersion7(),
        IdentityIssuerUri = actor.IssuerUri,
        IdentitySubjectIdentifier = actor.SubjectIdentifier,
        IdentityBindingEnabled = true,
    };

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;

        public override DateTimeOffset GetUtcNow() => now;

        public void Advance(TimeSpan by) => now += by;
    }
}
