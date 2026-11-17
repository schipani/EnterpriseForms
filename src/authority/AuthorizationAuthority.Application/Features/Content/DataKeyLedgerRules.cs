using AuthorizationAuthority.Domain.Features.Content;
using EnterpriseForms.Integrity.Canonical;

namespace AuthorizationAuthority.Application.Features.Content;

/// <summary>
/// E3-M2e (docs/security/E3_M2E_CONTRACT_CLOSURE.md §3; OD-E3-16): the pure decisions of the Authority-owned data-key ledger. Decided
/// from canonical bytes the Authority has verified and from ledger rows; no key, no plaintext, no business data. The callers run them
/// under the organization lock, in the Prepare or Commit transaction. Error texts carry reason codes only.
/// <list type="bullet">
/// <item><b>DK-4</b> (Prepare): no New group's identifier exists in the ledger, in any organization or operation state.</item>
/// <item><b>DK-5</b> (Prepare): one binding per New group, bound to this operation, organization, record, kind and commitment.</item>
/// <item><b>DK-6</b> (Commit): the bindings of this operation equal exactly the New groups derived from the verified staged bytes.</item>
/// <item><b>P-4</b> (Commit): every other v2 group is bound to an accepted (Committed) operation of the same organization and record,
/// with the same kind and commitment, and no more staged rows than its binding. P-4 is an additional ledger check; it does not prove
/// that a carried row comes from the current revision (that is DK-1 at Prepare, covered by CARV 3 and the head CAS).</item>
/// <item><b>DK-8</b> (Commit): the archived tuples of the New groups, in a deterministic order.</item>
/// </list>
/// Callers must have applied the v2 shape and batch rules first (<see cref="EnvelopeV2Rules.Enforce"/>): every row of one identifier
/// then has one kind and one commitment.
/// </summary>
public static class DataKeyLedgerRules
{
    /// <summary>One v2 data-key group of a candidate or staged revision: all rows that carry one identifier.</summary>
    public sealed record DataKeyGroup(Guid DataKeyIdentifier, short KindCode, byte[] Commitment, int ItemCount);

    /// <summary>One archived binding (DK-8, AM-3): with the stream's organization and the body's record and operation, every ledger column.</summary>
    public sealed record ArchivedBinding(Guid ContentDataKeyBindingId, Guid DataKeyIdentifier, short EnvelopePayloadKindCode, string DataKeyCommitmentHex, int BindingItemCount, DateTimeOffset DataKeyBoundOn);

    /// <summary>The v2 groups of <paramref name="rows"/> (v1 and non-envelope rows are ignored), ordered by identifier byte order.</summary>
    public static IReadOnlyList<DataKeyGroup> GroupsOf(IEnumerable<RowCanonicalDocument> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var groups = new Dictionary<Guid, (short Kind, byte[] Commitment, int Count)>();
        foreach (var document in rows)
        {
            if (!EnvelopeV2Rules.IsV2(document))
            {
                continue;
            }

            var id = EnvelopeV2Rules.DataKeyIdentifierOf(document) ?? throw EnvelopeRules.Refused(EnvelopeRules.Reasons.ColumnMissing);
            var kind = document.Header.EntityKind == EnvelopeRules.FieldEntityKind ? ContentDataKeyBindingKinds.FieldValueBatch : ContentDataKeyBindingKinds.DocumentVersion;
            var commitmentColumn = document.Column("data_key_commitment_bytes");
            if (commitmentColumn is null || commitmentColumn.IsNull)
            {
                throw EnvelopeRules.Refused(EnvelopeRules.Reasons.ColumnMissing); // R-M2d-8: NULL is tested explicitly, never read as empty
            }

            var commitment = commitmentColumn.AsBytes() is { } bytes ? bytes.ToArray() : throw EnvelopeRules.Refused(EnvelopeRules.Reasons.ColumnMissing);
            if (groups.TryGetValue(id, out var existing))
            {
                if (existing.Kind != kind || !existing.Commitment.AsSpan().SequenceEqual(commitment))
                {
                    throw EnvelopeRules.Refused(EnvelopeV2Rules.Reasons.BatchInconsistent); // unreachable after the batch rules; kept fail closed
                }

                groups[id] = (kind, existing.Commitment, existing.Count + 1);
            }
            else
            {
                groups[id] = (kind, commitment, 1);
            }
        }

        return groups.Select(g => new DataKeyGroup(g.Key, g.Value.Kind, g.Value.Commitment, g.Value.Count)).OrderBy(g => g.DataKeyIdentifier, IdentifierOrder.Instance).ToList();
    }

    /// <summary>DK-4: null when no New identifier is bound anywhere; otherwise <c>DATA_KEY_IDENTIFIER_REUSED</c>.</summary>
    public static string? PrepareViolation(IReadOnlyCollection<DataKeyGroup> newGroups, IReadOnlyCollection<ContentDataKeyBinding> existingBindings)
    {
        ArgumentNullException.ThrowIfNull(newGroups);
        ArgumentNullException.ThrowIfNull(existingBindings);
        var ids = newGroups.Select(g => g.DataKeyIdentifier).ToHashSet();
        return existingBindings.Any(b => ids.Contains(b.DataKeyIdentifier)) ? EnvelopeV2Rules.Reasons.DataKeyIdentifierReused : null;
    }

