using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Application.Features.PlatformOperators;
using AuthorizationAuthority.Infrastructure.Archive;
using AuthorizationAuthority.Infrastructure.Persistence;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// E3-M2e-1 (docs/security/E3_M2E_CONTRACT_CLOSURE.md): the default-off Envelope v2 acceptance switch and its startup prerequisites
/// (OD-M2e-07, AM-5, AM-9), the policy observation on the platform journal (OD-M2e-13; UQ-3: no new table), and the text of the
/// ledger and floor-3 SQL (the database behaviour itself is proven only by the disposable-PostgreSQL gate). Nothing here accepts
/// Envelope v2 content.
/// </summary>
public sealed class DataKeyLedgerTests
{
    private static readonly ContentAcceptancePolicy ReleaseA = ContentAcceptancePolicy.Resolve("ReleaseA", null);
    private static readonly ContentAcceptancePolicy Floor3 = ContentAcceptancePolicy.Resolve("ReleaseB", "3");

    // ---- the switch --------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("false")]
    [InlineData("1")]
    [InlineData("yes")]
    [InlineData("enabled")]
    [InlineData("tru")]
    public void OnlyTheLiteralTrueEnablesAndAnythingElseIsOff(string? raw)
    {
        Assert.False(EnvelopeV2AcceptanceSwitch.Parse(raw));
        var acceptance = EnvelopeV2AcceptanceSwitch.Create(raw, "Production", ReleaseA); // off never refuses a start, anywhere
        Assert.False(acceptance.ConfiguredEnabled);
        Assert.False(acceptance.Enabled);
        Assert.True(EnvelopeV2AcceptanceSwitch.Parse(" TRUE "));
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("Unknown")]
    public void ConfiguredOnOutsideDevelopmentOrTestingRefusesTheStart(string environment) =>
        Assert.Contains("must not be enabled outside Development/Testing", Assert.Throws<InvalidOperationException>(() => EnvelopeV2AcceptanceSwitch.Create("true", environment, Floor3)).Message, StringComparison.Ordinal);

    [Theory]
    [InlineData("ReleaseA", null)]
    [InlineData("ReleaseB", "1")]
    [InlineData("ReleaseB", "2")]
    [InlineData("Unconfigured", null)]
    public void ConfiguredOnWithoutReleaseBFloor3RefusesTheStart(string phase, string? floor) =>
        Assert.Contains("requires", Assert.Throws<InvalidOperationException>(() => EnvelopeV2AcceptanceSwitch.Create("true", "Development", ContentAcceptancePolicy.Resolve(phase, floor))).Message, StringComparison.Ordinal);

    [Fact]
    public void WrapCodesAreAdmittedOnlyInDevelopmentOrTesting()
    {
        Assert.Equal([1, 2], EnvelopeV2AcceptanceSwitch.Create(null, "Development", ReleaseA).AllowedWrapCodes.Order());
        Assert.Equal([1, 2], EnvelopeV2AcceptanceSwitch.Create(null, "Testing", ReleaseA).AllowedWrapCodes.Order());
        Assert.Empty(EnvelopeV2AcceptanceSwitch.Create(null, "Staging", ReleaseA).AllowedWrapCodes);
        Assert.Empty(EnvelopeV2AcceptanceSwitch.Create(null, "Production", ReleaseA).AllowedWrapCodes);
        Assert.False(EnvelopeV2AcceptanceSwitch.DarkForTests.Enabled);
        Assert.False(EnvelopeV2AcceptanceSwitch.DarkForTests.StaticPrerequisitesHold);
    }

    [Fact]
    public void TheTestingOverrideIsTestingOnlyAndCannotEnableWithoutTheFloorPrerequisite()
    {
        Assert.Throws<InvalidOperationException>(() => EnvelopeV2AcceptanceSwitch.Create(null, "Development", Floor3).SetTestingOverride(false)); // Testing only
        var releaseA = EnvelopeV2AcceptanceSwitch.Create(null, "Testing", ReleaseA);
        Assert.Contains("RequiredFloor = 3", Assert.Throws<InvalidOperationException>(() => releaseA.SetTestingOverride(true)).Message, StringComparison.Ordinal);
        releaseA.SetTestingOverride(false); // disabling is always allowed
        releaseA.SetTestingOverride(null);
        Assert.False(releaseA.Enabled);
    }

