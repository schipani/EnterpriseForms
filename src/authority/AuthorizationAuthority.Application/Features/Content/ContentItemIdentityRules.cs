using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Content;
using EnterpriseForms.Integrity.Canonical;

namespace AuthorizationAuthority.Application.Features.Content;

/// <summary>
/// R-53 (C-148): the permanent identity of content item ids, enforced by the Authority at the content-acceptance boundary for
/// every write path (record owner, share editor, protection migration):
/// <list type="bullet">
/// <item>I-1 continuity: a retained item keeps its identity anchor (values and selections: field and group occurrence;
/// attachments: field; group entries: group definition; record header: form). Content may change; the anchor may not.</item>
/// <item>I-2 no reuse: a new item may not name an id that is bound, or that was accepted before in any manifest of any
/// organization. A removed item can therefore never return, under its field or another (it could revive shares that name it).</item>
/// <item>I-3 permanence: every accepted item's anchor is recorded once, at commit, from the verified staged bytes, and every
/// later commit must match it. The unique item id refuses a concurrent second binding.</item>
/// </list>
/// I-1 is decided against the complete current snapshot verified against the accepted head, never against business rows.
/// </summary>
public static class ContentItemIdentityRules
{
    /// <summary>The identity anchor of one item, from its signed canonical bytes.</summary>
    public sealed record ItemAnchor(byte KindCode, Guid? FieldDefinitionId, Guid? FormRecordGroupId, Guid? GroupDefinitionId, Guid? FormDefinitionId);

    public static ItemAnchor Anchor(ManifestItemKind kind, RowCanonicalDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Guid? Uuid(string column) => document.Column(column)?.AsUuid();
        return kind switch
        {
            ManifestItemKind.FieldValue or ManifestItemKind.FieldSelection => new ItemAnchor((byte)kind, Required(Uuid(ContentDeltaRules.FieldColumn)), Uuid(ContentDeltaRules.GroupColumn), null, null),
            ManifestItemKind.Attachment => new ItemAnchor((byte)kind, Required(Uuid(ContentDeltaRules.FieldColumn)), null, null, null),
            ManifestItemKind.RepeatedGroup => new ItemAnchor((byte)kind, null, null, Required(Uuid("group_definition_id")), null),
            ManifestItemKind.RecordHeader => new ItemAnchor((byte)kind, null, null, null, Required(Uuid("form_definition_id"))),
            _ => throw AuthorityException.Invalid("UNSUPPORTED_SIGNED_SCHEMA: the item kind has no identity anchor"),
        };
    }

    public static ItemAnchor Anchor(ContentItemIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new ItemAnchor(identity.ManifestItemKindCode, identity.BoundFieldDefinitionId, identity.BoundFormRecordGroupId, identity.BoundGroupDefinitionId, identity.BoundFormDefinitionId);
    }

