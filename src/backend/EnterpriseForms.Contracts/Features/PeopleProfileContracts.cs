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

/// <summary>
/// C8 M2 (AC-7.5): cancel a Pending membership request. <c>ExpectedRequestDigest</c> is the <c>requestDigest</c> the list showed; a request
/// that changed meanwhile is refused (409 MEMBERSHIP_REQUEST_CHANGED). Needs an Idempotency-Key and the development step-up.
/// </summary>
public sealed record CancelMembershipRequestRequest(string ExpectedRequestDigest, string Reason, bool UseDevelopmentStepUp = false);

/// <summary>
/// C8 M2 (AC-7.5): edit a Pending request by replacing it. Only the display name, initial role (null or Designer) and the request reason can
/// change; the verified identity, issuer and organization are copied by the Authority from the original and are not part of this request.
/// The response carries the replacement's new <c>requestId</c> (fresh seven-day expiry, no approval carried over).
/// </summary>
public sealed record ReplaceMembershipRequestRequest(string ExpectedRequestDigest, string DisplayNameHint, string? InitialRoleName, string Reason, bool UseDevelopmentStepUp = false);
