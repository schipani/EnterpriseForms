using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using AuthorizationAuthority.Domain.Features.Sharing;

namespace AuthorizationAuthority.Application.Features.Lineage;

/// <summary>
/// C10 M1-WP10: the raw, read-only facts of one organization read in a single <c>REPEATABLE READ, READ ONLY</c> transaction.
/// Edges include removed rows: historical ownership is derived from their creating and removing commands. Command facts carry
/// no payload, reason text or request digest.
/// </summary>
public sealed record LineageStoreRows
{
    public required Guid OrganizationId { get; init; }

    public required long AuthorizationRevision { get; init; }

    public required DateTimeOffset ObservedOn { get; init; }

    /// <summary>Active <c>record_share</c> resources of the organization.</summary>
    public IReadOnlyList<AuthorityResource> ActiveShares { get; init; } = [];

    public IReadOnlyList<ShareResourceMetadata> ShareMetadata { get; init; } = [];

    public IReadOnlyList<ShareDocumentBinding> DocumentBindings { get; init; } = [];

    /// <summary>Share recipient, <c>field_item#shared_via</c>, <c>form_record#owner</c> and <c>team#member</c> edges, removed ones included.</summary>
    public IReadOnlyList<AuthorityRelationshipEdge> Edges { get; init; } = [];

    /// <summary>Every command referenced by a share registration or by one of <see cref="Edges"/>.</summary>
    public IReadOnlyList<LineageCommandFact> Commands { get; init; } = [];

    /// <summary>Identity binding id → Authority user id, for the organization.</summary>
    public IReadOnlyDictionary<Guid, Guid> IdentityBindingUsers { get; init; } = new Dictionary<Guid, Guid>();
}

/// <summary>The identity, kind, state and committed revision of a security command; nothing else.</summary>
public sealed record LineageCommandFact(
    Guid SecurityCommandId,
    Guid OrganizationId,
    Guid ActorIdentityBindingId,
    string CommandKindName,
    string CommandStateName,
    long? CommittedAuthorizationRevision);

/// <summary>C10 M1-WP10: the verified per-share inputs of the dry run, assembled from <see cref="LineageStoreRows"/>.</summary>
public sealed record LineageEvidenceSnapshot
{
    public required Guid OrganizationId { get; init; }

    public required long AuthorizationRevision { get; init; }

    public required DateTimeOffset ObservedOn { get; init; }

    public IReadOnlyList<LineageShareEvidence> Shares { get; init; } = [];
}

/// <summary>One Active share with its current scope and its historical registration evidence.</summary>
public sealed record LineageShareEvidence
{
    public required AuthorityResource Share { get; init; }

    public ShareResourceMetadata? Metadata { get; init; }

    /// <summary>Current (not removed) reader, contributor and manager edges of the share.</summary>
    public IReadOnlyList<AuthorityRelationshipEdge> RecipientEdges { get; init; } = [];

    public IReadOnlySet<Guid> FieldItemIds { get; init; } = new HashSet<Guid>();

    public IReadOnlyList<ShareDocumentBinding> DocumentBindings { get; init; } = [];

    /// <summary>Current members of the share's recipient team(s), at the snapshot.</summary>
    public IReadOnlySet<Guid> RecipientTeamMemberUserIds { get; init; } = new HashSet<Guid>();

    /// <summary>The verified registering actor, or null when the registration cannot be attributed.</summary>
    public Guid? GrantorAtRegistration { get; init; }

    /// <summary>Ownership at the registration revision, or null when the registration revision itself is unknown.</summary>
    public OwnerInfoAtRegistration? OwnerAtRegistration { get; init; }

    /// <summary>A share without committed lineage: no metadata row (created before C10) or explicitly flagged unresolved.</summary>
    public bool IsLegacy => Metadata is null || Metadata.LegacyLineageUnresolved;
}

/// <summary>
/// C10 M1-WP10: derives verified historical evidence from read-only store facts. Pure: no I/O.
/// <para>
/// The grantor is the actor of the share's registering command only when that command is an Effective <c>CreateShare</c> of the
/// same organization with a committed revision and a same-organization identity binding. Ownership at registration is the set of
/// <c>form_record#owner</c> edges created before and not removed before the registration revision; it is verifiable only when every
/// owner edge of the record (and, for a team owner, every membership edge of that team) resolves to committed same-organization
/// commands. Anything else is reported as unknown or unverifiable, never guessed.
/// </para>
/// </summary>
public static class LineageEvidenceAssembler
{
    private static readonly HashSet<string> RecipientRelations = new(StringComparer.Ordinal) { "reader", "contributor", "manager" };

