using System.Security.Cryptography;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Content;
using EnterpriseForms.Integrity;
using EnterpriseForms.Integrity.Canonical;
using EnterpriseForms.Integrity.Merkle;

namespace AuthorizationAuthority.Application.Features.Content;

/// <summary>Class of one signed column (Phase C5 proposal §5.2, decision D14). Framing (header) fields are checked separately.</summary>
public enum SignedColumnClass
{
    /// <summary>What the item is and where it belongs: must be equal between the current and the candidate row.</summary>
    Identity,

    /// <summary>The data the item carries: equal for every item except an authorized share-editor update.</summary>
    Content,

    /// <summary>The operation the row was staged under: must equal the exact expected operation (current: head; candidate: the new operation).</summary>
    OperationBinding,
}

/// <summary>Meaning of the canonical header primary key for an entity kind.</summary>
public enum PrimaryKeyClass
{
    /// <summary>The primary key is the logical item identity (record header, repeating-group occurrence).</summary>
    Identity,

    /// <summary>The primary key is a per-operation surrogate of the staged physical row (value rows, attachment versions).</summary>
    StorageKey,
}

/// <summary>Column-class assignment of one signed (entity kind, schema version) — the Authority's own copy, cross-checked against the business registry by an architecture test.</summary>
public sealed record SignedItemRule(
    ManifestItemKind Kind,
    string EntityKind,
    int SchemaVersion,
    PrimaryKeyClass PrimaryKey,
    string? ItemIdColumn,
    IReadOnlyDictionary<string, SignedColumnClass> Columns,
    bool ShareUpdatable);

/// <summary>
/// Phase C5 (decisions D1, D3, D4, D8, D14, D15): identity binding for every user content write, and the column-class
/// classification that lets a share editor (contributor/manager) change only explicitly shared, identity-preserving,
/// top-level single-item values while every other item of the record is proven unchanged. Decided from canonical bytes
/// alone; the Authority never sees plaintext or keys. Every unknown or uncertain case fails closed.
/// </summary>
public static class ContentDeltaRules
{
    public const string OperationColumn = "content_change_operation_id";
    public const string TenantColumn = "tenant_id";
    public const string RecordColumn = "form_record_id";
    public const string FieldColumn = "field_definition_id";
    public const string GroupColumn = "form_record_group_id";
    public const string EncryptedEntityKind = "field_encrypted_values";
    public const string NonceColumn = "encryption_nonce_bytes";
    public const string WrappedKeyColumn = "wrapped_data_key_bytes";
    public const string AlgorithmColumn = "encryption_algorithm_name";
    public const string EnvelopeVersionColumn = "encryption_envelope_version";

    /// <summary>Envelope algorithms a share-editor update may introduce (business AesGcmFieldEncryptionService).</summary>
    public static readonly IReadOnlySet<string> AllowedEnvelopeAlgorithms = new HashSet<string>(StringComparer.Ordinal) { "AES-256-GCM" };

    public static readonly IReadOnlySet<int> AllowedEnvelopeVersions = new HashSet<int> { 1 };

    private static readonly string[] PlaintextValueKinds =
    [
        "field_string_values", "field_integer_values", "field_decimal_values", "field_date_values", "field_date_time_values", "field_boolean_values", "field_guid_values",
    ];

    private static readonly Dictionary<string, string> PlaintextValueColumn = new(StringComparer.Ordinal)
    {
        ["field_string_values"] = "string_value",
        ["field_integer_values"] = "integer_value",
        ["field_decimal_values"] = "decimal_value",
        ["field_date_values"] = "date_value",
        ["field_date_time_values"] = "date_time_value",
        ["field_boolean_values"] = "boolean_value",
        ["field_guid_values"] = "guid_value",
    };

    private static readonly Dictionary<(string EntityKind, int SchemaVersion), SignedItemRule> RuleTable = BuildRules();

    /// <summary>Every supported (entity kind, schema version) and its column classes.</summary>
    public static IReadOnlyCollection<SignedItemRule> Rules => RuleTable.Values;

