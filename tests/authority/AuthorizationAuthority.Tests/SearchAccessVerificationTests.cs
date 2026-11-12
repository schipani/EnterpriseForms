using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.SearchAccess;
using AuthorizationAuthority.Domain.Features.SearchAccess;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// IX-5 M1 fence and M2 sweep (IX-41, IX-52), the IX-39 coupling guard and the IX-40 property-based differential, at unit level. The
/// SpiceDB side is an independent oracle: it evaluates <c>authority.schema.zed</c>'s permissions directly over the canonical edges (owner
/// and parent chain, shares, team participation), sharing no code with the Tier 2 derivation, and answers LookupSubjects at the pin.
/// </summary>
public sealed partial class SearchAccessProjectionTests
{
    /// <summary>A SpiceDB that holds exactly the canonical edges and evaluates the zed schema itself (the M1/M2 oracle).</summary>
    private sealed class OracleSpiceDb(World world) : ISpiceDbProjection
    {
        public int Lookups { get; private set; }

        /// <summary>Extra subjects SpiceDB reports for a resource (to simulate a Tier 2 omission).</summary>
        public Dictionary<(string Type, Guid Id, string Permission), List<(string SubjectType, Guid SubjectId)>> Extra { get; } = [];

        private IEnumerable<(string Resource, Guid Id, string Relation, string SubjectType, Guid SubjectId, string SubjectRelation)> Edges =>
            world.Store.Edges.Where(e => e.RemovedBySecurityCommandId is null).Select(e => (e.ResourceTypeName, e.ResourceEntityId, e.RelationshipName, e.SubjectTypeName, e.SubjectEntityId, e.SubjectRelationName));

        private HashSet<Guid> Members(Guid team) => Edges.Where(e => e.Resource == "team" && e.Id == team && e.Relation == "member" && e.SubjectType == "user").Select(e => e.SubjectId).ToHashSet();

        /// <summary>Subjects (type, id) of a relation; team#participant expands to its members at user level.</summary>
        private HashSet<(string, Guid)> Relation(string resource, Guid id, string relation)
        {
            var result = new HashSet<(string, Guid)>();
            foreach (var e in Edges.Where(e => e.Resource == resource && e.Id == id && e.Relation == relation))
            {
                result.Add((e.SubjectType, e.SubjectId));
                if (e.SubjectType == "team" && e.SubjectRelation == "participant")
                {
                    result.UnionWith(Members(e.SubjectId).Select(u => ("user", u)));
                }
            }

            return result;
        }

        private HashSet<(string, Guid)> EffectiveOwner(Guid record, int depth = 0)
        {
            var owners = Relation("form_record", record, "owner");
            if (depth < 16)
            {
                foreach (var parent in Edges.Where(e => e.Resource == "form_record" && e.Id == record && e.Relation == "parent").Select(e => e.SubjectId))
                {
                    owners.UnionWith(EffectiveOwner(parent, depth + 1));
                }
            }

            return owners;
        }

        private static readonly string[] ShareRoles = ["reader", "contributor", "manager"];

        private HashSet<(string, Guid)> ShareRead(Guid share) => ShareRoles.SelectMany(role => Relation("record_share", share, role)).ToHashSet();

        private HashSet<(string, Guid)> Evaluate(string type, Guid id, string permission)
        {
            var subjects = (type, permission) switch
            {
                ("form_record", "effective_owner") => EffectiveOwner(id),
                ("form_record", "discover") => EffectiveOwner(id).Union(Edges.Where(e => e.Resource == "form_record" && e.Id == id && e.Relation == "shared_via").SelectMany(e => ShareRead(e.SubjectId))).ToHashSet(),
                ("field_item", "read") => Edges.Where(e => e.Resource == "field_item" && e.Id == id && e.Relation == "parent_record").SelectMany(e => EffectiveOwner(e.SubjectId))
                    .Union(Edges.Where(e => e.Resource == "field_item" && e.Id == id && e.Relation == "shared_via").SelectMany(e => ShareRead(e.SubjectId))).ToHashSet(),
                _ => throw new NotSupportedException(type + "#" + permission),
            };
            subjects.UnionWith(Extra.GetValueOrDefault((type, id, permission)) ?? []);
            return subjects;
        }

