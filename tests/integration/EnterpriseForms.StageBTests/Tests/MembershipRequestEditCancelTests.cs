using System.Net;
using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// C8 M2 (AC-7.5) on the real stack: an owner cancels or edits (replaces) a pending membership request. The original is preserved,
/// the replacement is a new request with a fresh seven-day expiry and lineage, the identity is never supplied by the caller, no
/// approval carries over, a cancelled or replaced request can never be approved, stale and concurrent changes lose cleanly,
/// authorization and organization isolation are enforced by the Authority, and every command is journaled.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class MembershipRequestEditCancelTests(StageBFixture fixture)
{
    private const string Approver1 = "test-synthetic-approver-1";
    private const string Approver2 = "test-synthetic-approver-2";
    private const string Justification = "Owner request verified with the unit lead";

    private Task<HttpResult> PostAsync(StageBScenario s, TestUser actor, string path, object body, string? key = null) => fixture.Api.PostAsync("/api/v1/organization/" + path, s.Token(actor), body, key);

    private async Task<(Guid Id, string Digest)> RequestAsync(StageBScenario s, TestUser actor, string subject, string? role = "Designer", string reason = "joins the intake team")
    {
        var created = await PostAsync(s, actor, "membership-requests", new { subjectIdentifier = subject, displayNameHint = "Nia Newcomer", initialRoleName = role, reason, useDevelopmentStepUp = true });
        Assert.True(created.IsSuccess, created.Body);
        var id = created.Root.GetProperty("requestId").GetGuid();
        return (id, (await EntryAsync(s, actor, id)).GetProperty("requestDigest").GetString()!);
    }

    private async Task<JsonElement> EntryAsync(StageBScenario s, TestUser viewer, Guid requestId, string query = "")
    {
        var page = await fixture.Api.GetAsync("/api/v1/organization/membership-requests?pageSize=50" + query, s.Token(viewer));
        Assert.True(page.IsSuccess, page.Body);
        return page.Root.GetProperty("items").EnumerateArray().Single(r => r.GetProperty("requestId").GetGuid() == requestId).Clone();
    }

    private Task<HttpResult> CancelAsync(StageBScenario s, TestUser actor, Guid id, string digest, string? key = null, bool stepUp = true) =>
        PostAsync(s, actor, $"membership-requests/{id:D}/cancel", new { expectedRequestDigest = digest, reason = "entered for the wrong unit", useDevelopmentStepUp = stepUp }, key);

    private Task<HttpResult> ReplaceAsync(StageBScenario s, TestUser actor, Guid id, string digest, string name = "Nia Newcomer-Okafor", string? role = null, string reason = "corrected after the intake call", string? key = null, bool stepUp = true) =>
        PostAsync(s, actor, $"membership-requests/{id:D}/replace", new { expectedRequestDigest = digest, displayNameHint = name, initialRoleName = role, reason, useDevelopmentStepUp = stepUp }, key);

    private static void Refused(HttpResult result, string code)
    {
        Assert.False(result.IsSuccess, result.Body);
        Assert.Equal(code, result.ErrorCode);
    }

    private static string Path(StageBScenario s, Guid id, string action = "approve") => $"/internal/v1/operator/organizations/{s.OrganizationId:D}/membership-requests/{id:D}/{action}";

    private Task<HttpResult> PreviewAsync(StageBScenario s, Guid id) => fixture.Authority.OperatorAsync(HttpMethod.Post, Path(s, id), new { confirm_organization_id = s.OrganizationId, justification = Justification, dry_run = true });

    private Task<HttpResult> ApproveAsync(StageBScenario s, Guid id, HttpResult preview) =>
        fixture.Authority.OperatorAsync(HttpMethod.Post, Path(s, id), new { confirm_organization_id = s.OrganizationId, justification = Justification, dry_run = false, approvals = new[] { TestIdentity.OperatorApproval(preview.Root, Approver1), TestIdentity.OperatorApproval(preview.Root, Approver2) } });

    private string Journaled(StageBScenario s, string commandKind) =>
        fixture.Stack.AuthorityObserverSql($"SELECT count(*) FROM security_commands WHERE organization_id = '{s.OrganizationId:D}' AND command_kind_name = '{commandKind}' AND command_state_name <> 'Rejected';").Trim();

    private string BusinessAudit(StageBScenario s) =>
        fixture.Stack.AttackerSql($"SELECT string_agg(event_action, ',' ORDER BY audit_sequence) FROM audit_events WHERE tenant_id = '{s.OrganizationId:D}' AND event_action IN ('MEMBERSHIP_REQUEST_CANCELLED','MEMBERSHIP_REQUEST_REPLACED');").Trim();

    [Fact]
    public async Task EditingReplacesTheRequestWithLineageAFreshExpiryNoCarriedApprovalAndTheIdentityUntouched()
    {
        var s = new StageBScenario(fixture, "ac75-replace");
        await s.BootstrapAsync(withAdditionalOwners: true);
        var subject = "ac75-person-" + Guid.NewGuid().ToString("N")[..8];
        var (originalId, digest) = await RequestAsync(s, s.Designer, subject);
        var original = await EntryAsync(s, s.Designer, originalId);
        Assert.True(original.GetProperty("canChange").GetBoolean());
        var oldPreview = await PreviewAsync(s, originalId); // an operator already looked at the original
        Assert.True(oldPreview.Root.GetProperty("eligible").GetBoolean());

        // The caller cannot change the identity: the request body has no identity field, and unknown properties change nothing.
        var forged = await PostAsync(s, s.SecondOwner, $"membership-requests/{originalId:D}/replace", new { expectedRequestDigest = digest, displayNameHint = "Nia Newcomer-Okafor", initialRoleName = (string?)null, reason = "corrected after the intake call", useDevelopmentStepUp = true, subjectIdentifier = "someone-else", identityIssuer = "https://evil.example" });
        Assert.True(forged.IsSuccess, forged.Body);
        var replacementId = forged.Root.GetProperty("requestId").GetGuid();
        Assert.NotEqual(originalId, replacementId);
        var replacement = await EntryAsync(s, s.Designer, replacementId);
        Assert.Equal(subject, replacement.GetProperty("subjectIdentifier").GetString());
        Assert.Equal(original.GetProperty("issuerUri").GetString(), replacement.GetProperty("issuerUri").GetString());
        Assert.Equal("Nia Newcomer-Okafor", replacement.GetProperty("displayNameHint").GetString());
        Assert.Equal(JsonValueKind.Null, replacement.GetProperty("initialRoleName").ValueKind);
        Assert.Equal("Pending", replacement.GetProperty("requestState").GetString());
        Assert.Equal(originalId, replacement.GetProperty("replacesRequestId").GetGuid());
        Assert.True(replacement.GetProperty("expiresOn").GetDateTimeOffset() >= original.GetProperty("expiresOn").GetDateTimeOffset());
        Assert.Equal(forged.Root.GetProperty("commandId").GetGuid(), replacementId); // the replacement id is the command id

        // The original is preserved and shows what replaced it; nothing was granted.
        var preserved = await EntryAsync(s, s.Designer, originalId);
        Assert.Equal("Replaced", preserved.GetProperty("requestState").GetString());
        Assert.Equal(replacementId, preserved.GetProperty("replacedByRequestId").GetGuid());
        Assert.Equal("Nia Newcomer", preserved.GetProperty("displayNameHint").GetString());
        Assert.Equal("Designer", preserved.GetProperty("initialRoleName").GetString());
        Assert.False(preserved.GetProperty("canChange").GetBoolean());
        var members = await fixture.Api.GetAsync("/api/v1/organization/members?pageSize=50", s.Token(s.Designer));
        Assert.DoesNotContain(members.Root.GetProperty("members").EnumerateArray(), m => m.GetProperty("identitySubject").GetString() == subject);

        // The original can never be approved (not by a fresh preview, not by approvals already signed for it); only the replacement, with two approvals.
        var stalePreview = await PreviewAsync(s, originalId);
        Assert.False(stalePreview.Root.GetProperty("eligible").GetBoolean());
        Assert.Equal("MEMBERSHIP_REQUEST_NOT_PENDING", stalePreview.Root.GetProperty("refusal_code").GetString());
        var lateApproval = await ApproveAsync(s, originalId, oldPreview);
        Assert.False(lateApproval.IsSuccess, lateApproval.Body);
        Assert.Equal("MEMBERSHIP_REQUEST_NOT_PENDING", lateApproval.ErrorCode);
        var newPreview = await PreviewAsync(s, replacementId);
        Assert.True(newPreview.Root.GetProperty("eligible").GetBoolean());
        var single = await fixture.Authority.OperatorAsync(HttpMethod.Post, Path(s, replacementId), new { confirm_organization_id = s.OrganizationId, justification = Justification, dry_run = false, approvals = new[] { TestIdentity.OperatorApproval(newPreview.Root, Approver1) } });
        Assert.False(single.IsSuccess); // two-operator approval is unchanged
        Assert.True((await ApproveAsync(s, replacementId, newPreview)).IsSuccess);
        await s.WaitGateReadyAsync();
        var member = (await fixture.Api.GetAsync("/api/v1/organization/members?pageSize=50", s.Token(s.Designer))).Root.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("identitySubject").GetString() == subject);
        Assert.Equal("Nia Newcomer-Okafor", member.GetProperty("displayName").GetString());
        Assert.Empty(member.GetProperty("authorityRoles").EnumerateArray()); // the edited initial role (none) applies
        Assert.Equal("Replaced", (await EntryAsync(s, s.Designer, originalId)).GetProperty("requestState").GetString());
        Assert.Equal("Approved", (await EntryAsync(s, s.Designer, replacementId)).GetProperty("requestState").GetString());

        // Evidence: the command is journaled once, the Authority journal and the business timeline record it, an approved membership cannot be changed.
        Assert.Equal("1", Journaled(s, "ReplaceOrganizationMembershipRequest"));
        Assert.Equal("MEMBERSHIP_REQUEST_REPLACED", BusinessAudit(s));
        var effective = await ReplaceAsync(s, s.Designer, replacementId, (await EntryAsync(s, s.Designer, replacementId)).GetProperty("requestDigest").GetString()!, "Changed After Approval");
        Refused(effective, "MEMBERSHIP_REQUEST_NOT_PENDING");
    }

    [Fact]
    public async Task CancellingGrantsNothingIsAuditedAndMakesApprovalImpossibleThenANewRequestIsAllowed()
    {
        var s = new StageBScenario(fixture, "ac75-cancel");
        await s.BootstrapAsync(withAdditionalOwners: true);
        var subject = "ac75-cancel-" + Guid.NewGuid().ToString("N")[..8];
        var (id, digest) = await RequestAsync(s, s.Designer, subject);
        var preview = await PreviewAsync(s, id);
        Assert.True(preview.Root.GetProperty("eligible").GetBoolean());

        var cancelled = await CancelAsync(s, s.SecondOwner, id, digest, key: "ac75-cancel-key-0001");
        Assert.True(cancelled.IsSuccess, cancelled.Body);
        var retry = await CancelAsync(s, s.SecondOwner, id, digest, key: "ac75-cancel-key-0001"); // the same key is the same command
        Assert.True(retry.IsSuccess, retry.Body);
        Assert.Equal(cancelled.Root.GetProperty("commandId").GetGuid(), retry.Root.GetProperty("commandId").GetGuid());
        var entry = await EntryAsync(s, s.Designer, id);
        Assert.Equal("Cancelled", entry.GetProperty("requestState").GetString());
        Assert.False(entry.GetProperty("canChange").GetBoolean());
        Assert.Equal("1", Journaled(s, "CancelOrganizationMembershipRequest"));
        Assert.Equal("MEMBERSHIP_REQUEST_CANCELLED", BusinessAudit(s)); // one business event despite the retry
        Assert.DoesNotContain((await fixture.Api.GetAsync("/api/v1/organization/members?pageSize=50", s.Token(s.Designer))).Root.GetProperty("members").EnumerateArray(), m => m.GetProperty("identitySubject").GetString() == subject);

        // A cancelled request can never be approved or rejected, even with approvals that were valid before.
        var late = await ApproveAsync(s, id, preview);
        Assert.Equal("MEMBERSHIP_REQUEST_NOT_PENDING", late.ErrorCode);
        var reject = await fixture.Authority.OperatorAsync(HttpMethod.Post, Path(s, id, "reject"), new { confirm_organization_id = s.OrganizationId, reason = "not part of the unit" });
        Assert.False(reject.IsSuccess);
        Refused(await CancelAsync(s, s.Designer, id, digest), "MEMBERSHIP_REQUEST_CHANGED"); // a stale reading of a request that is now Cancelled

        // A new request for the identity follows (existing rules apply).
        var (newId, _) = await RequestAsync(s, s.Designer, subject);
        Assert.NotEqual(id, newId);
        Assert.Equal("Pending", (await EntryAsync(s, s.Designer, newId)).GetProperty("requestState").GetString());
    }

    [Fact]
    public async Task OnlyOwnersChangeRequestsAnotherOrganizationSeesNothingAndStepUpIsRequired()
    {
        var s = new StageBScenario(fixture, "ac75-authz");
        await s.BootstrapAsync(withAdditionalOwners: true);
        var other = new StageBScenario(fixture, "ac75-authz-other");
        await other.BootstrapAsync();
        var (id, digest) = await RequestAsync(s, s.Designer, "ac75-authz-" + Guid.NewGuid().ToString("N")[..8]);

        foreach (var actor in new[] { s.Clerk, s.Reader, s.Administrator, s.DesignerOnly })
        {
            Refused(await CancelAsync(s, actor, id, digest), "FORBIDDEN");
            Refused(await ReplaceAsync(s, actor, id, digest), "FORBIDDEN");
        }

        Refused(await CancelAsync(s, s.Designer, id, digest, stepUp: false), "STEP_UP_REQUIRED");
        Refused(await ReplaceAsync(s, s.Designer, id, digest, stepUp: false), "STEP_UP_REQUIRED");
        // Another organization's owner cannot see, cancel or replace this organization's request.
        var crossCancel = await fixture.Api.PostAsync($"/api/v1/organization/membership-requests/{id:D}/cancel", other.Token(other.Designer), new { expectedRequestDigest = digest, reason = "cross tenant", useDevelopmentStepUp = true });
        var crossReplace = await fixture.Api.PostAsync($"/api/v1/organization/membership-requests/{id:D}/replace", other.Token(other.Designer), new { expectedRequestDigest = digest, displayNameHint = "Hijack", initialRoleName = (string?)null, reason = "cross tenant", useDevelopmentStepUp = true });
        Assert.False(crossCancel.IsSuccess, crossCancel.Body);
        Assert.False(crossReplace.IsSuccess, crossReplace.Body);
        Assert.Equal(HttpStatusCode.NotFound, crossCancel.Status);
        Assert.Equal(HttpStatusCode.NotFound, crossReplace.Status);
        Assert.Equal("Pending", (await EntryAsync(s, s.Designer, id)).GetProperty("requestState").GetString());
        Assert.Equal("0", Journaled(s, "CancelOrganizationMembershipRequest"));
        Assert.Equal("0", Journaled(s, "ReplaceOrganizationMembershipRequest"));
        Assert.NotEqual("0", fixture.Stack.AuthorityObserverSql($"SELECT count(*) FROM security_journal_entries WHERE organization_id = '{s.OrganizationId:D}' AND journal_stream_name = 'audit' AND journal_event_kind = 'MembershipCommandRejected';").Trim()); // refusals are journaled
    }

    [Fact]
    public async Task StaleAndConcurrentChangesLoseCleanlyAndValidationHolds()
    {
        var s = new StageBScenario(fixture, "ac75-concurrent");
        await s.BootstrapAsync(withAdditionalOwners: true);
        var (id, digest) = await RequestAsync(s, s.Designer, "ac75-race-" + Guid.NewGuid().ToString("N")[..8]);

        // Validation: nothing changed, bad names and roles, owner role never accepted.
        Refused(await ReplaceAsync(s, s.Designer, id, digest, "Nia Newcomer", "Designer", "joins the intake team"), "INVALID_COMMAND");
        Refused(await ReplaceAsync(s, s.Designer, id, digest, " "), "INVALID_COMMAND");
        Refused(await ReplaceAsync(s, s.Designer, id, digest, role: "OrganizationOwner"), "INVALID_REQUEST");
        Refused(await ReplaceAsync(s, s.Designer, id, digest, reason: " "), "INVALID_REQUEST");
        Refused(await CancelAsync(s, s.Designer, id, ""), "INVALID_COMMAND");

        // Two owners act on the same reading at once: exactly one wins, the other is told the request changed.
        var results = await Task.WhenAll(ReplaceAsync(s, s.Designer, id, digest, "Winner One"), ReplaceAsync(s, s.SecondOwner, id, digest, "Winner Two"), CancelAsync(s, s.ThirdOwner, id, digest));
        Assert.Equal(1, results.Count(r => r.IsSuccess));
        // The loser is stopped either by the request digest (MEMBERSHIP_REQUEST_CHANGED) or, when the commands truly overlap, by the Authority's global revision check; both are 409s that change nothing.
        Assert.All(results.Where(r => !r.IsSuccess), r => Assert.True(r.Status == HttpStatusCode.Conflict && r.ErrorCode is "MEMBERSHIP_REQUEST_CHANGED" or "STALE_AUTHORIZATION_REVISION", r.Body));
        var page = await fixture.Api.GetAsync("/api/v1/organization/membership-requests?pageSize=50", s.Token(s.Designer));
        var items = page.Root.GetProperty("items").EnumerateArray().ToList();
        var pending = items.Count(i => i.GetProperty("requestState").GetString() == "Pending");
        var replaced = items.Count(i => i.GetProperty("requestState").GetString() == "Replaced");
        var cancelled = items.Count(i => i.GetProperty("requestState").GetString() == "Cancelled");
        Assert.True((pending, replaced, cancelled) is (1, 1, 0) or (0, 0, 1), $"pending {pending}, replaced {replaced}, cancelled {cancelled}"); // exactly one change happened, never two pending requests for one identity
    }

    [Fact]
    public async Task ApprovalListsPageFiveAtATimeAndShowCancelledAndReplacedStatuses()
    {
        var s = new StageBScenario(fixture, "ac75-paging");
        await s.BootstrapAsync(withAdditionalOwners: true);
        var run = Guid.NewGuid().ToString("N")[..6];
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var (id, digest) = await RequestAsync(s, s.Designer, $"ac75-page-{run}-{i}");
            ids.Add(id);
            if (i == 1)
            {
                Assert.True((await CancelAsync(s, s.Designer, id, digest)).IsSuccess);
            }

            if (i == 2)
            {
                Assert.True((await ReplaceAsync(s, s.Designer, id, digest)).IsSuccess); // adds a sixth request
            }
        }

        var first = await fixture.Api.GetAsync("/api/v1/organization/membership-requests", s.Token(s.Designer));
        Assert.Equal(5, first.Root.GetProperty("items").GetArrayLength());
        Assert.Equal(6, first.Root.GetProperty("totalCount").GetInt32());
        var second = await fixture.Api.GetAsync("/api/v1/organization/membership-requests?continuationToken=" + Uri.EscapeDataString(first.Root.GetProperty("nextToken").GetString()!), s.Token(s.Designer));
        Assert.Equal(1, second.Root.GetProperty("items").GetArrayLength());
        foreach (var (status, expected) in new[] { ("Cancelled", 1), ("Replaced", 1), ("Pending", 4), ("Approved", 0) })
        {
            var filtered = await fixture.Api.GetAsync("/api/v1/organization/membership-requests?status=" + status, s.Token(s.Designer));
            Assert.Equal(expected, filtered.Root.GetProperty("totalCount").GetInt32());
        }
    }
}
