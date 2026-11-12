using System.Buffers.Binary;
using System.Security.Cryptography;
using EnterpriseForms.Application.Features.Documents.Ports;
using EnterpriseForms.Application.Features.FormRecords.Encryption;

namespace EnterpriseForms.Infrastructure.Features.Documents;

/// <summary>
/// Segmented AES-256-GCM (CLAUDE.md §4C.3/§4C.4): a random 256-bit DEK per document version, wrapped by the KEK;
/// each 1 MiB plaintext segment is sealed with nonce = prefix(8 random bytes) ‖ counter(4, big-endian) and AAD =
/// envelope version ‖ tenant ‖ record ‖ attachment ‖ version ‖ segment index ‖ final-segment flag. Ciphertext layout:
/// repeated [UInt32BE plaintext length][ciphertext][16-byte tag]. Truncation, reordering and transplanting are detected.
/// The v1 format is pinned by known-answer vectors (E0-6, <c>EncryptionKnownAnswerTests</c>). E-1: the DEK is wrapped under the
/// organization's Document key (organization- and purpose-scoped, versioned); the envelope stores the returned key reference.
/// </summary>
public sealed class AesGcmFileEncryptionService(IEncryptionKeyProvider keys) : IFileEncryptionService
{
    public const string AlgorithmName = "AES-256-GCM";
    public const int EnvelopeVersion = 1;
    public const int SegmentSize = 1024 * 1024;
    private const int TagSize = 16;
    private const int NoncePrefixSize = 8;
    private const int DataKeySize = 32;
    private const int HeaderSize = 4;

    public Task<EncryptionOutcome> EncryptAsync(Stream plaintext, Stream ciphertextSink, FileEncryptionContext context, CancellationToken cancellationToken) =>
        EncryptAsync(plaintext, ciphertextSink, context, RandomNumberGenerator.GetBytes(DataKeySize), RandomNumberGenerator.GetBytes(NoncePrefixSize), cancellationToken);

    /// <summary>
    /// Deterministic core (the public overload supplies a random DEK and nonce prefix; tests reach this one for the
    /// known-answer vectors through InternalsVisibleTo). Takes ownership of <paramref name="dataKey"/>: it is zeroed on every
    /// exit path, including failures of the key provider, the source or the sink (E0-3).
    /// </summary>
    internal async Task<EncryptionOutcome> EncryptAsync(Stream plaintext, Stream ciphertextSink, FileEncryptionContext context, byte[] dataKey, byte[] noncePrefix, CancellationToken cancellationToken)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(plaintext);
            ArgumentNullException.ThrowIfNull(ciphertextSink);
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(dataKey);
            ArgumentNullException.ThrowIfNull(noncePrefix);
            if (!keys.IsConfigured)
            {
                throw new EncryptionUnavailableException("no encryption key provider is configured");
            }

            if (dataKey.Length != DataKeySize || noncePrefix.Length != NoncePrefixSize)
            {
                throw new ArgumentException("a 256-bit data key and an 8-byte nonce prefix are required");
            }

            // E1-2/E1-3: the organization's current Document key wraps the DEK; the reference returned names that exact key version.
            var wrapped = await keys.WrapDataKeyAsync(new DataKeyScope(context.TenantId, EncryptionKeyPurpose.Document), dataKey, cancellationToken).ConfigureAwait(false);
            using var aes = new AesGcm(dataKey, TagSize);
            using var plainHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var cipherHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            // Two segment buffers, allocated once and swapped: the segment being sealed and the look-ahead read that tells
            // whether it is the final one. (E0-3: previously a new look-ahead buffer was allocated for every segment.)
            var buffer = new byte[SegmentSize];
            var lookahead = new byte[SegmentSize];
            var nonce = new byte[12];
            noncePrefix.CopyTo(nonce, 0);
            long plainTotal = 0, cipherTotal = 0;
            uint segment = 0;
            var pending = await ReadFullAsync(plaintext, buffer, cancellationToken).ConfigureAwait(false);
            while (true)
            {
                // Only a full segment can have a successor; a short one is final by construction.
                var nextLength = pending == SegmentSize ? await ReadFullAsync(plaintext, lookahead, cancellationToken).ConfigureAwait(false) : 0;
                var isFinal = nextLength == 0;
                BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(8), segment);
                var cipher = new byte[pending];
                var tag = new byte[TagSize];
                aes.Encrypt(nonce, buffer.AsSpan(0, pending), cipher, tag, Aad(context, segment, isFinal));
                var header = new byte[HeaderSize];
                BinaryPrimitives.WriteUInt32BigEndian(header, (uint)pending);
                await ciphertextSink.WriteAsync(header, cancellationToken).ConfigureAwait(false);
                await ciphertextSink.WriteAsync(cipher, cancellationToken).ConfigureAwait(false);
                await ciphertextSink.WriteAsync(tag, cancellationToken).ConfigureAwait(false);
                plainHash.AppendData(buffer, 0, pending);
                cipherHash.AppendData(header);
                cipherHash.AppendData(cipher);
                cipherHash.AppendData(tag);
                plainTotal += pending;
                cipherTotal += HeaderSize + pending + TagSize;
                segment++;
                if (isFinal)
                {
                    break;
                }

