using System.Text.Json;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Domain.Features.Journal;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// E3-M2f-3b (docs/security/E3_M2F3_CONTRACT_CLOSURE.md §5.1–§5.3, §9): the lookup decision LK-3..LK-9, the authoritative bucket, the
/// closed body schema, the key and workload mapping and the refusal context. Pure functions; T2 itself is covered by
/// <see cref="CapabilityRefusalEvidenceRecorderTests"/>.
/// </summary>
public sealed class CapabilityRefusalEvidenceLookupTests
{
    private static readonly DateTimeOffset T = new(2026, 11, 17, 10, 30, 0, TimeSpan.Zero);

    private static (CapabilityRefusalEvidenceWorld World, Guid Org) Seeded(params (string Entry, DateTimeOffset Time)[] entries)
    {
        var world = new CapabilityRefusalEvidenceWorld();
        var org = world.AddOrganization();
        foreach (var (entry, time) in entries)
        {
            world.SeedEvidence(org, entry, time);
        }

        return (world, org);
    }

    private static List<CapabilityRefusalEvidenceRow> Rows(CapabilityRefusalEvidenceWorld world, Guid org) =>
        world.Journal.Where(e => e.OrganizationId == org && e.JournalEventKind == CapabilityRefusalEvidence.EventKind)
            .OrderByDescending(e => e.JournalStreamRevision).Take(8)
            .Select(e => new CapabilityRefusalEvidenceRow(e.SecurityJournalEntryId, e.JournalStreamRevision, e.JournalEventBodyJson, e.JournalEventDigest)).ToList();

    private static CapabilityRefusalEvidenceKey Key(Guid org, string entry = CapabilityRefusalEvidence.EntryPoints.Verify) => CapabilityRefusalEvidenceKey.For(org, entry);

    // ---- M2F3-67: the decision table ----

    [Fact]
    public void NoEvidenceAtAllIsConclusiveAndAppends()
    {
        var (world, org) = Seeded();
        Assert.Equal((CapabilityRefusalEvidenceVerdict.Append, CapabilityRefusalEvidenceReason.None), CapabilityRefusalEvidenceLookup.Decide(Rows(world, org), Key(org), T));
    }

    [Fact]
    public void FewerThanEightRowsAreTheWholeHistory()
    {
        var (world, org) = Seeded((CapabilityRefusalEvidence.EntryPoints.Prepare, T.AddMinutes(-5)), (CapabilityRefusalEvidence.EntryPoints.Commit, T.AddMinutes(-4)));
        Assert.Equal(CapabilityRefusalEvidenceVerdict.Append, CapabilityRefusalEvidenceLookup.Decide(Rows(world, org), Key(org), T).Verdict);
        Assert.Equal(CapabilityRefusalEvidenceVerdict.AlreadyRecorded, CapabilityRefusalEvidenceLookup.Decide(Rows(world, org), Key(org, CapabilityRefusalEvidence.EntryPoints.Commit), T).Verdict);
    }

    [Fact]
    public void EightRowsReachingAnEarlierBucketAreConclusive()
    {
        var earlier = Enumerable.Range(0, 2).Select(i => (CapabilityRefusalEvidence.EntryPoints.All[i], T.AddHours(-1).AddMinutes(i))).ToList();
        var current = Enumerable.Range(0, 6).Select(i => (CapabilityRefusalEvidence.EntryPoints.All[i], T.AddMinutes(-20 + i))).ToList();
        var (world, org) = Seeded([.. earlier, .. current]);
        Assert.Equal(8, Rows(world, org).Count);
        Assert.Equal(CapabilityRefusalEvidenceVerdict.Append, CapabilityRefusalEvidenceLookup.Decide(Rows(world, org), Key(org, CapabilityRefusalEvidence.EntryPoints.IndexDeepVerify), T).Verdict);
        Assert.Equal(CapabilityRefusalEvidenceVerdict.AlreadyRecorded, CapabilityRefusalEvidenceLookup.Decide(Rows(world, org), Key(org, CapabilityRefusalEvidence.EntryPoints.Prepare), T).Verdict);
    }

