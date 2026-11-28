using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;

namespace AuthorizationAuthority.Application.Common;

/// <summary>
/// C10 M1-WP2 (WP-0 Section 3.1, D10-1): access-time share expiry. A share whose effective expiry (its own, or any ancestor's: the
/// lineage minimum) is at or before the decision instant confers nothing, whether or not the convergence command has removed its
/// edges yet. SpiceDB holds no caveats (invariant 4): this overlay is applied to SpiceDB's answer and can only turn an Allow into a
/// Deny, never the reverse.
/// <para>
/// Share-derived permissions are flat (<c>authority.schema.zed</c>): a share grants through its own role edge (to a user, or to a
/// team's participants) and reaches a record (<c>form_record#shared_via</c>: discover, manage access) or an item
/// (<c>field_item</c>/<c>document_item#shared_via</c>: read, edit, replace, delegate). Owner paths never depend on a share. So for an
/// allowed decision: if the subject holds no expired share that could grant it, the answer stands; if a live share of the subject
/// grants it, the answer stands; otherwise the subject keeps access only through an owner path, which the verifier checks
/// (<c>effective_owner</c>) at the same pinned snapshot.
/// </para>
/// Loaded once per decision snapshot in the decision's own scope, at the snapshot's instant. With no expired share in the
/// organization it is empty and costs one indexed read.
/// </summary>
internal sealed class ShareExpiryOverlay
{
    private static readonly string[] AnyRole = ["reader", "contributor", "manager"];
    private static readonly string[] EditRoles = ["contributor", "manager"];
    private static readonly string[] ManagerRole = ["manager"];

    private readonly Dictionary<Guid, ShareFacts> shares;
    private readonly Dictionary<Guid, HashSet<Guid>> teamMembers;

    private ShareExpiryOverlay(Dictionary<Guid, ShareFacts> shares, Dictionary<Guid, HashSet<Guid>> teamMembers, DateTimeOffset instant)
    {
        this.shares = shares;
        this.teamMembers = teamMembers;
        Instant = instant;
    }

    public enum Verdict
    {
        /// <summary>No expired share could have produced the answer: it stands.</summary>
        Unaffected,

        /// <summary>A live share of the subject grants the permission: the answer stands.</summary>
        LiveShareGrants,

        /// <summary>Only expired shares could have granted it through sharing: the answer stands only if the subject is an effective owner.</summary>
        OwnerPathRequired,
    }

    /// <summary>The decision instant (the verifier's clock when the snapshot was pinned).</summary>
    public DateTimeOffset Instant { get; }

    /// <summary>True when no share of the organization has expired by the instant.</summary>
    public bool IsEmpty => !shares.Values.Any(s => s.Expired);

    /// <summary>
    /// C10 D5: every Active share that confers nothing at the instant — its own expiry passed, or an ancestor's (the lineage minimum).
    /// Excluding these from a candidate set leaves exactly the shares a decision at the same instant can allow through.
    /// </summary>
    public IReadOnlyCollection<Guid> ExpiredShareIds => [.. shares.Values.Where(s => s.Expired).Select(s => s.ShareId).Order()];

    private sealed record ShareFacts(Guid ShareId, Guid RecordId, bool Expired, DateTimeOffset? ExpiresOn)
    {
        public List<(string Role, string SubjectType, Guid SubjectId)> Holders { get; } = [];

        public HashSet<(string ResourceType, Guid ResourceId)> Targets { get; } = [];
    }

