using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Domain.Features.Content;
using AuthorizationAuthority.Domain.Features.Organizations;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>E3-M2f-2 test support: ledger rows, a capturing observer and a fingerprint of everything an entry point could write.</summary>
internal static class EnvelopeCapabilityTestSupport
{
    public static ClientEnvelopeCapability Level1 => ClientEnvelopeCapability.Legacy;

    public static ClientEnvelopeCapability Level2 => ClientEnvelopeCapability.Current;

    /// <summary>A ledger binding of <paramref name="organizationId"/> (the organization floor becomes 2; DK rules are not involved).</summary>
    public static ContentDataKeyBinding Binding(Guid organizationId, Guid? record = null, Guid? operation = null) => new()
    {
        ContentDataKeyBindingId = Guid.CreateVersion7(),
        DataKeyIdentifier = Guid.CreateVersion7(),
        OrganizationId = organizationId,
        FormRecordId = record ?? Guid.CreateVersion7(),
        EnvelopePayloadKindCode = ContentDataKeyBindingKinds.FieldValueBatch,
        DataKeyCommitmentBytes = new byte[32],
        BindingItemCount = 1,
        BindingContentChangeOperationId = operation ?? Guid.CreateVersion7(),
        DataKeyBoundOn = DateTimeOffset.UtcNow,
    };

    /// <summary>Everything a refused request must leave unchanged (CAP-O6, §6.3): rows, journal, heads, operations, revisions and scope commits.</summary>
    public static string Fingerprint(InMemoryAuthorityStore store) => string.Join('|',
        store.ScopeCommitCount,
        store.Journal.Count, store.Operations.Count, store.CandidateLeaves.Count, store.Commits.Count, store.ManifestLeaves.Count, store.ItemIdentities.Count,
        store.DataKeyBindings.Count, store.Checkpoints.Count, store.CensusResults.Count, store.Attestations.Count, store.Snapshots.Count, store.Outbox.Count,
        store.SearchIndex.Entries.Count, store.SearchIndex.Postings.Count,
        string.Join(',', store.Operations.Select(o => $"{o.ContentChangeOperationId:N}:{o.OperationStateName}:{o.OperationQuarantineReasonCode}:{o.CommitAcceptanceRuleVersion}")),
        string.Join(',', store.Heads.Select(h => $"{h.FormRecordId:N}:{h.CommittedContentRevision}:{h.CommittedOperationId:N}:{h.ContentHeadQuarantined}:{h.QuarantineReasonCode}")),
        string.Join(',', store.Resources.Select(r => $"{r.ResourceEntityId:N}:{r.ResourceLifecycleState}")),
        string.Join(',', store.Organizations.Select(o => $"{o.OrganizationId:N}:{o.AuthorizationRevision}:{o.ContentIntegrityRevision}:{o.AuditJournalRevision}")),
        string.Join(',', store.SearchIndex.Entries.Select(e => $"{e.FormRecordId:N}:{e.IndexedOperationId:N}:{e.LastDeepVerifiedOn:O}:{e.LastDeepAttemptedOn:O}:{e.LastDeepAttemptOutcomeCode}")),
        string.Join(',', store.SearchIndex.Projections.Select(p => $"{p.OrganizationId:N}:{p.ProjectionStatusName}")));

    public static int IntegrityIncidents(InMemoryAuthorityStore store) =>
        store.Journal.Count(j => j.JournalEventKind is "IntegrityVerificationFailed" or "CurrentSnapshotMismatch");

    /// <summary>Asserts the fixed CAP-E1 refusal.</summary>
    public static void AssertRefusal(AuthorityException ex)
    {
        Assert.Equal((AuthorityErrorCodes.ClientEnvelopeCapabilityInsufficient, 409, false), (ex.ErrorCode, ex.HttpStatusCode, ex.Retryable));
        Assert.Equal(EnvelopeCapabilityFloor.RefusalDetail, ex.Detail);
    }

    internal sealed class CapturingObserver : IEnvelopeCapabilityObserver
    {
        public List<(Guid Organization, int Level, string Outcome)> Events { get; } = [];

        public void Evaluated(Guid organizationId, int clientLevel, string outcome) => Events.Add((organizationId, clientLevel, outcome));
    }
}

