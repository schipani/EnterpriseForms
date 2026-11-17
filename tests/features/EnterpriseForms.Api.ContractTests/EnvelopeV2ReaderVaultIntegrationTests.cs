using System.Security.Cryptography;
using EnterpriseForms.Application.Features.Documents.Ports;
using EnterpriseForms.Application.Features.FormRecords.Encryption;
using EnterpriseForms.Application.Features.FormRecords.Values;
using EnterpriseForms.Domain.Features.FormRecords;
using EnterpriseForms.Infrastructure.Features.Documents;
using EnterpriseForms.Infrastructure.Features.Documents.EnvelopeV2;
using EnterpriseForms.Infrastructure.Features.FormRecords;
using Microsoft.Extensions.Options;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// E3-M2c dark v2 readers against a REAL HashiCorp Vault 1.20.4 dev server (Development/Testing only), run only by
/// <c>scripts/dev/e3-m2c-vault-reader.ps1</c>: a throwaway container on 127.0.0.1, keys provisioned with the repository's own
/// scripts, the second organization's field-value and document keys given a version 2. v2 rows are sealed by the M1 core
/// (no product v2 writer exists) and read through the product verified readers. Skipped without E2_VAULT_* /
/// E4_VAULT_ACTIVATION_ORGANIZATION. Nothing here changes Vault configuration, so the tests do not depend on order.
/// </summary>
public sealed class EnvelopeV2ReaderVaultIntegrationTests
{
    private const string Mount = "enterpriseforms-encryption";
    private static readonly string? Address = Environment.GetEnvironmentVariable("E2_VAULT_ADDRESS");
    private static readonly string? BusinessToken = Environment.GetEnvironmentVariable("E2_VAULT_BUSINESS_TOKEN");
    private static readonly string? VersionedOrganizationText = Environment.GetEnvironmentVariable("E4_VAULT_ACTIVATION_ORGANIZATION");

    /// <summary>Provisioned by the runner at version 1 only.</summary>
    private static readonly Guid Organization = Guid.Parse("019a5b2c-a000-7000-8000-00000000000a");

    private static readonly Guid Record = Guid.Parse("019a5b2c-0e00-7000-8000-0000000000b1");
    private static readonly Guid Field = Guid.Parse("019a5b2c-0e00-7000-8000-0000000000b2");

    private static Guid VersionedOrganization => Guid.Parse(VersionedOrganizationText!);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void RequireVault() => Assert.SkipWhen(
        string.IsNullOrEmpty(Address) || string.IsNullOrEmpty(BusinessToken) || string.IsNullOrEmpty(VersionedOrganizationText),
        "E2_VAULT_* / E4_VAULT_ACTIVATION_ORGANIZATION not set (run scripts/dev/e3-m2c-vault-reader.ps1)");

    private static string? Reason(Exception ex) => DecryptionFailure.Find(ex)?.ReasonCode;

    /// <summary>The real provider; optionally with an explicit field-value version table for one organization (the runner gives only that key a version 2).</summary>
    private static CountingKeys Keys(Guid? organization = null, int current = 1, params int[] retained)
    {
        var options = new EncryptionOptions
        {
            KeyProvider = EncryptionOptions.VaultTransitProviderName,
            VaultTransit = new VaultTransitEncryptionOptions { Address = Address, Token = BusinessToken, MountPath = Mount },
        };
        if (organization is { } org)
        {
            options.VaultTransit.Organizations[org.ToString("D")] = new(StringComparer.Ordinal)
            {
                [EncryptionKeyPurpose.FieldValue.Name] = new KeyVersionOptions { CurrentVersion = current, RetainedVersions = [.. retained] },
            };
        }

        return new CountingKeys(new VaultTransitKeyProvider(new PlainFactory(), Options.Create(options)));
    }

    private static async Task<FieldEncryptedValue> V2RowAsync(FieldSealBatchV2 batch, string plaintext)
    {
        var envelope = batch.Seal(Field, Guid.CreateVersion7(), "String", plaintext);
        await Task.CompletedTask;
        return new FieldEncryptedValue
        {
            FieldEncryptedValueId = Guid.CreateVersion7(), TenantId = envelope.Header.Prefix.OrganizationId, FormRecordId = Record, FieldDefinitionId = Field, FieldValueItemId = envelope.Identity.FieldValueItemId,
            EncryptedLogicalType = "String", EncryptedValueBytes = envelope.Ciphertext.ToArray(), EncryptionNonceBytes = envelope.Nonce.ToArray(), EncryptionAuthTagBytes = envelope.Tag.ToArray(),
            WrappedDataKeyBytes = envelope.WrappedSeed.ToArray(), EncryptionAlgorithmName = "AES-256-GCM", EncryptionKeyReference = envelope.Header.Prefix.KekReference, EncryptionEnvelopeVersion = 2,
            RowSignatureSchemaVersion = 2, DataKeyIdentifier = envelope.Header.Prefix.DataKeyIdentifier, DataKeyCommitmentBytes = envelope.Header.KeyCommitment.ToArray(), KeyWrapAlgorithmCode = (int)envelope.Header.Prefix.WrapAlgorithm,
        };
    }

