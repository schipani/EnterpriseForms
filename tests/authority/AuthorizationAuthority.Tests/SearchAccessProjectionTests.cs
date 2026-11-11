using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Journal;
using AuthorizationAuthority.Application.Features.SearchAccess;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SearchAccess;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// IX-5a (C-147) unit tests of the dark Tier 2 access projection: the derivation against the zed model, the journal verifier, and
/// the maintainer's checkpoint, quarantine, rebuild and replay behaviour (in-memory store; the stack tests cover PostgreSQL).
/// </summary>
public sealed partial class SearchAccessProjectionTests
{
    private static readonly Guid Org = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AccessEdge Owner(Guid record, Guid user) => new("form_record", record, "owner", "user", user, string.Empty);

    private static AccessEdge OwnerTeam(Guid record, Guid team) => new("form_record", record, "owner", "team", team, "participant");

    private static AccessEdge Parent(Guid child, Guid parent) => new("form_record", child, "parent", "form_record", parent, string.Empty);

    private static AccessEdge SharedVia(Guid record, Guid share) => new("form_record", record, "shared_via", "record_share", share, string.Empty);

    private static AccessEdge Reader(Guid share, Guid user) => new("record_share", share, "reader", "user", user, string.Empty);

    private static AccessEdge ReaderTeam(Guid share, Guid team) => new("record_share", share, "reader", "team", team, "participant");

    private static AccessEdge Item(Guid item, Guid share) => new("field_item", item, "shared_via", "record_share", share, string.Empty);

    private static AccessEdge Member(Guid team, Guid user) => new("team", team, "member", "user", user, string.Empty);

    private sealed class World
    {
        public InMemoryAuthorityStore Store { get; } = new();

        public OrganizationAuthorizationState State { get; }

        public SearchAccessProjectionService Service { get; }

        public TestProjection.RecordingAlerts Alerts { get; } = new();

        public World(int sampleSize = 0)
        {
            State = new OrganizationAuthorizationState { OrganizationId = Org };
            Store.Organizations.Add(State);
            var options = new SearchAccessOptions { Mode = SearchAccessMode.Dark, VerificationSampleSize = sampleSize };
            Service = new SearchAccessProjectionService(Store, TestProjection.Verifier(Store, new UnusedSpiceDb()), options, Alerts, new NoFaults(), TimeProvider.System);
        }

        /// <summary>One security command: canonical edges and the hash-chained journal event, as SecurityCommandService.ApplyPlan writes them.</summary>
        public void Commit(params (bool Touch, AccessEdge Edge)[] changes)
        {
            var revision = State.AuthorizationRevision + 1;
            var command = Guid.CreateVersion7();
            foreach (var (touch, e) in changes)
            {
                if (touch)
                {
                    Store.Edges.Add(new AuthorityRelationshipEdge { AuthorityRelationshipEdgeId = Guid.CreateVersion7(), OrganizationId = Org, ResourceTypeName = e.ResourceType, ResourceEntityId = e.ResourceId, RelationshipName = e.Relation, SubjectTypeName = e.SubjectType, SubjectEntityId = e.SubjectId, SubjectRelationName = e.SubjectRelation, CreatedBySecurityCommandId = command, RelationshipCreatedOn = DateTimeOffset.UtcNow });
                }
                else
                {
                    var edge = Store.Edges.Single(x => x.RemovedBySecurityCommandId is null && x.ResourceTypeName == e.ResourceType && x.ResourceEntityId == e.ResourceId && x.RelationshipName == e.Relation && x.SubjectTypeName == e.SubjectType && x.SubjectEntityId == e.SubjectId && x.SubjectRelationName == e.SubjectRelation);
                    edge.RemovedBySecurityCommandId = command;
                }
            }

            var body = new { security_command_id = command, command_kind = "Test", committed_authorization_revision = revision, relationship_delta = changes.Select(c => (c.Touch ? "touch " : "delete ") + c.Edge.Canonical).ToList() };
            Store.Journal.Add(JournalChain.Append(State, JournalStreamNames.Authorization, revision, "SecurityCommandCommitted", body, command, null, TimeProvider.System));
            State.AuthorizationRevision = revision;
        }

