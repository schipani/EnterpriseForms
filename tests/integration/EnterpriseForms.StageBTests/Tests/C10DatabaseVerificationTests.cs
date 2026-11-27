using System.Globalization;
using System.Net;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// Phase C10 focused Stage B, real PostgreSQL (WP-0 §10.4 and §10.5 missing evidence): the C10 migrations' constraints, the business
/// partial index and the Authority triggers as installed, and a content commit that fails after the share boundary changed rolls the
/// boundary, the share revision and the journal entry back together (Testing-only crash point CrashAfterSearchIndexDerivation, which
/// terminates the Authority inside the transaction; the container restarts).
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class C10DatabaseVerificationTests(StageBFixture fixture)
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(40);

    private string Authority(string sql) => fixture.Stack.AuthorityObserverSql(sql).Trim();

    [Fact]
    public async Task TheC10MigrationsInstallTheirConstraintsPartialIndexAndTriggers()
    {
        var s = new StageBScenario(fixture, "c10-database-objects");
        await s.BootstrapAsync();
        await s.BuildAccusedWorkspaceAsync();
        var record = await s.CreateAccusedAsync(s.Clerk);
        await s.UploadAsync(s.Clerk, record, "c10-db.txt", "text/plain", StageBScenario.SyntheticBytes(512, 31));

        // Authority: the evidence columns are nullable (older models leave them NULL) and their checks are installed and enforced.
        Assert.Equal("YES|YES|YES", Authority("""
            SELECT string_agg(is_nullable, '|' ORDER BY column_name) FROM information_schema.columns WHERE table_schema = 'authority'
              AND ((table_name = 'record_manifest_leaves' AND column_name = 'attachment_content_fingerprint')
                OR (table_name = 'content_candidate_leaves' AND column_name IN ('accepted_attachment_content_fingerprint', 'accepted_attachment_version_number')));
            """));
        Assert.Equal("2", Authority("SELECT count(*) FROM pg_constraint WHERE conname IN ('ck_manifest_leaf_attachment_fingerprint_length', 'ck_candidate_leaf_accepted_attachment_evidence');"));
        Assert.Equal("1", Authority($"SELECT count(*) FROM record_manifest_leaves l JOIN record_content_heads h ON h.current_record_manifest_commit_id = l.record_manifest_commit_id WHERE h.form_record_id = '{record:D}' AND l.attachment_content_fingerprint IS NOT NULL AND octet_length(l.attachment_content_fingerprint) = 32;"));
        var shortFingerprint = fixture.Stack.AuthorityAdministratorSql($"""
            BEGIN; SET LOCAL session_replication_role = replica;
            UPDATE record_manifest_leaves SET attachment_content_fingerprint = '\x01' WHERE record_manifest_leaf_id = (SELECT l.record_manifest_leaf_id FROM record_manifest_leaves l JOIN record_content_heads h ON h.current_record_manifest_commit_id = l.record_manifest_commit_id WHERE h.form_record_id = '{record:D}' AND l.attachment_content_fingerprint IS NOT NULL);
            ROLLBACK;
            """, throwOnError: false);
        Assert.Contains("ck_manifest_leaf_attachment_fingerprint_length", shortFingerprint, StringComparison.Ordinal);

        // Business: the version-row unique index applies to operation-bound copies only (owner decision 2).
        Assert.Contains("WHERE (content_change_operation_id <> '00000000-0000-0000-0000-000000000000'::uuid)",
            fixture.Stack.AttackerSql("SELECT indexdef FROM pg_indexes WHERE indexname = 'uq_file_attachment_versions_number_operation';"), StringComparison.Ordinal);

        // Triggers: a terminal share never returns to Active; a command's target record is immutable (C10 D6). Refused even to the
        // AuthorityDb administrator; nothing is changed (ROLLBACK).
        var share = await s.Fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/shares", s.Token(s.Clerk), new { useDevelopmentStepUp = true, recipientUserId = s.Reader.AuthorityUserId, recipientTeamId = (Guid?)null, recipientRole = "Reader", fieldKeys = new[] { "given_name" } });
        Assert.True(share.IsSuccess, share.Body);
        var shareId = share.Root.GetProperty("recordShareId").GetGuid();
        await s.WaitGateReadyAsync(Settle);
        var revoke = await s.Fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/shares/{shareId:D}/revoke?useDevelopmentStepUp=true", s.Token(s.Clerk), new { });
        Assert.True(revoke.IsSuccess, revoke.Body);
        await s.WaitGateReadyAsync(Settle);
        var reactivate = fixture.Stack.AuthorityAdministratorSql($"BEGIN; UPDATE authority_resources SET resource_lifecycle_state = 'Active' WHERE resource_type_name = 'record_share' AND resource_entity_id = '{shareId:D}'; ROLLBACK;", throwOnError: false);
        Assert.Contains("is terminal and cannot change state", reactivate, StringComparison.Ordinal);
        var retarget = fixture.Stack.AuthorityAdministratorSql($"BEGIN; UPDATE security_commands SET target_form_record_id = gen_random_uuid() WHERE target_form_record_id = '{record:D}'; ROLLBACK;", throwOnError: false);
        Assert.Contains("the target record of a security command is immutable", retarget, StringComparison.Ordinal);
        Assert.NotEqual("Active", Authority($"SELECT resource_lifecycle_state FROM authority_resources WHERE resource_type_name = 'record_share' AND resource_entity_id = '{shareId:D}';"));
    }

    [Fact]
    public async Task ACommitThatFailsAfterTheBoundaryChangedRollsTheBoundaryTheShareRevisionAndTheJournalEntryBack()
    {
        var s = new StageBScenario(fixture, "c10-boundary-rollback");
        await s.BootstrapAsync();
        await s.BuildAccusedWorkspaceAsync();
        var record = await s.CreateAccusedAsync(s.Clerk);
        var enabled = await fixture.Authority.OperatorAsync(HttpMethod.Post, "/internal/v1/operator/testing/contributor-writes", new { enabled = (bool?)true });
        Assert.True(enabled.IsSuccess, enabled.Body);
        try
        {
            var attachment = (await s.UploadAsync(s.Clerk, record, "c10-rollback.txt", "text/plain", StageBScenario.SyntheticBytes(1024, 41))).GetProperty("fileAttachmentId").GetGuid();
            var created = await s.Fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/shares", s.Token(s.Clerk), new
            {
                useDevelopmentStepUp = true, recipientUserId = s.Reader.AuthorityUserId, recipientTeamId = (Guid?)null, recipientRole = "Contributor", fieldKeys = Array.Empty<string>(),
                documents = new[] { new { fileAttachmentId = attachment, canDownloadDocument = true, canReplaceDocument = true } },
            });
            Assert.True(created.IsSuccess, created.Body);
            var share = created.Root.GetProperty("recordShareId").GetGuid();
            await s.WaitGateReadyAsync(Settle);
            var processed = await s.TransferAsync(s.Reader, record, "c10-rollback-v2.txt", "text/plain", StageBScenario.SyntheticBytes(1024, 42), replacesFileAttachmentId: attachment);
            Assert.Equal("Processed", processed.GetProperty("sessionState").GetString());
            string State() => Authority($"""
                SELECT m.share_revision || '|' || b.shared_through_version_number || '|' || h.committed_content_revision || '|' ||
                       (SELECT count(*) FROM security_journal_entries WHERE journal_event_kind = 'ShareDocumentBoundaryExtended' AND journal_event_body_json LIKE '%{share:D}%')
                FROM share_resource_metadata m JOIN share_document_bindings b ON b.record_share_id = m.record_share_id
                JOIN record_content_heads h ON h.form_record_id = '{record:D}' WHERE m.record_share_id = '{share:D}';
                """);
            var before = State();

            var armed = await fixture.Authority.OperatorAsync(HttpMethod.Post, "/internal/v1/operator/fault-injection", new { fault_name = "CrashAfterSearchIndexDerivation" });
            Assert.True(armed.IsSuccess, armed.Body);
            HttpResult failed;
            try
            {
                failed = await fixture.Api.PostAsync($"/api/v1/upload-sessions/{processed.GetProperty("uploadSessionId").GetGuid():D}/commit", s.Token(s.Reader), new { });
            }
            finally
            {
                await fixture.Authority.WaitReadyAsync(TimeSpan.FromSeconds(180));
                await fixture.Authority.OperatorAsync(HttpMethod.Post, "/internal/v1/operator/fault-injection", new { fault_name = "None" });
            }

            Assert.False(failed.IsSuccess, failed.Body);
            Assert.Equal(before, State()); // boundary, share revision, head and journal unchanged: the transaction rolled back as one

            var retried = await fixture.Api.PostAsync($"/api/v1/upload-sessions/{processed.GetProperty("uploadSessionId").GetGuid():D}/commit", s.Token(s.Reader), new { });
            Assert.True(retried.IsSuccess, retried.Body);
            await s.WaitGateReadyAsync(Settle);
            var parts = before.Split('|');
            Assert.Equal(string.Join('|', long.Parse(parts[0], CultureInfo.InvariantCulture) + 1, 2, long.Parse(parts[2], CultureInfo.InvariantCulture) + 1, 1), State());
            Assert.Equal(HttpStatusCode.OK, (await fixture.DownloadAsync($"/api/v1/form-records/{record:D}/documents/{attachment:D}/download", s.Token(s.Reader))).Status);
        }
        finally
        {
            await fixture.Authority.OperatorAsync(HttpMethod.Post, "/internal/v1/operator/testing/contributor-writes", new { enabled = (bool?)null });
        }
    }
}
