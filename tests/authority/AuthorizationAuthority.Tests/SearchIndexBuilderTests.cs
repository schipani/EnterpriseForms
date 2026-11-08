using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.SearchIndex;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Content;
using AuthorizationAuthority.Domain.Features.SearchIndex;
using AuthorizationAuthority.Infrastructure.Signing;
using Xunit;
using static AuthorizationAuthority.Tests.SearchIndexTests;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// IX-2 (C-129) unit tests of the Authority side of the search-index builder on the in-memory store: verified backfill, Authority
/// attestation, replay and race refusal (IX-18, IX-67), forged bytes (IX-25), withholding (IX-26), provider outages (IX-27), the
/// S-C deep comparison, the Complete-coverage completeness check of S-A, and SearchProjectionRebuild (IX-45, IX-59). Atomicity
/// against real PostgreSQL, the worker and the credential separation are the Docker tests (SearchIndexBuilderStackTests).
/// </summary>
public sealed class SearchIndexBuilderTests
{
    private const string Workload = "ix2-unit-builder";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Records written before the approval (no index), then the standard approvals: every record needs a backfill.</summary>
    private static async Task<List<Guid>> PreexistingRecordsAsync(Harness h, int count)
    {
        var records = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            var record = h.Record();
            await h.WriteAsync(record, h.Rows(record, "Ref " + i, 10 + i));
            records.Add(record);
        }

