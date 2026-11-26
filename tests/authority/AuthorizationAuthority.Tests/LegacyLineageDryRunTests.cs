using System.Text.Json;
using AuthorizationAuthority.Api.Composition;
using AuthorizationAuthority.Application.Features.Lineage;
using AuthorizationAuthority.Domain.Features.Lineage;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using AuthorizationAuthority.Domain.Features.Sharing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// C10 M1-WP10: the verified evidence assembly (historical grantor and ownership from committed command facts), the read-only
/// dry run over multi-level legacy lineage, and the operator command's refusals. Offline: no database.
/// </summary>
public sealed class LegacyLineageDryRunTests
{
    private static readonly DateTimeOffset Observed = new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    /// <summary>A synthetic organization: users, bindings, one record and its committed history.</summary>
    internal sealed class Fixture
    {
        public Guid Org { get; } = Guid.CreateVersion7();
        public Guid Record { get; } = Guid.CreateVersion7();
        public Guid Field1 { get; } = Guid.CreateVersion7();
        public Guid Field2 { get; } = Guid.CreateVersion7();
        public List<AuthorityResource> Shares { get; } = [];
        public List<ShareResourceMetadata> Metadata { get; } = [];
        public List<AuthorityRelationshipEdge> Edges { get; } = [];
        public List<LineageCommandFact> Commands { get; } = [];
        public Dictionary<Guid, Guid> Bindings { get; } = [];
        private readonly Dictionary<Guid, Guid> bindingOfUser = [];
        private long revision;

        public Guid User()
        {
            var user = Guid.CreateVersion7();
            var binding = Guid.CreateVersion7();
            Bindings[binding] = user;
            bindingOfUser[user] = binding;
            return user;
        }

        public Guid Command(string kind, Guid? actor = null, string state = SecurityCommandStates.Effective, bool committed = true)
        {
            var id = Guid.CreateVersion7();
            Commands.Add(new LineageCommandFact(id, Org, actor is { } a ? bindingOfUser[a] : Guid.CreateVersion7(), kind, state,
                committed ? ++revision : null));
            return id;
        }

        public void Edge(string type, Guid id, string relation, string subjectType, Guid subject, string subjectRelation, Guid createdBy, Guid? removedBy = null) =>
            Edges.Add(new AuthorityRelationshipEdge
            {
                AuthorityRelationshipEdgeId = Guid.CreateVersion7(),
                OrganizationId = Org,
                ResourceTypeName = type,
                ResourceEntityId = id,
                RelationshipName = relation,
                SubjectTypeName = subjectType,
                SubjectEntityId = subject,
                SubjectRelationName = subjectRelation,
                CreatedBySecurityCommandId = createdBy,
                RelationshipCreatedOn = Observed.AddDays(-10),
                RemovedBySecurityCommandId = removedBy,
            });

        public Guid UserOwner(Guid owner) { var c = Command(SecurityCommandKinds.RegisterRecord, owner); Edge(ResourceTypeNames.FormRecord, Record, "owner", ResourceTypeNames.User, owner, "", c); return c; }

        /// <summary>A legacy share (no metadata row) granted by <paramref name="grantor"/>.</summary>
        public Guid Share(Guid grantor, Guid recipient, string role, Guid[]? fields = null, bool team = false, Guid? registeredBy = null)
        {
            var id = Guid.CreateVersion7();
            var command = registeredBy ?? Command(SecurityCommandKinds.CreateShare, grantor);
            Shares.Add(new AuthorityResource
            {
                AuthorityResourceId = Guid.CreateVersion7(),
                OrganizationId = Org,
                ResourceTypeName = ResourceTypeNames.RecordShare,
                ResourceEntityId = id,
                ResourceLifecycleState = ResourceLifecycleStates.Active,
                ParentResourceEntityId = Record,
                RegisteredBySecurityCommandId = command,
                ResourceRegisteredOn = Observed.AddDays(-5),
            });
            Edge(ResourceTypeNames.RecordShare, id, role, team ? ResourceTypeNames.Team : ResourceTypeNames.User, recipient, team ? "participant" : "", command);
            foreach (var field in fields ?? [Field1])
            {
                Edge(ResourceTypeNames.FieldItem, field, "shared_via", ResourceTypeNames.RecordShare, id, "", command);
            }

            return id;
        }

