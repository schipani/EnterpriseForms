namespace EnterpriseForms.Contracts.Features;

/// <summary>
/// Directory profile of one person (non-security fields only). <c>DisplayName</c> is the name shown everywhere; it follows the identity
/// provider's name (<c>IdentityDisplayName</c>) unless <c>DisplayNameOverridden</c>. Verified identity, organization roles and
/// membership are shown for context and can never be changed here. <c>ProfileRevision</c> is the optimistic-concurrency token.
/// </summary>
public sealed record UserProfileResponse(Guid UserId, string DisplayName, string IdentityDisplayName, bool DisplayNameOverridden, string? PreferredName, string? JobTitle, long ProfileRevision, DateTimeOffset? ProfileModifiedOn,
    string IdentityIssuer, string IdentitySubject, bool IsCurrentUser, bool CanEdit);

/// <summary>
/// Edit a profile. <c>DisplayName</c> null or empty with <c>ResetDisplayName</c> returns to the identity provider's name; a name that
/// equals the identity provider's name also clears the override. 409 PROFILE_REVISION_CONFLICT when <c>ExpectedRevision</c> is stale.
/// </summary>
public sealed record UpdateUserProfileRequest(string? DisplayName, string? PreferredName, string? JobTitle, long ExpectedRevision, bool ResetDisplayName = false);

public sealed record PresenceHeartbeatRequest(int InteractionAgeSeconds);

/// <summary><c>State</c>: Available, Away, Offline or Unknown (presence could not be determined). Informational only.</summary>
public sealed record UserPresenceResponse(Guid UserId, string State);

public sealed record PresenceQueryResponse(IReadOnlyList<UserPresenceResponse> Presence, bool Available, DateTimeOffset AsOf, int RefreshAfterSeconds);

/// <summary>One server page (default 5, newest first) of owner proposals. States: Pending, Consumed (approved), Rejected, Expired.</summary>
public sealed record OwnerProposalPageResponse(IReadOnlyList<OwnerProposalResponse> Items, int TotalCount, int PageStart, int PageSize, string? NextToken, string? PreviousToken);

/// <summary>One server page (default 5, newest first) of membership requests. States: Pending, Approved, Rejected, Expired.</summary>
public sealed record MembershipRequestPageResponse(IReadOnlyList<MembershipRequestResponse> Items, int TotalCount, int PageStart, int PageSize, string? NextToken, string? PreviousToken);
