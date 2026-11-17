// E3-M2e-2 group L: the ledger's DATABASE controls exercised by raw SQL, independently of the Authority's L-13 verification (which
// refuses every ledger-using application write as soon as any of them is weakened). Runs right after activation and, in database-mode
// enforcement-removal runs, right after the mutation is applied. The operations used are a v1-only committed write (no ledger use) and
// a seeded Prepared operation stamped 3 (admin session, every CHECK applies).
using Npgsql;

internal sealed partial class Lab
{
    private async Task LedgerSqlScenariosAsync()
    {
        CurrentGroup = "L";
        var lOrg = await SeedOrgAsync(3, localKeys!, 1);
        var committedRecord = lOrg.Records[0];
        var w = await WriteAsync(acc, committedRecord, Owner(committedRecord, Rows.V1Field(lOrg.Id, committedRecord.Id, Guid.CreateVersion7(), Guid.CreateVersion7(), 15)));
        var committedOp = Guid.Parse(await ScalarAsync(adminCs, $"SELECT committed_operation_id FROM authority.record_content_heads WHERE form_record_id = '{committedRecord.Id}'"));
        var preparedRecord = lOrg.Records[1];
        var seeded = await SeedPreparedAsync(preparedRecord, Owner(preparedRecord), 3);
        var id = Guid.CreateVersion7();
        var positive = await RuntimeInsertAsync(BindingSql(id, lOrg.Id, preparedRecord.Id, seeded.Operation, 1, 1, Rows.Fill(32, 0x11)));
        Record("L-01", "Database", "raw SQL positive control: the runtime role inserts a binding for a Prepared operation stamped 3 of the same organization and record (P-1 admits it)",
            "setup write OK; seed OK; insert OK", $"setup {w}; seed {seeded.Seeded}; insert {positive}", w == "OK" && seeded.Seeded == "OK" && positive == "OK");

        var forged = await RuntimeInsertAsync(BindingSql(Guid.CreateVersion7(), lOrg.Id, committedRecord.Id, committedOp, 1, 1, Rows.Fill(32, 0x12)));
        Record("L-02", "Database", "P-1 (database): a binding for a Committed operation is refused by the binding guard trigger", "23514 trg_content_data_key_binding_guard", forged,
            forged.StartsWith("23514 trg_content_data_key_binding_guard", StringComparison.Ordinal));

        var duplicate = await RuntimeInsertAsync(BindingSql(id, lOrg.Id, preparedRecord.Id, seeded.Operation, 1, 1, Rows.Fill(32, 0x13)));
        Record("L-03", "Database", "global uniqueness (database): a second binding of an identifier is refused by the unique index", "23505 uq_content_data_key_binding_identifier", duplicate,
            duplicate.StartsWith("23505 uq_content_data_key_binding_identifier", StringComparison.Ordinal));

        var expectedCs = new NpgsqlConnectionStringBuilder(runtimeCs) { ApplicationName = "m2e2-expected-42501" }.ConnectionString;
        var rUpdate = await ExecAsync(expectedCs, $"UPDATE authority.content_data_key_bindings SET binding_item_count = 9 WHERE data_key_identifier = '{id}'");
        var rDelete = await ExecAsync(expectedCs, $"DELETE FROM authority.content_data_key_bindings WHERE data_key_identifier = '{id}'");
        var rTruncate = await ExecAsync(expectedCs, "TRUNCATE authority.content_data_key_bindings");
        Record("L-04", "Database", "L-8 (database): the runtime role has no UPDATE, DELETE or TRUNCATE privilege on the ledger", "42501 x3 (labelled expected)", $"{rUpdate} | {rDelete} | {rTruncate}",
            new[] { rUpdate, rDelete, rTruncate }.All(o => o.StartsWith("42501", StringComparison.Ordinal)));

        var aUpdate = await ExecAsync(adminCs, $"UPDATE authority.content_data_key_bindings SET binding_item_count = 9 WHERE data_key_identifier = '{id}'");
        var aDelete = await ExecAsync(adminCs, $"DELETE FROM authority.content_data_key_bindings WHERE data_key_identifier = '{id}'");
        var aTruncate = await ExecAsync(adminCs, "TRUNCATE authority.content_data_key_bindings");
        var intact = await ScalarAsync(adminCs, $"SELECT count(*)::text||'/'||coalesce(max(binding_item_count)::text,'-') FROM authority.content_data_key_bindings WHERE data_key_identifier = '{id}'");
        Record("L-05", "Database", "P-2 (database): even the superuser is refused UPDATE, DELETE and TRUNCATE by the immutability triggers; the binding is intact",
            "23514 trg_content_data_key_binding_permanent x3; 1/1", $"{aUpdate} | {aDelete} | {aTruncate}; {intact}",
            new[] { aUpdate, aDelete, aTruncate }.All(o => o.StartsWith("23514 trg_content_data_key_binding_permanent", StringComparison.Ordinal)) && intact == "1/1");
    }
}
