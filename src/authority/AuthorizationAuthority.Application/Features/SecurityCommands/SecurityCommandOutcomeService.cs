using System.Text.Json;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Organizations;

namespace AuthorizationAuthority.Application.Features.SecurityCommands;

/// <summary>
/// Phase C8 — AC-9 (Design R, PHASE_C8_AC9_SECURITY_REVIEW_PACKAGE.md §1.2): the read-only outcome of one team-membership
/// security command, for the business Worker's reconciler. Everything is read in one REPEATABLE READ, READ ONLY snapshot:
/// no lock, no revision, no journal or outbox row, no SpiceDB call. The answer concerns only the (team, member) pair named
/// by the command's own stored payload; the caller cannot choose a pair. During integrity quarantine it reports status only.
/// </summary>
public sealed class SecurityCommandOutcomeService(IAuthorityStore store)
{
    /// <summary>Allowlist (D8-13): only team add/remove outcomes are reconcilable.</summary>
    public static readonly IReadOnlySet<SecurityCommandKind> ReconcilableKinds = new HashSet<SecurityCommandKind> { SecurityCommandKind.AddTeamMember, SecurityCommandKind.RemoveTeamMember };

    public const string TeamMemberRelation = "member";

    public async Task<SecurityCommandOutcome> LookupAsync(SecurityCommandOutcomeLookupRequest request, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.OrganizationId == Guid.Empty || string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Length is < 8 or > 128)
        {
            throw new AuthorityException(AuthorityErrorCodes.InvalidRequest, 400, false, "organization_id and an idempotency_key of 8..128 characters are required");
        }

        if (!ReconcilableKinds.Contains(request.ExpectedCommandKind))
        {
            throw new AuthorityException(AuthorityErrorCodes.KindNotReconcilable, 422, false, "the command kind is not reconcilable");
        }

        await using var scope = await store.BeginReadOnlySnapshotScopeAsync(cancellationToken).ConfigureAwait(false);
        var state = await scope.ReadOrganizationAsync(request.OrganizationId, cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            return NotFound(request.OrganizationId, correlationId);
        }

        var command = await scope.FindCommandByIdempotencyKeyAsync(state.OrganizationId, request.IdempotencyKey, cancellationToken).ConfigureAwait(false);
        if (command is null || !string.Equals(command.CommandKindName, request.ExpectedCommandKind.ToString(), StringComparison.Ordinal))
        {
            return NotFound(request.OrganizationId, correlationId);
        }

        var commandState = Enum.Parse<CommandStatus>(command.CommandStateName);
        var (teamId, memberUserId) = ReadPair(command.CommandPayloadJson);
        var gate = await OrganizationGate.EvaluateAsync(scope, state, cancellationToken).ConfigureAwait(false);
        var organizationState = new SecurityCommandOutcomeOrganizationState(state.AuthorizationRevision, state.ProjectionAppliedRevision, gate, state.IntegrityQuarantineEnabled);

        SecurityCommandOutcomeMembership? membership = null;
        if (commandState != CommandStatus.Rejected && !state.IntegrityQuarantineEnabled && teamId is { } team && memberUserId is { } member)
        {
            membership = await EvaluatePairAsync(scope, state.OrganizationId, team, member, state.ProjectionAppliedRevision, cancellationToken).ConfigureAwait(false);
        }

        return new SecurityCommandOutcome(
            true,
            correlationId,
            state.OrganizationId,
            command.SecurityCommandId,
            request.ExpectedCommandKind,
            commandState,
            commandState == CommandStatus.Rejected ? null : command.CommittedAuthorizationRevision,
            command.RejectionReasonCode,
            teamId,
            memberUserId,
            organizationState,
            membership);
    }

    /// <summary>
    /// Package §1.2.4: at P the pair is an effective member iff some edge was created at a revision ≤ P and not removed at a
    /// revision ≤ P. E is the largest create or remove revision ≤ P (0 when none); a create or remove above P is a later change.
    /// </summary>
    internal static async Task<SecurityCommandOutcomeMembership> EvaluatePairAsync(IAuthorityScope scope, Guid organizationId, Guid teamId, Guid memberUserId, long appliedRevision, CancellationToken cancellationToken)
    {
        var edges = await scope.GetRelationshipEdgeHistoryAsync(organizationId, ResourceTypeNames.Team, teamId, TeamMemberRelation, ResourceTypeNames.User, memberUserId, cancellationToken).ConfigureAwait(false);
        var commandIds = edges.Select(e => e.CreatedBySecurityCommandId)
            .Concat(edges.Where(e => e.RemovedBySecurityCommandId is not null).Select(e => e.RemovedBySecurityCommandId!.Value))
            .Distinct().ToList();
        var revisions = commandIds.Count == 0 ? [] : await scope.GetCommittedRevisionsAsync(organizationId, commandIds, cancellationToken).ConfigureAwait(false);

        long Revision(Guid commandId) =>
            revisions.TryGetValue(commandId, out var revision) && revision is { } value
                ? value
                : throw AuthorityException.NotReady("relationship history references an uncommitted command"); // never guess: fail closed, retryable

        var effective = false;
        var effectiveRevision = 0L;
        var laterChange = false;
        foreach (var edge in edges)
        {
            var created = Revision(edge.CreatedBySecurityCommandId);
            long? removed = edge.RemovedBySecurityCommandId is { } removedBy ? Revision(removedBy) : null;
            if (created <= appliedRevision)
            {
                effectiveRevision = Math.Max(effectiveRevision, created);
                if (removed is null || removed > appliedRevision)
                {
                    effective = true;
                }
            }
            else
            {
                laterChange = true;
            }

            if (removed is { } removal)
            {
                if (removal <= appliedRevision)
                {
                    effectiveRevision = Math.Max(effectiveRevision, removal);
                }
                else
                {
                    laterChange = true;
                }
            }
        }

        return new SecurityCommandOutcomeMembership(appliedRevision, effective, effectiveRevision, laterChange);
    }

    /// <summary>Team and member from the Authority's stored command payload (normalized for committed commands, raw for rejected ones); unreadable ⇒ null.</summary>
    internal static (Guid? TeamId, Guid? MemberUserId) ReadPair(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var payload = CanonicalJson.Deserialize<TeamMemberPayload>(document.RootElement);
            return (payload.TeamId == Guid.Empty ? null : payload.TeamId, payload.MemberUserId == Guid.Empty ? null : payload.MemberUserId);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException or AuthorityException)
        {
            return (null, null);
        }
    }

    private static SecurityCommandOutcome NotFound(Guid organizationId, Guid correlationId) => new(false, correlationId, organizationId, null, null, null, null, null, null, null, null, null);
}
