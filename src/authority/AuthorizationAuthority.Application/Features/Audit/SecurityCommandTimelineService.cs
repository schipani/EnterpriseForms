using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Authorization;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;

namespace AuthorizationAuthority.Application.Features.Audit;

/// <summary>
/// C8 M4-3 (AC-8.2, contract §5.2 as amended by C-165): one page of the security-command timeline, under the M4-2 audit-access
/// protocol (<see cref="AuditAccessService.AuthorizeAndReadAsync"/>: view_audit, bounded transaction, NOWAIT organization lock,
/// revision comparison, committed <c>AuditAccessAuthorized</c> with <c>served_by: authority</c> before any answer; TC-3 bounds
/// and rate limits unchanged).
/// <para>The unit of the timeline is the command row (every state, including Rejected and FailedReconciliation), merged with
/// allowlisted audit-stream entries; order is (event time, source, key) descending. A command row is journal-backed when its
/// authorization-stream entry (committed commands) or its audit-stream rejection entry (rejected role, membership and
/// field-search commands) exists; otherwise it is shown as command-row-only, which is expected for most rejected commands and is
/// not a sign of tampering. Neither basis claims that the command row itself is cryptographically verified: a command item's
/// kind, state, revision and details are read from the mutable command row (a committed transfer's previous owner from its
/// journal entry); journal-backed only states that an entry naming the command was found, without comparing the row's fields to
/// it or re-verifying the journal chain on this read.</para>
/// </summary>
public sealed class SecurityCommandTimelineService(AuditAccessService access)
{
    public const int MaximumKindFilters = 40;

    /// <summary>A rejection entry is written in its command's transaction; this window only bounds the lookup.</summary>
    private static readonly TimeSpan RejectionWindow = TimeSpan.FromMinutes(1);

    public async Task<SecurityCommandTimelinePage> ReadAsync(VerifiedActor actor, SecurityCommandTimelineRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var filter = Validate(request);
        var accessRequest = new AuditTimelineAccess(request.OrganizationId, filter.Digest, request.PageSize, request.CorrelationId);
        var (decision, page) = await access.AuthorizeAndReadTimelineAsync(actor, accessRequest, (scope, state, token) => ReadPageAsync(scope, state, filter, request.PageSize, token), cancellationToken).ConfigureAwait(false);
        return new SecurityCommandTimelinePage(page.Items, page.After, decision.AuditJournalRevision, decision.AuthorizationRevision, AuditAccessService.ServedByAuthority, AuditAccessService.NotAttested);
    }

    /// <summary>The validated filter, the included kinds per source and the canonical filter digest (ids, codes and dates only).</summary>
    public sealed record TimelineFilter(IReadOnlyCollection<string> CommandKinds, IReadOnlyCollection<string> AuditKinds, IReadOnlyCollection<string>? States, DateTimeOffset? From, DateTimeOffset? To, TimelinePosition? After, string Digest);

    public static TimelineFilter Validate(SecurityCommandTimelineRequest? request)
    {
        if (request is null || request.OrganizationId == Guid.Empty || request.CorrelationId == Guid.Empty || request.PageSize is < 1 or > AuditAccessService.MaximumPageSize)
        {
            throw AuthorityException.Invalid("timeline: organization, correlation id and page_size 1..50 are required");
        }

        var kinds = request.Kinds is { Count: > 0 } requested ? requested.Distinct(StringComparer.Ordinal).ToList() : null;
        if (kinds is not null && (kinds.Count > MaximumKindFilters || kinds.Any(k => k is null || !SecurityCommandTimelineCatalog.IsFilterableKind(k))))
        {
            throw AuthorityException.Invalid("timeline: every kind must be a timeline kind");
        }

        var states = request.States is { Count: > 0 } requestedStates ? requestedStates.Distinct(StringComparer.Ordinal).ToList() : null;
        if (states is not null && states.Any(s => s is null || !SecurityCommandTimelineCatalog.CommandStates.Contains(s)))
        {
            throw AuthorityException.Invalid("timeline: every state must be a command state");
        }

        var from = request.From?.ToUniversalTime();
        var to = request.To?.ToUniversalTime();
        if (from is not null && to is not null && to <= from)
        {
            throw AuthorityException.Invalid("timeline: to must be after from");
        }

        var after = request.After is null ? null : DecodePosition(request.After) ?? throw AuthorityException.Invalid("timeline: after is not a timeline position");
        var commandKinds = (kinds ?? [.. SecurityCommandTimelineCatalog.CommandKinds.Keys]).Where(SecurityCommandTimelineCatalog.IsIncludedCommand).Order(StringComparer.Ordinal).ToList();

        // A state filter selects command rows only: audit-stream entries have no state.
        var auditKinds = states is not null ? [] : (kinds ?? [.. SecurityCommandTimelineCatalog.AuditKinds.Keys]).Where(SecurityCommandTimelineCatalog.AuditKinds.ContainsKey).Order(StringComparer.Ordinal).ToList();
        var canonical = string.Join('|', "security-command-timeline/v1",
            kinds is null ? "-" : string.Join(',', kinds.Order(StringComparer.Ordinal)),
            states is null ? "-" : string.Join(',', states.Order(StringComparer.Ordinal)),
            from?.ToString("O", CultureInfo.InvariantCulture) ?? "-",
            to?.ToString("O", CultureInfo.InvariantCulture) ?? "-",
            request.PageSize.ToString(CultureInfo.InvariantCulture));
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return new TimelineFilter(commandKinds, auditKinds, states?.Order(StringComparer.Ordinal).ToList(), from, to, after, digest);
    }

