namespace EnterpriseForms.Application.Common.Authority;

/// <summary>
/// E3-M2f-2 (docs/security/E3_M2F_CONTRACT_CLOSURE.md CAP-E3): the Authority refused a request because the business envelope capability
/// this build declares is below the organization's or the deployment's floor. Terminal for this build: never retried, never answered with
/// a fresh content operation (every attempt would be refused the same way), and surfaced to users as a service-unavailable condition
/// without detail. It is deliberately absent from every retryable and fresh-operation predicate. An architecture test keeps
/// <see cref="ErrorCode"/> equal to the Authority's error code.
/// </summary>
public static class EnvelopeCapabilityRefusal
{
    public const string ErrorCode = "CLIENT_ENVELOPE_CAPABILITY_INSUFFICIENT";

    public static bool Is(AuthorityClientException exception) =>
        exception is not null && string.Equals(exception.ErrorCode, ErrorCode, StringComparison.Ordinal);
}