                (buffer, lookahead) = (lookahead, buffer);
                pending = nextLength;
            }

            await ciphertextSink.FlushAsync(cancellationToken).ConfigureAwait(false);
            return new EncryptionOutcome(new FileEncryptionEnvelope(AlgorithmName, EnvelopeVersion, wrapped.KeyReference, wrapped.WrappedKeyBytes, noncePrefix, SegmentSize), plainTotal, plainHash.GetHashAndReset(), cipherTotal, cipherHash.GetHashAndReset());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    public async Task DecryptAsync(Stream ciphertext, Stream plaintextSink, FileEncryptionEnvelope envelope, FileEncryptionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        ArgumentNullException.ThrowIfNull(plaintextSink);
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(context);
        var stage = DecryptionStage.Envelope;
        try
        {
            if (envelope.AlgorithmName != AlgorithmName || envelope.EnvelopeVersion != EnvelopeVersion || envelope.NoncePrefix.Length != NoncePrefixSize || envelope.SegmentSizeBytes != SegmentSize)
            {
                throw new CryptographicException("unsupported encryption envelope");
            }

            if (!keys.IsConfigured)
            {
                throw new EncryptionUnavailableException("no encryption key provider is configured");
            }

            stage = DecryptionStage.KeyUnwrap;
            var dek = await keys.UnwrapDataKeyAsync(new DataKeyScope(context.TenantId, EncryptionKeyPurpose.Document), envelope.KeyReference, envelope.WrappedDataKey, cancellationToken).ConfigureAwait(false);
            stage = DecryptionStage.Payload;
            try
            {
                using var aes = new AesGcm(dek, TagSize);
                var nonce = new byte[12];
                envelope.NoncePrefix.CopyTo(nonce, 0);
                var tag = new byte[TagSize];

                // Final-segment detection reads the next segment's header ahead. E0-2: that header is carried into the next
                // iteration in this buffer, so the source stream is read directly at every segment — no wrapper streams, and
                // the call depth stays constant however many segments the document has.
                var header = new byte[HeaderSize];
                if (await ReadFullAsync(ciphertext, header, cancellationToken).ConfigureAwait(false) != HeaderSize)
                {
                    throw new CryptographicException("ciphertext truncated (missing segment header)");
                }

                uint segment = 0;
                var finished = false;
                while (!finished)
                {
                    var declaredLength = BinaryPrimitives.ReadUInt32BigEndian(header);
                    if (declaredLength > SegmentSize)
                    {
                        throw new CryptographicException("ciphertext segment too large");
                    }

                    var length = (int)declaredLength;
                    var cipher = new byte[length];
                    if (await ReadFullAsync(ciphertext, cipher, cancellationToken).ConfigureAwait(false) != length || await ReadFullAsync(ciphertext, tag, cancellationToken).ConfigureAwait(false) != TagSize)
                    {
                        throw new CryptographicException("ciphertext truncated");
                    }

                    // The next header is absent exactly for the last segment.
                    var probed = await ReadFullAsync(ciphertext, header, cancellationToken).ConfigureAwait(false);
                    finished = probed == 0;
                    if (!finished && probed != HeaderSize)
                    {
                        throw new CryptographicException("ciphertext truncated (partial header)");
                    }

                    BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(8), segment);
                    var plain = new byte[length];
                    aes.Decrypt(nonce, cipher, tag, plain, Aad(context, segment, finished));
                    await plaintextSink.WriteAsync(plain, cancellationToken).ConfigureAwait(false);
                    segment++;
                }

                await plaintextSink.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(dek);
            }
        }
        catch (Exception ex) when (DecryptionFailure.ReasonFor(ex, stage) is { } reason)
        {
            // E0-4: identifiers and a reason only; the exception (type, message, fail-closed handling) is otherwise unchanged.
            DecryptionFailure.ForDocument(reason, context.TenantId, context.FormRecordId, context.FileAttachmentId, context.VersionNumber).AttachTo(ex);
            throw;
        }
    }

    /// <summary>The exact v1 segment AAD (internal for the known-answer vectors).</summary>
    internal static byte[] Aad(FileEncryptionContext c, uint segment, bool isFinal)
    {
        var aad = new byte[4 + 16 * 3 + 4 + 4 + 1];
        BinaryPrimitives.WriteInt32BigEndian(aad.AsSpan(0, 4), EnvelopeVersion);
        c.TenantId.TryWriteBytes(aad.AsSpan(4, 16), bigEndian: true, out _);
        c.FormRecordId.TryWriteBytes(aad.AsSpan(20, 16), bigEndian: true, out _);
        c.FileAttachmentId.TryWriteBytes(aad.AsSpan(36, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteInt32BigEndian(aad.AsSpan(52, 4), c.VersionNumber);
        BinaryPrimitives.WriteUInt32BigEndian(aad.AsSpan(56, 4), segment);
        aad[60] = isFinal ? (byte)1 : (byte)0;
        return aad;
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