    private sealed record PageResult(IReadOnlyList<SecurityCommandTimelineEntry> Items, string? After);

    private static async Task<PageResult> ReadPageAsync(IAuthorityScope scope, OrganizationAuthorizationState state, TimelineFilter filter, int pageSize, CancellationToken cancellationToken)
    {
        var timeline = scope as ISecurityCommandTimelineScope ?? throw new AuthorityException(AuthorityErrorCodes.AuditEvidenceUnavailable, 503, true, "the store cannot read the security-command timeline");
        var organizationId = state.OrganizationId;
        var range = new TimelineRange(filter.From, filter.To, filter.After);

        // Each source is filtered, ordered and limited in the database; pageSize + 1 from each is enough for the merged page.
        var commands = filter.CommandKinds.Count == 0 ? [] : await timeline.GetTimelineCommandsAsync(organizationId, range, filter.CommandKinds, filter.States, pageSize + 1, cancellationToken).ConfigureAwait(false);
        var entries = filter.AuditKinds.Count == 0 ? [] : await timeline.GetTimelineAuditEntriesAsync(organizationId, range, filter.AuditKinds, pageSize + 1, cancellationToken).ConfigureAwait(false);

        var merged = commands.Select(c => (Position: new TimelinePosition(c.CommandCreatedOn, true, c.SecurityCommandId, 0), Command: (SecurityCommand?)c, Entry: (SecurityJournalEntry?)null))
            .Concat(entries.Select(e => (Position: new TimelinePosition(e.JournalCreatedOn, false, Guid.Empty, e.JournalStreamRevision), Command: (SecurityCommand?)null, Entry: (SecurityJournalEntry?)e)))
            .OrderByDescending(i => i.Position, PositionComparer.Instance)
            .Take(pageSize + 1)
            .ToList();
        var hasMore = merged.Count > pageSize;
        var page = merged.Take(pageSize).ToList();

        var pageCommands = page.Where(i => i.Command is not null).Select(i => i.Command!).ToList();
        var authorizationEvidence = await AuthorizationEvidenceAsync(timeline, organizationId, pageCommands, cancellationToken).ConfigureAwait(false);
        var rejectionEvidence = await RejectionEvidenceAsync(timeline, organizationId, pageCommands, cancellationToken).ConfigureAwait(false);
        var users = pageCommands.Count == 0 ? new Dictionary<Guid, Guid>()
            : (await scope.GetIdentityBindingsAsync(organizationId, cancellationToken).ConfigureAwait(false)).ToDictionary(b => b.IdentityBindingId, b => b.AuthorityUserId);

        var items = page.Select(i => i.Command is { } command
            ? CommandEntry(command, authorizationEvidence.GetValueOrDefault(command.SecurityCommandId) ?? rejectionEvidence.GetValueOrDefault(command.SecurityCommandId),
                authorizationEvidence.ContainsKey(command.SecurityCommandId), users)
            : AuditEntry(i.Entry!)).ToList();
        return new PageResult(items, hasMore ? EncodePosition(page[^1].Position) : null);
    }

