using System.Buffers.Binary;
using System.Security.Cryptography;
using EnterpriseForms.Application.Features.Documents.Ports;
using Microsoft.Extensions.Options;

namespace EnterpriseForms.Infrastructure.Features.Documents;

public sealed class EncryptionOptions
{
    public const string SectionName = "Encryption";

    /// <summary>"LocalDevelopment" (KEK from configuration, Development/Testing only) or "Unconfigured" (fail closed). Azure Key Vault/Managed HSM providers are not implemented in Stage B.</summary>
    public string KeyProvider { get; set; } = "Unconfigured";

    public string? LocalDevelopmentKekBase64 { get; set; }

    public string LocalDevelopmentKeyReference { get; set; } = "local-dev-kek-v1";
}

/// <summary>
/// Segmented AES-256-GCM (CLAUDE.md §4C.3/§4C.4): a random 256-bit DEK per document version, wrapped by the KEK;
/// each 1 MiB plaintext segment is sealed with nonce = prefix(8 random bytes) ‖ counter(4, big-endian) and AAD =
/// envelope version ‖ tenant ‖ record ‖ attachment ‖ version ‖ segment index ‖ final-segment flag. Ciphertext layout:
/// repeated [UInt32BE plaintext length][ciphertext][16-byte tag]. Truncation, reordering and transplanting are detected.
/// </summary>
public sealed class AesGcmFileEncryptionService(IEncryptionKeyProvider keys) : IFileEncryptionService
{
    public const string AlgorithmName = "AES-256-GCM";
    public const int EnvelopeVersion = 1;
    public const int SegmentSize = 1024 * 1024;
    private const int TagSize = 16;

    public async Task<EncryptionOutcome> EncryptAsync(Stream plaintext, Stream ciphertextSink, FileEncryptionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(ciphertextSink);
        ArgumentNullException.ThrowIfNull(context);
        if (!keys.IsConfigured)
        {
            throw new EncryptionUnavailableException("no encryption key provider is configured");
        }

        var dek = RandomNumberGenerator.GetBytes(32);
        var noncePrefix = RandomNumberGenerator.GetBytes(8);
        var keyReference = keys.CurrentKeyReference;
        var wrapped = keys.WrapDataKey(dek, keyReference);
        using var aes = new AesGcm(dek, TagSize);
        using var plainHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var cipherHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[SegmentSize];
        var nonce = new byte[12];
        noncePrefix.CopyTo(nonce, 0);
        long plainTotal = 0, cipherTotal = 0;
        uint segment = 0;
        var pending = await ReadFullAsync(plaintext, buffer, cancellationToken).ConfigureAwait(false);
        while (true)
        {
            var next = pending == SegmentSize ? await ReadFullAsync(plaintext, buffer.AsMemory(0, 0), cancellationToken).ConfigureAwait(false) : 0;
            _ = next;
            var lookahead = new byte[SegmentSize];
            var nextLength = pending == SegmentSize ? await ReadFullAsync(plaintext, lookahead, cancellationToken).ConfigureAwait(false) : 0;
            var isFinal = nextLength == 0;
            BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(8), segment);
            var cipher = new byte[pending];
            var tag = new byte[TagSize];
            aes.Encrypt(nonce, buffer.AsSpan(0, pending), cipher, tag, Aad(context, segment, isFinal));
            var header = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(header, (uint)pending);
            await ciphertextSink.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await ciphertextSink.WriteAsync(cipher, cancellationToken).ConfigureAwait(false);
            await ciphertextSink.WriteAsync(tag, cancellationToken).ConfigureAwait(false);
            plainHash.AppendData(buffer, 0, pending);
            cipherHash.AppendData(header);
            cipherHash.AppendData(cipher);
            cipherHash.AppendData(tag);
            plainTotal += pending;
            cipherTotal += 4 + pending + TagSize;
            segment++;
            if (isFinal)
            {
                break;
            }

            lookahead.AsSpan(0, nextLength).CopyTo(buffer);
            pending = nextLength;
        }