/// <summary>
/// E3-M2f-2 (docs/security/E3_M2F_CONTRACT_CLOSURE.md §3, §4, §5, §7, §9): the shared capability component against the in-memory store.
/// M2F-06 grammar, M2F-07 matrix, M2F-08 any binding state, M2F-09 isolation, M2F-20 boolean store port, M2F-22 short-circuit, M2F-24
/// store failure, M2F-27 refusal shape, §9 deployment-floor parse, CAP-E4 signal, and no stale negative result.
/// </summary>
public sealed class EnvelopeCapabilityFloorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Fixture
    {
        public InMemoryAuthorityStore Store { get; } = new();

        public Guid Org { get; } = Guid.CreateVersion7();

        public Guid OtherOrg { get; } = Guid.CreateVersion7();

        public Fixture()
        {
            Store.Organizations.Add(new OrganizationAuthorizationState { OrganizationId = Org, AuthorizationRevision = 3, ProjectionAppliedRevision = 3 });
            Store.Organizations.Add(new OrganizationAuthorizationState { OrganizationId = OtherOrg, AuthorizationRevision = 3, ProjectionAppliedRevision = 3 });
        }

        /// <summary>One evaluation the way an entry point runs it: own scope, organization lock, then the check.</summary>
        public async Task EvaluateAsync(EnvelopeCapabilityFloor floor, Guid organization, ClientEnvelopeCapability? client)
        {
            await using var scope = await Store.BeginScopeAsync(Ct);
            var state = await scope.LockOrganizationAsync(organization, Ct) ?? throw new InvalidOperationException("organization missing");
            await floor.RequireAsync(scope, state, client, Ct);
        }
    }

    // ---- M2F-06 header grammar (CAP-S3/S5/S6) ----

    public static TheoryData<string[]?, int> HeaderValues => new()
    {
        { null, 1 },
        { [], 1 },
        { ["1"], 1 },
        { ["2"], 2 },
        { [""], 1 },
        { ["02"], 1 },
        { [" 2"], 1 },
        { ["2 "], 1 },
        { ["+2"], 1 },
        { ["3"], 1 },
        { ["0"], 1 },
        { ["x"], 1 },
        { ["two"], 1 },
        { ["1, 2"], 1 },
        { ["2,2"], 1 },
        { ["2", "2"], 1 },
        { ["1", "2"], 1 },
        { ["2", "1"], 1 },
        { ["２"], 1 }, // full-width digit two
    };

    [Theory]
    [MemberData(nameof(HeaderValues))]
    public void M2F06TheHeaderGrammarAdmitsExactlyOneTwoAndNeverAnythingAboveTwo(string[]? values, int expected)
    {
        var parsed = ClientEnvelopeCapability.FromHeaderValues(values);
        Assert.Equal(expected, parsed.Level);
        Assert.InRange(parsed.Level, 1, 2);
    }

    [Fact]
    public void TheCapabilityLevelsAreTheTwoRecognizedInstances()
    {
        Assert.Equal("EnterpriseForms-Envelope-Capability", ClientEnvelopeCapability.HeaderName);
        Assert.Equal((1, 2), (ClientEnvelopeCapability.Legacy.Level, ClientEnvelopeCapability.Current.Level));
        Assert.Same(ClientEnvelopeCapability.Current, ClientEnvelopeCapability.FromHeaderValues(["2"]));
        Assert.Same(ClientEnvelopeCapability.Legacy, ClientEnvelopeCapability.FromHeaderValues(["1"]));
        Assert.Empty(typeof(ClientEnvelopeCapability).GetConstructors()); // no other level can be constructed
    }

    // ---- §9 deployment floor ----

    [Theory]
    [InlineData(null, 1)]
    [InlineData("1", 1)]
    [InlineData("2", 2)]
    public void TheDeploymentFloorIsAbsentOneOrTwo(string? configured, int expected)
    {
        Assert.Equal(expected, EnvelopeCapabilityFloor.ParseDeploymentFloor(configured));
        var floor = EnvelopeCapabilityFloor.Create(configured);
        Assert.Equal(expected, floor.DeploymentFloor);
        Assert.Equal("envelope capability floor enforced; deployment floor " + expected, floor.Description);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" 2")]
    [InlineData("2 ")]
    [InlineData("02")]
    [InlineData("+2")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("3")]
    [InlineData("two")]
    [InlineData("true")]
    [InlineData("1,2")]
    public void AnyOtherDeploymentFloorValueRefusesTheStart(string configured)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => EnvelopeCapabilityFloor.Create(configured));
        // One fixed message: the configured value is never echoed.
        Assert.Equal("Content:MinimumBusinessEnvelopeCapability must be absent, \"1\" or \"2\"; the Authorization Authority refuses to start.", ex.Message);
    }

    [Fact]
    public void TheDefaultFloorIsTheAbsentConfiguration() => Assert.Equal(1, EnvelopeCapabilityFloor.Default.DeploymentFloor);

    // ---- M2F-07 matrix (CAP-F1) and M2F-22 short-circuit (CAP-F3, AM-F1 #4) ----

    public static TheoryData<int?, int, bool, bool, int> Matrix => new()
    {
        // client level (null = no declaration), deployment floor, organization has a binding, refused, existence queries
        { null, 1, false, false, 1 },
        { null, 1, true, true, 1 },
        { null, 2, false, true, 0 },
        { null, 2, true, true, 0 },
        { 1, 1, false, false, 1 },
        { 1, 1, true, true, 1 },
        { 1, 2, false, true, 0 },
        { 1, 2, true, true, 0 },
        { 2, 1, false, false, 0 },
        { 2, 1, true, false, 0 },
        { 2, 2, false, false, 0 },
        { 2, 2, true, false, 0 },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task M2F07AndM2F22TheEffectiveFloorIsTheMaximumAndOnlyLevelOneAtFloorOneReadsTheLedger(int? clientLevel, int deploymentFloor, bool bound, bool refused, int queries)
    {
        var f = new Fixture();
        if (bound)
        {
            f.Store.DataKeyBindings.Add(EnvelopeCapabilityTestSupport.Binding(f.Org));
        }

        var client = clientLevel switch { null => null, 1 => ClientEnvelopeCapability.Legacy, _ => ClientEnvelopeCapability.Current };
        var floor = EnvelopeCapabilityFloor.Create(deploymentFloor.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var fingerprint = EnvelopeCapabilityTestSupport.Fingerprint(f.Store);
        if (refused)
        {
            EnvelopeCapabilityTestSupport.AssertRefusal(await Assert.ThrowsAsync<AuthorityException>(() => f.EvaluateAsync(floor, f.Org, client)));
        }
        else
        {
            await f.EvaluateAsync(floor, f.Org, client);
        }

        Assert.Equal(queries, f.Store.DataKeyBindingExistenceQueries);
        Assert.Equal(fingerprint, EnvelopeCapabilityTestSupport.Fingerprint(f.Store)); // CAP-F6: the check changes no state
        Assert.Equal(0, f.Store.DataKeyLedgerChecks); // L-13 is not required for the existence query (AM-F1 #9)
    }

    [Fact]
    public async Task M2F22LevelTwoTrafficNeverTouchesTheLedgerEvenWhenItIsUnreadable()
    {
        var f = new Fixture();
        f.Store.DataKeyBindingExistenceFailure = new InvalidOperationException("ledger unreadable");
        await f.EvaluateAsync(EnvelopeCapabilityFloor.Default, f.Org, ClientEnvelopeCapability.Current);
        await f.EvaluateAsync(EnvelopeCapabilityFloor.Create("2"), f.Org, ClientEnvelopeCapability.Current);
        Assert.Equal(0, f.Store.DataKeyBindingExistenceQueries);
    }

    // ---- M2F-08 any binding state (CAP-F2) and M2F-09 isolation (CAP-F7) ----

    [Theory]
    [InlineData(ContentOperationStates.Prepared, false)]
    [InlineData(ContentOperationStates.Committed, false)]
    [InlineData(ContentOperationStates.Aborted, false)]
    [InlineData(ContentOperationStates.Prepared, true)] // expired
    public async Task M2F08ABindingOfAnOperationInAnyStateRaisesTheOrganizationFloor(string operationState, bool expired)
    {
        var f = new Fixture();
        var operation = new ContentChangeOperation
        {
            ContentChangeOperationId = Guid.CreateVersion7(),
            OrganizationId = f.Org,
            FormRecordId = Guid.CreateVersion7(),
            OperationStateName = operationState,
            OperationExpiresOn = expired ? DateTimeOffset.UtcNow.AddHours(-1) : DateTimeOffset.UtcNow.AddMinutes(10),
            PrepareAcceptanceRuleVersion = ContentAcceptanceRules.CurrentVersion,
        };
        f.Store.Operations.Add(operation);
        f.Store.DataKeyBindings.Add(EnvelopeCapabilityTestSupport.Binding(f.Org, operation.FormRecordId, operation.ContentChangeOperationId));

        EnvelopeCapabilityTestSupport.AssertRefusal(await Assert.ThrowsAsync<AuthorityException>(() => f.EvaluateAsync(EnvelopeCapabilityFloor.Default, f.Org, ClientEnvelopeCapability.Legacy)));
        await f.EvaluateAsync(EnvelopeCapabilityFloor.Default, f.Org, ClientEnvelopeCapability.Current);
    }

    [Fact]
    public async Task M2F09ABindingInOneOrganizationDoesNotRaiseTheFloorOfAnother()
    {
        var f = new Fixture();
        f.Store.DataKeyBindings.Add(EnvelopeCapabilityTestSupport.Binding(f.Org));

        EnvelopeCapabilityTestSupport.AssertRefusal(await Assert.ThrowsAsync<AuthorityException>(() => f.EvaluateAsync(EnvelopeCapabilityFloor.Default, f.Org, ClientEnvelopeCapability.Legacy)));
        await f.EvaluateAsync(EnvelopeCapabilityFloor.Default, f.OtherOrg, ClientEnvelopeCapability.Legacy);
        await f.EvaluateAsync(EnvelopeCapabilityFloor.Default, f.OtherOrg, null);
    }

    [Fact]
    public async Task ANegativeResultIsNeverReusedAfterTheFirstBindingAppears()
    {
        var f = new Fixture();
        var floor = EnvelopeCapabilityFloor.Default;
        await f.EvaluateAsync(floor, f.Org, ClientEnvelopeCapability.Legacy); // no binding yet: admitted

        // The same scope keeps evaluating against the ledger as it is now: no cached "no binding".
        await using (var scope = await f.Store.BeginScopeAsync(Ct))
        {
            var state = (await scope.LockOrganizationAsync(f.Org, Ct))!;
            await floor.RequireAsync(scope, state, ClientEnvelopeCapability.Legacy, Ct);
            f.Store.DataKeyBindings.Add(EnvelopeCapabilityTestSupport.Binding(f.Org));
            EnvelopeCapabilityTestSupport.AssertRefusal(await Assert.ThrowsAsync<AuthorityException>(() => floor.RequireAsync(scope, state, ClientEnvelopeCapability.Legacy, Ct)));
        }

        EnvelopeCapabilityTestSupport.AssertRefusal(await Assert.ThrowsAsync<AuthorityException>(() => f.EvaluateAsync(floor, f.Org, ClientEnvelopeCapability.Legacy)));
        Assert.Equal(4, f.Store.DataKeyBindingExistenceQueries);
    }

    // ---- M2F-24 store failure (CAP-F5, AM-F1 #7) ----

    [Fact]
    public async Task M2F24AStoreFailureRefusesAsNotReadyAndNeverAdmits()
    {
        var f = new Fixture();
        var observer = new EnvelopeCapabilityTestSupport.CapturingObserver();
        var floor = EnvelopeCapabilityFloor.Create(null, observer);
        f.Store.DataKeyBindingExistenceFailure = new InvalidOperationException("relation \"authority.content_data_key_bindings\" does not exist");

        var ex = await Assert.ThrowsAsync<AuthorityException>(() => f.EvaluateAsync(floor, f.Org, ClientEnvelopeCapability.Legacy));
        Assert.Equal((AuthorityErrorCodes.AuthorityNotReady, 503), (ex.ErrorCode, ex.HttpStatusCode));
        Assert.Equal(EnvelopeCapabilityFloor.UnavailableDetail, ex.Detail);
        Assert.DoesNotContain("content_data_key_bindings", ex.Message, StringComparison.Ordinal);
        Assert.Equal([(f.Org, 1, EnvelopeCapabilityOutcomes.Unavailable)], observer.Events);

        // A store that throws OperationCanceledException without the caller's cancellation is a failure too, never an admit.
        f.Store.DataKeyBindingExistenceFailure = new OperationCanceledException();
        Assert.Equal(AuthorityErrorCodes.AuthorityNotReady, (await Assert.ThrowsAsync<AuthorityException>(() => f.EvaluateAsync(floor, f.Org, null))).ErrorCode);
    }

    [Fact]
    public async Task TheCallersCancellationPropagatesAndIsNeverAnAdmit()
    {
        var f = new Fixture();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        f.Store.DataKeyBindingExistenceFailure = new OperationCanceledException(cancelled.Token);
        await using var scope = await f.Store.BeginScopeAsync(Ct);
        var state = (await scope.LockOrganizationAsync(f.Org, Ct))!;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EnvelopeCapabilityFloor.Default.RequireAsync(scope, state, ClientEnvelopeCapability.Legacy, cancelled.Token));
    }

    // ---- M2F-27 refusal shape (CAP-E1/E2) and CAP-E4 ----

    [Fact]
    public async Task M2F27TheRefusalIsTheSameWhicheverFloorCausedIt()
    {
        var f = new Fixture();
        f.Store.DataKeyBindings.Add(EnvelopeCapabilityTestSupport.Binding(f.Org));
        var byDeployment = await Assert.ThrowsAsync<AuthorityException>(() => f.EvaluateAsync(EnvelopeCapabilityFloor.Create("2"), f.OtherOrg, ClientEnvelopeCapability.Legacy));
        var byOrganization = await Assert.ThrowsAsync<AuthorityException>(() => f.EvaluateAsync(EnvelopeCapabilityFloor.Create("1"), f.Org, ClientEnvelopeCapability.Legacy));

        Assert.Equal((byDeployment.ErrorCode, byDeployment.HttpStatusCode, byDeployment.Retryable, byDeployment.Detail, byDeployment.Message),
            (byOrganization.ErrorCode, byOrganization.HttpStatusCode, byOrganization.Retryable, byOrganization.Detail, byOrganization.Message));
        EnvelopeCapabilityTestSupport.AssertRefusal(byOrganization);
        foreach (var text in new[] { f.Org.ToString("D"), f.OtherOrg.ToString("D"), "floor", "binding", "level", "1", "2" })
        {
            Assert.DoesNotContain(text, byOrganization.Detail!, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task TheOperationalSignalCarriesOnlyOutcomeLevelAndOrganization()
    {
        var f = new Fixture();
        var observer = new EnvelopeCapabilityTestSupport.CapturingObserver();
        var floor = EnvelopeCapabilityFloor.Create("1", observer);
        await f.EvaluateAsync(floor, f.Org, ClientEnvelopeCapability.Current);
        await f.EvaluateAsync(floor, f.Org, null);
        f.Store.DataKeyBindings.Add(EnvelopeCapabilityTestSupport.Binding(f.Org));
        await Assert.ThrowsAsync<AuthorityException>(() => f.EvaluateAsync(floor, f.Org, ClientEnvelopeCapability.Legacy));

        Assert.Equal(
        [
            (f.Org, 2, EnvelopeCapabilityOutcomes.Admitted),
            (f.Org, 1, EnvelopeCapabilityOutcomes.Admitted),
            (f.Org, 1, EnvelopeCapabilityOutcomes.Refused),
        ], observer.Events);
        Assert.Equal(["Evaluated"], typeof(IEnvelopeCapabilityObserver).GetMethods().Select(m => m.Name));
        Assert.Equal([typeof(Guid), typeof(int), typeof(string)], typeof(IEnvelopeCapabilityObserver).GetMethod("Evaluated")!.GetParameters().Select(p => p.ParameterType));
    }

    // ---- M2F-20 store port (AM-F1 #1) ----

    [Fact]
    public void M2F20TheStorePortReturnsABooleanOnly()
    {
        var method = typeof(AuthorizationAuthority.Application.Common.Ports.IAuthorityScope).GetMethod(nameof(AuthorizationAuthority.Application.Common.Ports.IAuthorityScope.OrganizationHasDataKeyBindingsAsync))!;
        Assert.Equal(typeof(Task<bool>), method.ReturnType);
        Assert.Equal([typeof(Guid), typeof(CancellationToken)], method.GetParameters().Select(p => p.ParameterType));
    }
}
