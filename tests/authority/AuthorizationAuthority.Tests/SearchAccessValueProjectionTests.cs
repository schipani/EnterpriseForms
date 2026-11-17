using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.SearchAccess;
using AuthorizationAuthority.Domain.Features.Journal;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// IX-5b (C-148) unit tests of the dark B2 value projection: value rows per reading principal from accepted searchable values,
/// readiness over both authorization and content revisions and the coverage set, content-stream integrity, revocation and
/// membership behaviour, verification and the internal multi-condition evaluation (in-memory store; the stack covers PostgreSQL).
/// </summary>
public sealed partial class SearchAccessProjectionTests
{
    private static readonly Guid FormId = Guid.CreateVersion7();

    private sealed record Fixture(World W, Guid Alice, Guid Bob, Guid Carol, Guid Team, Guid Record, Guid Share, Guid Count, Guid Status, Guid Tag, Guid CountApproval, Guid StatusApproval, Guid TagApproval, Guid CountItem, Guid StatusItem, Guid TagItem, Guid Open);

    private static SearchablePosting Value(Fixture f, Guid field, Guid approval, Guid item, string type, long? integer = null, Guid? option = null, string? text = null) =>
        new(FormId, field, approval, f.Record, item, Guid.CreateVersion7(), type, StringValue: text, IntegerValue: integer, FieldOptionId: option);

    private static async Task<Fixture> FixtureAsync()
    {
        var w = new World();
        Guid alice = Guid.NewGuid(), bob = Guid.NewGuid(), carol = Guid.NewGuid(), team = Guid.NewGuid(), record = Guid.NewGuid(), share = Guid.NewGuid();
        Guid count = Guid.NewGuid(), status = Guid.NewGuid(), tag = Guid.NewGuid(), countApproval = Guid.NewGuid(), statusApproval = Guid.NewGuid(), tagApproval = Guid.NewGuid();
        Guid countItem = Guid.NewGuid(), statusItem = Guid.NewGuid(), tagItem = Guid.NewGuid(), open = Guid.NewGuid();
        var f = new Fixture(w, alice, bob, carol, team, record, share, count, status, tag, countApproval, statusApproval, tagApproval, countItem, statusItem, tagItem, open);
        w.Store.SearchAccess.Coverages.AddRange([new SearchableCoverage(countApproval, count, FormId), new SearchableCoverage(statusApproval, status, FormId), new SearchableCoverage(tagApproval, tag, FormId)]);
        w.Store.SearchAccess.Searchable.AddRange([Value(f, count, countApproval, countItem, "Integer", integer: 5), Value(f, status, statusApproval, statusItem, "Option", option: open), Value(f, tag, tagApproval, tagItem, "String", text: "urgent")]);
        foreach (var (approval, n) in new[] { (countApproval, 1), (statusApproval, 1), (tagApproval, 1) })
        {
            w.Store.SearchAccess.ItemCounts[(record, approval)] = n;
        }

        w.Store.SearchAccess.Tier1Status = "Active";
        // Alice owns; Bob reads the count directly; the team (Carol) reads the status.
        w.Touch(Owner(record, alice), Member(team, carol), SharedVia(record, share), Reader(share, bob), Item(countItem, share));
        var teamShare = Guid.NewGuid();
        w.Touch(SharedVia(record, teamShare), ReaderTeam(teamShare, team), Item(statusItem, teamShare));
        w.CommitContent(record);
        await w.Service.RebuildAsync(Org, "INITIAL_BUILD", Guid.NewGuid(), Ct);
        return f;
    }

    private static HashSet<(string, Guid, Guid)> Rows(World w) => w.Store.SearchAccess.ValuePostings.Select(v => (v.AccessPrincipalTypeName, v.AccessPrincipalEntityId, v.ManifestItemId)).ToHashSet();

    [Fact]
    public async Task ValueRowsExistOncePerReadingPrincipalAndTeamsAreNeverExpanded()
    {
        var f = await FixtureAsync();
        Assert.Equal(SearchAccessStatusNames.Ready, await f.W.StatusAsync());
        Assert.True(Rows(f.W).SetEquals([
            ("user", f.Alice, f.CountItem), ("user", f.Alice, f.StatusItem), ("user", f.Alice, f.TagItem), // the owner reads every item
            ("user", f.Bob, f.CountItem), // the direct share's item only
            ("team", f.Team, f.StatusItem), // the team principal, not Carol
        ]));
        Assert.DoesNotContain(f.W.Store.SearchAccess.ValuePostings, v => v.AccessPrincipalEntityId == f.Carol);
        Assert.True((await f.W.Service.VerifyAsync(Org, Guid.NewGuid(), Ct)).Clean);
    }

