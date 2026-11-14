using System.Security.Cryptography;
using System.Text;
using EnterpriseForms.Application.Features.Documents.Ports;

namespace EnterpriseForms.Infrastructure.Features.Documents.EnvelopeV2;

/// <summary>
/// One sealed field item (kinds FieldValueItem and SealedKey). Associated data = H ‖ C_field. Portable form (§3.10):
/// <code>H ‖ C_field ‖ u16 len ‖ wrapped seed ‖ nonce(12) ‖ tag(16) ‖ u32 len ‖ ciphertext</code>
/// All byte members are defensive copies; an instance is immutable.
/// </summary>
internal sealed class FieldEnvelopeV2
{
    private readonly byte[] wrappedSeed;
    private readonly byte[] nonce;
    private readonly byte[] tag;
    private readonly byte[] ciphertext;

    public FieldEnvelopeV2(EnvelopeV2Header header, FieldItemIdentity identity, ReadOnlySpan<byte> wrappedSeed, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> tag, ReadOnlySpan<byte> ciphertext)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(identity);
        if (header.Prefix.Kind is not (EnvelopeV2PayloadKind.FieldValueItem or EnvelopeV2PayloadKind.SealedKey))
        {
            throw EnvelopeV2Format.Unsupported();
        }

        identity.Validate(header.Prefix.Kind);
        if (wrappedSeed.Length != EnvelopeV2Format.WrappedSeedLengthOf(header.Prefix.WrapAlgorithm) || nonce.Length != EnvelopeV2Format.NonceSize
            || tag.Length != EnvelopeV2Format.TagSize || ciphertext.Length > EnvelopeV2Format.MaximumFieldCiphertextBytes)
        {
            throw EnvelopeV2Format.Malformed();
        }

        Header = header;
        Identity = identity;
        this.wrappedSeed = wrappedSeed.ToArray();
        this.nonce = nonce.ToArray();
        this.tag = tag.ToArray();
        this.ciphertext = ciphertext.ToArray();
    }

    public EnvelopeV2Header Header { get; }

    public FieldItemIdentity Identity { get; }

    public ReadOnlySpan<byte> WrappedSeed => wrappedSeed;

    public ReadOnlySpan<byte> Nonce => nonce;

    public ReadOnlySpan<byte> Tag => tag;

    public ReadOnlySpan<byte> Ciphertext => ciphertext;

    public byte[] AssociatedData() => new EnvelopeV2Writer().Bytes(Header.Encode()).Bytes(Identity.Encode()).ToArray();

    public byte[] ToPortableBytes() =>
        new EnvelopeV2Writer().Bytes(AssociatedData()).U16((ushort)wrappedSeed.Length).Bytes(wrappedSeed).Bytes(nonce).Bytes(tag).U32((uint)ciphertext.Length).Bytes(ciphertext).ToArray();

    /// <summary>Strict parse of the portable form: every length, code and identifier is checked, and trailing bytes are refused.</summary>
    public static FieldEnvelopeV2 ParsePortable(ReadOnlySpan<byte> portable)
    {
        var reader = new EnvelopeV2Reader(portable);
        var header = EnvelopeV2Header.Read(ref reader);
        if (header.Prefix.Kind is not (EnvelopeV2PayloadKind.FieldValueItem or EnvelopeV2PayloadKind.SealedKey))
        {
            throw EnvelopeV2Format.Unsupported();
        }

        var identity = FieldItemIdentity.Read(ref reader, header.Prefix.Kind);
        var wrapped = reader.Take(reader.U16());
        var nonce = reader.Take(EnvelopeV2Format.NonceSize);
        var tag = reader.Take(EnvelopeV2Format.TagSize);
        var length = reader.U32();
        if (length > EnvelopeV2Format.MaximumFieldCiphertextBytes)
        {
            throw EnvelopeV2Format.Malformed();
        }

        var ciphertext = reader.Take((int)length);
        reader.End();
        return new FieldEnvelopeV2(header, identity, wrapped, nonce, tag, ciphertext);
    }
}

/// <summary>
/// One seal batch (E3-D3): a single random 256-bit seed, wrapped once by the organization's key provider under the scope's current
/// version, seals items of exactly one record with HKDF-derived single-use item keys. The batch is bound to its organization,
/// record and purpose at creation; an item may be sealed once per batch; each item gets a fresh random 96-bit nonce. The seed is
/// zeroed as soon as the key schedule exists, and the schedule on <see cref="Dispose"/>. A batch must never be reused across
/// staging attempts: every attempt creates a new batch (fresh seed and data key identifier). <see cref="Seal(Guid, Guid, string, string)"/>
/// and <see cref="Dispose"/> are serialized: concurrent seals cannot both seal one item, and a dispose can never zero the key
/// schedule while a seal is deriving from it (which would encrypt under a key derived from an all-zero PRK).
/// </summary>
internal sealed class FieldSealBatchV2 : IDisposable
{
    private readonly Lock gate = new();
    private readonly EnvelopeV2KeySchedule schedule;
    private readonly byte[] h0;
    private readonly byte[] wrappedSeed;
    private readonly HashSet<Guid> sealedItems = [];
    private readonly HashSet<string> usedNonces = new(StringComparer.Ordinal);
    private bool disposed;