        CryptographicOperations.ZeroMemory(dek);
        await ciphertextSink.FlushAsync(cancellationToken).ConfigureAwait(false);
        return new EncryptionOutcome(new FileEncryptionEnvelope(AlgorithmName, EnvelopeVersion, keyReference, wrapped, noncePrefix, SegmentSize), plainTotal, plainHash.GetHashAndReset(), cipherTotal, cipherHash.GetHashAndReset());
    }

    public async Task DecryptAsync(Stream ciphertext, Stream plaintextSink, FileEncryptionEnvelope envelope, FileEncryptionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        ArgumentNullException.ThrowIfNull(plaintextSink);
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(context);
        if (envelope.AlgorithmName != AlgorithmName || envelope.EnvelopeVersion != EnvelopeVersion || envelope.NoncePrefix.Length != 8 || envelope.SegmentSizeBytes != SegmentSize)
        {
            throw new CryptographicException("unsupported encryption envelope");
        }

        if (!keys.IsConfigured)
        {
            throw new EncryptionUnavailableException("no encryption key provider is configured");
        }

        var dek = keys.UnwrapDataKey(envelope.WrappedDataKey, envelope.KeyReference);
        try
        {
            using var aes = new AesGcm(dek, TagSize);
            var nonce = new byte[12];
            envelope.NoncePrefix.CopyTo(nonce, 0);
            var header = new byte[4];
            uint segment = 0;
            var finished = false;
            while (!finished)
            {
                var headerRead = await ReadFullAsync(ciphertext, header, cancellationToken).ConfigureAwait(false);
                if (headerRead != 4)
                {
                    throw new CryptographicException("ciphertext truncated (missing segment header)");
                }

                var length = (int)BinaryPrimitives.ReadUInt32BigEndian(header);
                if (length > SegmentSize)
                {
                    throw new CryptographicException("ciphertext segment too large");
                }

                var cipher = new byte[length];
                var tag = new byte[TagSize];
                if (await ReadFullAsync(ciphertext, cipher, cancellationToken).ConfigureAwait(false) != length || await ReadFullAsync(ciphertext, tag, cancellationToken).ConfigureAwait(false) != TagSize)
                {
                    throw new CryptographicException("ciphertext truncated");
                }

                // Final-segment detection: the next header is absent exactly for the last segment.
                var probe = new byte[4];
                var probed = await ReadFullAsync(ciphertext, probe, cancellationToken).ConfigureAwait(false);
                finished = probed == 0;
                if (!finished && probed != 4)
                {
                    throw new CryptographicException("ciphertext truncated (partial header)");
                }

                BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(8), segment);
                var plain = new byte[length];
                aes.Decrypt(nonce, cipher, tag, plain, Aad(context, segment, finished));
                await plaintextSink.WriteAsync(plain, cancellationToken).ConfigureAwait(false);
                segment++;
                if (!finished)
                {
                    probe.CopyTo(header, 0);
                    // Re-use the probed header on the next iteration by pushing it back through a small wrapper stream.
                    ciphertext = new PrefixedStream(probe, ciphertext);
                }
            }

            await plaintextSink.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    private static byte[] Aad(FileEncryptionContext c, uint segment, bool isFinal)
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

    /// <summary>Read-only stream that yields a buffered prefix before the underlying stream (used to un-read a probed header).</summary>
    private sealed class PrefixedStream(byte[] prefix, Stream inner) : Stream
    {
        private int offset;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (offset < prefix.Length)
            {
                var n = Math.Min(buffer.Length, prefix.Length - offset);
                prefix.AsSpan(offset, n).CopyTo(buffer);
                offset += n;
                return n;
            }

            return inner.Read(buffer);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (offset < prefix.Length)
            {
                var n = Math.Min(buffer.Length, prefix.Length - offset);
                prefix.AsMemory(offset, n).CopyTo(buffer);
                offset += n;
                return n;
            }

            return await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>Development/Testing KEK from configuration (32 bytes). Refused in Staging/Production by the startup guard; AES-GCM key wrap with a random nonce.</summary>
public sealed class LocalDevelopmentKeyProvider : IEncryptionKeyProvider
{
    private readonly byte[]? kek;
    private readonly string reference;

    public LocalDevelopmentKeyProvider(IOptions<EncryptionOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var value = options.Value;
        reference = value.LocalDevelopmentKeyReference;
        if (!string.IsNullOrWhiteSpace(value.LocalDevelopmentKekBase64))
        {
            var bytes = Convert.FromBase64String(value.LocalDevelopmentKekBase64);
            kek = bytes.Length == 32 ? bytes : null;
        }
    }

    public string ProviderName => "LocalDevelopment";

    public bool IsConfigured => kek is not null;

    public string CurrentKeyReference => reference;

    public byte[] WrapDataKey(ReadOnlySpan<byte> dataKey, string keyReference)
    {
        RequireKey(keyReference);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[dataKey.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(kek!, 16);
        aes.Encrypt(nonce, dataKey, cipher, tag, System.Text.Encoding.ASCII.GetBytes("DynamicForms/DekWrap/v1:" + keyReference));
        return [.. nonce, .. cipher, .. tag];
    }

    public byte[] UnwrapDataKey(ReadOnlySpan<byte> wrappedDataKey, string keyReference)
    {
        RequireKey(keyReference);
        if (wrappedDataKey.Length < 12 + 16 + 1)
        {
            throw new CryptographicException("wrapped key too short");
        }

        var nonce = wrappedDataKey[..12];
        var tag = wrappedDataKey[^16..];
        var cipher = wrappedDataKey[12..^16];
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(kek!, 16);
        aes.Decrypt(nonce, cipher, tag, plain, System.Text.Encoding.ASCII.GetBytes("DynamicForms/DekWrap/v1:" + keyReference));
        return plain;
    }

    private void RequireKey(string keyReference)
    {
        if (kek is null)
        {
            throw new EncryptionUnavailableException("local development KEK is not configured");
        }

        if (keyReference != reference)
        {
            throw new EncryptionUnavailableException("unknown key reference " + keyReference);
        }
    }
}

/// <summary>Registered when no permitted key provider is configured: encryption and decryption fail closed.</summary>
public sealed class UnavailableKeyProvider(string reason) : IEncryptionKeyProvider
{
    public string ProviderName => "Unavailable";

    public bool IsConfigured => false;

    public string CurrentKeyReference => string.Empty;

    public byte[] WrapDataKey(ReadOnlySpan<byte> dataKey, string keyReference) => throw new EncryptionUnavailableException(reason);

    public byte[] UnwrapDataKey(ReadOnlySpan<byte> wrappedDataKey, string keyReference) => throw new EncryptionUnavailableException(reason);
}
