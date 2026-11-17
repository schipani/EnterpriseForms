using AuthorizationAuthority.Api.Composition;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// E3-M2f-3i (docs/security/E3_M2F3_CONTRACT_CLOSURE.md §5A): the approved index definition, its catalogue classification and the
/// command's refusals. The real catalogue behaviour (creation, idempotency, invalid and mismatched indexes, concurrency, lock
/// timeouts, privileges, plans) is proven on disposable PostgreSQL by scripts/dev/e3-m2f3i-index-pg-gate.ps1.
/// </summary>
public sealed class CapabilityRefusalEvidenceIndexTests
{
    private const string ApprovedCreateSql =
        "CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_journal_capability_refusal_evidence ON authority.security_journal_entries "
        + "(organization_id, journal_stream_revision) WHERE journal_stream_name = 'audit' AND journal_event_kind = 'EnvelopeCapabilityRefused'";

    private const string ApprovedLookupSql =
        "SELECT security_journal_entry_id, journal_stream_revision, journal_event_body_json, journal_event_digest FROM authority.security_journal_entries "
        + "WHERE organization_id = @organization_id AND journal_stream_name = 'audit' AND journal_event_kind = 'EnvelopeCapabilityRefused' "
        + "ORDER BY journal_stream_revision DESC LIMIT 8";

    // ---- the approved definition ----------------------------------------------------------------------------------------------

    [Fact]
    public void TheCreateStatementIsExactlyTheApprovedDdl() => Assert.Equal(ApprovedCreateSql, CapabilityRefusalEvidenceIndex.CreateSql);

    [Fact]
    public void TheLookupIsExactlyTheApprovedQueryWithLiteralStreamAndKind() => Assert.Equal(ApprovedLookupSql, CapabilityRefusalEvidenceIndex.EvidenceLookupSql);