    /// <summary>
    /// Prepare (I-1, I-2). <paramref name="current"/> is the verified current snapshot (empty for a record without content);
    /// <paramref name="candidate"/> the parsed candidate. Throws 422 on any violation; error texts carry reason codes only.
    /// </summary>
    public static async Task EnforceAtPrepareAsync(IAuthorityScope scope, Guid organizationId, Guid formRecordId,
        IReadOnlyList<(ManifestItemKind Kind, Guid ItemId, RowCanonicalDocument Document)> current,
        IReadOnlyList<(ManifestItemKind Kind, Guid ItemId, RowCanonicalDocument Document)> candidate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (candidate.Select(c => c.ItemId).Distinct().Count() != candidate.Count)
        {
            throw Reused("ITEM_IDENTITY_DUPLICATE: one item id appears more than once in the candidate");
        }

        var currentByKey = current.ToDictionary(c => ((byte)c.Kind, c.ItemId));
        var newIds = new List<Guid>();
        var retained = new List<(Guid ItemId, ItemAnchor Anchor)>();
        foreach (var item in candidate)
        {
            var anchor = Anchor(item.Kind, item.Document);
            if (currentByKey.TryGetValue(((byte)item.Kind, item.ItemId), out var existing))
            {
                if (Anchor(existing.Kind, existing.Document) != anchor)
                {
                    throw Changed("ITEM_IDENTITY_CHANGED: a retained item may not change its field, group occurrence, group definition or form");
                }

                retained.Add((item.ItemId, anchor));
            }
            else if (item.Kind != ManifestItemKind.RecordHeader)
            {
                if (current.Any(c => c.ItemId == item.ItemId))
                {
                    throw Changed("ITEM_IDENTITY_CHANGED: a retained item id may not change its item kind");
                }

                newIds.Add(item.ItemId);
            }
        }

        var lookup = newIds.Concat(retained.Select(r => r.ItemId)).ToList();
        var bound = lookup.Count == 0 ? [] : (await scope.FindItemIdentitiesAsync(lookup, cancellationToken).ConfigureAwait(false)).ToDictionary(i => i.ManifestItemId);
        foreach (var id in newIds)
        {
            if (bound.ContainsKey(id))
            {
                throw Reused("ITEM_IDENTITY_REUSED: a new item may not reuse an id that is already bound");
            }
        }

        foreach (var (id, anchor) in retained)
        {
            if (bound.TryGetValue(id, out var identity) && (identity.OrganizationId != organizationId || identity.FormRecordId != formRecordId || Anchor(identity) != anchor))
            {
                throw Changed("ITEM_IDENTITY_CHANGED: a retained item differs from its permanent binding");
            }
        }

        if (newIds.Count > 0 && (await scope.FindAcceptedItemIdsAsync(newIds, cancellationToken).ConfigureAwait(false)).Count > 0)
        {
            throw Reused("ITEM_IDENTITY_REUSED: a new item may not reuse an id that was accepted before");
        }
    }

    /// <summary>
    /// Commit (I-3), on the verified staged bytes of the complete new manifest: every bound item must match its binding; every
    /// unbound item is bound now. Returns the new bindings to add in the commit transaction.
    /// </summary>
    public static async Task<List<ContentItemIdentity>> BindAtCommitAsync(IAuthorityScope scope, Guid organizationId, Guid formRecordId, Guid operationId,
        IReadOnlyList<(ManifestItemKind Kind, Guid ItemId, RowCanonicalDocument Document)> staged, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        var bound = (await scope.FindItemIdentitiesAsync(staged.Select(s => s.ItemId).ToList(), cancellationToken).ConfigureAwait(false)).ToDictionary(i => i.ManifestItemId);
        var added = new List<ContentItemIdentity>();
        foreach (var item in staged)
        {
            var anchor = Anchor(item.Kind, item.Document);
            if (bound.TryGetValue(item.ItemId, out var identity))
            {
                if (identity.OrganizationId != organizationId || identity.FormRecordId != formRecordId || Anchor(identity) != anchor)
                {
                    throw Changed("ITEM_IDENTITY_CHANGED: the committed item differs from its permanent binding");
                }

                continue;
            }

            added.Add(new ContentItemIdentity
            {
                ContentItemIdentityId = Guid.CreateVersion7(),
                ManifestItemId = item.ItemId,
                OrganizationId = organizationId,
                FormRecordId = formRecordId,
                ManifestItemKindCode = anchor.KindCode,
                BoundFieldDefinitionId = anchor.FieldDefinitionId,
                BoundFormRecordGroupId = anchor.FormRecordGroupId,
                BoundGroupDefinitionId = anchor.GroupDefinitionId,
                BoundFormDefinitionId = anchor.FormDefinitionId,
                FirstCommittedOperationId = operationId,
                IdentityBoundOn = now,
            });
        }

        return added;
    }

    private static Guid Required(Guid? value) => value ?? throw AuthorityException.Invalid("IDENTITY_BINDING: the identity anchor column is missing");

    private static AuthorityException Changed(string detail) => new(AuthorityErrorCodes.ItemIdentityChanged, 422, false, detail);

    private static AuthorityException Reused(string detail) => new(AuthorityErrorCodes.ItemIdentityReused, 422, false, detail);
}
