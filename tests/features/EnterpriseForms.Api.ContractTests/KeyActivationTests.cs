using System.Net;
using System.Security.Cryptography;
using EnterpriseForms.Application.Features.Documents.Ports;
using EnterpriseForms.Application.Features.FormRecords.CreateFormRecord;
using EnterpriseForms.Application.Features.FormRecords.Encryption;
using EnterpriseForms.Domain.Features.FormRecords;
using EnterpriseForms.Infrastructure.Features.Documents;
using EnterpriseForms.Infrastructure.Features.FormRecords;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// E-4 controlled encryption key activation (E4-7), against the local keyring and the deterministic <see cref="FakeVaultTransit"/>.
/// The model under test: a key version is first <i>provisioned</i> (local: listed in <c>ProvisionedVersions</c>; Vault: a new
/// version exists in Vault), which never makes it current, and then <i>activated</i> by configuration (<c>CurrentVersion</c>),
/// with earlier versions kept in <c>RetainedVersions</c>. Real-Vault behaviour is in <see cref="KeyActivationVaultIntegrationTests"/>.
/// </summary>
public sealed class KeyActivationTests
{
    private const string Mount = "enterpriseforms-encryption";
    private static readonly Guid OrgA = Guid.Parse("019a5b2c-a000-7000-8000-00000000000a");
    private static readonly Guid OrgB = Guid.Parse("019a5b2c-b000-7000-8000-00000000000b");
    private static readonly byte[] Kek = RandomNumberGenerator.GetBytes(32);

    public enum Provider
    {
        LocalKeyring,
        VaultTransit,
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>One scope's version table (all organizations when <see cref="Organization"/> is null).</summary>
    private sealed record Table(string Purpose, int Current, int[]? Retained = null, int[]? Disabled = null, int[]? Provisioned = null, Guid? Organization = null);

    private static string KeyName(Guid organization, EncryptionKeyPurpose purpose) => "ef-enc-" + organization.ToString("N") + "-" + purpose.Token;

    /// <summary>A fake Vault with every key of OrgA and OrgB provisioned at version 1 (the state after the E-2 provisioning script).</summary>
    private static FakeVaultTransit NewVault()
    {
        var vault = new FakeVaultTransit(Mount);
        foreach (var organization in new[] { OrgA, OrgB })
        {
            foreach (var purpose in EncryptionKeyPurpose.All)
            {
                vault.Provision(KeyName(organization, purpose), 1);
            }
        }

        return vault;
    }

    /// <summary>
    /// The provider under the given version tables. For the local keyring, a table without <see cref="Table.Provisioned"/> provisions
    /// exactly its current and retained versions; tests of unprovisioned activation pass the provisioned set explicitly. For Vault,
    /// provisioning is the state of <paramref name="vault"/>.
    /// </summary>
    private static IEncryptionKeyProvider Build(Provider provider, FakeVaultTransit vault, params Table[] tables)
    {
        if (provider == Provider.LocalKeyring)
        {
            var options = new EncryptionOptions { KeyProvider = EncryptionOptions.LocalDevelopmentProviderName, LocalDevelopmentKekBase64 = Convert.ToBase64String(Kek), LocalDevelopmentKeyReference = "local-dev-kek-v1" };
            foreach (var t in tables)
            {
                var table = new LocalKeyVersionOptions { CurrentVersion = t.Current, RetainedVersions = [.. t.Retained ?? []], DisabledVersions = [.. t.Disabled ?? []], ProvisionedVersions = [.. t.Provisioned ?? [.. t.Retained ?? [], t.Current]] };
                if (t.Organization is { } organization)
                {
                    var key = organization.ToString("D");
                    if (!options.LocalDevelopmentKeyring.Organizations.TryGetValue(key, out var byPurpose))
                    {
                        options.LocalDevelopmentKeyring.Organizations[key] = byPurpose = new(StringComparer.Ordinal);
                    }

                    byPurpose[t.Purpose] = table;
                }
                else
                {
                    options.LocalDevelopmentKeyring.Purposes[t.Purpose] = table;
                }
            }

            return new LocalDevelopmentKeyProvider(Options.Create(options));
        }

        var settings = new EncryptionOptions
        {
            KeyProvider = EncryptionOptions.VaultTransitProviderName,
            VaultTransit = new VaultTransitEncryptionOptions { Address = "http://vault.test:8200", Token = FakeVaultTransit.BusinessToken, MountPath = Mount, TimeoutSeconds = 2 },
        };
        foreach (var t in tables)
        {
            var table = new KeyVersionOptions { CurrentVersion = t.Current, RetainedVersions = [.. t.Retained ?? []], DisabledVersions = [.. t.Disabled ?? []] };
            if (t.Organization is { } organization)
            {
                var key = organization.ToString("D");
                if (!settings.VaultTransit.Organizations.TryGetValue(key, out var byPurpose))
                {
                    settings.VaultTransit.Organizations[key] = byPurpose = new(StringComparer.Ordinal);
                }

                byPurpose[t.Purpose] = table;
            }
            else
            {
                settings.VaultTransit.Purposes[t.Purpose] = table;
            }
        }

        return new VaultTransitKeyProvider(vault.Factory(), Options.Create(settings));
    }

    /// <summary>Provisioning step: local — declare the version; Vault — add a version to the key in Vault (an operator action).</summary>
    private static Table[] Provisioned(Provider provider, FakeVaultTransit vault, Guid organization, EncryptionKeyPurpose purpose, int version, params Table[] others)
    {
        if (provider == Provider.VaultTransit)
        {
            vault.Provision(KeyName(organization, purpose), version);
            return others;
        }

        return [.. others, new Table(purpose.Name, 1, Provisioned: [.. Enumerable.Range(1, version)], Organization: organization)];
    }

