using System.Diagnostics;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using EnterpriseForms.Api.Composition;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.Audit;
using EnterpriseForms.Application.Features.Documents.Ports;
using EnterpriseForms.Application.Features.FormRecords.Encryption;
using EnterpriseForms.Domain.Features.Audit;
using EnterpriseForms.Domain.Features.FormRecords;
using EnterpriseForms.Infrastructure.Features.Audit;
using EnterpriseForms.Infrastructure.Features.Documents;
using EnterpriseForms.Infrastructure.Features.FormRecords;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// E0-1 … E0-4 regression tests: the key-provider environment policy shared by the Business API and the Worker (the Worker
/// host itself is covered in the architecture suite, which references it), the document decryption stream (constant call
/// depth, unchanged validation), encryption memory handling, and the decryption-failure security event.
/// </summary>
public sealed class EncryptionRemediationTests
{
    private const string KeyReference = "local-dev-kek-v1";

    private static LocalDevelopmentKeyProvider Keys(byte[]? kek = null) =>
        new(Options.Create(new EncryptionOptions { KeyProvider = EncryptionOptions.LocalDevelopmentProviderName, LocalDevelopmentKekBase64 = Convert.ToBase64String(kek ?? RandomNumberGenerator.GetBytes(32)), LocalDevelopmentKeyReference = KeyReference }));

