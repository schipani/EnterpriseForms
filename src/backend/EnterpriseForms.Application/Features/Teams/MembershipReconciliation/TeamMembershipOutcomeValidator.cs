using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Domain.Features.Teams;

namespace EnterpriseForms.Application.Features.Teams.MembershipReconciliation;

public enum OutcomeValidationVerdict
{
    Valid,
    OperationMismatch,
    InvalidAuthorityResponse,
}

public sealed record OutcomeValidation(OutcomeValidationVerdict Verdict, string Reason)
{
    public static readonly OutcomeValidation Valid = new(OutcomeValidationVerdict.Valid, string.Empty);

    public bool IsValid => Verdict == OutcomeValidationVerdict.Valid;
}

/// <summary>
/// Package §1.3 (fail closed): every AC-9 answer is checked against the request and the operation row before anything is
/// projected. Any violation is either <see cref="OutcomeValidationVerdict.OperationMismatch"/> (the answer names another
/// command, team or member than the row) or <see cref="OutcomeValidationVerdict.InvalidAuthorityResponse"/> (the answer is
/// internally inconsistent). Neither is ever projected.
/// </summary>
public static class TeamMembershipOutcomeValidator
{
    private static readonly string[] KnownStates = ["CommittedAwaitingProjection", "Effective", "Rejected", "FailedReconciliation"];

    public static OutcomeValidation Validate(AuthorityCommandOperation operation, Guid requestCorrelationId, SecurityCommandOutcomeAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(answer);
        if (answer.CorrelationId != requestCorrelationId)
        {
            return Invalid("correlation id differs from the request");
        }

        if (answer.OrganizationId != operation.TenantId)
        {
            return Invalid("organization differs from the request and the operation");
        }

        if (!answer.Found)
        {
            // The uniform not-found answer carries nothing else.
            return answer.SecurityCommandId is null && answer.CommandState is null && answer.Membership is null && answer.OrganizationState is null && answer.TeamId is null && answer.MemberUserId is null
                ? OutcomeValidation.Valid
                : Invalid("a not-found answer carries command data");
        }

        if (!string.Equals(answer.CommandKind, operation.CommandKindName, StringComparison.Ordinal))
        {
            return Invalid("command kind differs from the expected kind");
        }

        if (answer.SecurityCommandId is not { } commandId || commandId == Guid.Empty)
        {
            return Invalid("found without a command id");
        }

        if (operation.AuthorityCommandId is { } known && known != commandId)
        {
            return Mismatch("the key resolves to another command than the one recorded");
        }

        if (answer.TeamId != operation.TeamId || answer.MemberUserId != operation.MemberUserId)
        {
            return Mismatch("team or member differs from the operation");
        }

        if (answer.CommandState is not { } state || Array.IndexOf(KnownStates, state) < 0)
        {
            return Invalid("unknown command state");
        }

        if (answer.OrganizationState is not { } organization)
        {
            return Invalid("organization state missing");
        }

        if (organization.ProjectionAppliedRevision < 0 || organization.ProjectionAppliedRevision > organization.AuthorizationRevision)
        {
            return Invalid("projection-applied revision above the authorization revision");
        }

        var rejected = state == "Rejected";
        if (!rejected)
        {
            if (answer.CommittedAuthorizationRevision is not { } committed || committed < 1 || committed > organization.AuthorizationRevision)
            {
                return Invalid("committed revision outside 1…authorization revision");
            }

            if (operation.CommittedAuthorizationRevision is { } recorded && recorded != committed)
            {
                return Mismatch("committed revision differs from the one recorded");
            }

            if (state == "Effective" && committed > organization.ProjectionAppliedRevision)
            {
                return Invalid("Effective above the projection-applied revision");
            }

            if (state == "CommittedAwaitingProjection" && committed <= organization.ProjectionAppliedRevision)
            {
                return Invalid("awaiting projection at or below the projection-applied revision");
            }
        }
        else if (operation.CommittedAuthorizationRevision is not null || answer.CommittedAuthorizationRevision is not null)
        {
            return Invalid("a rejected command carries a committed revision, or was recorded as committed");
        }

        var membershipExpected = !rejected && !organization.IntegrityQuarantined;
        if (membershipExpected != (answer.Membership is not null))
        {
            return Invalid(membershipExpected ? "membership missing" : "membership present while rejected or quarantined");
        }

        if (answer.Membership is { } membership)
        {
            if (membership.EvaluatedAtRevision != organization.ProjectionAppliedRevision)
            {
                return Invalid("membership evaluated at another revision than the projection-applied revision");
            }

            if (membership.EffectiveMembershipRevision < 0 || membership.EffectiveMembershipRevision > membership.EvaluatedAtRevision)
            {
                return Invalid("membership revision above the evaluated revision");
            }

            if (state == "Effective" && membership.EffectiveMembershipRevision < answer.CommittedAuthorizationRevision)
            {
                return Invalid("Effective command older than the pair's membership revision allows");
            }
        }

        return OutcomeValidation.Valid;
    }

    private static OutcomeValidation Invalid(string reason) => new(OutcomeValidationVerdict.InvalidAuthorityResponse, reason);

    private static OutcomeValidation Mismatch(string reason) => new(OutcomeValidationVerdict.OperationMismatch, reason);
}