    private static string Prefix(Provider provider) => provider == Provider.LocalKeyring ? "localdev" : "vault-transit";

    private static string Reference(Provider provider, Guid organization, EncryptionKeyPurpose purpose, int version) =>
        Prefix(provider) + ":" + organization.ToString("D") + ":" + purpose.Token + ":" + version.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static FieldEncryptionContext FieldContext(Guid organization) => new(organization, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "String");

    /// <summary>Counts every key operation, so a read can be shown not to wrap (re-seal) anything.</summary>
    private sealed class CountingKeys(IEncryptionKeyProvider inner) : IEncryptionKeyProvider
    {
        public int Wraps { get; private set; }

        public List<string> UnwrappedReferences { get; } = [];

        public string ProviderName => inner.ProviderName;

        public bool IsConfigured => inner.IsConfigured;

        public Task<WrappedDataKey> WrapDataKeyAsync(DataKeyScope scope, ReadOnlyMemory<byte> dataKey, CancellationToken cancellationToken)
        {
            Wraps++;
            return inner.WrapDataKeyAsync(scope, dataKey, cancellationToken);
        }

        public Task<byte[]> UnwrapDataKeyAsync(DataKeyScope scope, string keyReference, ReadOnlyMemory<byte> wrappedDataKey, CancellationToken cancellationToken)
        {
            UnwrappedReferences.Add(keyReference);
            return inner.UnwrapDataKeyAsync(scope, keyReference, wrappedDataKey, cancellationToken);
        }
    }

    private static FieldEncryptedValue Copy(FieldEncryptedValue v) => new()
    {
        FieldEncryptedValueId = v.FieldEncryptedValueId, TenantId = v.TenantId, FormRecordId = v.FormRecordId, FieldDefinitionId = v.FieldDefinitionId, FieldValueItemId = v.FieldValueItemId,
        EncryptedValueBytes = [.. v.EncryptedValueBytes], EncryptionNonceBytes = [.. v.EncryptionNonceBytes], EncryptionAuthTagBytes = [.. v.EncryptionAuthTagBytes], WrappedDataKeyBytes = [.. v.WrappedDataKeyBytes],
        EncryptionAlgorithmName = v.EncryptionAlgorithmName, EncryptionKeyReference = v.EncryptionKeyReference, EncryptionEnvelopeVersion = v.EncryptionEnvelopeVersion, EncryptedLogicalType = v.EncryptedLogicalType,
    };

    private static void AssertUnchanged(FieldEncryptedValue before, FieldEncryptedValue after)
    {
        Assert.Equal(before.EncryptionKeyReference, after.EncryptionKeyReference);
        Assert.Equal(before.EncryptedValueBytes, after.EncryptedValueBytes);
        Assert.Equal(before.EncryptionNonceBytes, after.EncryptionNonceBytes);
        Assert.Equal(before.EncryptionAuthTagBytes, after.EncryptionAuthTagBytes);
        Assert.Equal(before.WrappedDataKeyBytes, after.WrappedDataKeyBytes);
        Assert.Equal((before.EncryptionAlgorithmName, before.EncryptionEnvelopeVersion), (after.EncryptionAlgorithmName, after.EncryptionEnvelopeVersion));
    }

    // ------------------------------------------------------------------ 1-3. Activation: v1 stays readable, new writes use v2, nothing is rewritten

    [Theory]
    [InlineData(Provider.LocalKeyring)]
    [InlineData(Provider.VaultTransit)]
    public async Task VersionOneDataStaysReadableAfterVersionTwoIsActivatedAndNewWritesUseVersionTwo(Provider provider)
    {
        var vault = NewVault();
        var field = FieldContext(OrgA);
        var document = new FileEncryptionContext(OrgA, Guid.CreateVersion7(), Guid.CreateVersion7(), 1);
        var canonical = new byte[] { 4, 2 };

        // Before: version 1 is current. Field value, document and idempotency fingerprint key are sealed under it.
        var v1Keys = Build(provider, vault);
        var v1Fields = new AesGcmFieldEncryptionService(v1Keys);
        var v1Value = await v1Fields.EncryptAsync(field, "sealed under v1", EncryptionKeyPurpose.FieldValue, Ct);
        using var v1Sink = new MemoryStream();
        var v1Document = await new AesGcmFileEncryptionService(v1Keys).EncryptAsync(new MemoryStream([1, 2, 3, 4]), v1Sink, document, Ct);
        var v1Attempt = await CreationRequestFingerprint.CreateAsync(v1Fields, OrgA, Guid.CreateVersion7(), Guid.CreateVersion7(), DateTimeOffset.UtcNow, canonical, Ct);
        Assert.Equal(Reference(provider, OrgA, EncryptionKeyPurpose.FieldValue, 1), v1Value.EncryptionKeyReference);
        Assert.Equal(Reference(provider, OrgA, EncryptionKeyPurpose.Document, 1), v1Document.Envelope.KeyReference);
        var storedValue = Copy(v1Value);
        var storedDocument = v1Sink.ToArray();
        var storedAttempt = v1Attempt.FingerprintKeyEnvelopeJson;

        // Provisioning version 2 (of all three purposes) does not activate it: new writes still use version 1.
        var tables = Provisioned(provider, vault, OrgA, EncryptionKeyPurpose.FieldValue, 2);
        tables = Provisioned(provider, vault, OrgA, EncryptionKeyPurpose.Document, 2, tables);
        tables = Provisioned(provider, vault, OrgA, EncryptionKeyPurpose.IdempotencyProtection, 2, tables);
        var provisionedOnly = Build(provider, vault, tables);
        Assert.True(provisionedOnly.IsConfigured);
        Assert.Equal(Reference(provider, OrgA, EncryptionKeyPurpose.FieldValue, 1), (await provisionedOnly.WrapDataKeyAsync(new DataKeyScope(OrgA, EncryptionKeyPurpose.FieldValue), RandomNumberGenerator.GetBytes(32), Ct)).KeyReference);
        if (provider == Provider.VaultTransit)
        {
            // Vault now has version 2, but every encrypt names version 1 explicitly: there is no "latest" default.
            Assert.All(vault.Requests.Where(r => r.Path.Contains("/encrypt/", StringComparison.Ordinal)), r => Assert.Equal(1, r.Body.GetProperty("key_version").GetInt32()));
        }

        // Activation: version 2 current, version 1 retained, for each purpose.
        var activated = new CountingKeys(Build(
            provider,
            vault,
            new Table("FieldValue", 2, [1], Organization: OrgA),
            new Table("Document", 2, [1], Organization: OrgA),
            new Table("IdempotencyProtection", 2, [1], Organization: OrgA)));
        var fields = new AesGcmFieldEncryptionService(activated);
        var documents = new AesGcmFileEncryptionService(activated);

        // Historical reads resolve exactly version 1 and never wrap (nothing is re-sealed or rewritten by reading).
        var requestsBeforeReads = vault.Requests.Count;
        Assert.Equal("sealed under v1", await fields.DecryptAsync(v1Value, EncryptionKeyPurpose.FieldValue, Ct));
        using (var opened = new MemoryStream())
        {
            await documents.DecryptAsync(new MemoryStream(storedDocument), opened, v1Document.Envelope, document, Ct);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, opened.ToArray());
        }

