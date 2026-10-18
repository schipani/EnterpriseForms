using System.Net;
using EnterpriseForms.SecurityTests.Harness;
using Xunit;

namespace EnterpriseForms.SecurityTests.Tests;

/// <summary>SEC-P04, SEC-P05, SEC-P10: value mutation, deletion/replay of signed rows, and whole-database rollback against the independent head.</summary>
[Collection(StageACollection.Name)]
public sealed class IntegrityTamperingTests(StageAFixture fixture)
{
    [Fact]
    [Trait("SEC-P", "P04")]
    public async Task SecP04_SignedValueMutationFailsClosed()
    {
        var s = new Scenario(fixture, "SEC-P04", "Ciphertext/value mutation: changing a signed row produces an integrity failure; zero protected bytes released");
        await ScenarioRunner.RunAsync(s, async () =>
        {
            await s.BootstrapAsync();
            await s.CreateFormAndRecordAsync();
            var ok = await s.ReadAsync(s.Owner);
            Assert.Equal(HttpStatusCode.OK, ok.Status);

            var updated = fixture.Stack.AttackerSql($"UPDATE field_string_values SET string_value = 'TAMPERED-BY-DBA' WHERE form_record_id = '{s.FormRecordId:D}' AND field_value_item_id = '{s.FieldItemIds["case_note"]:D}'; SELECT count(*) FROM field_string_values WHERE string_value = 'TAMPERED-BY-DBA' AND form_record_id = '{s.FormRecordId:D}';");
            s.Evidence.Observe("hostile UPDATE field_string_values.string_value (signature untouched)", new { rows = updated.Trim() });
            Assert.Equal("1", updated.Trim());

            var read = await s.ReadAsync(s.Owner);
            s.Evidence.ObserveHttp("owner read after mutation", read, "title", "detail");
            Assert.Equal(HttpStatusCode.Locked, read.Status);
            Assert.DoesNotContain("TAMPERED-BY-DBA", read.Body, StringComparison.Ordinal);
            Assert.DoesNotContain("protected note", read.Body, StringComparison.Ordinal);
            Assert.DoesNotContain("Synthetic", read.Body, StringComparison.Ordinal);
            Assert.Contains("INTEGRITY_VERIFICATION_FAILED", read.Body, StringComparison.Ordinal);

            var edit = await fixture.Api.PatchAsync($"/api/v1/form-records/{s.FormRecordId:D}", s.Token(s.Owner), new { expectedContentRevision = 1, values = new Dictionary<string, string> { ["given_name"] = "Changed" } });
            s.Evidence.ObserveHttp("owner edit after mutation (quarantined head)", edit, "title");
            Assert.Equal(HttpStatusCode.Locked, edit.Status);

            // A second tampering vector on a fresh record: flip one signature byte, leave the value intact.
            var s2 = new Scenario(fixture, "SEC-P04", "signature flip");
            await s2.BootstrapAsync();
            await s2.CreateFormAndRecordAsync();
            var flipped = fixture.Stack.AttackerSql($"UPDATE field_string_values SET row_signature = set_byte(row_signature, 0, (get_byte(row_signature, 0) # 1)) WHERE form_record_id = '{s2.FormRecordId:D}' AND field_value_item_id = '{s2.FieldItemIds["given_name"]:D}'; SELECT count(*) FROM field_string_values WHERE form_record_id = '{s2.FormRecordId:D}';");
            s.Evidence.Observe("hostile signature bit flip on second record", new { rows = flipped.Trim() });
            var read2 = await s2.ReadAsync(s2.Owner);
            s.Evidence.ObserveHttp("owner read after signature flip", read2, "title");
            Assert.Equal(HttpStatusCode.Locked, read2.Status);
            Assert.DoesNotContain("protected note", read2.Body, StringComparison.Ordinal);
        });
    }

