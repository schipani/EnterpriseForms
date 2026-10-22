namespace AuthorizationAuthority.Application.Common;

/// <summary>Stable machine-readable error family (CROSS_SYSTEM_CONSISTENCY_PROTOCOL.md §6). Status codes follow TRUST_AUTHORITY_ARCHITECTURE.md §5.</summary>
public static class AuthorityErrorCodes
{
    public const string Unauthorized = "UNAUTHORIZED";
    public const string Forbidden = "FORBIDDEN";
    public const string ActorNotBound = "ACTOR_NOT_BOUND";
    public const string NotFound = "NOT_FOUND";
    public const string ResourceNotRegistered = "RESOURCE_NOT_REGISTERED";
    public const string AuthorityNotReady = "AUTHORITY_NOT_READY";
    public const string StaleAuthorizationRevision = "STALE_AUTHORIZATION_REVISION";
    public const string ContentRevisionConflict = "CONTENT_REVISION_CONFLICT";
    public const string IntegrityQuarantine = "INTEGRITY_QUARANTINE";
    public const string IntegrityVerificationFailed = "INTEGRITY_VERIFICATION_FAILED";
    public const string IdempotencyConflict = "IDEMPOTENCY_CONFLICT";
    public const string ProjectionDivergence = "PROJECTION_DIVERGENCE";
    public const string InvalidCommand = "INVALID_COMMAND";
    public const string CommandKindNotImplemented = "COMMAND_KIND_NOT_IMPLEMENTED";
    public const string SigningUnavailable = "SIGNING_UNAVAILABLE";
    public const string ArchiveUnavailable = "ARCHIVE_UNAVAILABLE";
    public const string OperationExpired = "OPERATION_EXPIRED";
    public const string OperationStateInvalid = "OPERATION_STATE_INVALID";
    public const string KeyUnavailable = "KEY_UNAVAILABLE";
    public const string CandidateMismatch = "CANDIDATE_MISMATCH";
    public const string LimitExceeded = "LIMIT_EXCEEDED";

    /// <summary>A content change carries rows of a field in a protection form the authority no longer accepts (atomic cutover barrier, Amendment A1); rebuild and retry.</summary>
    public const string FieldProtectionChanged = "FIELD_PROTECTION_CHANGED";

    /// <summary>A discovery continuation token was issued at an earlier authorization revision; restart the enumeration from the first page (C-28).</summary>
    public const string ContinuationInvalidated = "CONTINUATION_INVALIDATED";

    /// <summary>A share editor's current snapshot is not exactly the accepted, verified head (coverage, digest, binding or root); nothing is signed (Phase C5, D4).</summary>
    public const string CurrentSnapshotMismatch = "CURRENT_SNAPSHOT_MISMATCH";

    /// <summary>Field-scoped contributor writes are disabled by deployment configuration (Phase C5, D10; the default everywhere).</summary>
    public const string ContributorWritesDisabled = "CONTRIBUTOR_WRITES_DISABLED";
}

/// <summary>Thrown by application services; mapped to an ErrorResponse by the host. Never carries secrets or protected values.</summary>
public sealed class AuthorityException(string errorCode, int httpStatusCode, bool retryable, string? detail = null) : Exception(detail ?? errorCode)
{
    public string ErrorCode { get; } = errorCode;

    public int HttpStatusCode { get; } = httpStatusCode;

    public bool Retryable { get; } = retryable;

    public string? Detail { get; } = detail;

    public static AuthorityException Forbidden(string? detail = null) => new(AuthorityErrorCodes.Forbidden, 403, false, detail);

    public static AuthorityException NotReady(string detail) => new(AuthorityErrorCodes.AuthorityNotReady, 503, true, detail);

    public static AuthorityException Invalid(string detail) => new(AuthorityErrorCodes.InvalidCommand, 422, false, detail);

    public static AuthorityException NotRegistered(string detail) => new(AuthorityErrorCodes.ResourceNotRegistered, 404, false, detail);

    public static AuthorityException Quarantined(string detail) => new(AuthorityErrorCodes.IntegrityQuarantine, 423, false, detail);
}