    public static async Task<ShareExpiryOverlay> LoadAsync(IAuthorityScope scope, Guid organizationId, DateTimeOffset instant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var withExpiry = await scope.GetActiveShareMetadataWithExpiryAsync(organizationId, cancellationToken).ConfigureAwait(false);
        if (withExpiry.Count == 0)
        {
            return new ShareExpiryOverlay([], [], instant);
        }

        // Effective expiry: the lineage minimum — the share's own expiry or any ancestor's (depth <= 3). It is propagated from every share
        // with an expiry to its descendants, so a descendant without its own expiry (or with a later one) is bounded by its ancestor:
        // expired once that has passed (D10-1), and the D10-10 snapshot bound below uses it (independent review F-1).
        var effective = withExpiry.Where(m => m.ShareExpiresOn is not null).ToDictionary(m => m.RecordShareId, m => m.ShareExpiresOn!.Value);
        var frontier = effective.Keys.ToList();
        for (var level = 0; level < 3 && frontier.Count > 0; level++)
        {
            var next = new List<Guid>();
            // Re-review N-1: one set-based read per level, not one per share.
            foreach (var child in await scope.FindShareMetadataByParentsAsync(organizationId, frontier, cancellationToken).ConfigureAwait(false))
            {
                if (child.OrganizationId != organizationId || child.ParentRecordShareId is not { } parent || !effective.TryGetValue(parent, out var bound))
                {
                    continue;
                }

                var childBound = child.ShareExpiresOn is { } own && own < bound ? own : bound;
                if (!effective.TryGetValue(child.RecordShareId, out var known) || childBound < known)
                {
                    effective[child.RecordShareId] = childBound;
                    next.Add(child.RecordShareId);
                }
            }

            frontier = next;
        }

        var expired = effective.Where(e => e.Value <= instant).Select(e => e.Key).ToHashSet();
        var resources = (await scope.GetResourcesAsync(organizationId, ResourceTypeNames.RecordShare, [.. effective.Keys], cancellationToken).ConfigureAwait(false))
            .Where(r => r.ResourceLifecycleState == ResourceLifecycleStates.Active && r.ParentResourceEntityId is not null)
            .ToDictionary(r => r.ResourceEntityId);

        // Every Active share of a record that has an expired one: a live sibling may still grant what an expired share did.
        foreach (var record in resources.Values.Where(r => expired.Contains(r.ResourceEntityId)).Select(r => r.ParentResourceEntityId!.Value).Distinct().ToList())
        {
            foreach (var share in await scope.GetActiveResourcesByParentAsync(organizationId, record, [ResourceTypeNames.RecordShare], cancellationToken).ConfigureAwait(false))
            {
                resources.TryAdd(share.ResourceEntityId, share);
            }
        }

        var facts = resources.Values.ToDictionary(r => r.ResourceEntityId,
            r => new ShareFacts(r.ResourceEntityId, r.ParentResourceEntityId!.Value, expired.Contains(r.ResourceEntityId), effective.TryGetValue(r.ResourceEntityId, out var at) ? at : null));
        var edges = await scope.GetActiveShareEdgesAsync(organizationId, [.. facts.Keys], cancellationToken).ConfigureAwait(false);
        foreach (var edge in edges.Where(e => e.OrganizationId == organizationId && e.RemovedBySecurityCommandId is null))
        {
            if (edge.ResourceTypeName == ResourceTypeNames.RecordShare && facts.TryGetValue(edge.ResourceEntityId, out var held) &&
                edge.RelationshipName is "reader" or "contributor" or "manager")
            {
                held.Holders.Add((edge.RelationshipName, edge.SubjectTypeName, edge.SubjectEntityId));
            }
            else if (edge.SubjectTypeName == ResourceTypeNames.RecordShare && edge.RelationshipName == "shared_via" && facts.TryGetValue(edge.SubjectEntityId, out var target))
            {
                target.Targets.Add((edge.ResourceTypeName, edge.ResourceEntityId));
            }
        }

        var teams = facts.Values.SelectMany(f => f.Holders).Where(h => h.SubjectType == ResourceTypeNames.Team).Select(h => h.SubjectId).Distinct().ToList();
        var members = new Dictionary<Guid, HashSet<Guid>>();
        if (teams.Count > 0)
        {
            foreach (var edge in await scope.GetActiveTeamMemberEdgesAsync(organizationId, teams, cancellationToken).ConfigureAwait(false))
            {
                if (edge.OrganizationId == organizationId && edge.RemovedBySecurityCommandId is null && edge.ResourceTypeName == ResourceTypeNames.Team &&
                    edge.RelationshipName == "member" && edge.SubjectTypeName == ResourceTypeNames.User)
                {
                    (members.TryGetValue(edge.ResourceEntityId, out var set) ? set : members[edge.ResourceEntityId] = []).Add(edge.SubjectEntityId);
                }
            }
        }

        return new ShareExpiryOverlay(facts, members, instant);
    }