    private FieldSealBatchV2(Guid organizationId, Guid formRecordId, EncryptionKeyPurpose purpose, ReadOnlySpan<byte> seed, Guid dataKeyIdentifier, WrappedDataKey wrapped)
    {
        ArgumentNullException.ThrowIfNull(wrapped);
        if (formRecordId == Guid.Empty)
        {
            throw new ArgumentException("a seal batch belongs to one record", nameof(formRecordId));
        }

        var kind = EnvelopeV2Format.FieldKindOf(purpose);

        // The provider's reference must name exactly this organization and purpose in a v2-capable family (a legacy, foreign or
        // other-scope reference is refused here, before anything is sealed).
        var wrap = EnvelopeV2Format.WrapAlgorithmOf(wrapped.KeyReference);
        var prefix = EnvelopeV2HeaderPrefix.Create(kind, wrap, organizationId, wrapped.KeyReference, dataKeyIdentifier);
        if (wrapped.WrappedKeyBytes is null || wrapped.WrappedKeyBytes.Length != EnvelopeV2Format.WrappedSeedLengthOf(wrap))
        {
            throw EnvelopeV2Format.Malformed();
        }

        FormRecordId = formRecordId;
        wrappedSeed = wrapped.WrappedKeyBytes.ToArray();
        h0 = prefix.Encode();
        schedule = new EnvelopeV2KeySchedule(seed);
        Header = new EnvelopeV2Header(prefix, schedule.Commitment(h0));
    }

    public Guid FormRecordId { get; }

    public EnvelopeV2Header Header { get; }

    /// <summary>Creates a batch: generates the seed and data key identifier and wraps the seed with the organization's key of <paramref name="purpose"/>.</summary>
    public static async Task<FieldSealBatchV2> CreateAsync(IEncryptionKeyProvider keys, Guid organizationId, Guid formRecordId, EncryptionKeyPurpose purpose, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        _ = EnvelopeV2Format.FieldKindOf(purpose);
        if (formRecordId == Guid.Empty || organizationId == Guid.Empty)
        {
            // Refused before the key provider is asked to wrap anything.
            throw new ArgumentException("a seal batch belongs to one organization and one record");
        }

        var seed = RandomNumberGenerator.GetBytes(EnvelopeV2Format.SeedSize);
        try
        {
            var wrapped = await keys.WrapDataKeyAsync(new DataKeyScope(organizationId, purpose), seed, cancellationToken).ConfigureAwait(false);
            return new FieldSealBatchV2(organizationId, formRecordId, purpose, seed, Guid.CreateVersion7(), wrapped);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    /// <summary>Deterministic core for the known-answer vectors only (tests reach it through InternalsVisibleTo). Never for production seeds.</summary>
    internal static FieldSealBatchV2 CreateDeterministic(Guid organizationId, Guid formRecordId, EncryptionKeyPurpose purpose, ReadOnlySpan<byte> seed, Guid dataKeyIdentifier, WrappedDataKey wrapped) =>
        new(organizationId, formRecordId, purpose, seed, dataKeyIdentifier, wrapped);

    /// <summary>Seals one item's canonical plaintext with a fresh random nonce.</summary>
    public FieldEnvelopeV2 Seal(Guid fieldDefinitionId, Guid fieldValueItemId, string logicalType, string canonicalPlaintext) =>
        Seal(fieldDefinitionId, fieldValueItemId, logicalType, canonicalPlaintext, RandomNumberGenerator.GetBytes(EnvelopeV2Format.NonceSize));

    /// <summary>Deterministic core (the known-answer vectors supply the nonce). A nonce is never accepted twice in one batch.</summary>
    internal FieldEnvelopeV2 Seal(Guid fieldDefinitionId, Guid fieldValueItemId, string logicalType, string canonicalPlaintext, byte[] nonce)
    {
        lock (gate)
        {
            return SealLocked(fieldDefinitionId, fieldValueItemId, logicalType, canonicalPlaintext, nonce);
        }
    }

    private FieldEnvelopeV2 SealLocked(Guid fieldDefinitionId, Guid fieldValueItemId, string logicalType, string canonicalPlaintext, byte[] nonce)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(canonicalPlaintext);
        ArgumentNullException.ThrowIfNull(nonce);
        var identity = new FieldItemIdentity(FormRecordId, fieldDefinitionId, fieldValueItemId, logicalType);
        identity.Validate(Header.Prefix.Kind);
        if (nonce.Length != EnvelopeV2Format.NonceSize)
        {
            throw new ArgumentException("a 96-bit nonce is required", nameof(nonce));
        }

        if (sealedItems.Contains(fieldValueItemId))
        {
            throw new InvalidOperationException("an item is sealed at most once per batch");
        }

        if (!usedNonces.Add(Convert.ToHexStringLower(nonce)))
        {
            throw new InvalidOperationException("a nonce is used at most once per batch");
        }

        sealedItems.Add(fieldValueItemId);
        var context = identity.Encode();
        var key = schedule.FieldItemKey(h0, context);
        var plaintext = EnvelopeV2Format.StrictUtf8.GetBytes(canonicalPlaintext);
        try
        {
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[EnvelopeV2Format.TagSize];
            var aad = new EnvelopeV2Writer().Bytes(Header.Encode()).Bytes(context).ToArray();
            using (var aes = new AesGcm(key, EnvelopeV2Format.TagSize))
            {
                aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);
            }

            return new FieldEnvelopeV2(Header, identity, wrappedSeed, nonce, tag, ciphertext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (!disposed)
            {
                schedule.Dispose();
                disposed = true;
            }
        }
    }
}

/// <summary>One envelope to open and the identity the caller expects it to carry (in E3-M2: the identity of the verified row).</summary>
internal sealed record FieldOpenRequestV2(FieldEnvelopeV2 Envelope, FieldItemIdentity Expected);

/// <summary>
/// Opens field envelopes v2 (§3.9 reader rules). Everything that needs no key is checked first, for every request, before any key
/// operation: kind and purpose, organization, the expected item identity, and the consistency of each data-key group (identical
/// header and wrapped seed, no repeated item or nonce). Then each group's seed is unwrapped once, its commitment compared in constant
/// time, and each item decrypted with its derived key. The first failure aborts the whole call (fail closed); seeds, the key schedule,
/// item keys and plaintext bytes are zeroed on every path. Key-provider exceptions (unavailable, foreign scope) propagate unchanged.
/// </summary>
internal static class FieldEnvelopeV2Opener
{
    public static async Task<IReadOnlyList<string>> OpenAsync(IEncryptionKeyProvider keys, Guid organizationId, EncryptionKeyPurpose purpose, IReadOnlyList<FieldOpenRequestV2> requests, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(requests);
        var kind = EnvelopeV2Format.FieldKindOf(purpose);
        foreach (var request in requests)
        {
            ArgumentNullException.ThrowIfNull(request);
            var envelope = request.Envelope;
            if (envelope.Header.Prefix.Kind != kind || envelope.Header.Prefix.OrganizationId != organizationId || envelope.Identity != request.Expected)
            {
                throw new EnvelopeV2Exception(EnvelopeV2FailureReason.IdentityMismatch);
            }
        }

        var groups = requests.Select((request, index) => (request.Envelope, Index: index)).GroupBy(e => e.Envelope.Header.Prefix.DataKeyIdentifier).ToList();
        foreach (var group in groups)
        {
            var first = group.First().Envelope;
            var items = new HashSet<Guid>();
            var nonces = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (envelope, _) in group)
            {
                // A seal batch belongs to exactly one record (E3-D3): H0 carries no record id, so a data-key group spanning two
                // records can only come from a writer that broke the batch rule, and is refused before any unwrap.
                if (!envelope.Header.SameBytesAs(first.Header) || !envelope.WrappedSeed.SequenceEqual(first.WrappedSeed)
                    || envelope.Identity.FormRecordId != first.Identity.FormRecordId
                    || !items.Add(envelope.Identity.FieldValueItemId) || !nonces.Add(Convert.ToHexStringLower(envelope.Nonce)))
                {
                    throw new EnvelopeV2Exception(EnvelopeV2FailureReason.InconsistentDataKeyGroup);
                }
            }
        }

