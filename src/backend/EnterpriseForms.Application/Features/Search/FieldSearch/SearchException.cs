using EnterpriseForms.Application.Common;

namespace EnterpriseForms.Application.Features.Search.FieldSearch;

/// <summary>
/// A typed search failure (ADR-PB-004). The host adds <c>retryable</c> and, when present, <c>unavailable_reason</c> /
/// <c>limit_kind</c> problem extensions. Details identify a condition by its position only and never carry a predicate value.
/// </summary>
public sealed class SearchException(string errorCode, int httpStatusCode, string detail, bool retryable, string? unavailableReason = null, string? limitKind = null)
    : BusinessException(errorCode, httpStatusCode, detail)
{
    public bool Retryable { get; } = retryable;

    public string? UnavailableReason { get; } = unavailableReason;

    public string? LimitKind { get; } = limitKind;

    public static SearchException RequestInvalid(string detail) => new(SearchErrorCodes.RequestInvalid, 422, detail, retryable: false);

    public static SearchException OperatorNotSupported(int conditionNumber) =>
        new(SearchErrorCodes.OperatorNotSupported, 422, $"condition {conditionNumber}: the operator is not supported for this value type", retryable: false);

    /// <summary>D3/D4: the eligible scope cannot be verified within the limit (records, bytes or time); nothing partial.</summary>
    public static SearchException VerificationLimit(string limitKind) =>
        new(SearchErrorCodes.VerificationLimit, 422, "the search scope is too large to verify completely; narrow it", retryable: false, limitKind: limitKind);

    /// <summary>Authorization or content moved while the scope was verified (or since its snapshot): restart from the beginning.</summary>
    public static SearchException SnapshotChanged() =>
        new(SearchErrorCodes.SnapshotChanged, 409, "records or access changed while the search was verified; run it again", retryable: true);

    /// <summary>D3: a record in scope could not be authenticated, or the Authority could not verify right now; never shown as no matches.</summary>
    public static SearchException VerificationUnavailable(string reason, bool retryable) =>
        new(SearchErrorCodes.VerificationUnavailable, 503, "the records in scope could not all be verified; no result is returned", retryable, reason);

    /// <summary>M0 gate: well-formed requests are refused because trusted execution does not exist; never an empty 200.</summary>
    public static SearchException TrustedSearchNotEnabled() =>
        new(SearchErrorCodes.VerificationUnavailable, 503, "advanced field-value search is not available: trusted, independently verified execution has not been enabled", retryable: false, SearchUnavailableReasons.TrustedSearchNotEnabled);
}