    /// <summary>A committed command's authorization-stream entry is at its committed revision; it must name the command.</summary>
    private static async Task<Dictionary<Guid, SecurityJournalEntry>> AuthorizationEvidenceAsync(ISecurityCommandTimelineScope timeline, Guid organizationId, List<SecurityCommand> commands, CancellationToken cancellationToken)
    {
        var committed = commands.Where(c => c.CommittedAuthorizationRevision is not null && c.CommandStateName != SecurityCommandStates.Rejected).ToList();
        if (committed.Count == 0)
        {
            return [];
        }

        var rows = await timeline.GetAuthorizationEntriesAsync(organizationId, [.. committed.Select(c => c.CommittedAuthorizationRevision!.Value).Distinct()], cancellationToken).ConfigureAwait(false);
        var byRevision = rows.Where(r => r.JournalStreamName == JournalStreamNames.Authorization).ToDictionary(r => r.JournalStreamRevision);
        var evidence = new Dictionary<Guid, SecurityJournalEntry>();
        foreach (var command in committed)
        {
            if (byRevision.TryGetValue(command.CommittedAuthorizationRevision!.Value, out var row) && row.SecurityCommandId == command.SecurityCommandId)
            {
                evidence[command.SecurityCommandId] = row;
            }
        }

        return evidence;
    }

    /// <summary>The audit-stream rejection entry naming a rejected command (role, membership and field-search kinds only).</summary>
    private static async Task<Dictionary<Guid, SecurityJournalEntry>> RejectionEvidenceAsync(ISecurityCommandTimelineScope timeline, Guid organizationId, List<SecurityCommand> commands, CancellationToken cancellationToken)
    {
        var rejected = commands.Where(c => c.CommandStateName == SecurityCommandStates.Rejected && HasRejectionEntry(c.CommandKindName)).ToList();
        if (rejected.Count == 0)
        {
            return [];
        }

        var rows = await timeline.GetRejectionEntriesAsync(organizationId, SecurityCommandTimelineCatalog.RejectionEvidenceKinds, [.. rejected.Select(c => c.SecurityCommandId)],
            rejected.Min(c => c.CommandCreatedOn) - RejectionWindow, rejected.Max(c => c.CommandCreatedOn) + RejectionWindow, cancellationToken).ConfigureAwait(false);
        var evidence = new Dictionary<Guid, SecurityJournalEntry>();
        foreach (var row in rows.Where(r => r.JournalStreamName == JournalStreamNames.Audit && SecurityCommandTimelineCatalog.RejectionEvidenceKinds.Contains(r.JournalEventKind)))
        {
            if (SecurityCommandTimelineCatalog.NamedCommandId(row.JournalEventBodyJson) is { } id && rejected.Any(c => c.SecurityCommandId == id))
            {
                evidence.TryAdd(id, row);
            }
        }

        return evidence;
    }

    /// <summary>The kinds whose rejection is also an audit-stream entry (SecurityCommandService, A12/OD-8/C-110/AC-7).</summary>
    private static bool HasRejectionEntry(string kind) => kind is nameof(SecurityCommandKind.ApproveFieldSearch) or nameof(SecurityCommandKind.WithdrawFieldSearch)
        or nameof(SecurityCommandKind.UpdateFieldSearchers) or nameof(SecurityCommandKind.GrantSystemRole) or nameof(SecurityCommandKind.RevokeSystemRole)
        or nameof(SecurityCommandKind.GrantOrganizationRole) or nameof(SecurityCommandKind.RevokeOrganizationRole) or nameof(SecurityCommandKind.ProposeOrganizationOwnerChange)
        or nameof(SecurityCommandKind.DisableIdentityBinding) or nameof(SecurityCommandKind.EnableIdentityBinding) or nameof(SecurityCommandKind.RequestOrganizationMembership)
        or nameof(SecurityCommandKind.CancelOrganizationMembershipRequest) or nameof(SecurityCommandKind.ReplaceOrganizationMembershipRequest);

