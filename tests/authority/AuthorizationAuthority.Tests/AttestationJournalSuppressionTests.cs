using System.Security.Cryptography;
using System.Text.Json;
using AuthorizationAuthority.Application.Features.SearchIndex;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.SearchIndex;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// C-143 (R-52) unit tests: a form whose attestation is blocked by an unchanged state journals that failure once; any change of the
/// blocking state is journaled; Complete is never suppressed; identical population issuance is journaled once; the audit hash chain
/// stays contiguous. Concurrency across builders and Authority instances, restarts and permission-change latency are proven on the
/// Docker stack (SearchIndexBuilderStackTests.R52…), because the in-memory store does not lock.
/// </summary>
public sealed class AttestationJournalSuppressionTests
{
    private const string Workload = "search-index-builder";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<SearchIndexAttestation> AttestAsync(SearchIndexTests.Harness h) =>
        h.Builder.AttestAsync(Workload, new SearchIndexAttestationRequest(h.Org, h.Form), Guid.CreateVersion7(), Ct);

    private static Task<SearchIndexPopulationPage> PopulationAsync(SearchIndexTests.Harness h) =>
        h.Builder.PopulationAsync(Workload, new SearchIndexPopulationRequest(h.Org, h.Form), Guid.CreateVersion7(), Ct);

    private static int Events(SearchIndexTests.Harness h, string kind) => h.Store.Journal.Count(j => j.JournalEventKind == kind);

    /// <summary>A form with one honest and one head-quarantined record: its coverages can never be attested Complete.</summary>
    private static async Task<(Guid Honest, Guid Blocked)> BlockedFormAsync(SearchIndexTests.Harness h)
    {
        await h.ApproveStandardFieldsAsync();
        var honest = h.Record();
        await h.WriteAsync(honest);
        var blocked = h.Record();
        await h.WriteAsync(blocked);
        h.Head(blocked).ContentHeadQuarantined = true;
        return (honest, blocked);
    }

    [Fact]
    public async Task AnUnchangedBlockedStateIsJournaledOnceAndTheFirstFailureStaysTheEvidence()
    {
        using var h = new SearchIndexTests.Harness();
        await BlockedFormAsync(h);
        var first = await AttestAsync(h);
        Assert.False(first.CoverageComplete);
        Assert.Equal("RECORD_QUARANTINED", first.IncompleteReasonCode);
        Assert.NotNull(first.AttestationJournalEntryId);
        var events = Events(h, SearchIndexBuilderService.AttestationEventKind);

        for (var i = 0; i < 20; i++)
        {
            var again = await AttestAsync(h);
            Assert.False(again.CoverageComplete); // evaluated every time; never marked attested
            Assert.Equal("RECORD_QUARANTINED", again.IncompleteReasonCode);
            Assert.Equal(first.AttestationJournalEntryId, again.AttestationJournalEntryId); // the original failure is the evidence
        }

        Assert.Equal(events, Events(h, SearchIndexBuilderService.AttestationEventKind));
        Assert.All(h.Index.Coverages.Where(c => c.CoverageStateName != SearchCoverageStates.Withdrawn), c => Assert.Equal(SearchCoverageStates.Building, c.CoverageStateName));
    }

    [Fact]
    public async Task EveryChangeOfTheBlockingStateIsJournaledAndCompleteIsNeverSuppressed()
    {
        using var h = new SearchIndexTests.Harness();
        var (_, blocked) = await BlockedFormAsync(h);
        await AttestAsync(h);
        var baseline = Events(h, SearchIndexBuilderService.AttestationEventKind);

        // A new record (a different blocking state: counts and records) is journaled, then suppressed again.
        await h.WriteAsync(h.Record());
        var changed = await AttestAsync(h);
        Assert.Equal(baseline + 1, Events(h, SearchIndexBuilderService.AttestationEventKind));
        Assert.Equal(changed.AttestationJournalEntryId, (await AttestAsync(h)).AttestationJournalEntryId);
        Assert.Equal(baseline + 1, Events(h, SearchIndexBuilderService.AttestationEventKind));

        // The blocking record is released: the attestation succeeds and is journaled (Complete is never suppressed).
        h.Head(blocked).ContentHeadQuarantined = false;
        var complete = await AttestAsync(h);
        Assert.True(complete.CoverageComplete);
        Assert.Equal(baseline + 2, Events(h, SearchIndexBuilderService.AttestationEventKind));
        Assert.All(h.Index.Coverages.Where(c => c.CoverageStateName != SearchCoverageStates.Withdrawn), c => Assert.Equal(SearchCoverageStates.Complete, c.CoverageStateName));
    }

    [Fact]
    public async Task ADifferentFailureAfterARecoveredStateIsJournaled()
    {
        // State A (blocked) -> B (blocked by a different record) -> A again: each transition is journaled; nothing is skipped
        // merely because an identical state was journaled earlier, since only the latest event is compared.
        using var h = new SearchIndexTests.Harness();
        var (honest, blocked) = await BlockedFormAsync(h);
        await AttestAsync(h);
        var start = Events(h, SearchIndexBuilderService.AttestationEventKind);
        h.Head(blocked).ContentHeadQuarantined = false;
        h.Head(honest).ContentHeadQuarantined = true; // B
        await AttestAsync(h);
        h.Head(honest).ContentHeadQuarantined = false;
        h.Head(blocked).ContentHeadQuarantined = true; // A again
        await AttestAsync(h);
        Assert.Equal(start + 2, Events(h, SearchIndexBuilderService.AttestationEventKind));
    }

    [Fact]
    public async Task IdenticalPopulationIssuanceIsJournaledOnceAndAChangedPopulationAgain()
    {
        using var h = new SearchIndexTests.Harness();
        await BlockedFormAsync(h);
        await PopulationAsync(h);
        var issued = Events(h, SearchIndexBuilderService.PopulationEventKind);
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(2, (await PopulationAsync(h)).Entries.Count); // the population itself is always served
        }

        Assert.Equal(issued, Events(h, SearchIndexBuilderService.PopulationEventKind));
        await h.WriteAsync(h.Record());
        await PopulationAsync(h);
        Assert.Equal(issued + 1, Events(h, SearchIndexBuilderService.PopulationEventKind));
    }

    [Fact]
    public async Task TheAuditHashChainStaysContiguousWithSuppression()
    {
        using var h = new SearchIndexTests.Harness();
        await BlockedFormAsync(h);
        for (var i = 0; i < 10; i++)
        {
            await PopulationAsync(h);
            await AttestAsync(h);
        }

        await h.WriteAsync(h.Record());
        await AttestAsync(h);
        var audit = h.Store.Journal.Where(j => j.OrganizationId == h.Org && j.JournalStreamName == "audit").OrderBy(j => j.JournalStreamRevision).ToList();
        Assert.NotEmpty(audit);
        for (var i = 0; i < audit.Count; i++)
        {
            Assert.Equal(i + 1, audit[i].JournalStreamRevision); // no gap
            Assert.Equal(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(audit[i].JournalEventBodyJson)), audit[i].JournalEventDigest);
            using var envelope = JsonDocument.Parse(audit[i].JournalEventBodyJson);
            var previous = envelope.RootElement.GetProperty("previous_digest_hex");
            Assert.Equal(i == 0 ? null : Convert.ToHexStringLower(audit[i - 1].JournalEventDigest), previous.ValueKind == JsonValueKind.Null ? null : previous.GetString());
        }
    }
}