    [Fact]
    public void AcceptanceIsNeverEffectiveBeforeItsExactPolicyIsRecorded()
    {
        // Object-level only (no content is accepted): the gate requires a request, the static prerequisites and the recorded digest.
        var acceptance = EnvelopeV2AcceptanceSwitch.Create(null, "Testing", Floor3);
        Assert.True(acceptance.StaticPrerequisitesHold);
        var offDigest = acceptance.Describe().PolicyDigestHex;
        acceptance.MarkObserved(offDigest);
        Assert.False(acceptance.Enabled); // not requested
        acceptance.SetTestingOverride(true);
        Assert.True(acceptance.Requested);
        Assert.False(acceptance.Enabled); // the recorded policy is the "off" one: a change not yet recorded stays off
        Assert.NotEqual(offDigest, acceptance.Describe().PolicyDigestHex);
        acceptance.SetTestingOverride(null);
        Assert.False(acceptance.Enabled);
    }

    [Fact]
    public void TheDescriptorIsSecretFreeAndNamesThePolicy()
    {
        var descriptor = EnvelopeV2AcceptanceSwitch.Create(null, "Development", ReleaseA).Describe();
        Assert.Equal((false, false, "None", "DevelopmentOrTesting", "phase=ReleaseA", 3), (descriptor.AcceptanceRequested, descriptor.AcceptanceConfigured, descriptor.TestingOverride, descriptor.EnvironmentClass, descriptor.ContentAcceptancePolicy, descriptor.ContentAcceptanceRuleVersion));
        Assert.Equal(64, descriptor.PolicyDigestHex.Length);
        Assert.NotEqual(descriptor.PolicyDigestHex, EnvelopeV2AcceptanceSwitch.Create(null, "Production", ReleaseA).Describe().PolicyDigestHex);
    }

    // ---- policy observation (OD-M2e-13) ---------------------------------------------------------------------------------------

    private static (InMemoryAuthorityStore Store, EnvelopeAcceptancePolicyObserver Observer, SwitchableArchive Archive) Observer(EnvelopeV2AcceptanceSwitch acceptance)
    {
        var store = new InMemoryAuthorityStore();
        var archive = new SwitchableArchive();
        var journal = new PlatformJournal(store, archive, new NoAlerts(), TimeProvider.System);
        return (store, new EnvelopeAcceptancePolicyObserver(store, journal, acceptance, TimeProvider.System), archive);
    }

    [Fact]
    public async Task ThePolicyIsRecordedOnceOnThePlatformJournalAndAgainOnlyWhenItChanges()
    {
        var acceptance = EnvelopeV2AcceptanceSwitch.Create(null, "Testing", Floor3);
        var (store, observer, _) = Observer(acceptance);
        Assert.Equal(EnvelopeAcceptancePolicyObserver.Recorded, await observer.RecordAsync(Guid.CreateVersion7(), CancellationToken.None));
        var entry = Assert.Single(store.PlatformJournal);
        Assert.Equal(EnvelopeAcceptancePolicyObserver.EventKind, entry.JournalEventKind);
        Assert.Equal(acceptance.Describe().PolicyDigestHex, EnvelopeAcceptancePolicyObserver.RecordedPolicyDigest(entry));
        Assert.Contains("\"delta\":{\"operators\":[]", entry.JournalEventBodyJson, StringComparison.Ordinal); // no platform state changes: replay-neutral

        Assert.Equal(EnvelopeAcceptancePolicyObserver.AlreadyRecorded, await observer.RecordAsync(Guid.CreateVersion7(), CancellationToken.None));
        Assert.Single(store.PlatformJournal);

        acceptance.SetTestingOverride(false);
        Assert.Equal(EnvelopeAcceptancePolicyObserver.Recorded, await observer.RecordAsync(Guid.CreateVersion7(), CancellationToken.None));
        Assert.Equal(2, store.PlatformJournal.Count);
    }