    [Fact]
    public async Task TheVerifiedFieldReaderOpensVaultWrappedV1AndV2RowsWithOneUnwrapPerDataKeyGroup()
    {
        RequireVault();
        var keys = Keys();
        var service = new AesGcmFieldEncryptionService(keys);
        using var first = await FieldSealBatchV2.CreateAsync(keys, Organization, Record, EncryptionKeyPurpose.FieldValue, Ct);
        using var second = await FieldSealBatchV2.CreateAsync(keys, Organization, Record, EncryptionKeyPurpose.FieldValue, Ct);
        var v1 = await service.EncryptAsync(new FieldEncryptionContext(Organization, Record, Field, Guid.CreateVersion7(), "String"), "v1 via vault", EncryptionKeyPurpose.FieldValue, Ct);
        v1.RowSignatureSchemaVersion = 1;
        var rows = new[] { await V2RowAsync(first, "a1"), await V2RowAsync(first, "a2"), await V2RowAsync(first, "a3"), v1, await V2RowAsync(second, "b1") };
        Assert.All(rows.Where(r => r.EncryptionEnvelopeVersion == 2), r => Assert.Equal(2, r.KeyWrapAlgorithmCode)); // vault-transit wrap
        Assert.StartsWith("vault-transit:" + Organization.ToString("D") + ":field-value:1", rows[0].EncryptionKeyReference, StringComparison.Ordinal);

        keys.Reset();
        var decrypted = await DecryptedFieldValues.DecryptAsync(service, rows, Ct);
        Assert.Equal(["a1", "a2", "a3", "v1 via vault", "b1"], rows.Select(decrypted.WireOf));
        Assert.Equal(3, keys.UnwrapCalls); // two data-key groups + one v1 row

        // Self-sealed mode never opens a Vault-wrapped v2 row (refused before Vault is called).
        keys.Reset();
        await Assert.ThrowsAnyAsync<CryptographicException>(() => service.DecryptAsync(rows[0], EncryptionKeyPurpose.FieldValue, Ct));
        Assert.Equal(0, keys.UnwrapCalls);
    }

    [Fact]
    public async Task TheExactVaultKeyVersionIsUsedAndARetiredVersionIsNotReplacedByTheCurrentOne()
    {
        RequireVault();
        // Rows sealed while version 1 was current, then while version 2 was current (version 1 retained).
        var atVersion1 = Keys(VersionedOrganization, current: 1);
        using var batch1 = await FieldSealBatchV2.CreateAsync(atVersion1, VersionedOrganization, Record, EncryptionKeyPurpose.FieldValue, Ct);
        var row1 = await V2RowAsync(batch1, "sealed under v1");
        var atVersion2 = Keys(VersionedOrganization, 2, 1);
        using var batch2 = await FieldSealBatchV2.CreateAsync(atVersion2, VersionedOrganization, Record, EncryptionKeyPurpose.FieldValue, Ct);
        var row2 = await V2RowAsync(batch2, "sealed under v2");
        Assert.EndsWith(":field-value:1", row1.EncryptionKeyReference, StringComparison.Ordinal);
        Assert.EndsWith(":field-value:2", row2.EncryptionKeyReference, StringComparison.Ordinal);

        var reader = new AesGcmFieldEncryptionService(atVersion2);
        Assert.Equal(["sealed under v1", "sealed under v2"], await reader.DecryptVerifiedAsync(VersionedOrganization, [row1, row2], Ct));

        // The reference names the version; a row relabelled to the other version is refused after exactly one unwrap attempt.
        var relabelled = new[] { row2 }.Select(r => { var c = Clone(r); c.EncryptionKeyReference = row1.EncryptionKeyReference; return c; }).Single();
        atVersion2.Reset();
        Assert.NotNull(Reason(await Assert.ThrowsAnyAsync<Exception>(() => reader.DecryptVerifiedAsync(VersionedOrganization, [relabelled], Ct))));
        Assert.Equal(1, atVersion2.UnwrapCalls);

        // Version 1 no longer retained: its rows fail closed; nothing falls back to the current version 2.
        var withoutVersion1 = Keys(VersionedOrganization, current: 2);
        var strict = new AesGcmFieldEncryptionService(withoutVersion1);
        var refused = await Assert.ThrowsAnyAsync<Exception>(() => strict.DecryptVerifiedAsync(VersionedOrganization, [row1], Ct));
        Assert.Contains(Reason(refused), new[] { DecryptionFailureReasons.KeyUnavailable, DecryptionFailureReasons.KeyMismatch });
        Assert.Equal(["sealed under v2"], await strict.DecryptVerifiedAsync(VersionedOrganization, [row2], Ct));
    }

