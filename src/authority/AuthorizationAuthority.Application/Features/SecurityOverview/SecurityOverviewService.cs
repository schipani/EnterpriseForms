using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.SecurityCommands;

namespace AuthorizationAuthority.Application.Features.SecurityOverview;

/// <summary>
/// C8 M4-4 — AC-8.4 security overview (PHASE_C8_M4_AC8_SECURITY_CONTRACT.md §5.4). One section per request, read with the
/// authorization in <b>one</b> <c>REPEATABLE READ READ ONLY</c> AuthorityDb snapshot: no lock, no write, no journal or outbox
/// row, no SpiceDB call. Role matrix: <c>gate</c> and <c>commands</c> need <c>system_administrator</c>; <c>archive</c> needs
/// <c>system_administrator</c> or <c>security_auditor</c> (D8-70), all under <see cref="EffectiveEdgeRule"/>. An unknown,
/// unbound, disabled or unauthorized caller, and another organization, get the same 404. Content is counts, codes, kinds,
/// revisions and times only (AC-M4-4.2); the overview is operational state, not verified evidence or a compliance result.
/// </summary>
public sealed class SecurityOverviewService(IAuthorityStore store, TimeProvider clock)
{
    public const int CommandListLimit = 20;

    public const string AuthorizationBasis =
        "Authorized from the Authorization Authority's committed desired state (Effective-edge rule: enabled membership, current role edge, Effective granting command), "
        + "read in one database snapshot; available while the organization gate is closed.";

    public const string CommandListStatement =
        "Unresolved commands only (Pending, CommittedAwaitingProjection, FailedReconciliation), oldest first, at most 20. Kind, state, revision and times only; "
        + "rejected and effective commands are in the security-command timeline.";

    public const string ArchiveStatement =
        "Delivery state of the Authority journal streams to the configured archive. In Development/Testing the archive is in-memory or Azurite: neither is immutable, "
        + "write-once or a confidential ledger, and \"Anchored\" means only that a local archive receipt was recorded (RR-6). No production immutability claim.";

    public static readonly IReadOnlyList<string> UnresolvedStates = [SecurityCommandStates.Pending, SecurityCommandStates.CommittedAwaitingProjection, SecurityCommandStates.FailedReconciliation];

    public static readonly IReadOnlyList<string> Streams = [JournalStreamNames.Authorization, JournalStreamNames.Content, JournalStreamNames.Audit];

    /// <summary>The roles that may read each section (§5.4 table).</summary>
    public static IReadOnlyList<string> RolesFor(string section) => section switch
    {
        SecurityOverviewSections.Gate or SecurityOverviewSections.Commands => [OrganizationRoleNames.SystemAdministrator],
        SecurityOverviewSections.Archive => [OrganizationRoleNames.SystemAdministrator, OrganizationRoleNames.SecurityAuditor],
        _ => throw AuthorityException.Invalid("security overview: section must be gate, commands or archive"),
    };

    public async Task<SecurityOverviewResponse> ReadAsync(VerifiedActor actor, string? section, SecurityOverviewRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (request is null || request.OrganizationId == Guid.Empty || request.CorrelationId == Guid.Empty || section is null)
        {
            throw AuthorityException.Invalid("security overview: organization, correlation id and section are required");
        }

        var roles = RolesFor(section);
        await using var snapshot = await store.BeginReadOnlySnapshotScopeAsync(cancellationToken).ConfigureAwait(false);
        var state = await snapshot.ReadOrganizationAsync(request.OrganizationId, cancellationToken).ConfigureAwait(false);
        if (state is null || !await EffectiveEdgeRule.HoldsAnyAsync(snapshot, state.OrganizationId, actor, roles, cancellationToken).ConfigureAwait(false))
        {
            throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false);
        }