        /// <summary>IX-5b: one content-stream event naming a record (as ContentService journals a head commit).</summary>
        public void CommitContent(Guid record, string kind = "ContentHeadCommitted")
        {
            var revision = State.ContentIntegrityRevision + 1;
            typeof(OrganizationAuthorizationState).GetProperty(nameof(OrganizationAuthorizationState.ContentIntegrityRevision))!.SetValue(State, revision); // init-only in production (coordinator)
            Store.Journal.Add(JournalChain.Append(State, JournalStreamNames.Content, revision, kind, new { form_record_id = record, committed_content_revision = revision }, null, Guid.CreateVersion7(), TimeProvider.System));
        }

        public void Touch(params AccessEdge[] edges) => Commit(edges.Select(e => (true, e)).ToArray());

        public void Delete(params AccessEdge[] edges) => Commit(edges.Select(e => (false, e)).ToArray());

        public SearchAccessCheckpoint Checkpoint => Store.SearchAccess.Checkpoints.Single();

        public HashSet<string> RecordRows => Store.SearchAccess.RecordGrants.Select(SearchAccessDerivation.Key).ToHashSet();

        public HashSet<string> ItemRows => Store.SearchAccess.FieldItemGrants.Select(SearchAccessDerivation.Key).ToHashSet();

        public DerivedAccessRows FromCanonical()
        {
            var set = new AccessEdgeSet(Store.Edges.Where(e => e.RemovedBySecurityCommandId is null && AccessRelationshipDelta.IsMirrored(e.ResourceTypeName, e.RelationshipName)).Select(e => new AccessEdge(e.ResourceTypeName, e.ResourceEntityId, e.RelationshipName, e.SubjectTypeName, e.SubjectEntityId, e.SubjectRelationName)));
            return SearchAccessDerivation.Derive(Org, set, set.Edges.Where(e => e.ResourceType == "form_record").Select(e => e.ResourceId), State.AuthorizationRevision, 16);
        }

        public void AssertRowsEqualCanonical()
        {
            var expected = FromCanonical();
            Assert.True(expected.RecordGrants.Select(SearchAccessDerivation.Key).ToHashSet().SetEquals(RecordRows), "record rows differ from the canonical derivation");
            Assert.True(expected.FieldItemGrants.Select(SearchAccessDerivation.Key).ToHashSet().SetEquals(ItemRows), "item rows differ from the canonical derivation");
            Assert.Equal(expected.FieldItemGrants.Count, Store.SearchAccess.FieldItemGrants.Count);
        }

        public async Task<string> StatusAsync() => (await Service.StatusAsync(Org, Ct)).StatusName;
    }

    [Fact]
    public void DerivationMirrorsTheZedModelWithoutExpandingTeams()
    {
        Guid alice = Guid.NewGuid(), bob = Guid.NewGuid(), carol = Guid.NewGuid(), team = Guid.NewGuid(), parent = Guid.NewGuid(), child = Guid.NewGuid(), s1 = Guid.NewGuid(), s2 = Guid.NewGuid(), i1 = Guid.NewGuid(), i2 = Guid.NewGuid();
        var edges = new AccessEdgeSet([
            Owner(parent, alice), Parent(child, parent), SharedVia(parent, s1), SharedVia(parent, s2), Reader(s1, bob), ReaderTeam(s2, team), Reader(s2, bob),
            Item(i1, s1), Item(i1, s2), Item(i2, s2), Member(team, carol)]);
        var rows = SearchAccessDerivation.Derive(Org, edges, [parent, child], 7, 16);

        // Effective ownership: the parent's owner owns the child too (parent->effective_owner); the share does not reach the child.
        Assert.Contains(rows.RecordGrants, g => g.FormRecordId == child && g.AccessKindName == SearchAccessKinds.EffectiveOwner && g.AccessPrincipalEntityId == alice);
        Assert.DoesNotContain(rows.RecordGrants, g => g.FormRecordId == child && g.AccessKindName == SearchAccessKinds.ShareRecipient);
        // The team is a principal; its member is never expanded into rows (D-1).
        Assert.Contains(rows.FieldItemGrants, g => g.AccessPrincipalTypeName == "team" && g.AccessPrincipalEntityId == team && g.ManifestItemId == i2);
        Assert.DoesNotContain(rows.RecordGrants.Select(g => g.AccessPrincipalEntityId).Concat(rows.FieldItemGrants.Select(g => g.AccessPrincipalEntityId)), p => p == carol);
        // Bob reaches i1 by two shares: two path rows, one distinct item (INV-T2-13 is a query-time distinct union).
        var bobItems = rows.FieldItemGrants.Where(g => g.AccessPrincipalEntityId == bob).ToList();
        Assert.Equal(3, bobItems.Count);
        Assert.Equal(2, bobItems.Select(g => g.ManifestItemId).Distinct().Count());
        Assert.All(rows.RecordGrants, g => Assert.Equal(7, g.DerivedAuthorizationRevision));
    }