        public LineageStoreRows Rows() => new()
        {
            OrganizationId = Org,
            AuthorizationRevision = revision,
            ObservedOn = Observed,
            ActiveShares = Shares,
            ShareMetadata = Metadata,
            Edges = Edges,
            Commands = Commands,
            IdentityBindingUsers = Bindings,
        };

        public LegacyLineageClassificationReport Run() =>
            LegacyLineageDryRun.Run(LineageEvidenceAssembler.Assemble(Rows()), new LineagePseudonymizer(Org, Key));
    }

    private static LegacyShareClassificationEntry Entry(LegacyLineageClassificationReport report, Guid share) =>
        Assert.Single(report.Entries, e => e.RecordShareId == share);

    private static void AssertValid(LegacyLineageClassificationReport report)
    {
        var validation = LegacyLineageReportValidator.Validate(report);
        Assert.True(validation.IsValid, string.Join("; ", validation.Issues));
    }

    [Fact]
    public void MultiLevelLegacyLineageResolvesFromAVerifiedOwnerRootDownToTheDepthLimit()
    {
        var f = new Fixture();
        var (owner, lead, teamLead, worker, extra) = (f.User(), f.User(), f.User(), f.User(), f.User());
        f.UserOwner(owner);
        var s1 = f.Share(owner, lead, "manager", [f.Field1, f.Field2]);
        var s2 = f.Share(lead, teamLead, "manager", [f.Field1, f.Field2]);
        var s3 = f.Share(teamLead, worker, "reader");
        var s4 = f.Share(teamLead, extra, "manager");

        var report = f.Run();

        Assert.Equal(LegacyShareClassificationOutcome.OwnerGranted, Entry(report, s1).Classification);
        Assert.Equal((s1, 2), (Entry(report, s2).ProposedParentShareId!.Value, Entry(report, s2).ProposedDelegationDepth!.Value));
        Assert.Equal(LegacyShareClassificationOutcome.UnambiguousParent, Entry(report, s3).Classification);
        Assert.Equal((s2, 3), (Entry(report, s3).ProposedParentShareId!.Value, Entry(report, s3).ProposedDelegationDepth!.Value));
        Assert.Equal(LegacyShareClassificationOutcome.ExceedsDepth, Entry(report, s4).Classification); // no Manager at depth 3
        Assert.True(report.IsEvidenceComplete);
        AssertValid(report);
    }

    [Fact]
    public void AnUnverifiableRootIsRefusedAndItsLegacyDescendantsAreRefusedNotInvalidated()
    {
        var f = new Fixture();
        var (owner, lead, worker) = (f.User(), f.User(), f.User());
        // The owner edge was created by a command the store cannot produce: ownership history is unverifiable.
        f.Edge(ResourceTypeNames.FormRecord, f.Record, "owner", ResourceTypeNames.User, owner, "", Guid.CreateVersion7());
        var s1 = f.Share(owner, lead, "manager");
        var s2 = f.Share(lead, worker, "reader");

        var report = f.Run();

        Assert.Equal(InsufficientEvidenceReason.UnverifiableOwnership, Entry(report, s1).EvidenceValidationError?.Reason);
        Assert.Equal(InsufficientEvidenceReason.UnverifiableOwnership, Entry(report, s2).EvidenceValidationError?.Reason);
        Assert.DoesNotContain(report.Entries, e => e.Classification is LegacyShareClassificationOutcome.NoCoveringParent
            or LegacyShareClassificationOutcome.UnambiguousParent or LegacyShareClassificationOutcome.OwnerGranted);
        Assert.False(report.IsEvidenceComplete);
        Assert.Equal(2, report.EvidenceRefusals.Count);
        AssertValid(report);
    }