    public static LineageEvidenceSnapshot Assemble(LineageStoreRows rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.OrganizationId == Guid.Empty || rows.AuthorizationRevision <= 0)
        {
            throw new InvalidOperationException("the evidence snapshot needs an organization and a positive authorization revision");
        }

        var organization = rows.OrganizationId;
        if (rows.ActiveShares.Any(s => s.OrganizationId != organization) || rows.Edges.Any(e => e.OrganizationId != organization) ||
            rows.ShareMetadata.Any(m => m.OrganizationId != organization) || rows.DocumentBindings.Any(d => d.OrganizationId != organization))
        {
            throw new InvalidOperationException("evidence rows of another organization were supplied");
        }

        var commands = new Dictionary<Guid, LineageCommandFact>();
        foreach (var command in rows.Commands)
        {
            if (!commands.TryAdd(command.SecurityCommandId, command))
            {
                throw new InvalidOperationException("duplicate command fact");
            }
        }

        var metadata = new Dictionary<Guid, ShareResourceMetadata>();
        foreach (var row in rows.ShareMetadata)
        {
            if (!metadata.TryAdd(row.RecordShareId, row))
            {
                throw new InvalidOperationException("duplicate share metadata");
            }
        }

        var shares = new List<LineageShareEvidence>();
        foreach (var share in rows.ActiveShares.OrderBy(s => s.ParentResourceEntityId).ThenBy(s => s.ResourceEntityId))
        {
            if (share.ResourceTypeName != ResourceTypeNames.RecordShare ||
                share.ResourceLifecycleState != ResourceLifecycleStates.Active || share.ParentResourceEntityId is not { } record)
            {
                throw new InvalidOperationException("an evidence share is not an Active record share bound to a record");
            }

            var recipients = rows.Edges.Where(e => e.ResourceTypeName == ResourceTypeNames.RecordShare &&
                                                   e.ResourceEntityId == share.ResourceEntityId &&
                                                   RecipientRelations.Contains(e.RelationshipName) &&
                                                   e.RemovedBySecurityCommandId is null).ToList();
            var recipientTeams = recipients.Where(e => e.SubjectTypeName == ResourceTypeNames.Team).Select(e => e.SubjectEntityId).ToHashSet();
            var registration = RegistrationRevision(share, commands);
            shares.Add(new LineageShareEvidence
            {
                Share = share,
                Metadata = metadata.GetValueOrDefault(share.ResourceEntityId),
                RecipientEdges = recipients,
                FieldItemIds = rows.Edges.Where(e => e.ResourceTypeName == ResourceTypeNames.FieldItem && e.RelationshipName == "shared_via" &&
                                                     e.SubjectTypeName == ResourceTypeNames.RecordShare &&
                                                     e.SubjectEntityId == share.ResourceEntityId && e.RemovedBySecurityCommandId is null)
                    .Select(e => e.ResourceEntityId).ToHashSet(),
                DocumentBindings = rows.DocumentBindings.Where(d => d.RecordShareId == share.ResourceEntityId).ToList(),
                RecipientTeamMemberUserIds = rows.Edges.Where(e => e.ResourceTypeName == ResourceTypeNames.Team && e.RelationshipName == "member" &&
                                                                   recipientTeams.Contains(e.ResourceEntityId) &&
                                                                   e.SubjectTypeName == ResourceTypeNames.User && e.RemovedBySecurityCommandId is null)
                    .Select(e => e.SubjectEntityId).ToHashSet(),
                GrantorAtRegistration = registration is null ? null : Grantor(share, commands, rows.IdentityBindingUsers),
                OwnerAtRegistration = registration is { } revision ? OwnerAt(organization, record, revision, rows.Edges, commands) : null,
            });
        }