    [Fact]
    public async Task TheVerifiedDocumentReaderOpensAVaultWrappedV2VersionAndRefusesAForeignOrganization()
    {
        RequireVault();
        var keys = Keys();
        var service = new AesGcmFileEncryptionService(keys);
        var context = new FileEncryptionContext(Organization, Record, Guid.CreateVersion7(), 1);
        var plaintext = RandomNumberGenerator.GetBytes((1024 * 1024) + 5);
        using var sealedStream = new MemoryStream();
        var outcome = await DocumentEnvelopeV2Cipher.EncryptAsync(new MemoryStream(plaintext), sealedStream, keys, Organization, new DocumentVersionIdentity(Record, context.FileAttachmentId, 1), Ct);
        var e = outcome.Envelope;
        var envelope = new VerifiedDocumentEnvelope(2, "AES-256-GCM", 2, e.Header.Prefix.KekReference, e.WrappedSeed.ToArray(), e.Context.NoncePrefix.ToArray(), e.Context.SegmentSizeBytes, e.Header.Prefix.DataKeyIdentifier, e.Header.KeyCommitment.ToArray(), (int)e.Header.Prefix.WrapAlgorithm);

        keys.Reset();
        using var output = new MemoryStream();
        await service.DecryptVerifiedAsync(new MemoryStream(sealedStream.ToArray()), output, envelope, context, Ct);
        Assert.Equal(plaintext, output.ToArray());
        Assert.Equal(1, keys.UnwrapCalls);

        keys.Reset();
        var foreign = await Assert.ThrowsAnyAsync<CryptographicException>(() => service.DecryptVerifiedAsync(new MemoryStream(sealedStream.ToArray()), new MemoryStream(), envelope, context with { TenantId = VersionedOrganization }, Ct));
        Assert.Equal(DecryptionFailureReasons.UnsupportedEnvelope, Reason(foreign));
        Assert.Equal(0, keys.UnwrapCalls); // refused before Vault is called
    }

    private static FieldEncryptedValue Clone(FieldEncryptedValue r) => new()
    {
        FieldEncryptedValueId = Guid.CreateVersion7(), TenantId = r.TenantId, FormRecordId = r.FormRecordId, FieldDefinitionId = r.FieldDefinitionId, FieldValueItemId = r.FieldValueItemId, EncryptedLogicalType = r.EncryptedLogicalType,
        EncryptedValueBytes = r.EncryptedValueBytes.ToArray(), EncryptionNonceBytes = r.EncryptionNonceBytes.ToArray(), EncryptionAuthTagBytes = r.EncryptionAuthTagBytes.ToArray(), WrappedDataKeyBytes = r.WrappedDataKeyBytes.ToArray(),
        EncryptionAlgorithmName = r.EncryptionAlgorithmName, EncryptionKeyReference = r.EncryptionKeyReference, EncryptionEnvelopeVersion = r.EncryptionEnvelopeVersion, RowSignatureSchemaVersion = r.RowSignatureSchemaVersion,
        DataKeyIdentifier = r.DataKeyIdentifier, DataKeyCommitmentBytes = r.DataKeyCommitmentBytes?.ToArray(), KeyWrapAlgorithmCode = r.KeyWrapAlgorithmCode,
    };

    /// <summary>Counts unwrap calls of the real provider (the unwrap-count evidence).</summary>
    private sealed class CountingKeys(IEncryptionKeyProvider inner) : IEncryptionKeyProvider
    {
        private int unwraps;

        public int UnwrapCalls => Volatile.Read(ref unwraps);

        public string ProviderName => inner.ProviderName;

        public bool IsConfigured => inner.IsConfigured;

        public void Reset() => Interlocked.Exchange(ref unwraps, 0);

        public Task<WrappedDataKey> WrapDataKeyAsync(DataKeyScope scope, ReadOnlyMemory<byte> dataKey, CancellationToken cancellationToken) => inner.WrapDataKeyAsync(scope, dataKey, cancellationToken);

        public Task<byte[]> UnwrapDataKeyAsync(DataKeyScope scope, string keyReference, ReadOnlyMemory<byte> wrappedDataKey, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref unwraps);
            return inner.UnwrapDataKeyAsync(scope, keyReference, wrappedDataKey, cancellationToken);
        }
    }

    private sealed class PlainFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new SocketsHttpHandler { AllowAutoRedirect = false });
    }
}
