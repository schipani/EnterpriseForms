using System.Security.Cryptography;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Journal;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Keys;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Infrastructure.Persistence;
using AuthorizationAuthority.Infrastructure.Signing;
using AuthorizationAuthority.Infrastructure.SpiceDb;
using EnterpriseForms.Integrity.Canonical;
using Xunit;

namespace AuthorizationAuthority.Tests;

public sealed class IntegrityBindingTests
{
    private static readonly RowSchemaDefinition Schema = new("field_string_values", 1,
    [
        new RowSignedColumn("content_change_operation_id", RowColumnType.Uuid, false),
        new RowSignedColumn("string_value", RowColumnType.Text, false),
        new RowSignedColumn("tenant_id", RowColumnType.Uuid, false),
    ]);

    [Fact]
    public void CanonicalHeaderParsesTheTrustedBindingPrefix()
    {
        var org = Guid.CreateVersion7();
        var pk = Guid.CreateVersion7();
        var op = Guid.CreateVersion7();
        var bytes = RowCanonicalizerV1.Encode(Schema, new RowSigningContext(3, org, pk, 7), new Dictionary<string, RowValue?>
        {
            ["content_change_operation_id"] = RowValue.Uuid(op),
            ["string_value"] = RowValue.Text("Café"),
            ["tenant_id"] = RowValue.Uuid(org),
        });
        var header = RowCanonicalHeader.Parse(bytes);
        Assert.Equal(1, header.SchemaVersion);
        Assert.Equal(3u, header.KeyVersion);
        Assert.Equal("field_string_values", header.EntityKind);
        Assert.Equal(org, header.OrganizationId);
        Assert.Equal(pk, header.PrimaryKeyId);
        Assert.Equal(7ul, header.RowRevision);
        Assert.Equal(3u, header.ColumnCount);
    }

    [Theory]
    [InlineData(new byte[] { 0x11, 0x00 })]
    [InlineData(new byte[] { 0x10, 0x00, 0x03, 0x41, 0x42, 0x43 })]
    [InlineData(new byte[] { })]
    public void CanonicalHeaderRejectsForeignFraming(byte[] bytes)
    {
        Assert.Throws<EnterpriseForms.Integrity.IntegrityFormatException>(() => RowCanonicalHeader.Parse(bytes));
    }

    [Fact]
    public async Task LocalProviderSignsVerifiesAndRejectsTamperingWrongKeyAndWrongVersion()
    {
        var provider = new LocalHmacProvider();
        var org = Guid.CreateVersion7();
        var name = IntegrityKeyNaming.ProviderKeyName(org, IntegrityKeyPurposes.RowIntegrityV1);
        var version = await provider.ProvisionKeyAsync(org, IntegrityKeyPurposes.RowIntegrityV1, name, CancellationToken.None);
        var reference = new IntegrityKeyReference(org, IntegrityKeyPurposes.RowIntegrityV1, name, version);
        var message = RandomNumberGenerator.GetBytes(64);
        var mac = await provider.SignAsync(reference, message, CancellationToken.None);
        Assert.Equal(32, mac.Mac.Length);
        Assert.True(await provider.VerifyAsync(reference, message, mac.Mac, CancellationToken.None));

        var tampered = (byte[])message.Clone();
        tampered[10] ^= 0x01;
        Assert.False(await provider.VerifyAsync(reference, tampered, mac.Mac, CancellationToken.None));
        Assert.False(await provider.VerifyAsync(reference with { KeyVersion = 2 }, message, mac.Mac, CancellationToken.None));
        Assert.False(await provider.VerifyAsync(reference, message, mac.Mac.AsMemory(0, 31), CancellationToken.None));

        var otherOrg = Guid.CreateVersion7();
        var otherName = IntegrityKeyNaming.ProviderKeyName(otherOrg, IntegrityKeyPurposes.RowIntegrityV1);
        await provider.ProvisionKeyAsync(otherOrg, IntegrityKeyPurposes.RowIntegrityV1, otherName, CancellationToken.None);
        Assert.False(await provider.VerifyAsync(new IntegrityKeyReference(otherOrg, IntegrityKeyPurposes.RowIntegrityV1, otherName, 1), message, mac.Mac, CancellationToken.None), "SIG-09: organization B cannot verify A's tag");
        await Assert.ThrowsAsync<IntegrityProviderUnavailableException>(() => provider.SignAsync(new IntegrityKeyReference(Guid.CreateVersion7(), IntegrityKeyPurposes.RowIntegrityV1, "org-unprovisioned-row-integrity-v1", 1), message, CancellationToken.None));
    }

