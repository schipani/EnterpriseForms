using System.Reflection;
using System.Text.Json;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.SecurityOverview;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Catalogue;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// C8 M4-4 (AC-8.4, AC-8.5; AC-M4-4.1, AC-M4-4.2; T12 and T09 at unit level): the Effective-edge rule, the per-section role
/// matrix, the counts-only shapes and the coverage states, through the real services over the in-memory store. PostgreSQL
/// (REPEATABLE READ READ ONLY, the SQL aggregates) and the business path are exercised by Stage B <c>SecurityHomeTests</c>,
/// which is authored but not run here.
/// </summary>
public sealed class SecurityOverviewTests
{
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly Guid OtherOrg = Guid.CreateVersion7();
    private static readonly Guid Admin = Guid.CreateVersion7();
    private static readonly Guid Auditor = Guid.CreateVersion7();
    private static readonly Guid Member = Guid.CreateVersion7();
    private const string Issuer = "https://idp.test.invalid";
    private static readonly DateTimeOffset T0 = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryAuthorityStore store = new();
    private readonly FixedClock clock = new(T0.AddHours(2));

    public SecurityOverviewTests()
    {
        store.Organizations.Add(new OrganizationAuthorizationState { OrganizationId = Org, OrganizationDisplayName = "Overview Org", AuthorizationRevision = 12, ProjectionAppliedRevision = 12, ProjectionZedToken = "token", ContentIntegrityRevision = 40 });
        store.Organizations.Add(new OrganizationAuthorizationState { OrganizationId = OtherOrg, OrganizationDisplayName = "Other Org", AuthorizationRevision = 3, ProjectionAppliedRevision = 3, ProjectionZedToken = "token", ContentIntegrityRevision = 2 });
        foreach (var user in new[] { Admin, Auditor, Member })
        {
            Bind(Org, user);
        }

        Grant(Org, Admin, OrganizationRoleNames.SystemAdministrator, SecurityCommandStates.Effective);
        Grant(Org, Auditor, OrganizationRoleNames.SecurityAuditor, SecurityCommandStates.Effective);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Subject(Guid user) => "subject-" + user.ToString("N");

    private static VerifiedActor Actor(Guid user) => new(Issuer, Subject(user), "enterpriseforms-api");

    private SecurityOverviewService Overview() => new(store, clock);

    private SecurityCoverageService Coverage() => new(store, clock);

    private static SecurityOverviewRequest Request(Guid? organization = null) => new(organization ?? Org, Guid.CreateVersion7());

    private IdentityBinding Bind(Guid organization, Guid user, bool enabled = true)
    {
        var binding = new IdentityBinding { IdentityBindingId = Guid.CreateVersion7(), OrganizationId = organization, AuthorityUserId = user, IdentityIssuerUri = Issuer, IdentitySubjectIdentifier = Subject(user), IdentityBindingEnabled = enabled };
        store.Bindings.Add(binding);
        return binding;
    }

    private SecurityCommand Command(Guid organization, string state, string kind = "GrantSystemRole", DateTimeOffset? createdOn = null, long? revision = null)
    {
        var command = new SecurityCommand
        {
            SecurityCommandId = Guid.CreateVersion7(), OrganizationId = organization, ActorIdentityBindingId = Guid.CreateVersion7(), CommandKindName = kind, CommandIdempotencyKey = Guid.NewGuid().ToString("N"),
            CommandStateName = state, CommittedAuthorizationRevision = state == SecurityCommandStates.Pending ? null : revision ?? 5, CommandCreatedOn = createdOn ?? T0, CommandModifiedOn = createdOn ?? T0,
            CommandPayloadJson = "{\"secret_note\":\"SENTINEL-PAYLOAD\"}", CommandReasonText = "SENTINEL-REASON",
        };
        store.Commands.Add(command);
        return command;
    }

    private AuthorityRelationshipEdge Grant(Guid organization, Guid user, string role, string commandState, Guid? commandOrganization = null)
    {
        var command = Command(commandOrganization ?? organization, commandState);
        var edge = new AuthorityRelationshipEdge
        {
            AuthorityRelationshipEdgeId = Guid.CreateVersion7(), OrganizationId = organization, ResourceTypeName = ResourceTypeNames.Organization, ResourceEntityId = organization, RelationshipName = role,
            SubjectTypeName = ResourceTypeNames.User, SubjectEntityId = user, CreatedBySecurityCommandId = command.SecurityCommandId, RelationshipCreatedOn = T0,
        };
        store.Edges.Add(edge);
        return edge;
    }

    private async Task<bool> CanReadAsync(Guid user, string section, Guid? organization = null)
    {
        try
        {
            await Overview().ReadAsync(Actor(user), section, Request(organization), Ct);
            return true;
        }
        catch (AuthorityException ex) when (ex.HttpStatusCode == 404)
        {
            return false;
        }
    }

    // ---- Effective-edge rule (§5.4, AC-M4-4.1, T12) ----

    [Fact]
    public async Task AnEffectiveGrantAuthorizesTheAdministratorSections()
    {
        foreach (var section in SecurityOverviewSections.All)
        {
            Assert.True(await CanReadAsync(Admin, section), section);
        }
    }

    [Theory]
    [InlineData(SecurityCommandStates.Pending)]
    [InlineData(SecurityCommandStates.CommittedAwaitingProjection)]
    [InlineData(SecurityCommandStates.FailedReconciliation)]
    [InlineData(SecurityCommandStates.Rejected)]
    public async Task AGrantWhoseCommandIsNotEffectiveConfersNothing(string state)
    {
        var user = Guid.CreateVersion7();
        Bind(Org, user);
        Grant(Org, user, OrganizationRoleNames.SystemAdministrator, state);
        Grant(Org, user, OrganizationRoleNames.SecurityAuditor, state); // T12: a pending grant of either role
        foreach (var section in SecurityOverviewSections.All)
        {
            Assert.False(await CanReadAsync(user, section), section);
        }

        await Assert.ThrowsAsync<AuthorityException>(() => Coverage().ReadAsync(Actor(user), new SecurityCoverageRequest(Org, Guid.CreateVersion7()), Ct));
    }

    [Fact]
    public async Task ACommittedRevokeDeniesAtOnceEvenBeforeItIsProjected()
    {
        var user = Guid.CreateVersion7();
        Bind(Org, user);
        var edge = Grant(Org, user, OrganizationRoleNames.SystemAdministrator, SecurityCommandStates.Effective);
        Assert.True(await CanReadAsync(user, SecurityOverviewSections.Gate));

        // The revoke has committed (the edge is removed) but SpiceDB has not caught up: the gate is closed.
        var revoke = Command(Org, SecurityCommandStates.CommittedAwaitingProjection, "RevokeSystemRole", revision: 13);
        edge.RemovedBySecurityCommandId = revoke.SecurityCommandId;
        store.Organizations[0].AuthorizationRevision = 13;
        Assert.False(await CanReadAsync(user, SecurityOverviewSections.Gate));
        Assert.True(await CanReadAsync(Admin, SecurityOverviewSections.Gate)); // the other administrator still reads it
    }

    [Fact]
    public async Task ADisabledBindingConfersNothingAndReEnablingRestoresAccess()
    {
        var binding = store.Bindings.Single(b => b.OrganizationId == Org && b.AuthorityUserId == Admin);
        binding.IdentityBindingEnabled = false;
        Assert.False(await CanReadAsync(Admin, SecurityOverviewSections.Gate));
        binding.IdentityBindingEnabled = true;
        Assert.True(await CanReadAsync(Admin, SecurityOverviewSections.Gate));
    }

    [Fact]
    public async Task AnUnboundCallerAndAMemberWithoutARoleGetTheSameNotFound()
    {
        Assert.False(await CanReadAsync(Guid.CreateVersion7(), SecurityOverviewSections.Archive));
        Assert.False(await CanReadAsync(Member, SecurityOverviewSections.Archive));
        var ex = await Assert.ThrowsAsync<AuthorityException>(() => Overview().ReadAsync(Actor(Member), SecurityOverviewSections.Gate, Request(), Ct));
        Assert.Equal((404, AuthorityErrorCodes.NotFound), (ex.HttpStatusCode, ex.ErrorCode));
    }

    [Fact]
    public async Task AnotherOrganizationIsNotFoundAndItsGrantsDoNotCount() // T09
    {
        // An administrator of this organization asks for the other organization: not bound there.
        Assert.False(await CanReadAsync(Admin, SecurityOverviewSections.Gate, OtherOrg));
        await Assert.ThrowsAsync<AuthorityException>(() => Coverage().ReadAsync(Actor(Admin), new SecurityCoverageRequest(OtherOrg, Guid.CreateVersion7()), Ct));

        // Bound in the other organization, but its only role edge is in this one, or was granted by another organization's command.
        var user = Guid.CreateVersion7();
        Bind(OtherOrg, user);
        Grant(Org, user, OrganizationRoleNames.SystemAdministrator, SecurityCommandStates.Effective);
        Assert.False(await CanReadAsync(user, SecurityOverviewSections.Gate, OtherOrg));
        Grant(OtherOrg, user, OrganizationRoleNames.SystemAdministrator, SecurityCommandStates.Effective, commandOrganization: Org);
        Assert.False(await CanReadAsync(user, SecurityOverviewSections.Gate, OtherOrg));

        // An unknown organization is the same 404.
        Assert.False(await CanReadAsync(Admin, SecurityOverviewSections.Gate, Guid.CreateVersion7()));
    }

    [Fact]
    public async Task TheSectionRoleMatrixIsExact() // §5.4 table, D8-70, §4
    {
        Assert.False(await CanReadAsync(Auditor, SecurityOverviewSections.Gate));
        Assert.False(await CanReadAsync(Auditor, SecurityOverviewSections.Commands));
        Assert.True(await CanReadAsync(Auditor, SecurityOverviewSections.Archive));
        await Assert.ThrowsAsync<AuthorityException>(() => Coverage().ReadAsync(Actor(Auditor), new SecurityCoverageRequest(Org, Guid.CreateVersion7()), Ct));

        // Organization owners and designers hold no overview section.
        var owner = Guid.CreateVersion7();
        Bind(Org, owner);
        Grant(Org, owner, OrganizationRoleNames.OrganizationOwner, SecurityCommandStates.Effective);
        Grant(Org, owner, OrganizationRoleNames.Designer, SecurityCommandStates.Effective);
        foreach (var section in SecurityOverviewSections.All)
        {
            Assert.False(await CanReadAsync(owner, section), section);
        }

        // A dual holder reads everything.
        Grant(Org, Auditor, OrganizationRoleNames.SystemAdministrator, SecurityCommandStates.Effective);
        foreach (var section in SecurityOverviewSections.All)
        {
            Assert.True(await CanReadAsync(Auditor, section), section);
        }
    }

    [Fact]
    public async Task AnUnknownSectionIsRefusedBeforeAnyRead()
    {
        var ex = await Assert.ThrowsAsync<AuthorityException>(() => Overview().ReadAsync(Actor(Admin), "incidents", Request(), Ct));
        Assert.Equal(422, ex.HttpStatusCode);
        Assert.Equal(0, store.ReadOnlyScopeCount);
    }

    [Fact]
    public async Task EachReadUsesOneReadOnlySnapshotAndNoWriteScope() // AC-M4-4.1
    {
        foreach (var section in SecurityOverviewSections.All)
        {
            await Overview().ReadAsync(Actor(Admin), section, Request(), Ct);
        }

        await Coverage().ReadAsync(Actor(Admin), new SecurityCoverageRequest(Org, Guid.CreateVersion7()), Ct);
        Assert.Equal(4, store.ReadOnlyScopeCount);
        Assert.Equal(0, store.ScopeCount);
    }

    // ---- gate (T12) ----

    [Fact]
    public async Task WhileTheGateIsClosedAnEnabledAdministratorReadsGateAndCommands() // T12
    {
        store.Organizations[0].AuthorizationRevision = 15; // committed, not projected: the gate is closed for every SpiceDB check
        var gate = (await Overview().ReadAsync(Actor(Admin), SecurityOverviewSections.Gate, Request(), Ct)).Gate!;
        Assert.Equal(("AwaitingProjection", 15L, 12L, 40L, false), (gate.GateState, gate.AuthorizationRevision, gate.ProjectionAppliedRevision, gate.ContentIntegrityRevision, gate.IntegrityQuarantined));
        Assert.NotNull((await Overview().ReadAsync(Actor(Admin), SecurityOverviewSections.Commands, Request(), Ct)).Commands);

        store.Organizations[0].IntegrityQuarantineEnabled = true;
        gate = (await Overview().ReadAsync(Actor(Admin), SecurityOverviewSections.Gate, Request(), Ct)).Gate!;
        Assert.Equal(("Quarantined", true), (gate.GateState, gate.IntegrityQuarantined));
    }

    [Fact]
    public async Task AnOpenGateIsReportedReady()
    {
        var response = await Overview().ReadAsync(Actor(Admin), SecurityOverviewSections.Gate, Request(), Ct);
        Assert.Equal(("gate", "Ready"), (response.Section, response.Gate!.GateState));
        Assert.Null(response.Commands);
        Assert.Null(response.Archive);
        Assert.Equal(T0.AddHours(2), response.ComputedOn);
    }

    // ---- commands (OD-4) ----

    [Fact]
    public async Task CommandsCountOnlyUnresolvedStatesAndListTheOldestFirst()
    {
        var oldest = Command(Org, SecurityCommandStates.FailedReconciliation, SecurityCommandKinds.CreateShare, T0.AddMinutes(-30), 8);
        Command(Org, SecurityCommandStates.Pending, SecurityCommandKinds.TransferOwnership, T0.AddMinutes(10));
        Command(Org, SecurityCommandStates.CommittedAwaitingProjection, SecurityCommandKinds.RevokeShare, T0.AddMinutes(5), 13);
        Command(Org, SecurityCommandStates.Rejected, SecurityCommandKinds.CreateShare, T0.AddMinutes(-60));
        Command(OtherOrg, SecurityCommandStates.Pending, SecurityCommandKinds.CreateShare, T0.AddMinutes(-90));

        var commands = (await Overview().ReadAsync(Actor(Admin), SecurityOverviewSections.Commands, Request(), Ct)).Commands!;
        Assert.Equal(
            [("Pending", 1), ("CommittedAwaitingProjection", 1), ("FailedReconciliation", 1)],
            commands.StateCounts.Select(c => (c.CommandState, c.CommandCount)).ToList());
        Assert.Equal(oldest.CommandCreatedOn, commands.OldestUnresolvedCreatedOn);
        Assert.Equal((long)TimeSpan.FromMinutes(150).TotalSeconds, commands.OldestUnresolvedAgeSeconds);
        Assert.Equal(["CreateShare", "RevokeShare", "TransferOwnership"], commands.Commands.Select(c => c.CommandKind).ToList());
        Assert.DoesNotContain(commands.Commands, c => c.CommandState is "Rejected" or "Effective");
        Assert.Equal((8L, (long)TimeSpan.FromMinutes(150).TotalSeconds), (commands.Commands[0].CommittedAuthorizationRevision!.Value, commands.Commands[0].AgeSeconds));
        Assert.Null(commands.Commands[2].CommittedAuthorizationRevision); // Pending: no revision
    }

    [Fact]
    public async Task CommandsListAtMostTwentyAndCountAll()
    {
        for (var i = 0; i < 25; i++)
        {
            Command(Org, SecurityCommandStates.Pending, SecurityCommandKinds.CreateShare, T0.AddSeconds(i));
        }

        var commands = (await Overview().ReadAsync(Actor(Admin), SecurityOverviewSections.Commands, Request(), Ct)).Commands!;
        Assert.Equal(25, commands.StateCounts.Single(s => s.CommandState == "Pending").CommandCount);
        Assert.Equal(20, commands.Commands.Count);
        Assert.Equal(T0, commands.Commands[0].CreatedOn);
    }

    [Fact]
    public async Task NoUnresolvedCommandsReportsZeroCountsAndNoAge()
    {
        var commands = (await Overview().ReadAsync(Actor(Admin), SecurityOverviewSections.Commands, Request(), Ct)).Commands!;
        Assert.All(commands.StateCounts, c => Assert.Equal(0, c.CommandCount));
        Assert.Equal(3, commands.StateCounts.Count);
        Assert.Null(commands.OldestUnresolvedCreatedOn);
        Assert.Null(commands.OldestUnresolvedAgeSeconds);
        Assert.Empty(commands.Commands);
    }

    // ---- archive ----

    [Fact]
    public async Task ArchiveReportsEveryStreamWithCountsOldestPendingAndLastAnchored()
    {
        void Entry(Guid organization, string stream, long revision, string delivery, DateTimeOffset created, DateTimeOffset? archived = null) => store.Journal.Add(new SecurityJournalEntry
        {
            SecurityJournalEntryId = Guid.CreateVersion7(), OrganizationId = organization, JournalStreamName = stream, JournalStreamRevision = revision, JournalEventKind = "Sentinel",
            JournalEventBodyJson = "{\"body\":{\"note\":\"SENTINEL-JOURNAL\"}}", ArchiveDeliveryState = delivery, JournalCreatedOn = created, JournalArchivedOn = archived,
        });
        Entry(Org, JournalStreamNames.Audit, 1, ArchiveDeliveryStates.Anchored, T0, T0.AddMinutes(1));
        Entry(Org, JournalStreamNames.Audit, 2, ArchiveDeliveryStates.Anchored, T0.AddMinutes(2), T0.AddMinutes(3));
        Entry(Org, JournalStreamNames.Audit, 3, ArchiveDeliveryStates.Pending, T0.AddMinutes(30));
        Entry(Org, JournalStreamNames.Content, 1, ArchiveDeliveryStates.VerificationFailed, T0);
        Entry(OtherOrg, JournalStreamNames.Authorization, 1, ArchiveDeliveryStates.Pending, T0.AddMinutes(-100));

        var response = await Overview().ReadAsync(Actor(Auditor), SecurityOverviewSections.Archive, Request(), Ct);
        var streams = response.Archive!.Streams.ToDictionary(s => s.StreamName);
        Assert.Equal(["authorization", "content", "audit"], response.Archive.Streams.Select(s => s.StreamName).ToList());
        Assert.Equal((0, 0, 0, (DateTimeOffset?)null, (DateTimeOffset?)null), (streams["authorization"].PendingCount, streams["authorization"].AnchoredCount, streams["authorization"].VerificationFailedCount, streams["authorization"].OldestPendingCreatedOn, streams["authorization"].LastAnchoredOn));
        Assert.Equal((1, 2, 0), (streams["audit"].PendingCount, streams["audit"].AnchoredCount, streams["audit"].VerificationFailedCount));
        Assert.Equal((T0.AddMinutes(30), (long?)5400, T0.AddMinutes(3)), (streams["audit"].OldestPendingCreatedOn, streams["audit"].OldestPendingAgeSeconds, streams["audit"].LastAnchoredOn));
        Assert.Equal(1, streams["content"].VerificationFailedCount);
        Assert.Contains("RR-6", response.Archive.ArchiveStatement, StringComparison.Ordinal);
        Assert.Contains("No production immutability claim", response.Archive.ArchiveStatement, StringComparison.Ordinal);
    }

    // ---- counts and kinds only (AC-M4-4.2) ----

    [Fact]
    public async Task ResponsesCarryNoPayloadReasonTargetOrJournalBody()
    {
        Command(Org, SecurityCommandStates.Pending, SecurityCommandKinds.CreateShare);
        store.Journal.Add(new SecurityJournalEntry { SecurityJournalEntryId = Guid.CreateVersion7(), OrganizationId = Org, JournalStreamName = JournalStreamNames.Audit, JournalStreamRevision = 1, JournalEventKind = "X", JournalEventBodyJson = "{\"body\":{\"note\":\"SENTINEL-JOURNAL\"}}", JournalCreatedOn = T0 });
        foreach (var section in SecurityOverviewSections.All)
        {
            var json = JsonSerializer.Serialize(await Overview().ReadAsync(Actor(Admin), section, Request(), Ct));
            Assert.DoesNotContain("SENTINEL", json, StringComparison.Ordinal);
            Assert.DoesNotContain(Admin.ToString("D"), json, StringComparison.OrdinalIgnoreCase);
            foreach (var command in store.Commands)
            {
                Assert.DoesNotContain(command.SecurityCommandId.ToString("D"), json, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>The wire shapes are an allowlist: a new property is a contract change and must be added here deliberately.</summary>
    [Fact]
    public void TheOverviewAndCoverageShapesAreAllowlisted()
    {
        static string[] Props(Type t) => [.. t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).Order(StringComparer.Ordinal)];
        Assert.Equal(["AgeSeconds", "CommandKind", "CommandState", "CommittedAuthorizationRevision", "CreatedOn"], Props(typeof(SecurityOverviewCommand)));
        Assert.Equal(["AuthorizationRevision", "ContentIntegrityRevision", "GateState", "IntegrityQuarantined", "ProjectionAppliedRevision"], Props(typeof(SecurityOverviewGate)));
        Assert.Equal(["AnchoredCount", "LastAnchoredOn", "OldestPendingAgeSeconds", "OldestPendingCreatedOn", "PendingCount", "StreamName", "VerificationFailedCount"], Props(typeof(SecurityOverviewArchiveStreamState)));
        Assert.Equal(["Attestation", "AttestationState", "FieldDefinitionId", "FormDefinitionId", "FormRegisteredActive", "ProtectionDirection", "SecurityCommandId"], Props(typeof(SecurityCoverageDecision)));
        Assert.Equal(
            ["AcceptedRecordCount", "AttestationComplete", "AttestationId", "AttestationIncompleteReasonCode", "AttestedContentIntegrityRevision", "AttestedOn", "AuditJournalRevision", "CompliantRecordCount",
             "ExpectedRecordCount", "NotYetCensusedRecordCount", "PendingCreationRecordCount", "QuarantinedRecordCount", "RetainedHistoryRevisionCount", "RetiredRecordCount", "UnverifiedRecordCount"],
            Props(typeof(SecurityCoverageAttestation)));
    }

    // ---- coverage (AC-8.5, OD-1, OD-3) ----

    private (Guid Command, Guid Form, Guid Field) Decision(Guid organization)
    {
        var command = Command(organization, SecurityCommandStates.Effective, SecurityCommandKinds.PublishFormSecurityRevision);
        var form = Guid.CreateVersion7();
        var field = Guid.CreateVersion7();
        store.Resources.Add(new AuthorityResource { AuthorityResourceId = Guid.CreateVersion7(), OrganizationId = organization, ResourceTypeName = ResourceTypeNames.FormDefinition, ResourceEntityId = form, ResourceLifecycleState = ResourceLifecycleStates.Active, RegisteredBySecurityCommandId = command.SecurityCommandId });
        store.Resources.Add(new AuthorityResource { AuthorityResourceId = Guid.CreateVersion7(), OrganizationId = organization, ResourceTypeName = ResourceTypeNames.FieldProtectionRequirement, ResourceEntityId = field, ParentResourceEntityId = form, ResourceLifecycleState = ResourceLifecycleStates.Active, RegisteredBySecurityCommandId = command.SecurityCommandId });
        return (command.SecurityCommandId, form, field);
    }

    private ProtectionReconciliationAttestation Attest((Guid Command, Guid Form, Guid Field) decision, long contentIntegrityRevision, bool complete = true)
    {
        var attestation = new ProtectionReconciliationAttestation
        {
            ProtectionReconciliationAttestationId = Guid.CreateVersion7(), OrganizationId = Org, ScopingSecurityCommandId = decision.Command, FormDefinitionId = decision.Form, FieldDefinitionId = decision.Field,
            ProtectionDirectionName = "Encrypt", SearchCatalogueSnapshotId = Guid.CreateVersion7(), ExpectedRecordCount = 7, AcceptedRecordCount = 6, PendingCreationRecordCount = 1, CompliantRecordCount = complete ? 6 : 5,
            UnverifiedRecordCount = complete ? 0 : 1, RetiredRecordCount = 2, RetainedHistoryRevisionCount = 9, AttestationComplete = complete, AttestationIncompleteReason = complete ? null : ProtectionAttestationReasons.Unverified,
            AttestationDigest = [1, 2, 3], AttestedAuthorizationRevision = 12, AttestedContentIntegrityRevision = contentIntegrityRevision, AttestedAuditJournalRevision = 33, RequestingWorkloadName = "verifier", AttestedOn = T0,
        };
        store.Attestations.Add(attestation);
        return attestation;
    }

    [Fact]
    public async Task CoverageReportsMissingStaleAndNoLaterChangeAgainstTheOrganizationRevision()
    {
        var missing = Decision(Org);
        var stale = Decision(Org);
        var current = Decision(Org);
        Attest(stale, contentIntegrityRevision: 39); // complete as recorded, but stale: counted complete and stale
        var currentAttestation = Attest(current, contentIntegrityRevision: 40, complete: false);
        Decision(OtherOrg); // never in this organization's report

        var coverage = await Coverage().ReadAsync(Actor(Admin), new SecurityCoverageRequest(Org, Guid.CreateVersion7()), Ct);
        var byField = coverage.Decisions.ToDictionary(d => d.FieldDefinitionId);
        Assert.Equal((3, 3, 1, 1, 1, 40L), (coverage.DecisionCount, coverage.RegisteredFormCount, coverage.AttestedCompleteDecisionCount, coverage.MissingDecisionCount, coverage.StaleDecisionCount, coverage.OrganizationContentIntegrityRevision));
        Assert.Equal(SecurityCoverageAttestationStates.Missing, byField[missing.Field].AttestationState);
        Assert.Null(byField[missing.Field].Attestation);
        Assert.Equal(SecurityCoverageAttestationStates.StaleOrganizationWide, byField[stale.Field].AttestationState);
        Assert.Equal(SecurityCoverageAttestationStates.NoLaterOrganizationContentChange, byField[current.Field].AttestationState);
        var a = byField[current.Field].Attestation!;
        Assert.Equal((currentAttestation.ProtectionReconciliationAttestationId, false, "RECORDS_UNVERIFIED", 7, 6, 5, 1, 1, 33L, 40L), (a.AttestationId, a.AttestationComplete, a.AttestationIncompleteReasonCode, a.ExpectedRecordCount, a.AcceptedRecordCount, a.CompliantRecordCount, a.PendingCreationRecordCount, a.UnverifiedRecordCount, a.AuditJournalRevision, a.AttestedContentIntegrityRevision));
        Assert.Contains("organization-wide", coverage.StalenessStatement, StringComparison.Ordinal);
        Assert.Contains("not a compliance certification", coverage.CoverageStatement, StringComparison.Ordinal);
        Assert.Contains("a possibly stale attestation is still counted as complete and is not a current verification", coverage.CoverageStatement, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CoverageCarriesNoRecordIdsDigestsOrSnapshotIds()
    {
        var decision = Decision(Org);
        var attestation = Attest(decision, 40);
        var record = Guid.CreateVersion7();
        store.Resources.Add(new AuthorityResource { AuthorityResourceId = Guid.CreateVersion7(), OrganizationId = Org, ResourceTypeName = ResourceTypeNames.FormRecord, ResourceEntityId = record, ParentResourceEntityId = decision.Form, ResourceLifecycleState = ResourceLifecycleStates.Active });

        var json = JsonSerializer.Serialize(await Coverage().ReadAsync(Actor(Admin), new SecurityCoverageRequest(Org, Guid.CreateVersion7()), Ct));
        Assert.DoesNotContain(record.ToString("D"), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(attestation.SearchCatalogueSnapshotId!.Value.ToString("D"), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("010203", json, StringComparison.Ordinal);
        Assert.DoesNotContain("verifier", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CoverageIsAvailableWhileTheGateIsClosedAndUnchangedForTheVerifierRoute()
    {
        var decision = Decision(Org);
        Attest(decision, 40);
        store.Organizations[0].AuthorizationRevision = 20; // gate closed
        var user = await Coverage().ReadAsync(Actor(Admin), new SecurityCoverageRequest(Org, Guid.CreateVersion7()), Ct);
        Assert.Single(user.Decisions);

        // The verifier projection is the same computation: same decision identities and counts.
        var reconciliation = new AuthorizationAuthority.Application.Features.Catalogue.ProtectionReconciliationService(store, null!, clock);
        var verifier = await reconciliation.CoverageAsync(new ProtectionCoverageRequest(Org), Guid.CreateVersion7(), Ct);
        Assert.Equal(verifier.Decisions.Select(d => (d.SecurityCommandId, d.FieldDefinitionId, d.LatestAttestation!.ExpectedRecordCount)), user.Decisions.Select(d => (d.SecurityCommandId, d.FieldDefinitionId, d.Attestation!.ExpectedRecordCount)));
        Assert.Equal(1, store.ScopeCount); // the verifier route keeps its own scope; the user plane opened none
    }

    [Theory]
    [InlineData(null, "Missing")]
    [InlineData(39L, "StaleOrganizationWide")]
    [InlineData(40L, "NoLaterOrganizationContentChange")]
    [InlineData(41L, "NoLaterOrganizationContentChange")]
    public void StalenessIsTheOrganizationRevisionComparison(long? attestedRevision, string expected)
    {
        ProtectionAttestation? attestation = attestedRevision is { } revision
            ? new ProtectionAttestation(Guid.Empty, Org, Guid.Empty, Guid.Empty, Guid.Empty, "Encrypt", null, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, true, null, "", 1, revision, 1, T0, "", Guid.Empty)
            : null;
        Assert.Equal(expected, SecurityCoverageService.StateOf(attestation, 40));
    }

    // ---- guard (AC-M4-4.1) ----

    [Fact]
    public void OnlyTheOverviewAndCoverageServicesUseTheEffectiveEdgeRule()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src", "authority")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var users = Directory.EnumerateFiles(Path.Combine(root.FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains("EffectiveEdgeRule", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(["EffectiveEdgeRule.cs", "SecurityCoverageService.cs", "SecurityOverviewService.cs"], users);
        var rule = typeof(SecurityOverviewService).Assembly.GetType("AuthorizationAuthority.Application.Features.SecurityOverview.EffectiveEdgeRule");
        Assert.NotNull(rule);
        Assert.False(rule.IsPublic); // internal to the Application assembly
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