        var now = clock.GetUtcNow();
        SecurityOverviewGate? gate = null;
        SecurityOverviewCommands? commands = null;
        SecurityOverviewArchive? archive = null;
        switch (section)
        {
            case SecurityOverviewSections.Gate:
                var gateState = await OrganizationGate.EvaluateAsync(snapshot, state, cancellationToken).ConfigureAwait(false);
                gate = new SecurityOverviewGate(state.AuthorizationRevision, state.ProjectionAppliedRevision, state.ContentIntegrityRevision, gateState.ToString(), state.IntegrityQuarantineEnabled);
                break;
            case SecurityOverviewSections.Commands:
                commands = await CommandsAsync(Overview(snapshot), state.OrganizationId, now, cancellationToken).ConfigureAwait(false);
                break;
            default:
                archive = await ArchiveAsync(Overview(snapshot), state.OrganizationId, now, cancellationToken).ConfigureAwait(false);
                break;
        }

        return new SecurityOverviewResponse(state.OrganizationId, section, AuthorizationBasis, now, gate, commands, archive, request.CorrelationId);
    }

    private static ISecurityOverviewScope Overview(IAuthorityScope snapshot) =>
        snapshot as ISecurityOverviewScope ?? throw new AuthorityException(AuthorityErrorCodes.AuthorityNotReady, 503, true, "the store cannot read the security overview");

    private static async Task<SecurityOverviewCommands> CommandsAsync(ISecurityOverviewScope overview, Guid organizationId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var tallies = (await overview.CountCommandsByStateAsync(organizationId, UnresolvedStates, cancellationToken).ConfigureAwait(false))
            .Where(t => UnresolvedStates.Contains(t.CommandStateName, StringComparer.Ordinal)).ToList();
        var counts = UnresolvedStates.Select(s => new SecurityOverviewStateCount(s, tallies.Where(t => t.CommandStateName == s).Sum(t => t.CommandCount))).ToList();
        DateTimeOffset? oldest = tallies.Count == 0 ? null : tallies.Min(t => t.OldestCreatedOn);
        var rows = await overview.GetOldestCommandsInStatesAsync(organizationId, UnresolvedStates, CommandListLimit, cancellationToken).ConfigureAwait(false);
        var listed = rows
            .Where(c => c.OrganizationId == organizationId && UnresolvedStates.Contains(c.CommandStateName, StringComparer.Ordinal))
            .Take(CommandListLimit)
            .Select(c => new SecurityOverviewCommand(c.CommandKindName, c.CommandStateName, c.CommittedAuthorizationRevision, c.CommandCreatedOn, AgeSeconds(now, c.CommandCreatedOn)))
            .ToList();
        return new SecurityOverviewCommands(counts, oldest, oldest is { } o ? AgeSeconds(now, o) : null, listed, CommandListLimit, CommandListStatement);
    }

    private static async Task<SecurityOverviewArchive> ArchiveAsync(ISecurityOverviewScope overview, Guid organizationId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var tallies = await overview.CountJournalArchiveStatesAsync(organizationId, cancellationToken).ConfigureAwait(false);
        var streams = Streams.Select(stream =>
        {
            var of = tallies.Where(t => t.StreamName == stream).ToList();
            int Count(string deliveryState) => of.Where(t => t.ArchiveDeliveryState == deliveryState).Sum(t => t.EntryCount);
            var pending = of.Where(t => t.ArchiveDeliveryState == ArchiveDeliveryStates.Pending && t.EntryCount > 0).ToList();
            DateTimeOffset? oldestPending = pending.Count == 0 ? null : pending.Min(t => t.OldestCreatedOn);
            var lastAnchored = of.Where(t => t.ArchiveDeliveryState == ArchiveDeliveryStates.Anchored).Select(t => t.LatestArchivedOn).Where(d => d is not null).Max();
            return new SecurityOverviewArchiveStreamState(stream, Count(ArchiveDeliveryStates.Pending), Count(ArchiveDeliveryStates.Anchored), Count(ArchiveDeliveryStates.VerificationFailed),
                oldestPending, oldestPending is { } o ? AgeSeconds(now, o) : null, lastAnchored);
        }).ToList();
        return new SecurityOverviewArchive(streams, ArchiveStatement);
    }

    private static long AgeSeconds(DateTimeOffset now, DateTimeOffset createdOn) => Math.Max(0L, (long)Math.Floor((now - createdOn).TotalSeconds));
}