        var results = new string[requests.Count];
        foreach (var group in groups)
        {
            var first = group.First().Envelope;
            var seed = await keys.UnwrapDataKeyAsync(new DataKeyScope(organizationId, purpose), first.Header.Prefix.KekReference, first.WrappedSeed.ToArray(), cancellationToken).ConfigureAwait(false);
            try
            {
                using var schedule = new EnvelopeV2KeySchedule(seed);
                var h0 = first.Header.Prefix.Encode();
                if (!schedule.Commits(h0, first.Header.KeyCommitment))
                {
                    throw new EnvelopeV2Exception(EnvelopeV2FailureReason.KeyCommitmentMismatch);
                }

                foreach (var (envelope, index) in group)
                {
                    results[index] = Decrypt(schedule, h0, envelope);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(seed);
            }
        }

        return results;
    }

    private static string Decrypt(EnvelopeV2KeySchedule schedule, byte[] h0, FieldEnvelopeV2 envelope)
    {
        var key = schedule.FieldItemKey(h0, envelope.Identity.Encode());
        var plaintext = new byte[envelope.Ciphertext.Length];
        try
        {
            using (var aes = new AesGcm(key, EnvelopeV2Format.TagSize))
            {
                try
                {
                    aes.Decrypt(envelope.Nonce, envelope.Ciphertext, envelope.Tag, plaintext, envelope.AssociatedData());
                }
                catch (AuthenticationTagMismatchException)
                {
                    throw new EnvelopeV2Exception(EnvelopeV2FailureReason.AuthenticationFailed);
                }
            }

            try
            {
                return EnvelopeV2Format.StrictUtf8.GetString(plaintext);
            }
            catch (DecoderFallbackException)
            {
                // Authenticated bytes that are not UTF-8 can only come from a faulty writer: refuse rather than repair.
                throw EnvelopeV2Format.Malformed();
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}