    public static SignedItemRule? FindRule(string entityKind, int schemaVersion) => RuleTable.GetValueOrDefault((entityKind, schemaVersion));

    /// <summary>
    /// Identity binding I1–I3 (decision D8), applied to every candidate item of every user or reprotection write: the
    /// (entity kind, schema version) is supported, the column set is exactly the registered one, the item id inside the
    /// bytes equals the manifest item id, and tenant/record columns equal the request. Throws an invalid-command error.
    /// </summary>
    public static void EnforceIdentityBinding(ManifestItemKind kind, Guid itemId, RowCanonicalDocument document, Guid organizationId, Guid formRecordId)
    {
        ArgumentNullException.ThrowIfNull(document);
        var rule = RequireRule(document);
        if (rule.Kind != kind)
        {
            throw AuthorityException.Invalid("IDENTITY_BINDING: entity kind does not belong to the item kind");
        }

        if (document.Header.OrganizationId != organizationId || document.Column(TenantColumn)?.AsUuid() != organizationId)
        {
            throw AuthorityException.Invalid("IDENTITY_BINDING: tenant binding mismatch");
        }

        if (kind == ManifestItemKind.RecordHeader)
        {
            if (document.Header.PrimaryKeyId != formRecordId || itemId != formRecordId)
            {
                throw AuthorityException.Invalid("IDENTITY_BINDING: record header is bound to another record");
            }
        }
        else if (document.Column(RecordColumn)?.AsUuid() != formRecordId)
        {
            throw AuthorityException.Invalid("IDENTITY_BINDING: item is bound to another record");
        }

        var boundItemId = rule.PrimaryKey == PrimaryKeyClass.Identity ? document.Header.PrimaryKeyId : document.Column(rule.ItemIdColumn!)?.AsUuid();
        if (boundItemId != itemId)
        {
            throw AuthorityException.Invalid("IDENTITY_BINDING: item identity inside the signed bytes differs from the manifest item id");
        }
    }

    /// <summary>One decoded item of a snapshot (current or candidate).</summary>
    public sealed record DecodedItem(ManifestItemKind Kind, Guid ItemId, byte[] Key, RowCanonicalDocument Document, byte[] Digest);

    /// <summary>The accepted state the current snapshot must prove equality with (head + its manifest commit + leaf index).</summary>
    public sealed record AcceptedState(
        long ContentRevision,
        Guid OperationId,
        byte[] ManifestRoot,
        byte[] LeafIndexDigest,
        long LeafCount,
        int SignatureKeyVersion,
        IReadOnlyList<(byte[] Key, byte KindCode, Guid ItemId, byte[] Digest, byte[] Signature)> Leaves);

    /// <summary>
    /// Decision D4 (condition 2): proves that the supplied current snapshot is exactly the accepted, previously verified
    /// signed state — complete key coverage, no duplicates, every SHA-256 equal to the accepted digest, header bindings to
    /// the organization and accepted key version, and the Merkle root and leaf-index digest recomputed from the accepted
    /// signatures and the observed digests equal the accepted head. Returns null and the decoded items, or a failure code.
    /// </summary>
    public static (string? FailureCode, List<DecodedItem> Items) BindCurrentSnapshot(IReadOnlyList<StagedContentItem> current, AcceptedState accepted, Guid organizationId)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(accepted);
        if (current.Count != accepted.Leaves.Count || accepted.Leaves.Count != accepted.LeafCount)
        {
            return ("CURRENT_SNAPSHOT_COVERAGE", []);
        }

