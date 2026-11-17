using System.Runtime.CompilerServices;
using EnterpriseForms.Application.Features.Documents.Ports;
using EnterpriseForms.Domain.Features.FormRecords;

namespace EnterpriseForms.Application.Features.FormRecords.Values;

/// <summary>
/// E1-1: the asynchronous decryption step of an operation whose projection or validation logic is synchronous (record
/// projection, the write rule engine, shared edits, restore planning). The caller decrypts exactly the encrypted rows that
/// logic will read — rows it is already authorized to read — before running it, so key operations stay asynchronous and
/// cancellable without blocking. Plaintext lives only in this per-operation object; rows are matched by reference.
/// E3-M2c OD-M2c-02a: two explicit modes. <see cref="DecryptAsync"/> is Mode A (Authority-verified stored content, the verified
/// batch reader with strict S-8 dispatch). <see cref="DecryptSelfSealedAsync"/> is Mode B (only rows this process freshly sealed
/// through <see cref="FieldValueCodec.ProtectAsync"/> in the current operation; anything else is refused at run time).
/// </summary>
public sealed class DecryptedFieldValues
{
    private readonly Dictionary<FieldEncryptedValue, string> wires;

    private DecryptedFieldValues(Dictionary<FieldEncryptedValue, string> wires) => this.wires = wires;

    /// <summary>No encrypted rows (for operations over plaintext rows only).</summary>
    public static DecryptedFieldValues None { get; } = new(new Dictionary<FieldEncryptedValue, string>(ReferenceEqualityComparer.Instance));

    /// <summary>
    /// Mode A: decrypts every encrypted row of <paramref name="rows"/> — stored rows of an Authority-verified read — through the
    /// verified batch reader (<see cref="Encryption.IFieldEncryptionService.DecryptVerifiedAsync"/>): one call per operation, one
    /// key unwrap per Envelope v2 data-key group, every row checked before any key operation, first failure aborts (fail closed).
    /// A batch belongs to one organization.
    /// </summary>
    public static async Task<DecryptedFieldValues> DecryptAsync(Encryption.IFieldEncryptionService encryption, IEnumerable<ISignedContentRow> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(encryption);
        ArgumentNullException.ThrowIfNull(rows);
        var encrypted = Distinct(rows);
        var map = new Dictionary<FieldEncryptedValue, string>(ReferenceEqualityComparer.Instance);
        if (encrypted.Count == 0)
        {
            return new DecryptedFieldValues(map);
        }

        var organizationId = encrypted[0].TenantId;
        if (encrypted.Any(v => v.TenantId != organizationId))
        {
            throw new InvalidOperationException("a verified decryption batch belongs to exactly one organization");
        }

        var plaintexts = await encryption.DecryptVerifiedAsync(organizationId, encrypted, cancellationToken).ConfigureAwait(false);
        if (plaintexts.Count != encrypted.Count)
        {
            throw new InvalidOperationException("the verified decryption batch returned a different number of values");
        }

        for (var i = 0; i < encrypted.Count; i++)
        {
            map[encrypted[i]] = plaintexts[i];
        }

        return new DecryptedFieldValues(map);
    }

    /// <summary>
    /// Mode B: decrypts rows the current operation itself sealed from its own input (<see cref="FieldValueCodec.ProtectAsync"/>),
    /// with the single-value v1 self-sealed API. A row this process did not freshly seal — a stored, retrieved, cloned or
    /// reconstructed row — is refused before any key operation: such content must take the verified path. The reviewed callers are
    /// pinned by <c>DecryptionCallSiteTests</c>.
    /// </summary>
    public static async Task<DecryptedFieldValues> DecryptSelfSealedAsync(Encryption.IFieldEncryptionService encryption, IEnumerable<ISignedContentRow> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(encryption);
        ArgumentNullException.ThrowIfNull(rows);
        var encrypted = Distinct(rows);
        SelfSealedFieldValues.RequireAll(encrypted);
        var map = new Dictionary<FieldEncryptedValue, string>(ReferenceEqualityComparer.Instance);
        foreach (var value in encrypted)
        {
            map[value] = await encryption.DecryptAsync(value, EncryptionKeyPurpose.FieldValue, cancellationToken).ConfigureAwait(false);
        }

        return new DecryptedFieldValues(map);
    }

    /// <summary>The union of two decryption steps of one operation over disjoint rows (a row decrypted by both is a programming error).</summary>
    public static DecryptedFieldValues Combine(DecryptedFieldValues first, DecryptedFieldValues second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        var map = new Dictionary<FieldEncryptedValue, string>(first.wires, ReferenceEqualityComparer.Instance);
        foreach (var (value, wire) in second.wires)
        {
            if (!map.TryAdd(value, wire))
            {
                throw new InvalidOperationException("a row was decrypted by two decryption steps of one operation");
            }
        }

        return new DecryptedFieldValues(map);
    }

