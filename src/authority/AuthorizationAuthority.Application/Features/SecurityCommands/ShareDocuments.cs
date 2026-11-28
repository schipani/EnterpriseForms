using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Content;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.Sharing;

namespace AuthorizationAuthority.Application.Features.SecurityCommands;

/// <summary>
/// C10 WP8 (WP-0 Section 3.6): the document entries of a share. Each names an attachment of the record's accepted content and takes
/// its version boundary only from the Authority's own verified evidence (<see cref="RecordManifestLeaf.AttachmentVersionNumber"/>);
/// no business-supplied version is accepted. A delegated entry never exceeds the named parent's entry for the same attachment.
/// </summary>
internal static class ShareDocuments
{
    public const int MaximumDocuments = 100;

    /// <summary>The verified current version of each named attachment, after the entry rules; refuses anything else.</summary>
    /// <param name="ownerGranted">True only for a share the record owner grants directly (depth 1, no parent, resolved lineage).</param>
    /// <param name="role">The share's recipient role relation (reader, contributor, manager).</param>
    public static Dictionary<Guid, int> Resolve(IReadOnlyList<ShareDocumentEntry> documents, IReadOnlyList<RecordManifestLeaf> acceptedLeaves, bool ownerGranted, string role)
    {
        if (documents.Count > MaximumDocuments || documents.Select(d => d.FileAttachmentId).Distinct().Count() != documents.Count)
        {
            throw AuthorityException.Invalid("documents must be a distinct list of at most 100 attachments");
        }

        var attachments = acceptedLeaves.Where(l => l.ManifestItemKindCode == (byte)ManifestItemKind.Attachment).ToDictionary(l => l.ManifestItemId);
        var versions = new Dictionary<Guid, int>();
        foreach (var document in documents)
        {
            if (!document.CanViewDocument)
            {
                throw AuthorityException.Invalid("a shared document must allow viewing; downloading and replacing each require viewing");
            }

            // C10 WP8-B (owner decision 2026-11-27, WP-0 Section 3.6.1): replacement only through a share the owner grants directly, and
            // only with a role that can edit. A request that asks for more is refused, never silently reduced.
            if (document.CanReplaceDocument && !ownerGranted)
            {
                throw new AuthorityException(AuthorityErrorCodes.DocumentReplaceRequiresOwnerGrant, 422, false, "only a share granted directly by the record owner may allow replacing a document");
            }

            if (document.CanReplaceDocument && role is not ("contributor" or "manager"))
            {
                throw AuthorityException.Invalid("replacing a document needs the Contributor or Manager role");
            }

            if (!attachments.TryGetValue(document.FileAttachmentId, out var leaf))
            {
                throw AuthorityException.Invalid("documents must reference attachments in the accepted manifest");
            }

            versions[document.FileAttachmentId] = leaf.AttachmentVersionNumber
                ?? throw new AuthorityException(AuthorityErrorCodes.DocumentVersionEvidenceUnavailable, 422, false, "the attachment has no verified version evidence yet; it can be shared after the record's next accepted change");
        }

        return versions;
    }

    /// <summary>A delegated share's documents must each be in the parent share with at least the same flags and a version boundary it already covers.</summary>
    public static async Task RequireWithinParentAsync(IAuthorityScope scope, Guid organizationId, Guid parentRecordShareId, IReadOnlyList<ShareDocumentEntry> documents,
        IReadOnlyDictionary<Guid, int> versions, CancellationToken cancellationToken)
    {
        if (documents.Count == 0)
        {
            return;
        }

        var parent = (await scope.GetShareDocumentBindingsAsync(organizationId, [parentRecordShareId], cancellationToken).ConfigureAwait(false))
            .Where(b => b.OrganizationId == organizationId && b.RecordShareId == parentRecordShareId).ToDictionary(b => b.FileAttachmentId);
        foreach (var document in documents)
        {
            if (!parent.TryGetValue(document.FileAttachmentId, out var held) || !held.CanViewDocument ||
                (document.CanDownloadDocument && !held.CanDownloadDocument) || (document.CanReplaceDocument && !held.CanReplaceDocument) ||
                versions[document.FileAttachmentId] > held.SharedThroughVersionNumber)
            {
                throw AuthorityException.Invalid("delegation exceeds the named parent's document scope");
            }
        }
    }

    /// <summary>True when the new entries drop a document or a download/replace flag the share holds now (an update can only raise boundaries).</summary>
    public static bool Narrows(IReadOnlyList<ShareDocumentBinding> current, IReadOnlyList<ShareDocumentEntry> documents, IReadOnlyDictionary<Guid, int> versions)
    {
        var next = documents.ToDictionary(d => d.FileAttachmentId);
        return current.Any(b => !next.TryGetValue(b.FileAttachmentId, out var entry) ||
                                (b.CanDownloadDocument && !entry.CanDownloadDocument) || (b.CanReplaceDocument && !entry.CanReplaceDocument) ||
                                versions[b.FileAttachmentId] < b.SharedThroughVersionNumber);
    }