    /// <summary>
    /// The verdict for an allowed decision of <paramref name="subjectType"/>:<paramref name="subjectId"/> (a user, or a team's
    /// participants) on a resource, and the record whose owner path decides it when <see cref="Verdict.OwnerPathRequired"/>.
    /// </summary>
    public (Verdict Verdict, Guid RecordId) Evaluate(string resourceType, Guid resourceId, string permission, string subjectType, Guid subjectId, string subjectRelation)
    {
        if (IsEmpty || Roles(resourceType, permission) is not { } roles)
        {
            return (Verdict.Unaffected, Guid.Empty);
        }

        var relevant = resourceType == ResourceTypeNames.FormRecord
            ? shares.Values.Where(s => s.RecordId == resourceId && s.Targets.Contains((ResourceTypeNames.FormRecord, resourceId)))
            : shares.Values.Where(s => s.Targets.Contains((resourceType, resourceId)));
        var granting = relevant.Where(s => s.Holders.Any(h => roles.Contains(h.Role) && Holds(h, subjectType, subjectId, subjectRelation))).ToList();
        if (!granting.Any(s => s.Expired))
        {
            return (Verdict.Unaffected, Guid.Empty);
        }

        return granting.Any(s => !s.Expired) ? (Verdict.LiveShareGrants, Guid.Empty) : (Verdict.OwnerPathRequired, granting[0].RecordId);
    }

    /// <summary>
    /// D10-10: the earliest future effective expiry (the lineage minimum) among the live shares the user holds (directly or through a
    /// team), or null. A set the decision enumerated through those shares is valid only before it.
    /// </summary>
    public DateTimeOffset? NextExpiryFor(Guid userId) =>
        shares.Values.Where(s => !s.Expired && s.ExpiresOn > Instant && s.Holders.Any(h => Holds(h, ResourceTypeNames.User, userId, string.Empty)))
            .Select(s => s.ExpiresOn).Min();

    /// <summary>Whether the user holds any expired, unconverged share in the organization (directly or through a team).</summary>
    public bool UserHoldsExpiredShare(Guid userId) =>
        shares.Values.Any(s => s.Expired && s.Holders.Any(h => Holds(h, ResourceTypeNames.User, userId, string.Empty)));

    private bool Holds((string Role, string SubjectType, Guid SubjectId) holder, string subjectType, Guid subjectId, string subjectRelation) => subjectType switch
    {
        ResourceTypeNames.User when subjectRelation.Length == 0 =>
            (holder.SubjectType == ResourceTypeNames.User && holder.SubjectId == subjectId) ||
            (holder.SubjectType == ResourceTypeNames.Team && teamMembers.TryGetValue(holder.SubjectId, out var members) && members.Contains(subjectId)),
        ResourceTypeNames.Team when subjectRelation == "participant" => holder.SubjectType == ResourceTypeNames.Team && holder.SubjectId == subjectId,
        _ => false,
    };

    /// <summary>The share roles that grant a permission through sharing (<c>authority.schema.zed</c>), or null when no share can grant it.</summary>
    private static string[]? Roles(string resourceType, string permission) => (resourceType, permission) switch
    {
        (ResourceTypeNames.FormRecord, "discover") => AnyRole,
        (ResourceTypeNames.FormRecord, "manage_access") => ManagerRole,
        (ResourceTypeNames.FieldItem or ResourceTypeNames.DocumentItem, "read") => AnyRole,
        (ResourceTypeNames.FieldItem, "edit") => EditRoles,
        (ResourceTypeNames.DocumentItem, "replace") => EditRoles,
        (ResourceTypeNames.FieldItem or ResourceTypeNames.DocumentItem, "delegate") => ManagerRole,
        _ => null,
    };
}