        await h.ApproveStandardFieldsAsync();
        Assert.Empty(h.Index.Entries);
        return records;
    }

    private static SearchIndexBackfillItem Item(Harness h, Guid record, IReadOnlyList<StagedContentItem>? items = null) =>
        new(record, h.Head(record).CommittedOperationId!.Value, items ?? h.Accepted[record]);

    private static Task<SearchIndexBackfillResponse> BackfillAsync(Harness h, params SearchIndexBackfillItem[] items) =>
        h.Builder.BackfillAsync(Workload, new SearchIndexBackfillRequest(h.Org, h.Form, items), Guid.CreateVersion7(), Ct);

    private static Task<SearchIndexAttestation> AttestAsync(Harness h) => h.Builder.AttestAsync(Workload, new SearchIndexAttestationRequest(h.Org, h.Form), Guid.CreateVersion7(), Ct);

    private static void AllowAdministerSecurity(Harness h, Guid user) => h.Spice.Allowed.Add(("organization", h.Org.ToString("D"), "administer_security", user.ToString("D")));

    private static VerifiedActor Actor(Guid user) => new("https://issuer.test", user.ToString("D"), "test");

    // ---- Backfill and attestation ----------------------------------------------------------------------------------------

    [Fact]
    public async Task ABackfillFromVerifiedBytesProducesASealedEntryAndTheAuthorityAttestsComplete()
    {
        using var h = new Harness();
        var records = await PreexistingRecordsAsync(h, 3);

        var population = await h.Builder.PopulationAsync(Workload, new SearchIndexPopulationRequest(h.Org, h.Form), Guid.CreateVersion7(), Ct);
        Assert.Equal(records.Order(), population.Entries.Where(e => e.NeedsIndexing).Select(e => e.FormRecordId).Order());
        Assert.Contains(h.Store.Journal, j => j.JournalEventKind == "SearchIndexPopulationIssued");
        var incomplete = await AttestAsync(h);
        Assert.False(incomplete.CoverageComplete);
        Assert.Equal(("RECORDS_NOT_INDEXED", 3), (incomplete.IncompleteReasonCode, incomplete.MissingRecordCount));

        var response = await BackfillAsync(h, records.Select(r => Item(h, r)).ToArray());
        Assert.All(response.Results, r => Assert.Equal(SearchIndexBuilderService.Indexed, r.Outcome));
        foreach (var record in records)
        {
            var entry = h.Entry(record);
            Assert.Equal(h.Head(record).CommittedOperationId, entry.IndexedOperationId);
            await using var scope = await h.Store.BeginScopeAsync(Ct);
            Assert.Equal((SweepUnitOutcome.Pass, "VALID"), await h.Sweep.VerifyEntryAsync(scope, entry, h.Store.Approvals.ToDictionary(a => a.FieldSearchApprovalId), Ct));
        }

        // Approved top-level values only; the encrypted leaf is an anomaly; repeating-group and unapproved values are never indexed.
        Assert.Equal(3, h.Index.Postings.OfType<SearchStringPosting>().Count());
        Assert.DoesNotContain(h.Index.Postings, p => p.FieldDefinitionId == h.EncryptedField || p.FieldDefinitionId == h.GroupField || p.FieldDefinitionId == h.UnapprovedField);
        Assert.Equal(3, h.Index.Anomalies.Count(a => a.AnomalyKindName == SearchAnomalyKinds.EncryptedValuePresent));

        var attestation = await AttestAsync(h);
        Assert.True(attestation.CoverageComplete, attestation.IncompleteReasonCode);
        Assert.Equal((3, 3, 0), (attestation.ContentfulRecordCount, attestation.IndexedRecordCount, attestation.MissingRecordCount));
        Assert.All(h.Index.Coverages, c => Assert.Equal(SearchCoverageStates.Complete, c.CoverageStateName));
        Assert.All(h.Index.Coverages, c => Assert.Equal(attestation.AttestationJournalEntryId, c.CoverageAttestedJournalEntryId));
        Assert.Equal(SweepUnitOutcome.Pass, (await h.Sweep.RunCheapPassAsync(h.Org, Ct)).Outcome);
        Assert.Equal(h.State.ContentIntegrityRevision, h.Projection.ProjectionContentIntegrityRevision); // a backfill changes no content revision
    }

    [Fact]
    public async Task IX18AReplayedBatchIsIdempotentAndAnOlderAcceptedOperationIsRefused()
    {
        using var h = new Harness();
        var record = (await PreexistingRecordsAsync(h, 1)).Single();
        var older = Item(h, record);
        Assert.Equal(SearchIndexBuilderService.Indexed, (await BackfillAsync(h, older)).Results.Single().Outcome);
        var entryId = h.Entry(record).SearchRecordEntryId;
        var formRevision = h.Index.FormStates.Single().FormContentRevision;

        // The same batch again: nothing written.
        Assert.Equal(SearchIndexBuilderService.AlreadyIndexed, (await BackfillAsync(h, older)).Results.Single().Outcome);
        Assert.Equal(entryId, h.Entry(record).SearchRecordEntryId);
        Assert.Equal(formRevision, h.Index.FormStates.Single().FormContentRevision);

        // A new commit moves the head (inline entry); replaying the previous operation's validly signed bytes is refused.
        await h.WriteAsync(record, h.Rows(record, "newer", 99));
        var inline = h.Entry(record);
        var replay = (await BackfillAsync(h, older)).Results.Single();
        Assert.Equal(("HEAD_CHANGED", false), (replay.Outcome, replay.RecordQuarantined));
        Assert.Equal(inline.SearchRecordEntryId, h.Entry(record).SearchRecordEntryId);
        Assert.Equal("NEWER", Assert.Single(h.Index.Postings.OfType<SearchStringPosting>()).NormalizedStringValue);
        Assert.False(h.Head(record).ContentHeadQuarantined);
    }

    [Fact]
    public async Task IX67ABackfillThatRacesACommitIsRefusedAndNeverOverwritesTheInlineEntry()
    {
        using var h = new Harness();
        var record = (await PreexistingRecordsAsync(h, 1)).Single();
        var population = await h.Builder.PopulationAsync(Workload, new SearchIndexPopulationRequest(h.Org, h.Form), Guid.CreateVersion7(), Ct);
        var read = population.Entries.Single();
        var bytesRead = h.Accepted[record];

        // Between the builder's read and its submission a user commits: the commit derives the entry inline.
        await h.WriteAsync(record, h.Rows(record, "committed meanwhile", 5));
        var inline = h.Entry(record);
        var result = (await BackfillAsync(h, new SearchIndexBackfillItem(record, read.CommittedOperationId!.Value, bytesRead))).Results.Single();
        Assert.Equal("HEAD_CHANGED", result.Outcome);
        Assert.Equal(inline.SearchRecordEntryId, h.Entry(record).SearchRecordEntryId);
        Assert.Equal(h.Head(record).CommittedOperationId, h.Entry(record).IndexedOperationId);

        // The record is already covered at its head, so the attestation completes without the stale submission.
        Assert.True((await AttestAsync(h)).CoverageComplete);
    }

    // ---- IX-25 forged bytes ------------------------------------------------------------------------------------------------

    public static TheoryData<string, string> Forgeries => new()
    {
        { "altered", "ITEM_DIGEST_MISMATCH" },
        { "missing", "MISSING_ITEM" },
        { "none", "MISSING_ITEM" },
        { "extra", "EXTRA_ITEM" },
        { "other-record", "EXTRA_ITEM" },
    };

    [Theory]
    [MemberData(nameof(Forgeries))]
    public async Task IX25ForgedBytesAreRefusedQuarantineTheRecordAndWriteNoIndexRow(string forgery, string expected)
    {
        using var h = new Harness();
        var records = await PreexistingRecordsAsync(h, 2);
        var target = records[0];
        var items = h.Accepted[target].ToList();
        switch (forgery)
        {
            case "altered":
                var index = items.FindIndex(i => i.ItemKind == ManifestItemKind.FieldValue);
                var bytes = Convert.FromBase64String(items[index].CanonicalBytesBase64);
                bytes[^1] ^= 0x01;
                items[index] = items[index] with { CanonicalBytesBase64 = Convert.ToBase64String(bytes) };
                break;
            case "missing":
                items.RemoveAt(items.FindIndex(i => i.ItemKind == ManifestItemKind.FieldValue));
                break;
            case "none":
                items.Clear();
                break;
            case "extra":
                items.Add(h.Accepted[records[1]].First(i => i.ItemKind == ManifestItemKind.FieldValue));
                items.RemoveAt(items.FindIndex(i => i.ItemKind == ManifestItemKind.FieldSelection));
                break;
            default:
                items = h.Accepted[records[1]].ToList();
                break;
        }

        var revision = h.State.ContentIntegrityRevision;
        var result = (await BackfillAsync(h, Item(h, target, items))).Results.Single();
        Assert.Equal((expected, true), (result.Outcome, result.RecordQuarantined));
        Assert.True(h.Head(target).ContentHeadQuarantined);
        Assert.Empty(h.Index.Entries);
        Assert.Empty(h.Index.Postings);
        // W2 (INV-R): the quarantine advanced the content revision and the projection with it; the index itself is not quarantined.
        Assert.Equal(revision + 1, h.State.ContentIntegrityRevision);
        Assert.Equal(h.State.ContentIntegrityRevision, h.Projection.ProjectionContentIntegrityRevision);
        Assert.Equal(SearchProjectionStatuses.Active, h.Projection.ProjectionStatusName);
        Assert.Contains(h.Store.Journal, j => j.JournalEventKind == "IntegrityVerificationFailed" && j.JournalEventBodyJson.Contains(Workload, StringComparison.Ordinal));

        // The quarantined record blocks Complete (fail closed), even when every other record is indexed.
        Assert.Equal(SearchIndexBuilderService.Indexed, (await BackfillAsync(h, Item(h, records[1]))).Results.Single().Outcome);
        var attestation = await AttestAsync(h);
        Assert.Equal((false, "RECORD_QUARANTINED", 1), (attestation.CoverageComplete, attestation.IncompleteReasonCode, attestation.QuarantinedRecordCount));
    }

    [Fact]
    public async Task IX25RecordsOfAnotherFormAndUncoveredFormsAreNeverIndexed()
    {
        using var h = new Harness();
        var record = (await PreexistingRecordsAsync(h, 1)).Single();
        var otherForm = await h.Builder.BackfillAsync(Workload, new SearchIndexBackfillRequest(h.Org, Guid.CreateVersion7(), [Item(h, record)]), Guid.CreateVersion7(), Ct);
        Assert.Equal(SearchIndexBuilderService.NotCovered, otherForm.Results.Single().Outcome);
        Assert.Empty(h.Index.Entries);
        Assert.False(h.Head(record).ContentHeadQuarantined);
    }

    // ---- IX-26 withholding -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task IX26AWithheldRecordKeepsTheCoverageBuildingUntilItIsSubmitted()
    {
        using var h = new Harness();
        var records = await PreexistingRecordsAsync(h, 3);
        await BackfillAsync(h, Item(h, records[0]), Item(h, records[1]));

        var withheld = await AttestAsync(h);
        Assert.Equal((false, "RECORDS_NOT_INDEXED", 1, 2), (withheld.CoverageComplete, withheld.IncompleteReasonCode, withheld.MissingRecordCount, withheld.IndexedRecordCount));
        Assert.All(h.Index.Coverages, c => Assert.Equal(SearchCoverageStates.Building, c.CoverageStateName));
        Assert.Contains(h.Store.Journal, j => j.JournalEventKind == "SearchIndexCoverageAttested" && j.JournalEventBodyJson.Contains("\"coverage_complete\":false", StringComparison.Ordinal));

        await BackfillAsync(h, Item(h, records[2]));
        Assert.True((await AttestAsync(h)).CoverageComplete);
        Assert.All(h.Index.Coverages, c => Assert.Equal(SearchCoverageStates.Complete, c.CoverageStateName));
    }

    [Fact]
    public async Task ARecordWithoutAcceptedContentDoesNotBlockTheAttestation()
    {
        using var h = new Harness();
        var records = await PreexistingRecordsAsync(h, 1);
        h.Record(); // registered, never committed (revision 0): its first commit will derive inline
        await BackfillAsync(h, Item(h, records[0]));
        var attestation = await AttestAsync(h);
        Assert.True(attestation.CoverageComplete);
        Assert.Equal(1, attestation.PendingCreationRecordCount);
    }

    // ---- IX-27 outages --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task IX27AMacProviderOutageFailsTheBatchAndWritesNothing()
    {
        using var h = new Harness();
        var records = await PreexistingRecordsAsync(h, 2);
        h.Mac.Unavailable = true;
        var ex = await Assert.ThrowsAsync<AuthorityException>(() => BackfillAsync(h, records.Select(r => Item(h, r)).ToArray()));
        Assert.Equal((AuthorityErrorCodes.SigningUnavailable, 503), (ex.ErrorCode, ex.HttpStatusCode));
        Assert.Empty(h.Index.Entries);
        Assert.All(records, r => Assert.False(h.Head(r).ContentHeadQuarantined)); // an outage is never tamper evidence
        Assert.All(h.Index.Coverages, c => Assert.Equal(SearchCoverageStates.Building, c.CoverageStateName));

        h.Mac.Unavailable = false; // resumable: the next round starts again from the Authority's population
        Assert.All((await BackfillAsync(h, records.Select(r => Item(h, r)).ToArray())).Results, r => Assert.Equal(SearchIndexBuilderService.Indexed, r.Outcome));
        Assert.True((await AttestAsync(h)).CoverageComplete);
    }

    [Fact]
    public async Task IX27ASealProviderOutageFailsTheBatchAndWritesNothing()
    {
        using var h = new Harness(signerOverride: new UnavailableSearchSealSigner("test outage", new SearchSealOptions()));
        var records = new List<Guid>();
        var record = h.Record();
        await h.WriteAsync(record); // before any approval: no seal needed
        await h.ApproveStandardFieldsAsync();
        var ex = await Assert.ThrowsAsync<AuthorityException>(() => BackfillAsync(h, Item(h, record)));
        Assert.Equal(AuthorityErrorCodes.SearchSealUnavailable, ex.ErrorCode);
        Assert.Empty(h.Index.Entries);
        Assert.False((await AttestAsync(h)).CoverageComplete);
    }

    // ---- S-A completeness of a Complete coverage, and INV-R reconciliation ------------------------------------------------

    [Fact]
    public async Task AnEntryMissingUnderACompleteCoverageIsAFailThatQuarantinesTheIndex()
    {
        using var h = new Harness();
        var records = await PreexistingRecordsAsync(h, 2);
        await BackfillAsync(h, records.Select(r => Item(h, r)).ToArray());
        Assert.True((await AttestAsync(h)).CoverageComplete);

        // Authority-domain omission: one entry and its rows disappear.
        h.Index.Entries.RemoveAll(e => e.FormRecordId == records[0]);
        h.Index.Postings.RemoveAll(p => p.FormRecordId == records[0]);
        h.Index.Anomalies.RemoveAll(a => a.FormRecordId == records[0]);
        var pass = await h.Sweep.RunCheapPassAsync(h.Org, Ct);
        Assert.Equal((SweepUnitOutcome.Fail, "COMPLETE_COVERAGE_MISSING_ENTRY"), (pass.Outcome, pass.FindingCode));
        Assert.Equal(SearchProjectionStatuses.Quarantined, h.Projection.ProjectionStatusName);
        Assert.False(h.State.IntegrityQuarantineEnabled);
    }

    [Fact]
    public async Task ReconciliationReturnsEveryCompleteCoverageToBuildingAndTheBuilderCompletesItAgain()
    {
        using var h = new Harness();
        var records = await PreexistingRecordsAsync(h, 2);
        await BackfillAsync(h, records.Select(r => Item(h, r)).ToArray());
        Assert.True((await AttestAsync(h)).CoverageComplete);

        h.Projection.ProjectionStatusName = SearchProjectionStatuses.ReconciliationRequired; // INV-R option (b), e.g. a Disabled-mode commit
        h.Projection.ReconciliationFromRevision = h.Projection.ProjectionContentIntegrityRevision;
        await h.Sweep.RunCheapPassAsync(h.Org, Ct);
        Assert.Equal(SearchProjectionStatuses.Active, h.Projection.ProjectionStatusName);
        Assert.All(h.Index.Coverages, c => Assert.Equal((SearchCoverageStates.Building, (Guid?)null), (c.CoverageStateName, c.CoverageAttestedJournalEntryId)));
        Assert.True((await AttestAsync(h)).CoverageComplete); // entries still at their heads: re-attested without re-derivation
    }

    // ---- S-C deep comparison --------------------------------------------------------------------------------------------

    [Fact]
    public async Task ADeepComparisonPassWritesOnlyThatEntrysTimestamp()
    {
        using var h = new Harness();
        var records = await PreexistingRecordsAsync(h, 2);
        await BackfillAsync(h, records.Select(r => Item(h, r)).ToArray());
        var before = h.Index.Entries.ToDictionary(e => e.FormRecordId, e => e.LastDeepVerifiedOn);
        h.Clock.Now = h.Clock.Now.AddDays(3);

        var response = await h.Builder.DeepVerifyAsync(Workload, new SearchIndexDeepVerifyRequest(h.Org, [Item(h, records[0])]), Guid.CreateVersion7(), Ct);
        Assert.Equal(("Pass", "MATCH"), (response.Results.Single().UnitOutcome, response.Results.Single().OutcomeCode));
        Assert.Equal(h.Clock.Now, h.Entry(records[0]).LastDeepVerifiedOn);
        Assert.Equal(before[records[1]], h.Entry(records[1]).LastDeepVerifiedOn); // K1: no neighbouring unit is touched
        Assert.Equal(SearchProjectionStatuses.Active, response.ProjectionStatusName);
    }

    [Fact]
    public async Task ADeepComparisonPostingMismatchIsAFailThatQuarantinesTheIndex()
    {
        using var h = new Harness();
        var record = (await PreexistingRecordsAsync(h, 1)).Single();
        await BackfillAsync(h, Item(h, record));
        var stamped = h.Entry(record).LastDeepVerifiedOn;
        h.Clock.Now = h.Clock.Now.AddDays(1);
        Assert.Single(h.Index.Postings.OfType<SearchIntegerPosting>()).IntegerValue = 31_337; // Authority-domain posting change

        var response = await h.Builder.DeepVerifyAsync(Workload, new SearchIndexDeepVerifyRequest(h.Org, [Item(h, record)]), Guid.CreateVersion7(), Ct);
        Assert.Equal(("Fail", "POSTING_MISMATCH"), (response.Results.Single().UnitOutcome, response.Results.Single().OutcomeCode));
        Assert.Equal(SearchProjectionStatuses.Quarantined, h.Projection.ProjectionStatusName);
        Assert.Equal(stamped, h.Entry(record).LastDeepVerifiedOn);
        Assert.Contains(h.Alerts.Raised, a => a.AlertKind == "SEARCH_INDEX_QUARANTINE");
        Assert.False(h.State.IntegrityQuarantineEnabled);
    }

    [Fact]
    public async Task DeepComparisonOfTamperedBusinessBytesQuarantinesTheRecordAndIsInconclusiveForTheIndex()
    {
        using var h = new Harness();
        var record = (await PreexistingRecordsAsync(h, 1)).Single();
        await BackfillAsync(h, Item(h, record));
        var stamped = h.Entry(record).LastDeepVerifiedOn;
        h.Clock.Now = h.Clock.Now.AddDays(1);
        var items = h.Accepted[record].ToList();
        var index = items.FindIndex(i => i.ItemKind == ManifestItemKind.FieldValue);
        var bytes = Convert.FromBase64String(items[index].CanonicalBytesBase64);
        bytes[^1] ^= 0x02;
        items[index] = items[index] with { CanonicalBytesBase64 = Convert.ToBase64String(bytes) };

        var result = (await h.Builder.DeepVerifyAsync(Workload, new SearchIndexDeepVerifyRequest(h.Org, [Item(h, record, items)]), Guid.CreateVersion7(), Ct)).Results.Single();
        Assert.Equal(("Inconclusive", "BUSINESS_BYTES_MISMATCH", true), (result.UnitOutcome, result.OutcomeCode, result.RecordQuarantined));
        Assert.True(h.Head(record).ContentHeadQuarantined);
        Assert.Equal(SearchProjectionStatuses.Active, h.Projection.ProjectionStatusName);
        Assert.Equal(stamped, h.Entry(record).LastDeepVerifiedOn); // K3: no timestamp
        Assert.Equal("BUSINESS_BYTES_MISMATCH", h.Entry(record).LastDeepAttemptOutcomeCode);
    }

    [Fact]
    public async Task ADeepComparisonOfAnOperationTheEntryNoLongerHoldsIsInconclusive()
    {
        using var h = new Harness();
        var record = (await PreexistingRecordsAsync(h, 1)).Single();
        await BackfillAsync(h, Item(h, record));
        var old = Item(h, record);
        await h.WriteAsync(record, h.Rows(record, "moved", 1));
        var result = (await h.Builder.DeepVerifyAsync(Workload, new SearchIndexDeepVerifyRequest(h.Org, [old]), Guid.CreateVersion7(), Ct)).Results.Single();
        Assert.Equal(("Inconclusive", "ENTRY_CHANGED"), (result.UnitOutcome, result.OutcomeCode));
    }

    [Fact]
    public async Task TheDeepSweepSelectionIsTheAuthoritysOldestFirstPlusItsOwnSample()
    {
        using var h = new Harness();
        var records = await PreexistingRecordsAsync(h, 4);
        await BackfillAsync(h, records.Select(r => Item(h, r)).ToArray());
        for (var i = 0; i < records.Count; i++)
        {
            h.Entry(records[i]).LastDeepVerifiedOn = h.Clock.Now.AddDays(-10 + i);
        }

        var oldest = await h.Builder.DeepSweepWorkAsync(new SearchIndexDeepSweepWorkRequest(h.Org, 2, 0), Guid.CreateVersion7(), Ct);
        Assert.Equal(records.Take(2), oldest.Entries.Select(e => e.FormRecordId));
        var withSample = await h.Builder.DeepSweepWorkAsync(new SearchIndexDeepSweepWorkRequest(h.Org, 1, 4), Guid.CreateVersion7(), Ct);
        Assert.Equal(4, withSample.Entries.Count); // distinct: the sample adds the entries the oldest list did not
        await Assert.ThrowsAsync<AuthorityException>(() => h.Builder.DeepSweepWorkAsync(new SearchIndexDeepSweepWorkRequest(h.Org, 0, 0), Guid.CreateVersion7(), Ct));
    }

    // ---- SearchProjectionRebuild (IX-D7, IX-45, IX-59) ------------------------------------------------------------------

    [Fact]
    public async Task IX59OnlyARebuildClearsAQuarantineAndTheIndexServesNothingUntilItIsAttestedAgain()
    {
        using var h = new Harness();
        var records = await PreexistingRecordsAsync(h, 2);
        await BackfillAsync(h, records.Select(r => Item(h, r)).ToArray());
        Assert.True((await AttestAsync(h)).CoverageComplete);
        h.Entry(records[0]).IndexedContentRevision += 5; // Authority-domain tampering
        Assert.Equal(SweepUnitOutcome.Fail, (await h.Sweep.RunCheapPassAsync(h.Org, Ct)).Outcome);
        Assert.Equal(SearchProjectionStatuses.Quarantined, h.Projection.ProjectionStatusName);

        // Nothing is built into a quarantined index, and no number of later passes clears it.
        var refused = await Assert.ThrowsAsync<AuthorityException>(() => BackfillAsync(h, Item(h, records[0])));
        Assert.Equal((AuthorityErrorCodes.SearchIndexQuarantined, 409), (refused.ErrorCode, refused.HttpStatusCode));
        await h.Sweep.RunCheapPassAsync(h.Org, Ct);
        Assert.Equal(SearchProjectionStatuses.Quarantined, h.Projection.ProjectionStatusName);

        AllowAdministerSecurity(h, h.Owner);
        var rebuild = await h.Rebuild.RebuildAsync(Actor(h.Owner), new SearchProjectionRebuildRequest(h.Org, "INTEGRITY_FINDING_RESOLVED"), Guid.CreateVersion7(), Ct);
        Assert.Equal((SearchProjectionStatuses.Quarantined, SearchProjectionStatuses.Rebuilding, 2), (rebuild.PreviousProjectionStatusName, rebuild.ProjectionStatusName, rebuild.EntriesPurged));
        Assert.Empty(h.Index.Entries);
        Assert.Empty(h.Index.Postings);
        Assert.All(h.Index.Coverages, c => Assert.Equal((SearchCoverageStates.Building, (Guid?)null), (c.CoverageStateName, c.CoverageAttestedJournalEntryId)));
        Assert.Contains(h.Store.Journal, j => j.JournalEventKind == "SearchProjectionRebuildRequested" && j.JournalEventBodyJson.Contains(h.Owner.ToString("D"), StringComparison.Ordinal));

        await BackfillAsync(h, records.Select(r => Item(h, r)).ToArray());
        var attestation = await AttestAsync(h);
        Assert.True(attestation.CoverageComplete);
        Assert.Equal(SearchProjectionStatuses.Active, attestation.ProjectionStatusName);
        Assert.Contains(h.Store.Journal, j => j.JournalEventKind == "SearchIndexRebuilt");
        Assert.Equal(SweepUnitOutcome.Pass, (await h.Sweep.RunCheapPassAsync(h.Org, Ct)).Outcome);
    }

    [Fact]
    public async Task IX45ARebuildIsRefusedWhileTheOrganizationIsQuarantined()
    {
        using var h = new Harness();
        await PreexistingRecordsAsync(h, 1);
        AllowAdministerSecurity(h, h.Owner);
        h.State.IntegrityQuarantineEnabled = true; // S1 organization quarantine
        var ex = await Assert.ThrowsAsync<AuthorityException>(() => h.Rebuild.RebuildAsync(Actor(h.Owner), new SearchProjectionRebuildRequest(h.Org, "OPERATOR_REQUEST"), Guid.CreateVersion7(), Ct));
        Assert.Equal((AuthorityErrorCodes.IntegrityQuarantine, 423), (ex.ErrorCode, ex.HttpStatusCode));
        Assert.Equal(SearchProjectionStatuses.Active, h.Projection.ProjectionStatusName);
        Assert.DoesNotContain(h.Store.Journal, j => j.JournalEventKind == "SearchProjectionRebuildRequested");

        // The builder plane honours the same gate: nothing is verified, derived or attested while the organization is quarantined.
        var backfill = await Assert.ThrowsAsync<AuthorityException>(() => BackfillAsync(h, Item(h, h.Store.Heads.Single().FormRecordId)));
        Assert.Equal(423, backfill.HttpStatusCode);
        Assert.Equal(423, (await Assert.ThrowsAsync<AuthorityException>(() => AttestAsync(h))).HttpStatusCode);
        Assert.Equal(423, (await Assert.ThrowsAsync<AuthorityException>(() => h.Builder.DeepVerifyAsync(Workload, new SearchIndexDeepVerifyRequest(h.Org, [Item(h, h.Store.Heads.Single().FormRecordId)]), Guid.CreateVersion7(), Ct))).HttpStatusCode);
    }

    // ---- D-IX2-7 (C-129): quarantined records and the S-C deadline ----------------------------------------------------

    /// <summary>Runs S-A and S-B at the current clock (so only S-C can be overdue) and then the deadline evaluation.</summary>
    private static async Task<DeadlineEvaluationResult> EvaluateAfterFreshSweepsAsync(Harness h)
    {
        Assert.Equal(SweepUnitOutcome.Pass, (await h.Sweep.RunCheapPassAsync(h.Org, Ct)).Outcome);
        Assert.Equal(SweepUnitOutcome.Pass, (await h.Sweep.RunSealPassAsync(h.Org, Ct)).Outcome);
        return await h.Sweep.EvaluateDeadlinesAsync(h.Org, Ct);
    }

    [Fact]
    public async Task DIX27AQuarantinedRecordIsExcludedFromTheDeepDeadlineWithoutCountingAsVerified()
    {
        using var h = new Harness();
        var records = await PreexistingRecordsAsync(h, 2);
        await BackfillAsync(h, records.Select(r => Item(h, r)).ToArray());
        var excludedStamp = h.Entry(records[0]).LastDeepVerifiedOn;
        h.Head(records[0]).ContentHeadQuarantined = true; // Authority state (in production set only by a verified-read incident)

        h.Clock.Now = h.Clock.Now.AddDays(31);
        var pass = await h.Builder.DeepVerifyAsync(Workload, new SearchIndexDeepVerifyRequest(h.Org, [Item(h, records[1])]), Guid.CreateVersion7(), Ct);
        Assert.Equal("Pass", pass.Results.Single().UnitOutcome);
        var evaluation = await EvaluateAfterFreshSweepsAsync(h);

        Assert.False(evaluation.Suspended, string.Join(",", evaluation.OverdueScopes)); // without the exclusion S-C would be overdue
        Assert.Equal(excludedStamp, h.Entry(records[0]).LastDeepVerifiedOn);            // rule 4: exclusion is not verification
        Assert.Equal(1, h.Projection.DeepDeadlineExcludedEntryCount);
        Assert.Contains(h.Store.Journal, j => j.JournalEventKind == "SearchIndexDeepDeadlineExclusionChanged" && j.JournalEventBodyJson.Contains("\"excluded_entries\":1", StringComparison.Ordinal));
        Assert.Contains(h.Alerts.Raised, a => a.AlertKind == "SEARCH_INDEX_DEEP_EXCLUSION_CHANGED");
        Assert.Single(h.Index.Entries, e => e.FormRecordId == records[0]); // rule 2: the entry stays for forensics and recovery
    }

    [Fact]
    public async Task DIX27QuarantinedRecordsAreNeverSelectedForTheDeepComparison()
    {
        using var h = new Harness();
        var records = await PreexistingRecordsAsync(h, 3);
        await BackfillAsync(h, records.Select(r => Item(h, r)).ToArray());
        h.Head(records[0]).ContentHeadQuarantined = true;
        h.Store.Resources.Single(r => r.ResourceEntityId == records[1]).ResourceLifecycleState = "Quarantined";
        var work = await h.Builder.DeepSweepWorkAsync(new SearchIndexDeepSweepWorkRequest(h.Org, 10, 10), Guid.CreateVersion7(), Ct);
        Assert.Equal([records[2]], work.Entries.Select(e => e.FormRecordId));
    }

    [Fact]
    public async Task DIX27ARecordLeavingQuarantineSuspendsTheIndexUntilItPassesTheDeepComparison()
    {
        using var h = new Harness();
        var records = await PreexistingRecordsAsync(h, 2);
        await BackfillAsync(h, records.Select(r => Item(h, r)).ToArray());
        h.Head(records[0]).ContentHeadQuarantined = true;
        h.Clock.Now = h.Clock.Now.AddDays(31);
        await h.Builder.DeepVerifyAsync(Workload, new SearchIndexDeepVerifyRequest(h.Org, [Item(h, records[1])]), Guid.CreateVersion7(), Ct);
        Assert.False((await EvaluateAfterFreshSweepsAsync(h)).Suspended);

        // The quarantine ends (no product path does this today; rule 7 is enforced defensively): the record re-enters the deadline
        // with its old timestamp, so the index stops serving until the record has passed the deep comparison again.
        h.Head(records[0]).ContentHeadQuarantined = false;
        var suspended = await EvaluateAfterFreshSweepsAsync(h);
        Assert.True(suspended.Suspended);
        Assert.Equal(["S-C"], suspended.OverdueScopes);
        Assert.Equal(0, h.Projection.DeepDeadlineExcludedEntryCount);
        Assert.Equal(2, h.Store.Journal.Count(j => j.JournalEventKind == "SearchIndexDeepDeadlineExclusionChanged"));

        Assert.Equal("Pass", (await h.Builder.DeepVerifyAsync(Workload, new SearchIndexDeepVerifyRequest(h.Org, [Item(h, records[0])]), Guid.CreateVersion7(), Ct)).Results.Single().UnitOutcome);
        Assert.False((await h.Sweep.EvaluateDeadlinesAsync(h.Org, Ct)).Suspended);
    }

    [Fact]
    public async Task DIX27TheExclusionNeverHidesAnIntegrityFailure()
    {
        using var h = new Harness();
        var records = await PreexistingRecordsAsync(h, 2);
        await BackfillAsync(h, records.Select(r => Item(h, r)).ToArray());
        h.Head(records[0]).ContentHeadQuarantined = true;

        // An Authority-domain change to the quarantined record's posting is still a Fail of S-B (every entry is sealed and checked).
        Assert.Single(h.Index.Postings.OfType<SearchIntegerPosting>(), p => p.FormRecordId == records[0]).IntegerValue = 777;
        var seal = await h.Sweep.RunSealPassAsync(h.Org, Ct);
        Assert.Equal(SweepUnitOutcome.Fail, seal.Outcome);
        Assert.Equal(SearchProjectionStatuses.Quarantined, h.Projection.ProjectionStatusName);
    }


    [Fact]
    public async Task ARebuildRequiresAdministerSecurityAndAValidReasonCode()
    {
        using var h = new Harness();
        await PreexistingRecordsAsync(h, 1);
        var denied = await Assert.ThrowsAsync<AuthorityException>(() => h.Rebuild.RebuildAsync(Actor(h.Owner), new SearchProjectionRebuildRequest(h.Org, "OPERATOR_REQUEST"), Guid.CreateVersion7(), Ct));
        Assert.Equal(403, denied.HttpStatusCode);
        AllowAdministerSecurity(h, h.Owner);
        Assert.Equal(422, (await Assert.ThrowsAsync<AuthorityException>(() => h.Rebuild.RebuildAsync(Actor(h.Owner), new SearchProjectionRebuildRequest(h.Org, "lower case"), Guid.CreateVersion7(), Ct))).HttpStatusCode);
        Assert.Equal(SearchProjectionStatuses.Active, h.Projection.ProjectionStatusName);
    }

    [Fact]
    public async Task TheBuilderPlaneIsRefusedWhenTheIndexIsNotMaintained()
    {
        using var h = new Harness();
        h.Options.Mode = SearchIndexMode.Disabled;
        var ex = await Assert.ThrowsAsync<AuthorityException>(() => h.Builder.WorkAsync(Guid.CreateVersion7(), Ct));
        Assert.Equal((AuthorityErrorCodes.SearchIndexNotPresent, 404), (ex.ErrorCode, ex.HttpStatusCode));
    }

    [Fact]
    public async Task BatchesAreBoundedAndEachRecordAppearsOnce()
    {
        using var h = new Harness();
        var record = (await PreexistingRecordsAsync(h, 1)).Single();
        Assert.Equal(422, (await Assert.ThrowsAsync<AuthorityException>(() => BackfillAsync(h))).HttpStatusCode);
        Assert.Equal(422, (await Assert.ThrowsAsync<AuthorityException>(() => BackfillAsync(h, Item(h, record), Item(h, record)))).HttpStatusCode);
        Assert.Equal(422, (await Assert.ThrowsAsync<AuthorityException>(() => BackfillAsync(h, Enumerable.Range(0, 51).Select(_ => Item(h, record) with { FormRecordId = Guid.CreateVersion7() }).ToArray()))).HttpStatusCode);
    }
}