    /// <summary>The document scope of each share for the narrowing walk: the updated share's new entries, the descendants' bindings.</summary>
    public static Dictionary<Guid, IReadOnlyDictionary<Guid, ShareDocumentBinding>> Scopes(Guid updatedShareId, IReadOnlyList<ShareDocumentEntry> documents,
        IReadOnlyDictionary<Guid, int> versions, IEnumerable<ShareDocumentBinding> descendantBindings)
    {
        var scopes = descendantBindings.GroupBy(b => b.RecordShareId)
            .ToDictionary(g => g.Key, g => (IReadOnlyDictionary<Guid, ShareDocumentBinding>)g.ToDictionary(b => b.FileAttachmentId));
        scopes[updatedShareId] = documents.ToDictionary(d => d.FileAttachmentId, d => new ShareDocumentBinding
        {
            FileAttachmentId = d.FileAttachmentId,
            CanViewDocument = d.CanViewDocument,
            CanDownloadDocument = d.CanDownloadDocument,
            CanReplaceDocument = d.CanReplaceDocument,
            SharedThroughVersionNumber = versions[d.FileAttachmentId],
        });
        return scopes;
    }

    /// <summary>Whether a child's documents are each within the parent's (same attachment, flags no wider, boundary no later).</summary>
    public static bool Covers(IReadOnlyDictionary<Guid, ShareDocumentBinding>? parent, IReadOnlyDictionary<Guid, ShareDocumentBinding>? child) =>
        child is null || child.Values.All(c => parent is not null && parent.TryGetValue(c.FileAttachmentId, out var p) && p.CanViewDocument &&
                                               (!c.CanDownloadDocument || p.CanDownloadDocument) && (!c.CanReplaceDocument || p.CanReplaceDocument) &&
                                               c.SharedThroughVersionNumber <= p.SharedThroughVersionNumber);

    /// <summary>Plans the document edge and binding diff of a scope update: removed, re-bound (flags or boundary) and added documents.</summary>
    public static void PlanDiff(CommandPlan plan, Application.Features.SecurityCommands.DesiredStateGraph graph, Guid organizationId, Guid recordShareId,
        IReadOnlyList<ShareDocumentBinding> current, IReadOnlyList<ShareDocumentEntry> documents, IReadOnlyDictionary<Guid, int> versions)
    {
        var next = documents.ToDictionary(d => d.FileAttachmentId);
        foreach (var binding in current)
        {
            if (!next.TryGetValue(binding.FileAttachmentId, out var entry))
            {
                plan.RemovedDocumentBindings.Add(binding);
                plan.RemoveEdges.AddRange(graph.EdgesOf(ResourceTypeNames.DocumentItem, binding.FileAttachmentId, "shared_via")
                    .Where(e => e.SubjectTypeName == ResourceTypeNames.RecordShare && e.SubjectEntityId == recordShareId));
                continue;
            }

            var version = versions[binding.FileAttachmentId];
            if (binding.CanViewDocument != entry.CanViewDocument || binding.CanDownloadDocument != entry.CanDownloadDocument ||
                binding.CanReplaceDocument != entry.CanReplaceDocument || binding.SharedThroughVersionNumber != version)
            {
                plan.UpdatedDocumentBindings.Add((binding, entry, version)); // applied only when the command is accepted
            }
        }

        foreach (var entry in documents.Where(d => current.All(b => b.FileAttachmentId != d.FileAttachmentId)))
        {
            plan.AddEdges.Add((ResourceTypeNames.DocumentItem, entry.FileAttachmentId, "shared_via", ResourceTypeNames.RecordShare, recordShareId, string.Empty));
            plan.AddDocumentBindings.Add(new ShareDocumentBinding
            {
                ShareDocumentBindingId = Guid.CreateVersion7(),
                OrganizationId = organizationId,
                RecordShareId = recordShareId,
                FileAttachmentId = entry.FileAttachmentId,
                CanViewDocument = entry.CanViewDocument,
                CanDownloadDocument = entry.CanDownloadDocument,
                CanReplaceDocument = entry.CanReplaceDocument,
                SharedThroughVersionNumber = versions[entry.FileAttachmentId],
            });
        }
    }

    /// <summary>The committed payload with the new documents and their Authority-derived boundaries appended (journal evidence).</summary>
    public static string WithDocuments(string normalizedPayloadJson, IReadOnlyList<ShareDocumentEntry> documents, IReadOnlyDictionary<Guid, int> versions)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(normalizedPayloadJson)!.AsObject();
        node["documents"] = System.Text.Json.JsonSerializer.SerializeToNode(documents.OrderBy(d => d.FileAttachmentId).Select(d => new
        {
            d.FileAttachmentId,
            d.CanViewDocument,
            d.CanDownloadDocument,
            d.CanReplaceDocument,
            SharedThroughVersionNumber = versions[d.FileAttachmentId],
        }).ToArray(), CanonicalJson.Options);
        return node.ToJsonString();
    }
}
