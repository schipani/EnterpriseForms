using System.Buffers.Binary;
using System.Security.Cryptography;
using EnterpriseForms.Application.Features.Documents.Ports;

namespace EnterpriseForms.Infrastructure.Features.Documents.EnvelopeV2;

/// <summary>
/// The Envelope v2 metadata of one document version (kind DocumentVersion). Portable header (§3.10, documents):
/// <code>H ‖ C_doc ‖ u16 len ‖ wrapped seed</code>
/// The segment stream is stored separately with the v1 framing: repeated [UInt32BE plaintext length][ciphertext][16-byte tag].
/// </summary>
internal sealed class DocumentEnvelopeV2
{
    private readonly byte[] wrappedSeed;

    public DocumentEnvelopeV2(EnvelopeV2Header header, DocumentContext context, ReadOnlySpan<byte> wrappedSeed)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(context);
        if (header.Prefix.Kind != EnvelopeV2PayloadKind.DocumentVersion)
        {
            throw EnvelopeV2Format.Unsupported();
        }

        if (wrappedSeed.Length != EnvelopeV2Format.WrappedSeedLengthOf(header.Prefix.WrapAlgorithm))
        {
            throw EnvelopeV2Format.Malformed();
        }

        Header = header;
        Context = context;
        this.wrappedSeed = wrappedSeed.ToArray();
    }

    public EnvelopeV2Header Header { get; }

    public DocumentContext Context { get; }

    public ReadOnlySpan<byte> WrappedSeed => wrappedSeed;

    public byte[] ToPortableHeaderBytes() =>
        new EnvelopeV2Writer().Bytes(Header.Encode()).Bytes(Context.Encode()).U16((ushort)wrappedSeed.Length).Bytes(wrappedSeed).ToArray();

    public static DocumentEnvelopeV2 ParsePortableHeader(ReadOnlySpan<byte> portable)
    {
        var reader = new EnvelopeV2Reader(portable);
        var header = EnvelopeV2Header.Read(ref reader);
        if (header.Prefix.Kind != EnvelopeV2PayloadKind.DocumentVersion)
        {
            throw EnvelopeV2Format.Unsupported();
        }

        var context = DocumentContext.Read(ref reader);
        var wrapped = reader.Take(reader.U16());
        reader.End();
        return new DocumentEnvelopeV2(header, context, wrapped);
    }
}

internal sealed record DocumentEncryptionV2Outcome(DocumentEnvelopeV2 Envelope, long PlaintextSizeBytes, byte[] PlaintextSha256, long CiphertextSizeBytes, byte[] CiphertextSha256);

/// <summary>
/// Segmented AES-256-GCM under Envelope v2: one seed per document version, wrapped by the organization's Document key; the
/// document key is HKDF-derived from the seed, H0 and C_doc (which includes the random 8-byte nonce prefix), so it is single-use.
/// Nonce = prefix(8) ‖ UInt32BE(segment); segment AAD = H ‖ C_doc ‖ UInt32BE(segment) ‖ final flag. The framing, final-segment
/// detection, bounds and the E0-2 constant-depth reader are those of v1. The key commitment is checked before any segment is
/// decrypted.
/// <para>
/// <b>Decryption caller contract (fail closed; design §13.9).</b> Decryption streams: each segment is authenticated before it is
/// written, so a failure at a later segment (truncation, tamper, reordering) throws after earlier segments were written, as in v1.
/// (1) The <b>only</b> success signal is normal completion of <see cref="DecryptAsync"/>; until then the sink holds provisional
/// output. (2) Provisional output is always whole authenticated segments, an exact prefix of the plaintext at segment boundaries;
/// no byte of an unauthenticated segment is ever written, and the sink is flushed only on success. (3) Identity, key, commitment
/// and first-frame failures write nothing. (4) On any exception the caller must discard the sink: a server-side sink (export,
/// scan, preview, reseal) must be a non-published staging location, published only after completion and deleted on failure; an
/// HTTP response must be aborted, never completed or retried onto the same response, and must carry the full Content-Length so the
/// client sees the truncation. Bytes already transmitted to a client cannot be recalled.
/// </para>
/// </summary>
internal static class DocumentEnvelopeV2Cipher
{
    private const int HeaderSize = 4;