    private static SecurityCommandTimelineEntry CommandEntry(SecurityCommand command, SecurityJournalEntry? evidence, bool committedEvidence, Dictionary<Guid, Guid> users)
    {
        var envelope = SecurityCommandTimelineCatalog.BuildCommandEnvelope(command.CommandKindName, command.CommandPayloadJson, command.SecurityCommandId, command.CommandStateName,
            committedEvidence ? evidence?.JournalEventBodyJson : null);
        return new SecurityCommandTimelineEntry(
            SecurityCommandTimelineCatalog.CommandSource,
            command.CommandKindName,
            command.CommandCreatedOn,
            command.SecurityCommandId,
            command.CommandStateName,
            command.CommittedAuthorizationRevision,
            command.CommandStateName == SecurityCommandStates.Rejected ? RejectionCode(command.RejectionReasonCode) : null,
            users.TryGetValue(command.ActorIdentityBindingId, out var userId) ? userId : null,
            command.CommandEffectiveOn,
            evidence is null ? SecurityCommandTimelineCatalog.CommandRowOnly : SecurityCommandTimelineCatalog.JournalBacked,
            SecurityCommandTimelineCatalog.ReportedByAuthority,
            evidence is null ? null : Evidence(evidence),
            envelope.Details,
            envelope.WithheldFields);
    }

    private static SecurityCommandTimelineEntry AuditEntry(SecurityJournalEntry entry)
    {
        var envelope = SecurityCommandTimelineCatalog.BuildAuditEnvelope(entry.JournalEventKind, entry.JournalEventBodyJson);
        var rule = SecurityCommandTimelineCatalog.AuditKinds[entry.JournalEventKind];
        return new SecurityCommandTimelineEntry(
            SecurityCommandTimelineCatalog.AuditStreamSource,
            entry.JournalEventKind,
            entry.JournalCreatedOn,
            null,
            null,
            null,
            null,
            null,
            null,
            SecurityCommandTimelineCatalog.JournalBacked,
            rule.ReportedByBusinessRuntime ? SecurityCommandTimelineCatalog.ReportedByBusinessRuntime : SecurityCommandTimelineCatalog.ReportedByAuthority,
            Evidence(entry),
            envelope.Details,
            envelope.WithheldFields);
    }

    private static SecurityCommandTimelineEvidence Evidence(SecurityJournalEntry entry) =>
        new(entry.JournalStreamName, entry.JournalStreamRevision, Convert.ToHexStringLower(entry.JournalEventDigest), entry.ArchiveDeliveryState, !string.IsNullOrEmpty(entry.ArchiveReceiptReference));

    /// <summary>Rejection codes are structured error codes; anything else is not shown.</summary>
    private static string? RejectionCode(string? code) =>
        code is { Length: > 0 and <= 80 } && code.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-') ? code : null;

    /// <summary>The Authority's keyset position, "v1:{utc ticks}:{C|A}:{command id | audit revision}" (the business wraps it in its protected cursor).</summary>
    public static string EncodePosition(TimelinePosition position) => string.Create(CultureInfo.InvariantCulture,
        $"v1:{position.OccurredOn.UtcTicks}:{(position.IsCommand ? 'C' : 'A')}:{(position.IsCommand ? position.CommandId.ToString("D") : position.StreamRevision.ToString(CultureInfo.InvariantCulture))}");

    public static TimelinePosition? DecodePosition(string token)
    {
        var parts = token.Split(':');
        if (parts.Length != 4 || parts[0] != "v1" || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks)
        {
            return null;
        }

        var at = new DateTimeOffset(ticks, TimeSpan.Zero);
        return parts[2] switch
        {
            "C" when Guid.TryParseExact(parts[3], "D", out var id) => new TimelinePosition(at, true, id, 0),
            "A" when long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var revision) && revision > 0 => new TimelinePosition(at, false, Guid.Empty, revision),
            _ => null,
        };
    }

    /// <summary>(event time, command rows above audit entries, key) ascending; the page sorts it descending.</summary>
    public sealed class PositionComparer : IComparer<TimelinePosition>
    {
        public static readonly PositionComparer Instance = new();

        public int Compare(TimelinePosition? x, TimelinePosition? y)
        {
            ArgumentNullException.ThrowIfNull(x);
            ArgumentNullException.ThrowIfNull(y);
            var byTime = x.OccurredOn.UtcTicks.CompareTo(y.OccurredOn.UtcTicks);
            if (byTime != 0)
            {
                return byTime;
            }

            if (x.IsCommand != y.IsCommand)
            {
                return x.IsCommand ? 1 : -1;
            }

            return x.IsCommand ? CompareGuids(x.CommandId, y.CommandId) : x.StreamRevision.CompareTo(y.StreamRevision);
        }

        /// <summary>PostgreSQL uuid order (byte-wise, big-endian), which is the text order of the canonical form.</summary>
        private static int CompareGuids(Guid a, Guid b) => string.CompareOrdinal(a.ToString("D"), b.ToString("D"));
    }
}