    [Fact]
    public async Task AValueWithoutACompletePublicOrInternalCoverageNeverEntersARow()
    {
        var f = await FixtureAsync();
        // A posting of a field without a Complete Public/Internal coverage (for example a Confidential or encrypted field, which
        // Tier 1 never posts and the store's query excludes): not searchable, so not projected.
        var confidential = Guid.NewGuid();
        f.W.Store.SearchAccess.Searchable.Add(Value(f, Guid.NewGuid(), confidential, Guid.NewGuid(), "String", text: "secret"));
        f.W.CommitContent(f.Record);
        await f.W.Service.CatchUpAsync(Org, 100, Ct);
        Assert.DoesNotContain(f.W.Store.SearchAccess.ValuePostings, v => v.FieldSearchApprovalId == confidential);
        Assert.Equal(SearchAccessStatusNames.Ready, await f.W.StatusAsync());
    }

    [Fact]
    public async Task ReadinessNeedsBothRevisionsTheCoverageSetAndAServableTier1()
    {
        var f = await FixtureAsync();
        // A content commit (a changed value) closes readiness until consumed; then the new value is projected.
        f.W.Store.SearchAccess.Searchable.RemoveAll(s => s.ManifestItemId == f.CountItem);
        f.W.Store.SearchAccess.Searchable.Add(Value(f, f.Count, f.CountApproval, f.CountItem, "Integer", integer: 9));
        f.W.CommitContent(f.Record);
        var status = await f.W.Service.StatusAsync(Org, Ct);
        Assert.Equal(SearchAccessStatusNames.Building, status.StatusName);
        Assert.True(status.ConsumedContentIntegrityRevision < status.CurrentContentIntegrityRevision);
        await f.W.Service.CatchUpAsync(Org, 100, Ct);
        Assert.Equal(SearchAccessStatusNames.Ready, await f.W.StatusAsync());
        Assert.All(f.W.Store.SearchAccess.ValuePostings.Where(v => v.ManifestItemId == f.CountItem), v => Assert.Equal(9, v.IntegerValue));

        // A coverage withdrawal (no content event) closes readiness too; consuming it removes the field's rows.
        f.W.Store.SearchAccess.Coverages.RemoveAll(c => c.FieldSearchApprovalId == f.TagApproval);
        var withdrawn = await f.W.Service.StatusAsync(Org, Ct);
        Assert.Equal(SearchAccessStatusNames.Building, withdrawn.StatusName);
        Assert.False(withdrawn.CoverageCurrent);
        await f.W.Service.CatchUpAsync(Org, 100, Ct);
        Assert.Equal(SearchAccessStatusNames.Ready, await f.W.StatusAsync());
        Assert.DoesNotContain(f.W.Store.SearchAccess.ValuePostings, v => v.FieldSearchApprovalId == f.TagApproval);

        // Tier 1 not servable: never ready.
        f.W.Store.SearchAccess.Tier1Status = "ReconciliationRequired";
        Assert.Equal(SearchAccessStatusNames.Building, await f.W.StatusAsync());
        f.W.Store.SearchAccess.Tier1Status = "Active";
        Assert.True((await f.W.Service.VerifyAsync(Org, Guid.NewGuid(), Ct)).Clean);
    }

    [Fact]
    public async Task ARevocationIsNeverReadyFromOldRowsAndAMembershipChangeRewritesNoValueRow()
    {
        var f = await FixtureAsync();
        f.W.Delete(Reader(f.Share, f.Bob));
        Assert.Equal(SearchAccessStatusNames.Building, await f.W.StatusAsync()); // closed at once, before any maintenance
        Assert.Contains(Rows(f.W), r => r.Item2 == f.Bob);
        await f.W.Service.CatchUpAsync(Org, 100, Ct);
        Assert.Equal(SearchAccessStatusNames.Ready, await f.W.StatusAsync());
        Assert.DoesNotContain(Rows(f.W), r => r.Item2 == f.Bob);

        var before = Rows(f.W);
        f.W.Delete(Member(f.Team, f.Carol));
        await f.W.Service.CatchUpAsync(Org, 100, Ct);
        Assert.True(before.SetEquals(Rows(f.W)));
        Assert.True((await f.W.Service.VerifyAsync(Org, Guid.NewGuid(), Ct)).Clean);
    }

    [Fact]
    public async Task OwnershipTransferMovesTheOwnersValueRows()
    {
        var f = await FixtureAsync();
        f.W.Commit((false, Owner(f.Record, f.Alice)), (true, OwnerTeam(f.Record, f.Team)));
        await f.W.Service.CatchUpAsync(Org, 100, Ct);
        Assert.DoesNotContain(Rows(f.W), r => r.Item2 == f.Alice);
        Assert.Contains(("team", f.Team, f.TagItem), Rows(f.W));
        Assert.True((await f.W.Service.VerifyAsync(Org, Guid.NewGuid(), Ct)).Clean);
    }