    [Fact]
    public void EightRowsAllInTheCurrentBucketAreInconsistent()
    {
        // Seven keys plus one duplicate in the same hour: impossible under the bound, so absence cannot be proven.
        var all = CapabilityRefusalEvidence.EntryPoints.All.Select((e, i) => (e, T.AddMinutes(-20 + i))).Append((CapabilityRefusalEvidence.EntryPoints.Prepare, T.AddMinutes(-1))).ToArray();
        var (world, org) = Seeded(all);
        Assert.Equal((CapabilityRefusalEvidenceVerdict.Inconclusive, CapabilityRefusalEvidenceReason.BoundInconsistent), CapabilityRefusalEvidenceLookup.Decide(Rows(world, org), Key(org), T));
    }

    [Fact]
    public void ADuplicateKeyInTheCurrentBucketIsInconsistent()
    {
        var (world, org) = Seeded((CapabilityRefusalEvidence.EntryPoints.Verify, T.AddMinutes(-9)), (CapabilityRefusalEvidence.EntryPoints.Verify, T.AddMinutes(-8)));
        Assert.Equal((CapabilityRefusalEvidenceVerdict.Inconclusive, CapabilityRefusalEvidenceReason.BoundInconsistent), CapabilityRefusalEvidenceLookup.Decide(Rows(world, org), Key(org, CapabilityRefusalEvidence.EntryPoints.Commit), T));
    }

    [Fact]
    public void ANewerEntryThanTheDatabaseTimeIsAClockRegression()
    {
        var (world, org) = Seeded((CapabilityRefusalEvidence.EntryPoints.Prepare, T.AddSeconds(1)));
        Assert.Equal((CapabilityRefusalEvidenceVerdict.Inconclusive, CapabilityRefusalEvidenceReason.ClockRegression), CapabilityRefusalEvidenceLookup.Decide(Rows(world, org), Key(org), T));
    }

    [Fact]
    public void TimesIncreasingTowardsOlderRevisionsAreAnOrderViolation()
    {
        var (world, org) = Seeded((CapabilityRefusalEvidence.EntryPoints.Prepare, T.AddMinutes(-1)), (CapabilityRefusalEvidence.EntryPoints.Commit, T.AddMinutes(-10)));
        Assert.Equal((CapabilityRefusalEvidenceVerdict.Inconclusive, CapabilityRefusalEvidenceReason.TimeOrderViolation), CapabilityRefusalEvidenceLookup.Decide(Rows(world, org), Key(org), T));
    }

    [Theory]
    [InlineData("digest")]
    [InlineData("body")]
    [InlineData("extra-member")]
    [InlineData("other-organization")]
    [InlineData("revision-order")]
    [InlineData("bucket")]
    [InlineData("workload")]
    [InlineData("level")]
    public void AnyUnverifiableRowIsInconclusive(string tampering)
    {
        var (world, org) = Seeded((CapabilityRefusalEvidence.EntryPoints.Prepare, T.AddMinutes(-3)), (CapabilityRefusalEvidence.EntryPoints.Commit, T.AddMinutes(-2)));
        var rows = Rows(world, org);
        var first = rows[0];
        string Rebody(Func<string, string> change) => change(first.JournalEventBodyJson);
        CapabilityRefusalEvidenceRow Resealed(string json) => first with { JournalEventBodyJson = json, JournalEventDigest = CanonicalJson.Digest(json) };
        rows[0] = tampering switch
        {
            "digest" => first with { JournalEventDigest = new byte[32] },
            "body" => first with { JournalEventBodyJson = first.JournalEventBodyJson.Replace("commit", "verify", StringComparison.Ordinal) },
            "extra-member" => Resealed(Rebody(j => j.Replace("\"refusal_code\":", "\"floor_cause\":\"organization\",\"refusal_code\":", StringComparison.Ordinal))),
            "other-organization" => Resealed(Rebody(j => j.Replace(org.ToString("D"), Guid.CreateVersion7().ToString("D"), StringComparison.Ordinal))),
            "revision-order" => first with { JournalStreamRevision = rows[1].JournalStreamRevision - 1 },
            "bucket" => Resealed(Rebody(j => j.Replace("T10:00:00+00:00", "T09:00:00+00:00", StringComparison.Ordinal))),
            "workload" => Resealed(Rebody(j => j.Replace("\"business-api\"", "\"search-index-builder\"", StringComparison.Ordinal))),
            "level" => Resealed(Rebody(j => j.Replace("\"client_capability_level\":1", "\"client_capability_level\":2", StringComparison.Ordinal))),
            _ => throw new ArgumentOutOfRangeException(nameof(tampering)),
        };
        Assert.Equal((CapabilityRefusalEvidenceVerdict.Inconclusive, CapabilityRefusalEvidenceReason.UnverifiableBody), CapabilityRefusalEvidenceLookup.Decide(rows, Key(org), T));
    }