    [Fact]
    public void KeyPurposeAllowListAndNamingAreEnforced()
    {
        var org = Guid.Parse("11111111-1111-4111-8111-111111111111");
        Assert.Equal("org-11111111111141118111111111111111-row-integrity-v1", IntegrityKeyNaming.ProviderKeyName(org, IntegrityKeyPurposes.RowIntegrityV1));
        Assert.Throws<ArgumentException>(() => IntegrityKeyNaming.ProviderKeyName(org, "../transit/keys/other"));
        Assert.False(IntegrityKeyPurposes.IsAllowed("jwt-signing"));
    }

    [Fact]
    public void JournalEntriesAreHashChainedPerStream()
    {
        var state = new OrganizationAuthorizationState { OrganizationId = Guid.CreateVersion7() };
        var first = JournalChain.Append(state, JournalStreamNames.Authorization, 1, "SecurityCommandCommitted", new { a = 1 }, Guid.CreateVersion7(), null, TimeProvider.System);
        var second = JournalChain.Append(state, JournalStreamNames.Authorization, 2, "SecurityCommandCommitted", new { a = 2 }, Guid.CreateVersion7(), null, TimeProvider.System);
        var content = JournalChain.Append(state, JournalStreamNames.Content, 1, "ContentHeadCommitted", new { b = 1 }, null, Guid.CreateVersion7(), TimeProvider.System);

        Assert.Null(first.JournalPreviousDigest);
        Assert.Equal(first.JournalEventDigest, second.JournalPreviousDigest);
        Assert.Null(content.JournalPreviousDigest);
        Assert.Equal(second.JournalEventDigest, state.LatestSecurityJournalDigest);
        Assert.Equal(content.JournalEventDigest, state.LatestContentJournalDigest);
        Assert.Equal(CanonicalJson.Digest(System.Text.Encoding.UTF8.GetBytes(second.JournalEventBodyJson)), second.JournalEventDigest);
        Assert.Contains("\"previous_digest_hex\":\"" + Convert.ToHexStringLower(first.JournalEventDigest) + "\"", second.JournalEventBodyJson, StringComparison.Ordinal);
    }

    [Fact]
    public void SnakeCaseMappingProducesDescriptiveColumns()
    {
        Assert.Equal("projection_applied_revision", SnakeCaseNaming.ToSnakeCase("ProjectionAppliedRevision"));
        Assert.Equal("organization_id", SnakeCaseNaming.ToSnakeCase("OrganizationId"));
        Assert.Equal("committed_operation_id", SnakeCaseNaming.ToSnakeCase("CommittedOperationId"));
    }

    [Fact]
    public void EmbeddedSpiceDbSchemaContainsTheCursorMarkerAndNoWildcardPermissions()
    {
        var schema = SpiceDbProjectionClient.EmbeddedSchema();
        Assert.Contains("definition projection_step {}", schema, StringComparison.Ordinal);
        Assert.Contains("relation projection_cursor: projection_step", schema, StringComparison.Ordinal);
        // B-04: record read/edit derive from effective ownership (owner or parent chain); shares never widen record read.
        Assert.Contains("permission effective_owner = owner + parent->effective_owner", schema, StringComparison.Ordinal);
        Assert.Contains("permission read = effective_owner\n", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("permission read = effective_owner + shared_via->read", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("*", schema, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnconfiguredSpiceDbClientFailsClosedInsteadOfThrowingAtConstruction()
    {
        using var client = new SpiceDbProjectionClient(Microsoft.Extensions.Options.Options.Create(new SpiceDbOptions()));
        await Assert.ThrowsAsync<SpiceDbUnavailableException>(() => client.EnsureSchemaAsync(CancellationToken.None));
        var (ready, detail) = await client.ProbeAsync(CancellationToken.None);
        Assert.False(ready);
        Assert.Equal("SpiceDbUnconfigured", detail);
    }
}