        Assert.True(await CreationRequestFingerprint.MatchesAsync(fields, v1Attempt, canonical, Ct));
        Assert.Equal(0, activated.Wraps);
        Assert.Equal(
            [Reference(provider, OrgA, EncryptionKeyPurpose.FieldValue, 1), Reference(provider, OrgA, EncryptionKeyPurpose.Document, 1), Reference(provider, OrgA, EncryptionKeyPurpose.IdempotencyProtection, 1)],
            activated.UnwrappedReferences);
        AssertUnchanged(storedValue, v1Value);
        Assert.Equal(storedAttempt, v1Attempt.FingerprintKeyEnvelopeJson);
        if (provider == Provider.VaultTransit)
        {
            var reads = vault.Requests.Skip(requestsBeforeReads).ToList();
            Assert.All(reads, r => Assert.Contains("/decrypt/", r.Path, StringComparison.Ordinal));
            Assert.All(reads, r => Assert.StartsWith("vault:v1:", r.Body.GetProperty("ciphertext").GetString(), StringComparison.Ordinal));
        }

        // New writes use version 2, and version 2 data reads back.
        var v2Value = await fields.EncryptAsync(FieldContext(OrgA), "sealed under v2", EncryptionKeyPurpose.FieldValue, Ct);
        using var v2Sink = new MemoryStream();
        var v2Document = await documents.EncryptAsync(new MemoryStream([9]), v2Sink, document with { FileAttachmentId = Guid.CreateVersion7() }, Ct);
        var v2Attempt = await CreationRequestFingerprint.CreateAsync(fields, OrgA, Guid.CreateVersion7(), Guid.CreateVersion7(), DateTimeOffset.UtcNow, canonical, Ct);
        Assert.Equal(Reference(provider, OrgA, EncryptionKeyPurpose.FieldValue, 2), v2Value.EncryptionKeyReference);
        Assert.Equal(Reference(provider, OrgA, EncryptionKeyPurpose.Document, 2), v2Document.Envelope.KeyReference);
        Assert.Contains(":idempotency-protection:2", v2Attempt.FingerprintKeyEnvelopeJson, StringComparison.Ordinal);
        Assert.Equal("sealed under v2", await fields.DecryptAsync(v2Value, EncryptionKeyPurpose.FieldValue, Ct));
        Assert.True(await CreationRequestFingerprint.MatchesAsync(fields, v2Attempt, canonical, Ct));
        Assert.Equal(("AES-256-GCM", 1), (v2Value.EncryptionAlgorithmName, v2Value.EncryptionEnvelopeVersion)); // envelope v1 unchanged
        Assert.Equal(("AES-256-GCM", 1), (v2Document.Envelope.AlgorithmName, v2Document.Envelope.EnvelopeVersion));
        if (provider == Provider.VaultTransit)
        {
            Assert.Equal(2, vault.Requests.Last(r => r.Path.Contains("/encrypt/", StringComparison.Ordinal)).Body.GetProperty("key_version").GetInt32());
        }

        // The v1 row is still byte-identical after all of this.
        AssertUnchanged(storedValue, v1Value);
    }

    // ------------------------------------------------------------------ 4. Organization-specific activation