    [Theory]
    [InlineData("body", SearchAccessReasonCodes.JournalDigestMismatch)]
    [InlineData("chain", SearchAccessReasonCodes.JournalChainBreak)]
    [InlineData("gap", SearchAccessReasonCodes.JournalGap)]
    [InlineData("kind", SearchAccessReasonCodes.JournalRevisionMismatch)]
    public async Task ContentStreamTamperingQuarantines(string tamper, string expected)
    {
        var f = await FixtureAsync();
        f.W.CommitContent(f.Record);
        var entry = f.W.Store.Journal.Single(e => e.JournalStreamName == JournalStreamNames.Content && e.JournalStreamRevision == f.W.State.ContentIntegrityRevision);
        switch (tamper)
        {
            case "body":
                entry.JournalEventBodyJson = entry.JournalEventBodyJson.Replace(f.Record.ToString("D"), Guid.NewGuid().ToString("D"), StringComparison.Ordinal);
                break;
            case "chain":
                entry.JournalPreviousDigest = new byte[32];
                break;
            case "gap":
                f.W.Store.Journal.Remove(entry);
                break;
            case "kind":
                entry.JournalEventKind = "SomethingElse";
                break;
        }

        Assert.Equal(SearchAccessStepOutcome.Quarantined, (await f.W.Service.CatchUpAsync(Org, 100, Ct)).Outcome);
        var status = await f.W.Service.StatusAsync(Org, Ct);
        Assert.Equal((SearchAccessStatusNames.Quarantined, expected), (status.StatusName, status.QuarantineReasonCode));
        Assert.NotEqual("COMPLETE", (await f.W.Service.VerifyJournalReplayAsync(Org, Ct)).OutcomeCode);
        await f.W.Service.RebuildAsync(Org, "OPERATOR_REBUILD", Guid.NewGuid(), Ct);
        Assert.Equal(SearchAccessStatusNames.Ready, await f.W.StatusAsync());
    }

    [Fact]
    public async Task AForgedValueRowIsDetectedAndKept()
    {
        var f = await FixtureAsync();
        var forged = Guid.NewGuid();
        f.W.Store.SearchAccess.ValuePostings.Add(new Domain.Features.SearchAccess.SearchAccessValuePosting { SearchAccessValuePostingId = Guid.NewGuid(), OrganizationId = Org, AccessPrincipalTypeName = "user", AccessPrincipalEntityId = forged, FormDefinitionId = FormId, FieldDefinitionId = f.Tag, FieldSearchApprovalId = f.TagApproval, FormRecordId = f.Record, ManifestItemId = f.TagItem, ValueTypeName = "String", NormalizedStringValue = "urgent" });
        Assert.Equal(SearchAccessReasonCodes.ValuesDiverged, (await f.W.Service.VerifyAsync(Org, Guid.NewGuid(), Ct)).OutcomeCode);
        Assert.Equal(SearchAccessStatusNames.Quarantined, await f.W.StatusAsync());
        Assert.Contains(f.W.Store.SearchAccess.ValuePostings, v => v.AccessPrincipalEntityId == forged);
    }

    [Fact]
    public async Task MultiConditionEvaluationHonoursPathsAndTheShareOnlyItemRule()
    {
        var f = await FixtureAsync();
        var countIs5 = new SearchAccessEvaluationCondition(f.Count, "Integer", MinimumInteger: 5, MaximumInteger: 5);
        var statusOpen = new SearchAccessEvaluationCondition(f.Status, "Option", FieldOptionId: f.Open);
        var tagUrgent = new SearchAccessEvaluationCondition(f.Tag, "String", StringValue: "urgent");
        async Task<IReadOnlyList<Guid>> As(Guid user, params SearchAccessEvaluationCondition[] conditions) => await f.W.Service.EvaluateForTestingAsync(Org, FormId, user, conditions, Ct);

        Assert.Equal([f.Record], await As(f.Alice, countIs5, statusOpen, tagUrgent)); // the owner: every field
        Assert.Equal([f.Record], await As(f.Bob, countIs5)); // the direct share's field
        Assert.Empty(await As(f.Bob, countIs5, statusOpen)); // the status is not Bob's
        Assert.Equal([f.Record], await As(f.Carol, statusOpen)); // through the team (P = Carol and her team)
        Assert.Empty(await As(f.Carol, tagUrgent));
        Assert.Empty(await As(f.Alice, new SearchAccessEvaluationCondition(f.Count, "Integer", MinimumInteger: 6)));

        // A share-only caller who reads only some items of a multi-item field cannot match on it (UF-25 rule).
        f.W.Store.SearchAccess.ItemCounts[(f.Record, f.CountApproval)] = 2;
        Assert.Empty(await As(f.Bob, countIs5));
        Assert.Equal([f.Record], await As(f.Alice, countIs5));
    }
}
