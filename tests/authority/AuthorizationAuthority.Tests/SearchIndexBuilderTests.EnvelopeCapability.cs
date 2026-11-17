using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.SearchIndex;
using EnterpriseForms.Integrity.Merkle;
using Xunit;
using static AuthorizationAuthority.Tests.EnvelopeCapabilityTestSupport;
using Harness = AuthorizationAuthority.Tests.SearchIndexTests.Harness;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// E3-M2f-2 (docs/security/E3_M2F_CONTRACT_CLOSURE.md §6.3, M2F-15, M2F-16): search-index backfill and deep-sweep verification without a
/// level-2 declaration in an organization with a data-key binding are refused right after the organization lock: no record quarantined, no
/// index entry, posting or deep-check stamp written, no index quarantine and no incident. The level-2 controls show what the refusal prevents.
/// </summary>
public sealed partial class SearchIndexBuilderTests
{
    private static List<StagedContentItem> ReencodedValue(Harness h, Guid record)
    {
        var items = h.Accepted[record].ToList();
        var index = items.FindIndex(i => i.ItemKind == ManifestItemKind.FieldValue);
        var bytes = Convert.FromBase64String(items[index].CanonicalBytesBase64);
        bytes[^1] ^= 0x01;
        items[index] = items[index] with { CanonicalBytesBase64 = Convert.ToBase64String(bytes) };
        return items;
    }

    [Fact]
    public async Task M2F15ABackfillBelowLevelTwoInABoundOrganizationIsRefusedWithoutQuarantineOrIndexWrite()
    {
        using var h = new Harness();
        var record = (await PreexistingRecordsAsync(h, 1)).Single();
        h.Store.DataKeyBindings.Add(Binding(h.Org, record));
        var request = new SearchIndexBackfillRequest(h.Org, h.Form, [Item(h, record, ReencodedValue(h, record))]);
        var before = Fingerprint(h.Store);

        foreach (var capability in new ClientEnvelopeCapability?[] { null, ClientEnvelopeCapability.Legacy })
        {
            AssertRefusal(await Assert.ThrowsAsync<AuthorityException>(() => h.Builder.BackfillAsync(Workload, request, Guid.CreateVersion7(), Ct, capability)));
        }

        Assert.Equal(before, Fingerprint(h.Store));
        Assert.False(h.Head(record).ContentHeadQuarantined);
        Assert.Empty(h.Index.Entries);
        Assert.Equal(0, IntegrityIncidents(h.Store));

        var result = (await h.Builder.BackfillAsync(Workload, request, Guid.CreateVersion7(), Ct, ClientEnvelopeCapability.Current)).Results.Single(); // control
        Assert.Equal(("ITEM_DIGEST_MISMATCH", true), (result.Outcome, result.RecordQuarantined));
        Assert.True(h.Head(record).ContentHeadQuarantined);
    }

    [Fact]
    public async Task M2F16ADeepSweepVerificationBelowLevelTwoInABoundOrganizationIsRefusedWithoutQuarantineOrStamp()
    {
        using var h = new Harness();
        var record = (await PreexistingRecordsAsync(h, 1)).Single();
        await BackfillAsync(h, Item(h, record)); // before the binding: a level-1 builder is admitted
        h.Clock.Now = h.Clock.Now.AddDays(1);
        h.Store.DataKeyBindings.Add(Binding(h.Org, record));
        var request = new SearchIndexDeepVerifyRequest(h.Org, [Item(h, record, ReencodedValue(h, record))]);
        var before = Fingerprint(h.Store);
        var entry = (h.Entry(record).LastDeepVerifiedOn, h.Entry(record).LastDeepAttemptedOn, h.Entry(record).LastDeepAttemptOutcomeCode);

        foreach (var capability in new ClientEnvelopeCapability?[] { null, ClientEnvelopeCapability.Legacy })
        {
            AssertRefusal(await Assert.ThrowsAsync<AuthorityException>(() => h.Builder.DeepVerifyAsync(Workload, request, Guid.CreateVersion7(), Ct, capability)));
        }

        Assert.Equal(before, Fingerprint(h.Store));
        Assert.Equal(entry, (h.Entry(record).LastDeepVerifiedOn, h.Entry(record).LastDeepAttemptedOn, h.Entry(record).LastDeepAttemptOutcomeCode));
        Assert.False(h.Head(record).ContentHeadQuarantined);
        Assert.Equal(SearchProjectionStatuses.Active, h.Projection.ProjectionStatusName);

        var result = (await h.Builder.DeepVerifyAsync(Workload, request, Guid.CreateVersion7(), Ct, ClientEnvelopeCapability.Current)).Results.Single(); // control
        Assert.Equal(("Inconclusive", "BUSINESS_BYTES_MISMATCH", true), (result.UnitOutcome, result.OutcomeCode, result.RecordQuarantined));
        Assert.True(h.Head(record).ContentHeadQuarantined);
    }
}