    public static async Task<DocumentEncryptionV2Outcome> EncryptAsync(Stream plaintext, Stream ciphertextSink, IEncryptionKeyProvider keys, Guid organizationId, DocumentVersionIdentity identity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var seed = RandomNumberGenerator.GetBytes(EnvelopeV2Format.SeedSize);
        try
        {
            var wrapped = await keys.WrapDataKeyAsync(new DataKeyScope(organizationId, EncryptionKeyPurpose.Document), seed, cancellationToken).ConfigureAwait(false);
            return await EncryptAsync(plaintext, ciphertextSink, organizationId, identity, seed, Guid.CreateVersion7(), RandomNumberGenerator.GetBytes(EnvelopeV2Format.NoncePrefixSize), wrapped, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    /// <summary>Deterministic core (known-answer vectors only, through InternalsVisibleTo). Does not take ownership of <paramref name="seed"/>.</summary>
    internal static async Task<DocumentEncryptionV2Outcome> EncryptAsync(Stream plaintext, Stream ciphertextSink, Guid organizationId, DocumentVersionIdentity identity, byte[] seed, Guid dataKeyIdentifier, byte[] noncePrefix, WrappedDataKey wrapped, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(ciphertextSink);
        ArgumentNullException.ThrowIfNull(seed);
        ArgumentNullException.ThrowIfNull(noncePrefix);
        ArgumentNullException.ThrowIfNull(wrapped);
        var wrap = EnvelopeV2Format.WrapAlgorithmOf(wrapped.KeyReference);
        var prefix = EnvelopeV2HeaderPrefix.Create(EnvelopeV2PayloadKind.DocumentVersion, wrap, organizationId, wrapped.KeyReference, dataKeyIdentifier);
        var context = new DocumentContext(identity, EnvelopeV2Format.SegmentSize, noncePrefix);
        var h0 = prefix.Encode();
        var contextBytes = context.Encode();
        byte[] documentKey;
        EnvelopeV2Header header;
        using (var schedule = new EnvelopeV2KeySchedule(seed))
        {
            header = new EnvelopeV2Header(prefix, schedule.Commitment(h0));
            documentKey = schedule.DocumentKey(h0, contextBytes);
        }

        var envelope = new DocumentEnvelopeV2(header, context, wrapped.WrappedKeyBytes);
        var buffer = new byte[EnvelopeV2Format.SegmentSize];
        var lookahead = new byte[EnvelopeV2Format.SegmentSize];
        try
        {
            using var aes = new AesGcm(documentKey, EnvelopeV2Format.TagSize);
            using var plainHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var cipherHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var aad = SegmentAadPrefix(header, contextBytes);
            var nonce = new byte[EnvelopeV2Format.NonceSize];
            noncePrefix.CopyTo(nonce, 0);
            long plainTotal = 0, cipherTotal = 0;
            uint segment = 0;
            var pending = await ReadFullAsync(plaintext, buffer, cancellationToken).ConfigureAwait(false);
            while (true)
            {
                // Only a full segment can have a successor; a short one is final by construction.
                var nextLength = pending == EnvelopeV2Format.SegmentSize ? await ReadFullAsync(plaintext, lookahead, cancellationToken).ConfigureAwait(false) : 0;
                var isFinal = nextLength == 0;
                BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(8), segment);
                SetSegment(aad, segment, isFinal);
                var cipher = new byte[pending];
                var tag = new byte[EnvelopeV2Format.TagSize];
                aes.Encrypt(nonce, buffer.AsSpan(0, pending), cipher, tag, aad);
                var frame = new byte[HeaderSize];
                BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)pending);
                await ciphertextSink.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
                await ciphertextSink.WriteAsync(cipher, cancellationToken).ConfigureAwait(false);
                await ciphertextSink.WriteAsync(tag, cancellationToken).ConfigureAwait(false);
                plainHash.AppendData(buffer, 0, pending);
                cipherHash.AppendData(frame);
                cipherHash.AppendData(cipher);
                cipherHash.AppendData(tag);
                plainTotal += pending;
                cipherTotal += HeaderSize + pending + EnvelopeV2Format.TagSize;
                segment++;
                if (isFinal)
                {
                    break;
                }

                (buffer, lookahead) = (lookahead, buffer);
                pending = nextLength;
            }

            await ciphertextSink.FlushAsync(cancellationToken).ConfigureAwait(false);
            return new DocumentEncryptionV2Outcome(envelope, plainTotal, plainHash.GetHashAndReset(), cipherTotal, cipherHash.GetHashAndReset());
        }
        finally
        {
            // Plaintext read buffers and the document key are zeroed on every exit, including a failing source or sink.
            CryptographicOperations.ZeroMemory(buffer);
            CryptographicOperations.ZeroMemory(lookahead);
            CryptographicOperations.ZeroMemory(documentKey);
        }
    }