        var expected = accepted.Leaves.ToDictionary(l => Convert.ToHexStringLower(l.Key), StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var items = new List<DecodedItem>(current.Count);
        var leaves = new List<MerkleLeafInput>(current.Count);
        foreach (var item in current)
        {
            if (!Enum.IsDefined(item.ItemKind))
            {
                return ("CURRENT_SNAPSHOT_COVERAGE", []);
            }

            var kind = (MerkleItemKind)(byte)item.ItemKind;
            var key = MerkleManifestV1.ItemKey(kind, item.ItemId);
            var keyHex = Convert.ToHexStringLower(key);
            if (!expected.TryGetValue(keyHex, out var leaf) || !seen.Add(keyHex))
            {
                return ("CURRENT_SNAPSHOT_COVERAGE", []);
            }

            byte[] bytes;
            RowCanonicalDocument document;
            try
            {
                bytes = Convert.FromBase64String(item.CanonicalBytesBase64);
                document = RowCanonicalDecoder.Decode(bytes);
            }
            catch (FormatException)
            {
                return ("CURRENT_SNAPSHOT_ENCODING", []);
            }
            catch (IntegrityFormatException)
            {
                return ("CURRENT_SNAPSHOT_ENCODING", []);
            }

            var digest = SHA256.HashData(bytes);
            if (!digest.AsSpan().SequenceEqual(leaf.Digest))
            {
                return ("CURRENT_SNAPSHOT_DIGEST", []);
            }

            if (document.Header.OrganizationId != organizationId || document.Header.KeyVersion != (uint)accepted.SignatureKeyVersion)
            {
                return ("CURRENT_SNAPSHOT_BINDING", []);
            }

            items.Add(new DecodedItem(item.ItemKind, item.ItemId, key, document, digest));
            leaves.Add(new MerkleLeafInput(kind, item.ItemId, leaf.Signature, digest, (ulong)accepted.ContentRevision, accepted.OperationId));
        }

        var rebuilt = MerkleManifestV1.Build(leaves);
        if (!rebuilt.MerkleRoot.AsSpan().SequenceEqual(accepted.ManifestRoot) || !rebuilt.LeafIndexDigest.AsSpan().SequenceEqual(accepted.LeafIndexDigest))
        {
            return ("CURRENT_SNAPSHOT_ROOT", []);
        }

        return (null, items);
    }

    /// <summary>Inputs of the share-scoped classification.</summary>
    public sealed record ShareScope(
        Guid OrganizationId,
        Guid FormRecordId,
        long CurrentRevision,
        Guid CurrentOperationId,
        long CandidateRevision,
        Guid CandidateOperationId,
        IReadOnlySet<Guid> EditableItemIds);

    /// <summary>
    /// Classifies every item of a share editor's candidate against the bound current snapshot (proposal §5–§7). Returns the
    /// mutation kind per item key (hex): only <c>Unchanged</c> and <c>Update</c>. Any other difference, an unsupported
    /// schema, an invalid operation binding, a reused storage key or a missing update throws (fail closed). Error texts
    /// carry reason codes only, never item ids.
    /// </summary>
    public static Dictionary<string, string> ClassifyShareScoped(IReadOnlyList<DecodedItem> current, IReadOnlyList<DecodedItem> candidate, ShareScope scope)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(scope);
        var currentByKey = new Dictionary<string, DecodedItem>(StringComparer.Ordinal);
        foreach (var item in current)
        {
            if (!currentByKey.TryAdd(Convert.ToHexStringLower(item.Key), item))
            {
                throw AuthorityException.Invalid("CURRENT_SNAPSHOT_COVERAGE");
            }
        }

