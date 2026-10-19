using System.Net;
using EnterpriseForms.SecurityTests.Harness;
using Xunit;

namespace EnterpriseForms.SecurityTests.Tests;

/// <summary>SEC-P07, SEC-P08, SEC-P09, SEC-P12: projection lag, signing outage, crash-and-retry, archive outage.</summary>
[Collection(StageACollection.Name)]
public sealed class DistributedConsistencyTests(StageAFixture fixture)
{
    [Fact]
    [Trait("SEC-P", "P07")]
    public async Task SecP07_ProjectionLagFailsClosed()
    {
        var s = new Scenario(fixture, "SEC-P07", "SpiceDB projection lag: protected operations fail closed while applied revision differs from the authority head; no stale allow from a ZedToken or cache");
        await ScenarioRunner.RunAsync(s, async () =>
        {
            await s.BootstrapAsync();
            await s.CreateFormAndRecordAsync();
            var ok = await s.ReadAsync(s.Owner);
            Assert.Equal(HttpStatusCode.OK, ok.Status);

            fixture.Stack.Pause("spicedb");
            try
            {
                var duringOutage = await s.ReadAsync(s.Owner);
                s.Evidence.ObserveHttp("owner read while projection engine unavailable", duringOutage);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, duringOutage.Status);
                Assert.DoesNotContain("protected note", duringOutage.Body, StringComparison.Ordinal);

                // Grant committed while the projection cannot advance: the recipient must not become allowed and the gate must close.
                var share = await s.SubmitAuthorityCommandAsync(s.Owner, "CreateShare", "FormRecord", s.FormRecordId, new { record_share_id = Guid.CreateVersion7(), form_record_id = s.FormRecordId, recipient_user_id = s.Reader.AuthorityUserId, recipient_team_id = (Guid?)null, recipient_role = "Reader", field_item_ids = new[] { s.FieldItemIds["given_name"] } });
                Assert.Equal(HttpStatusCode.Accepted, share.Status);
                Assert.Equal("CommittedAwaitingProjection", share.Root.GetProperty("command_status").GetString());
                // The archive receipt lands within a few hundred milliseconds; the projection stays blocked while SpiceDB is paused.
                var gate = await fixture.Authority.GateAsync(s.OrganizationId);
                var gateDeadline = DateTime.UtcNow.AddSeconds(10);
                while (gate.GetProperty("projection_gate_state").GetString() == "AwaitingArchive" && DateTime.UtcNow < gateDeadline)
                {
                    await Task.Delay(200);
                    gate = await fixture.Authority.GateAsync(s.OrganizationId);
                }

                s.Evidence.Observe("gate during lag", gate.GetRawText());
                Assert.Equal("AwaitingProjection", gate.GetProperty("projection_gate_state").GetString());
                Assert.True(gate.GetProperty("authorization_revision").GetInt64() > gate.GetProperty("projection_applied_revision").GetInt64());

                var readerDuring = await s.ReadAsync(s.Reader);
                s.Evidence.ObserveHttp("reader read during lag (grant committed, not projected)", readerDuring);
                Assert.NotEqual(HttpStatusCode.OK, readerDuring.Status);
                var ownerDuring = await s.ReadAsync(s.Owner);
                s.Evidence.ObserveHttp("owner read during lag (gate closed for the whole organization)", ownerDuring);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, ownerDuring.Status);
            }
            finally
            {
                fixture.Stack.Unpause("spicedb");
            }

            var ready = await s.WaitGateReadyAsync();
            s.Evidence.AuthorityRevision = ready.GetProperty("authorization_revision").GetInt64();
            Assert.Equal(ready.GetProperty("authorization_revision").GetInt64(), ready.GetProperty("projection_applied_revision").GetInt64());
            var readerAfter = await s.WaitReadableAsync(s.Reader);
            s.Evidence.ObserveHttp("reader read after projection caught up", readerAfter, "isCompleteProjection");
            Assert.Equal(HttpStatusCode.OK, readerAfter.Status);
            Assert.Equal(["given_name"], readerAfter.Root.GetProperty("values").EnumerateArray().Select(v => v.GetProperty("fieldKey").GetString()).ToList());
        });
    }

    [Fact]
    [Trait("SEC-P", "P08")]
    public async Task SecP08_SigningOutageNeverCommitsAndRetriesIdempotently()
    {
        var s = new Scenario(fixture, "SEC-P08", "Signing service outage: signing-required writes never commit as accepted; retries are idempotent after restoration; no insecure fallback");
        await ScenarioRunner.RunAsync(s, async () =>
        {
            await s.BootstrapAsync();
            await s.CreateFormAndRecordAsync();
            var gateBefore = await fixture.Authority.GateAsync(s.OrganizationId);
            var idempotencyKey = "sec-p08-" + Guid.NewGuid().ToString("N");

            fixture.Stack.Pause("vault");
            HttpResult duringOutage;
            try
            {
                duringOutage = await fixture.Api.PostAsync($"/api/v1/form-definitions/{s.FormDefinitionId:D}/records", s.Token(s.Owner), new { values = new Dictionary<string, string> { ["given_name"] = "Outage", ["family_name"] = "Case" } }, idempotencyKey);
                s.Evidence.ObserveHttp("create record while Vault paused", duringOutage, "title", "detail");
                Assert.Equal(HttpStatusCode.ServiceUnavailable, duringOutage.Status);
                Assert.Contains("SIGNING_UNAVAILABLE", duringOutage.Body, StringComparison.Ordinal);

                var edit = await fixture.Api.PatchAsync($"/api/v1/form-records/{s.FormRecordId:D}", s.Token(s.Owner), new { expectedContentRevision = 1, values = new Dictionary<string, string> { ["case_note"] = "during outage" } });
                s.Evidence.ObserveHttp("update existing record while Vault paused", edit, "title");
                Assert.Equal(HttpStatusCode.ServiceUnavailable, edit.Status);

                // Verification also needs the signer: reads fail closed rather than trusting unverified rows.
                var read = await s.ReadAsync(s.Owner);
                s.Evidence.ObserveHttp("owner read while Vault paused", read, "title");
                Assert.Equal(HttpStatusCode.ServiceUnavailable, read.Status);
                Assert.DoesNotContain("protected note", read.Body, StringComparison.Ordinal);
            }
            finally
            {
                fixture.Stack.Unpause("vault");
            }

            await fixture.Authority.WaitReadyAsync(TimeSpan.FromSeconds(60));
            var retry = await fixture.Api.PostAsync($"/api/v1/form-definitions/{s.FormDefinitionId:D}/records", s.Token(s.Owner), new { values = new Dictionary<string, string> { ["given_name"] = "Outage", ["family_name"] = "Case" } }, idempotencyKey);
            s.Evidence.ObserveHttp("retry with the same idempotency key after restoration", retry, "formRecordId", "contentRevision", "committedOperationId");
            Assert.Equal(HttpStatusCode.Created, retry.Status);
            Assert.Equal(1, retry.Root.GetProperty("contentRevision").GetInt64());
            var newRecord = retry.Root.GetProperty("formRecordId").GetGuid();

            var retryAgain = await fixture.Api.PostAsync($"/api/v1/form-definitions/{s.FormDefinitionId:D}/records", s.Token(s.Owner), new { values = new Dictionary<string, string> { ["given_name"] = "Outage", ["family_name"] = "Case" } }, idempotencyKey);
            s.Evidence.ObserveHttp("second retry (duplicate)", retryAgain, "formRecordId", "contentRevision");
            Assert.Equal(HttpStatusCode.Created, retryAgain.Status);
            Assert.Equal(newRecord, retryAgain.Root.GetProperty("formRecordId").GetGuid());
            Assert.Equal(1, retryAgain.Root.GetProperty("contentRevision").GetInt64());

            var staged = fixture.Stack.AttackerSql($"SELECT count(DISTINCT content_change_operation_id) FROM form_record_revisions WHERE form_record_id = '{newRecord:D}';");
            s.Evidence.Observe("staged header operations for the retried record", new { operations = staged.Trim() });
            Assert.Equal("1", staged.Trim());
            var original = await s.WaitReadableAsync(s.Owner);
            Assert.Equal(HttpStatusCode.OK, original.Status);
            Assert.Equal(1, original.Root.GetProperty("contentRevision").GetInt64());
            var gateAfter = await fixture.Authority.GateAsync(s.OrganizationId);
            s.Evidence.AuthorityRevision = gateAfter.GetProperty("authorization_revision").GetInt64();
            Assert.Equal(gateBefore.GetProperty("authorization_revision").GetInt64() + 1, s.Evidence.AuthorityRevision);
        });
    }

    [Fact]
    [Trait("SEC-P", "P09")]
    public async Task SecP09_CrashBetweenJournalProjectionAndAckRecoversExactlyOnce()
    {
        var s = new Scenario(fixture, "SEC-P09", "Command crash and retry: crash after journal commit and after the external projection write; recovery yields exactly-once effective security semantics");
        await ScenarioRunner.RunAsync(s, async () =>
        {
            await s.BootstrapAsync();
            await s.CreateFormAndRecordAsync();
            var gateBefore = await fixture.Authority.GateAsync(s.OrganizationId);
            var baseRevision = gateBefore.GetProperty("authorization_revision").GetInt64();

            // Crash point 1: after the authority DB transaction (journal + outbox) commits, before the HTTP reply.
            var arm = await fixture.Authority.OperatorAsync(HttpMethod.Post, "/internal/v1/operator/fault-injection", new { fault_name = "CrashAfterJournalCommit" });
            Assert.True(arm.IsSuccess, arm.Body);
            var shareKey = "sec-p09-share-" + Guid.NewGuid().ToString("N");
            var crashed = await fixture.Api.PostAsync($"/api/v1/form-records/{s.FormRecordId:D}/shares", s.Token(s.Owner), new { recipientUserId = s.Reader.AuthorityUserId, recipientRole = "Reader", fieldKeys = new[] { "given_name" } }, shareKey);
            s.Evidence.ObserveHttp("share request during CrashAfterJournalCommit", crashed, "title", "command_status");
            Assert.NotEqual(HttpStatusCode.Created, crashed.Status);
            await fixture.Authority.WaitReadyAsync(TimeSpan.FromSeconds(90));
            var recovered = await s.WaitGateReadyAsync(TimeSpan.FromSeconds(60));
            s.Evidence.Observe("authority restarted and recovered the committed command on its own (journal -> archive -> projection)", recovered.GetRawText());
            Assert.Equal(baseRevision + 1, recovered.GetProperty("authorization_revision").GetInt64());

            var retried = await fixture.Api.PostAsync($"/api/v1/form-records/{s.FormRecordId:D}/shares", s.Token(s.Owner), new { recipientUserId = s.Reader.AuthorityUserId, recipientRole = "Reader", fieldKeys = new[] { "given_name" } }, shareKey);
            s.Evidence.ObserveHttp("same-key retry after restart", retried, "recordShareId", "shareStatus");
            Assert.Equal(HttpStatusCode.Created, retried.Status);
            var shareId = retried.Root.GetProperty("recordShareId").GetGuid();
            var gateMid = await s.WaitGateReadyAsync();
            s.Evidence.Observe("gate after recovery 1", gateMid.GetRawText());
            Assert.Equal(baseRevision + 1, gateMid.GetProperty("authorization_revision").GetInt64());
            var readerRead = await s.WaitReadableAsync(s.Reader);
            Assert.Equal(HttpStatusCode.OK, readerRead.Status);

            // Crash point 2: after the SpiceDB write succeeded, before the authority acknowledged the applied revision.
            var arm2 = await fixture.Authority.OperatorAsync(HttpMethod.Post, "/internal/v1/operator/fault-injection", new { fault_name = "CrashAfterProjectionWrite" });
            Assert.True(arm2.IsSuccess, arm2.Body);
            var revokeKey = "sec-p09-revoke-" + Guid.NewGuid().ToString("N");
            var revoke = await fixture.Api.PostAsync($"/api/v1/form-records/{s.FormRecordId:D}/shares/{shareId:D}/revoke", s.Token(s.Owner), new { }, revokeKey);
            s.Evidence.ObserveHttp("revoke during CrashAfterProjectionWrite", revoke, "title", "command");
            await fixture.Authority.WaitReadyAsync(TimeSpan.FromSeconds(90));
            var gateAfter = await s.WaitGateReadyAsync(TimeSpan.FromSeconds(60));
            s.Evidence.Observe("gate after recovery 2 (ambiguous external write reconciled)", gateAfter.GetRawText());
            Assert.Equal(baseRevision + 2, gateAfter.GetProperty("authorization_revision").GetInt64());
            Assert.Equal(baseRevision + 2, gateAfter.GetProperty("projection_applied_revision").GetInt64());
            s.Evidence.AuthorityRevision = gateAfter.GetProperty("authorization_revision").GetInt64();

            var revokeRetry = await fixture.Api.PostAsync($"/api/v1/form-records/{s.FormRecordId:D}/shares/{shareId:D}/revoke", s.Token(s.Owner), new { }, revokeKey);
            s.Evidence.ObserveHttp("revoke retry with same key", revokeRetry, "command");
            Assert.Equal(HttpStatusCode.OK, revokeRetry.Status);
            Assert.Equal("Effective", revokeRetry.Root.GetProperty("command").GetProperty("commandStatus").GetString());
            var gateFinal = await fixture.Authority.GateAsync(s.OrganizationId);
            Assert.Equal(baseRevision + 2, gateFinal.GetProperty("authorization_revision").GetInt64()); // retry consumed no extra revision
            var readerAfter = await s.ReadAsync(s.Reader);
            s.Evidence.ObserveHttp("reader after recovered revocation", readerAfter);
            Assert.Equal(HttpStatusCode.NotFound, readerAfter.Status);
        });
    }

    [Fact]
    [Trait("SEC-P", "P12")]
    public async Task SecP12_ArchiveOutageKeepsCommandsNonEffectiveAndContentUnreleased()
    {
        var s = new Scenario(fixture, "SEC-P12", "Mandatory audit archive unavailable: operations requiring independently durable evidence stay non-effective; protected bytes are not released without the receipt");
        await ScenarioRunner.RunAsync(s, async () =>
        {
            await s.BootstrapAsync();
            await s.CreateFormAndRecordAsync();
            var ok = await s.ReadAsync(s.Owner);
            Assert.Equal(HttpStatusCode.OK, ok.Status);

            fixture.Stack.Pause("archive-store");
            try
            {
                var share = await fixture.Api.PostAsync($"/api/v1/form-records/{s.FormRecordId:D}/shares", s.Token(s.Owner), new { recipientUserId = s.Reader.AuthorityUserId, recipientRole = "Reader", fieldKeys = new[] { "given_name" } });
                s.Evidence.ObserveHttp("share while archive paused", share, "title", "command_status");
                Assert.Equal(HttpStatusCode.Accepted, share.Status);
                Assert.Contains("CommittedAwaitingProjection", share.Body, StringComparison.Ordinal);
                var gate = await fixture.Authority.GateAsync(s.OrganizationId);
                s.Evidence.Observe("gate during archive outage", gate.GetRawText());
                Assert.Equal("AwaitingArchive", gate.GetProperty("projection_gate_state").GetString());
                Assert.True(gate.GetProperty("pending_archive_entries").GetInt32() >= 1);

                var readerDuring = await s.ReadAsync(s.Reader);
                s.Evidence.ObserveHttp("reader read during archive outage", readerDuring);
                Assert.NotEqual(HttpStatusCode.OK, readerDuring.Status);
                var ownerDuring = await s.ReadAsync(s.Owner);
                s.Evidence.ObserveHttp("owner read during archive outage (gate closed)", ownerDuring);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, ownerDuring.Status);
                Assert.DoesNotContain("protected note", ownerDuring.Body, StringComparison.Ordinal);
            }
            finally
            {
                fixture.Stack.Unpause("archive-store");
            }

            var ready = await s.WaitGateReadyAsync(TimeSpan.FromSeconds(60));
            s.Evidence.AuthorityRevision = ready.GetProperty("authorization_revision").GetInt64();
            var readerAfter = await s.WaitReadableAsync(s.Reader);
            s.Evidence.ObserveHttp("reader read after archive receipt", readerAfter, "isCompleteProjection");
            Assert.Equal(HttpStatusCode.OK, readerAfter.Status);

            // Content checkpoint receipts gate reads of a new revision until archived.
            fixture.Stack.Pause("archive-store");
            try
            {
                var update = await fixture.Api.PatchAsync($"/api/v1/form-records/{s.FormRecordId:D}", s.Token(s.Owner), new { expectedContentRevision = 1, values = new Dictionary<string, string> { ["case_note"] = "archived later" } });
                s.Evidence.ObserveHttp("update while archive paused (commit allowed, release gated)", update, "contentRevision", "integrityStatus");
                var readDuring = await s.ReadAsync(s.Owner);
                s.Evidence.ObserveHttp("owner read of new revision during archive outage", readDuring, "title");
                Assert.NotEqual(HttpStatusCode.OK, readDuring.Status);
                Assert.DoesNotContain("archived later", readDuring.Body, StringComparison.Ordinal);
            }
            finally
            {
                fixture.Stack.Unpause("archive-store");
            }

            var finalRead = await s.WaitReadableAsync(s.Owner, TimeSpan.FromSeconds(60));
            s.Evidence.ObserveHttp("owner read after content checkpoint archived", finalRead, "contentRevision");
            Assert.Equal(HttpStatusCode.OK, finalRead.Status);
            Assert.Contains("archived later", finalRead.Body, StringComparison.Ordinal);
            s.Evidence.ContentRevision = finalRead.Root.GetProperty("contentRevision").GetInt64();
        });
    }
}