    /// <summary>DK-5: one binding per New group. The bound-on time is truncated to the microsecond (PostgreSQL timestamptz precision).</summary>
    public static List<ContentDataKeyBinding> BindingsFor(IReadOnlyCollection<DataKeyGroup> newGroups, Guid organizationId, Guid formRecordId, Guid contentChangeOperationId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(newGroups);
        var boundOn = new DateTimeOffset(now.UtcTicks - (now.UtcTicks % 10), TimeSpan.Zero);
        return newGroups.Select(g => new ContentDataKeyBinding
        {
            ContentDataKeyBindingId = Guid.CreateVersion7(),
            DataKeyIdentifier = g.DataKeyIdentifier,
            OrganizationId = organizationId,
            FormRecordId = formRecordId,
            EnvelopePayloadKindCode = g.KindCode,
            DataKeyCommitmentBytes = [.. g.Commitment],
            BindingItemCount = g.ItemCount,
            BindingContentChangeOperationId = contentChangeOperationId,
            DataKeyBoundOn = boundOn,
        }).ToList();
    }

    /// <summary>
    /// DK-6 and P-4 at Commit. <paramref name="stagedGroups"/> come from the verified staged bytes; <paramref name="bindingsOfOperation"/>
    /// are the ledger rows whose binding operation is this one; <paramref name="bindingsOfStagedIdentifiers"/> are the ledger rows of the
    /// staged identifiers (any operation); <paramref name="committedOperationIds"/> the binding operations among them that are Committed.
    /// Returns the New groups (bound to this operation) when consistent, otherwise the violation <c>DATA_KEY_BINDING_MISMATCH</c>.
    /// </summary>
    public static (string? Violation, IReadOnlyList<DataKeyGroup> NewGroups) CommitDecision(
        IReadOnlyList<DataKeyGroup> stagedGroups,
        IReadOnlyCollection<ContentDataKeyBinding> bindingsOfOperation,
        IReadOnlyCollection<ContentDataKeyBinding> bindingsOfStagedIdentifiers,
        IReadOnlySet<Guid> committedOperationIds,
        Guid organizationId,
        Guid formRecordId,
        Guid contentChangeOperationId)
    {
        ArgumentNullException.ThrowIfNull(stagedGroups);
        ArgumentNullException.ThrowIfNull(bindingsOfOperation);
        ArgumentNullException.ThrowIfNull(bindingsOfStagedIdentifiers);
        ArgumentNullException.ThrowIfNull(committedOperationIds);
        const string mismatch = DataKeyLedgerFailures.BindingMismatchReason;
        var staged = stagedGroups.ToDictionary(g => g.DataKeyIdentifier);
        var byIdentifier = new Dictionary<Guid, ContentDataKeyBinding>();
        foreach (var binding in bindingsOfStagedIdentifiers.Concat(bindingsOfOperation))
        {
            if (byIdentifier.TryGetValue(binding.DataKeyIdentifier, out var other) && other.ContentDataKeyBindingId != binding.ContentDataKeyBindingId)
            {
                return (mismatch, []); // two ledger rows for one identifier: impossible under the unique index
            }

            byIdentifier[binding.DataKeyIdentifier] = binding;
        }

        // DK-6: every binding of this operation names a staged group with exactly its tuple.
        var newGroups = new List<DataKeyGroup>();
        foreach (var binding in bindingsOfOperation)
        {
            if (binding.BindingContentChangeOperationId != contentChangeOperationId || !staged.TryGetValue(binding.DataKeyIdentifier, out var group)
                || !SameTuple(binding, group, organizationId, formRecordId) || binding.BindingItemCount != group.ItemCount)
            {
                return (mismatch, []);
            }

            newGroups.Add(group);
        }

        // P-4: every other staged group is bound to an accepted operation of this organization and record, kind and commitment.
        foreach (var group in stagedGroups)
        {
            if (!byIdentifier.TryGetValue(group.DataKeyIdentifier, out var binding))
            {
                return (mismatch, []); // an unbound v2 identifier can never be committed
            }

            if (binding.BindingContentChangeOperationId == contentChangeOperationId)
            {
                continue; // a New group, judged by DK-6 above
            }

            if (!committedOperationIds.Contains(binding.BindingContentChangeOperationId) || !SameTuple(binding, group, organizationId, formRecordId)
                || group.ItemCount > binding.BindingItemCount)
            {
                return (mismatch, []);
            }
        }

        return (null, newGroups.OrderBy(g => g.DataKeyIdentifier, IdentifierOrder.Instance).ToList());
    }

    /// <summary>DK-8 (AM-3): the archived tuples of the given bindings, ordered by the RFC 9562 byte order of the identifier.</summary>
    public static IReadOnlyList<ArchivedBinding> ArchiveTuples(IEnumerable<ContentDataKeyBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        return bindings.OrderBy(b => b.DataKeyIdentifier, IdentifierOrder.Instance)
            .Select(b => new ArchivedBinding(b.ContentDataKeyBindingId, b.DataKeyIdentifier, b.EnvelopePayloadKindCode, Convert.ToHexStringLower(b.DataKeyCommitmentBytes), b.BindingItemCount, b.DataKeyBoundOn.ToUniversalTime()))
            .ToList();
    }

    private static bool SameTuple(ContentDataKeyBinding binding, DataKeyGroup group, Guid organizationId, Guid formRecordId) =>
        binding.OrganizationId == organizationId && binding.FormRecordId == formRecordId && binding.EnvelopePayloadKindCode == group.KindCode
        && binding.DataKeyCommitmentBytes.AsSpan().SequenceEqual(group.Commitment);

    /// <summary>RFC 9562 byte order (big-endian), independent of the platform's Guid comparison.</summary>
    public sealed class IdentifierOrder : IComparer<Guid>
    {
        public static readonly IdentifierOrder Instance = new();

        public int Compare(Guid x, Guid y)
        {
            Span<byte> a = stackalloc byte[16];
            Span<byte> b = stackalloc byte[16];
            x.TryWriteBytes(a, bigEndian: true, out _);
            y.TryWriteBytes(b, bigEndian: true, out _);
            return a.SequenceCompareTo(b);
        }
    }
}