    [Fact]
    [Trait("SEC-P", "P05")]
    public async Task SecP05_DeletedAndReplayedRowsAreDetectedAgainstTheAcceptedHead()
    {
        var s = new Scenario(fixture, "SEC-P05", "Delete or replay signed rows: missing or older valid rows are detected against the current external manifest/head; no stale read");
        await ScenarioRunner.RunAsync(s, async () =>
        {
            await s.BootstrapAsync();
            await s.CreateFormAndRecordAsync();
            var first = await s.ReadAsync(s.Owner);
            var firstOperation = first.Root.GetProperty("committedOperationId").GetGuid();
            var update = await fixture.Api.PatchAsync($"/api/v1/form-records/{s.FormRecordId:D}", s.Token(s.Owner), new { expectedContentRevision = 1, values = new Dictionary<string, string> { ["case_note"] = "revised note" } });
            s.Evidence.ObserveHttp("owner update to revision 2", update, "contentRevision", "committedOperationId");
            Assert.Equal(HttpStatusCode.OK, update.Status);
            var secondOperation = update.Root.GetProperty("committedOperationId").GetGuid();
            Assert.Equal(2, update.Root.GetProperty("contentRevision").GetInt64());
            var readable = await s.WaitReadableAsync(s.Owner);
            Assert.Equal(HttpStatusCode.OK, readable.Status);
            Assert.Contains("revised note", readable.Body, StringComparison.Ordinal);

            // Replay: overwrite the accepted revision-2 note with the (validly signed) revision-1 row content and signature.
            var replay = fixture.Stack.AttackerSql($"""
                UPDATE field_string_values AS new SET string_value = old.string_value, row_signature = old.row_signature
                FROM field_string_values AS old
                WHERE new.form_record_id = '{s.FormRecordId:D}' AND old.form_record_id = '{s.FormRecordId:D}'
                  AND new.content_change_operation_id = '{secondOperation:D}' AND old.content_change_operation_id = '{firstOperation:D}'
                  AND new.field_value_item_id = old.field_value_item_id AND new.field_value_item_id = '{s.FieldItemIds["case_note"]:D}';
                SELECT count(*) FROM field_string_values WHERE content_change_operation_id = '{secondOperation:D}' AND string_value LIKE 'protected note%';
                """);
            s.Evidence.Observe("hostile replay of revision-1 signed row into revision-2 slot", new { rows = replay.Trim() });
            Assert.Equal("1", replay.Trim());
            var replayed = await s.ReadAsync(s.Owner);
            s.Evidence.ObserveHttp("owner read after replay", replayed, "title");
            Assert.Equal(HttpStatusCode.Locked, replayed.Status);
            Assert.DoesNotContain("protected note", replayed.Body, StringComparison.Ordinal);
            Assert.DoesNotContain("revised note", replayed.Body, StringComparison.Ordinal);

            // Deletion on a fresh record: remove one accepted value row; completeness check fails.
            var s2 = new Scenario(fixture, "SEC-P05", "deletion");
            await s2.BootstrapAsync();
            await s2.CreateFormAndRecordAsync();
            var deleted = fixture.Stack.AttackerSql($"DELETE FROM field_string_values WHERE form_record_id = '{s2.FormRecordId:D}' AND field_value_item_id = '{s2.FieldItemIds["family_name"]:D}'; SELECT count(*) FROM field_string_values WHERE form_record_id = '{s2.FormRecordId:D}';");
            s.Evidence.Observe("hostile DELETE of one accepted value row", new { remaining_rows = deleted.Trim() });
            Assert.Equal("2", deleted.Trim());
            var afterDelete = await s2.ReadAsync(s2.Owner);
            s.Evidence.ObserveHttp("owner read after deletion", afterDelete, "title");
            Assert.Equal(HttpStatusCode.Locked, afterDelete.Status);
            Assert.DoesNotContain("Synthetic", afterDelete.Body, StringComparison.Ordinal);

            // Whole-record deletion: header and all values gone; nothing is served and no fallback to a prior revision.
            var s3 = new Scenario(fixture, "SEC-P05", "whole record deletion");
            await s3.BootstrapAsync();
            await s3.CreateFormAndRecordAsync();
            fixture.Stack.AttackerSql($"DELETE FROM field_string_values WHERE form_record_id = '{s3.FormRecordId:D}'; DELETE FROM form_record_revisions WHERE form_record_id = '{s3.FormRecordId:D}';");
            var afterWipe = await s3.ReadAsync(s3.Owner);
            s.Evidence.ObserveHttp("owner read after whole-record deletion", afterWipe, "title");
            Assert.Equal(HttpStatusCode.Locked, afterWipe.Status);
        });
    }

