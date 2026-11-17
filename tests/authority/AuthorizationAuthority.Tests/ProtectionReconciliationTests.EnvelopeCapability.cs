using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Catalogue;
using EnterpriseForms.Integrity.Canonical;
using EnterpriseForms.Integrity.Merkle;
using Xunit;
using static AuthorizationAuthority.Tests.ContributorWriteFixtures;
using static AuthorizationAuthority.Tests.EnvelopeCapabilityTestSupport;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// E3-M2f-2 (docs/security/E3_M2F_CONTRACT_CLOSURE.md §6.3, M2F-14): a protection census without a level-2 declaration in an organization
/// with a data-key binding is refused right after the organization lock: no head quarantined, no incident naming the verifier, no census
/// result stored, no revision. The level-2 control shows the quarantine the refusal prevents.
/// </summary>
public sealed partial class ProtectionReconciliationTests
{
    [Fact]
    public async Task M2F14ACensusBelowLevelTwoInABoundOrganizationIsRefusedWithoutQuarantine()
    {
        var h = new Harness();
        var record = await h.RecordAsync(h.FormA);
        var command = h.Decision(h.FormA);
        await h.WriteSealedAsync(record);
        await h.CatalogueAsync(h.FormA, command);
        h.Store.DataKeyBindings.Add(Binding(h.Org, record));
        var reencoded = h.Accepted[record].Select(i => i.ItemKind == ManifestItemKind.FieldValue && RowCanonicalDecoder.Decode(Convert.FromBase64String(i.CanonicalBytesBase64)).Column("field_definition_id")?.AsUuid() == h.Field
            ? i with { CanonicalBytesBase64 = Encode(Text(h.Org, record, h.Field, i.ItemId, "re-encoded"), h.Org, h.AcceptedOperation[record], 2).Base64 }
            : i).ToList();
        var request = new ProtectionCensusRequest([h.Item(record, reencoded)]);
        var before = Fingerprint(h.Store);

        foreach (var capability in new ClientEnvelopeCapability?[] { null, ClientEnvelopeCapability.Legacy })
        {
            AssertRefusal(await Assert.ThrowsAsync<AuthorityException>(() => h.Reconciliation.CensusAsync(Verifier, h.LastSnapshot, request, Guid.CreateVersion7(), CancellationToken.None, capability)));
        }

        Assert.Equal(before, Fingerprint(h.Store));
        Assert.False(h.Head(record).ContentHeadQuarantined);
        Assert.Equal(0, IntegrityIncidents(h.Store));

        var result = (await h.Reconciliation.CensusAsync(Verifier, h.LastSnapshot, request, Guid.CreateVersion7(), CancellationToken.None, ClientEnvelopeCapability.Current)).Results.Single(); // control
        Assert.Equal("ITEM_DIGEST_MISMATCH", result.Outcome);
        Assert.NotEqual(ProtectionCensusOutcomes.Verified, result.Outcome);
        Assert.True(h.Head(record).ContentHeadQuarantined);
        Assert.Equal(1, IntegrityIncidents(h.Store));
    }
}