    [Fact]
    public void ACoveringLegacyManagerWithAnUnattributableRegistrationBlocksAnInvalidationClaim()
    {
        var f = new Fixture();
        var (owner, lead, worker) = (f.User(), f.User(), f.User());
        f.UserOwner(owner);
        // The lead's Manager share was registered by a command that never became Effective: its grantor is unknown.
        var s1 = f.Share(owner, lead, "manager", registeredBy: f.Command(SecurityCommandKinds.CreateShare, owner, SecurityCommandStates.Rejected, committed: false));
        var s2 = f.Share(lead, worker, "reader");

        var report = f.Run();

        Assert.Equal(InsufficientEvidenceReason.UnknownGrantor, Entry(report, s1).EvidenceValidationError?.Reason);
        Assert.Null(Entry(report, s1).GrantorPseudonymousReference);
        var child = Entry(report, s2);
        Assert.Equal(LegacyShareClassificationOutcome.InsufficientEvidence, child.Classification);
        Assert.Equal(InsufficientEvidenceReason.UnresolvedCandidateParent, child.EvidenceValidationError?.Reason);
        Assert.NotNull(child.GrantorPseudonymousReference);
        AssertValid(report);
    }

    [Fact]
    public void OwnershipIsEvaluatedAtTheRegistrationRevisionNotNow()
    {
        var f = new Fixture();
        var (formerOwner, newOwner, recipient, later) = (f.User(), f.User(), f.User(), f.User());
        var registered = f.UserOwner(formerOwner);
        var before = f.Share(formerOwner, recipient, "reader");
        var transfer = f.Command(SecurityCommandKinds.TransferOwnership, formerOwner);
        f.Edges.Single(e => e.CreatedBySecurityCommandId == registered).RemovedBySecurityCommandId = transfer;
        f.Edge(ResourceTypeNames.FormRecord, f.Record, "owner", ResourceTypeNames.User, newOwner, "", transfer);
        var after = f.Share(formerOwner, later, "reader");

        var report = f.Run();

        Assert.Equal(LegacyShareClassificationOutcome.OwnerGranted, Entry(report, before).Classification);
        var nonOwner = Entry(report, after);
        Assert.Equal(LegacyShareClassificationOutcome.NoCoveringParent, nonOwner.Classification); // verified non-owner, no Manager share
        Assert.Null(nonOwner.EvidenceValidationError);
        AssertValid(report);
    }

    [Fact]
    public void ATeamOwnerCountsOnlyMembersAtTheRegistrationRevision()
    {
        var f = new Fixture();
        var (member, laterMember, recipient) = (f.User(), f.User(), f.User());
        var team = Guid.CreateVersion7();
        f.Edge(ResourceTypeNames.Team, team, "member", ResourceTypeNames.User, member, "", f.Command(SecurityCommandKinds.AddTeamMember, member));
        f.Edge(ResourceTypeNames.FormRecord, f.Record, "owner", ResourceTypeNames.Team, team, "participant", f.Command(SecurityCommandKinds.RegisterRecord, member));
        var byMember = f.Share(member, recipient, "reader");
        var byEarly = f.Share(laterMember, recipient, "contributor");
        f.Edge(ResourceTypeNames.Team, team, "member", ResourceTypeNames.User, laterMember, "", f.Command(SecurityCommandKinds.AddTeamMember, member));

        var report = f.Run();

        Assert.Equal(LegacyShareClassificationOutcome.OwnerGranted, Entry(report, byMember).Classification);
        Assert.Equal(LegacyShareClassificationOutcome.NoCoveringParent, Entry(report, byEarly).Classification);
    }

    [Fact]
    public void AnUncommittedMembershipChangeMakesTeamOwnershipUnverifiable()
    {
        var f = new Fixture();
        var (member, recipient) = (f.User(), f.User());
        var team = Guid.CreateVersion7();
        f.Edge(ResourceTypeNames.Team, team, "member", ResourceTypeNames.User, member, "",
            f.Command(SecurityCommandKinds.AddTeamMember, member, SecurityCommandStates.Pending, committed: false));
        f.Edge(ResourceTypeNames.FormRecord, f.Record, "owner", ResourceTypeNames.Team, team, "participant", f.Command(SecurityCommandKinds.RegisterRecord, member));
        var share = f.Share(member, recipient, "reader");

        Assert.Equal(InsufficientEvidenceReason.UnverifiableOwnership, Entry(f.Run(), share).EvidenceValidationError?.Reason);
    }

    [Fact]
    public void AnUnboundActorIsAnUnknownGrantor()
    {
        var f = new Fixture();
        var (owner, recipient) = (f.User(), f.User());
        f.UserOwner(owner);
        var share = f.Share(owner, recipient, "reader");
        f.Bindings.Clear();

        var entry = Entry(f.Run(), share);
        Assert.Equal(InsufficientEvidenceReason.UnknownGrantor, entry.EvidenceValidationError?.Reason);
        Assert.Null(entry.ProposedDelegationDepth);
    }