    [Fact]
    [Trait("SEC-P", "P10")]
    public async Task SecP10_OlderBusinessDbRestoreDoesNotRevivePermissionsOrContent()
    {
        var s = new Scenario(fixture, "SEC-P10", "Older BusinessDb restore: a pre-revocation / pre-update snapshot restored into BusinessDb neither revives the revoked share nor serves the stale content");
        await ScenarioRunner.RunAsync(s, async () =>
        {
            await s.BootstrapAsync();
            await s.CreateFormAndRecordAsync();
            var share = await fixture.Api.PostAsync($"/api/v1/form-records/{s.FormRecordId:D}/shares", s.Token(s.Owner), new { recipientUserId = s.Reader.AuthorityUserId, recipientRole = "Reader", fieldKeys = new[] { "case_note" } });
            Assert.Equal(HttpStatusCode.Created, share.Status);
            var shareId = share.Root.GetProperty("recordShareId").GetGuid();
            var readerBefore = await s.WaitReadableAsync(s.Reader);
            Assert.Equal(HttpStatusCode.OK, readerBefore.Status);

            // Snapshot of the whole business database at the pre-revocation / pre-update point.
            var dump = fixture.Stack.AttackerShell("pg_dump -Fc -T '\"__EFMigrationsHistory\"' -f /tmp/business-snapshot.dump && ls -l /tmp/business-snapshot.dump");
            s.Evidence.Observe("pg_dump BusinessDb snapshot", new { result = Evidence.Sanitize(dump.Trim()) });

            var revoke = await fixture.Api.PostAsync($"/api/v1/form-records/{s.FormRecordId:D}/shares/{shareId:D}/revoke", s.Token(s.Owner), new { });
            Assert.True(revoke.Root.GetProperty("command").GetProperty("effective").GetBoolean(), revoke.Body);
            var update = await fixture.Api.PatchAsync($"/api/v1/form-records/{s.FormRecordId:D}", s.Token(s.Owner), new { expectedContentRevision = 1, values = new Dictionary<string, string> { ["case_note"] = "post-snapshot note" } });
            Assert.Equal(HttpStatusCode.OK, update.Status);
            s.Evidence.ContentRevision = update.Root.GetProperty("contentRevision").GetInt64();
            var ownerAfterUpdate = await s.WaitReadableAsync(s.Owner);
            Assert.Contains("post-snapshot note", ownerAfterUpdate.Body, StringComparison.Ordinal);

            // Hostile restore: truncate every business table and reload the old snapshot (data-only, triggers disabled).
            var restore = fixture.Stack.AttackerShell("""
                psql -v ON_ERROR_STOP=1 -qAt -c "DO \$\$ DECLARE r record; BEGIN FOR r IN SELECT tablename FROM pg_tables WHERE schemaname = 'public' AND tablename <> '__EFMigrationsHistory' LOOP EXECUTE 'TRUNCATE TABLE public.' || quote_ident(r.tablename) || ' CASCADE'; END LOOP; END \$\$;" &&
                pg_restore --data-only --disable-triggers --no-owner -d "$PGDATABASE" /tmp/business-snapshot.dump 2>&1 && echo RESTORED &&
                psql -qAt -c "SELECT share_status FROM record_shares WHERE record_share_id = '{SHARE}'" &&
                psql -qAt -c "SELECT count(*) FROM field_string_values WHERE form_record_id = '{RECORD}'"
                """.Replace("{SHARE}", shareId.ToString("D"), StringComparison.Ordinal).Replace("{RECORD}", s.FormRecordId.ToString("D"), StringComparison.Ordinal), timeoutSeconds: 120);
            s.Evidence.Observe("hostile pg_restore of pre-revocation snapshot", new { result = Evidence.Sanitize(restore.Trim()) });
            Assert.Contains("RESTORED", restore, StringComparison.Ordinal);
            Assert.Contains("Active", restore, StringComparison.Ordinal);

            var readerAfter = await s.ReadAsync(s.Reader);
            s.Evidence.ObserveHttp("revoked reader read after restore (share row is Active again locally)", readerAfter);
            Assert.Equal(HttpStatusCode.NotFound, readerAfter.Status);
            Assert.DoesNotContain("protected note", readerAfter.Body, StringComparison.Ordinal);

            var ownerAfter = await s.ReadAsync(s.Owner);
            s.Evidence.ObserveHttp("owner read after restore (accepted revision 2 rows missing)", ownerAfter, "title");
            Assert.Equal(HttpStatusCode.Locked, ownerAfter.Status);
            Assert.DoesNotContain("protected note", ownerAfter.Body, StringComparison.Ordinal);
            var gate = await fixture.Authority.GateAsync(s.OrganizationId);
            s.Evidence.AuthorityRevision = gate.GetProperty("authorization_revision").GetInt64();
            s.Evidence.Observe("authority state after restore (unchanged by business restore)", gate.GetRawText());
        });
    }
}