    [Fact]
    public async Task ATamperedPreviousRecordIsNotTrustedAndAnUnreachableArchiveRecordsNothing()
    {
        var acceptance = EnvelopeV2AcceptanceSwitch.Create(null, "Testing", ReleaseA);
        var (store, observer, archive) = Observer(acceptance);
        await observer.RecordAsync(Guid.CreateVersion7(), CancellationToken.None);
        store.PlatformJournal.Single().JournalEventBodyJson += " "; // the body no longer matches its digest
        Assert.Null(EnvelopeAcceptancePolicyObserver.RecordedPolicyDigest(store.PlatformJournal.Single()));
        Assert.Equal(EnvelopeAcceptancePolicyObserver.Recorded, await observer.RecordAsync(Guid.CreateVersion7(), CancellationToken.None)); // recorded again, never skipped

        archive.Available = false;
        acceptance.SetTestingOverride(false);
        var count = store.PlatformJournal.Count;
        await Assert.ThrowsAsync<AuthorityException>(() => observer.RecordAsync(Guid.CreateVersion7(), CancellationToken.None));
        Assert.Equal(count, store.PlatformJournal.Count);
    }

    [Fact]
    public async Task TheBoundedWindowCanOnlyCauseAnExtraRecordNeverAMissingOne()
    {
        var acceptance = EnvelopeV2AcceptanceSwitch.Create(null, "Testing", ReleaseA);
        var (store, observer, _) = Observer(acceptance);
        await observer.RecordAsync(Guid.CreateVersion7(), CancellationToken.None);
        var head = store.PlatformHead!;
        for (var i = 0; i < EnvelopeAcceptancePolicyObserver.SearchWindow; i++)
        {
            await using var scope = await store.BeginScopeAsync(CancellationToken.None);
            PlatformJournal.Append(scope, head, "OtherPlatformEvent", new { n = i }, PlatformStateDelta.Of(), DateTimeOffset.UtcNow);
        }

        Assert.Equal(EnvelopeAcceptancePolicyObserver.Recorded, await observer.RecordAsync(Guid.CreateVersion7(), CancellationToken.None)); // outside the window: recorded again
        Assert.Equal(2, store.PlatformJournal.Count(e => e.JournalEventKind == EnvelopeAcceptancePolicyObserver.EventKind));
    }

    // ---- ledger and floor-3 SQL (text; behaviour on disposable PostgreSQL) -------------------------------------------------

    [Fact]
    public void TheLedgerSqlHasTheBindingGuardImmutabilityAndNoBraceOrSecurityDefiner()
    {
        var sql = ContentDataKeyLedgerSql.TriggersSql;
        Assert.Contains("o.operation_state_name = 'Prepared'", sql, StringComparison.Ordinal);
        Assert.Contains("o.organization_id = NEW.organization_id", sql, StringComparison.Ordinal);
        Assert.Contains("o.form_record_id = NEW.form_record_id", sql, StringComparison.Ordinal);
        Assert.Contains("o.prepare_acceptance_rule_version IS NOT NULL", sql, StringComparison.Ordinal); // two-valued (D-1)
        Assert.Contains("o.prepare_acceptance_rule_version >= 3", sql, StringComparison.Ordinal);
        Assert.Contains("BEFORE UPDATE OR DELETE ON authority.content_data_key_bindings", sql, StringComparison.Ordinal);
        Assert.Contains("BEFORE TRUNCATE ON authority.content_data_key_bindings", sql, StringComparison.Ordinal);
        Assert.Contains("REVOKE ALL ON FUNCTION authority.content_data_key_binding_guard() FROM PUBLIC;", sql, StringComparison.Ordinal);
        Assert.Contains("REVOKE ALL ON FUNCTION authority.content_data_key_binding_is_permanent() FROM PUBLIC;", sql, StringComparison.Ordinal);
        Assert.Equal(2, CountOf(sql, "SET search_path = pg_catalog, pg_temp"));
        Assert.DoesNotContain("SECURITY DEFINER", sql, StringComparison.Ordinal);
        foreach (var text in new[] { sql, ContentDataKeyLedgerSql.DownGuardSql, ContentAcceptanceFloor3Sql.UpSql, ContentAcceptanceFloor3Sql.ReinstateSql, ContentAcceptanceFloor3Sql.DownSql })
        {
            Assert.DoesNotContain('{', text);
            Assert.DoesNotContain('}', text);
        }

        Assert.Contains("RAISE EXCEPTION 'the data-key ledger migration cannot be reverted while any binding exists'", ContentDataKeyLedgerSql.DownGuardSql, StringComparison.Ordinal);
        Assert.True(ContentDataKeyLedgerSql.DownGuardSql.IndexOf("RAISE EXCEPTION", StringComparison.Ordinal) < ContentDataKeyLedgerSql.DownGuardSql.IndexOf("DROP TRIGGER", StringComparison.Ordinal));
        Assert.Contains("content_data_key_bindings", AuthorityRuntimeGrants.InsertOnlyTables);
        Assert.Equal(3, ContentDataKeyLedgerSql.MinimumBindingRuleVersion);
    }