    public string WireOf(FieldEncryptedValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return wires.TryGetValue(value, out var wire) ? wire : throw new InvalidOperationException("an encrypted value was not decrypted for this operation");
    }

    private static List<FieldEncryptedValue> Distinct(IEnumerable<ISignedContentRow> rows)
    {
        var seen = new HashSet<FieldEncryptedValue>(ReferenceEqualityComparer.Instance);
        var list = new List<FieldEncryptedValue>();
        foreach (var row in rows)
        {
            if (row is FieldEncryptedValue encrypted && seen.Add(encrypted))
            {
                list.Add(encrypted);
            }
        }

        return list;
    }
}

/// <summary>
/// E3-M2c OD-M2c-02a run-time provenance for Mode B. <see cref="FieldValueCodec.ProtectAsync"/> — the single product path that
/// seals a field value — registers each envelope instance it freshly seals. The self-sealed entry points accept only registered
/// instances. Rows read from the store (fresh scope, <c>AsNoTracking</c>: always new instances), clones (<c>CloneValue</c>) and
/// envelopes rebuilt from stored bytes are never registered, so they cannot be decrypted in self-sealed mode even if a future
/// change routed them there; they must use the verified reader.
/// <para>
/// Scope and lifetime (closure §5): an entry is keyed by object identity and held weakly (<see cref="ConditionalWeakTable{TKey,TValue}"/>),
/// so it lives exactly as long as the operation that created the instance keeps it reachable, and cannot be obtained by another
/// request without that instance; no service holds envelope instances. Equal bytes confer nothing (identity, not value). Each entry
/// records the binding the seal was issued for — organization, record, field, item, logical type, FieldValue purpose and a digest of
/// every envelope byte and column — and the check requires the instance to still match it, so mutating a registered instance (for
/// example copying stored bytes or another organization into it) voids its provenance. Signing metadata the stager sets in place
/// (operation id, revisions, signature, signed schema) is not part of the binding.
/// </para>
/// </summary>
internal static class SelfSealedFieldValues
{
    private static readonly ConditionalWeakTable<FieldEncryptedValue, Binding> Sealed = new();

    public static void Register(FieldEncryptedValue value) => Sealed.AddOrUpdate(value, Binding.Of(value));

    public static bool IsSelfSealed(FieldEncryptedValue value) => Sealed.TryGetValue(value, out var binding) && binding.Equals(Binding.Of(value));

    public static void RequireAll(IEnumerable<FieldEncryptedValue> values)
    {
        if (values.Any(v => !IsSelfSealed(v)))
        {
            throw new InvalidOperationException("an encrypted value that this operation did not seal cannot be decrypted in self-sealed mode; stored content must use the verified reader");
        }
    }

    /// <summary>What a registration was issued for. Records compare by value; the digest covers every envelope byte and column.</summary>
    private sealed record Binding(Guid TenantId, Guid FormRecordId, Guid FieldDefinitionId, Guid FieldValueItemId, string LogicalType, string PurposeToken, string EnvelopeDigest)
    {
        public static Binding Of(FieldEncryptedValue v)
        {
            using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            void Part(ReadOnlySpan<byte> bytes)
            {
                Span<byte> length = stackalloc byte[4];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
                hash.AppendData(length);
                hash.AppendData(bytes);
            }

            Part(v.EncryptedValueBytes ?? []);
            Part(v.EncryptionNonceBytes ?? []);
            Part(v.EncryptionAuthTagBytes ?? []);
            Part(v.WrappedDataKeyBytes ?? []);
            Part(System.Text.Encoding.UTF8.GetBytes(v.EncryptionAlgorithmName ?? string.Empty));
            Part(System.Text.Encoding.UTF8.GetBytes(v.EncryptionKeyReference ?? string.Empty));
            Part(BitConverter.GetBytes(v.EncryptionEnvelopeVersion));
            Part(v.DataKeyIdentifier?.ToByteArray() ?? []);
            Part(v.DataKeyCommitmentBytes ?? []);
            Part(BitConverter.GetBytes(v.KeyWrapAlgorithmCode ?? -1));
            return new Binding(v.TenantId, v.FormRecordId, v.FieldDefinitionId, v.FieldValueItemId, v.EncryptedLogicalType ?? string.Empty, EncryptionKeyPurpose.FieldValue.Token, Convert.ToHexString(hash.GetHashAndReset()));
        }
    }
}
