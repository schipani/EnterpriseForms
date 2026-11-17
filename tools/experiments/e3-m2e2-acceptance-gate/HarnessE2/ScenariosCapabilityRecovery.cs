// E3-M2f-2G group GR (docs/security/E3_M2F2G_VERIFICATION_RECORD.md; closure §4.4, R-M2f-4, BD-1, BD-2; M2F-39): restore and incomplete
// ledger reconstruction, demonstrated ONLY on disposable copies (CREATE DATABASE ... TEMPLATE) of this run's database. Nothing here remedies
// BD-1 or BD-2. Unresolved behaviour is recorded as EXPECTED_GAP_RESTORE, EXPECTED_GAP_BD2 or INCONCLUSIVE (never counted as a pass); only the
// deployment-floor backstop and the any-binding property are counted, because they are properties of the E3-M2f-2 floor itself.
internal sealed partial class Lab
{
    private const string RestoreT0Database = "m2f2grst";
    private const string LostLedgerDatabase = "m2f2gbd2";
    private const string PartialLedgerDatabase = "m2f2gbd2p";

    private async Task CapabilityRecoveryScenariosAsync()
    {
        CurrentGroup = "GR";
        var node = GInstance("GRsetup");
        var rOrg = await SeedV1OrgAsync(4, node);

        // T0: a restore point BEFORE the organization's first binding.
        await CopyDatabaseAsync(PrimaryDatabase, RestoreT0Database);

        // After T0: two schema-2 heads (the first binding), anchored. The business now holds schema-2 rows for r0 and r2.
        var w0 = await WriteAsync(AsNode(node), rOrg.Records[0], Owner(rOrg.Records[0], (await BatchAsync(rOrg.Records[0])).Seal("after-t0-a")));
        var w2 = await WriteAsync(AsNode(node), rOrg.Records[2], Owner(rOrg.Records[2], (await BatchAsync(rOrg.Records[2])).Seal("after-t0-b")));
        await VerifyArchivedContentStreamAsync(rOrg, PrimaryDatabase);
        var liveBindings = await BindingCountAsync($"organization_id = '{rOrg.Id}'");
        var r1 = rOrg.Records[1];
        var h1 = await HeadIdentityAsync(r1);
        var verifyR1 = new GCase(Entry.Verify, r1, h1.Operation, h1.Revision);
        var live = await CallAsync(GInstance("GR-live"), verifyR1, r1.AcceptedStaged(), BusinessBuild.Undeclared);
        Record("GR-00", "Security", "live (not restored): after the first binding, a level-1 Verify of an untouched v1 record is refused", $"writes OK x2; bindings 2; {Refused}",
            $"writes {w0},{w2}; bindings {liveBindings}; {live}", w0 == "OK" && w2 == "OK" && liveBindings == 2 && live == Refused);

        // M2F-39 / R-M2f-4 (BD-1): on the T0 restore copy the ledger has no binding of the organization, so O = 1 again.
        {
            var copyBindings = await BindingCountAsync($"organization_id = '{rOrg.Id}'", RestoreT0Database);
            var restoredHead = await HeadIdentityAsync(r1, RestoreT0Database);
            var onCopy = await CallAsync(GInstance("GR-t0-d1", database: RestoreT0Database), new GCase(Entry.Verify, r1, restoredHead.Operation, restoredHead.Revision), r1.AcceptedStaged(), BusinessBuild.Undeclared);
            var r0Head = await HeadStateAsync(rOrg.Records[0], RestoreT0Database);
            Demonstrate("GR-01", "EXPECTED_GAP_RESTORE", "M2F-39 / R-M2f-4 (BD-1, restore copy at T0 only): the AuthorityDb restored to before the organization's first binding has no binding, so the organization floor is 1 again although the business holds schema-2 rows of that organization written after T0: a level-1 Verify is ADMITTED. This is the restore regression BD-1 must close; it is not a passing property",
                $"copy bindings 0 (live {liveBindings}); expected capability floor 2, computed 1; level-1 Verify admitted (OK: verified True)",
                $"copy bindings {copyBindings} (live {liveBindings}); r0 head on the copy {r0Head} (the business holds a schema-2 row for it); expected floor 2, computed {(copyBindings > 0 ? 2 : 1)}; level-1 Verify {onCopy}",
                copyBindings == 0 && onCopy.StartsWith("OK: verified True", StringComparison.Ordinal));

            var backstop = await CallAsync(GInstance("GR-t0-d2", deploymentFloor: "2", database: RestoreT0Database), new GCase(Entry.Verify, r1, restoredHead.Operation, restoredHead.Revision), r1.AcceptedStaged(), BusinessBuild.Undeclared);
            var level2 = await CallAsync(GInstance("GR-t0-d2b", deploymentFloor: "2", database: RestoreT0Database), new GCase(Entry.Verify, r1, restoredHead.Operation, restoredHead.Revision), r1.AcceptedStaged(), BusinessBuild.Current);
            Record("GR-02", "Security", "the deployment floor is the restore-independent backstop (closure §4.4, §9): on the same T0 restore copy an Authority configured with Content:MinimumBusinessEnvelopeCapability = 2 refuses the level-1 Verify without reading the ledger, and admits level 2",
                $"D = 2: {Refused}; level 2 OK: verified True", $"D = 2 level 1 {backstop}; level 2 {level2}", backstop == Refused && level2.StartsWith("OK: verified True", StringComparison.Ordinal));

            // On the copy the business is AHEAD of the restored Authority for r0 and r2: level 1 (legacy bytes) and level 2 (faithful bytes)
            // both mismatch, so a quarantine here cannot be attributed to G-2.
            var cfNode = GInstance("GR-t0-ahead", database: RestoreT0Database);
            var h0 = await HeadIdentityAsync(rOrg.Records[0], RestoreT0Database);
            var h2 = await HeadIdentityAsync(rOrg.Records[2], RestoreT0Database);
            var legacyAhead = await CallAsync(cfNode, new GCase(Entry.Verify, rOrg.Records[0], h0.Operation, h0.Revision), await ObservedAsync(rOrg.Records[0], inputs.BusinessAgents["11e15ee"]), BusinessBuild.Undeclared);
            var currentAhead = await CallAsync(cfNode, new GCase(Entry.Verify, rOrg.Records[2], h2.Operation, h2.Revision), rOrg.Records[2].AcceptedStaged(), BusinessBuild.Current);
            Demonstrate("GR-03", "INCONCLUSIVE", "after the T0 restore the business is ahead of the Authority for the post-T0 records: a level-1 client with legacy bytes and a level-2 client with faithful bytes are BOTH rejected by verification (restored head older than the business rows); whether a G-2 false quarantine would follow cannot be isolated from the restore discrepancy itself on this copy",
                "both rejected (not verified or quarantined) for reasons of the restore, not of the capability floor",
                $"level 1 legacy: {legacyAhead}; level 2 faithful: {currentAhead}; r0 {await HeadStateAsync(rOrg.Records[0], RestoreT0Database)}; r2 {await HeadStateAsync(rOrg.Records[2], RestoreT0Database)}",
                !legacyAhead.StartsWith("OK: verified True", StringComparison.Ordinal) && !currentAhead.StartsWith("OK: verified True", StringComparison.Ordinal));
        }

        // BD-2 (incomplete reconstruction): copies taken AFTER the first binding; the ledger rows of the organization are then removed on the
        // copy only (superuser, P-2 trigger disabled for the statement, re-enabled), as a reconstruction that lost them would leave it.
        await CopyDatabaseAsync(PrimaryDatabase, LostLedgerDatabase);
        await CopyDatabaseAsync(PrimaryDatabase, PartialLedgerDatabase);
        {
            var removeAll = await ExecAsync(AdminFor(LostLedgerDatabase), $"ALTER TABLE authority.content_data_key_bindings DISABLE TRIGGER trg_content_data_key_binding_permanent; DELETE FROM authority.content_data_key_bindings WHERE organization_id = '{rOrg.Id}'; ALTER TABLE authority.content_data_key_bindings ENABLE TRIGGER trg_content_data_key_binding_permanent");
            var removeSome = await ExecAsync(AdminFor(PartialLedgerDatabase), $"ALTER TABLE authority.content_data_key_bindings DISABLE TRIGGER trg_content_data_key_binding_permanent; DELETE FROM authority.content_data_key_bindings WHERE organization_id = '{rOrg.Id}' AND form_record_id = '{rOrg.Records[0].Id}'; ALTER TABLE authority.content_data_key_bindings ENABLE TRIGGER trg_content_data_key_binding_permanent");
            var lost = await BindingCountAsync($"organization_id = '{rOrg.Id}'", LostLedgerDatabase);
            var partial = await BindingCountAsync($"organization_id = '{rOrg.Id}'", PartialLedgerDatabase);
            var r0 = rOrg.Records[0];
            var h0 = await HeadIdentityAsync(r0, LostLedgerDatabase);
            var legacyBytes = await ObservedAsync(r0, inputs.BusinessAgents["11e15ee"], LostLedgerDatabase);

            var partialOutcome = await CallAsync(GInstance("GR-partial", database: PartialLedgerDatabase), new GCase(Entry.Verify, r0, h0.Operation, h0.Revision), legacyBytes, BusinessBuild.Undeclared);
            Record("GR-04", "Security", "CAP-F2 on a partially reconstructed ledger (copy): with one of the organization's two bindings missing, any surviving binding keeps the organization floor at 2: the 11e15ee Verify is refused before verification and the schema-2 head is not quarantined",
                $"removal OK; 1 binding left; {Refused}; head not quarantined", $"removal {removeSome}; bindings {partial}; {partialOutcome}; head {await HeadStateAsync(r0, PartialLedgerDatabase)}",
                removeSome == "OK" && partial == 1 && partialOutcome == Refused && (await HeadStateAsync(r0, PartialLedgerDatabase)).EndsWith(":false", StringComparison.OrdinalIgnoreCase));

            var backstop = await CallAsync(GInstance("GR-lost-d2", deploymentFloor: "2", database: LostLedgerDatabase), new GCase(Entry.Verify, r0, h0.Operation, h0.Revision), legacyBytes, BusinessBuild.Undeclared);
            Record("GR-05", "Security", "deployment-floor backstop on the copy whose ledger lost every binding of the organization: D = 2 refuses the 11e15ee Verify before verification; the schema-2 head is not quarantined",
                $"removal OK; 0 bindings; {Refused}; head not quarantined", $"removal {removeAll}; bindings {lost}; {backstop}; head {await HeadStateAsync(r0, LostLedgerDatabase)}",
                removeAll == "OK" && lost == 0 && backstop == Refused && (await HeadStateAsync(r0, LostLedgerDatabase)).EndsWith(":false", StringComparison.OrdinalIgnoreCase));

            var incidents = await IncidentCountAsync(rOrg, LostLedgerDatabase);
            var lostOutcome = await CallAsync(GInstance("GR-lost-d1", database: LostLedgerDatabase), new GCase(Entry.Verify, r0, h0.Operation, h0.Revision), legacyBytes, BusinessBuild.Undeclared);
            Demonstrate("GR-06", "EXPECTED_GAP_BD2", "BD-2 (copy whose reconstructed ledger lost the organization's bindings while its schema-2 heads survive): the organization floor computes 1 although the accepted head is schema 2; with D = 1 the 11e15ee Verify is ADMITTED and its re-encoded bytes falsely QUARANTINE the head (G-2 reproduced). A reconstruction must restore the bindings (BD-2) or D must be 2; this is not a passing property",
                "expected floor 2 (schema-2 head), computed 1 (0 bindings); admitted; head quarantined; incident journaled",
                $"bindings {lost}; head before {h0.Revision}; outcome {lostOutcome}; head {await HeadStateAsync(r0, LostLedgerDatabase)}; incidents {incidents}->{await IncidentCountAsync(rOrg, LostLedgerDatabase)}",
                lost == 0 && (await HeadStateAsync(r0, LostLedgerDatabase)).EndsWith(":true", StringComparison.OrdinalIgnoreCase) && await IncidentCountAsync(rOrg, LostLedgerDatabase) > incidents);
        }

        await GrantSentinelAsync("after-capability-recovery");
    }
}