    [Fact]
    public void ThePredicateLiteralsAreTheEvidenceConstants()
    {
        Assert.Equal(JournalStreamNames.Audit, CapabilityRefusalEvidence.StreamName);
        Assert.Equal("audit", CapabilityRefusalEvidence.StreamName);
        Assert.Equal("EnvelopeCapabilityRefused", CapabilityRefusalEvidence.EventKind);
        Assert.True(CapabilityRefusalEvidence.EventKind.Length <= 60, "journal_event_kind is varchar(60)");
        Assert.Contains("journal_stream_name = 'audit'", CapabilityRefusalEvidenceIndex.CreateSql, StringComparison.Ordinal);
        Assert.Contains("journal_event_kind = 'EnvelopeCapabilityRefused'", CapabilityRefusalEvidenceIndex.CreateSql, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePinnedRenderingsDescribeTheSameIndex()
    {
        Assert.Equal(
            "CREATE INDEX ix_journal_capability_refusal_evidence ON authority.security_journal_entries USING btree (organization_id, journal_stream_revision) "
            + "WHERE (((journal_stream_name)::text = 'audit'::text) AND ((journal_event_kind)::text = 'EnvelopeCapabilityRefused'::text))",
            CapabilityRefusalEvidenceIndex.ExpectedDefinition);
        Assert.EndsWith(CapabilityRefusalEvidenceIndex.ExpectedPredicate, CapabilityRefusalEvidenceIndex.ExpectedDefinition, StringComparison.Ordinal);
        Assert.Equal(["organization_id", "journal_stream_revision"], CapabilityRefusalEvidenceIndex.KeyColumns);
        Assert.True(CapabilityRefusalEvidenceIndex.IndexName.Length <= 63);
    }

    [Fact]
    public void ACreationIsExactlyTheIndexRelationAndOneValidIndexRowOnTheJournal()
    {
        const string relation = "rel|authority|ix_journal_capability_refusal_evidence|i|authority_owner|";
        const string index = "idx|authority.ix_journal_capability_refusal_evidence|authority.security_journal_entries|false|false|false|true|true|true|true|2 4|...";
        Assert.Equal(relation, CapabilityRefusalEvidenceIndex.CreatedRelationLine("authority_owner"));
        Assert.True(CapabilityRefusalEvidenceIndex.IsExactlyTheCreation([relation, index], [], "authority_owner"));
        Assert.False(CapabilityRefusalEvidenceIndex.IsExactlyTheCreation([relation, index], ["rel|authority|something_else|r|authority_owner|"], "authority_owner"));
        Assert.False(CapabilityRefusalEvidenceIndex.IsExactlyTheCreation([relation, index, "trg|x"], [], "authority_owner"));
        Assert.False(CapabilityRefusalEvidenceIndex.IsExactlyTheCreation([relation], [], "authority_owner"));
        Assert.False(CapabilityRefusalEvidenceIndex.IsExactlyTheCreation([relation, index], [], "someone_else"));
        Assert.False(CapabilityRefusalEvidenceIndex.IsExactlyTheCreation([relation, index.Replace("|true|true|true|true|", "|true|false|true|true|", StringComparison.Ordinal)], [], "authority_owner"));
        Assert.False(CapabilityRefusalEvidenceIndex.IsExactlyTheCreation([relation, index.Replace("|false|false|false|", "|true|false|false|", StringComparison.Ordinal)], [], "authority_owner"));
    }

    [Fact]
    public void TheCatalogueSnapshotUsesNoDeparsingFunctionThatLocksATable()
    {
        // PostgreSQL 18 takes ACCESS SHARE on the underlying table in these functions (verified on a disposable container in E3-M2f-3i).
        foreach (var function in new[] { "pg_get_indexdef", "pg_get_expr", "pg_get_constraintdef", "pg_get_triggerdef", "pg_get_viewdef", "pg_get_ruledef" })
        {
            Assert.DoesNotContain(function, CapabilityRefusalEvidenceIndex.CatalogueSnapshotSql, StringComparison.Ordinal);
        }

        Assert.Contains("CASE WHEN i.indrelid = to_regclass('authority.security_journal_entries') THEN pg_catalog.pg_get_indexdef(c.oid) END", CapabilityRefusalEvidenceIndex.FactsSql, StringComparison.Ordinal);
        Assert.Contains("CASE WHEN i.indrelid = to_regclass('authority.security_journal_entries') THEN pg_catalog.pg_get_expr(i.indpred, i.indrelid) END", CapabilityRefusalEvidenceIndex.FactsSql, StringComparison.Ordinal);
    }

    // ---- classification -------------------------------------------------------------------------------------------------------

    private static CapabilityRefusalEvidenceIndexFacts Approved(bool valid = true, bool ready = true, bool live = true, bool building = false) => new(
        RelationOid: 4242, RelationKind: "i", OnTargetTable: true, AccessMethod: "btree", IsUnique: false, IsPrimary: false, IsExclusion: false,
        IsValid: valid, IsReady: ready, IsLive: live, AttributeCount: 2, KeyAttributeCount: 2, HasExpressions: false,
        KeyColumns: ["organization_id", "journal_stream_revision"], KeyOptions: "0 0", OperatorClasses: ["uuid_ops", "int8_ops"],
        Predicate: CapabilityRefusalEvidenceIndex.ExpectedPredicate, Definition: CapabilityRefusalEvidenceIndex.ExpectedDefinition,
        OwnedByTableOwner: true, HasPrivileges: false, BuildVisiblyInProgress: building);

    [Fact]
    public void NoRelationIsAbsent() => Assert.Equal(CapabilityRefusalEvidenceIndexState.Absent, CapabilityRefusalEvidenceIndex.Classify(null).State);

    [Fact]
    public void TheApprovedValidIndexIsValidMatching()
    {
        var inspection = CapabilityRefusalEvidenceIndex.Classify(Approved());
        Assert.Equal(CapabilityRefusalEvidenceIndexState.ValidMatching, inspection.State);
        Assert.Empty(inspection.Mismatches);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    public void AnApprovedButUnusableIndexWithoutAVisibleBuildIsInvalid(bool valid, bool ready, bool live) =>
        Assert.Equal(CapabilityRefusalEvidenceIndexState.Invalid, CapabilityRefusalEvidenceIndex.Classify(Approved(valid, ready, live)).State);

    [Fact]
    public void AnApprovedUnusableIndexWithAVisibleBuildIsInProgress() =>
        Assert.Equal(CapabilityRefusalEvidenceIndexState.BuildInProgress, CapabilityRefusalEvidenceIndex.Classify(Approved(valid: false, ready: false, building: true)).State);

    [Fact]
    public void AVisibleBuildDoesNotMakeAValidIndexInProgress() =>
        Assert.Equal(CapabilityRefusalEvidenceIndexState.ValidMatching, CapabilityRefusalEvidenceIndex.Classify(Approved(building: true)).State);

    public static TheoryData<string> MismatchNames => new()
    {
        "relation kind", "other table", "access method", "unique", "primary", "exclusion", "expressions", "include column", "column order",
        "descending", "operator class", "predicate", "definition", "owner", "privileges",
    };

    [Theory]
    [MemberData(nameof(MismatchNames))]
    public void EveryDeviationFromTheApprovedDefinitionIsAMismatch(string deviation)
    {
        var facts = Approved();
        facts = deviation switch
        {
            "relation kind" => facts with { RelationKind = "r" },
            "other table" => facts with { OnTargetTable = false },
            "access method" => facts with { AccessMethod = "hash" },
            "unique" => facts with { IsUnique = true },
            "primary" => facts with { IsPrimary = true },
            "exclusion" => facts with { IsExclusion = true },
            "expressions" => facts with { HasExpressions = true },
            "include column" => facts with { AttributeCount = 3 },
            "column order" => facts with { KeyColumns = ["journal_stream_revision", "organization_id"] },
            "descending" => facts with { KeyOptions = "0 3" },
            "operator class" => facts with { OperatorClasses = ["uuid_ops", "int4_ops"] },
            "predicate" => facts with { Predicate = "((journal_stream_name)::text = 'audit'::text)" },
            "definition" => facts with { Definition = CapabilityRefusalEvidenceIndex.ExpectedDefinition.Replace("btree", "hash", StringComparison.Ordinal) },
            "owner" => facts with { OwnedByTableOwner = false },
            "privileges" => facts with { HasPrivileges = true },
            _ => throw new ArgumentOutOfRangeException(nameof(deviation)),
        };
        var inspection = CapabilityRefusalEvidenceIndex.Classify(facts);
        Assert.Equal(CapabilityRefusalEvidenceIndexState.DefinitionMismatch, inspection.State);
        Assert.NotEmpty(inspection.Mismatches);
    }

    [Fact]
    public void AMismatchTakesPrecedenceOverValidity() =>
        Assert.Equal(CapabilityRefusalEvidenceIndexState.DefinitionMismatch,
            CapabilityRefusalEvidenceIndex.Classify(Approved(valid: false, ready: false) with { IsUnique = true }).State);

    // ---- the command's refusals -----------------------------------------------------------------------------------------------

    private static IConfiguration Settings(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    /// <summary>A migration connection string that can never connect: an exit code other than 2 means the command got past its refusals.</summary>
    private static IConfiguration Unreachable() =>
        Settings(("ConnectionStrings:AuthorityDbMigration", "Host=unit-test.invalid;Database=unused;Username=unused;Password=unused;Timeout=2"));

    [Fact]
    public void TheCommandIsInactiveWithoutItsFlag()
    {
        Assert.False(CapabilityRefusalEvidenceIndexCommand.TryRun(["--migrate"], Unreachable(), out _));
        Assert.False(CapabilityRefusalEvidenceIndexCommand.TryRun([ContentAcceptanceFloorCommand.Flag, "3"], Unreachable(), out _));
    }

    [Theory]
    [InlineData("--provision-capability-refusal-evidence-index --migrate")]
    [InlineData("--migrate --provision-capability-refusal-evidence-index")]
    [InlineData("--provision-capability-refusal-evidence-index --apply-content-acceptance-floor 3")]
    [InlineData("--provision-capability-refusal-evidence-index --reinstate-recorded-floor")]
    [InlineData("--provision-capability-refusal-evidence-index --drop")]
    [InlineData("--provision-capability-refusal-evidence-index --rebuild")]
    public void TheCommandRefusesCombinationsAndUnknownOptions(string commandLine)
    {
        Assert.True(CapabilityRefusalEvidenceIndexCommand.TryRun(commandLine.Split(' '), Unreachable(), out var exitCode));
        Assert.Equal(CapabilityRefusalEvidenceIndexCommand.Usage, exitCode);
    }

    [Fact]
    public void TheCommandRequiresTheMigrationIdentityAndNeverFallsBackToTheRuntimeConnection()
    {
        Assert.True(CapabilityRefusalEvidenceIndexCommand.TryRun([CapabilityRefusalEvidenceIndexCommand.Flag], Settings(), out var none));
        Assert.Equal(CapabilityRefusalEvidenceIndexCommand.Usage, none);
        var runtimeOnly = Settings(("ConnectionStrings:AuthorityDb", "Host=unit-test.invalid;Database=unused;Username=unused;Password=unused;Timeout=2"));
        Assert.True(CapabilityRefusalEvidenceIndexCommand.TryRun([CapabilityRefusalEvidenceIndexCommand.Flag], runtimeOnly, out var runtime));
        Assert.Equal(CapabilityRefusalEvidenceIndexCommand.Usage, runtime);
    }

    [Theory]
    [InlineData("--provision-capability-refusal-evidence-index")]
    [InlineData("--provision-capability-refusal-evidence-index --verify-only")]
    public void AValidInvocationGetsPastTheRefusalsAndFailsClosedWhenTheDatabaseIsUnreachable(string commandLine)
    {
        Assert.True(CapabilityRefusalEvidenceIndexCommand.TryRun(commandLine.Split(' '), Unreachable(), out var exitCode));
        Assert.Equal(CapabilityRefusalEvidenceIndexCommand.DatabaseFailure, exitCode);
    }
}
