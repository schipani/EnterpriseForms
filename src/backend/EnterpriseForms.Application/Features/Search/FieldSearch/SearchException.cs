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

    /// <summary>M0 gate: well-formed requests are refused because trusted execution does not exist; never an empty 200.</summary>
    public static SearchException TrustedSearchNotEnabled() =>
        new(SearchErrorCodes.VerificationUnavailable, 503, "advanced field-value search is not available: trusted, independently verified execution has not been enabled", retryable: false, SearchUnavailableReasons.TrustedSearchNotEnabled);
}