    [Theory]
    [InlineData(Provider.LocalKeyring)]
    [InlineData(Provider.VaultTransit)]
    public async Task ActivationAppliesOnlyToTheIntendedOrganization(Provider provider)
    {
        var vault = NewVault();
        var before = new AesGcmFieldEncryptionService(Build(provider, vault));
        var b1 = await before.EncryptAsync(FieldContext(OrgB), "B under v1", EncryptionKeyPurpose.FieldValue, Ct);

        // Provision and activate version 2 for OrgA only (Vault: only OrgA's key gains a version).
        Provisioned(provider, vault, OrgA, EncryptionKeyPurpose.FieldValue, 2);
        var keys = Build(provider, vault, new Table("FieldValue", 2, [1], Organization: OrgA));
        var fields = new AesGcmFieldEncryptionService(keys);
        Assert.Equal(Reference(provider, OrgA, EncryptionKeyPurpose.FieldValue, 2), (await fields.EncryptAsync(FieldContext(OrgA), "A", EncryptionKeyPurpose.FieldValue, Ct)).EncryptionKeyReference);
        Assert.Equal(Reference(provider, OrgB, EncryptionKeyPurpose.FieldValue, 1), (await fields.EncryptAsync(FieldContext(OrgB), "B", EncryptionKeyPurpose.FieldValue, Ct)).EncryptionKeyReference);
        Assert.Equal("B under v1", await fields.DecryptAsync(b1, EncryptionKeyPurpose.FieldValue, Ct));
        if (keys is LocalDevelopmentKeyProvider local)
        {
            Assert.Equal((2, 1), (local.CurrentVersion(new DataKeyScope(OrgA, EncryptionKeyPurpose.FieldValue)), local.CurrentVersion(new DataKeyScope(OrgB, EncryptionKeyPurpose.FieldValue))));
        }

        // OrgB has no version 2: a version-2 reference of OrgB is not retained, and OrgA's version-2 wrap does not open as OrgB's.
        var a2 = await keys.WrapDataKeyAsync(new DataKeyScope(OrgA, EncryptionKeyPurpose.FieldValue), RandomNumberGenerator.GetBytes(32), Ct);
        var requests = vault.Requests.Count;
        await Assert.ThrowsAnyAsync<CryptographicException>(() => keys.UnwrapDataKeyAsync(new DataKeyScope(OrgB, EncryptionKeyPurpose.FieldValue), a2.KeyReference, a2.WrappedKeyBytes, Ct));
        Assert.Equal("the key version is not retained", (await Assert.ThrowsAsync<EncryptionUnavailableException>(() => keys.UnwrapDataKeyAsync(new DataKeyScope(OrgB, EncryptionKeyPurpose.FieldValue), Reference(provider, OrgB, EncryptionKeyPurpose.FieldValue, 2), a2.WrappedKeyBytes, Ct))).Message);
        Assert.Equal(requests, vault.Requests.Count); // both refused before Vault is called

        // And OrgB's v1 row moved under OrgA does not open.
        var moved = Copy(b1);
        moved.TenantId = OrgA;
        await Assert.ThrowsAnyAsync<CryptographicException>(() => fields.DecryptAsync(moved, EncryptionKeyPurpose.FieldValue, Ct));
    }

    // ------------------------------------------------------------------ 5. Purpose-specific activation

    [Theory]
    [InlineData(Provider.LocalKeyring)]
    [InlineData(Provider.VaultTransit)]
    public async Task ActivationAppliesOnlyToTheIntendedPurpose(Provider provider)
    {
        var vault = NewVault();
        var tables = Provisioned(provider, vault, OrgA, EncryptionKeyPurpose.FieldValue, 2);
        var keys = Build(provider, vault, [.. tables.Where(t => t.Purpose != "FieldValue"), new Table("FieldValue", 2, [1], Organization: OrgA)]);
        var dek = RandomNumberGenerator.GetBytes(32);
        var wrapped = new Dictionary<EncryptionKeyPurpose, WrappedDataKey>();
        foreach (var purpose in EncryptionKeyPurpose.All)
        {
            wrapped[purpose] = await keys.WrapDataKeyAsync(new DataKeyScope(OrgA, purpose), dek, Ct);
            Assert.Equal(Reference(provider, OrgA, purpose, purpose == EncryptionKeyPurpose.FieldValue ? 2 : 1), wrapped[purpose].KeyReference);
        }

        // The field-value version-2 key is not a document, reason-note or idempotency key, under either reference.
        foreach (var other in EncryptionKeyPurpose.All.Where(p => p != EncryptionKeyPurpose.FieldValue))
        {
            await Assert.ThrowsAnyAsync<CryptographicException>(() => keys.UnwrapDataKeyAsync(new DataKeyScope(OrgA, other), wrapped[EncryptionKeyPurpose.FieldValue].KeyReference, wrapped[EncryptionKeyPurpose.FieldValue].WrappedKeyBytes, Ct));
            await Assert.ThrowsAsync<EncryptionUnavailableException>(() => keys.UnwrapDataKeyAsync(new DataKeyScope(OrgA, other), Reference(provider, OrgA, other, 2), wrapped[EncryptionKeyPurpose.FieldValue].WrappedKeyBytes, Ct));
            Assert.Equal(dek, await keys.UnwrapDataKeyAsync(new DataKeyScope(OrgA, other), wrapped[other].KeyReference, wrapped[other].WrappedKeyBytes, Ct));
        }
    }

    // ------------------------------------------------------------------ 6. Invalid activation fails closed