    [Fact]
    public void TwoUserOwnersAtTheRegistrationRevisionAreUnverifiableAndAUserPlusTeamIsAmbiguous()
    {
        var f = new Fixture();
        var (a, b, recipient) = (f.User(), f.User(), f.User());
        f.UserOwner(a);
        f.UserOwner(b);
        var two = f.Share(a, recipient, "reader");
        Assert.Equal(InsufficientEvidenceReason.UnverifiableOwnership, Entry(f.Run(), two).EvidenceValidationError?.Reason);

        var g = new Fixture();
        var (c, r2) = (g.User(), g.User());
        var team = Guid.CreateVersion7();
        g.UserOwner(c);
        g.Edge(ResourceTypeNames.FormRecord, g.Record, "owner", ResourceTypeNames.Team, team, "participant", g.Command(SecurityCommandKinds.RegisterRecord, c));
        var mixed = g.Share(c, r2, "reader");
        Assert.Equal(InsufficientEvidenceReason.AmbiguousOwnership, Entry(g.Run(), mixed).EvidenceValidationError?.Reason);
    }

    [Fact]
    public void ACommittedLineageShareServesAsAParentButIsNotItselfReported()
    {
        var f = new Fixture();
        var (owner, lead, worker) = (f.User(), f.User(), f.User());
        f.UserOwner(owner);
        var committed = f.Share(owner, lead, "manager");
        f.Metadata.Add(new ShareResourceMetadata
        {
            ShareResourceMetadataId = Guid.CreateVersion7(),
            OrganizationId = f.Org,
            RecordShareId = committed,
            DelegationDepth = 1,
        });
        var legacy = f.Share(lead, worker, "reader");

        var report = f.Run();

        Assert.DoesNotContain(report.Entries, e => e.RecordShareId == committed);
        Assert.Equal(committed, Entry(report, legacy).ProposedParentShareId);
        Assert.Equal(1, Assert.Single(report.Summaries).TotalSharesEvaluated);
    }

    [Fact]
    public void AManagerWhoseScopeDoesNotCoverIsAVerifiedNoCoveringParent()
    {
        var f = new Fixture();
        var (owner, lead, worker) = (f.User(), f.User(), f.User());
        f.UserOwner(owner);
        f.Share(owner, lead, "manager", [f.Field1]);
        var wider = f.Share(lead, worker, "reader", [f.Field1, f.Field2]);

        var entry = Entry(f.Run(), wider);
        Assert.Equal(LegacyShareClassificationOutcome.NoCoveringParent, entry.Classification);
        Assert.Null(entry.EvidenceValidationError);
    }

