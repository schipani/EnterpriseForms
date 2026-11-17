using System.Security.Cryptography;
using EnterpriseForms.Application.Features.Documents.Ports;
using EnterpriseForms.Infrastructure.Features.Documents;
using Microsoft.Extensions.Options;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// E3-M1 test double for <see cref="IEncryptionKeyProvider"/>. In mapped mode it unwraps only an exact (organization, purpose,
/// reference, wrapped bytes) entry it was given or wrapped itself, and refuses anything else as a foreign or tampered wrap
/// (<see cref="CryptographicException"/>), like the real providers. <see cref="Override"/> replaces the returned seed, to model a
/// provider without associated-data binding (any wrap of the scope opens) or one that returns the wrong key.
/// </summary>
internal sealed class EnvelopeV2KeyDouble(string family = "vault-transit", int version = 1) : IEncryptionKeyProvider
{
    private readonly Dictionary<string, byte[]> wraps = new(StringComparer.Ordinal);
    private int nextWrap;

    public string ProviderName => "E3M1TestDouble";

    public bool IsConfigured => true;

    public int WrapCalls { get; private set; }

    public int UnwrapCalls { get; private set; }

    /// <summary>When set, every unwrap returns this value (after the call is counted), whatever was wrapped.</summary>
    public Func<byte[]>? Override { get; set; }

    public bool Unavailable { get; set; }

    public static string Reference(string family, Guid organization, EncryptionKeyPurpose purpose, int version) => $"{family}:{organization:D}:{purpose.Token}:{version}";

    public void Map(Guid organization, EncryptionKeyPurpose purpose, string reference, byte[] wrapped, byte[] seed) =>
        wraps[Key(organization, purpose, reference, wrapped)] = seed.ToArray();

    public Task<WrappedDataKey> WrapDataKeyAsync(DataKeyScope scope, ReadOnlyMemory<byte> dataKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Unavailable)
        {
            throw new EncryptionUnavailableException("test double unavailable");
        }

        WrapCalls++;
        var wrapped = RandomNumberGenerator.GetBytes(60);
        BitConverter.GetBytes(++nextWrap).CopyTo(wrapped, 0);
        var reference = Reference(family, scope.OrganizationId, scope.Purpose, version);
        Map(scope.OrganizationId, scope.Purpose, reference, wrapped, dataKey.ToArray());
        return Task.FromResult(new WrappedDataKey(reference, wrapped));
    }

    public Task<byte[]> UnwrapDataKeyAsync(DataKeyScope scope, string keyReference, ReadOnlyMemory<byte> wrappedDataKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Unavailable)
        {
            throw new EncryptionUnavailableException("test double unavailable");
        }

        UnwrapCalls++;
        if (Override is { } replacement)
        {
            return Task.FromResult(replacement());
        }

        return wraps.TryGetValue(Key(scope.OrganizationId, scope.Purpose, keyReference, wrappedDataKey.ToArray()), out var seed)
            ? Task.FromResult(seed.ToArray())
            : throw new CryptographicException("test double: unknown or foreign wrap");
    }

    private static string Key(Guid organization, EncryptionKeyPurpose purpose, string reference, byte[] wrapped) =>
        $"{organization:D}|{purpose.Token}|{reference}|{Convert.ToHexStringLower(wrapped)}";
}

/// <summary>A plaintext sink that records the length of every write and counts flushes (the document caller-contract tests).</summary>
internal sealed class RecordingSink : MemoryStream
{
    public List<int> Writes { get; } = [];

    public int Flushes { get; private set; }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Writes.Add(buffer.Length);
        return base.WriteAsync(buffer, cancellationToken);
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        Flushes++;
        return base.FlushAsync(cancellationToken);
    }
}

internal static class EnvelopeV2TestSupport
{
    public static byte[] Seq(int start, int count) => Enumerable.Range(0, count).Select(i => (byte)((start + i) % 256)).ToArray();

    /// <summary>The real Development/Testing keyring (scoped <c>localdev:</c> references, organization- and purpose-bound wrap).</summary>
    public static LocalDevelopmentKeyProvider LocalKeyring(int kekStart = 0x00) =>
        new(Options.Create(new EncryptionOptions
        {
            KeyProvider = EncryptionOptions.LocalDevelopmentProviderName,
            LocalDevelopmentKekBase64 = Convert.ToBase64String(Seq(kekStart, 32)),
            LocalDevelopmentKeyReference = "local-dev-kek-v1",
        }));

    /// <summary>The tags of a v1/v2 segment stream ([UInt32BE len][ciphertext][16-byte tag] repeated).</summary>
    public static List<string> SegmentTags(byte[] stream)
    {
        var tags = new List<string>();
        var offset = 0;
        while (offset < stream.Length)
        {
            var length = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(stream.AsSpan(offset, 4));
            offset += 4 + length;
            tags.Add(Convert.ToHexStringLower(stream.AsSpan(offset, 16)));
            offset += 16;
        }

        return tags;
    }
}
