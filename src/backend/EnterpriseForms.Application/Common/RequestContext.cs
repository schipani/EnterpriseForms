using EnterpriseForms.Application.Common.Authority;

namespace EnterpriseForms.Application.Common;

/// <summary>
/// The authenticated request context: verified (issuer, subject), the asserted organization and the raw user assertion
/// to forward. The business API treats <see cref="OrganizationId"/> as an assertion; the authority binds the principal
/// to the organization independently on every call. <see cref="LocalUserId"/> is the authority user id carried in the
/// assertion (<c>authority_user_id</c> claim) used only to label local projections.
/// </summary>
public sealed record RequestContext(Guid OrganizationId, Guid LocalUserId, string IssuerUri, string SubjectIdentifier, string DisplayName, string RawAssertion, Guid CorrelationId)
{
    public DelegatedUserContext ToDelegatedUser() => new(IssuerUri, SubjectIdentifier, OrganizationId, RawAssertion);
}

/// <summary>Application-level failure mapped to RFC 9457 Problem Details by the host.</summary>
public sealed class BusinessException(string errorCode, int httpStatusCode, string? detail = null) : Exception(detail ?? errorCode)
{
    public string ErrorCode { get; } = errorCode;

    public int HttpStatusCode { get; } = httpStatusCode;
}