    [Fact]
    public void DerivationRefusesCyclesAndUnexpectedSubjects()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        Assert.Equal(SearchAccessReasonCodes.ParentChainTooDeep, Assert.Throws<SearchAccessIntegrityException>(() => SearchAccessDerivation.Derive(Org, new AccessEdgeSet([Parent(a, b), Parent(b, a)]), [a], 1, 16)).ReasonCode);
        var bad = new AccessEdgeSet([new AccessEdge("form_record", a, "owner", "team", b, string.Empty)]);
        Assert.Equal(SearchAccessReasonCodes.UnexpectedSubject, Assert.Throws<SearchAccessIntegrityException>(() => SearchAccessDerivation.Derive(Org, bad, [a], 1, 16)).ReasonCode);
    }

    [Theory]
    [InlineData("touch form_record:x#owner@user:y")]
    [InlineData("upsert form_record:4f8991b5-d3ff-83cf-9b73-c79275996c9b#owner@user:01a1c61a-0bdf-7968-9b06-1b3025b0ce9a")]
    [InlineData("touch form_record:4f8991b5-d3ff-83cf-9b73-c79275996c9b#owner")]
    public void MalformedDeltaEntriesFailClosed(string entry) =>
        Assert.Equal(SearchAccessReasonCodes.DeltaMalformed, Assert.Throws<SearchAccessIntegrityException>(() => AccessRelationshipDelta.Parse(entry)).ReasonCode);

    [Fact]
    public void DeltaParsingKeepsAccessEdgesOnly()
    {
        var owner = AccessRelationshipDelta.Parse("touch form_record:4f8991b5-d3ff-83cf-9b73-c79275996c9b#owner@team:01a1c61a-0bdf-7968-9b06-1b3025b0ce9a#participant");
        Assert.True(owner.IsTouch);
        Assert.Equal(new AccessEdge("form_record", Guid.Parse("4f8991b5-d3ff-83cf-9b73-c79275996c9b"), "owner", "team", Guid.Parse("01a1c61a-0bdf-7968-9b06-1b3025b0ce9a"), "participant"), owner.Edge);
        Assert.Null(AccessRelationshipDelta.Parse("touch organization:4f8991b5-d3ff-83cf-9b73-c79275996c9b#designer@user:01a1c61a-0bdf-7968-9b06-1b3025b0ce9a").Edge);
    }

    [Fact]
    public async Task CatchUpFromTheJournalEqualsTheCanonicalDerivationAndReadinessIsExact()
    {
        var w = new World();
        Guid alice = Guid.NewGuid(), bob = Guid.NewGuid(), team = Guid.NewGuid(), r1 = Guid.NewGuid(), r2 = Guid.NewGuid(), child = Guid.NewGuid(), s1 = Guid.NewGuid(), s2 = Guid.NewGuid(), i1 = Guid.NewGuid(), i2 = Guid.NewGuid();
        w.Touch(Owner(r1, alice));
        Assert.Equal(SearchAccessStatusNames.NotBuilt, await w.StatusAsync());
        await w.Service.RebuildAsync(Org, "INITIAL_BUILD", Guid.NewGuid(), Ct);
        Assert.Equal(SearchAccessStatusNames.Ready, await w.StatusAsync());
        w.AssertRowsEqualCanonical();

        w.Touch(OwnerTeam(r2, team), Member(team, bob));
        w.Touch(Parent(child, r1));
        w.Touch(SharedVia(r1, s1), Reader(s1, bob), Item(i1, s1), Item(i2, s1));
        w.Touch(SharedVia(r1, s2), ReaderTeam(s2, team), Item(i1, s2));
        Assert.Equal(SearchAccessStatusNames.Building, await w.StatusAsync()); // every event closes readiness until consumed
        var (outcome, advanced) = await w.Service.CatchUpAsync(Org, 100, Ct);
        Assert.Equal((SearchAccessStepOutcome.Idle, 4), (outcome, advanced));
        Assert.Equal(SearchAccessStatusNames.Ready, await w.StatusAsync());
        w.AssertRowsEqualCanonical();

        // Revocation: until consumed Tier 2 is not ready (so the stale row cannot be served); after it the row is gone.
        w.Delete(SharedVia(r1, s1), Reader(s1, bob), Item(i1, s1), Item(i2, s1));
        Assert.Equal(SearchAccessStatusNames.Building, await w.StatusAsync());
        Assert.Contains(w.Store.SearchAccess.FieldItemGrants, g => g.RecordShareId == s1);
        await w.Service.CatchUpAsync(Org, 100, Ct);
        Assert.Equal(SearchAccessStatusNames.Ready, await w.StatusAsync());
        Assert.DoesNotContain(w.Store.SearchAccess.FieldItemGrants, g => g.RecordShareId == s1);
        w.AssertRowsEqualCanonical();

        // Ownership transfer user -> team and child unlinking.
        w.Commit((false, Owner(r1, alice)), (true, OwnerTeam(r1, team)));
        w.Delete(Parent(child, r1));
        await w.Service.CatchUpAsync(Org, 100, Ct);
        w.AssertRowsEqualCanonical();
        Assert.DoesNotContain(w.Store.SearchAccess.RecordGrants, g => g.FormRecordId == child);
        Assert.Contains(w.Store.SearchAccess.RecordGrants, g => g.FormRecordId == r1 && g.AccessPrincipalEntityId == team && g.AccessKindName == SearchAccessKinds.EffectiveOwner);

        // Team membership changes write no rows (D-1).
        var before = (w.RecordRows.ToHashSet(), w.ItemRows.ToHashSet());
        w.Delete(Member(team, bob));
        await w.Service.CatchUpAsync(Org, 100, Ct);
        Assert.True(before.Item1.SetEquals(w.RecordRows) && before.Item2.SetEquals(w.ItemRows));
        Assert.Equal(w.State.AuthorizationRevision, w.Checkpoint.ConsumedAuthorizationRevision);
        Assert.True((await w.Service.VerifyAsync(Org, Guid.NewGuid(), Ct)).Clean);
        Assert.Equal("COMPLETE", (await w.Service.VerifyJournalReplayAsync(Org, Ct)).OutcomeCode);
    }

    public static TheoryData<string, string> Tampering => new()
    {
        { "body", SearchAccessReasonCodes.JournalDigestMismatch },
        { "chain", SearchAccessReasonCodes.JournalChainBreak },
        { "gap", SearchAccessReasonCodes.JournalGap },
        { "kind", SearchAccessReasonCodes.JournalRevisionMismatch },
        { "absent-delete", SearchAccessReasonCodes.MirrorDeleteAbsent },
    };

    [Theory]
    [MemberData(nameof(Tampering))]
    public async Task JournalTamperingQuarantinesTier2AndOnlyARebuildLeavesIt(string tamper, string expectedCode)
    {
        var w = new World();
        Guid alice = Guid.NewGuid(), bob = Guid.NewGuid(), r = Guid.NewGuid(), s = Guid.NewGuid(), i = Guid.NewGuid();
        w.Touch(Owner(r, alice));
        await w.Service.RebuildAsync(Org, "INITIAL_BUILD", Guid.NewGuid(), Ct);
        if (tamper == "absent-delete")
        {
            w.Delete(Owner(r, alice));
            w.Store.SearchAccess.Edges.Clear(); // the mirror lost the edge the next delete names
        }
        else
        {
            w.Touch(SharedVia(r, s), Reader(s, bob), Item(i, s));
            var entry = w.Store.Journal.Single(e => e.JournalStreamName == JournalStreamNames.Authorization && e.JournalStreamRevision == w.State.AuthorizationRevision);
            switch (tamper)
            {
                case "body":
                    entry.JournalEventBodyJson = entry.JournalEventBodyJson.Replace(bob.ToString("D"), Guid.NewGuid().ToString("D"), StringComparison.Ordinal);
                    break;
                case "chain":
                    entry.JournalPreviousDigest = new byte[32];
                    break;
                case "gap":
                    w.Store.Journal.Remove(entry);
                    break;
                case "kind":
                    entry.JournalEventKind = "SomethingElse";
                    break;
            }
        }

        var (outcome, _) = await w.Service.CatchUpAsync(Org, 10, Ct);
        Assert.Equal(SearchAccessStepOutcome.Quarantined, outcome);
        var status = await w.Service.StatusAsync(Org, Ct);
        Assert.Equal(SearchAccessStatusNames.Quarantined, status.StatusName);
        Assert.Equal(expectedCode, status.QuarantineReasonCode);
        Assert.Contains(w.Store.Journal, e => e.JournalEventKind == SearchAccessProjectionService.QuarantineEventKind);
        Assert.Contains(w.Alerts.Raised, a => a.AlertKind == "SEARCH_ACCESS_PROJECTION_QUARANTINED");
        Assert.Equal(SearchAccessStepOutcome.Quarantined, await w.Service.StepAsync(Org, Ct)); // never repaired by the maintainer
        Assert.Equal("QUARANTINED", (await w.Service.VerifyAsync(Org, Guid.NewGuid(), Ct)).OutcomeCode);

        if (tamper is not ("gap" or "absent-delete"))
        {
            Assert.NotEqual("COMPLETE", (await w.Service.VerifyJournalReplayAsync(Org, Ct)).OutcomeCode);
        }

        await w.Service.RebuildAsync(Org, "OPERATOR_REBUILD", Guid.NewGuid(), Ct);
        Assert.Equal(SearchAccessStatusNames.Ready, await w.StatusAsync());
        w.AssertRowsEqualCanonical();
        Assert.Contains(w.Store.Journal, e => e.JournalEventKind == SearchAccessProjectionService.RebuildEventKind);
    }

    [Fact]
    public async Task ALostCompareAndSetChangesNothingAndTheNextStepSucceeds()
    {
        var w = new World();
        Guid alice = Guid.NewGuid(), r = Guid.NewGuid();
        await w.Service.RebuildAsync(Org, "INITIAL_BUILD", Guid.NewGuid(), Ct);
        w.Touch(Owner(r, alice));
        var revision = w.Checkpoint.CheckpointRevision;
        w.Store.SearchAccess.LoseNextCompareAndSet = true;
        Assert.Equal(SearchAccessStepOutcome.Contended, await w.Service.StepAsync(Org, Ct));
        Assert.Equal(revision, w.Checkpoint.CheckpointRevision);
        Assert.Equal(0, w.Checkpoint.ConsumedAuthorizationRevision);
        // (In PostgreSQL the transaction rolls back its row changes; the in-memory contract keeps them, so only the checkpoint is asserted.)
        Assert.Equal(SearchAccessStepOutcome.Advanced, await w.Service.StepAsync(Org, Ct));
        Assert.Equal(1, w.Checkpoint.ConsumedAuthorizationRevision);
        Assert.Equal(revision + 1, w.Checkpoint.CheckpointRevision);
    }

    [Fact]
    public async Task ACheckpointAheadOfTheOrganizationIsQuarantinedAndANewerProtocolIsFenced()
    {
        var w = new World();
        w.Touch(Owner(Guid.NewGuid(), Guid.NewGuid()));
        await w.Service.RebuildAsync(Org, "INITIAL_BUILD", Guid.NewGuid(), Ct);
        w.State.AuthorizationRevision = 0; // a restored (older) organization state beneath a newer checkpoint
        Assert.Equal(SearchAccessStepOutcome.Quarantined, await w.Service.StepAsync(Org, Ct));
        Assert.Equal(SearchAccessReasonCodes.CheckpointAhead, w.Checkpoint.QuarantineReasonCode);

        var v = new World();
        await v.Service.RebuildAsync(Org, "INITIAL_BUILD", Guid.NewGuid(), Ct);
        v.Store.SearchAccess.Checkpoints.Single().AccessProjectionProtocolVersion = SearchAccessDerivation.ProtocolVersion + 1;
        v.Touch(Owner(Guid.NewGuid(), Guid.NewGuid()));
        Assert.Equal(SearchAccessStepOutcome.Fenced, await v.Service.StepAsync(Org, Ct));
        Assert.Equal(0, v.Checkpoint.ConsumedAuthorizationRevision);
        Assert.Empty(v.Store.SearchAccess.RecordGrants);
    }

    [Fact]
    public async Task VerificationQuarantinesDivergedRowsWithoutRepairingThem()
    {
        var w = new World();
        Guid alice = Guid.NewGuid(), r = Guid.NewGuid();
        w.Touch(Owner(r, alice));
        await w.Service.RebuildAsync(Org, "INITIAL_BUILD", Guid.NewGuid(), Ct);
        Assert.True((await w.Service.VerifyAsync(Org, Guid.NewGuid(), Ct)).Clean);
        w.Store.SearchAccess.RecordGrants.Add(new SearchAccessRecordGrant { SearchAccessRecordGrantId = Guid.NewGuid(), OrganizationId = Org, AccessPrincipalTypeName = "user", AccessPrincipalEntityId = Guid.NewGuid(), FormRecordId = r, AccessKindName = SearchAccessKinds.EffectiveOwner, DerivedAuthorizationRevision = 1 });
        var result = await w.Service.VerifyAsync(Org, Guid.NewGuid(), Ct);
        Assert.Equal(SearchAccessReasonCodes.RowsDiverged, result.OutcomeCode);
        Assert.Equal(SearchAccessStatusNames.Quarantined, await w.StatusAsync());
        Assert.Equal(2, w.Store.SearchAccess.RecordGrants.Count); // evidence preserved, not repaired

        var m = new World();
        m.Touch(Owner(r, alice));
        await m.Service.RebuildAsync(Org, "INITIAL_BUILD", Guid.NewGuid(), Ct);
        m.Store.SearchAccess.Edges.Clear();
        Assert.Equal(SearchAccessReasonCodes.MirrorDiverged, (await m.Service.VerifyAsync(Org, Guid.NewGuid(), Ct)).OutcomeCode);
    }

    [Fact]
    public async Task OffModeRefusesMaintenanceAndReportsDisabled()
    {
        var store = new InMemoryAuthorityStore();
        store.Organizations.Add(new OrganizationAuthorizationState { OrganizationId = Org });
        var service = new SearchAccessProjectionService(store, TestProjection.Verifier(store, new UnusedSpiceDb()), new SearchAccessOptions(), new TestProjection.RecordingAlerts(), new NoFaults(), TimeProvider.System);
        Assert.Equal(SearchAccessStatusNames.Disabled, (await service.StatusAsync(Org, Ct)).StatusName);
        await Assert.ThrowsAsync<AuthorityException>(() => service.StepAsync(Org, Ct));
        await Assert.ThrowsAsync<AuthorityException>(() => service.RebuildAsync(Org, "INITIAL_BUILD", Guid.NewGuid(), Ct));
    }

    [Fact]
    public async Task TheStoreRefusesOperationsOutsideTheBoundOrganization()
    {
        var store = new InMemorySearchAccessStore();
        await store.BindOrganizationAsync(Guid.NewGuid(), Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.FindCheckpointAsync(Org, Ct));
        Assert.Throws<InvalidOperationException>(() => store.AddRecordGrants([new SearchAccessRecordGrant { OrganizationId = Org }]));
    }

    private sealed class NoFaults : IFaultInjector
    {
        public void CrashIfArmed(string faultName)
        {
        }
    }

    /// <summary>The SpiceDB comparison is exercised on the stack; unit tests run with a sample size of 0.</summary>
    private sealed class UnusedSpiceDb : ISpiceDbProjection
    {
        public Task EnsureSchemaAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string> InitializeCursorAsync(Guid organizationId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string> ApplyDeltaAsync(Guid organizationId, string expectedMarkerSubjectId, string newMarkerSubjectId, IReadOnlyList<RelationshipDeltaEntry> delta, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<MarkerObservation> ReadMarkerAsync(Guid organizationId, MarkerReadConsistency consistency, string? atLeastAsFreshToken, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> TupleExistsAsync(RelationshipTuple tuple, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SpiceDbCheckOutcome> CheckAsync(SpiceDbCheck check, VerifiedProjectionSnapshot snapshot, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<SpiceDbCheckOutcome>> CheckBulkAsync(IReadOnlyList<SpiceDbCheck> checks, VerifiedProjectionSnapshot snapshot, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> LookupResourcesAsync(string resourceType, string permission, string subjectType, string subjectId, VerifiedProjectionSnapshot snapshot, int maximumResults, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<(bool Ready, string Detail)> ProbeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
