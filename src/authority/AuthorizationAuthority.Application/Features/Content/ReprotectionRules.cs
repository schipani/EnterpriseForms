using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Content;
using EnterpriseForms.Integrity.Canonical;

namespace AuthorizationAuthority.Application.Features.Content;

/// <summary>
/// The only content change the authority accepts without a record-permitted user: replacing the plaintext rows of one
/// field — the field named by an effective <c>PublishFormSecurityRevision</c> command — with application-encrypted rows
/// of the same item identity. Decided from the canonical bytes of the verified current snapshot and the candidate:
/// every other column of every other row must be byte-identical except the operation binding, nothing may be added or
/// removed beyond the plaintext→ciphertext pairs, and no plaintext row of the field may remain. The authority cannot
/// see plaintext or keys; what it enforces is that the operation changes nothing else and leaves nothing behind.
/// </summary>
public static class ReprotectionRules
{
    public const string EncryptedEntityKind = "field_encrypted_values";
    public const string OptionEntityKind = "field_option_values";
    private const string OperationColumn = "content_change_operation_id";
    private const string FieldColumn = "field_definition_id";

    private static readonly HashSet<string> PlaintextValueKinds = new(StringComparer.Ordinal)
    {
        "field_string_values", "field_integer_values", "field_decimal_values", "field_date_values", "field_date_time_values", "field_boolean_values", "field_guid_values",
    };

    public sealed record SnapshotItem(ManifestItemKind Kind, Guid ItemId, byte[] Key, RowCanonicalDocument Document, byte[] Digest);

    /// <summary>Returns the mutation kind per candidate key (hex) or throws an invalid-command error naming the violated rule.</summary>
    public static Dictionary<string, string> Validate(IReadOnlyList<SnapshotItem> current, IReadOnlyList<SnapshotItem> candidate, Guid fieldDefinitionId)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(candidate);
        var currentByKey = current.ToDictionary(i => Convert.ToHexStringLower(i.Key), StringComparer.Ordinal);
        var consumed = new HashSet<string>(StringComparer.Ordinal);
        var mutations = new Dictionary<string, string>(StringComparer.Ordinal);
        var reprotected = 0;

        foreach (var item in candidate)
        {
            var keyHex = Convert.ToHexStringLower(item.Key);
            if (currentByKey.TryGetValue(keyHex, out var existing))
            {
                consumed.Add(keyHex);
                if (existing.Digest.AsSpan().SequenceEqual(item.Digest))
                {
                    mutations[keyHex] = ContentMutationKinds.Unchanged;
                    continue;
                }

                if (item.Kind == ManifestItemKind.FieldValue && IsPlaintextToCiphertext(existing.Document, item.Document, fieldDefinitionId))
                {
                    mutations[keyHex] = ContentMutationKinds.Reprotect;
                    reprotected++;
                    continue;
                }

                if (SameRowExceptOperation(existing.Document, item.Document))
                {
                    mutations[keyHex] = ContentMutationKinds.Unchanged;
                    continue;
                }

                throw AuthorityException.Invalid($"REPROTECT_ROW_CHANGED: item {item.Kind}:{item.ItemId:D} changes more than the operation binding");
            }

            // Only a selection row of the field may be replaced by an encrypted row under the same item id.
            if (item.Kind == ManifestItemKind.FieldValue && item.Document.Header.EntityKind == EncryptedEntityKind && FieldOf(item.Document) == fieldDefinitionId)
            {
                var selectionKey = Convert.ToHexStringLower(EnterpriseForms.Integrity.Merkle.MerkleManifestV1.ItemKey(EnterpriseForms.Integrity.Merkle.MerkleItemKind.FieldSelection, item.ItemId));
                if (currentByKey.TryGetValue(selectionKey, out var selection) && selection.Document.Header.EntityKind == OptionEntityKind && FieldOf(selection.Document) == fieldDefinitionId && SharedColumnsEqualExceptOperation(selection.Document, item.Document))
                {
                    consumed.Add(selectionKey);
                    mutations[keyHex] = ContentMutationKinds.Reprotect;
                    reprotected++;
                    continue;
                }
            }

            throw AuthorityException.Invalid($"REPROTECT_ITEM_ADDED: item {item.Kind}:{item.ItemId:D} is not part of the accepted snapshot");
        }

        foreach (var (keyHex, item) in currentByKey)
        {
            if (!consumed.Contains(keyHex))
            {
                throw AuthorityException.Invalid($"REPROTECT_ITEM_REMOVED: item {item.Kind}:{item.ItemId:D} of the accepted snapshot is missing");
            }
        }

        foreach (var item in candidate.Where(i => i.Kind is ManifestItemKind.FieldValue or ManifestItemKind.FieldSelection && FieldOf(i.Document) == fieldDefinitionId))
        {
            if (item.Document.Header.EntityKind != EncryptedEntityKind)
            {
                throw AuthorityException.Invalid($"REPROTECT_INCOMPLETE: a plaintext row of the field remains ({item.Document.Header.EntityKind})");
            }
        }

        if (reprotected == 0)
        {
            throw AuthorityException.Invalid("REPROTECT_NOTHING_TO_DO: the snapshot holds no plaintext row of the field");
        }

        return mutations;
    }

    private static bool IsPlaintextToCiphertext(RowCanonicalDocument current, RowCanonicalDocument candidate, Guid fieldDefinitionId) =>
        PlaintextValueKinds.Contains(current.Header.EntityKind)
        && candidate.Header.EntityKind == EncryptedEntityKind
        && FieldOf(current) == fieldDefinitionId
        && FieldOf(candidate) == fieldDefinitionId
        && current.Header.OrganizationId == candidate.Header.OrganizationId
        && SharedColumnsEqualExceptOperation(current, candidate);

    private static Guid? FieldOf(RowCanonicalDocument document) => document.Column(FieldColumn)?.AsUuid();

    /// <summary>
    /// Same entity kind and schema, same column set, every column byte-identical except the operation binding. The framing
    /// primary key (a per-operation surrogate row id for value and attachment copies) and the row revision are not compared:
    /// item identity is the manifest key plus the identity columns, which are compared.
    /// </summary>
    private static bool SameRowExceptOperation(RowCanonicalDocument current, RowCanonicalDocument candidate)
    {
        if (current.Header.EntityKind != candidate.Header.EntityKind || current.Header.SchemaVersion != candidate.Header.SchemaVersion || current.Header.OrganizationId != candidate.Header.OrganizationId
            || current.Columns.Count != candidate.Columns.Count)
        {
            return false;
        }

        for (var i = 0; i < current.Columns.Count; i++)
        {
            if (!string.Equals(current.Columns[i].Name, candidate.Columns[i].Name, StringComparison.Ordinal))
            {
                return false;
            }

            if (current.Columns[i].Name == OperationColumn)
            {
                continue;
            }

            if (!current.Columns[i].FramedValueEquals(candidate.Columns[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Every column present in both rows (identity, group, creator, tenant, …) is byte-identical except the operation binding.</summary>
    private static bool SharedColumnsEqualExceptOperation(RowCanonicalDocument current, RowCanonicalDocument candidate)
    {
        var shared = 0;
        foreach (var column in current.Columns)
        {
            var other = candidate.Column(column.Name);
            if (other is null)
            {
                continue;
            }

            shared++;
            if (column.Name == OperationColumn)
            {
                continue;
            }

            if (!column.FramedValueEquals(other))
            {
                return false;
            }
        }

        // A plaintext→ciphertext pair must share at least the identity envelope (record, field, item, tenant, group).
        return shared >= 5;
    }
}
