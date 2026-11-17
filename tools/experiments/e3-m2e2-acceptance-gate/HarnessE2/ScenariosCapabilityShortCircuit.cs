// E3-M2f-2G follow-up group GS (docs/security/E3_M2F2G_VERIFICATION_RECORD.md §11.2; closure CAP-F3, AM-F1 #4, M2F-22 on PostgreSQL): the
// designated detector for MG-17 (level-2 short-circuit removed). Self-contained: its fixtures never need a level-2 COMMIT in a bound
// organization, so a build without the short-circuit cannot break the setup of the property under test. The v1 head is written while the
// organization is still unbound; the binding comes from a level-2 PREPARE that is left Prepared (CAP-F2: a binding in any operation state
// raises the organization floor). Setup failures are recorded as Setup checks and stop the group; they are never security results.
internal sealed partial class Lab
{
    private async Task CapabilityShortCircuitScenariosAsync()
    {
        CurrentGroup = "GS";
        var node = GInstance("GSsetup");
        var org = await SeedOrgAsync(2, localKeys!, 1, journalDigest: true);
        var plain = org.Records[0];
        var write = await WriteAsync(AsNode(node), plain, Owner(plain, Rows.V1Field(org.Id, plain.Id, Guid.CreateVersion7(), Guid.CreateVersion7(), 51)));
        var anchored = (await VerifyArchivedContentStreamAsync(org, PrimaryDatabase)).Summary;
        var reservation = org.Records[1];
        var prepared = await PrepareAsync(AsNode(node), reservation, Owner(reservation, (await BatchAsync(reservation)).Seal("gs-binding")));
        var bindings = await BindingCountAsync($"organization_id = '{org.Id}'");
        var operation = prepared.Outcome == "OK" ? await StateAsync(prepared.OperationId) : "none";
        var setupOk = write == "OK" && anchored.Contains("chain verified", StringComparison.Ordinal) && prepared.Outcome == "OK" && bindings == 1 && operation == "3/NULL Prepared -";
        Record("GS-00", "Setup", "setup (self-contained, no level-2 Commit in a bound organization): a v1 head written while the organization is unbound and anchored; then a level-2 Prepare of a New v2 group left Prepared, whose binding makes the organization bound (CAP-F2)",
            "write OK; chain verified; Prepare OK; 1 binding; 3/NULL Prepared", $"write {write}; {anchored}; Prepare {prepared.Outcome} {prepared.Message}; bindings {bindings}; operation {operation}", setupOk);
        if (!setupOk)
        {
            Record("GS-SKIPPED", "Setup", "SETUP FAILED: no check of group GS was executed (setup failure, not a security result)", "setup OK", "setup failed", false);
            return;
        }

        var head = await HeadIdentityAsync(plain);
        var verify = new GCase(Entry.Verify, plain, head.Operation, head.Revision);
        var batch = new GCase(Entry.BatchVerify, plain, head.Operation, head.Revision);

        // GS-03 first (control): the organization IS bound, and the level-1 path does run the existence query; so GS-01's zero is meaningful.
        {
            var n = GInstance("GS03");
            n.Trace.Reset();
            var outcome = await CallAsync(n, verify, plain.AcceptedStaged(), BusinessBuild.Undeclared);
            Record("GS-03", "Control", "control: in this organization a level-1 Verify is refused after exactly one boolean existence query (the organization is bound, and the existence query is observable in the trace)",
                $"{Refused}; ledger statements 1 (ledger-exists)", $"{outcome}; ledger statements {n.Trace.LedgerStatements.Count} [{string.Join(" | ", n.Trace.LedgerStatements.Select(StatementTrace.Classify))}]; shape [{n.Trace.Shape()}]",
                outcome == Refused && n.Trace.LedgerStatements.Count == 1 && StatementTrace.Classify("SQL " + n.Trace.LedgerStatements[0]) == "ledger-exists");
        }

        // GS-01 (declared MG-17 detector, statement evidence): level-2 Verify and batch Verify run NO statement on the ledger at all.
        {
            var results = new List<string>();
            var pass = true;
            foreach (var c in new[] { verify, batch })
            {
                var n = GInstance("GS01-" + c.Entry);
                n.Trace.Reset();
                var outcome = await CallAsync(n, c, plain.AcceptedStaged(), BusinessBuild.Current);
                var ledger = n.Trace.LedgerStatements;
                results.Add($"{c.Entry}: {outcome}; ledger statements {ledger.Count}{(ledger.Count == 0 ? string.Empty : " [" + string.Join(" | ", ledger) + "]")}; shape [{n.Trace.Shape()}]");
                pass &= outcome.StartsWith("OK: verified True", StringComparison.Ordinal) && ledger.Count == 0;
            }

            Record("GS-01", "Security", "M2F-22 / CAP-F3 / AM-F1 #4 on real PostgreSQL (MG-17 detector, statement evidence): in an organization WITH a binding, a level-2 Verify and a level-2 batch Verify execute no statement at all on authority.content_data_key_bindings (the level-2 short-circuit admits without the existence query) and verify",
                "Verify and BatchVerify: OK: verified True; ledger statements 0", string.Join(" || ", results), pass);
        }

        // GS-02 (declared MG-17 detector, server-enforced): with the runtime role's SELECT on the ledger revoked, any ledger read would fail
        // with 42501; level 2 still verifies; level 1 on the same node is refused as not ready, proving the revocation took effect on this path.
        {
            var n = GInstance("GS02", applicationName: "m2e2-expected-42501-shortcircuit");
            var revoke = await ExecAsync(adminCs, "REVOKE SELECT ON authority.content_data_key_bindings FROM authority_runtime");
            var level2Verify = await CallAsync(n, verify, plain.AcceptedStaged(), BusinessBuild.Current);
            var level2Batch = await CallAsync(n, batch, plain.AcceptedStaged(), BusinessBuild.Current);
            var level1 = await CallAsync(n, verify, plain.AcceptedStaged(), BusinessBuild.Undeclared);
            var grant = await ExecAsync(adminCs, "GRANT SELECT ON authority.content_data_key_bindings TO authority_runtime");
            Record("GS-02", "Security", "M2F-22 server-enforced (MG-17 detector): with SELECT on the ledger revoked from the runtime role (superuser, restored at once; the 42501 is labelled expected), level-2 Verify and batch Verify in the bound organization still verify, so they read nothing from the ledger; the level-1 Verify on the same node is refused as not ready (the revocation is in force on this path)",
                "revoke OK; level-2 Verify and BatchVerify OK: verified True; level 1 AUTHORITY_NOT_READY 503; grant OK",
                $"revoke {revoke}; level-2 Verify {level2Verify}; level-2 BatchVerify {level2Batch}; level 1 {level1}; grant {grant}",
                revoke == "OK" && level2Verify.StartsWith("OK: verified True", StringComparison.Ordinal) && level2Batch.StartsWith("OK: verified True", StringComparison.Ordinal)
                && level1.StartsWith("AuthorityException AUTHORITY_NOT_READY 503", StringComparison.Ordinal) && grant == "OK");
        }

        await GrantSentinelAsync("after-capability-short-circuit");
    }
}
