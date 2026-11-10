using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.SearchIndex;
using AuthorizationAuthority.Domain.Features.SearchIndex;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// C-141 (UF-25, U-1b) unit tests: the stored per-field item counts (derivation, seal check, cheap and deep sweeps, derivation
/// upgrade) and the share-only readability rule (RQ-1 differential against the R1 §E.3 rule, RQ-2 invariance under hidden content,
/// RQ-4 fail-closed cases). The SQL that reads the candidates from the caller's grants is proven on the Docker stack
/// (IndexedSearchShareOnlyItemChannelTests); here a model of the same relations stands in for it.
/// </summary>
public sealed class ShareOnlyReadabilityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- Stored counts: derivation (W1), S-B, S-A, S-C, upgrade --------------------------------------------------------------

    [Fact]
    public async Task ACommitWritesVersion3EntriesWithTheItemCountOfEveryDerivedField()
    {
        using var h = new SearchIndexTests.Harness();
        var approvals = await h.ApproveStandardFieldsAsync();
        var record = h.Record();
        await h.WriteAsync(record);

        var entry = h.Entry(record);
        Assert.Equal(3, entry.EntryDerivationVersion);
        var expected = new Dictionary<Guid, int>
        {
            [h.StringField] = 1, [h.IntegerField] = 1, [h.OptionField] = 2,
            [h.EncryptedField] = 0, // an encrypted value is an anomaly, never a posting
            [h.GroupField] = 0, // repeating-group occurrences are not indexed
        };
        for (var i = 0; i < entry.DerivedFieldSearchApprovalIds.Length; i++)
        {
            var field = approvals.Single(a => a.FieldSearchApprovalId == entry.DerivedFieldSearchApprovalIds[i]).FieldDefinitionId;
            Assert.Equal(expected[field], entry.DerivedFieldItemCounts![i]);
        }

        await using var scope = await h.Store.BeginScopeAsync(Ct);
        Assert.Equal((SweepUnitOutcome.Pass, "VALID"), await h.Sweep.VerifyEntryAsync(scope, entry, h.Store.Approvals.ToDictionary(a => a.FieldSearchApprovalId), Ct));
    }

    [Fact]
    public async Task AChangedItemCountFailsTheKeyedSealCheckTheCheapPassAndTheDeepComparison()
    {
        using var h = new SearchIndexTests.Harness();
        await h.ApproveStandardFieldsAsync();
        var record = h.Record();
        await h.WriteAsync(record);
        var entry = h.Entry(record);
        var stored = entry.DerivedFieldItemCounts!.ToArray();

        // One count raised, the largest count lowered, and one count dropped (misaligned with the approvals).
        foreach (var change in new Func<int[], int[]>[] { c => [.. c.Select((v, i) => i == 0 ? v + 1 : v)], c => [.. c.Select((v, i) => i == Array.IndexOf(c, c.Max()) ? v - 1 : v)], c => [.. c.Skip(1)] })
        {
            entry.DerivedFieldItemCounts = change(stored);
            await using (var scope = await h.Store.BeginScopeAsync(Ct))
            {
                var (outcome, code) = await h.Sweep.VerifyEntryAsync(scope, entry, h.Store.Approvals.ToDictionary(a => a.FieldSearchApprovalId), Ct);
                Assert.Equal((SweepUnitOutcome.Fail, "ENTRY_FIELD_ITEM_COUNT_MISMATCH"), (outcome, code));
            }

            var index = await h.Index.GetStoredRecordIndexAsync(h.Org, record, Ct);
            Assert.Equal((SweepUnitOutcome.Fail, "FIELD_ITEM_COUNT_MISMATCH"), SearchIndexBuilderService.CompareWithStored(index!, h.Head(record), h.Accepted[record], h.Store.Approvals.ToDictionary(a => a.FieldSearchApprovalId)));
            Assert.Equal(1, (await h.Index.RunCheapChecksAsync(h.Org, Ct)).EntriesWithFieldItemCountMismatch);
        }

        entry.DerivedFieldItemCounts = stored;
        Assert.True((await h.Index.RunCheapChecksAsync(h.Org, Ct)).Clean);
        entry.DerivedFieldItemCounts = [stored[0] + 1, .. stored.Skip(1)];
        var cheap = await h.Sweep.RunCheapPassAsync(h.Org, Ct);
        Assert.Equal((SweepUnitOutcome.Fail, "ENTRY_FIELD_ITEM_COUNT_MISMATCH"), (cheap.Outcome, cheap.FindingCode));
        Assert.Equal(SearchProjectionStatuses.Quarantined, h.Projection.ProjectionStatusName);
    }

    [Fact]
    public async Task TheDerivationUpgradeReturnsCompleteCoveragesToBuildingAndAVersion2EntryWithCountsIsAFinding()
    {
        using var h = new SearchIndexTests.Harness();
        await h.ApproveStandardFieldsAsync();
        var record = h.Record();
        await h.WriteAsync(record);
        foreach (var coverage in h.Index.Coverages)
        {
            coverage.CoverageStateName = SearchCoverageStates.Complete;
            coverage.CoverageCompletedOn = h.Clock.Now;
        }

        // An entry of the image before C-141: version 2, no counts.
        var entry = h.Entry(record);
        var counts = entry.DerivedFieldItemCounts;
        entry.EntryDerivationVersion = 2;
        entry.DerivedFieldItemCounts = null;
        var journal = h.Store.Journal.Count;
        var pass = await h.Sweep.RunCheapPassAsync(h.Org, Ct);
        Assert.Equal(SweepUnitOutcome.Pass, pass.Outcome);
        Assert.All(h.Index.Coverages, c => Assert.Equal(SearchCoverageStates.Building, c.CoverageStateName));
        Assert.Contains(h.Store.Journal.Skip(journal), j => j.JournalEventKind == "SearchIndexDerivationUpgradeStarted");
        Assert.Contains(h.Alerts.Raised, a => a.AlertKind == "SEARCH_INDEX_DERIVATION_UPGRADE");
        // Not ready for the builder: a version 2 entry is re-derived, never accepted as indexed.
        Assert.False(SearchIndexBuilderService.IsIndexedAtHead(entry, h.Head(record), entry.DerivedFieldSearchApprovalIds.ToHashSet()));

        // A version 2 entry that carries counts is not an older image's entry: a finding.
        entry.DerivedFieldItemCounts = counts;
        var finding = await h.Sweep.RunCheapPassAsync(h.Org, Ct);
        Assert.Equal((SweepUnitOutcome.Fail, "ENTRY_FIELD_ITEM_COUNT_MISMATCH"), (finding.Outcome, finding.FindingCode));
    }

    [Fact]
    public void TheCountHelpersAlignWithTheApprovalsAndRefuseAmbiguity()
    {
        Guid a = Guid.CreateVersion7(), b = Guid.CreateVersion7(), c = Guid.CreateVersion7();
        Assert.Equal([2, 0, 1], SearchFieldItemCounts.Derive([a, b, c], [a, c, a]));
        Assert.True(SearchFieldItemCounts.TryGet([a, b], [3, 0], b, out var count) && count == 0);
        Assert.False(SearchFieldItemCounts.TryGet([a, b], [3], b, out _));
        Assert.False(SearchFieldItemCounts.TryGet([a, a], [1, 1], a, out _));
        Assert.False(SearchFieldItemCounts.TryGet([a], [-1], a, out _));
        Assert.False(SearchFieldItemCounts.TryGet([a], null, a, out _));
        Assert.False(SearchFieldItemCounts.TryGet([a], [1], c, out _));
        var entry = new SearchRecordEntry { EntryDerivationVersion = 3, DerivedFieldSearchApprovalIds = [a, b], DerivedFieldItemCounts = [1, 0] };
        Assert.True(SearchFieldItemCounts.MatchPostings(entry, [a]));
        Assert.False(SearchFieldItemCounts.MatchPostings(entry, [a, b]));
        entry.DerivedFieldItemCounts = null;
        Assert.False(SearchFieldItemCounts.WellFormed(entry));
        entry.EntryDerivationVersion = 2;
        Assert.True(SearchFieldItemCounts.WellFormed(entry));
    }

    // ---- RQ-4: the readability rule fails closed ------------------------------------------------------------------------------

    [Fact]
    public void MissingStaleOrContradictoryCountMetadataIsRefusedBeforeAnyCandidateIsRead()
    {
        Guid field = Guid.CreateVersion7(), approval = Guid.CreateVersion7(), other = Guid.CreateVersion7(), record = Guid.CreateVersion7();
        var predicate = new Dictionary<Guid, Guid> { [field] = approval };
        ShareOnlyDecision? Validate(int? version, Guid[]? approvals, int[]? counts) => ShareOnlyReadability.ValidateCounts([new ShareOnlyRecordCounts(record, version, approvals, counts)], predicate);

        Assert.Null(Validate(3, [approval], [2]));
        Assert.Equal((ShareOnlyOutcome.NotReady, "SCOPE_ENTRY_FIELD_ITEM_COUNTS_PENDING_UPGRADE"), Pair(Validate(2, [approval], null)));
        Assert.Equal((ShareOnlyOutcome.IndexFinding, "SCOPE_ENTRY_FIELD_ITEM_COUNTS_UNEXPECTED"), Pair(Validate(2, [approval], [2])));
        Assert.Equal((ShareOnlyOutcome.IndexFinding, "SCOPE_ENTRY_DERIVATION_OUTDATED"), Pair(Validate(1, [approval], null)));
        Assert.Equal((ShareOnlyOutcome.IndexFinding, "SCOPE_CONTENTFUL_RECORD_WITHOUT_ENTRY"), Pair(Validate(null, null, null)));
        Assert.Equal((ShareOnlyOutcome.IndexFinding, "SCOPE_ENTRY_FIELD_ITEM_COUNTS_INVALID"), Pair(Validate(3, [approval], null)));
        Assert.Equal((ShareOnlyOutcome.IndexFinding, "SCOPE_ENTRY_FIELD_ITEM_COUNTS_INVALID"), Pair(Validate(3, [approval, other], [1])));
        Assert.Equal((ShareOnlyOutcome.IndexFinding, "SCOPE_ENTRY_FIELD_ITEM_COUNTS_INVALID"), Pair(Validate(3, [approval], [-1])));
        Assert.Equal((ShareOnlyOutcome.IndexFinding, "SCOPE_ENTRY_FIELD_ITEM_COUNT_MISSING"), Pair(Validate(3, [other], [1])));

        // A finding anywhere wins over not-ready elsewhere (fail closed with the stronger outcome).
        var mixed = ShareOnlyReadability.ValidateCounts([new ShareOnlyRecordCounts(record, 2, [approval], null), new ShareOnlyRecordCounts(Guid.CreateVersion7(), 3, [approval], null)], predicate);
        Assert.Equal(ShareOnlyOutcome.IndexFinding, mixed!.Outcome);
    }

    [Fact]
    public void TheDecisionCountsDistinctItemsAcrossPathsAndRefusesImpossibleCandidates()
    {
        Guid field = Guid.CreateVersion7(), approval = Guid.CreateVersion7(), r1 = Guid.CreateVersion7(), r2 = Guid.CreateVersion7(), r3 = Guid.CreateVersion7();
        Guid i1 = Guid.CreateVersion7(), i2 = Guid.CreateVersion7();
        var predicate = new Dictionary<Guid, Guid> { [field] = approval };
        ShareOnlyRecordCounts R(Guid id, int total) => new(id, 3, [approval], [total]);
        ShareReadableItem I(Guid record, Guid item) => new(record, field, item, approval);

        // r1: two items, both reachable, i1 through two paths (direct and team) — counted once; r2: one of two readable; r3: empty field.
        var decision = ShareOnlyReadability.Decide([R(r1, 2), R(r2, 2), R(r3, 0)], predicate, [I(r1, i1), I(r1, i1), I(r1, i2), I(r2, Guid.CreateVersion7())]);
        Assert.Equal(ShareOnlyOutcome.Decided, decision.Outcome);
        Assert.Equal(new[] { r1, r3 }.Order(), decision.Evaluable.Order());
        Assert.Equal(2, decision.ConfirmedItemsByRecord[r1].Count);

        Assert.Equal("SCOPE_READABLE_ITEMS_EXCEED_COUNT", ShareOnlyReadability.Decide([R(r1, 1)], predicate, [I(r1, i1), I(r1, i2)]).FindingCode);
        Assert.Equal("SCOPE_CANDIDATE_OUTSIDE_SCOPE", ShareOnlyReadability.Decide([R(r1, 1)], predicate, [I(r2, i1)]).FindingCode);
        Assert.Equal("SCOPE_CANDIDATE_OUTSIDE_SCOPE", ShareOnlyReadability.Decide([R(r1, 1)], predicate, [new ShareReadableItem(r1, field, i1, Guid.CreateVersion7())]).FindingCode);
        Assert.Equal("SCOPE_CANDIDATE_OUTSIDE_SCOPE", ShareOnlyReadability.Decide([R(r1, 1)], predicate, [new ShareReadableItem(r1, Guid.CreateVersion7(), i1, approval)]).FindingCode);
    }

    [Fact]
    public void ABoundShareOnlyRecordMustHaveExactlyItsCountOfPostingsAllConfirmedReadable()
    {
        Guid field = Guid.CreateVersion7(), approval = Guid.CreateVersion7(), record = Guid.CreateVersion7();
        Guid i1 = Guid.CreateVersion7(), i2 = Guid.CreateVersion7();
        var predicate = new Dictionary<Guid, Guid> { [field] = approval };
        SearchRecordBindingData Data(int[] counts, params Guid[] items) => new(
            new SearchRecordEntry { FormRecordId = record, EntryDerivationVersion = 3, DerivedFieldSearchApprovalIds = [approval], DerivedFieldItemCounts = counts },
            [.. items.Select(i => new SearchOptionPosting { FormRecordId = record, FieldDefinitionId = field, ManifestItemId = i, FieldSearchApprovalId = approval })], [], 1, null, [], false, null, null, null, []);

        Assert.Null(ShareOnlyReadability.VerifyBoundRecord(Data([2], i1, i2), predicate, new HashSet<Guid> { i1, i2 }));
        Assert.Equal("MATCH_FIELD_ITEM_COUNT_MISMATCH", ShareOnlyReadability.VerifyBoundRecord(Data([1], i1, i2), predicate, new HashSet<Guid> { i1, i2 }));
        Assert.Equal("MATCH_PREDICATE_ITEM_NOT_READABLE", ShareOnlyReadability.VerifyBoundRecord(Data([2], i1, i2), predicate, new HashSet<Guid> { i1 }));
        Assert.Equal("MATCH_PREDICATE_ITEM_NOT_READABLE", ShareOnlyReadability.VerifyBoundRecord(Data([1], i1), predicate, null));
        Assert.Equal("MATCH_FIELD_ITEM_COUNT_MISSING", ShareOnlyReadability.VerifyBoundRecord(Data([]), predicate, new HashSet<Guid>()));
    }

    // ---- RQ-1 (differential) and RQ-2 (invariance), over generated configurations ----------------------------------------------

    /// <summary>
    /// RQ-1: over generated organizations (direct, team and overlapping shares, several shares per record, owned and shared records,
    /// single and multi-select fields, partly shared fields, empty fields, child records without the parent's shares, and items of
    /// non-predicate fields), the evaluable share-only scope of the C-141 rule equals the R1 §E.3 rule on every configuration.
    /// </summary>
    [Fact]
    public void RQ1TheNewRuleDecidesExactlyTheR1Scope()
    {
        for (var seed = 0; seed < 600; seed++)
        {
            var model = Model.Generate(new Random(seed));
            foreach (var predicate in model.PredicateChoices())
            {
                var expected = model.OldRuleEvaluable(predicate);
                var (decision, _) = model.NewRule(predicate);
                Assert.True(expected.SetEquals(decision.Evaluable), $"seed {seed}: R1 {expected.Count} vs C-141 {decision.Evaluable.Count}");
            }
        }
    }

    /// <summary>
    /// RQ-2: adding, removing or changing items the caller cannot read (in any share-only record, on predicate or other fields)
    /// never changes the number of item checks, the item-limit decision or the decision on any record whose readable items did
    /// not change. The R1 rule's check count is reported alongside: it moves with the hidden content (the UF-25 defect).
    /// </summary>
    [Fact]
    public void RQ2HiddenContentNeverChangesTheCheckCountOrTheLimitDecision()
    {
        var oldRuleMoved = 0;
        for (var seed = 0; seed < 400; seed++)
        {
            var random = new Random(seed);
            var model = Model.Generate(random);
            var predicate = model.PredicateChoices().First();
            var (before, checksBefore) = model.NewRule(predicate);
            var oldChecksBefore = model.OldRuleChecks(predicate);
            var touched = model.AddHiddenItems(random, predicate);
            var (after, checksAfter) = model.NewRule(predicate);
            Assert.Equal(checksBefore, checksAfter);
            foreach (var limit in new[] { 0, 1, 2, checksBefore, checksBefore + 1 })
            {
                Assert.Equal(checksBefore > limit, checksAfter > limit);
            }

            Assert.Equal(before.Evaluable.Except(touched).Order(), after.Evaluable.Except(touched).Order());
            Assert.DoesNotContain(touched, r => after.Evaluable.Contains(r) && !before.Evaluable.Contains(r));
            oldRuleMoved += model.OldRuleChecks(predicate) != oldChecksBefore ? 1 : 0;
        }

        Assert.True(oldRuleMoved > 0, "the generator must produce hidden content that the R1 rule would have checked");
    }

    private static readonly string[] Roles = ["reader", "contributor", "manager"];

    private static (ShareOnlyOutcome, string?) Pair(ShareOnlyDecision? d) => (d!.Outcome, d.FindingCode);

    /// <summary>
    /// A small organization: the caller, two teams (the caller is a member of the first), records owned by the caller or by
    /// someone else, fields, items, and shares carrying field items to the caller directly, to a team, or to an unrelated user.
    /// The relations are those of authority.schema.zed, as the reader's SQL and SpiceDB use them.
    /// </summary>
    private sealed class Model
    {
        private readonly Guid caller = Guid.CreateVersion7();
        private readonly Guid memberTeam = Guid.CreateVersion7();
        private readonly Guid otherTeam = Guid.CreateVersion7();
        private readonly Guid stranger = Guid.CreateVersion7();
        private readonly List<Guid> fields = [];
        private readonly Dictionary<Guid, Guid> approvalOf = [];
        private readonly List<(Guid Record, bool Owned)> records = [];
        private readonly Dictionary<(Guid Record, Guid Field), List<Guid>> items = [];
        private readonly List<(Guid Share, Guid Record, Guid? User, Guid? Team, string Role)> shares = [];
        private readonly List<(Guid Item, Guid Share)> sharedItems = [];

        public static Model Generate(Random random)
        {
            var m = new Model();
            for (var f = 0; f < random.Next(1, 5); f++)
            {
                var field = Guid.CreateVersion7();
                m.fields.Add(field);
                m.approvalOf[field] = Guid.CreateVersion7();
            }

            for (var r = 0; r < random.Next(1, 9); r++)
            {
                var record = Guid.CreateVersion7();
                m.records.Add((record, random.Next(4) == 0));
                foreach (var field in m.fields)
                {
                    m.items[(record, field)] = [.. Enumerable.Range(0, random.Next(0, 4)).Select(_ => Guid.CreateVersion7())];
                }

                // Items of a non-predicate field (or repeating group occurrences): shared or not, never postings of a predicate field.
                var unindexed = Guid.CreateVersion7();
                for (var s = 0; s < random.Next(0, 4); s++)
                {
                    var share = Guid.CreateVersion7();
                    var path = random.Next(4);
                    // path 0: direct to the caller; 1: to a team the caller belongs to; 2: to an unrelated user; 3: direct and team overlap.
                    m.shares.Add((share, record, path is 0 or 3 ? m.caller : path == 2 ? m.stranger : null, path == 1 ? m.memberTeam : null, Roles[random.Next(3)]));
                    if (path == 3)
                    {
                        // Overlap: the same items through a direct share and a team share of the same record.
                        var teamShare = Guid.CreateVersion7();
                        m.shares.Add((teamShare, record, null, m.memberTeam, "reader"));
                        foreach (var item in m.items.Where(kv => kv.Key.Record == record).SelectMany(kv => kv.Value).Where(_ => random.Next(2) == 0))
                        {
                            m.sharedItems.Add((item, share));
                            m.sharedItems.Add((item, teamShare));
                        }
                    }
                    else
                    {
                        if (random.Next(5) == 0)
                        {
                            m.shares[^1] = (share, record, null, m.otherTeam, "reader");
                        }

                        foreach (var item in m.items.Where(kv => kv.Key.Record == record).SelectMany(kv => kv.Value).Where(_ => random.Next(2) == 0))
                        {
                            m.sharedItems.Add((item, share));
                        }
                    }

                    if (random.Next(3) == 0)
                    {
                        m.sharedItems.Add((unindexed, share));
                    }
                }
            }

            return m;
        }

        public IEnumerable<List<Guid>> PredicateChoices()
        {
            yield return [fields[0]];
            if (fields.Count > 1)
            {
                yield return [fields[0], fields[^1]];
                yield return [.. fields];
            }
        }

        /// <summary>authority.schema.zed for a share-only record: field_item#read = shared_via->read; read = reader + contributor + manager; team participant = member.</summary>
        private bool Readable(Guid item) => sharedItems.Any(si => si.Item == item && shares.Any(s => s.Share == si.Share && (s.User == caller || s.Team == memberTeam)));

        private IEnumerable<Guid> ShareOnly => records.Where(r => !r.Owned).Select(r => r.Record);

        public HashSet<Guid> OldRuleEvaluable(List<Guid> predicate) =>
            [.. ShareOnly.Where(r => predicate.All(f => items[(r, f)].All(Readable)))];

        public int OldRuleChecks(List<Guid> predicate) => ShareOnly.Sum(r => predicate.Sum(f => items[(r, f)].Count));

        /// <summary>The C-141 rule end to end: candidates from the caller's grants (the reader's relations), the SpiceDB confirmation, counts, decision.</summary>
        public (ShareOnlyDecision Decision, int Checks) NewRule(List<Guid> predicate)
        {
            var scope = ShareOnly.ToHashSet();
            var callerShares = shares.Where(s => s.User == caller || s.Team == memberTeam).ToList();
            var candidates = callerShares.Where(s => scope.Contains(s.Record))
                .SelectMany(s => sharedItems.Where(si => si.Share == s.Share).Select(si => (s.Record, si.Item))).Distinct()
                .SelectMany(c => predicate.Where(f => items[(c.Record, f)].Contains(c.Item)).Select(f => new ShareReadableItem(c.Record, f, c.Item, approvalOf[f])))
                .ToList();
            Assert.All(candidates, c => Assert.True(Readable(c.ManifestItemId), "a candidate the projection would deny"));
            var counts = scope.Select(r => new ShareOnlyRecordCounts(r, 3, [.. fields.Select(f => approvalOf[f])], [.. fields.Select(f => items[(r, f)].Count)])).ToList();
            var byField = predicate.ToDictionary(f => f, f => approvalOf[f]);
            Assert.Null(ShareOnlyReadability.ValidateCounts(counts, byField));
            return (ShareOnlyReadability.Decide(counts, byField, candidates), candidates.Count);
        }

        /// <summary>Adds items the caller cannot read (unshared, or shared only with the stranger) to random share-only records. Returns the records touched.</summary>
        public HashSet<Guid> AddHiddenItems(Random random, List<Guid> predicate)
        {
            var touched = new HashSet<Guid>();
            foreach (var record in ShareOnly.Where(_ => random.Next(2) == 0).ToList())
            {
                var field = fields[random.Next(fields.Count)];
                for (var k = 0; k < random.Next(1, 30); k++)
                {
                    var item = Guid.CreateVersion7();
                    items[(record, field)].Add(item);
                    if (random.Next(3) == 0)
                    {
                        var share = Guid.CreateVersion7();
                        shares.Add((share, record, stranger, null, "reader"));
                        sharedItems.Add((item, share));
                    }
                }

                if (predicate.Contains(field))
                {
                    touched.Add(record);
                }
            }

            return touched;
        }
    }
}
