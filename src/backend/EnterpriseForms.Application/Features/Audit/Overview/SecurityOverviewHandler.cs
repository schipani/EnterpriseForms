using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Contracts.Features;

namespace EnterpriseForms.Application.Features.Audit.Overview;

/// <summary>
/// C8 M4-4 (AC-8.4, AC-8.5; contract §6.1): the gate, commands, archive and coverage sections of the Security Home (PG-052). The
/// Authority authorizes each read with the Effective-edge rule in one read-only snapshot and builds its content; this handler asks
/// with the session's organization only, adds business directory form names to coverage rows (OD-3), copies the Authority's fields
/// one by one and writes nothing. No permission is cached: every request is authorized again. Incidents and search approvals are
/// served by their own endpoints and checks (OD-6), not through this handler.
/// </summary>
public sealed class SecurityOverviewHandler(IBusinessStore store, ISecurityOverviewAuthority authority)
{
    public const string Gate = "gate";
    public const string Commands = "commands";
    public const string Archive = "archive";
    public const string Coverage = "coverage";

    public static readonly IReadOnlyList<string> Sections = [Gate, Commands, Archive, Coverage];

    public const string FormNameStatement = "Form names are the business directory's current names; fields are identified by id only.";

    /// <summary>The section path value, or 404 for anything else (no section name is reflected).</summary>
    public static string Normalize(string? section) =>
        section is not null && Sections.Contains(section, StringComparer.Ordinal) ? section : throw new BusinessException("NOT_FOUND", 404, "not found");

    public async Task<SecurityOverviewSectionResponse> ReadAsync(RequestContext context, string section, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        section = Normalize(section);
        var query = new SecurityOverviewQuery(context.OrganizationId, context.CorrelationId);
        try
        {
            if (section == Coverage)
            {
                var coverage = await authority.ReadProtectionCoverageAsync(context.ToDelegatedUser(), query, cancellationToken).ConfigureAwait(false);
                RequireOrganization(coverage.OrganizationId, context);
                return new SecurityOverviewSectionResponse(Coverage, coverage.AuthorizationBasis, coverage.ComputedOn, null, null, null, await CoverageAsync(context, coverage, cancellationToken).ConfigureAwait(false));
            }

            var overview = await authority.ReadSecurityOverviewAsync(context.ToDelegatedUser(), section, query, cancellationToken).ConfigureAwait(false);
            RequireOrganization(overview.OrganizationId, context);
            if (!string.Equals(overview.Section, section, StringComparison.Ordinal))
            {
                throw Unavailable();
            }

            return section switch
            {
                Gate => new SecurityOverviewSectionResponse(Gate, overview.AuthorizationBasis, overview.ComputedOn, GateOf(overview.Gate ?? throw Unavailable()), null, null, null),
                Commands => new SecurityOverviewSectionResponse(Commands, overview.AuthorizationBasis, overview.ComputedOn, null, CommandsOf(overview.Commands ?? throw Unavailable()), null, null),
                _ => new SecurityOverviewSectionResponse(Archive, overview.AuthorizationBasis, overview.ComputedOn, null, null, ArchiveOf(overview.Archive ?? throw Unavailable()), null),
            };
        }
        catch (AuthorityClientException ex)
        {
            throw ex.HttpStatusCode switch
            {
                // The Authority answers 404 to anyone not authorized for the section; the caller's own organization exists, so 403.
                404 or 403 => section == Archive
                    ? new BusinessException("SECURITY_ADMINISTRATION_FORBIDDEN", 403, "archive health is available to Security Administrators and Security Auditors")
                    : new BusinessException("SECURITY_ADMINISTRATION_FORBIDDEN", 403, "this section is available to Security Administrators"),
                422 => new BusinessException("INVALID_REQUEST", 422, "this section request is not valid"),
                429 => new BusinessException("RATE_LIMITED", 429, "too many requests; wait a moment and try again"),
                _ => new BusinessException("AUTHORITY_UNAVAILABLE", 503, "the authorization authority is unavailable; this section cannot be shown"),
            };
        }
    }

    private static void RequireOrganization(Guid answered, RequestContext context)
    {
        if (answered != context.OrganizationId)
        {
            throw Unavailable(); // fail closed: never show another organization's answer
        }
    }

    private static BusinessException Unavailable() => new("AUTHORITY_UNAVAILABLE", 503, "the authorization authority returned an unexpected answer; this section cannot be shown");

    private static SecurityOverviewGateResponse GateOf(AuthorityOverviewGate g) => new(g.AuthorizationRevision, g.ProjectionAppliedRevision, g.ContentIntegrityRevision, g.GateState, g.IntegrityQuarantined);

    private static SecurityOverviewCommandsResponse CommandsOf(AuthorityOverviewCommands c) => new(
        [.. c.StateCounts.Select(s => new SecurityOverviewStateCountResponse(s.CommandState, s.CommandCount))],
        c.OldestUnresolvedCreatedOn,
        c.OldestUnresolvedAgeSeconds,
        [.. c.Commands.Select(x => new SecurityOverviewCommandResponse(x.CommandKind, x.CommandState, x.CommittedAuthorizationRevision, x.CreatedOn, x.AgeSeconds))],
        c.ListLimit,
        c.ListStatement);

    private static SecurityOverviewArchiveResponse ArchiveOf(AuthorityOverviewArchive a) => new(
        [.. a.Streams.Select(s => new SecurityOverviewArchiveStreamResponse(s.StreamName, s.PendingCount, s.AnchoredCount, s.VerificationFailedCount, s.OldestPendingCreatedOn, s.OldestPendingAgeSeconds, s.LastAnchoredOn))],
        a.ArchiveStatement);

    private async Task<SecurityCoverageResponse> CoverageAsync(RequestContext context, AuthoritySecurityCoverage coverage, CancellationToken cancellationToken)
    {
        Dictionary<Guid, string> forms;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            forms = (await scope.GetFormDefinitionsAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false)).ToDictionary(f => f.FormDefinitionId, f => f.FormDefinitionName);
        }

        var decisions = coverage.Decisions.Select(d => new SecurityCoverageDecisionResponse(
            d.SecurityCommandId,
            d.FormDefinitionId,
            forms.GetValueOrDefault(d.FormDefinitionId),
            d.FieldDefinitionId,
            d.ProtectionDirection,
            d.FormRegisteredActive,
            d.AttestationState,
            d.Attestation is not { } a ? null : new SecurityCoverageAttestationResponse(a.AttestationId, a.AttestationComplete, a.AttestationIncompleteReasonCode, a.ExpectedRecordCount, a.AcceptedRecordCount,
                a.CompliantRecordCount, a.PendingCreationRecordCount, a.QuarantinedRecordCount, a.UnverifiedRecordCount, a.NotYetCensusedRecordCount, a.RetiredRecordCount, a.RetainedHistoryRevisionCount,
                a.AuditJournalRevision, a.AttestedContentIntegrityRevision, a.AttestedOn))).ToList();
        return new SecurityCoverageResponse(coverage.RegisteredFormCount, coverage.DecisionCount, coverage.AttestedCompleteDecisionCount, coverage.MissingDecisionCount, coverage.StaleDecisionCount,
            coverage.OrganizationContentIntegrityRevision, decisions, coverage.CoverageStatement, coverage.StalenessStatement, FormNameStatement);
    }
}