    [Fact]
    public void AnotherEventKindIsNotEvidence()
    {
        var world = new CapabilityRefusalEvidenceWorld();
        var org = world.AddOrganization();
        var entry = world.Seed(org, "FieldSearchExecuted", new { outcome = "COMPLETED" });
        var row = new CapabilityRefusalEvidenceRow(entry.SecurityJournalEntryId, entry.JournalStreamRevision, entry.JournalEventBodyJson, entry.JournalEventDigest);
        Assert.Null(CapabilityRefusalEvidenceLookup.Verify(row, org));
    }

    // ---- M2F3-04, M2F3-60: the authoritative bucket ----

    [Theory]
    [InlineData("2026-11-17T10:59:59.9999999+00:00", "2026-11-17T10:00:00+00:00")]
    [InlineData("2026-11-17T11:00:00.0000000+00:00", "2026-11-17T11:00:00+00:00")]
    [InlineData("2026-11-17T23:59:59.9999990+00:00", "2026-11-17T23:00:00+00:00")]
    [InlineData("2026-11-18T01:30:00.0000000+02:00", "2026-11-17T23:00:00+00:00")]
    public void TheBucketIsTheUtcHour(string time, string bucket) =>
        Assert.Equal(DateTimeOffset.Parse(bucket, System.Globalization.CultureInfo.InvariantCulture), CapabilityRefusalEvidence.BucketOf(DateTimeOffset.Parse(time, System.Globalization.CultureInfo.InvariantCulture)));

    [Fact]
    public void AnEntryJustBeforeTheHourDoesNotBlockTheNextHour()
    {
        var boundary = new DateTimeOffset(2026, 11, 17, 11, 0, 0, TimeSpan.Zero);
        var (world, org) = Seeded((CapabilityRefusalEvidence.EntryPoints.Verify, boundary.AddTicks(-1)));
        Assert.Equal(CapabilityRefusalEvidenceVerdict.AlreadyRecorded, CapabilityRefusalEvidenceLookup.Decide(Rows(world, org), Key(org), boundary.AddTicks(-1)).Verdict);
        Assert.Equal(CapabilityRefusalEvidenceVerdict.Append, CapabilityRefusalEvidenceLookup.Decide(Rows(world, org), Key(org), boundary).Verdict);
    }

    // ---- M2F3-01, M2F3-02: the closed body ----

