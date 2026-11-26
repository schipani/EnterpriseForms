using AuthorizationAuthority.Domain.Features.Lineage;
using AuthorizationAuthority.Domain.Features.Organizations;

namespace AuthorizationAuthority.Application.Features.Lineage;

/// <summary>
/// C10 M1-WP10 (Section 5.1): the read-only dry run over one organization's verified evidence snapshot. Pure: no I/O, no command,
/// no write. Only legacy shares (no committed lineage) are classified; shares with committed lineage take part as evidence only.
/// <para>
/// A legacy share whose verified classification is decisive (<see cref="LegacyShareClassificationOutcome.OwnerGranted"/>, or
/// <see cref="LegacyShareClassificationOutcome.UnambiguousParent"/>) has a determined depth, and the would-be lineage it implies
/// is what a later, separately authorized execution would record. The run therefore lets such shares serve as parent evidence in
/// the next pass, and repeats until no further share becomes decisive. This is monotone: a decisive or invalidating
/// classification depends only on the fixed set of covering shares, and a pass can only turn an unresolved candidate into a
/// resolved one. A classification that changed after it was relied on would contradict that, so the run fails closed.
/// Unresolved candidates are refused (<see cref="InsufficientEvidenceReason.UnresolvedCandidateParent"/>), never invalidated.
/// </para>
/// </summary>
public static class LegacyLineageDryRun
{
    public static LegacyLineageClassificationReport Run(LineageEvidenceSnapshot snapshot, LineagePseudonymizer pseudonymizer)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(pseudonymizer);
        if (snapshot.Shares.Select(s => s.Share.ResourceEntityId).Distinct().Count() != snapshot.Shares.Count ||
            snapshot.Shares.Any(s => s.Share.OrganizationId != snapshot.OrganizationId || s.Share.ParentResourceEntityId is null ||
                                     s.Share.ResourceLifecycleState != ResourceLifecycleStates.Active))
        {
            throw new InvalidOperationException("the evidence snapshot is not a set of distinct Active shares of one organization");
        }

        var legacy = snapshot.Shares.Where(s => s.IsLegacy).ToList();
        var decided = new Dictionary<Guid, (int Depth, Guid? Parent)>();
        List<LegacyShareClassificationEntry> entries = [];

        // Each pass either determines at least one more share or stops; the bound is never reached by a correct snapshot.
        for (var pass = 0; pass <= legacy.Count; pass++)
        {
            var grants = snapshot.Shares
                .GroupBy(s => s.Share.ParentResourceEntityId!.Value)
                .ToDictionary(g => g.Key, g => new RecordGrantsSnapshot
                {
                    ObservedOn = snapshot.ObservedOn,
                    ActiveShares = g.Select(s => ToActiveShareInfo(s, decided, snapshot.Shares)).ToList(),
                });

            entries = legacy.Select(s => LegacyLineageClassifier.ClassifyShare(
                s.Share, s.Metadata, s.RecipientEdges, s.FieldItemIds, s.DocumentBindings, s.GrantorAtRegistration, s.OwnerAtRegistration,
                grants[s.Share.ParentResourceEntityId!.Value], pseudonymizer, evaluateIndependentCoverage: true)).ToList();

            var added = false;
            foreach (var entry in entries)
            {
                var determined = entry.Classification switch
                {
                    LegacyShareClassificationOutcome.OwnerGranted => ((int Depth, Guid? Parent)?)(1, null),
                    LegacyShareClassificationOutcome.UnambiguousParent => (entry.ProposedDelegationDepth!.Value, entry.ProposedParentShareId),
                    _ => null,
                };

                if (decided.TryGetValue(entry.RecordShareId, out var previous))
                {
                    if (determined != previous)
                    {
                        throw new InvalidOperationException("a determined lineage changed between passes; the evidence is inconsistent");
                    }
                }
                else if (determined is { } value)
                {
                    decided[entry.RecordShareId] = value;
                    added = true;
                }
            }

            if (!added)
            {
                return new LegacyLineageReportBuilder()
                    .WithAuthorizationRevision(snapshot.AuthorizationRevision)
                    .WithTimestamp(snapshot.ObservedOn)
                    .AddEntries(entries)
                    .Build();
            }
        }

        throw new InvalidOperationException("the lineage dry run did not converge");
    }

    private static ActiveShareInfo ToActiveShareInfo(
        LineageShareEvidence share, IReadOnlyDictionary<Guid, (int Depth, Guid? Parent)> decided, IReadOnlyList<LineageShareEvidence> all)
    {
        var roles = share.RecipientEdges.Select(e => e.RelationshipName).Distinct(StringComparer.Ordinal).ToList();
        if (roles.Count != 1 || share.RecipientEdges.Count != 1)
        {
            throw new InvalidOperationException("share has no unique current recipient");
        }

        var recipient = share.RecipientEdges[0];
        var (depth, parent) = Lineage(share, decided);
        return new ActiveShareInfo
        {
            RecordShareId = share.Share.ResourceEntityId,
            OrganizationId = share.Share.OrganizationId,
            RecordId = share.Share.ParentResourceEntityId!.Value,
            Role = roles[0],
            DelegationDepth = depth,
            ParentRecordShareId = parent,
            IsActive = true,
            EffectiveExpiresOn = EffectiveExpiry(share, decided, all),
            FieldItemIds = share.FieldItemIds,
            DocumentBindings = share.DocumentBindings,
            DirectUserRecipientIds = recipient.SubjectTypeName == ResourceTypeNames.User ? new HashSet<Guid> { recipient.SubjectEntityId } : new HashSet<Guid>(),
            TeamRecipientIds = recipient.SubjectTypeName == ResourceTypeNames.Team ? new HashSet<Guid> { recipient.SubjectEntityId } : new HashSet<Guid>(),
            TeamMemberUserIds = share.RecipientTeamMemberUserIds,
        };
    }

    /// <summary>Committed lineage for non-legacy shares; for legacy shares, the lineage a decisive classification determined, else unknown.</summary>
    private static (int? Depth, Guid? Parent) Lineage(LineageShareEvidence share, IReadOnlyDictionary<Guid, (int Depth, Guid? Parent)> decided)
    {
        if (!share.IsLegacy)
        {
            return (share.Metadata!.DelegationDepth, share.Metadata.ParentRecordShareId);
        }

        return decided.TryGetValue(share.Share.ResourceEntityId, out var value) ? (value.Depth, value.Parent) : (null, null);
    }

    /// <summary>The minimum expiry along the share's known lineage (Section 3.1); a gap leaves the chain to the classifier's own check.</summary>
    private static DateTimeOffset? EffectiveExpiry(
        LineageShareEvidence share, IReadOnlyDictionary<Guid, (int Depth, Guid? Parent)> decided, IReadOnlyList<LineageShareEvidence> all)
    {
        DateTimeOffset? effective = null;
        var current = share;
        for (var level = 0; level < LegacyLineageClassifier.MaxDelegationDepth && current is not null; level++)
        {
            if (current.Metadata?.ShareExpiresOn is { } expiry && (effective is null || expiry < effective))
            {
                effective = expiry;
            }

            var parent = Lineage(current, decided).Parent;
            current = parent is { } id ? all.FirstOrDefault(s => s.Share.ResourceEntityId == id) : null;
        }

        return effective;
    }
}