    [Fact]
    public async Task ActivatingAnUnprovisionedLocalVersionMakesTheKeyringUnavailableAndStopsTheHost()
    {
        var vault = NewVault();
        var v1 = await Build(Provider.LocalKeyring, vault).WrapDataKeyAsync(new DataKeyScope(OrgA, EncryptionKeyPurpose.FieldValue), RandomNumberGenerator.GetBytes(32), Ct);
        var invalid = new (Table Table, string Problem)[]
        {
            (new Table("FieldValue", 2, [1], Provisioned: [1]), "the current key version of FieldValue is not provisioned"),
            (new Table("FieldValue", 2, Provisioned: []), "the current key version of FieldValue is not provisioned"),
            (new Table("FieldValue", 3, [1, 2], Provisioned: [1, 3]), "a retained key version of FieldValue is not provisioned"),
            (new Table("FieldValue", 2, [1], Provisioned: [1], Organization: OrgB), "the current key version of FieldValue is not provisioned"),
            (new Table("FieldValue", 2, [1], Provisioned: [0, 1, 2]), "provisioned key versions of FieldValue must be positive"),
            (new Table("FieldValue", 2, [1], Disabled: [2], Provisioned: [1, 2]), "a key version of FieldValue is both usable and disabled"),
        };
        foreach (var (table, problem) in invalid)
        {
            var keys = Build(Provider.LocalKeyring, vault, table);
            Assert.False(keys.IsConfigured);
            // No write and no read at all, for every organization: no fallback to the previous current version.
            Assert.Contains(problem, (await Assert.ThrowsAsync<EncryptionUnavailableException>(() => keys.WrapDataKeyAsync(new DataKeyScope(OrgA, EncryptionKeyPurpose.FieldValue), RandomNumberGenerator.GetBytes(32), Ct))).Message, StringComparison.Ordinal);
            await Assert.ThrowsAsync<EncryptionUnavailableException>(() => keys.WrapDataKeyAsync(new DataKeyScope(OrgA, EncryptionKeyPurpose.Document), RandomNumberGenerator.GetBytes(32), Ct));
            await Assert.ThrowsAsync<EncryptionUnavailableException>(() => keys.UnwrapDataKeyAsync(new DataKeyScope(OrgA, EncryptionKeyPurpose.FieldValue), v1.KeyReference, v1.WrappedKeyBytes, Ct));
        }

        // The API and Worker startup rule refuses the same configurations, naming the problem but never key material.
        var kek = Convert.ToBase64String(Kek);
        (string, string?)[] Settings(params (string, string?)[] extra) => [("Encryption:KeyProvider", "LocalDevelopment"), ("Encryption:LocalDevelopmentKekBase64", kek), .. extra];
        foreach (var settings in new[]
                 {
                     Settings(("Encryption:LocalDevelopmentKeyring:Purposes:FieldValue:CurrentVersion", "2"), ("Encryption:LocalDevelopmentKeyring:Purposes:FieldValue:RetainedVersions:0", "1")),
                     Settings(("Encryption:LocalDevelopmentKeyring:Organizations:" + OrgA.ToString("D") + ":Document:CurrentVersion", "4"), ("Encryption:LocalDevelopmentKeyring:Organizations:" + OrgA.ToString("D") + ":Document:ProvisionedVersions:0", "1")),
                     Settings(("Encryption:LocalDevelopmentKeyring:Purposes:FieldValue:CurrentVersion", "2"), ("Encryption:LocalDevelopmentKeyring:Purposes:FieldValue:ProvisionedVersions:0", "2"), ("Encryption:LocalDevelopmentKeyring:Purposes:FieldValue:RetainedVersions:0", "1")),
                 })
        {
            var refused = Assert.Throws<InvalidOperationException>(() => EncryptionProviderEnvironmentPolicy.EnsurePermitted(Configuration(settings), "Development"));
            Assert.Contains("is not provisioned", refused.Message, StringComparison.Ordinal);
            Assert.Contains("Refusing to start", refused.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(kek, refused.Message, StringComparison.Ordinal);
        }

        // A provisioned activation, and the E-1 default (no tables), start.
        EncryptionProviderEnvironmentPolicy.EnsurePermitted(Configuration(Settings(
            ("Encryption:LocalDevelopmentKeyring:Purposes:FieldValue:CurrentVersion", "2"),
            ("Encryption:LocalDevelopmentKeyring:Purposes:FieldValue:ProvisionedVersions:0", "1"),
            ("Encryption:LocalDevelopmentKeyring:Purposes:FieldValue:ProvisionedVersions:1", "2"),
            ("Encryption:LocalDevelopmentKeyring:Purposes:FieldValue:RetainedVersions:0", "1"))), "Development");
        EncryptionProviderEnvironmentPolicy.EnsurePermitted(Configuration(Settings()), "Testing");
    }

    [Fact]
    public async Task ActivatingAVersionVaultDoesNotHaveFailsEveryWriteWithoutFallingBackAndKeepsHistoricalReads()
    {
        var vault = NewVault();
        var v1 = await Build(Provider.VaultTransit, vault).WrapDataKeyAsync(new DataKeyScope(OrgA, EncryptionKeyPurpose.FieldValue), RandomNumberGenerator.GetBytes(32), Ct);

        // Version 2 configured current before it was provisioned in Vault (the business token cannot read key metadata, so this is
        // detected at the first wrap, not at startup).
        var keys = Build(Provider.VaultTransit, vault, new Table("FieldValue", 2, [1]));
        Assert.True(keys.IsConfigured);
        var requests = vault.Requests.Count;
        await Assert.ThrowsAsync<EncryptionUnavailableException>(() => keys.WrapDataKeyAsync(new DataKeyScope(OrgA, EncryptionKeyPurpose.FieldValue), RandomNumberGenerator.GetBytes(32), Ct));
        var attempts = vault.Requests.Skip(requests).ToList();
        Assert.Equal(2, Assert.Single(attempts).Body.GetProperty("key_version").GetInt32()); // one attempt at version 2, no retry under version 1
        Assert.Equal(1, vault.Provision(KeyName(OrgA, EncryptionKeyPurpose.FieldValue), 1)); // and Vault gained no version

        // Retained version-1 data still reads.
        Assert.Equal(32, (await keys.UnwrapDataKeyAsync(new DataKeyScope(OrgA, EncryptionKeyPurpose.FieldValue), v1.KeyReference, v1.WrappedKeyBytes, Ct)).Length);

        // Invalid tables refuse every operation and stop the host.
        foreach (var table in new[] { new Table("FieldValue", 0), new Table("FieldValue", 2, [1], Disabled: [1]), new Table("FieldValue", 2, Disabled: [2]) })
        {
            var invalid = Build(Provider.VaultTransit, vault, table);
            Assert.False(invalid.IsConfigured);
            await Assert.ThrowsAsync<EncryptionUnavailableException>(() => invalid.WrapDataKeyAsync(new DataKeyScope(OrgA, EncryptionKeyPurpose.FieldValue), RandomNumberGenerator.GetBytes(32), Ct));
            await Assert.ThrowsAsync<EncryptionUnavailableException>(() => invalid.UnwrapDataKeyAsync(new DataKeyScope(OrgA, EncryptionKeyPurpose.FieldValue), v1.KeyReference, v1.WrappedKeyBytes, Ct));
        }

        var refused = Assert.Throws<InvalidOperationException>(() => EncryptionProviderEnvironmentPolicy.EnsurePermitted(Configuration(
            ("Encryption:KeyProvider", "VaultTransit"), ("Encryption:VaultTransit:Address", "http://vault:8200"), ("Encryption:VaultTransit:Token", "dev-business-encryption-token"),
            ("Encryption:VaultTransit:Purposes:FieldValue:CurrentVersion", "2"), ("Encryption:VaultTransit:Purposes:FieldValue:DisabledVersions:0", "2")), "Development"));
        Assert.Contains("Refusing to start", refused.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ 7. Losing a historical version never falls back

    [Theory]
    [InlineData(Provider.LocalKeyring)]
    [InlineData(Provider.VaultTransit)]
    public async Task LosingAHistoricalVersionFailsClosedWithoutFallbackAndIsRecoverableByConfiguration(Provider provider)
    {
        var vault = NewVault();
        var v1Value = await new AesGcmFieldEncryptionService(Build(provider, vault)).EncryptAsync(FieldContext(OrgA), "kept", EncryptionKeyPurpose.FieldValue, Ct);
        var stored = Copy(v1Value);
        Provisioned(provider, vault, OrgA, EncryptionKeyPurpose.FieldValue, 2);

        // Incorrect activation: version 2 current, version 1 forgotten (not retained) or disabled. Reads of v1 data fail closed and
        // are never tried under version 2; for Vault, no request is made at all.
        foreach (var (table, message) in new[]
                 {
                     (new Table("FieldValue", 2, Provisioned: [1, 2], Organization: OrgA), "the key version is not retained"),
                     (new Table("FieldValue", 2, Disabled: [1], Provisioned: [1, 2], Organization: OrgA), "the key version is disabled"),
                 })
        {
            var keys = new CountingKeys(Build(provider, vault, table));
            var requests = vault.Requests.Count;
            var thrown = await Assert.ThrowsAsync<EncryptionUnavailableException>(() => new AesGcmFieldEncryptionService(keys).DecryptAsync(v1Value, EncryptionKeyPurpose.FieldValue, Ct));
            Assert.Equal(DecryptionFailureReasons.KeyUnavailable, DecryptionFailure.Find(thrown)!.ReasonCode);
            Assert.Equal(message, thrown.Message);
            Assert.Equal([v1Value.EncryptionKeyReference], keys.UnwrappedReferences);
            Assert.Equal(requests, vault.Requests.Count);
        }

        if (provider == Provider.VaultTransit)
        {
            // Version 1 retained in configuration but retired in Vault: one decrypt of exactly version 1, refused, nothing else tried.
            vault.SetMinimumDecryptionVersion(KeyName(OrgA, EncryptionKeyPurpose.FieldValue), 2);
            var keys = Build(provider, vault, new Table("FieldValue", 2, [1], Organization: OrgA));
            var requests = vault.Requests.Count;
            await Assert.ThrowsAsync<EncryptionUnavailableException>(() => keys.UnwrapDataKeyAsync(new DataKeyScope(OrgA, EncryptionKeyPurpose.FieldValue), v1Value.EncryptionKeyReference, v1Value.WrappedDataKeyBytes, Ct));
            Assert.StartsWith("vault:v1:", Assert.Single(vault.Requests.Skip(requests)).Body.GetProperty("ciphertext").GetString(), StringComparison.Ordinal);
            vault.SetMinimumDecryptionVersion(KeyName(OrgA, EncryptionKeyPurpose.FieldValue), 1); // the operator undoes the retirement
        }

        // Recovery: correcting the configuration (version 1 back in RetainedVersions) makes the untouched data readable again.
        AssertUnchanged(stored, v1Value);
        var corrected = new AesGcmFieldEncryptionService(Build(provider, vault, new Table("FieldValue", 2, [1], Organization: OrgA)));
        Assert.Equal("kept", await corrected.DecryptAsync(v1Value, EncryptionKeyPurpose.FieldValue, Ct));

        // Rolling back the activation (version 1 current again, version 2 retained) also loses nothing.
        var v2Value = await corrected.EncryptAsync(FieldContext(OrgA), "written while v2 was current", EncryptionKeyPurpose.FieldValue, Ct);
        var rolledBack = new AesGcmFieldEncryptionService(Build(provider, vault, new Table("FieldValue", 1, [2], Provisioned: [1, 2], Organization: OrgA)));
        Assert.Equal(("kept", "written while v2 was current"), (await rolledBack.DecryptAsync(v1Value, EncryptionKeyPurpose.FieldValue, Ct), await rolledBack.DecryptAsync(v2Value, EncryptionKeyPurpose.FieldValue, Ct)));
        Assert.Equal(Reference(provider, OrgA, EncryptionKeyPurpose.FieldValue, 1), (await rolledBack.EncryptAsync(FieldContext(OrgA), "x", EncryptionKeyPurpose.FieldValue, Ct)).EncryptionKeyReference);
    }

    // ------------------------------------------------------------------ 8./9. Legacy data and the v1 vectors after activation

    /// <summary>E0-6 vector F1 (EncryptionKnownAnswerTests): a legacy-wrapped (local-dev-kek-v1) v1 field envelope under the KEK 00..1f.</summary>
    private static FieldEncryptedValue VectorF1() => new()
    {
        FieldEncryptedValueId = Guid.Parse("019a5b2c-e005-7000-8000-000000000005"),
        TenantId = Guid.Parse("019a5b2c-e001-7000-8000-000000000001"),
        FormRecordId = Guid.Parse("019a5b2c-e002-7000-8000-000000000002"),
        FieldDefinitionId = Guid.Parse("019a5b2c-e003-7000-8000-000000000003"),
        FieldValueItemId = Guid.Parse("019a5b2c-e004-7000-8000-000000000004"),
        EncryptedValueBytes = Convert.FromHexString("f0d6cd60480f24745a7b627f1960e08f3e6cdb1f3435d06182c8ea"),
        EncryptionNonceBytes = [.. Enumerable.Range(0x50, 12).Select(i => (byte)i)],
        EncryptionAuthTagBytes = Convert.FromHexString("d82e6790db93b540e70daa1bd08e423b"),
        WrappedDataKeyBytes = Convert.FromHexString("404142434445464748494a4bc2988c000219a124e5ed3d1db7493d7449f7606f830039144f7e74deab2ed3e405e2a68bd43d7c6116749b14f108ed35"),
        EncryptionAlgorithmName = "AES-256-GCM",
        EncryptionKeyReference = "local-dev-kek-v1",
        EncryptionEnvelopeVersion = 1,
        EncryptedLogicalType = "String",
    };

    [Fact]
    public async Task LegacyDataAndTheV1VectorsAreUnaffectedByActivation()
    {
        var vectorKek = Convert.ToBase64String([.. Enumerable.Range(0, 32).Select(i => (byte)i)]);
        var options = new EncryptionOptions { KeyProvider = EncryptionOptions.LocalDevelopmentProviderName, LocalDevelopmentKekBase64 = vectorKek, LocalDevelopmentKeyReference = "local-dev-kek-v1" };
        options.LocalDevelopmentKeyring.Purposes["FieldValue"] = new LocalKeyVersionOptions { ProvisionedVersions = [1, 2], CurrentVersion = 2, RetainedVersions = [1] };
        var keys = new LocalDevelopmentKeyProvider(Options.Create(options));
        var fields = new AesGcmFieldEncryptionService(keys);

        // Legacy data still opens (decrypt only) with version 2 active, and the stored row is not changed by reading it.
        var legacy = VectorF1();
        Assert.Equal("Avery Synthetic — Léa 42", await fields.DecryptAsync(legacy, EncryptionKeyPurpose.FieldValue, Ct));
        AssertUnchanged(VectorF1(), legacy);

        // The v1 payload envelope is independent of the wrapping-key version: F1's DEK and nonce give F1's ciphertext and tag
        // byte for byte; only the key reference names version 2 (never the legacy reference).
        var context = new FieldEncryptionContext(legacy.TenantId, legacy.FormRecordId, legacy.FieldDefinitionId, legacy.FieldValueItemId, "String");
        var resealed = await fields.EncryptAsync(context, "Avery Synthetic — Léa 42", EncryptionKeyPurpose.FieldValue, [.. Enumerable.Range(0x20, 32).Select(i => (byte)i)], [.. legacy.EncryptionNonceBytes], Ct);
        Assert.Equal(legacy.EncryptedValueBytes, resealed.EncryptedValueBytes);
        Assert.Equal(legacy.EncryptionAuthTagBytes, resealed.EncryptionAuthTagBytes);
        Assert.Equal("localdev:019a5b2c-e001-7000-8000-000000000001:field-value:2", resealed.EncryptionKeyReference);

        // K1/K2 (E-1 keyring vectors): versions 1 and 2 of the same scope are the vector keys, whichever is current.
        var scope = new DataKeyScope(legacy.TenantId, EncryptionKeyPurpose.FieldValue);
        var dek = Enumerable.Range(0x20, 32).Select(i => (byte)i).ToArray();
        Assert.Equal(dek, await keys.UnwrapDataKeyAsync(scope, "localdev:019a5b2c-e001-7000-8000-000000000001:field-value:1", Convert.FromHexString("404142434445464748494a4bfcb6ccdcb29bb56ea249d05067c447a30a5a569aacdeab687ce42cd5441c2f9666dd71777becbe2ec79e6b54fcd93c70"), Ct));
        Assert.Equal(dek, await keys.UnwrapDataKeyAsync(scope, "localdev:019a5b2c-e001-7000-8000-000000000001:field-value:2", Convert.FromHexString("404142434445464748494a4bece52a983cfe34917e110f92fd82d700eba76b15708dd8d8d6b25b74b7a34e4e42445943ebb5c1f6ccc628ec21177897"), Ct));

        // With legacy decryption switched off, legacy data fails closed even though a newer version is active (no substitution).
        options.LocalDevelopmentLegacyKeyDecryptionEnabled = false;
        var noLegacy = new AesGcmFieldEncryptionService(new LocalDevelopmentKeyProvider(Options.Create(options)));
        Assert.Equal(DecryptionFailureReasons.KeyUnavailable, DecryptionFailure.Find(await Assert.ThrowsAnyAsync<Exception>(() => noLegacy.DecryptAsync(VectorF1(), EncryptionKeyPurpose.FieldValue, Ct)))!.ReasonCode);
    }

    // ------------------------------------------------------------------ 10. Vault runtime permissions, and failures after activation

    [Fact]
    public async Task TheVaultRuntimeOnlyEverWrapsAndUnwrapsAndCannotProvisionOrActivate()
    {
        var vault = NewVault();
        var before = await Build(Provider.VaultTransit, vault).WrapDataKeyAsync(new DataKeyScope(OrgA, EncryptionKeyPurpose.Document), RandomNumberGenerator.GetBytes(32), Ct);
        vault.Provision(KeyName(OrgA, EncryptionKeyPurpose.Document), 2);
        var keys = Build(Provider.VaultTransit, vault, new Table("Document", 2, [1], Organization: OrgA));
        var after = await keys.WrapDataKeyAsync(new DataKeyScope(OrgA, EncryptionKeyPurpose.Document), RandomNumberGenerator.GetBytes(32), Ct);
        await keys.UnwrapDataKeyAsync(new DataKeyScope(OrgA, EncryptionKeyPurpose.Document), before.KeyReference, before.WrappedKeyBytes, Ct);
        await keys.UnwrapDataKeyAsync(new DataKeyScope(OrgA, EncryptionKeyPurpose.Document), after.KeyReference, after.WrappedKeyBytes, Ct);

        // Every request the provider made is an encrypt or decrypt with the business token and an explicit version: never keys/*,
        // rotate, config (min versions), trim or a request without a version.
        Assert.All(vault.Requests, r =>
        {
            Assert.Equal(FakeVaultTransit.BusinessToken, r.Token);
            Assert.Matches("^/v1/" + Mount + "/(encrypt|decrypt)/ef-enc-[0-9a-f]{32}-[a-z-]+$", r.Path);
            Assert.True(r.Body.TryGetProperty("key_version", out _) || (r.Body.GetProperty("ciphertext").GetString() ?? string.Empty).StartsWith("vault:v", StringComparison.Ordinal));
        });

        // The provider's whole public surface is wrap and unwrap: there is no provisioning or activation operation to call.
        Assert.Equal(["UnwrapDataKeyAsync", "WrapDataKeyAsync"], typeof(VaultTransitKeyProvider).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly).Where(m => !m.IsSpecialName).Select(m => m.Name).Where(n => n.EndsWith("Async", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray());
    }

    [Theory]
    [InlineData(Provider.LocalKeyring)]
    [InlineData(Provider.VaultTransit)]
    public async Task OutagesPermissionDenialAndCancellationAfterActivationFailClosedWithoutFallback(Provider provider)
    {
        var vault = NewVault();
        var v1 = await Build(provider, vault).WrapDataKeyAsync(new DataKeyScope(OrgA, EncryptionKeyPurpose.FieldValue), RandomNumberGenerator.GetBytes(32), Ct);
        Provisioned(provider, vault, OrgA, EncryptionKeyPurpose.FieldValue, 2);
        var keys = Build(provider, vault, new Table("FieldValue", 2, [1], Organization: OrgA));
        var scope = new DataKeyScope(OrgA, EncryptionKeyPurpose.FieldValue);

        using (var source = new CancellationTokenSource())
        {
            await source.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => keys.WrapDataKeyAsync(scope, RandomNumberGenerator.GetBytes(32), source.Token));
            var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AesGcmFieldEncryptionService(keys).DecryptAsync(FieldRow(v1), EncryptionKeyPurpose.FieldValue, source.Token));
            Assert.Null(DecryptionFailure.Find(cancelled)); // a cancellation is not a decryption failure
        }

        if (provider == Provider.VaultTransit)
        {
            foreach (var (status, error) in new[] { (HttpStatusCode.ServiceUnavailable, "Vault is sealed"), (HttpStatusCode.Forbidden, "permission denied") })
            {
                vault.Override = (_, _) => Task.FromResult(FakeVaultTransit.Error(status, error));
                var requests = vault.Requests.Count;
                await Assert.ThrowsAsync<EncryptionUnavailableException>(() => keys.WrapDataKeyAsync(scope, RandomNumberGenerator.GetBytes(32), Ct));
                await Assert.ThrowsAsync<EncryptionUnavailableException>(() => keys.UnwrapDataKeyAsync(scope, v1.KeyReference, v1.WrappedKeyBytes, Ct));
                var attempts = vault.Requests.Skip(requests).ToList();
                Assert.Equal(2, attempts.Count); // one wrap at version 2 and one unwrap at version 1; nothing retried under another version
                Assert.Equal(2, attempts[0].Body.GetProperty("key_version").GetInt32());
                Assert.StartsWith("vault:v1:", attempts[1].Body.GetProperty("ciphertext").GetString(), StringComparison.Ordinal);
            }

            vault.Override = null;
        }

        Assert.Equal(32, (await keys.UnwrapDataKeyAsync(scope, v1.KeyReference, v1.WrappedKeyBytes, Ct)).Length);
    }

    private static FieldEncryptedValue FieldRow(WrappedDataKey key) => new()
    {
        FieldEncryptedValueId = Guid.CreateVersion7(), TenantId = OrgA, FormRecordId = Guid.CreateVersion7(), FieldDefinitionId = Guid.CreateVersion7(), FieldValueItemId = Guid.CreateVersion7(),
        EncryptedValueBytes = new byte[4], EncryptionNonceBytes = new byte[12], EncryptionAuthTagBytes = new byte[16], WrappedDataKeyBytes = key.WrappedKeyBytes,
        EncryptionAlgorithmName = "AES-256-GCM", EncryptionKeyReference = key.KeyReference, EncryptionEnvelopeVersion = 1, EncryptedLogicalType = "String",
    };

    private static IConfiguration Configuration(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();
}
