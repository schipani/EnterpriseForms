using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Contracts.V1;
using EnterpriseForms.Integrity.Merkle;
using Xunit;
using static AuthorizationAuthority.Tests.ContributorWriteFixtures;
using static AuthorizationAuthority.Tests.EnvelopeCapabilityTestSupport;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// E3-M2f-2 (docs/security/E3_M2F_CONTRACT_CLOSURE.md §6.3) at Verify and batch Verify: in an organization with a data-key binding, a
/// request without a level-2 declaration whose observed bytes differ from the accepted leaves (what a pre-M2b-2 reader submits for a
/// schema-2 row) is refused before actor resolution, pinning and verification: no quarantine, no incident, no revision (M2F-12, M2F-13).
/// The level-2 control shows the quarantine that would otherwise follow. Endpoints without signed content are never refused (M2F-32).
/// </summary>
public sealed partial class SearchCatalogueTests
{
    private static readonly ClientEnvelopeCapability?[] BelowLevelTwo = [null, ClientEnvelopeCapability.Legacy];

    private static List<StagedContentItem> Reencoded(Harness h, Guid record) =>
        h.Accepted[record].Select(i => i.ItemKind == ManifestItemKind.FieldValue
            ? i with { CanonicalBytesBase64 = Encode(Text(h.Org, record, h.Field, i.ItemId, "re-encoded"), h.Org, h.AcceptedOperation[record], 1).Base64 }
            : i).ToList();

    private static Task<VerifyRecordProjectionResult> VerifyWithAsync(Harness h, Guid record, IReadOnlyList<StagedContentItem> observed, ClientEnvelopeCapability? capability) =>
        h.Content.VerifyProjectionAsync(Harness.Actor(h.Owner), new VerifyRecordProjectionRequest(h.Org, record, RequestedAction.Read, observed), Guid.CreateVersion7(), CancellationToken.None, capability);

    private static Task<VerifyRecordProjectionsResponse> BatchWithAsync(Harness h, ClientEnvelopeCapability? capability, params BatchVerificationItem[] records) =>
        h.Content.VerifyProjectionsAsync(Harness.Actor(h.Owner), new VerifyRecordProjectionsRequest(h.Org, records), Guid.CreateVersion7(), CancellationToken.None, capability);

    [Fact]
    public async Task M2F12AVerificationBelowLevelTwoInABoundOrganizationIsRefusedWithoutQuarantine()
    {
        var h = new Harness();
        var record = await h.RecordAsync(h.FormA, value: "accepted");
        h.Store.DataKeyBindings.Add(Binding(h.Org, record));
        var observed = Reencoded(h, record);
        var before = Fingerprint(h.Store);

        foreach (var capability in BelowLevelTwo)
        {
            AssertRefusal(await Assert.ThrowsAsync<AuthorityException>(() => VerifyWithAsync(h, record, observed, capability)));
        }

        Assert.Equal(before, Fingerprint(h.Store));
        Assert.False(h.Head(record).ContentHeadQuarantined);
        Assert.Equal(0, IntegrityIncidents(h.Store));

        // Control (G-2 without the check): the same bytes from a level-2 caller quarantine the head and journal an incident.
        var integrityRevision = h.State.ContentIntegrityRevision;
        Assert.False((await VerifyWithAsync(h, record, observed, ClientEnvelopeCapability.Current)).Verified);
        Assert.True(h.Head(record).ContentHeadQuarantined);
        Assert.True(IntegrityIncidents(h.Store) > 0);
        Assert.True(h.State.ContentIntegrityRevision > integrityRevision);
    }

    [Fact]
    public async Task M2F13ABatchVerificationBelowLevelTwoInABoundOrganizationIsRefusedForEveryRecord()
    {
        var h = new Harness();
        var intact = await h.RecordAsync(h.FormA, value: "intact");
        var reencoded = await h.RecordAsync(h.FormA, value: "re-encoded");
        h.Store.DataKeyBindings.Add(Binding(h.Org, reencoded));
        var items = new[] { h.BatchItem(intact), h.BatchItem(reencoded, Reencoded(h, reencoded)) };
        var before = Fingerprint(h.Store);

        foreach (var capability in BelowLevelTwo)
        {
            AssertRefusal(await Assert.ThrowsAsync<AuthorityException>(() => BatchWithAsync(h, capability, items)));
        }

        Assert.Equal(before, Fingerprint(h.Store));
        Assert.False(h.Head(reencoded).ContentHeadQuarantined);

        var results = (await BatchWithAsync(h, ClientEnvelopeCapability.Current, items)).Results.ToDictionary(r => r.FormRecordId); // control
        Assert.True(results[intact].Verified);
        Assert.False(results[reencoded].Verified);
        Assert.True(h.Head(reencoded).ContentHeadQuarantined);
    }

    [Fact]
    public async Task M2F12VerificationBelowLevelTwoIsUnchangedInAnUnboundOrganization()
    {
        var h = new Harness();
        var record = await h.RecordAsync(h.FormA);
        var other = Guid.CreateVersion7();
        h.Store.Organizations.Add(new AuthorizationAuthority.Domain.Features.Organizations.OrganizationAuthorizationState { OrganizationId = other, AuthorizationRevision = 1, ProjectionAppliedRevision = 1 });
        h.Store.DataKeyBindings.Add(Binding(other)); // another organization's binding (CAP-F7)

        Assert.True((await VerifyWithAsync(h, record, h.Accepted[record], null)).Verified);
        Assert.True((await BatchWithAsync(h, ClientEnvelopeCapability.Legacy, h.BatchItem(record))).Results.Single().Verified);
    }

    [Fact]
    public async Task M2F32EndpointsWithoutSignedContentAreNotRefusedInABoundOrganization()
    {
        var h = new Harness();
        var record = await h.RecordAsync(h.FormA);
        h.Store.DataKeyBindings.Add(Binding(h.Org, record));
        var queries = h.Store.DataKeyBindingExistenceQueries;

        var head = await h.Content.GetHeadAsync(Harness.Actor(h.Owner), h.Org, record, null, Guid.CreateVersion7(), CancellationToken.None);
        Assert.Equal(1, head.CommittedContentRevision);
        Assert.Single((await h.Content.GetHistoryAsync(Harness.Actor(h.Owner), h.Org, record, null, null, Guid.CreateVersion7(), CancellationToken.None)).Entries);
        var catalogue = await h.UserCatalogueAsync(h.Owner, h.FormA);
        Assert.Contains(catalogue.Entries, e => e.FormRecordId == record);
        Assert.True((await h.ValidateAsync(h.Owner, catalogue.SearchCatalogueSnapshotId)).Valid);
        Assert.Equal(queries, h.Store.DataKeyBindingExistenceQueries); // the ledger is consulted by the seven entry points only
    }
}
