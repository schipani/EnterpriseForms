using System.Globalization;
using System.Net;
using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// Phase C10 focused Stage B (WP-0 §3, §4, §10.5) on the real stack (PostgreSQL ×2, SpiceDB, Vault Transit, scanner, Authority, API):
/// share update under its revision, access-time expiry on every reachable path before convergence (§4 test 9), inherited expiry and
/// descendant invalidation (§4 test 10), expiry racing a revoke, an update and a transfer (§4 test 8), member deactivation and
/// reactivation, child-record ownership, delegated shares against the real Authority (F-6 regression), document shares with version
/// boundaries, replacement, the D-5 share revision and journal entry, live download re-checks, business upload admission and version
/// numbering from the accepted version (owner decision 2). Every human share and transfer command carries the development step-up (G27).
/// The expiry convergence loop runs every 30 s in Development/Testing and cannot be switched off; access-time checks run within a
/// second of the expiry instant and assert from AuthorityDb that convergence had not yet run.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class C10ShareLifecycleTests(StageBFixture fixture)
{
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(40);

    // ---- helpers (existing harness only) ----

    private static string Path(Guid record, string suffix = "") => $"/api/v1/form-records/{record:D}/shares{suffix}";

    private static async Task<HttpResult> CreateShareAsync(StageBScenario s, TestUser grantor, Guid record, TestUser recipient, string role, string[] fieldKeys,
        object[]? documents = null, DateTimeOffset? expiresOn = null, Guid? parent = null) =>
        await s.Fixture.Api.PostAsync(Path(record), s.Token(grantor), new
        {
            useDevelopmentStepUp = true, recipientUserId = recipient.AuthorityUserId, recipientTeamId = (Guid?)null, recipientRole = role, fieldKeys, documents,
            shareExpiresOn = expiresOn, parentRecordShareId = parent,
        });

    private static async Task<Guid> ShareAsync(StageBScenario s, TestUser grantor, Guid record, TestUser recipient, string role, string[] fieldKeys,
        object[]? documents = null, DateTimeOffset? expiresOn = null, Guid? parent = null)
    {
        var created = await CreateShareAsync(s, grantor, record, recipient, role, fieldKeys, documents, expiresOn, parent);
        Assert.True(created.IsSuccess, created.Body);
        await s.WaitGateReadyAsync(Settle);
        return created.Root.GetProperty("recordShareId").GetGuid();
    }

    private static Task<HttpResult> GetShareAsync(StageBScenario s, TestUser user, Guid record, Guid share) => s.Fixture.Api.GetAsync(Path(record, $"/{share:D}"), s.Token(user));

    private static async Task<long> ShareRevisionAsync(StageBScenario s, Guid record, Guid share)
    {
        var details = await GetShareAsync(s, s.Clerk, record, share);
        Assert.True(details.IsSuccess, details.Body);
        return details.Root.GetProperty("shareRevision").GetInt64();
    }

    private static Task<HttpResult> UpdateShareAsync(StageBScenario s, TestUser user, Guid record, Guid share, long revision, string role, string[] fieldKeys, Guid? parent = null, object[]? documents = null) =>
        s.Fixture.Api.SendAsync(HttpMethod.Patch, Path(record, $"/{share:D}"), s.Token(user),
            new { recipientRole = role, fieldKeys, noExpiry = true, parentRecordShareId = parent, documents, useDevelopmentStepUp = true },
            Guid.NewGuid().ToString("N"), "\"" + share.ToString("D") + ":" + revision.ToString(CultureInfo.InvariantCulture) + "\"");

    private static async Task RevokeAsync(StageBScenario s, TestUser user, Guid record, Guid share)
    {
        var revoked = await s.Fixture.Api.PostAsync(Path(record, $"/{share:D}/revoke?useDevelopmentStepUp=true"), s.Token(user), new { });
        Assert.True(revoked.IsSuccess, revoked.Body);
        await s.WaitGateReadyAsync(Settle);
    }

    private static Task<HttpResult> TransferAsync(StageBScenario s, TestUser user, Guid record, TestUser newOwner) =>
        s.Fixture.Api.PostAsync($"/api/v1/form-records/{record:D}/transfer-ownership", s.Token(user),
            new { useDevelopmentStepUp = true, newOwnerUserId = newOwner.AuthorityUserId, newOwnerTeamId = (Guid?)null, transferReason = "C10 Stage B transfer" });

    private static string[] VisibleKeys(HttpResult projection) =>
        [.. projection.Root.GetProperty("record").GetProperty("values").EnumerateArray().Select(v => v.GetProperty("fieldKey").GetString()!).Order(StringComparer.Ordinal)];

    private static bool Denied(HttpResult result) => result.Status is HttpStatusCode.Forbidden or HttpStatusCode.NotFound;

    private static async Task<HttpResult> UntilAsync(Func<Task<HttpResult>> read, Func<HttpResult, bool> done, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        HttpResult last;
        do
        {
            last = await read();
            if (done(last))
            {
                return last;
            }

            await Task.Delay(500);
        }
        while (DateTime.UtcNow < deadline);
        return last;
    }

    private string ShareResourceState(Guid share) =>
        fixture.Stack.AuthorityObserverSql($"SELECT resource_lifecycle_state FROM authority_resources WHERE resource_type_name = 'record_share' AND resource_entity_id = '{share:D}';").Trim();

    private int JournalEvents(string kind, Guid record) =>
        int.Parse(fixture.Stack.AuthorityObserverSql($"SELECT count(*) FROM security_journal_entries WHERE journal_event_kind = '{kind}' AND journal_event_body_json LIKE '%{record:D}%';").Trim(), CultureInfo.InvariantCulture);

    private async Task WaitConvergedAsync(Guid share, string state, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (ShareResourceState(share) != state)
        {
            Assert.True(DateTime.UtcNow < deadline, $"share {share:D} did not converge to {state} (now {ShareResourceState(share)})");
            await Task.Delay(1000);
        }
    }

    private static async Task WaitUntilAsync(DateTimeOffset instant)
    {
        var wait = instant - DateTimeOffset.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait);
        }
    }

    private async Task SetContributorWritesAsync(bool? enabled)
    {
        var result = await fixture.Authority.OperatorAsync(HttpMethod.Post, "/internal/v1/operator/testing/contributor-writes", new { enabled });
        Assert.True(result.IsSuccess, result.Body);
    }

    private async Task<(StageBScenario S, Guid Record)> AccusedAsync(string name)
    {
        var s = new StageBScenario(fixture, name);
        await s.BootstrapAsync();
        await s.BuildAccusedWorkspaceAsync();
        return (s, await s.CreateAccusedAsync(s.Clerk));
    }

    private static object Document(Guid attachment, bool download = true, bool replace = false) => new { fileAttachmentId = attachment, canDownloadDocument = download, canReplaceDocument = replace };

    private static string DownloadPath(Guid record, Guid attachment) => $"/api/v1/form-records/{record:D}/documents/{attachment:D}/download";

    // ---- share lifecycle ----

    [Fact]
    public async Task AShareIsUpdatedUnderItsRevisionNarrowedAndRevokedAndItsHistoryListsEachStep()
    {
        var (s, record) = await AccusedAsync("c10-share-update");
        var share = await ShareAsync(s, s.Clerk, record, s.Reader, "Reader", ["given_name", "family_name"]);
        Assert.Equal(["family_name", "given_name"], VisibleKeys(await s.WaitProjectionAsync(s.Reader, record, Settle)));

        var revision = await ShareRevisionAsync(s, record, share);
        var narrowed = await UpdateShareAsync(s, s.Clerk, record, share, revision, "Reader", ["given_name"]);
        Assert.True(narrowed.IsSuccess, narrowed.Body);
        await s.WaitGateReadyAsync(Settle);
        Assert.Equal(revision + 1, await ShareRevisionAsync(s, record, share));
        var stale = await UpdateShareAsync(s, s.Clerk, record, share, revision, "Reader", ["family_name"]);
        Assert.Equal((HttpStatusCode.PreconditionFailed, "STALE_SHARE_REVISION"), (stale.Status, stale.ErrorCode));
        var reader = await UntilAsync(() => s.ProjectionAsync(s.Reader, record), r => r.IsSuccess && VisibleKeys(r).SequenceEqual(["given_name"]), Settle);
        Assert.Equal(["given_name"], VisibleKeys(reader));

        var history = await fixture.Api.GetAsync(Path(record, "/history"), s.Token(s.Clerk));
        Assert.True(history.IsSuccess, history.Body);
        var kinds = history.Root.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("commandKind").GetString()).ToList();
        Assert.Contains("CreateShare", kinds);
        Assert.Contains("UpdateShareScope", kinds);

        await RevokeAsync(s, s.Clerk, record, share);
        Assert.True(Denied(await s.ProjectionAsync(s.Reader, record)));
        Assert.Equal("Revoked", (await GetShareAsync(s, s.Clerk, record, share)).Root.GetProperty("shareStatus").GetString());
    }

    [Fact]
    public async Task AccessEndsAtTheExpiryInstantOnEveryReachablePathBeforeConvergence()
    {
        // §4 test 9: projection, discovery (record list), My access, effective scope and download deny from the expiry instant,
        // independently of the convergence loop; convergence then records exactly one ShareExpired.
        var (s, record) = await AccusedAsync("c10-expiry-instant");
        var document = await s.UploadAsync(s.Clerk, record, "c10-expiry.txt", "text/plain", StageBScenario.SyntheticBytes(1024, 91));
        var attachment = document.GetProperty("fileAttachmentId").GetGuid();
        var expiresOn = DateTimeOffset.UtcNow.AddSeconds(20);
        var share = await ShareAsync(s, s.Clerk, record, s.Reader, "Reader", ["given_name"], [Document(attachment)], expiresOn);
        await s.WaitProjectionAsync(s.Reader, record, Settle);
        Assert.Equal(HttpStatusCode.OK, (await fixture.DownloadAsync(DownloadPath(record, attachment), s.Token(s.Reader))).Status);

        await WaitUntilAsync(expiresOn.AddMilliseconds(500));
        Assert.True(ShareResourceState(share) == "Active", "convergence ran before the access checks; rerun (the loop runs every 30 s)");
        Assert.True(Denied(await s.ProjectionAsync(s.Reader, record)));
        Assert.Equal(HttpStatusCode.NotFound, (await fixture.DownloadAsync(DownloadPath(record, attachment), s.Token(s.Reader))).Status);
        var listed = await fixture.Api.GetAsync("/api/v1/form-records?pageSize=50", s.Token(s.Reader));
        Assert.True(listed.IsSuccess, listed.Body);
        Assert.DoesNotContain(listed.Root.GetProperty("items").EnumerateArray(), i => i.GetProperty("formRecordId").GetGuid() == record);
        var mine = await fixture.Api.GetAsync("/api/v1/me/access", s.Token(s.Reader));
        Assert.True(mine.IsSuccess, mine.Body);
        Assert.DoesNotContain(mine.Root.GetProperty("entries").EnumerateArray(), e => e.GetProperty("formRecordId").GetGuid() == record && e.GetProperty("recordShareId").ValueKind == JsonValueKind.String && e.GetProperty("recordShareId").GetGuid() == share);
        var scope = await fixture.Api.GetAsync(Path(record, $"/{share:D}/effective-scope"), s.Token(s.Clerk));
        Assert.True(scope.IsSuccess, scope.Body);
        Assert.Equal("Expired", scope.Root.GetProperty("status").GetString());

        await WaitConvergedAsync(share, "Expired", TimeSpan.FromSeconds(90));
        Assert.Equal(1, JournalEvents("ShareExpired", record));
        Assert.True(Denied(await s.ProjectionAsync(s.Reader, record)));
    }

    [Fact]
    public async Task AnAncestorsExpiryDeniesTheDelegatedShareAtOnceAndAnIndependentGrantKeepsItsAccess()
    {
        // §4 test 10 and the set-based descendant read (FindShareMetadataByParentsAsync) on real PostgreSQL.
        var (s, record) = await AccusedAsync("c10-inherited-expiry");
        var expiresOn = DateTimeOffset.UtcNow.AddSeconds(25);
        var manager = await ShareAsync(s, s.Clerk, record, s.Reader, "Manager", ["given_name", "family_name"], expiresOn: expiresOn);
        await s.WaitProjectionAsync(s.Reader, record, Settle);
        var delegated = await ShareAsync(s, s.Reader, record, s.DesignerOnly, "Reader", ["given_name"], parent: manager);
        await ShareAsync(s, s.Clerk, record, s.DesignerOnly, "Reader", ["family_name"]); // independent grant
        Assert.Equal(["family_name", "given_name"], VisibleKeys(await s.WaitProjectionAsync(s.DesignerOnly, record, Settle)));

        await WaitUntilAsync(expiresOn.AddMilliseconds(500));
        Assert.True(ShareResourceState(delegated) == "Active", "convergence ran before the access check; rerun");
        Assert.Equal(["family_name"], VisibleKeys(await s.WaitProjectionAsync(s.DesignerOnly, record, Settle)));

        await WaitConvergedAsync(manager, "Expired", TimeSpan.FromSeconds(90));
        await WaitConvergedAsync(delegated, "Invalidated", TimeSpan.FromSeconds(30));
        Assert.Equal(["family_name"], VisibleKeys(await s.WaitProjectionAsync(s.DesignerOnly, record, Settle)));
    }

    [Fact]
    public async Task ExpiryRacingARevokeAnUpdateAndATransferHasExactlyOneOutcome()
    {
        // §4 test 8: after the expiry instant but before convergence, a revoke wins (no ShareExpired), an update is refused and the
        // share converges to Expired once, and a transfer leaves the durable share to converge once. No double journal entry.
        var (s, record) = await AccusedAsync("c10-expiry-races");
        var expiresOn = DateTimeOffset.UtcNow.AddSeconds(20);
        var revoked = await ShareAsync(s, s.Clerk, record, s.Reader, "Reader", ["given_name"], expiresOn: expiresOn);
        var updated = await ShareAsync(s, s.Clerk, record, s.DesignerOnly, "Reader", ["given_name"], expiresOn: expiresOn);
        var transferred = await ShareAsync(s, s.Clerk, record, s.Administrator, "Reader", ["family_name"], expiresOn: expiresOn);
        var revision = await ShareRevisionAsync(s, record, updated);

        await WaitUntilAsync(expiresOn.AddMilliseconds(500));
        Assert.True(ShareResourceState(revoked) == "Active", "convergence ran before the race; rerun");
        await RevokeAsync(s, s.Clerk, record, revoked);
        var update = await UpdateShareAsync(s, s.Clerk, record, updated, revision, "Reader", ["family_name"]);
        Assert.False(update.IsSuccess, update.Body); // an expired share has no active lineage to update
        var transfer = await TransferAsync(s, s.Clerk, record, s.Reader);
        Assert.True(transfer.IsSuccess, transfer.Body);
        await s.WaitGateReadyAsync(Settle);

        await WaitConvergedAsync(updated, "Expired", TimeSpan.FromSeconds(90));
        await WaitConvergedAsync(transferred, "Expired", TimeSpan.FromSeconds(30));
        Assert.Equal("Revoked", ShareResourceState(revoked));
        Assert.Equal(2, JournalEvents("ShareExpired", record)); // updated and transferred, once each; never the revoked share
    }

    [Fact]
    public async Task DeactivationInvalidatesTheMembersDelegationsAndReactivationRestoresNone()
    {
        var (s, record) = await AccusedAsync("c10-deactivation");
        var manager = await ShareAsync(s, s.Clerk, record, s.Reader, "Manager", ["given_name"]);
        await s.WaitProjectionAsync(s.Reader, record, Settle);
        var delegated = await ShareAsync(s, s.Reader, record, s.DesignerOnly, "Reader", ["given_name"], parent: manager);
        await s.WaitProjectionAsync(s.DesignerOnly, record, Settle);

        var disabled = await fixture.Api.PostAsync($"/api/v1/organization/members/{s.Reader.AuthorityUserId:D}/disable", s.Token(s.Designer), new { reason = "C10 deactivation", useDevelopmentStepUp = true });
        Assert.True(disabled.IsSuccess, disabled.Body);
        await s.WaitGateReadyAsync(Settle);
        Assert.Equal("Invalidated", ShareResourceState(delegated));
        Assert.True(Denied(await s.ProjectionAsync(s.DesignerOnly, record)));

        var enabled = await fixture.Api.PostAsync($"/api/v1/organization/members/{s.Reader.AuthorityUserId:D}/enable", s.Token(s.Designer), new { reason = "C10 reactivation", useDevelopmentStepUp = true });
        Assert.True(enabled.IsSuccess, enabled.Body);
        await s.WaitGateReadyAsync(Settle);
        Assert.Equal(["given_name"], VisibleKeys(await s.WaitProjectionAsync(s.Reader, record, Settle))); // the member's own share is usable again
        Assert.Equal("Invalidated", ShareResourceState(delegated)); // terminal: nothing restored
        Assert.True(Denied(await s.ProjectionAsync(s.DesignerOnly, record)));
    }

    // ---- ownership ----

    [Fact]
    public async Task AChildRecordIsNeverTransferredOnItsOwnAndFollowsItsParent()
    {
        var (s, record) = await AccusedAsync("c10-child-ownership");
        var child = await s.CreateAddressAsync(s.Clerk, record, "1 Inherited Way");
        Assert.False((await s.WaitProjectionAsync(s.Clerk, child, Settle)).Root.GetProperty("permissions").GetProperty("canTransferOwnership").GetBoolean());

        // The reported retention sequence: transfer the child to oneself (or anyone), then the parent away.
        foreach (var target in new[] { s.Clerk, s.Reader })
        {
            var refused = await TransferAsync(s, s.Clerk, child, target);
            Assert.Equal(((HttpStatusCode)422, "CHILD_RECORD_OWNERSHIP_FOLLOWS_PARENT"), (refused.Status, refused.ErrorCode));
        }

        var administrator = await TransferAsync(s, s.Administrator, child, s.Reader);
        Assert.Equal(((HttpStatusCode)422, "CHILD_RECORD_OWNERSHIP_FOLLOWS_PARENT"), (administrator.Status, administrator.ErrorCode));
        var parent = await TransferAsync(s, s.Clerk, record, s.Reader);
        Assert.True(parent.IsSuccess, parent.Body);
        await s.WaitGateReadyAsync(Settle);
        Assert.True((await s.WaitProjectionAsync(s.Reader, child, Settle)).Root.GetProperty("permissions").GetProperty("isCompleteProjection").GetBoolean());
        Assert.True(Denied(await s.ProjectionAsync(s.Clerk, child)));
        Assert.Equal("0", fixture.Stack.AuthorityObserverSql($"SELECT count(*) FROM authority_relationship_edges WHERE resource_type_name = 'form_record' AND resource_entity_id = '{child:D}' AND relationship_name = 'owner' AND relationship_removed_on IS NULL;").Trim());
    }

    // ---- delegation (F-6 regression against the real Authority) ----

    [Fact]
    public async Task AManagerDelegatesAndUpdatesWithinWhatItCanReadAndEveryOtherFieldGetsOneAnswer()
    {
        var (s, record) = await AccusedAsync("c10-delegation");
        var manager = await ShareAsync(s, s.Clerk, record, s.Reader, "Manager", ["given_name", "family_name"]);
        await s.WaitProjectionAsync(s.Reader, record, Settle);

        var delegated = await ShareAsync(s, s.Reader, record, s.DesignerOnly, "Reader", ["given_name"], parent: manager);
        var answers = new List<(HttpStatusCode, string?)>();
        foreach (var keys in new[] { new[] { "date_of_birth" }, ["no_such_field"], ["given_name", "date_of_birth"] })
        {
            var refused = await CreateShareAsync(s, s.Reader, record, s.DesignerOnly, "Reader", keys, parent: manager);
            answers.Add((refused.Status, refused.ErrorCode));
        }

        Assert.Equal((HttpStatusCode.Forbidden, "FIELD_NOT_IN_SHAREABLE_SCOPE"), Assert.Single(answers.Distinct()));
        var widened = await UpdateShareAsync(s, s.Reader, record, delegated, await ShareRevisionAsync(s, record, delegated), "Reader", ["given_name", "family_name"], parent: manager);
        Assert.True(widened.IsSuccess, widened.Body);
        await s.WaitGateReadyAsync(Settle);
        Assert.Equal(["family_name", "given_name"], VisibleKeys(await s.WaitProjectionAsync(s.DesignerOnly, record, Settle)));

        await RevokeAsync(s, s.Clerk, record, manager);
        Assert.Equal("Invalidated", ShareResourceState(delegated));
        Assert.True(Denied(await s.ProjectionAsync(s.DesignerOnly, record)));
        var list = await fixture.Api.GetAsync(Path(record), s.Token(s.Clerk));
        Assert.True(list.IsSuccess, list.Body);
        Assert.Equal("Invalidated", list.Root.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("recordShareId").GetGuid() == delegated).GetProperty("shareStatus").GetString()); // F6
    }

    // ---- documents ----

    [Fact]
    public async Task ADocumentShareReleasesOnlyItsBoundaryAndAReplacementExtendsItUnderANewShareRevision()
    {
        var (s, record) = await AccusedAsync("c10-document-share");
        await SetContributorWritesAsync(true);
        try
        {
            var v1 = StageBScenario.SyntheticBytes(2048, 11);
            var attachment = (await s.UploadAsync(s.Clerk, record, "c10-doc.txt", "text/plain", v1)).GetProperty("fileAttachmentId").GetGuid();
            var share = await ShareAsync(s, s.Clerk, record, s.Reader, "Contributor", [], [Document(attachment, replace: true)]);
            await s.WaitProjectionAsync(s.Reader, record, Settle);
            var first = await fixture.DownloadAsync(DownloadPath(record, attachment), s.Token(s.Reader));
            Assert.Equal((HttpStatusCode.OK, StageBScenario.Sha256Hex(v1)), (first.Status, StageBScenario.Sha256Hex(first.Body)));
            var revision = await ShareRevisionAsync(s, record, share);

            // Business upload admission: another field's policy or an undeclared attachment is refused before any policy check.
            var otherField = await fixture.Api.PostAsync("/api/v1/upload-sessions", s.Token(s.Reader), new { formRecordId = record, fieldDefinitionId = s.AccusedFields["given_name"], originalFileName = "x.txt", contentType = "text/plain", sizeBytes = 16L, replacesFileAttachmentId = attachment });
            Assert.False(otherField.IsSuccess, otherField.Body);
            var unshared = await fixture.Api.PostAsync("/api/v1/upload-sessions", s.Token(s.Reader), new { formRecordId = record, fieldDefinitionId = s.AccusedFields["case_documents"], originalFileName = "x.txt", contentType = "text/plain", sizeBytes = 16L, replacesFileAttachmentId = Guid.CreateVersion7() });
            Assert.False(unshared.IsSuccess, unshared.Body);

            // The recipient's replacement: exactly the next version, the boundary and the share revision advance (D-5), journalled once.
            var v2 = StageBScenario.SyntheticBytes(2048, 12);
            await s.UploadAsync(s.Reader, record, "c10-doc-v2.txt", "text/plain", v2, replacesFileAttachmentId: attachment);
            var details = await GetShareAsync(s, s.Clerk, record, share);
            Assert.True(details.IsSuccess, details.Body);
            Assert.Equal(revision + 1, details.Root.GetProperty("shareRevision").GetInt64());
            Assert.Equal(2, details.Root.GetProperty("documents").EnumerateArray().Single().GetProperty("sharedThroughVersionNumber").GetInt32());
            Assert.Equal(1, JournalEvents("ShareDocumentBoundaryExtended", record));
            var stale = await UpdateShareAsync(s, s.Clerk, record, share, revision, "Contributor", [], documents: [Document(attachment, replace: true)]);
            Assert.Equal(HttpStatusCode.PreconditionFailed, stale.Status);
            var second = await fixture.DownloadAsync(DownloadPath(record, attachment), s.Token(s.Reader));
            Assert.Equal(StageBScenario.Sha256Hex(v2), StageBScenario.Sha256Hex(second.Body));

            // The owner's next version lies outside the boundary: neither visible nor replaceable through the share.
            await s.UploadAsync(s.Clerk, record, "c10-doc-v3.txt", "text/plain", StageBScenario.SyntheticBytes(2048, 13), replacesFileAttachmentId: attachment);
            Assert.Equal(HttpStatusCode.NotFound, (await fixture.DownloadAsync(DownloadPath(record, attachment), s.Token(s.Reader))).Status);
            var outside = await s.TransferAsync(s.Reader, record, "c10-doc-v4.txt", "text/plain", StageBScenario.SyntheticBytes(512, 14), replacesFileAttachmentId: attachment);
            if (outside.GetProperty("sessionState").GetString() == "Processed")
            {
                var commit = await fixture.Api.PostAsync($"/api/v1/upload-sessions/{outside.GetProperty("uploadSessionId").GetGuid():D}/commit", s.Token(s.Reader), new { });
                Assert.False(commit.IsSuccess, commit.Body);
            }

            Assert.Equal(1, JournalEvents("ShareDocumentBoundaryExtended", record));

            // Live re-check: a revoked recipient's next download is refused.
            await RevokeAsync(s, s.Clerk, record, share);
            Assert.Equal(HttpStatusCode.NotFound, (await fixture.DownloadAsync(DownloadPath(record, attachment), s.Token(s.Reader))).Status);
        }
        finally
        {
            await SetContributorWritesAsync(null);
        }
    }

    [Fact]
    public async Task AReplacementAfterAnAbandonedUploadIsNumberedFromTheAcceptedVersion()
    {
        // Owner decision 2: the abandoned processed copy shares the number with the accepted one (partial unique index on real
        // PostgreSQL); the history shows the accepted copy as current; no number is skipped.
        var (s, record) = await AccusedAsync("c10-version-numbers");
        var attachment = (await s.UploadAsync(s.Clerk, record, "c10-numbers.txt", "text/plain", StageBScenario.SyntheticBytes(1024, 21))).GetProperty("fileAttachmentId").GetGuid();
        var abandoned = await s.TransferAsync(s.Clerk, record, "c10-numbers-abandoned.txt", "text/plain", StageBScenario.SyntheticBytes(1024, 22), replacesFileAttachmentId: attachment);
        Assert.Equal("Processed", abandoned.GetProperty("sessionState").GetString()); // processed as version 2, never committed
        var accepted = StageBScenario.SyntheticBytes(1024, 23);
        await s.UploadAsync(s.Clerk, record, "c10-numbers-v2.txt", "text/plain", accepted, replacesFileAttachmentId: attachment);

        var detail = await fixture.Api.GetAsync($"/api/v1/form-records/{record:D}/documents/{attachment:D}", s.Token(s.Clerk));
        Assert.True(detail.IsSuccess, detail.Body);
        var current = detail.Root.GetProperty("versions").EnumerateArray().Single(v => v.GetProperty("versionState").GetString() == "Current");
        Assert.Equal((2, "c10-numbers-v2.txt"), (current.GetProperty("versionNumber").GetInt32(), current.GetProperty("originalFileName").GetString()));
        Assert.Equal("2", fixture.Stack.AttackerSql($"SELECT count(*) FROM file_attachment_versions WHERE file_attachment_id = '{attachment:D}' AND version_number = 2 AND content_change_operation_id = '00000000-0000-0000-0000-000000000000';").Trim());

        // The abandoned upload is now stale: its commit is refused before anything is staged.
        var stale = await fixture.Api.PostAsync($"/api/v1/upload-sessions/{abandoned.GetProperty("uploadSessionId").GetGuid():D}/commit", s.Token(s.Clerk), new { });
        Assert.Equal((HttpStatusCode.Conflict, "DOCUMENT_VERSION_SUPERSEDED"), (stale.Status, stale.ErrorCode));
    }
}
