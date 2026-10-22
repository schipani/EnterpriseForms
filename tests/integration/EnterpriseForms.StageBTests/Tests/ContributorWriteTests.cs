using System.Net;
using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// Phase C5 share-editor (contributor) writes on the real stack (PostgreSQL ×2, SpiceDB, Vault Transit, Authority, API).
/// Contributor writes are OFF by default in every profile (D10); these tests turn them on through the Testing-only
/// operator override and always restore the configured value. Races are made deterministic with the Testing-only
/// <c>PauseBeforeContentCommit</c> point, which holds a prepared commit before it takes the organization lock.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class ContributorWriteTests(StageBFixture fixture)
{
    private async Task SetContributorWritesAsync(bool? enabled)
    {
        var result = await fixture.Authority.OperatorAsync(HttpMethod.Post, "/internal/v1/operator/testing/contributor-writes", new { enabled });
        Assert.True(result.IsSuccess, result.Body);
    }

    private async Task ArmPauseAsync(bool armed)
    {
        var result = await fixture.Authority.OperatorAsync(HttpMethod.Post, "/internal/v1/operator/fault-injection", new { fault_name = armed ? "PauseBeforeContentCommit" : "None" });
        Assert.True(result.IsSuccess, result.Body);
    }

    private async Task WaitPausedAsync(int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var status = await fixture.Authority.OperatorAsync(HttpMethod.Get, "/internal/v1/operator/fault-injection", null);
            Assert.True(status.IsSuccess, status.Body);
            if (status.Root.GetProperty("paused_operations").GetInt32() >= count)
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"{count} commit(s) did not reach the pause point");
    }

    private static async Task<Guid> ShareAsync(StageBScenario s, Guid record, TestUser recipient, string role, params string[] fieldKeys)
    {
        var share = await s.Fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/shares", s.Token(s.Clerk), new { recipientUserId = recipient.AuthorityUserId, recipientTeamId = (Guid?)null, recipientRole = role, fieldKeys });
        Assert.True(share.IsSuccess, share.Body);
        await s.WaitGateReadyAsync(TimeSpan.FromSeconds(40));
        return share.Root.GetProperty("recordShareId").GetGuid();
    }

    private static async Task<HttpResult> EditAsync(StageBScenario s, Guid record, TestUser user, Dictionary<string, string> values, long? expectedRevision = null)
    {
        var revision = expectedRevision ?? (await s.WaitProjectionAsync(user, record, TimeSpan.FromSeconds(40))).Root.GetProperty("record").GetProperty("contentRevision").GetInt64();
        return await s.Fixture.Api.PatchAsync($"/api/v1/form-records/{record:D}", s.Token(user), new { expectedContentRevision = revision, values });
    }

    private static Dictionary<string, string> Values(JsonElement projectionRoot) =>
        projectionRoot.GetProperty("record").GetProperty("values").EnumerateArray().ToDictionary(v => v.GetProperty("fieldKey").GetString()!, v => v.GetProperty("stringValue").GetString()!);

    private static long Revision(JsonElement projectionRoot) => projectionRoot.GetProperty("record").GetProperty("contentRevision").GetInt64();

    private async Task<(StageBScenario S, Guid Record)> AccusedWithDocumentAsync(string name)
    {
        var s = new StageBScenario(fixture, name);
        await s.BootstrapAsync();
        await s.BuildAccusedWorkspaceAsync();
        var record = await s.CreateAccusedAsync(s.Clerk);
        await s.UploadAsync(s.Clerk, record, "c5-note.txt", "text/plain", StageBScenario.SyntheticBytes(1024, 5));
        return (s, record);
    }

    [Fact]
    public async Task ContributorWritesAreOffByDefaultAndOwnerWritesAreUnchanged()
    {
        await SetContributorWritesAsync(null); // the deployment configuration of the security-test stack: off
        var (s, record) = await AccusedWithDocumentAsync("c5-default-off");
        await ShareAsync(s, record, s.Designer, "Contributor", "given_name");
        var forEditor = await s.WaitProjectionAsync(s.Designer, record, TimeSpan.FromSeconds(40));
        var permissions = forEditor.Root.GetProperty("permissions");
        Assert.False(permissions.GetProperty("contributorWritesEnabled").GetBoolean());
        Assert.False(permissions.GetProperty("canEditSharedFields").GetBoolean());
        var access = Assert.Single(forEditor.Root.GetProperty("sharedFieldAccess").EnumerateArray());
        Assert.Equal("ContributorWritesDisabled", access.GetProperty("notEditableReason").GetString());

        var refused = await EditAsync(s, record, s.Designer, new() { ["given_name"] = "Changed" });
        Assert.Equal(HttpStatusCode.Forbidden, refused.Status);
        Assert.Equal("CONTRIBUTOR_WRITES_DISABLED", refused.ErrorCode);
        var ownerView = await s.WaitProjectionAsync(s.Clerk, record);
        Assert.Equal(Revision(forEditor.Root), Revision(ownerView.Root));
        Assert.Equal("Avery", Values(ownerView.Root)["given_name"]);

        var ownerEdit = await EditAsync(s, record, s.Clerk, new() { ["family_name"] = "Owner-Edited" });
        Assert.True(ownerEdit.IsSuccess, ownerEdit.Body);
        Assert.Equal("Owner-Edited", Values((await s.WaitProjectionAsync(s.Clerk, record)).Root)["family_name"]);
    }

    [Fact]
    public async Task AContributorChangesSharedValuesAndEveryOtherItemIsPreservedByteForByte()
    {
        var (s, record) = await AccusedWithDocumentAsync("c5-preserve");
        try
        {
            await SetContributorWritesAsync(true);
            await ShareAsync(s, record, s.Designer, "Contributor", "given_name", "case_status");
            var operationBefore = fixture.Stack.AttackerSql($"SELECT accepted_content_change_operation_id FROM form_records WHERE form_record_id = '{record:D}';").Trim();
            string Ciphertext(string op, string fieldKey) => fixture.Stack.AttackerSql(
                $"SELECT encode(e.encrypted_value_bytes,'hex') || ':' || encode(e.encryption_nonce_bytes,'hex') || ':' || encode(e.wrapped_data_key_bytes,'hex') FROM field_encrypted_values e JOIN field_definitions f ON f.field_definition_id = e.field_definition_id WHERE e.form_record_id = '{record:D}' AND f.field_key = '{fieldKey}' AND e.content_change_operation_id = '{op}';").Trim();
            string Document(string op) => fixture.Stack.AttackerSql($"SELECT storage_object_key || ':' || encode(ciphertext_sha256,'hex') || ':' || encode(wrapped_data_key,'hex') FROM file_attachment_versions WHERE form_record_id = '{record:D}' AND content_change_operation_id = '{op}';").Trim();
            var familyBefore = Ciphertext(operationBefore, "family_name");
            var givenBefore = Ciphertext(operationBefore, "given_name");
            var documentBefore = Document(operationBefore);
            Assert.NotEmpty(familyBefore);
            Assert.NotEmpty(documentBefore);

            var forEditor = await s.WaitProjectionAsync(s.Designer, record, TimeSpan.FromSeconds(40));
            Assert.True(forEditor.Root.GetProperty("permissions").GetProperty("canEditSharedFields").GetBoolean());
            var edited = await EditAsync(s, record, s.Designer, new() { ["given_name"] = "Contributor-Edited", ["case_status"] = "closed" }, Revision(forEditor.Root));
            Assert.True(edited.IsSuccess, edited.Body);

            var ownerView = await s.WaitProjectionAsync(s.Clerk, record);
            Assert.Equal(Revision(forEditor.Root) + 1, Revision(ownerView.Root));
            var values = Values(ownerView.Root);
            Assert.Equal("Contributor-Edited", values["given_name"]);
            Assert.Equal("closed", values["case_status"]);
            Assert.Equal("Synthetic", values["family_name"]);
            Assert.Single(ownerView.Root.GetProperty("documents").EnumerateArray());

            // D15: the out-of-scope ciphertext and the document version are the same bytes under the new operation; the edited value is a new envelope.
            var operationAfter = fixture.Stack.AttackerSql($"SELECT accepted_content_change_operation_id FROM form_records WHERE form_record_id = '{record:D}';").Trim();
            Assert.NotEqual(operationBefore, operationAfter);
            Assert.Equal(familyBefore, Ciphertext(operationAfter, "family_name"));
            Assert.Equal(documentBefore, Document(operationAfter));
            Assert.NotEqual(givenBefore, Ciphertext(operationAfter, "given_name"));
            Assert.Contains("\"editBasis\":\"share\"", fixture.Stack.AttackerSql($"SELECT event_metadata_json::text FROM audit_events WHERE form_record_id = '{record:D}' AND event_action = 'RECORD_UPDATED' ORDER BY audit_sequence DESC LIMIT 1;").Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        }
        finally
        {
            await SetContributorWritesAsync(null);
        }
    }

    [Fact]
    public async Task OutOfScopeUnsupportedAndViewOnlyEditsAreRefusedWithoutChangingTheRecord()
    {
        var (s, record) = await AccusedWithDocumentAsync("c5-refusals");
        try
        {
            await SetContributorWritesAsync(true);
            await ShareAsync(s, record, s.Designer, "Contributor", "given_name");
            await ShareAsync(s, record, s.Reader, "Reader", "given_name");
            var forEditor = await s.WaitProjectionAsync(s.Designer, record, TimeSpan.FromSeconds(40));
            var revision = Revision(forEditor.Root);

            var unknown = await EditAsync(s, record, s.Designer, new() { ["given_name"] = "x", ["no_such_field"] = "y" }, revision);
            var unshared = await EditAsync(s, record, s.Designer, new() { ["given_name"] = "x", ["family_name"] = "y" }, revision);
            Assert.Equal((HttpStatusCode.Forbidden, "FIELD_NOT_EDITABLE"), (unknown.Status, unknown.ErrorCode));
            Assert.Equal((HttpStatusCode.Forbidden, "FIELD_NOT_EDITABLE"), (unshared.Status, unshared.ErrorCode));
            Assert.Equal(JsonSerializer.Serialize(unknown.Root.GetProperty("detail")), JsonSerializer.Serialize(unshared.Root.GetProperty("detail"))); // indistinguishable

            Assert.Equal("CLEAR_NOT_PERMITTED", (await EditAsync(s, record, s.Designer, new() { ["given_name"] = string.Empty }, revision)).ErrorCode);
            var reader = await EditAsync(s, record, s.Reader, new() { ["given_name"] = "x" }, revision);
            Assert.Equal((HttpStatusCode.Forbidden, "FIELD_NOT_EDITABLE"), (reader.Status, reader.ErrorCode));
            var stale = await EditAsync(s, record, s.Designer, new() { ["given_name"] = "x" }, revision - 1);
            Assert.Equal(HttpStatusCode.Conflict, stale.Status);

            var ownerView = await s.WaitProjectionAsync(s.Clerk, record);
            Assert.Equal(revision, Revision(ownerView.Root));
            Assert.Equal("Avery", Values(ownerView.Root)["given_name"]);
        }
        finally
        {
            await SetContributorWritesAsync(null);
        }
    }

    [Fact]
    public async Task ARevocationBetweenPrepareAndCommitAbortsTheWrite()
    {
        var (s, record) = await AccusedWithDocumentAsync("c5-revoke-race");
        try
        {
            await SetContributorWritesAsync(true);
            var shareId = await ShareAsync(s, record, s.Designer, "Contributor", "given_name");
            var revision = Revision((await s.WaitProjectionAsync(s.Designer, record, TimeSpan.FromSeconds(40))).Root);

            await ArmPauseAsync(true);
            var write = EditAsync(s, record, s.Designer, new() { ["given_name"] = "After-Revocation" }, revision);
            await WaitPausedAsync(1);
            var revoke = await fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/shares/{shareId:D}/revoke", s.Token(s.Clerk), new { });
            Assert.True(revoke.IsSuccess, revoke.Body);
            await s.WaitGateReadyAsync(TimeSpan.FromSeconds(40));
            await ArmPauseAsync(false); // release the held commit: it now re-authorizes against the current edges

            var result = await write;
            Assert.Equal(HttpStatusCode.Forbidden, result.Status);
            var ownerView = await s.WaitProjectionAsync(s.Clerk, record);
            Assert.Equal(revision, Revision(ownerView.Root));
            Assert.Equal("Avery", Values(ownerView.Root)["given_name"]);
        }
        finally
        {
            await ArmPauseAsync(false);
            await SetContributorWritesAsync(null);
        }
    }

    [Fact]
    public async Task TurningContributorWritesOffBetweenPrepareAndCommitBlocksTheCommit()
    {
        var (s, record) = await AccusedWithDocumentAsync("c5-switch-race");
        try
        {
            await SetContributorWritesAsync(true);
            await ShareAsync(s, record, s.Designer, "Contributor", "given_name");
            var revision = Revision((await s.WaitProjectionAsync(s.Designer, record, TimeSpan.FromSeconds(40))).Root);

            await ArmPauseAsync(true);
            var write = EditAsync(s, record, s.Designer, new() { ["given_name"] = "Switched-Off" }, revision);
            await WaitPausedAsync(1);
            await SetContributorWritesAsync(false);
            await ArmPauseAsync(false);

            var result = await write;
            Assert.Equal((HttpStatusCode.Forbidden, "CONTRIBUTOR_WRITES_DISABLED"), (result.Status, result.ErrorCode));
            Assert.Equal(revision, Revision((await s.WaitProjectionAsync(s.Clerk, record)).Root));
        }
        finally
        {
            await ArmPauseAsync(false);
            await SetContributorWritesAsync(null);
        }
    }

    [Fact]
    public async Task ConcurrentOwnerAndContributorCommitsAcceptExactlyOne()
    {
        var (s, record) = await AccusedWithDocumentAsync("c5-concurrent");
        try
        {
            await SetContributorWritesAsync(true);
            await ShareAsync(s, record, s.Designer, "Contributor", "given_name");
            var revision = Revision((await s.WaitProjectionAsync(s.Designer, record, TimeSpan.FromSeconds(40))).Root);

            await ArmPauseAsync(true);
            var contributor = EditAsync(s, record, s.Designer, new() { ["given_name"] = "By-Contributor" }, revision);
            var owner = EditAsync(s, record, s.Clerk, new() { ["given_name"] = "By-Owner" }, revision);
            await WaitPausedAsync(2);
            await ArmPauseAsync(false);
            var results = await Task.WhenAll(contributor, owner);

            Assert.Single(results, r => r.IsSuccess);
            Assert.Single(results, r => r.Status == HttpStatusCode.Conflict);
            var winner = results[0].IsSuccess ? "By-Contributor" : "By-Owner";
            var ownerView = await s.WaitProjectionAsync(s.Clerk, record);
            Assert.Equal(revision + 1, Revision(ownerView.Root));
            Assert.Equal(winner, Values(ownerView.Root)["given_name"]);
        }
        finally
        {
            await ArmPauseAsync(false);
            await SetContributorWritesAsync(null);
        }
    }

    [Fact]
    public async Task UnderARevocationRaceNoContributorWriteIsAcceptedAtOrAfterTheRevocation()
    {
        var (s, record) = await AccusedWithDocumentAsync("c5-revocation-load");
        try
        {
            await SetContributorWritesAsync(true);
            var shareId = await ShareAsync(s, record, s.Designer, "Contributor", "given_name");
            var accepted = new System.Collections.Concurrent.ConcurrentQueue<long>();
            var refusedAfterRevocation = 0;
            var revoked = new TaskCompletionSource();
            var cancellation = TestContext.Current.CancellationToken;
            var writer = Task.Run(async () =>
            {
                for (var i = 0; i < 40 && refusedAfterRevocation < 2; i++)
                {
                    var projection = await s.ProjectionAsync(s.Designer, record);
                    if (!projection.IsSuccess)
                    {
                        if (revoked.Task.IsCompleted)
                        {
                            refusedAfterRevocation++;
                        }

                        await Task.Delay(100, cancellation);
                        continue;
                    }

                    var result = await s.Fixture.Api.PatchAsync($"/api/v1/form-records/{record:D}", s.Token(s.Designer), new { expectedContentRevision = Revision(projection.Root), values = new Dictionary<string, string> { ["given_name"] = "Load-" + i } });
                    if (result.IsSuccess)
                    {
                        accepted.Enqueue(result.Root.GetProperty("authorizationRevision").GetInt64());
                    }
                    else if (revoked.Task.IsCompleted && result.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                    {
                        refusedAfterRevocation++;
                    }
                }
            }, cancellation);

            // Revoke only once at least one contributor write has landed, so the race always has both sides.
            var firstWrite = DateTime.UtcNow.AddSeconds(60);
            while (accepted.IsEmpty && DateTime.UtcNow < firstWrite)
            {
                await Task.Delay(100, cancellation);
            }

            var revoke = await fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/shares/{shareId:D}/revoke", s.Token(s.Clerk), new { });
            Assert.True(revoke.IsSuccess, revoke.Body);
            var gate = await s.WaitGateReadyAsync(TimeSpan.FromSeconds(40));
            var revocationRevision = gate.GetProperty("authorization_revision").GetInt64();
            revoked.SetResult();
            await writer;

            Assert.NotEmpty(accepted);                        // writes did land before the revocation
            Assert.All(accepted, r => Assert.True(r < revocationRevision, $"a write was accepted at authorization revision {r} >= revocation revision {revocationRevision}"));
            Assert.True(refusedAfterRevocation >= 1);
        }
        finally
        {
            await SetContributorWritesAsync(null);
        }
    }
}