        public Task<IReadOnlyList<string>> LookupSubjectsAsync(string resourceType, string resourceId, string permission, string subjectType, string subjectRelation, VerifiedProjectionSnapshot snapshot, int maximumSubjects, CancellationToken cancellationToken)
        {
            snapshot.RequireOpen();
            Lookups++;
            var ids = Evaluate(resourceType, Guid.Parse(resourceId), permission).Where(s => s.Item1 == subjectType).Select(s => s.Item2.ToString("D")).Distinct().ToList();
            return ids.Count > maximumSubjects ? throw new SpiceDbLookupLimitExceededException("limit") : Task.FromResult<IReadOnlyList<string>>(ids);
        }

        public Task<MarkerObservation> ReadMarkerAsync(Guid organizationId, MarkerReadConsistency consistency, string? atLeastAsFreshToken, CancellationToken cancellationToken) =>
            Task.FromResult(TestProjection.MarkerFor(world.State));

        public Task EnsureSchemaAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<string> InitializeCursorAsync(Guid organizationId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string> ApplyDeltaAsync(Guid organizationId, string expectedMarkerSubjectId, string newMarkerSubjectId, IReadOnlyList<RelationshipDeltaEntry> delta, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> TupleExistsAsync(RelationshipTuple tuple, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<SpiceDbCheckOutcome> CheckAsync(SpiceDbCheck check, VerifiedProjectionSnapshot snapshot, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<SpiceDbCheckOutcome>> CheckBulkAsync(IReadOnlyList<SpiceDbCheck> checks, VerifiedProjectionSnapshot snapshot, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<string>> LookupResourcesAsync(string resourceType, string permission, string subjectType, string subjectId, VerifiedProjectionSnapshot snapshot, int maximumResults, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<(bool Ready, string Detail)> ProbeAsync(CancellationToken cancellationToken) => Task.FromResult((true, "oracle"));
    }

    private static AccessEdge ItemOf(Guid item, Guid record) => new("field_item", item, "parent_record", "form_record", record, string.Empty);

    private static (World W, OracleSpiceDb Spice) OracleWorld(Action<SearchAccessOptions>? configure = null)
    {
        OracleSpiceDb? spice = null;
        var w = new World(0, world => spice = new OracleSpiceDb(world), configure);
        return (w, spice!);
    }

    /// <summary>The S1 projection caught up with the organization (the oracle then answers at the revision).</summary>
    private static void Projected(World w)
    {
        w.State.ProjectionAppliedRevision = w.State.AuthorizationRevision;
        w.State.ProjectionZedToken = "zed-" + w.State.AuthorizationRevision;
        w.State.WithJournalDigest();
    }

    /// <summary>Catch-up, projection and verification steps until the step reports verified (or the bound is reached).</summary>
    private static async Task<SearchAccessVerificationStep> VerifiedAsync(World w, int maximumSteps = 20)
    {
        if (w.Store.SearchAccess.Checkpoints.Count == 0)
        {
            await w.Service.RebuildAsync(Org, "INITIAL_BUILD", Guid.NewGuid(), Ct);
        }

        await w.Service.CatchUpAsync(Org, 10_000, Ct);
        Projected(w);
        SearchAccessVerificationStep step = null!;
        for (var i = 0; i < maximumSteps; i++)
        {
            step = await w.Service.VerifyStepAsync(Org, Guid.NewGuid(), Ct);
            if (step.OutcomeCode is not (SearchAccessVerificationOutcomes.Initialized or SearchAccessVerificationOutcomes.Verified) || step.Verified)
            {
                return step;
            }
        }

        return step;
    }

    [Fact]
    public void TheDerivationIsCoupledToTheExactSchemaTextItMirrors()
    {
        // CI half of the IX-39 coupling guard: a schema change fails here until the derivation and this digest are updated together.
        var schema = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "authority", "AuthorizationAuthority.Infrastructure", "SpiceDb", "authority.schema.zed"));
        Assert.True(SearchAccessSchemaCoupling.Coupled(schema), "authority.schema.zed changed: update SearchAccessDerivation and SearchAccessSchemaCoupling.MirroredSchemaSha256Hex together");
        Assert.True(SearchAccessSchemaCoupling.Coupled(schema.Replace("\n", "\r\n", StringComparison.Ordinal))); // line endings never matter
        Assert.False(SearchAccessSchemaCoupling.Coupled(schema.Replace("permission discover", "permission discovers", StringComparison.Ordinal)));
        Assert.Equal(32, SearchAccessSchemaCoupling.MirroredSchemaDigest.Length);

        static string RepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CLAUDE.md")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName ?? throw new InvalidOperationException("repository root not found");
        }
    }

    [Fact]
    public async Task RowsOfAnotherSchemaAreNotReadyAreRebuiltAndMustBeVerifiedAgain()
    {
        var f = await FixtureAsync();
        Assert.Equal(SearchAccessStatusNames.Ready, await f.W.StatusAsync());
        f.W.Store.SearchAccess.VerificationStates.Add(new SearchAccessVerificationState { SearchAccessVerificationStateId = Guid.NewGuid(), OrganizationId = Org, AccessVerifiedRevision = f.W.State.AuthorizationRevision, AccessVerifiedJournalDigest = f.W.State.LatestSecurityJournalDigest });
        f.W.Checkpoint.DerivedSchemaDigest = new byte[32]; // rows derived under another schema text
        Assert.Equal(SearchAccessStatusNames.Building, await f.W.StatusAsync()); // never ready
        Assert.Equal(SearchAccessStepOutcome.UpgradeRequired, await f.W.Service.StepAsync(Org, Ct));
        await f.W.Service.RebuildAsync(Org, "SCHEMA_COUPLING", Guid.NewGuid(), Ct);
        Assert.True(SearchAccessSchemaCoupling.SchemaCoupled(f.W.Checkpoint));
        Assert.Equal(SearchAccessStatusNames.Ready, await f.W.StatusAsync());
        Assert.Empty(f.W.Store.SearchAccess.VerificationStates); // the rebuild resets M2: a complete pass must follow
    }

    [Fact]
    public void VerificationRequiresTheFenceClosedAndBothM2DeadlinesWithoutGrace()
    {
        var options = new SearchAccessOptions();
        var now = DateTimeOffset.UtcNow;
        SearchAccessVerificationState State(long fenced, DateTimeOffset? full, DateTimeOffset? sweep) => new() { AccessVerifiedRevision = fenced, FullVerificationCompletedOn = full, SpiceDbSweepCompletedOn = sweep };
        Assert.Equal("M2_NOT_STARTED", SearchAccessProjectionService.VerificationStatus(null, 5, options, now).ReasonCode);
        Assert.Equal("M1_FENCE_OPEN", SearchAccessProjectionService.VerificationStatus(State(4, now, now), 5, options, now).ReasonCode);
        Assert.Equal("M2_EQUIVALENCE_OVERDUE", SearchAccessProjectionService.VerificationStatus(State(5, null, now), 5, options, now).ReasonCode);
        Assert.Equal("M2_EQUIVALENCE_OVERDUE", SearchAccessProjectionService.VerificationStatus(State(5, now.AddSeconds(-options.FullVerificationDeadlineSeconds - 1), now), 5, options, now).ReasonCode);
        Assert.Equal("M2_SPICEDB_SWEEP_OVERDUE", SearchAccessProjectionService.VerificationStatus(State(5, now, null), 5, options, now).ReasonCode);
        Assert.Equal("M2_SPICEDB_SWEEP_OVERDUE", SearchAccessProjectionService.VerificationStatus(State(5, now, now.AddSeconds(-options.SpiceDbSweepDeadlineSeconds - 1)), 5, options, now).ReasonCode);
        Assert.True(SearchAccessProjectionService.VerificationStatus(State(5, now, now), 5, options, now).Verified);
    }

    [Fact]
    public async Task AFreshBuildIsVerifiedOnlyAfterACompletePassAndAChangeOpensTheFenceUntilVerified()
    {
        var (w, spice) = OracleWorld(o => o.SpiceDbSweepBatchSize = 2);
        Guid alice = Guid.NewGuid(), bob = Guid.NewGuid(), carol = Guid.NewGuid(), team = Guid.NewGuid(), share = Guid.NewGuid();
        var records = Enumerable.Range(0, 5).Select(_ => Guid.CreateVersion7()).ToList();
        var item = Guid.NewGuid();
        w.Touch([.. records.Select(r => Owner(r, alice)), Member(team, carol), SharedVia(records[0], share), ReaderTeam(share, team), Item(item, share), ItemOf(item, records[0])]);
        await w.Service.RebuildAsync(Org, "INITIAL_BUILD", Guid.NewGuid(), Ct);
        Projected(w);

        // Not verified before the first state, nor while the M2-S pass (5 records, 2 per step) is in progress.
        Assert.Equal(SearchAccessVerificationOutcomes.Initialized, (await w.Service.VerifyStepAsync(Org, Guid.NewGuid(), Ct)).OutcomeCode);
        var steps = new List<SearchAccessVerificationStep>();
        do
        {
            steps.Add(await w.Service.VerifyStepAsync(Org, Guid.NewGuid(), Ct));
        }
        while (!steps[^1].SweepCompleted && steps.Count < 10);
        Assert.All(steps, s => Assert.Equal(SearchAccessVerificationOutcomes.Verified, s.OutcomeCode));
        Assert.All(steps.SkipLast(1), s => Assert.False(s.Verified));
        Assert.True(steps[^1].Verified);
        Assert.Equal(3, steps.Count); // 2 + 2 + 1
        Assert.True(steps[0].FullVerificationRan); // M2-E/V ran and was clean
        Assert.True((await w.Service.VerificationStatusAsync(Org, Ct)).Verified);

        // A share to Bob: Tier 2 catches up, but the rows are unverified until the fence verifies the affected record at a pin.
        w.Touch(Reader(share, bob));
        await w.Service.CatchUpAsync(Org, 100, Ct);
        Projected(w);
        Assert.Equal("M1_FENCE_OPEN", (await w.Service.VerificationStatusAsync(Org, Ct)).ReasonCode);
        var fenced = await w.Service.VerifyStepAsync(Org, Guid.NewGuid(), Ct);
        Assert.Equal((SearchAccessVerificationOutcomes.Verified, 1, true), (fenced.OutcomeCode, fenced.FencedRecords, fenced.Verified));

        // A membership-only change affects no record (F-1): the fence closes without a record comparison.
        w.Touch(Member(team, bob));
        await w.Service.CatchUpAsync(Org, 100, Ct);
        Projected(w);
        var membership = await w.Service.VerifyStepAsync(Org, Guid.NewGuid(), Ct);
        Assert.Equal((SearchAccessVerificationOutcomes.Verified, 0, true), (membership.OutcomeCode, membership.FencedRecords, membership.Verified));
        Assert.True(spice.Lookups > 0);
    }

    [Fact]
    public async Task ASpiceDbSubjectTier2LacksIsADivergenceThatQuarantinesAndResetsVerification()
    {
        // IX-38 divergence: an omission (SpiceDB grants a subject the Tier 2 rows do not have) is detected by the M1 comparison.
        var (w, spice) = OracleWorld();
        Guid alice = Guid.NewGuid(), mallory = Guid.NewGuid();
        var record = Guid.CreateVersion7();
        w.Touch(Owner(record, alice));
        Assert.True((await VerifiedAsync(w)).Verified);
        spice.Extra[("form_record", record, "discover")] = [("user", mallory)];
        w.Touch(Owner(Guid.CreateVersion7(), alice), Owner(record, alice)); // any change of the record re-opens the fence for it
        w.Touch(Owner(record, Guid.NewGuid()));
        await w.Service.CatchUpAsync(Org, 100, Ct);
        Projected(w);
        var step = await w.Service.VerifyStepAsync(Org, Guid.NewGuid(), Ct);
        Assert.Equal(SearchAccessVerificationOutcomes.Diverged, step.OutcomeCode);
        Assert.Contains("discover", step.FindingDetail, StringComparison.Ordinal);
        Assert.Equal(SearchAccessStatusNames.Quarantined, await w.StatusAsync());
        Assert.Empty(w.Store.SearchAccess.VerificationStates); // nothing stays verified
        Assert.Contains(w.Alerts.Raised, a => a.AlertKind == "SEARCH_ACCESS_PROJECTION_QUARANTINED");
    }

    [Fact]
    public async Task ADeletedTier2RowIsFoundByTheSweepEvenWithoutAChange()
    {
        // IX-41: Authority-domain deletion after verification (T2-04): no event names the record; the M2-S pass finds it.
        var (w, _) = OracleWorld(o => o.SpiceDbSweepIntervalSeconds = 10);
        Guid alice = Guid.NewGuid(), bob = Guid.NewGuid(), share = Guid.NewGuid();
        var record = Guid.CreateVersion7();
        w.Touch(Owner(record, alice), SharedVia(record, share), Reader(share, bob));
        Assert.True((await VerifiedAsync(w)).Verified);
        w.Store.SearchAccess.RecordGrants.RemoveAll(g => g.AccessPrincipalEntityId == bob);
        var state = w.Store.SearchAccess.VerificationStates.Single();
        state.SpiceDbSweepCompletedOn = DateTimeOffset.UtcNow.AddSeconds(-11); // the next pass is due
        var step = await w.Service.VerifyStepAsync(Org, Guid.NewGuid(), Ct);
        Assert.Equal(SearchAccessVerificationOutcomes.Diverged, step.OutcomeCode);
        Assert.Equal(SearchAccessStatusNames.Quarantined, await w.StatusAsync());
    }

    [Fact]
    public async Task TooManyAffectedRecordsFailClosedAndResetTheVerification()
    {
        var (w, _) = OracleWorld(o => o.FenceMaximumRecords = 2);
        var alice = Guid.NewGuid();
        var records = Enumerable.Range(0, 3).Select(_ => Guid.CreateVersion7()).ToList();
        w.Touch(Owner(records[0], alice));
        Assert.True((await VerifiedAsync(w)).Verified);
        w.Touch([.. records.Skip(1).Select(r => Owner(r, alice)), Owner(records[0], Guid.NewGuid())]);
        await w.Service.CatchUpAsync(Org, 100, Ct);
        Projected(w);
        Assert.Equal(SearchAccessVerificationOutcomes.FenceLimit, (await w.Service.VerifyStepAsync(Org, Guid.NewGuid(), Ct)).OutcomeCode);
        Assert.Empty(w.Store.SearchAccess.VerificationStates); // unverified until a complete new pass
        Assert.Equal(SearchAccessStatusNames.Ready, await w.StatusAsync()); // not quarantined: a limit is not a divergence
        Assert.True((await VerifiedAsync(w)).Verified); // the new pass covers every record
    }

    [Fact]
    public async Task AnItemWithoutAParentRecordTupleIsReadOnlyThroughItsShares()
    {
        // The Authority writes no field_item#parent_record tuples today (stack observation): SpiceDB's field_item.read is then the
        // share path only, and the M1 comparison must expect exactly that (owners read records through form_record#read).
        var (w, _) = OracleWorld();
        Guid alice = Guid.NewGuid(), bob = Guid.NewGuid(), share = Guid.NewGuid(), item = Guid.NewGuid();
        var record = Guid.CreateVersion7();
        w.Touch(Owner(record, alice), SharedVia(record, share), Reader(share, bob), Item(item, share));
        var step = await VerifiedAsync(w);
        Assert.True(step.Verified, step.OutcomeCode + " " + step.FindingDetail);
        Assert.NotEqual(SearchAccessStatusNames.Quarantined, await w.StatusAsync());
    }

    [Fact]
    public async Task NothingIsVerifiedWhileSpiceDbHasNotProjectedTheRevision()
    {
        var (w, _) = OracleWorld();
        var record = Guid.CreateVersion7();
        w.Touch(Owner(record, Guid.NewGuid()));
        Assert.True((await VerifiedAsync(w)).Verified);
        w.Touch(Owner(record, Guid.NewGuid()));
        await w.Service.CatchUpAsync(Org, 100, Ct); // Tier 2 at the head, S1 projection behind
        Assert.Equal(SearchAccessVerificationOutcomes.SpiceDbNotReady, (await w.Service.VerifyStepAsync(Org, Guid.NewGuid(), Ct)).OutcomeCode);
        Assert.Equal("M1_FENCE_OPEN", (await w.Service.VerificationStatusAsync(Org, Ct)).ReasonCode);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task PropertyBasedDifferentialRandomCommandSequencesAlwaysVerify(int seed)
    {
        // IX-40: random owner, team-owner, parent, share, item, membership and revocation sequences; after every batch the rows must be
        // verified against the independent oracle (0 differences), and the stored rows must equal the derivation of the canonical edges.
        var random = new Random(seed);
        var (w, spice) = OracleWorld(o => o.SpiceDbSweepBatchSize = 4);
        var users = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToList();
        var teams = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToList();
        var records = Enumerable.Range(0, 8).Select(_ => Guid.CreateVersion7()).ToList();
        var items = records.ToDictionary(r => r, _ => Enumerable.Range(0, 2).Select(_ => Guid.NewGuid()).ToList());
        var shares = new List<(Guid Share, Guid Record)>();
        var live = new HashSet<AccessEdge>();
        void Apply(bool touch, AccessEdge edge)
        {
            if (touch && live.Add(edge))
            {
                w.Touch(edge);
            }
            else if (!touch && live.Remove(edge))
            {
                w.Delete(edge);
            }
        }

        foreach (var r in records)
        {
            Apply(true, Owner(r, users[random.Next(users.Count)]));
            foreach (var i in items[r])
            {
                Apply(true, ItemOf(i, r));
            }
        }

        Assert.True((await VerifiedAsync(w, 40)).Verified);
        for (var batch = 0; batch < 6; batch++)
        {
            for (var op = 0; op < 6; op++)
            {
                var record = records[random.Next(records.Count)];
                switch (random.Next(7))
                {
                    case 0:
                        Apply(true, OwnerTeam(record, teams[random.Next(teams.Count)]));
                        break;
                    case 1:
                        var share = Guid.NewGuid();
                        shares.Add((share, record));
                        Apply(true, SharedVia(record, share));
                        Apply(true, random.Next(2) == 0 ? Reader(share, users[random.Next(users.Count)]) : ReaderTeam(share, teams[random.Next(teams.Count)]));
                        Apply(true, Item(items[record][random.Next(2)], share));
                        break;
                    case 2 when shares.Count > 0:
                        var (revoked, of) = shares[random.Next(shares.Count)];
                        foreach (var edge in live.Where(e => (e.ResourceType == "record_share" && e.ResourceId == revoked) || (e.SubjectType == "record_share" && e.SubjectId == revoked)).ToList())
                        {
                            Apply(false, edge);
                        }

                        shares.RemoveAll(s => s.Share == revoked);
                        break;
                    case 3:
                        Apply(random.Next(3) > 0, Member(teams[random.Next(teams.Count)], users[random.Next(users.Count)]));
                        break;
                    case 4:
                        var child = records[random.Next(records.Count)];
                        if (child != record && !live.Any(e => e.ResourceType == "form_record" && e.Relation == "parent" && (e.ResourceId == child || e.SubjectId == child || e.ResourceId == record)))
                        {
                            Apply(true, Parent(child, record));
                        }

                        break;
                    case 5:
                        foreach (var edge in live.Where(e => e.ResourceType == "form_record" && e.ResourceId == record && e.Relation == "owner").Skip(1).ToList())
                        {
                            Apply(false, edge);
                        }

                        break;
                    default:
                        Apply(true, Owner(record, users[random.Next(users.Count)]));
                        break;
                }
            }

            var step = await VerifiedAsync(w, 40);
            Assert.True(step.Verified, $"seed {seed} batch {batch}: {step.OutcomeCode} {step.FindingDetail}");
            w.AssertRowsEqualCanonical();
        }

        Assert.NotEqual(SearchAccessStatusNames.Quarantined, await w.StatusAsync());
        Assert.True(spice.Lookups > 0);
    }
}