    [Fact]
    public void TheBodyHasExactlyTheClosedSchema()
    {
        var org = Guid.CreateVersion7();
        var json = CanonicalJson.Serialize(new CapabilityRefusalEvidenceBody(1, CapabilityRefusalEvidence.RefusalCode, org, "business-api", "verify", 1, T, CapabilityRefusalEvidence.BucketOf(T), T.AddSeconds(-1)));
        using var document = JsonDocument.Parse(json);
        Assert.Equal(CapabilityRefusalEvidenceBody.MemberNames.Order(StringComparer.Ordinal), document.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        foreach (var excluded in new[] { "floor", "cause", "effective", "binding", "correlation", "header", "token", "assertion", "user", "actor", "record", "operation", "build", "instance", "key_", "ciphertext" })
        {
            Assert.DoesNotContain(excluded, string.Join(",", document.RootElement.EnumerateObject().Select(p => p.Name)), StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---- M2F3-03, M2F3-59: keys and workloads ----

    [Theory]
    [InlineData("prepare", "business-api")]
    [InlineData("commit", "business-api")]
    [InlineData("verify", "business-api")]
    [InlineData("verify-batch", "business-api")]
    [InlineData("protection-census", "field-protection-verifier")]
    [InlineData("index-backfill", "search-index-builder")]
    [InlineData("index-deep-verify", "search-index-builder")]
    public void TheWorkloadIsFixedByTheEntryPoint(string entryPoint, string workload)
    {
        Assert.Equal(workload, CapabilityRefusalEvidence.WorkloadOf(entryPoint));
        Assert.True(CapabilityRefusalEvidenceKey.For(Guid.CreateVersion7(), entryPoint).IsApproved);
    }

    [Fact]
    public void ExactlySevenKeysExistPerOrganizationAndOtherIsUnreachable()
    {
        var org = Guid.CreateVersion7();
        Assert.Equal(7, CapabilityRefusalEvidence.EntryPoints.All.Select(e => CapabilityRefusalEvidenceKey.For(org, e)).Distinct().Count());
        Assert.DoesNotContain(CapabilityRefusalEvidence.EntryPoints.All, e => CapabilityRefusalEvidence.WorkloadOf(e) == CapabilityRefusalEvidence.Workloads.Other);
        Assert.False(CapabilityRefusalEvidenceKey.For(org, "history").IsApproved);
        Assert.False((CapabilityRefusalEvidenceKey.For(org, "verify") with { WorkloadName = "search-index-builder" }).IsApproved);
        Assert.Equal(JournalStreamNames.Audit, CapabilityRefusalEvidence.StreamName);
    }

    // ---- the refusal context (Exception.Data; wire unchanged) ----

    [Fact]
    public async Task TheFloorAttachesTheSameContextWhicheverFloorRefusesAndNothingElse()
    {
        var store = new InMemoryAuthorityStore();
        var bound = Guid.CreateVersion7();
        var unbound = Guid.CreateVersion7();
        store.Organizations.Add(new AuthorizationAuthority.Domain.Features.Organizations.OrganizationAuthorizationState { OrganizationId = bound, AuthorizationRevision = 3, ProjectionAppliedRevision = 3 });
        store.Organizations.Add(new AuthorizationAuthority.Domain.Features.Organizations.OrganizationAuthorizationState { OrganizationId = unbound, AuthorizationRevision = 3, ProjectionAppliedRevision = 3 });
        store.DataKeyBindings.Add(EnvelopeCapabilityTestSupport.Binding(bound));
        async Task EvaluateAsync(EnvelopeCapabilityFloor floor, Guid organization)
        {
            await using var scope = await store.BeginScopeAsync(TestContext.Current.CancellationToken);
            var state = await scope.LockOrganizationAsync(organization, TestContext.Current.CancellationToken);
            await floor.RequireAsync(scope, state!, ClientEnvelopeCapability.Legacy, TestContext.Current.CancellationToken);
        }

        var byDeployment = await Assert.ThrowsAsync<AuthorityException>(() => EvaluateAsync(EnvelopeCapabilityFloor.Create("2"), unbound));
        var byOrganization = await Assert.ThrowsAsync<AuthorityException>(() => EvaluateAsync(EnvelopeCapabilityFloor.Create("1"), bound));
        Assert.Equal(new CapabilityRefusalContext(unbound, 1), CapabilityRefusalContext.From(byDeployment));
        Assert.Equal(new CapabilityRefusalContext(bound, 1), CapabilityRefusalContext.From(byOrganization));
        Assert.Single(byDeployment.Data.Keys.Cast<object>());
        EnvelopeCapabilityTestSupport.AssertRefusal(byDeployment);
        EnvelopeCapabilityTestSupport.AssertRefusal(byOrganization);
    }

    [Fact]
    public void OnlyACapabilityRefusalCarriesAContext()
    {
        var other = new AuthorityException(AuthorityErrorCodes.OperationRulesOutdated, 409, false);
        other.Data[CapabilityRefusalContext.ExceptionDataKey] = new CapabilityRefusalContext(Guid.CreateVersion7(), 1);
        Assert.Null(CapabilityRefusalContext.From(other));
        Assert.Null(CapabilityRefusalContext.From(new AuthorityException(AuthorityErrorCodes.ClientEnvelopeCapabilityInsufficient, 409, false)));
        Assert.Null(CapabilityRefusalContext.From(new InvalidOperationException()));
        Assert.Null(CapabilityRefusalContext.From(null));
    }
}