    [Fact]
    public void Floor3IsAddedAfterItsPreconditionsUnderTheLockThenFloor2IsDropped()
    {
        var up = ContentAcceptanceFloor3Sql.UpSql;
        var lockAt = up.IndexOf("LOCK TABLE authority.content_change_operations IN ACCESS EXCLUSIVE MODE", StringComparison.Ordinal);
        var floor2Check = up.IndexOf("floor 2 must be installed and recorded, and be the only floor", StringComparison.Ordinal);
        var ledgerCheck = up.IndexOf("the data-key ledger migration is not applied", StringComparison.Ordinal);
        var ledgerObjects = up.IndexOf("binding-guard and immutability triggers are not present exactly as created", StringComparison.Ordinal);
        var inFlight = up.IndexOf("prepared below content acceptance rule version 3", StringComparison.Ordinal);
        var sessions = up.IndexOf("carv-v([0-9]+)')::numeric, 0) < 3", StringComparison.Ordinal);
        var add = up.IndexOf("ADD CONSTRAINT ck_content_operation_rule_floor_v3", StringComparison.Ordinal);
        var drop = up.IndexOf("DROP CONSTRAINT ck_content_operation_rule_floor_v2", StringComparison.Ordinal);
        Assert.True(up.IndexOf("SET LOCAL lock_timeout = '5s';", StringComparison.Ordinal) < lockAt);
        Assert.True(lockAt < floor2Check && floor2Check < ledgerCheck && ledgerCheck < ledgerObjects && ledgerObjects < inFlight && inFlight < sessions && sessions < add && add < drop);
        Assert.Contains("prepare_acceptance_rule_version IS NOT NULL AND prepare_acceptance_rule_version >= 3)) NOT VALID", up, StringComparison.Ordinal);
        Assert.Contains(ContentDataKeyLedgerSql.MigrationId, up, StringComparison.Ordinal);
        foreach (var expected in new[] { ContentDataKeyLedgerSql.ExpectedUniqueIndexDefinition, ContentDataKeyLedgerSql.ExpectedGuardTriggerDefinition, ContentDataKeyLedgerSql.ExpectedPermanentTriggerDefinition, ContentDataKeyLedgerSql.ExpectedTruncateTriggerDefinition })
        {
            Assert.Contains("'" + expected + "'", up, StringComparison.Ordinal); // the floor checks the same renderings the write path verifies (L-13)
        }

        Assert.Contains("floors are never lowered", ContentAcceptanceFloor3Sql.DownSql, StringComparison.Ordinal);
        Assert.Contains("DROP CONSTRAINT IF EXISTS ck_content_operation_rule_floor_v2", ContentAcceptanceFloor3Sql.ReinstateSql, StringComparison.Ordinal);
        Assert.Equal(ContentAcceptanceFloor3Sql.ExpectedDefinition, ContentAcceptanceFloors.Find(3)!.ExpectedDefinition);
    }

