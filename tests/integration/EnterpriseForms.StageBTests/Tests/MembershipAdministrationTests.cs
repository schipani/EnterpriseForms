using System.Net;
using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// C8 M2 (AC-7) on the real stack (business API, Authority, both PostgreSQL databases, SpiceDB, Vault): owners only, no
/// self-change, step-up, idempotent retries; designer grants take effect in the Authority's decisions; disabling refuses the
/// person's next request, keeps roles, is refused while they own records and is reversible; owner changes need two distinct
/// owners, proposals are single use, concurrent approvals produce one change; membership requests grant nothing until two
/// operator approvals, which create the binding exactly once; refusals are journaled by the Authority; another organization
/// sees and changes nothing.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class MembershipAdministrationTests(StageBFixture fixture)
{
    private const string Approver1 = "test-synthetic-approver-1";
    private const string Approver2 = "test-synthetic-approver-2";

    private Task<HttpResult> PostAsync(StageBScenario s, TestUser actor, string path, object body, string? key = null) => fixture.Api.PostAsync("/api/v1/organization/" + path, s.Token(actor), body, key);

    private static object Command(string reason = "governance ticket 12", bool stepUp = true) => new { reason, useDevelopmentStepUp = stepUp };

    private Task<HttpResult> GrantDesignerAsync(StageBScenario s, TestUser actor, TestUser target, bool stepUp = true, string? key = null) =>
        PostAsync(s, actor, $"members/{target.AuthorityUserId:D}/roles", new { roleName = "Designer", reason = "needs to design intake forms", useDevelopmentStepUp = stepUp }, key);

    private async Task<JsonElement> DirectoryAsync(StageBScenario s, TestUser viewer, string query = "")
    {
        var result = await fixture.Api.GetAsync("/api/v1/organization/members?pageSize=50" + query, s.Token(viewer));
        Assert.True(result.IsSuccess, result.Body);
        return result.Root.Clone();
    }

    private async Task<JsonElement> MemberAsync(StageBScenario s, Guid userId) =>
        (await DirectoryAsync(s, s.Designer)).GetProperty("members").EnumerateArray().Single(m => m.GetProperty("userId").GetGuid() == userId);

    private static List<string?> Roles(JsonElement member) => member.GetProperty("authorityRoles").EnumerateArray().Select(r => r.GetString()).ToList();

    private static void Refused(HttpResult result, string code)
    {
        Assert.False(result.IsSuccess, result.Body);
        Assert.Equal(code, result.ErrorCode);
    }

    private string AuditJournal(StageBScenario s, string eventKind) =>
        fixture.Stack.AuthorityObserverSql($"SELECT count(*) FROM security_journal_entries WHERE organization_id = '{s.OrganizationId:D}' AND journal_stream_name = 'audit' AND journal_event_kind = '{eventKind}';").Trim();

    private Task<HttpResult> CreateFormAsync(StageBScenario s, TestUser actor) =>
        fixture.Api.PostAsync("/api/v1/form-definitions/designer", s.Token(actor), new { formDefinitionName = "AC-7 probe " + Guid.NewGuid().ToString("N")[..6], formDefinitionDescription = "", formUserUserIds = Array.Empty<Guid>(), formUserTeamIds = Array.Empty<Guid>() });

    [Fact]
    public async Task OwnersAdministerDesignersAndMembershipWithinTheAuthorityRules()
    {
        var s = new StageBScenario(fixture, "ac7-roles");
        await s.BootstrapAsync(withAdditionalOwners: true);
        var other = new StageBScenario(fixture, "ac7-roles-other");
        await other.BootstrapAsync();

        // Only owners; never oneself; step-up required; another organization's owner cannot touch these people.
        Refused(await GrantDesignerAsync(s, s.Clerk, s.Reader), "FORBIDDEN");
        Refused(await GrantDesignerAsync(s, s.Administrator, s.Reader), "FORBIDDEN"); // a Security Administrator is not an owner (G25)
        Refused(await GrantDesignerAsync(s, s.DesignerOnly, s.Reader), "FORBIDDEN");
        Refused(await PostAsync(s, s.SecondOwner, $"members/{s.SecondOwner.AuthorityUserId:D}/disable", Command()), "SELF_CHANGE_FORBIDDEN");
        Refused(await GrantDesignerAsync(s, s.Designer, s.Reader, stepUp: false), "STEP_UP_REQUIRED");
        Assert.False((await fixture.Api.PostAsync($"/api/v1/organization/members/{s.Reader.AuthorityUserId:D}/roles", other.Token(other.Designer), new { roleName = "Designer", reason = "cross tenant", useDevelopmentStepUp = true })).IsSuccess);
        Assert.NotEqual("0", AuditJournal(s, "MembershipCommandRejected"));
        Assert.DoesNotContain((await DirectoryAsync(other, other.Designer)).GetProperty("members").EnumerateArray(), m => m.GetProperty("userId").GetGuid() == s.Reader.AuthorityUserId);

        // Designer grant takes effect in the Authority's decisions; the retry with the same key is the same command.
        Refused(await CreateFormAsync(s, s.Clerk), "FORBIDDEN");
        var granted = await GrantDesignerAsync(s, s.Designer, s.Clerk, key: "ac7-grant-clerk-0001");
        Assert.True(granted.IsSuccess, granted.Body);
        Assert.Equal("Effective", granted.Root.GetProperty("commandStatus").GetString());
        var retried = await GrantDesignerAsync(s, s.Designer, s.Clerk, key: "ac7-grant-clerk-0001");
        Assert.Equal(granted.Root.GetProperty("commandId").GetGuid(), retried.Root.GetProperty("commandId").GetGuid());
        Assert.Contains("Designer", Roles(await MemberAsync(s, s.Clerk.AuthorityUserId)));
        Assert.Equal(HttpStatusCode.Created, (await CreateFormAsync(s, s.Clerk)).Status);
        var revoked = await PostAsync(s, s.Designer, $"members/{s.Clerk.AuthorityUserId:D}/roles/Designer/remove", Command());
        Assert.True(revoked.IsSuccess, revoked.Body);
        Refused(await CreateFormAsync(s, s.Clerk), "FORBIDDEN");

        // Disabling refuses the person's next request and keeps their roles; re-enabling restores them.
        Assert.True((await GrantDesignerAsync(s, s.Designer, s.Reader)).IsSuccess);
        var disabled = await PostAsync(s, s.Designer, $"members/{s.Reader.AuthorityUserId:D}/disable", Command("left the unit"));
        Assert.True(disabled.IsSuccess, disabled.Body);
        Assert.False((await fixture.Api.GetAsync("/api/v1/dashboard", s.Token(s.Reader))).IsSuccess);
        var readerRow = await MemberAsync(s, s.Reader.AuthorityUserId);
        Assert.Equal("Disabled", readerRow.GetProperty("membershipState").GetString());
        Assert.Contains("Designer", Roles(readerRow));
        Refused(await GrantDesignerAsync(s, s.SecondOwner, s.Reader), "MEMBERSHIP_STATE_CONFLICT"); // a disabled member receives nothing
        Refused(await PostAsync(s, s.Designer, $"members/{s.Reader.AuthorityUserId:D}/disable", Command()), "MEMBERSHIP_STATE_CONFLICT");
        Assert.True((await PostAsync(s, s.SecondOwner, $"members/{s.Reader.AuthorityUserId:D}/enable", Command("returned"))).IsSuccess);
        Assert.True((await fixture.Api.GetAsync("/api/v1/dashboard", s.Token(s.Reader))).IsSuccess);
        Assert.Equal("Enabled", (await MemberAsync(s, s.Reader.AuthorityUserId)).GetProperty("membershipState").GetString());

        // Someone who directly owns records cannot be disabled until ownership is transferred (count only).
        await s.BuildAccusedWorkspaceAsync();
        await s.CreateAccusedAsync(s.Clerk);
        var owns = await PostAsync(s, s.Designer, $"members/{s.Clerk.AuthorityUserId:D}/disable", Command());
        Refused(owns, "OWNS_RECORDS");
        Assert.Contains("1 active record", owns.Body, StringComparison.Ordinal);

        // The last enabled Security Administrator cannot be disabled.
        Refused(await PostAsync(s, s.Designer, $"members/{s.Administrator.AuthorityUserId:D}/disable", Command()), "LAST_SECURITY_ADMINISTRATOR");

        var businessAudit = fixture.Stack.AttackerSql($"SELECT string_agg(event_action, ',' ORDER BY audit_sequence) FROM audit_events WHERE tenant_id = '{s.OrganizationId:D}' AND event_action IN ('ORGANIZATION_ROLE_GRANTED','ORGANIZATION_ROLE_REVOKED','MEMBERSHIP_DISABLED','MEMBERSHIP_ENABLED');").Trim();
        Assert.Equal("ORGANIZATION_ROLE_GRANTED,ORGANIZATION_ROLE_REVOKED,ORGANIZATION_ROLE_GRANTED,MEMBERSHIP_DISABLED,MEMBERSHIP_ENABLED", businessAudit);
    }

    [Fact]
    public async Task OwnerChangesNeedTwoDistinctOwnersAndAProposalIsUsedOnce()
    {
        var s = new StageBScenario(fixture, "ac7-owners");
        await s.BootstrapAsync(withAdditionalOwners: true);

        // Granting an owner: A proposes; neither A nor the person can approve; B approves; a second approval is refused.
        var proposal = await PostAsync(s, s.Designer, $"members/{s.Clerk.AuthorityUserId:D}/owner-proposals", new { change = "GrantOwner", reason = "board decision", useDevelopmentStepUp = true });
        Assert.True(proposal.IsSuccess, proposal.Body);
        var proposalId = proposal.Root.GetProperty("proposalId").GetGuid();
        Assert.DoesNotContain("OrganizationOwner", Roles(await MemberAsync(s, s.Clerk.AuthorityUserId))); // a proposal grants nothing
        Refused(await PostAsync(s, s.Designer, $"owner-proposals/{proposalId:D}/approve", Command()), "OWNER_APPROVAL_REQUIRED");
        Assert.False((await PostAsync(s, s.Clerk, $"owner-proposals/{proposalId:D}/approve", Command())).IsSuccess);
        Refused(await PostAsync(s, s.Designer, $"members/{s.Clerk.AuthorityUserId:D}/owner-proposals", new { change = "GrantOwner", reason = "again", useDevelopmentStepUp = true }), "PROPOSAL_EXISTS");
        var approved = await PostAsync(s, s.SecondOwner, $"owner-proposals/{proposalId:D}/approve", Command("second owner agrees"));
        Assert.True(approved.IsSuccess, approved.Body);
        Assert.Contains("OrganizationOwner", Roles(await MemberAsync(s, s.Clerk.AuthorityUserId)));
        var again = await PostAsync(s, s.ThirdOwner, $"owner-proposals/{proposalId:D}/approve", Command());
        Assert.False(again.IsSuccess, again.Body); // the person is already an owner and the proposal is consumed: refused either way
        Assert.Contains(again.ErrorCode, new[] { "ROLE_ALREADY_HELD", "PROPOSAL_NOT_PENDING" });

        // Disabling an owner needs a proposal too; two owners approving at the same time produce exactly one change.
        Refused(await PostAsync(s, s.Designer, $"members/{s.ThirdOwner.AuthorityUserId:D}/disable", Command()), "OWNER_APPROVAL_REQUIRED");
        var disable = await PostAsync(s, s.Designer, $"members/{s.ThirdOwner.AuthorityUserId:D}/owner-proposals", new { change = "DisableOwner", reason = "left", useDevelopmentStepUp = true });
        var disableId = disable.Root.GetProperty("proposalId").GetGuid();
        var racing = await Task.WhenAll(
            PostAsync(s, s.SecondOwner, $"owner-proposals/{disableId:D}/approve", Command("race one")),
            PostAsync(s, s.Clerk, $"owner-proposals/{disableId:D}/approve", Command("race two")));
        Assert.Single(racing, r => r.IsSuccess);
        Assert.Contains(racing, r => r.ErrorCode is "PROPOSAL_NOT_PENDING" or "MEMBERSHIP_STATE_CONFLICT" or "STALE_AUTHORIZATION_REVISION" or "AUTHORITY_NOT_READY");
        Assert.Equal("Disabled", (await MemberAsync(s, s.ThirdOwner.AuthorityUserId)).GetProperty("membershipState").GetString());
        Assert.False((await fixture.Api.GetAsync("/api/v1/organization/members", s.Token(s.ThirdOwner))).IsSuccess);

        var proposals = (await DirectoryAsync(s, s.Designer)).GetProperty("ownerProposals").EnumerateArray().ToList();
        Assert.All(proposals, p => Assert.Equal("Consumed", p.GetProperty("proposalState").GetString()));
    }

    [Fact]
    public async Task MembershipRequestsGrantNothingUntilTwoOperatorApprovals()
    {
        var s = new StageBScenario(fixture, "ac7-requests");
        await s.BootstrapAsync();
        var subject = "ac7-newcomer-" + Guid.NewGuid().ToString("N")[..8];
        object Request(string subjectId, string? role = "Designer") => new { subjectIdentifier = subjectId, displayNameHint = "Nia Newcomer", initialRoleName = role, reason = "joins the intake team", useDevelopmentStepUp = true };

        Refused(await PostAsync(s, s.Clerk, "membership-requests", Request(subject)), "FORBIDDEN");
        Refused(await PostAsync(s, s.Designer, "membership-requests", Request("nia@contoso.example")), "IDENTITY_NOT_ALLOWED");
        Refused(await PostAsync(s, s.Designer, "membership-requests", Request(s.Clerk.Subject)), "IDENTITY_ALREADY_BOUND");
        var requested = await PostAsync(s, s.Designer, "membership-requests", Request(subject));
        Assert.True(requested.IsSuccess, requested.Body);
        var requestId = requested.Root.GetProperty("requestId").GetGuid();
        Refused(await PostAsync(s, s.Designer, "membership-requests", Request(subject)), "MEMBERSHIP_REQUEST_EXISTS");
        var directory = await DirectoryAsync(s, s.Designer);
        var pending = directory.GetProperty("membershipRequests").EnumerateArray().Single(r => r.GetProperty("requestId").GetGuid() == requestId);
        Assert.Equal("Pending", pending.GetProperty("requestState").GetString());
        Assert.DoesNotContain(directory.GetProperty("members").EnumerateArray(), m => m.GetProperty("identitySubject").GetString() == subject);

        // Operator plane: dry run, then exactly two independent approvals; the binding is created once.
        string Path(Guid id) => $"/internal/v1/operator/organizations/{s.OrganizationId:D}/membership-requests/{id:D}/approve";
        var preview = await fixture.Authority.OperatorAsync(HttpMethod.Post, Path(requestId), new { confirm_organization_id = s.OrganizationId, justification = "Owner request verified with the unit lead", dry_run = true });
        Assert.True(preview.IsSuccess, preview.Body);
        Assert.True(preview.Root.GetProperty("eligible").GetBoolean());
        var single = await fixture.Authority.OperatorAsync(HttpMethod.Post, Path(requestId), new { confirm_organization_id = s.OrganizationId, justification = "Owner request verified with the unit lead", dry_run = false, approvals = new[] { TestIdentity.OperatorApproval(preview.Root, Approver1) } });
        Assert.False(single.IsSuccess);
        var approvals = new[] { TestIdentity.OperatorApproval(preview.Root, Approver1), TestIdentity.OperatorApproval(preview.Root, Approver2) };
        var executed = await fixture.Authority.OperatorAsync(HttpMethod.Post, Path(requestId), new { confirm_organization_id = s.OrganizationId, justification = "Owner request verified with the unit lead", dry_run = false, approvals });
        Assert.True(executed.IsSuccess, executed.Body);
        var replay = await fixture.Authority.OperatorAsync(HttpMethod.Post, Path(requestId), new { confirm_organization_id = s.OrganizationId, justification = "Owner request verified with the unit lead", dry_run = false, approvals });
        Assert.True(replay.Root.GetProperty("replayed").GetBoolean());
        await s.WaitGateReadyAsync();

        var member = (await DirectoryAsync(s, s.Designer)).GetProperty("members").EnumerateArray().Single(m => m.GetProperty("identitySubject").GetString() == subject);
        Assert.Equal("Nia Newcomer", member.GetProperty("displayName").GetString());
        Assert.Equal(["Designer"], Roles(member));
        Assert.Equal("NeverSignedIn", member.GetProperty("signInStatus").GetString());
        Assert.Equal("Approved", (await DirectoryAsync(s, s.Designer)).GetProperty("membershipRequests").EnumerateArray().Single(r => r.GetProperty("requestId").GetGuid() == requestId).GetProperty("requestState").GetString());
        Assert.Equal("1", AuditJournal(s, "OrganizationMembershipApproved"));

        // A rejected request can no longer be approved.
        var second = await PostAsync(s, s.Designer, "membership-requests", Request("ac7-second-" + Guid.NewGuid().ToString("N")[..8], null));
        var secondId = second.Root.GetProperty("requestId").GetGuid();
        var rejected = await fixture.Authority.OperatorAsync(HttpMethod.Post, $"/internal/v1/operator/organizations/{s.OrganizationId:D}/membership-requests/{secondId:D}/reject", new { confirm_organization_id = s.OrganizationId, reason = "not part of the unit" });
        Assert.True(rejected.IsSuccess, rejected.Body);
        var late = await fixture.Authority.OperatorAsync(HttpMethod.Post, Path(secondId), new { confirm_organization_id = s.OrganizationId, justification = "Owner request verified with the unit lead", dry_run = true });
        Assert.False(late.Root.GetProperty("eligible").GetBoolean());
        Assert.Equal("MEMBERSHIP_REQUEST_NOT_PENDING", late.Root.GetProperty("refusal_code").GetString());
    }
}
