using EnterpriseForms.Application.Features.Documents.Ports;
using EnterpriseForms.Domain.Features.FormRecords;

namespace EnterpriseForms.Application.Features.FormRecords.Values;

/// <summary>
/// E1-1: the asynchronous decryption step of an operation whose projection or validation logic is synchronous (record
/// projection, the write rule engine, shared edits, restore planning). The caller decrypts exactly the encrypted rows that
/// logic will read — rows it is already authorized to read — before running it, so key operations stay asynchronous and
/// cancellable without blocking. Plaintext lives only in this per-operation object; rows are matched by reference.
/// </summary>
public sealed class DecryptedFieldValues
{
    private readonly Dictionary<FieldEncryptedValue, string> wires;

    private DecryptedFieldValues(Dictionary<FieldEncryptedValue, string> wires) => this.wires = wires;

    /// <summary>No encrypted rows (for operations over plaintext rows only).</summary>
    public static DecryptedFieldValues None { get; } = new(new Dictionary<FieldEncryptedValue, string>(ReferenceEqualityComparer.Instance));

    /// <summary>Decrypts every encrypted row of <paramref name="rows"/> with its tenant's FieldValue key (first failure aborts; fail closed).</summary>
    public static async Task<DecryptedFieldValues> DecryptAsync(Encryption.IFieldEncryptionService encryption, IEnumerable<ISignedContentRow> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(encryption);
        ArgumentNullException.ThrowIfNull(rows);
        var map = new Dictionary<FieldEncryptedValue, string>(ReferenceEqualityComparer.Instance);
        foreach (var row in rows)
        {
            if (row is FieldEncryptedValue encrypted && !map.ContainsKey(encrypted))
            {
                map[encrypted] = await encryption.DecryptAsync(encrypted, EncryptionKeyPurpose.FieldValue, cancellationToken).ConfigureAwait(false);
            }
        }

        return new DecryptedFieldValues(map);
    }

    public string WireOf(FieldEncryptedValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return wires.TryGetValue(value, out var wire) ? wire : throw new InvalidOperationException("an encrypted value was not decrypted for this operation");
    }
}