    private static FileEncryptionContext DocumentContext() => new(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), 2);

    // ---------------------------------------------------------------- E0-1

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "EnterpriseForms.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static IConfiguration Configuration(string? keyProvider) =>
        new ConfigurationBuilder().AddInMemoryCollection(keyProvider is null ? [] : [new KeyValuePair<string, string?>("Encryption:KeyProvider", keyProvider)]).Build();

    [Theory]
    [InlineData("LocalDevelopment", "Development", true)]
    [InlineData("LocalDevelopment", "Testing", true)]
    [InlineData("LocalDevelopment", "development", true)] // IHostEnvironment.IsDevelopment/IsEnvironment are case-insensitive
    [InlineData("LocalDevelopment", "TESTING", true)]
    [InlineData("LocalDevelopment", "Production", false)]
    [InlineData("LocalDevelopment", "Staging", false)]
    [InlineData("LocalDevelopment", "", false)]
    [InlineData("LocalDevelopment", "Development2", false)]
    [InlineData("Unconfigured", "Production", true)] // unconfigured registers the fail-closed UnavailableKeyProvider
    [InlineData(null, "Production", true)]
    [InlineData("localdevelopment", "Production", true)] // not the registration's selector: the unavailable provider is registered (fail closed at use)
    public void TheKeyProviderEnvironmentPolicyAcceptsTheLocalProviderOnlyInDevelopmentAndTesting(string? keyProvider, string environment, bool permitted)
    {
        var configuration = Configuration(keyProvider);
        Assert.Equal(permitted, EncryptionProviderEnvironmentPolicy.IsPermitted(EncryptionProviderEnvironmentPolicy.ConfiguredKeyProvider(configuration), environment));
        var guard = new DocumentProviderStartupGuard(configuration, new FakeEnvironment(environment));
        if (permitted)
        {
            Assert.True(guard.StartAsync(CancellationToken.None).IsCompletedSuccessfully);
        }
        else
        {
            var refused = Assert.Throws<InvalidOperationException>(() => EncryptionProviderEnvironmentPolicy.EnsurePermitted(configuration, environment));
            Assert.Contains("Refusing to start", refused.Message, StringComparison.Ordinal);
            Assert.Throws<InvalidOperationException>(() => { _ = guard.StartAsync(CancellationToken.None); }); // the Business API host fails to start (synchronously, before any other hosted service)
        }
    }

    // ---------------------------------------------------------------- E0-2

    /// <summary>Records the call-stack depth of every read the decryptor issues against the ciphertext source.</summary>
    private sealed class DepthRecordingStream(byte[] data) : MemoryStream(data, writable: false)
    {
        public List<int> Depths { get; } = [];

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Depths.Add(new StackTrace(false).FrameCount);
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    /// <summary>A valid v1 ciphertext of many short segments (the decryptor accepts any segment length up to the segment size).</summary>
    private static (byte[] Ciphertext, byte[] Plaintext, FileEncryptionEnvelope Envelope) ManySegments(LocalDevelopmentKeyProvider keys, FileEncryptionContext context, int segments, int segmentLength)
    {
        var dek = RandomNumberGenerator.GetBytes(32);
        var prefix = RandomNumberGenerator.GetBytes(8);
        var plaintext = RandomNumberGenerator.GetBytes(segments * segmentLength);
        using var output = new MemoryStream();
        using var aes = new AesGcm(dek, 16);
        var nonce = new byte[12];
        prefix.CopyTo(nonce, 0);
        for (var index = 0; index < segments; index++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(8), (uint)index);
            var cipher = new byte[segmentLength];
            var tag = new byte[16];
            aes.Encrypt(nonce, plaintext.AsSpan(index * segmentLength, segmentLength), cipher, tag, AesGcmFileEncryptionService.Aad(context, (uint)index, index == segments - 1));
            var header = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header, (uint)segmentLength);
            output.Write(header);
            output.Write(cipher);
            output.Write(tag);
        }

        return (output.ToArray(), plaintext, new FileEncryptionEnvelope("AES-256-GCM", 1, KeyReference, keys.WrapDataKey(dek, KeyReference), prefix, AesGcmFileEncryptionService.SegmentSize));
    }

    [Fact]
    public async Task DocumentDecryptionReadsTheSourceAtConstantCallDepthWhateverTheSegmentCount()
    {
        var keys = Keys();
        var service = new AesGcmFileEncryptionService(keys);
        var context = DocumentContext();
        var (ciphertext, plaintext, envelope) = ManySegments(keys, context, 300, 3);
        var source = new DepthRecordingStream(ciphertext);
        using var sink = new MemoryStream();
        await service.DecryptAsync(source, sink, envelope, context, TestContext.Current.CancellationToken);
        Assert.Equal(plaintext, sink.ToArray());
        Assert.True(source.Depths.Count > 3 * 300, "the decryptor reads header, body and tag of every segment");
        // Before E0-2 every segment wrapped the source in one more PrefixedStream, so the 300th segment was read through
        // ~300 nested delegations (and a few thousand segments overflowed the stack). Now every read goes directly to the source.
        Assert.True(source.Depths.Max() - source.Depths.Min() <= 4, $"read depth varied from {source.Depths.Min()} to {source.Depths.Max()}");
    }

    [Fact]
    public async Task LargeMultiSegmentDocumentsRoundTripAndKeepEverySegmentCheck()
    {
        var ct = TestContext.Current.CancellationToken;
        var keys = Keys();
        var service = new AesGcmFileEncryptionService(keys);
        var context = DocumentContext();
        var plaintext = RandomNumberGenerator.GetBytes((12 * AesGcmFileEncryptionService.SegmentSize) + 4321);
        using var sealedStream = new MemoryStream();
        var outcome = await service.EncryptAsync(new MemoryStream(plaintext), sealedStream, context, ct);
        var ciphertext = sealedStream.ToArray();
        var source = new DepthRecordingStream(ciphertext);
        using var opened = new MemoryStream();
        await service.DecryptAsync(source, opened, outcome.Envelope, context, ct);
        Assert.Equal(plaintext, opened.ToArray());
        Assert.True(source.Depths.Max() - source.Depths.Min() <= 4);

        const int Record = 4 + AesGcmFileEncryptionService.SegmentSize + 16;
        async Task RejectedAsync(byte[] bytes) =>
            Assert.Equal(DecryptionFailureReasons.AuthenticationFailed, DecryptionFailure.Find(await Assert.ThrowsAnyAsync<CryptographicException>(() => service.DecryptAsync(new MemoryStream(bytes), new MemoryStream(), outcome.Envelope, context, ct)))!.ReasonCode);

        // Truncation at a segment boundary, inside a header, inside a body; reordering; transplanting a middle segment.
        await RejectedAsync(ciphertext[..(6 * Record)]);
        foreach (var partial in new[] { 1, 2, 3 })
        {
            await RejectedAsync(ciphertext[..((6 * Record) + partial)]);
            await RejectedAsync([.. ciphertext, .. new byte[partial]]);
        }

        await RejectedAsync(ciphertext[..^1]);
        await RejectedAsync([.. ciphertext[Record..(2 * Record)], .. ciphertext[..Record], .. ciphertext[(2 * Record)..]]);
        using var otherStream = new MemoryStream();
        await service.EncryptAsync(new MemoryStream(plaintext), otherStream, context, ct);
        var other = otherStream.ToArray();
        await RejectedAsync([.. ciphertext[..(3 * Record)], .. other[(3 * Record)..(4 * Record)], .. ciphertext[(4 * Record)..]]);

        // A declared segment length beyond the segment size (including values above Int32.MaxValue) is rejected as ciphertext, never as an overflow.
        foreach (var declared in new uint[] { (uint)AesGcmFileEncryptionService.SegmentSize + 1, 0x80000000, 0xFFFFFFFF })
        {
            var oversized = (byte[])ciphertext.Clone();
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(oversized.AsSpan(Record, 4), declared);
            await RejectedAsync(oversized);
        }

        await RejectedAsync([]);
        await RejectedAsync([0, 0]);
    }

    // ---------------------------------------------------------------- E0-3

    private sealed class RefusingWrapProvider : IEncryptionKeyProvider
    {
        public string ProviderName => "Refusing";

        public bool IsConfigured => true;

        public string CurrentKeyReference => KeyReference;

        public byte[] WrapDataKey(ReadOnlySpan<byte> dataKey, string keyReference) => throw new EncryptionUnavailableException("wrap refused (test)");

        public byte[] UnwrapDataKey(ReadOnlySpan<byte> wrappedDataKey, string keyReference) => throw new EncryptionUnavailableException("unwrap refused (test)");
    }

    [Fact]
    public async Task TheDocumentDataKeyIsZeroedOnSuccessAndOnEveryFailurePath()
    {
        var ct = TestContext.Current.CancellationToken;
        var context = DocumentContext();
        var plaintext = RandomNumberGenerator.GetBytes(AesGcmFileEncryptionService.SegmentSize + 10);

        static byte[] Dek() => RandomNumberGenerator.GetBytes(32);

        var success = Dek();
        await new AesGcmFileEncryptionService(Keys()).EncryptAsync(new MemoryStream(plaintext), new MemoryStream(), context, success, RandomNumberGenerator.GetBytes(8), ct);
        Assert.All(success, b => Assert.Equal(0, b));

        // The sink fails after the first header (fixed-capacity stream).
        var sinkFailure = Dek();
        await Assert.ThrowsAsync<NotSupportedException>(() => new AesGcmFileEncryptionService(Keys()).EncryptAsync(new MemoryStream(plaintext), new MemoryStream(new byte[8]), context, sinkFailure, RandomNumberGenerator.GetBytes(8), ct));
        Assert.All(sinkFailure, b => Assert.Equal(0, b));

        var wrapFailure = Dek();
        await Assert.ThrowsAsync<EncryptionUnavailableException>(() => new AesGcmFileEncryptionService(new RefusingWrapProvider()).EncryptAsync(new MemoryStream(plaintext), new MemoryStream(), context, wrapFailure, RandomNumberGenerator.GetBytes(8), ct));
        Assert.All(wrapFailure, b => Assert.Equal(0, b));

        var unavailable = Dek();
        await Assert.ThrowsAsync<EncryptionUnavailableException>(() => new AesGcmFileEncryptionService(new UnavailableKeyProvider("test")).EncryptAsync(new MemoryStream(plaintext), new MemoryStream(), context, unavailable, RandomNumberGenerator.GetBytes(8), ct));
        Assert.All(unavailable, b => Assert.Equal(0, b));

        var badPrefix = Dek();
        await Assert.ThrowsAsync<ArgumentException>(() => new AesGcmFileEncryptionService(Keys()).EncryptAsync(new MemoryStream(plaintext), new MemoryStream(), context, badPrefix, new byte[7], ct));
        Assert.All(badPrefix, b => Assert.Equal(0, b));

        // Field values: the single-use data key is zeroed too (unchanged behaviour, now also on the deterministic core).
        var fieldKey = Dek();
        new AesGcmFieldEncryptionService(Keys()).Encrypt(new FieldEncryptionContext(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "String"), "x", fieldKey, RandomNumberGenerator.GetBytes(12));
        Assert.All(fieldKey, b => Assert.Equal(0, b));
        var fieldWrapFailure = Dek();
        Assert.Throws<EncryptionUnavailableException>(() => new AesGcmFieldEncryptionService(new RefusingWrapProvider()).Encrypt(new FieldEncryptionContext(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "String"), "x", fieldWrapFailure, RandomNumberGenerator.GetBytes(12)));
        Assert.All(fieldWrapFailure, b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task DocumentEncryptionAllocatesTheLookAheadBufferOnceNotPerSegment()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = new AesGcmFileEncryptionService(Keys());
        var context = DocumentContext();
        var plaintext = RandomNumberGenerator.GetBytes(8 * AesGcmFileEncryptionService.SegmentSize);
        await service.EncryptAsync(new MemoryStream(plaintext), Stream.Null, context, ct); // warm-up (JIT, crypto provider)

        // Source and sink complete synchronously, so the whole operation runs on this thread.
        var before = GC.GetAllocatedBytesForCurrentThread();
        await service.EncryptAsync(new MemoryStream(plaintext), Stream.Null, context, ct);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        // Ciphertext arrays (one per segment, as before) + two segment buffers ≈ 10 MiB. Before E0-3 a fresh 1 MiB look-ahead
        // buffer per segment added another 8 MiB (≈ 17 MiB).
        Assert.True(allocated < plaintext.Length + (4L * AesGcmFileEncryptionService.SegmentSize), $"allocated {allocated} bytes for {plaintext.Length} bytes of plaintext");
    }

    // ---------------------------------------------------------------- E0-4

    [Fact]
    public void FieldDecryptionFailuresCarryTheirReasonAndIdentifiersWithoutChangingTheExceptionType()
    {
        var keys = Keys();
        var service = new AesGcmFieldEncryptionService(keys);
        var context = new FieldEncryptionContext(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "String");
        var sealedRow = service.Encrypt(context, "Avery Synthetic");

        DecryptionFailure Expect<TException>(Func<string> decrypt, string reason)
            where TException : Exception
        {
            var thrown = Assert.ThrowsAny<TException>(decrypt);
            var failure = DecryptionFailure.Find(thrown);
            Assert.NotNull(failure);
            Assert.Equal(reason, failure.ReasonCode);
            Assert.Equal((ProtectedItemKinds.FieldValue, context.TenantId, context.FormRecordId, context.FieldDefinitionId, context.FieldValueItemId), (failure.ProtectedItemKind, failure.TenantId, failure.FormRecordId, failure.FieldDefinitionId, failure.FieldValueItemId));
            Assert.Null(failure.FileAttachmentId);
            return failure;
        }

        var tampered = Clone(sealedRow);
        tampered.EncryptionAuthTagBytes[0] ^= 0x01;
        Expect<CryptographicException>(() => service.Decrypt(tampered), DecryptionFailureReasons.AuthenticationFailed);
        Expect<CryptographicException>(() => new AesGcmFieldEncryptionService(Keys()).Decrypt(sealedRow), DecryptionFailureReasons.KeyMismatch); // a different KEK under the same reference
        var unknownKey = Clone(sealedRow);
        unknownKey.EncryptionKeyReference = "rotated-kek-v2";
        Expect<EncryptionUnavailableException>(() => service.Decrypt(unknownKey), DecryptionFailureReasons.KeyUnavailable);
        Expect<EncryptionUnavailableException>(() => new AesGcmFieldEncryptionService(new UnavailableKeyProvider("test")).Decrypt(sealedRow), DecryptionFailureReasons.KeyUnavailable);
        var unsupported = Clone(sealedRow);
        unsupported.EncryptionEnvelopeVersion = 2;
        Expect<CryptographicException>(() => service.Decrypt(unsupported), DecryptionFailureReasons.UnsupportedEnvelope);

        // The descriptor is identifiers and a code only.
        var text = Expect<CryptographicException>(() => service.Decrypt(tampered), DecryptionFailureReasons.AuthenticationFailed).ToString();
        Assert.DoesNotContain(KeyReference, text, StringComparison.Ordinal);
        Assert.DoesNotContain("Avery", text, StringComparison.Ordinal);

        // An encryption-side outage is not a decryption failure.
        Assert.Null(DecryptionFailure.Find(Assert.Throws<EncryptionUnavailableException>(() => new AesGcmFieldEncryptionService(new UnavailableKeyProvider("test")).Encrypt(context, "x"))));
        // The successful path is unchanged.
        Assert.Equal("Avery Synthetic", service.Decrypt(sealedRow));
    }

    [Fact]
    public async Task DocumentDecryptionFailuresCarryTheirReasonAndIdentifiersButSinkFailuresDoNot()
    {
        var ct = TestContext.Current.CancellationToken;
        var keys = Keys();
        var service = new AesGcmFileEncryptionService(keys);
        var context = DocumentContext();
        using var sealedStream = new MemoryStream();
        var outcome = await service.EncryptAsync(new MemoryStream(RandomNumberGenerator.GetBytes(5000)), sealedStream, context, ct);
        var ciphertext = sealedStream.ToArray();

        async Task Expect<TException>(byte[] bytes, FileEncryptionEnvelope envelope, AesGcmFileEncryptionService with, string reason)
            where TException : Exception
        {
            var thrown = await Assert.ThrowsAnyAsync<TException>(() => with.DecryptAsync(new MemoryStream(bytes), new MemoryStream(), envelope, context, ct));
            var failure = DecryptionFailure.Find(thrown);
            Assert.NotNull(failure);
            Assert.Equal((reason, ProtectedItemKinds.Document, context.TenantId, context.FormRecordId, context.FileAttachmentId, context.VersionNumber), (failure.ReasonCode, failure.ProtectedItemKind, failure.TenantId, failure.FormRecordId, failure.FileAttachmentId, failure.VersionNumber));
        }

        var tampered = (byte[])ciphertext.Clone();
        tampered[100] ^= 0x01;
        await Expect<CryptographicException>(tampered, outcome.Envelope, service, DecryptionFailureReasons.AuthenticationFailed);
        await Expect<CryptographicException>(ciphertext, outcome.Envelope, new AesGcmFileEncryptionService(Keys()), DecryptionFailureReasons.KeyMismatch);
        await Expect<EncryptionUnavailableException>(ciphertext, outcome.Envelope with { KeyReference = "rotated-kek-v2" }, service, DecryptionFailureReasons.KeyUnavailable);
        await Expect<EncryptionUnavailableException>(ciphertext, outcome.Envelope, new AesGcmFileEncryptionService(new UnavailableKeyProvider("test")), DecryptionFailureReasons.KeyUnavailable);
        await Expect<CryptographicException>(ciphertext, outcome.Envelope with { AlgorithmName = "AES-128-CBC" }, service, DecryptionFailureReasons.UnsupportedEnvelope);

        // A failing response stream (client gone, I/O error) is not a decryption failure and carries no descriptor.
        var sinkFailure = await Assert.ThrowsAsync<NotSupportedException>(() => service.DecryptAsync(new MemoryStream(ciphertext), new MemoryStream(new byte[10]), outcome.Envelope, context, ct));
        Assert.Null(DecryptionFailure.Find(sinkFailure));
    }

    public class Stub<T> : DispatchProxy
        where T : class
    {
        public Func<MethodInfo, object?[], object?> Handler { get; set; } = (method, _) => throw new NotSupportedException(method.Name);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args ?? []);
    }

    private static T Create<T>(Func<MethodInfo, object?[], object?> handler)
        where T : class
    {
        var proxy = DispatchProxy.Create<T, Stub<T>>();
        ((Stub<T>)(object)proxy).Handler = handler;
        return proxy;
    }

    /// <summary>Captures audit events written through committed scopes.</summary>
    private sealed class CapturingStore
    {
        public CapturingStore(bool failing = false)
        {
            Proxy = Create<IBusinessStore>((method, _) =>
            {
                ScopesOpened++;
                return failing ? Task.FromException<IBusinessScope>(new InvalidOperationException("store down (test)")) : Task.FromResult(NewScope());
            });
        }

        public IBusinessStore Proxy { get; }

        public List<AuditEvent> Committed { get; } = [];

        public int ScopesOpened { get; private set; }

        private IBusinessScope NewScope()
        {
            var pending = new List<AuditEvent>();
            return Create<IBusinessScope>((method, args) =>
            {
                switch (method.Name)
                {
                    case nameof(IBusinessScope.AddAuditEvent):
                        pending.Add((AuditEvent)args[0]!);
                        return null;
                    case nameof(IBusinessScope.CommitAsync):
                        Committed.AddRange(pending);
                        return Task.CompletedTask;
                    case nameof(IAsyncDisposable.DisposeAsync):
                        return ValueTask.CompletedTask;
                    default:
                        throw new NotSupportedException(method.Name);
                }
            });
        }
    }

    private static RequestContext Context(Guid organizationId) => new(organizationId, Guid.CreateVersion7(), "https://issuer.test", "subject", "Tester", "raw", Guid.CreateVersion7());

    private static DecryptionFailureRecorder Recorder(CapturingStore store) => new(store.Proxy, TimeProvider.System, NullLogger<DecryptionFailureRecorder>.Instance);

    [Fact]
    public async Task ADecryptionFailureIsRecordedOnceAsASecurityEventWithoutSensitiveContent()
    {
        var keys = Keys();
        var service = new AesGcmFieldEncryptionService(keys);
        var tenant = Guid.CreateVersion7();
        var fieldContext = new FieldEncryptionContext(tenant, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "String");
        var sealedRow = service.Encrypt(fieldContext, "Avery Synthetic");
        var tampered = Clone(sealedRow);
        tampered.EncryptedValueBytes[0] ^= 0x01;
        var thrown = Assert.ThrowsAny<CryptographicException>(() => service.Decrypt(tampered));

        var store = new CapturingStore();
        var recorder = Recorder(store);
        var context = Context(tenant);
        Assert.True(await recorder.RecordOnceAsync(context, thrown));
        Assert.False(await recorder.RecordOnceAsync(context, thrown)); // an outer boundary seeing the same exception does not duplicate it
        var recorded = Assert.Single(store.Committed);
        Assert.Equal((AuditCategories.Security, AuditActions.DecryptionFailed, "Failed", tenant, context.LocalUserId, context.CorrelationId), (recorded.EventCategory, recorded.EventAction, recorded.EventOutcome, recorded.TenantId, recorded.ActorUserId, recorded.CorrelationIdentifier));
        Assert.Equal((fieldContext.FormRecordId, fieldContext.FieldDefinitionId, (Guid?)null), (recorded.FormRecordId, recorded.FieldDefinitionId, recorded.FileAttachmentId));
        using var metadata = JsonDocument.Parse(recorded.EventMetadataJson);
        Assert.Equal(DecryptionFailureReasons.AuthenticationFailed, metadata.RootElement.GetProperty("reasonCode").GetString());
        Assert.Equal(ProtectedItemKinds.FieldValue, metadata.RootElement.GetProperty("protectedItemKind").GetString());
        Assert.Equal(fieldContext.FieldValueItemId, metadata.RootElement.GetProperty("fieldValueItemId").GetGuid());

        // No plaintext, key reference, wrapped key, nonce, tag or ciphertext anywhere in the event.
        var everything = recorded.EventDescription + recorded.EventMetadataJson;
        foreach (var secret in new[]
                 {
                     "Avery", KeyReference,
                     Convert.ToBase64String(sealedRow.WrappedDataKeyBytes), Convert.ToHexStringLower(sealedRow.WrappedDataKeyBytes), Convert.ToHexString(sealedRow.WrappedDataKeyBytes),
                     Convert.ToBase64String(sealedRow.EncryptedValueBytes), Convert.ToHexStringLower(sealedRow.EncryptedValueBytes),
                     Convert.ToBase64String(sealedRow.EncryptionNonceBytes), Convert.ToBase64String(sealedRow.EncryptionAuthTagBytes),
                 })
        {
            Assert.DoesNotContain(secret, everything, StringComparison.OrdinalIgnoreCase);
        }

        // Only decryption failures are recorded; nothing is attached to an ordinary error.
        Assert.False(await recorder.RecordOnceAsync(context, new CryptographicException("unrelated")));
        Assert.Single(store.Committed);
    }

    [Fact]
    public async Task RecordingNeverThrowsAndNeverCopiesAnotherTenantsIdentifiers()
    {
        var failure = new CryptographicException("tag mismatch (test)");
        DecryptionFailure.ForDocument(DecryptionFailureReasons.AuthenticationFailed, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), 4).AttachTo(failure);

        var down = new CapturingStore(failing: true);
        Assert.False(await Recorder(down).RecordOnceAsync(Context(Guid.CreateVersion7()), failure)); // logged, swallowed: the caller still fails closed
        Assert.Equal(1, down.ScopesOpened);

        var other = new CryptographicException("tag mismatch (test)");
        var otherTenant = Guid.CreateVersion7();
        DecryptionFailure.ForDocument(DecryptionFailureReasons.AuthenticationFailed, otherTenant, Guid.CreateVersion7(), Guid.CreateVersion7(), 4).AttachTo(other);
        var store = new CapturingStore();
        var context = Context(Guid.CreateVersion7());
        Assert.True(await Recorder(store).RecordOnceAsync(context, other));
        var recorded = Assert.Single(store.Committed);
        Assert.Equal((context.OrganizationId, (Guid?)null, (Guid?)null), (recorded.TenantId, recorded.FormRecordId, recorded.FileAttachmentId));
    }

    [Fact]
    public void AFailureCarriedOntoATypedRefusalIsStillRecordedExactlyOnce()
    {
        var original = new EncryptionUnavailableException("historic key missing (test)");
        DecryptionFailure.ForFieldValue(DecryptionFailureReasons.KeyUnavailable, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7()).AttachTo(original);
        var refused = new BusinessException("HISTORIC_KEY_UNAVAILABLE", 503, "a key needed to read this version is not available; nothing was restored");
        DecryptionFailure.CarryOver(original, refused);
        Assert.Equal(DecryptionFailure.Find(original), DecryptionFailure.Find(refused));
        Assert.True(DecryptionFailure.TryClaimForRecording(refused, out _));
        Assert.False(DecryptionFailure.TryClaimForRecording(refused, out _));

        // Claimed before being carried: the replacement is not recorded again.
        var claimed = new CryptographicException("x");
        DecryptionFailure.ForFieldValue(DecryptionFailureReasons.AuthenticationFailed, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7()).AttachTo(claimed);
        Assert.True(DecryptionFailure.TryClaimForRecording(claimed, out _));
        var replacement = new BusinessException("X", 503);
        DecryptionFailure.CarryOver(claimed, replacement);
        Assert.False(DecryptionFailure.TryClaimForRecording(replacement, out _));

        // Wrapped as an inner exception, it is found and claimed through the outer one.
        var inner = new CryptographicException("y");
        DecryptionFailure.ForFieldValue(DecryptionFailureReasons.AuthenticationFailed, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7()).AttachTo(inner);
        var outer = new InvalidOperationException("outer", inner);
        Assert.True(DecryptionFailure.TryClaimForRecording(outer, out _));
        Assert.False(DecryptionFailure.TryClaimForRecording(inner, out _));
    }

    private static (DefaultHttpContext Http, CapturingStore Store, Guid Organization, Guid Correlation) HttpWithRecorder(bool authenticated = true)
    {
        var store = new CapturingStore();
        var services = new ServiceCollection();
        services.AddSingleton<IDecryptionFailureRecorder>(Recorder(store));
        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        http.Response.Body = new MemoryStream();
        var organization = Guid.CreateVersion7();
        var correlation = Guid.CreateVersion7();
        http.Request.Headers[BusinessAuthentication.CorrelationHeader] = correlation.ToString();
        if (authenticated)
        {
            http.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("iss", "https://issuer.test"), new Claim("sub", "subject"), new Claim("org", organization.ToString()), new Claim("authority_user_id", Guid.CreateVersion7().ToString())], "test"));
            http.Items[BusinessAuthentication.RawAssertionItem] = "raw-assertion";
        }

        return (http, store, organization, correlation);
    }

    private static async Task<(int Status, string Code)> ProblemAsync(DefaultHttpContext http)
    {
        http.Response.Body.Position = 0;
        using var problem = await JsonDocument.ParseAsync(http.Response.Body);
        return (http.Response.StatusCode, problem.RootElement.GetProperty("title").GetString()!);
    }

    [Fact]
    public async Task TheApiBoundaryRecordsTheFailureOnceAndKeepsThePublicErrorContract()
    {
        var handler = new BusinessProblemHandler();
        var ct = TestContext.Current.CancellationToken;

        // Authentication failure: 423 FIELD_DECRYPTION_FAILED, as before; one event under the request's tenant and correlation.
        var (http, store, organization, correlation) = HttpWithRecorder();
        var tag = new CryptographicException("tag mismatch (test)");
        DecryptionFailure.ForFieldValue(DecryptionFailureReasons.AuthenticationFailed, organization, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7()).AttachTo(tag);
        Assert.True(await handler.TryHandleAsync(http, tag, ct));
        Assert.Equal((423, "FIELD_DECRYPTION_FAILED"), await ProblemAsync(http));
        var recorded = Assert.Single(store.Committed);
        Assert.Equal((organization, correlation, AuditActions.DecryptionFailed), (recorded.TenantId, recorded.CorrelationIdentifier, recorded.EventAction));
        var again = HttpWithRecorder();
        Assert.True(await handler.TryHandleAsync(again.Http, tag, ct)); // the same exception reaching a second boundary is not recorded twice
        Assert.Equal((423, "FIELD_DECRYPTION_FAILED"), await ProblemAsync(again.Http));
        Assert.Empty(again.Store.Committed);
        Assert.Single(store.Committed);

        // Key unavailable: 503 ENCRYPTION_UNAVAILABLE, as before.
        (http, store, organization, _) = HttpWithRecorder();
        var unavailable = new EncryptionUnavailableException("no provider (test)");
        DecryptionFailure.ForDocument(DecryptionFailureReasons.KeyUnavailable, organization, Guid.CreateVersion7(), Guid.CreateVersion7(), 1).AttachTo(unavailable);
        Assert.True(await handler.TryHandleAsync(http, unavailable, ct));
        Assert.Equal((503, "ENCRYPTION_UNAVAILABLE"), await ProblemAsync(http));
        Assert.Single(store.Committed);

        // A typed refusal that carries the failure (historic restore) keeps its own contract and is recorded.
        (http, store, organization, _) = HttpWithRecorder();
        var original = new EncryptionUnavailableException("historic key (test)");
        DecryptionFailure.ForFieldValue(DecryptionFailureReasons.KeyUnavailable, organization, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7()).AttachTo(original);
        var refused = new BusinessException("HISTORIC_KEY_UNAVAILABLE", 503, "a key needed to read this version is not available; nothing was restored");
        DecryptionFailure.CarryOver(original, refused);
        Assert.True(await handler.TryHandleAsync(http, refused, ct));
        Assert.Equal((503, "HISTORIC_KEY_UNAVAILABLE"), await ProblemAsync(http));
        Assert.Single(store.Committed);

        // An encryption-side outage (no descriptor) and an unauthenticated request are answered as before, without an event.
        (http, store, _, _) = HttpWithRecorder();
        Assert.True(await handler.TryHandleAsync(http, new EncryptionUnavailableException("no provider (test)"), ct));
        Assert.Equal((503, "ENCRYPTION_UNAVAILABLE"), await ProblemAsync(http));
        Assert.Empty(store.Committed);
        (http, store, _, _) = HttpWithRecorder(authenticated: false);
        var anonymous = new CryptographicException("tag mismatch (test)");
        DecryptionFailure.ForFieldValue(DecryptionFailureReasons.AuthenticationFailed, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7()).AttachTo(anonymous);
        Assert.True(await handler.TryHandleAsync(http, anonymous, ct));
        Assert.Equal((423, "FIELD_DECRYPTION_FAILED"), await ProblemAsync(http));
        Assert.Empty(store.Committed);
    }

    private static FieldEncryptedValue Clone(FieldEncryptedValue v) => new()
    {
        FieldEncryptedValueId = v.FieldEncryptedValueId, TenantId = v.TenantId, FormRecordId = v.FormRecordId, FieldDefinitionId = v.FieldDefinitionId, FieldValueItemId = v.FieldValueItemId,
        EncryptedValueBytes = [.. v.EncryptedValueBytes], EncryptionNonceBytes = [.. v.EncryptionNonceBytes], EncryptionAuthTagBytes = [.. v.EncryptionAuthTagBytes], WrappedDataKeyBytes = [.. v.WrappedDataKeyBytes],
        EncryptionAlgorithmName = v.EncryptionAlgorithmName, EncryptionKeyReference = v.EncryptionKeyReference, EncryptionEnvelopeVersion = v.EncryptionEnvelopeVersion, EncryptedLogicalType = v.EncryptedLogicalType,
    };
}