    /// <summary>
    /// Decrypts a version after checking that the envelope carries exactly <paramref name="organizationId"/> and <paramref name="expected"/>,
    /// unwrapping the seed with the referenced version and verifying the key commitment.
    /// </summary>
    public static async Task DecryptAsync(Stream ciphertext, Stream plaintextSink, DocumentEnvelopeV2 envelope, Guid organizationId, DocumentVersionIdentity expected, IEncryptionKeyProvider keys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        ArgumentNullException.ThrowIfNull(plaintextSink);
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(keys);
        if (envelope.Header.Prefix.OrganizationId != organizationId || envelope.Context.Identity != expected)
        {
            throw new EnvelopeV2Exception(EnvelopeV2FailureReason.IdentityMismatch);
        }

        var h0 = envelope.Header.Prefix.Encode();
        var contextBytes = envelope.Context.Encode();
        byte[] documentKey;
        var seed = await keys.UnwrapDataKeyAsync(new DataKeyScope(organizationId, EncryptionKeyPurpose.Document), envelope.Header.Prefix.KekReference, envelope.WrappedSeed.ToArray(), cancellationToken).ConfigureAwait(false);
        try
        {
            using var schedule = new EnvelopeV2KeySchedule(seed);
            if (!schedule.Commits(h0, envelope.Header.KeyCommitment))
            {
                throw new EnvelopeV2Exception(EnvelopeV2FailureReason.KeyCommitmentMismatch);
            }

            documentKey = schedule.DocumentKey(h0, contextBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }

        try
        {
            using var aes = new AesGcm(documentKey, EnvelopeV2Format.TagSize);
            var aad = SegmentAadPrefix(envelope.Header, contextBytes);
            var nonce = new byte[EnvelopeV2Format.NonceSize];
            envelope.Context.NoncePrefix.CopyTo(nonce);
            var tag = new byte[EnvelopeV2Format.TagSize];
            var frame = new byte[HeaderSize];
            if (await ReadFullAsync(ciphertext, frame, cancellationToken).ConfigureAwait(false) != HeaderSize)
            {
                throw EnvelopeV2Format.Malformed();
            }

            uint segment = 0;
            var finished = false;
            while (!finished)
            {
                var declared = BinaryPrimitives.ReadUInt32BigEndian(frame);
                if (declared > EnvelopeV2Format.SegmentSize)
                {
                    throw EnvelopeV2Format.Malformed();
                }

                var length = (int)declared;
                var cipher = new byte[length];
                if (await ReadFullAsync(ciphertext, cipher, cancellationToken).ConfigureAwait(false) != length || await ReadFullAsync(ciphertext, tag, cancellationToken).ConfigureAwait(false) != EnvelopeV2Format.TagSize)
                {
                    throw EnvelopeV2Format.Malformed();
                }

                // The next frame header is absent exactly for the last segment (E0-2: carried into the next iteration, no wrapper streams).
                var probed = await ReadFullAsync(ciphertext, frame, cancellationToken).ConfigureAwait(false);
                finished = probed == 0;
                if (!finished && probed != HeaderSize)
                {
                    throw EnvelopeV2Format.Malformed();
                }

                BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(8), segment);
                SetSegment(aad, segment, finished);
                var plain = new byte[length];
                try
                {
                    try
                    {
                        aes.Decrypt(nonce, cipher, tag, plain, aad);
                    }
                    catch (AuthenticationTagMismatchException)
                    {
                        // Contract (2): nothing of this segment reaches the sink.
                        throw new EnvelopeV2Exception(EnvelopeV2FailureReason.AuthenticationFailed);
                    }

                    await plaintextSink.WriteAsync(plain, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plain);
                }

                segment++;
            }

            await plaintextSink.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(documentKey);
        }
    }

    /// <summary>H ‖ C_doc followed by five bytes (UInt32BE segment ‖ final flag) that <see cref="SetSegment"/> rewrites per segment.</summary>
    internal static byte[] SegmentAadPrefix(EnvelopeV2Header header, byte[] contextBytes) => new EnvelopeV2Writer().Bytes(header.Encode()).Bytes(contextBytes).U32(0).U8(0).ToArray();

    private static void SetSegment(byte[] aad, uint segment, bool isFinal)
    {
        BinaryPrimitives.WriteUInt32BigEndian(aad.AsSpan(aad.Length - 5, 4), segment);
        aad[^1] = isFinal ? (byte)1 : (byte)0;
    }

    private static async Task<int> ReadFullAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