    [Fact]
    public void TheReportCarriesNoRawIdentifiersOfPeopleOrFields()
    {
        var f = new Fixture();
        var (owner, lead, worker) = (f.User(), f.User(), f.User());
        f.UserOwner(owner);
        f.Share(owner, lead, "manager");
        f.Share(lead, worker, "reader");
        var json = JsonSerializer.Serialize(f.Run());

        foreach (var id in new[] { owner, lead, worker, f.Field1 }.Concat(f.Bindings.Keys))
        {
            Assert.DoesNotContain(id.ToString("D"), json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void EvidenceOfAnotherOrganizationOrAMalformedShareIsRefused()
    {
        var f = new Fixture();
        var (owner, recipient) = (f.User(), f.User());
        f.UserOwner(owner);
        var share = f.Share(owner, recipient, "reader");
        var rows = f.Rows();

        Assert.Throws<InvalidOperationException>(() => LineageEvidenceAssembler.Assemble(rows with
        {
            Edges = [.. rows.Edges, new AuthorityRelationshipEdge
            {
                AuthorityRelationshipEdgeId = Guid.CreateVersion7(),
                OrganizationId = Guid.CreateVersion7(),
                ResourceTypeName = ResourceTypeNames.FormRecord,
                ResourceEntityId = f.Record,
                RelationshipName = "owner",
                SubjectTypeName = ResourceTypeNames.User,
                SubjectEntityId = recipient,
                CreatedBySecurityCommandId = f.Commands[0].SecurityCommandId,
            }],
        }));
        Assert.Throws<InvalidOperationException>(() => LineageEvidenceAssembler.Assemble(rows with { AuthorizationRevision = 0 }));
        f.Edge(ResourceTypeNames.RecordShare, share, "contributor", ResourceTypeNames.User, owner, "", f.Commands[^1].SecurityCommandId);
        Assert.Throws<InvalidOperationException>(() => f.Run()); // two current recipients
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void TheCommandIsRefusedOutsideDevelopmentAndTesting(string environment)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:AuthorityDbReadOnly"] = "Host=127.0.0.1;Port=9;Database=none",
        }).Build();
        Assert.True(LegacyLineageDryRunCommand.TryRun(
            [LegacyLineageDryRunCommand.Flag, "--organization", Guid.CreateVersion7().ToString("D"), "--output", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))],
            configuration, environment, out var exit));
        Assert.Equal(LegacyLineageDryRunCommand.Usage, exit);
    }

    public static TheoryData<string> MalformedArguments =>
    [
        "--organization|not-a-guid|--output|x.json",
        "--output|x.json",
        "--organization|0199a2b3-c4d5-7e6f-8123-456789abcdef",
        "--organization|0199a2b3-c4d5-7e6f-8123-456789abcdef|--output|x.json|--migrate",
        "--organization|0199a2b3-c4d5-7e6f-8123-456789abcdef|--output|x.json|--ConnectionStrings:AuthorityDb=Host=x",
        "--organization|0199a2b3-c4d5-7e6f-8123-456789abcdef|--organization|0199a2b3-c4d5-7e6f-8123-456789abcdee|--output|x.json",
    ];

    [Theory]
    [MemberData(nameof(MalformedArguments))]
    public void MalformedOrCombinedArgumentsAreUsageErrors(string options)
    {
        Assert.True(LegacyLineageDryRunCommand.TryRun([LegacyLineageDryRunCommand.Flag, .. options.Split('|')], new ConfigurationBuilder().Build(), "Testing", out var exit));
        Assert.Equal(LegacyLineageDryRunCommand.Usage, exit);
    }

    [Fact]
    public void AMissingReadOnlyConnectionAndAnExistingOutputAreRefusedBeforeAnyRead()
    {
        var organization = Guid.CreateVersion7().ToString("D");
        var output = Path.Combine(Path.GetTempPath(), "c10-wp10-" + Guid.NewGuid().ToString("N") + ".json");
        var withRuntimeOnly = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:AuthorityDb"] = "Host=127.0.0.1;Port=9;Database=none",
            ["ConnectionStrings:AuthorityDbMigration"] = "Host=127.0.0.1;Port=9;Database=none",
        }).Build();
        Assert.True(LegacyLineageDryRunCommand.TryRun([LegacyLineageDryRunCommand.Flag, "--organization", organization, "--output", output], withRuntimeOnly, "Testing", out var exit));
        Assert.Equal(LegacyLineageDryRunCommand.Usage, exit);

        File.WriteAllText(output, "existing");
        try
        {
            var configured = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:AuthorityDbReadOnly"] = "Host=127.0.0.1;Port=9;Database=none",
            }).Build();
            Assert.True(LegacyLineageDryRunCommand.TryRun([LegacyLineageDryRunCommand.Flag, "--organization", organization, "--output", output], configured, "Testing", out exit));
            Assert.Equal(LegacyLineageDryRunCommand.OutputExists, exit);
            Assert.Equal("existing", File.ReadAllText(output));
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public void AnUnreachableDatabaseWritesNoReport()
    {
        var output = Path.Combine(Path.GetTempPath(), "c10-wp10-" + Guid.NewGuid().ToString("N") + ".json");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:AuthorityDbReadOnly"] = "Host=127.0.0.1;Port=9;Database=none;Timeout=2",
        }).Build();
        Assert.True(LegacyLineageDryRunCommand.TryRun([LegacyLineageDryRunCommand.Flag, "--organization", Guid.CreateVersion7().ToString("D"), "--output", output],
            configuration, "Testing", out var exit));
        Assert.Equal(LegacyLineageDryRunCommand.DatabaseFailure, exit);
        Assert.False(File.Exists(output));
    }
}
