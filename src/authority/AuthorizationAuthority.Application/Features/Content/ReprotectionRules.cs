using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Content;
using EnterpriseForms.Integrity.Canonical;
using EnterpriseForms.Integrity.Merkle;

namespace AuthorizationAuthority.Application.Features.Content;

/// <summary>Direction of a field protection migration operation, taken from the authorizing PublishFormSecurityRevision command.</summary>
public enum ProtectionDirection
{
    /// <summary>Plaintext rows of the field become encrypted rows (B-28).</summary>
    Encrypt,

    /// <summary>Encrypted rows of the field become plaintext rows (ADR-PB-004 Amendment A1).</summary>
    Decrypt,
}

/// <summary>
/// The only content changes the authority accepts without a record-permitted user: replacing the rows of one field —
/// the field named by an effective <c>PublishFormSecurityRevision</c> command — between plaintext and application
/// encryption, keeping the same item identity. Decided from the canonical bytes of the verified current snapshot and
/// the candidate: every other column of every other row must be byte-identical except the operation binding, nothing
/// may be added or removed beyond the paired replacements, and no row of the field may remain in the old form. The
/// authority cannot see plaintext or keys; what it enforces is that the operation changes nothing else and leaves
/// nothing behind. The same class holds the protection barrier applied to every user operation (§EnforceBarrier).
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
    public static Dictionary<string, string> Validate(IReadOnlyList<SnapshotItem> current, IReadOnlyList<SnapshotItem> candidate, Guid fieldDefinitionId, ProtectionDirection direction = ProtectionDirection.Encrypt)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(candidate);
        var mutationKind = direction == ProtectionDirection.Encrypt ? ContentMutationKinds.Reprotect : ContentMutationKinds.Deprotect;
        var prefix = direction == ProtectionDirection.Encrypt ? "REPROTECT" : "DEPROTECT";
        var currentByKey = current.ToDictionary(i => Convert.ToHexStringLower(i.Key), StringComparer.Ordinal);
        var consumed = new HashSet<string>(StringComparer.Ordinal);
        var mutations = new Dictionary<string, string>(StringComparer.Ordinal);
        var changed = 0;

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

                if (item.Kind == ManifestItemKind.FieldValue && IsPairedReplacement(existing.Document, item.Document, fieldDefinitionId, direction))
                {
                    mutations[keyHex] = mutationKind;
                    changed++;
                    continue;
                }

                if (SameRowExceptOperation(existing.Document, item.Document))
                {
                    mutations[keyHex] = ContentMutationKinds.Unchanged;
                    continue;
                }

                throw AuthorityException.Invalid($"{prefix}_ROW_CHANGED: item {item.Kind}:{item.ItemId:D} changes more than the operation binding");
            }

            // Option fields change manifest kind: a selection row (FieldSelection) becomes an encrypted row (FieldValue) or back, under the same item id.
            if (direction == ProtectionDirection.Encrypt && item.Kind == ManifestItemKind.FieldValue && item.Document.Header.EntityKind == EncryptedEntityKind && FieldOf(item.Document) == fieldDefinitionId)
            {
                var selectionKey = Convert.ToHexStringLower(MerkleManifestV1.ItemKey(MerkleItemKind.FieldSelection, item.ItemId));
                if (currentByKey.TryGetValue(selectionKey, out var selection) && selection.Document.Header.EntityKind == OptionEntityKind && FieldOf(selection.Document) == fieldDefinitionId && SharedColumnsEqualExceptOperation(selection.Document, item.Document))
                {
                    consumed.Add(selectionKey);
                    mutations[keyHex] = mutationKind;
                    changed++;
                    continue;
                }
            }

            if (direction == ProtectionDirection.Decrypt && item.Kind == ManifestItemKind.FieldSelection && item.Document.Header.EntityKind == OptionEntityKind && FieldOf(item.Document) == fieldDefinitionId)
            {
                var valueKey = Convert.ToHexStringLower(MerkleManifestV1.ItemKey(MerkleItemKind.FieldValue, item.ItemId));
                if (currentByKey.TryGetValue(valueKey, out var sealedRow) && sealedRow.Document.Header.EntityKind == EncryptedEntityKind && FieldOf(sealedRow.Document) == fieldDefinitionId && SharedColumnsEqualExceptOperation(sealedRow.Document, item.Document))
                {
                    consumed.Add(valueKey);
                    mutations[keyHex] = mutationKind;
                    changed++;
                    continue;
                }
            }

            throw AuthorityException.Invalid($"{prefix}_ITEM_ADDED: item {item.Kind}:{item.ItemId:D} is not part of the accepted snapshot");
        }

        foreach (var (keyHex, item) in currentByKey)
        {
            if (!consumed.Contains(keyHex))
            {
                throw AuthorityException.Invalid($"{prefix}_ITEM_REMOVED: item {item.Kind}:{item.ItemId:D} of the accepted snapshot is missing");
            }
        }

        foreach (var item in candidate.Where(i => i.Kind is ManifestItemKind.FieldValue or ManifestItemKind.FieldSelection && FieldOf(i.Document) == fieldDefinitionId))
        {
            var encrypted = item.Document.Header.EntityKind == EncryptedEntityKind;
            if (direction == ProtectionDirection.Encrypt ? !encrypted : encrypted)
            {
                throw AuthorityException.Invalid($"{prefix}_INCOMPLETE: a row of the field remains in the old form ({item.Document.Header.EntityKind})");
            }
        }

        if (changed == 0)
        {
            throw AuthorityException.Invalid($"{prefix}_NOTHING_TO_DO: the snapshot holds no row of the field to convert");
        }

        return mutations;
    }

    /// <summary>
    /// Protection barrier for user operations (atomic cutover, Amendment A1 req. 7): once the authority holds an active
    /// requirement for a field, no candidate may carry a plaintext row of it; once it holds an active release, no candidate
    /// may carry an encrypted row of it. Applied at prepare and again at commit, so a write built before the command became
    /// effective cannot introduce rows of the old form afterwards; the caller rebuilds and retries.
    /// </summary>
    public static void EnforceBarrier(IEnumerable<(ManifestItemKind Kind, ReadOnlyMemory<byte> Bytes)> items, IReadOnlySet<Guid> mustBeEncrypted, IReadOnlySet<Guid> mustBePlaintext)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(mustBeEncrypted);
        ArgumentNullException.ThrowIfNull(mustBePlaintext);
        if (mustBeEncrypted.Count == 0 && mustBePlaintext.Count == 0)
        {
            return;
        }

        foreach (var (kind, bytes) in items)
        {
            if (kind is not (ManifestItemKind.FieldValue or ManifestItemKind.FieldSelection))
            {
                continue;
            }

            var document = RowCanonicalDecoder.Decode(bytes);
            var field = FieldOf(document);
            if (field is null)
            {
                continue;
            }

            var encrypted = document.Header.EntityKind == EncryptedEntityKind;
            if (mustBeEncrypted.Contains(field.Value) && !encrypted)
            {
                throw new AuthorityException(AuthorityErrorCodes.FieldProtectionChanged, 409, false, "FIELD_PROTECTION_CHANGED: the field now requires application encryption; rebuild the change against the current field protection");
            }

            if (mustBePlaintext.Contains(field.Value) && encrypted)
            {
                throw new AuthorityException(AuthorityErrorCodes.FieldProtectionChanged, 409, false, "FIELD_PROTECTION_CHANGED: encryption of the field has been released; rebuild the change against the current field protection");
            }
        }
    }

    private static bool IsPairedReplacement(RowCanonicalDocument current, RowCanonicalDocument candidate, Guid fieldDefinitionId, ProtectionDirection direction)
    {
        var shapeOk = direction == ProtectionDirection.Encrypt
            ? PlaintextValueKinds.Contains(current.Header.EntityKind) && candidate.Header.EntityKind == EncryptedEntityKind
            : current.Header.EntityKind == EncryptedEntityKind && PlaintextValueKinds.Contains(candidate.Header.EntityKind);
        return shapeOk
            && FieldOf(current) == fieldDefinitionId
            && FieldOf(candidate) == fieldDefinitionId
            && current.Header.OrganizationId == candidate.Header.OrganizationId
            && SharedColumnsEqualExceptOperation(current, candidate);
    }

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

        // A paired replacement must share at least the identity envelope (record, field, item, tenant, group).
        return shared >= 5;
    }
}