        return new LineageEvidenceSnapshot
        {
            OrganizationId = organization,
            AuthorizationRevision = rows.AuthorizationRevision,
            ObservedOn = rows.ObservedOn,
            Shares = shares,
        };
    }

    /// <summary>The committed revision of the share's registering command, when it is an Effective CreateShare of this organization.</summary>
    private static long? RegistrationRevision(AuthorityResource share, Dictionary<Guid, LineageCommandFact> commands) =>
        commands.TryGetValue(share.RegisteredBySecurityCommandId, out var command) &&
        command.OrganizationId == share.OrganizationId &&
        command.CommandKindName == SecurityCommandKinds.CreateShare &&
        command.CommandStateName == SecurityCommandStates.Effective &&
        command.CommittedAuthorizationRevision is > 0
            ? command.CommittedAuthorizationRevision
            : null;

    private static Guid? Grantor(AuthorityResource share, Dictionary<Guid, LineageCommandFact> commands, IReadOnlyDictionary<Guid, Guid> bindings) =>
        bindings.TryGetValue(commands[share.RegisteredBySecurityCommandId].ActorIdentityBindingId, out var user) && user != Guid.Empty ? user : null;

    private static OwnerInfoAtRegistration OwnerAt(
        Guid organization, Guid record, long revision, IReadOnlyList<AuthorityRelationshipEdge> edges, Dictionary<Guid, LineageCommandFact> commands)
    {
        var ownerEdges = edges.Where(e => e.ResourceTypeName == ResourceTypeNames.FormRecord && e.ResourceEntityId == record && e.RelationshipName == "owner").ToList();
        if (!TryActiveAt(ownerEdges, revision, organization, commands, out var owners))
        {
            return new OwnerInfoAtRegistration { IsVerifiable = false };
        }

        var users = owners.Where(e => e.SubjectTypeName == ResourceTypeNames.User && e.SubjectRelationName.Length == 0).Select(e => e.SubjectEntityId).Distinct().ToList();
        var teams = owners.Where(e => e.SubjectTypeName == ResourceTypeNames.Team && e.SubjectRelationName == "participant").Select(e => e.SubjectEntityId).Distinct().ToList();
        if (users.Count + teams.Count != owners.Count || users.Count > 1 || teams.Count > 1)
        {
            // Malformed subjects or more than one owner of one kind: the single-owner model cannot express it.
            return new OwnerInfoAtRegistration { IsVerifiable = false };
        }

        if (teams.Count == 1)
        {
            var memberEdges = edges.Where(e => e.ResourceTypeName == ResourceTypeNames.Team && e.ResourceEntityId == teams[0] && e.RelationshipName == "member").ToList();
            if (!TryActiveAt(memberEdges, revision, organization, commands, out var members) ||
                members.Any(e => e.SubjectTypeName != ResourceTypeNames.User || e.SubjectRelationName.Length != 0))
            {
                return new OwnerInfoAtRegistration { IsVerifiable = false };
            }

            return new OwnerInfoAtRegistration
            {
                OwnerUserId = users.Count == 1 ? users[0] : null, // both set ⇒ the classifier refuses it as ambiguous
                OwnerTeamId = teams[0],
                TeamMemberUserIds = members.Select(e => e.SubjectEntityId).ToHashSet(),
                IsVerifiable = true,
            };
        }

        return new OwnerInfoAtRegistration { OwnerUserId = users.Count == 1 ? users[0] : null, IsVerifiable = true };
    }

    /// <summary>
    /// The edges in force at <paramref name="revision"/>: created by a command committed before it and not removed by a command
    /// committed before it. False when any creating or removing command of the set is missing, of another organization or uncommitted.
    /// </summary>
    private static bool TryActiveAt(
        IReadOnlyList<AuthorityRelationshipEdge> edges, long revision, Guid organization, Dictionary<Guid, LineageCommandFact> commands,
        out List<AuthorityRelationshipEdge> active)
    {
        active = [];
        foreach (var edge in edges)
        {
            if (Committed(edge.CreatedBySecurityCommandId, organization, commands) is not { } created)
            {
                return false;
            }

            long? removed = null;
            if (edge.RemovedBySecurityCommandId is { } removedBy)
            {
                if (Committed(removedBy, organization, commands) is not { } removedRevision)
                {
                    return false;
                }

                removed = removedRevision;
            }

            if (created < revision && (removed is null || removed > revision))
            {
                active.Add(edge);
            }
        }

        return true;
    }

    private static long? Committed(Guid commandId, Guid organization, Dictionary<Guid, LineageCommandFact> commands) =>
        commands.TryGetValue(commandId, out var command) && command.OrganizationId == organization &&
        command.CommandStateName is SecurityCommandStates.Effective or SecurityCommandStates.CommittedAwaitingProjection &&
        command.CommittedAuthorizationRevision is > 0
            ? command.CommittedAuthorizationRevision
            : null;
}