        var candidateKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in candidate)
        {
            if (!candidateKeys.Add(Convert.ToHexStringLower(item.Key)))
            {
                throw AuthorityException.Invalid("duplicate candidate item");
            }
        }

        if (!candidateKeys.SetEquals(currentByKey.Keys))
        {
            throw Out("SHARE_ITEM_ADDED_OR_REMOVED");
        }

        // Single-item fields (decision D3, trusted boundary): number of current top-level value/selection items per field.
        var itemsPerField = new Dictionary<Guid, int>();
        var currentStorageKeys = new HashSet<Guid>();
        foreach (var item in current)
        {
            var rule = RequireRule(item.Document);
            RequireBinding(item.Document, scope.CurrentOperationId, scope.CurrentRevision, AuthorityErrorCodes.CurrentSnapshotMismatch, "CURRENT_SNAPSHOT_LEGACY_BINDING");
            if (rule.PrimaryKey == PrimaryKeyClass.StorageKey)
            {
                currentStorageKeys.Add(item.Document.Header.PrimaryKeyId);
            }

            if (item.Kind is ManifestItemKind.FieldValue or ManifestItemKind.FieldSelection && item.Document.Column(FieldColumn)?.AsUuid() is { } field)
            {
                itemsPerField[field] = itemsPerField.GetValueOrDefault(field) + 1;
            }
        }

        var candidateStorageKeys = new HashSet<Guid>();
        var mutations = new Dictionary<string, string>(StringComparer.Ordinal);
        var updates = 0;
        foreach (var cand in candidate)
        {
            var keyHex = Convert.ToHexStringLower(cand.Key);
            var cur = currentByKey[keyHex];
            var rule = RequireRule(cand.Document);
            EnforceIdentityBinding(cand.Kind, cand.ItemId, cand.Document, scope.OrganizationId, scope.FormRecordId);
            RequireBinding(cand.Document, scope.CandidateOperationId, scope.CandidateRevision, AuthorityErrorCodes.CandidateMismatch, "CANDIDATE_OPERATION_BINDING");

            // Framing: same entity kind, schema version and ordered column list (no schema upgrade, no storage-type switch).
            if (cur.Kind != cand.Kind || cur.Document.Header.EntityKind != cand.Document.Header.EntityKind || cur.Document.Header.SchemaVersion != cand.Document.Header.SchemaVersion
                || !SameColumnNames(cur.Document, cand.Document))
            {
                throw Out("SHARE_ITEM_FRAMING_CHANGED");
            }

            // Storage key: a fresh, unique surrogate per staged physical row; identity keys must be equal.
            if (rule.PrimaryKey == PrimaryKeyClass.StorageKey)
            {
                var storageKey = cand.Document.Header.PrimaryKeyId;
                if (storageKey == Guid.Empty || !candidateStorageKeys.Add(storageKey) || currentStorageKeys.Contains(storageKey))
                {
                    throw Out("SHARE_ITEM_STORAGE_KEY_REUSED");
                }
            }
            else if (cur.Document.Header.PrimaryKeyId != cand.Document.Header.PrimaryKeyId)
            {
                throw Out("SHARE_ITEM_IDENTITY_CHANGED");
            }

            if (!ColumnsEqual(cur.Document, cand.Document, rule, SignedColumnClass.Identity))
            {
                throw Out("SHARE_ITEM_IDENTITY_CHANGED");
            }

            if (ColumnsEqual(cur.Document, cand.Document, rule, SignedColumnClass.Content))
            {
                mutations[keyHex] = ContentMutationKinds.Unchanged;
                continue;
            }

            RequirePermittedUpdate(cur, cand, rule, scope, itemsPerField);
            mutations[keyHex] = ContentMutationKinds.Update;
            updates++;
        }

        if (updates == 0)
        {
            throw new AuthorityException(AuthorityErrorCodes.InvalidCommand, 422, false, "NO_CHANGE: a share-scoped write must change at least one shared value");
        }

        return mutations;
    }

    private static void RequirePermittedUpdate(DecodedItem cur, DecodedItem cand, SignedItemRule rule, ShareScope scope, IReadOnlyDictionary<Guid, int> itemsPerField)
    {
        if (!rule.ShareUpdatable || cand.Kind is not (ManifestItemKind.FieldValue or ManifestItemKind.FieldSelection))
        {
            throw Out("SHARE_ITEM_NOT_UPDATABLE");
        }

        if (!scope.EditableItemIds.Contains(cand.ItemId))
        {
            throw Out("SHARE_ITEM_NOT_IN_SCOPE");
        }

        // V1: top-level values only (decision D2).
        if (cand.Document.Column(GroupColumn) is not { IsNull: true } || cur.Document.Column(GroupColumn) is not { IsNull: true })
        {
            throw Out("SHARE_ITEM_IN_REPEATING_SECTION");
        }

        // D3: the field must hold exactly one item; an update replaces it with exactly one item (multi-selections stay owner-only).
        if (cand.Document.Column(FieldColumn)?.AsUuid() is not { } field || itemsPerField.GetValueOrDefault(field) != 1)
        {
            throw Out("SHARE_ITEM_NOT_SINGLE_VALUE");
        }

        if (cand.Document.Header.EntityKind == EncryptedEntityKind)
        {
            // The whole sealed envelope is replaced: new nonce and new wrapped data key, allow-listed algorithm/version.
            if (cur.Document.Column(NonceColumn)!.FramedValueEquals(cand.Document.Column(NonceColumn)!)
                || cur.Document.Column(WrappedKeyColumn)!.FramedValueEquals(cand.Document.Column(WrappedKeyColumn)!))
            {
                throw Out("SHARE_ENVELOPE_REUSED");
            }

            if (cand.Document.Column(AlgorithmColumn)?.AsText() is not { } algorithm || !AllowedEnvelopeAlgorithms.Contains(algorithm)
                || ReadInt32(cand.Document.Column(EnvelopeVersionColumn)) is not { } version || !AllowedEnvelopeVersions.Contains(version))
            {
                throw Out("SHARE_ENVELOPE_NOT_ALLOWED");
            }
        }
    }

    /// <summary>Operation binding (class B): the row revision and the operation column must equal the exact expected values — never ignored.</summary>
    private static void RequireBinding(RowCanonicalDocument document, Guid expectedOperation, long expectedRevision, string errorCode, string reason)
    {
        if (document.Header.RowRevision != (ulong)expectedRevision || document.Column(OperationColumn)?.AsUuid() != expectedOperation)
        {
            throw new AuthorityException(errorCode, 409, false, reason);
        }
    }

    private static SignedItemRule RequireRule(RowCanonicalDocument document)
    {
        var rule = FindRule(document.Header.EntityKind, document.Header.SchemaVersion)
                   ?? throw AuthorityException.Invalid("UNSUPPORTED_SIGNED_SCHEMA: entity kind or schema version has no column-class assignment");
        if (document.Columns.Count != rule.Columns.Count || document.Columns.Any(c => !rule.Columns.ContainsKey(c.Name)))
        {
            throw AuthorityException.Invalid("UNSUPPORTED_SIGNED_SCHEMA: column set differs from the registered assignment");
        }

        return rule;
    }

    private static bool SameColumnNames(RowCanonicalDocument a, RowCanonicalDocument b) =>
        a.Columns.Count == b.Columns.Count && a.Columns.Select(c => c.Name).SequenceEqual(b.Columns.Select(c => c.Name), StringComparer.Ordinal);

    private static bool ColumnsEqual(RowCanonicalDocument current, RowCanonicalDocument candidate, SignedItemRule rule, SignedColumnClass columnClass)
    {
        foreach (var (name, cls) in rule.Columns)
        {
            if (cls != columnClass)
            {
                continue;
            }

            var a = current.Column(name);
            var b = candidate.Column(name);
            if (a is null || b is null || !a.FramedValueEquals(b))
            {
                return false;
            }
        }

        return true;
    }

    private static int? ReadInt32(RowCanonicalColumn? column)
    {
        if (column is null || column.Type != RowColumnType.Int32 || column.FramedValue.Length != 5)
        {
            return null;
        }

        return System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(column.FramedValue.Span[1..]);
    }

    private static AuthorityException Out(string code) => AuthorityException.Forbidden("SHARE_MUTATION_OUT_OF_SCOPE: " + code);

    private static Dictionary<(string, int), SignedItemRule> BuildRules()
    {
        const SignedColumnClass K = SignedColumnClass.Identity;
        const SignedColumnClass C = SignedColumnClass.Content;
        const SignedColumnClass B = SignedColumnClass.OperationBinding;
        var rules = new List<SignedItemRule>();

        Dictionary<string, SignedColumnClass> ValueEnvelope() => new(StringComparer.Ordinal)
        {
            [OperationColumn] = B, [FieldColumn] = K, ["field_value_item_id"] = K, [GroupColumn] = K, [RecordColumn] = K, [TenantColumn] = K,
        };

        foreach (var kind in PlaintextValueKinds)
        {
            var columns = ValueEnvelope();
            columns[PlaintextValueColumn[kind]] = C;
            rules.Add(new SignedItemRule(ManifestItemKind.FieldValue, kind, 1, PrimaryKeyClass.StorageKey, "field_value_item_id", columns, ShareUpdatable: true));
        }

        var encrypted = ValueEnvelope();
        encrypted["encrypted_logical_type"] = K;
        foreach (var column in new[] { "encrypted_value_bytes", "encryption_auth_tag_bytes", NonceColumn, WrappedKeyColumn, "encryption_key_reference", AlgorithmColumn, EnvelopeVersionColumn })
        {
            encrypted[column] = C;
        }

        rules.Add(new SignedItemRule(ManifestItemKind.FieldValue, EncryptedEntityKind, 1, PrimaryKeyClass.StorageKey, "field_value_item_id", encrypted, ShareUpdatable: true));

        var option = ValueEnvelope();
        option["field_option_id"] = C;
        rules.Add(new SignedItemRule(ManifestItemKind.FieldSelection, "field_option_values", 1, PrimaryKeyClass.StorageKey, "field_value_item_id", option, ShareUpdatable: true));

        var headerV2 = new Dictionary<string, SignedColumnClass>(StringComparer.Ordinal)
        {
            [OperationColumn] = B, ["created_by_user_id"] = C, ["created_form_version_id"] = C, ["form_definition_id"] = K, ["record_status"] = C, ["submitted_on"] = C, [TenantColumn] = K,
        };
        rules.Add(new SignedItemRule(ManifestItemKind.RecordHeader, "form_records", 2, PrimaryKeyClass.Identity, null, headerV2, ShareUpdatable: false));
        var headerV3 = new Dictionary<string, SignedColumnClass>(headerV2, StringComparer.Ordinal)
        {
            ["form_relationship_definition_id"] = K, ["parent_form_record_id"] = K, ["relationship_sort_order"] = C,
        };
        rules.Add(new SignedItemRule(ManifestItemKind.RecordHeader, "form_records", 3, PrimaryKeyClass.Identity, null, headerV3, ShareUpdatable: false));

        rules.Add(new SignedItemRule(ManifestItemKind.RepeatedGroup, "form_record_groups", 1, PrimaryKeyClass.Identity, null, new Dictionary<string, SignedColumnClass>(StringComparer.Ordinal)
        {
            [OperationColumn] = B, [RecordColumn] = K, ["group_definition_id"] = K, ["group_sort_order"] = C, ["parent_form_record_group_id"] = K, [TenantColumn] = K,
        }, ShareUpdatable: false));

        var attachment = new Dictionary<string, SignedColumnClass>(StringComparer.Ordinal)
        {
            [OperationColumn] = B, ["file_attachment_id"] = K, [RecordColumn] = K, [TenantColumn] = K, [FieldColumn] = K, ["version_number"] = K,
        };
        foreach (var column in new[]
                 {
                     "ciphertext_sha256", "ciphertext_size_bytes", "detected_content_type", AlgorithmColumn, EnvelopeVersionColumn, "encryption_key_reference", "encryption_nonce_prefix",
                     "encryption_segment_size_bytes", "original_file_name", "plaintext_sha256", "plaintext_size_bytes", "scan_signature_name", "scan_verdict", "scanned_on", "scanner_name",
                     "storage_object_key", "storage_provider_name", "uploaded_by_user_id", "uploaded_on", "wrapped_data_key",
                 })
        {
            attachment[column] = C;
        }

        rules.Add(new SignedItemRule(ManifestItemKind.Attachment, "file_attachment_versions", 1, PrimaryKeyClass.StorageKey, "file_attachment_id", attachment, ShareUpdatable: false));

        // file_attachment_lifecycle_events (AttachmentLifecycle) has no registered business schema and no producer: it is
        // deliberately absent, so any such item fails closed (UNSUPPORTED_SIGNED_SCHEMA) for identity binding and share writes.
        return rules.ToDictionary(r => (r.EntityKind, r.SchemaVersion));
    }
}