    [Fact]
    public void TheLedgerVerificationRefusesEveryWeakenedObjectInAFixedOrder()
    {
        var good = new ContentDataKeyLedgerSql.LedgerFacts(true, ContentDataKeyLedgerSql.ExpectedUniqueIndexDefinition, ContentDataKeyLedgerSql.ExpectedGuardTriggerDefinition, ContentDataKeyLedgerSql.ExpectedGuardSource,
            ContentDataKeyLedgerSql.ExpectedPermanentTriggerDefinition, ContentDataKeyLedgerSql.ExpectedTruncateTriggerDefinition, 1, ContentDataKeyLedgerSql.ExpectedPermanentSource, 0, 0, 0, "r");
        Assert.Null(ContentDataKeyLedgerSql.Evaluate(good));
        Assert.Null(ContentDataKeyLedgerSql.Evaluate(good with { GuardSource = good.GuardSource!.Replace("\n", "\r\n", StringComparison.Ordinal) })); // line endings normalized

        Assert.Equal(ContentDataKeyLedgerSql.Failures.Unverifiable, ContentDataKeyLedgerSql.Evaluate(null));
        Assert.Equal(ContentDataKeyLedgerSql.Failures.LedgerMigrationNotRecorded, ContentDataKeyLedgerSql.Evaluate(good with { LedgerRecorded = false }));
        Assert.Equal(ContentDataKeyLedgerSql.Failures.UniqueIndexMismatch, ContentDataKeyLedgerSql.Evaluate(good with { UniqueIndexDefinition = null }));
        Assert.Equal(ContentDataKeyLedgerSql.Failures.UniqueIndexMismatch, ContentDataKeyLedgerSql.Evaluate(good with { UniqueIndexDefinition = "CREATE INDEX uq_content_data_key_binding_identifier ON authority.content_data_key_bindings USING btree (data_key_identifier)" }));
        Assert.Equal(ContentDataKeyLedgerSql.Failures.GuardMismatch, ContentDataKeyLedgerSql.Evaluate(good with { GuardTriggerDefinition = null })); // dropped or disabled
        Assert.Equal(ContentDataKeyLedgerSql.Failures.GuardMismatch, ContentDataKeyLedgerSql.Evaluate(good with { GuardSource = "BEGIN RETURN NEW; END" })); // replaced body
        Assert.Equal(ContentDataKeyLedgerSql.Failures.GuardMismatch, ContentDataKeyLedgerSql.Evaluate(good with { GuardSource = null })); // SECURITY DEFINER or another language
        Assert.Equal(ContentDataKeyLedgerSql.Failures.PermanenceMismatch, ContentDataKeyLedgerSql.Evaluate(good with { PermanentTriggerDefinition = null }));
        Assert.Equal(ContentDataKeyLedgerSql.Failures.PermanenceMismatch, ContentDataKeyLedgerSql.Evaluate(good with { TruncateTriggerDefinition = null }));
        Assert.Equal(ContentDataKeyLedgerSql.Failures.PermanenceMismatch, ContentDataKeyLedgerSql.Evaluate(good with { PermanentSourceCount = 2 })); // the two triggers call different functions
        Assert.Equal(ContentDataKeyLedgerSql.Failures.PermanenceMismatch, ContentDataKeyLedgerSql.Evaluate(good with { PermanentSource = "BEGIN RETURN NEW; END" }));
        Assert.Equal(ContentDataKeyLedgerSql.Failures.UnexpectedTableObject, ContentDataKeyLedgerSql.Evaluate(good with { OtherTriggerCount = 1 }));
        Assert.Equal(ContentDataKeyLedgerSql.Failures.UnexpectedTableObject, ContentDataKeyLedgerSql.Evaluate(good with { RuleCount = 1 }));
        Assert.Equal(ContentDataKeyLedgerSql.Failures.UnexpectedTableObject, ContentDataKeyLedgerSql.Evaluate(good with { RelationKind = "p" }));

    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private sealed class SwitchableArchive : ISecurityEventArchive
    {
        private readonly InMemorySecurityEventArchive inner = new();

        public bool Available { get; set; } = true;

        public string ArchiveName => "Switchable(InMemory)";

        public Task<ArchiveReceipt> AppendAsync(Guid organizationId, string streamName, long streamRevision, ReadOnlyMemory<byte> eventBody, byte[] expectedDigest, CancellationToken cancellationToken) =>
            Available ? inner.AppendAsync(organizationId, streamName, streamRevision, eventBody, expectedDigest, cancellationToken) : throw new ArchiveUnavailableException("archive down (test)");

        public Task<(bool Ready, string Detail)> ProbeAsync(CancellationToken cancellationToken) => Task.FromResult((Available, "test"));

        public Task<byte[]?> ReadAsync(Guid organizationId, string streamName, long streamRevision, CancellationToken cancellationToken) =>
            Available ? inner.ReadAsync(organizationId, streamName, streamRevision, cancellationToken) : throw new ArchiveUnavailableException("archive down (test)");
    }

    private sealed class NoAlerts : ISecurityAlertSink
    {
        public void Raise(SecurityAlert alert)
        {
        }
    }
}
