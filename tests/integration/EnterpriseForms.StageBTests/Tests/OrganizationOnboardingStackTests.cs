using System.Globalization;
using System.Net;
using System.Text.Json;
using EnterpriseForms.SecurityTests.Harness;
using EnterpriseForms.StageBTests.Harness;
using Xunit;

namespace EnterpriseForms.StageBTests.Tests;

/// <summary>
/// C8 onboarding (C-104) on the real stack: Platform Console BFF (5192) → Authority platform plane (5190) → AuthorityDb, SpiceDB, the
/// archive and Vault Transit. Two console operators approve with their own Vault-signed evidence; the organization key is created by the
/// dedicated create-only custody credential (never by the Authority); initialization commits the organization, the owners and their
/// owner roles only, and the organization becomes usable when revision 1 is Effective. Also: conflicts and idempotency through the
/// console, the custody credential's denials, concurrent approvals and initializations, archive and SpiceDB outages, and the legacy
/// fixture bootstrap regression (OB-F2). Synthetic operators: never two-human approval evidence.
/// </summary>
[Collection(StageBStackCollectionDefinition.Name)]
public sealed class OrganizationOnboardingStackTests(StageBFixture fixture)
{
    public const string CustodyToken = "sectest-organization-key-custody-token-0001";
    private const string Justification = "Contract signed; owners confirmed by support callback (OPS-104).";
    private static readonly string[] ConsoleSubjects = ["sectest-console-operator-dana", "sectest-console-operator-ellis", "sectest-console-operator-frankie"];

    private string Sql(string sql) => fixture.Stack.AuthorityObserverSql(sql).Trim();

    private int Count(string sql) => int.Parse(Sql(sql), CultureInfo.InvariantCulture);

    private static string Suffix() => Guid.NewGuid().ToString("N")[..10];

    /// <summary>Owners from the synthetic test identity provider (the only approved issuer of this stack).</summary>
    private static object[] Owners(params string[] subjects) =>
        subjects.Select(s => (object)new { issuerUri = TestIdentity.Issuer, subjectIdentifier = s, displayNameHint = "Owner " + s, verificationMethod = "SupportCallback", verificationReference = "CASE-" + s[..Math.Min(s.Length, 20)] }).ToArray();

    private static Task<ConsoleResult> CreateAsync(ConsoleSession session, string name, object[] owners, string? key = null, bool dryRun = false) =>
        session.SendAsync(HttpMethod.Post, "/console-api/v1/organization-onboarding", new { idempotencyKey = key ?? "onb-" + Guid.NewGuid().ToString("N"), organizationDisplayName = name, initialOwners = owners, justification = Justification, dryRun });

    private static async Task<JsonElement> DetailAsync(ConsoleSession session, Guid requestId)
    {
        var detail = await session.SendAsync(HttpMethod.Get, $"/console-api/v1/organization-onboarding/{requestId:D}");
        Assert.True(detail.Status == HttpStatusCode.OK, detail.Text);
        return detail.Body;
    }

    private static async Task<ConsoleResult> DecideAsync(ConsoleSession session, Guid requestId, string action, string? sha = null) =>
        await session.SendAsync(HttpMethod.Post, $"/console-api/v1/organization-onboarding/{requestId:D}{action}",
            new { expectedStatementSha256 = sha ?? (await DetailAsync(session, requestId)).GetProperty("statementSha256").GetString(), justification = Justification });

    /// <summary>
    /// A decision racing another one may be refused PLATFORM_JOURNAL_ARCHIVE_PENDING (retryable, 503): grant-direction platform decisions
    /// require every earlier platform event to be anchored first (fail closed). The console offers a retry; so does this helper.
    /// </summary>
    private static async Task<ConsoleResult> DecideWithRetryAsync(ConsoleSession session, Guid requestId, string action)
    {
        for (var attempt = 1; ; attempt++)
        {
            var result = await DecideAsync(session, requestId, action);
            if (result.Code != "PLATFORM_JOURNAL_ARCHIVE_PENDING" || attempt == 10)
            {
                return result;
            }

            await Task.Delay(300);
        }
    }

    private static async Task<(Guid RequestId, Guid OrganizationId, string KeyName, JsonElement Detail)> ApprovedRequestAsync(ConsoleSession first, ConsoleSession second, string name, params string[] subjects)
    {
        var created = await CreateAsync(first, name, Owners(subjects));
        Assert.True(created.Status == HttpStatusCode.OK, created.Text);
        var request = created.Body.GetProperty("request");
        var requestId = request.GetProperty("requestId").GetGuid();
        Assert.Equal("AwaitingSecondApproval", (await DecideAsync(first, requestId, "/approvals")).Body.GetProperty("outcome").GetString());
        var approved = await DecideAsync(second, requestId, "/approvals");
        Assert.True(approved.Status == HttpStatusCode.OK, approved.Text);
        Assert.Equal("Approved", approved.Body.GetProperty("outcome").GetString());
        var detail = await DetailAsync(first, requestId);
        return (requestId, request.GetProperty("organizationId").GetGuid(), detail.GetProperty("keyCustody").GetProperty("keyName").GetString()!, detail);
    }

    /// <summary>The custody step as the development tool performs it: the dedicated create-only credential creates one non-exportable HMAC key.</summary>
    internal static void ProvisionKey(string keyName)
    {
        EnsureCustodyCredential();
        PlatformConsoleHarness.Vault($"vault write transit/keys/{keyName} type=hmac key_size=32 exportable=false allow_plaintext_backup=false", CustodyToken);
    }

    /// <summary>Idempotent: the stack's vault-custody-init service, run on demand (the same policy and fixed synthetic token).</summary>
    internal static void EnsureCustodyCredential()
    {
        if (PlatformConsoleHarness.Vault("vault token lookup >/dev/null && echo present", CustodyToken, throwOnError: false).Contains("present", StringComparison.Ordinal))
        {
            return;
        }

        new ComposeStack().Compose("up --no-deps vault-custody-init", 120);
    }

    private static async Task<JsonElement> WaitDisplayStateAsync(ConsoleSession session, Guid requestId, string wanted, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        JsonElement detail;
        do
        {
            detail = await DetailAsync(session, requestId);
            if (detail.GetProperty("displayState").GetString() == wanted)
            {
                return detail;
            }

            await Task.Delay(300);
        }
        while (DateTime.UtcNow < deadline);
        throw new TimeoutException($"request did not reach {wanted}: " + detail.GetRawText());
    }

    [Fact]
    public async Task TwoOperatorsApproveCustodyProvisionsTheKeyAndInitializationCreatesTheOwnersOnly()
    {
        await PlatformConsoleHarness.EnsureConsoleOperatorsAsync(fixture);
        using var dana = await ConsoleSession.SignInAsync(ConsoleSubjects[0]);
        using var ellis = await ConsoleSession.SignInAsync(ConsoleSubjects[1]);
        using var frankie = await ConsoleSession.SignInAsync(ConsoleSubjects[2]);
        var suffix = Suffix();
        var name = "Onboarded Clinic " + suffix;
        var owners = new[] { TestUser.New("OwnerOne"), TestUser.New("OwnerTwo"), TestUser.New("OwnerThree") };

        var preview = await CreateAsync(dana, name, Owners(owners.Select(o => o.Subject).ToArray()), dryRun: true);
        Assert.True(preview.Status == HttpStatusCode.OK, preview.Text);
        Assert.True(preview.Body.GetProperty("eligible").GetBoolean());
        Assert.Equal(0, preview.Body.GetProperty("warnings").GetArrayLength());

        var created = await CreateAsync(dana, name, Owners(owners.Select(o => o.Subject).ToArray()));
        Assert.True(created.Status == HttpStatusCode.OK, created.Text);
        var request = created.Body.GetProperty("request");
        var requestId = request.GetProperty("requestId").GetGuid();
        var organizationId = request.GetProperty("organizationId").GetGuid();
        var ownerIds = request.GetProperty("owners").EnumerateArray().ToDictionary(o => o.GetProperty("subjectIdentifier").GetString()!, o => o.GetProperty("authorityUserId").GetGuid());
        Assert.Equal("AwaitingApproval", request.GetProperty("displayState").GetString());
        Assert.Equal(0, Count($"SELECT count(*) FROM organization_authorization_states WHERE organization_id = '{organizationId:D}';"));

        // Approval 1 by the creator; the same operator cannot give approval 2, also not from a second session.
        Assert.Equal("AwaitingSecondApproval", (await DecideAsync(dana, requestId, "/approvals")).Body.GetProperty("outcome").GetString());
        using var danaAgain = await ConsoleSession.SignInAsync(ConsoleSubjects[0]);
        Assert.Equal("OPERATOR_ALREADY_APPROVED", (await DecideAsync(danaAgain, requestId, "/approvals")).Code);
        var approved = await DecideAsync(ellis, requestId, "/approvals");
        Assert.Equal("Approved", approved.Body.GetProperty("outcome").GetString());
        var detail = await DetailAsync(frankie, requestId);
        Assert.Equal("AwaitingKeyProvisioning", detail.GetProperty("displayState").GetString());
        Assert.Equal("Missing", detail.GetProperty("keyCustody").GetProperty("status").GetString());
        Assert.False(detail.GetProperty("myEligibility").GetProperty("canInitialize").GetBoolean());

        // Initialization before key custody fails closed and creates nothing.
        Assert.Equal("ORGANIZATION_KEY_NOT_PROVISIONED", (await DecideAsync(frankie, requestId, "/initialize")).Code);
        Assert.Equal(0, Count($"SELECT count(*) FROM organization_authorization_states WHERE organization_id = '{organizationId:D}';"));

        ProvisionKey(detail.GetProperty("keyCustody").GetProperty("keyName").GetString()!);
        Assert.Equal("Ready", (await DetailAsync(frankie, requestId)).GetProperty("keyCustody").GetProperty("status").GetString());
        var initialized = await DecideAsync(frankie, requestId, "/initialize");
        Assert.True(initialized.Status == HttpStatusCode.OK, initialized.Text);
        Assert.Equal("Initialized", initialized.Body.GetProperty("outcome").GetString());
        var active = await WaitDisplayStateAsync(frankie, requestId, "Active", TimeSpan.FromSeconds(60));
        Assert.Equal("Active", active.GetProperty("activation").GetProperty("status").GetString());

        // Exactly the owners, with Authority-generated ids, and the owner role only.
        Assert.Equal(requestId.ToString("D"), Sql($"SELECT organization_onboarding_request_id FROM organization_authorization_states WHERE organization_id = '{organizationId:D}';"));
        Assert.Equal(3, Count($"SELECT count(*) FROM identity_bindings WHERE organization_id = '{organizationId:D}' AND organization_onboarding_request_id = '{requestId:D}';"));
        Assert.Equal(3, Count($"SELECT count(*) FROM identity_bindings WHERE organization_id = '{organizationId:D}';"));
        Assert.Equal("organization_owner|3", Sql($"SELECT relationship_name || '|' || count(*) FROM authority_relationship_edges WHERE organization_id = '{organizationId:D}' AND relationship_removed_on IS NULL GROUP BY relationship_name;"));
        Assert.Equal("InitializeOrganization|1", Sql($"SELECT command_kind_name || '|' || committed_authorization_revision FROM security_commands WHERE organization_id = '{organizationId:D}';"));
        foreach (var kind in new[] { "OrganizationOnboardingRequested", "OrganizationOnboardingApprovalRecorded", "OrganizationOnboardingApproved", "OrganizationOnboardingInitialized" })
        {
            Assert.Equal(1, Count($"SELECT count(*) FROM platform_journal_entries WHERE journal_event_kind = '{kind}' AND journal_event_body_json LIKE '%{requestId:D}%';"));
        }

        // The organization is usable by its owners (Authority-generated user ids) once revision 1 is Effective.
        var ownerOne = owners[0] with { AuthorityUserId = ownerIds[owners[0].Subject] };
        var me = await fixture.Api.GetAsync("/api/v1/users/me", TestIdentity.UserToken(ownerOne, organizationId));
        Assert.True(me.IsSuccess, me.Body);
        var overview = await fixture.Api.GetAsync("/api/v1/organization/overview", TestIdentity.UserToken(ownerOne, organizationId));
        Assert.True(overview.IsSuccess, overview.Body);
        // An identity that is not an initial owner is not a member.
        Assert.False((await fixture.Api.GetAsync("/api/v1/organization/overview", TestIdentity.UserToken(TestUser.New("Stranger"), organizationId))).IsSuccess);

        // PG-072: the organization is listed with three owners; the detail view is audited on its audit stream.
        var listed = await dana.SendAsync(HttpMethod.Get, "/console-api/v1/organizations?q=" + Uri.EscapeDataString(name));
        var row = Assert.Single(listed.Body.GetProperty("items").EnumerateArray());
        Assert.Equal(3, row.GetProperty("enabledOwnerCount").GetInt32());
        Assert.Equal("Active", row.GetProperty("lifecycle").GetString());
        var directory = await dana.SendAsync(HttpMethod.Get, $"/console-api/v1/organizations/{organizationId:D}");
        Assert.True(directory.Status == HttpStatusCode.OK, directory.Text);
        Assert.Equal(3, directory.Body.GetProperty("owners").GetArrayLength());
        Assert.Equal(2, directory.Body.GetProperty("onboardingApprovedBy").GetArrayLength());
        Assert.Equal(1, Count($"SELECT count(*) FROM security_journal_entries WHERE organization_id = '{organizationId:D}' AND journal_event_kind = 'PlatformOrganizationDirectoryViewed';"));
    }

    [Fact]
    public async Task ConflictsSelfAppointmentIdempotencyAndStaleFingerprintsAreRefusedThroughTheConsole()
    {
        await PlatformConsoleHarness.EnsureConsoleOperatorsAsync(fixture);
        using var dana = await ConsoleSession.SignInAsync(ConsoleSubjects[0]);
        using var ellis = await ConsoleSession.SignInAsync(ConsoleSubjects[1]);
        var suffix = Suffix();
        var name = "Conflict Office " + suffix;
        var subjects = new[] { "conflict-a-" + suffix, "conflict-b-" + suffix, "conflict-c-" + suffix };

        Assert.Equal("ORGANIZATION_OWNERS_INSUFFICIENT", (await CreateAsync(dana, name, Owners(subjects[0]))).Code);
        // An operator identity can never be an initial owner: operators come from their own issuer, which is never an approved owner issuer.
        var operatorAsOwner = new object[]
        {
            new { issuerUri = TestIdentity.PlatformOperatorIssuer, subjectIdentifier = ConsoleSubjects[0], displayNameHint = "Dana", verificationMethod = "SupportCallback", verificationReference = "CASE-1" },
            Owners(subjects[1])[0],
        };
        Assert.Equal("ORGANIZATION_OWNER_IDENTITY_INVALID", (await CreateAsync(dana, name, operatorAsOwner)).Code);
        // The browser cannot supply an organization id, user ids or an operator.
        var smuggled = await dana.SendAsync(HttpMethod.Post, "/console-api/v1/organization-onboarding", new { idempotencyKey = "onb-" + suffix + "-x", organizationDisplayName = name, initialOwners = Owners(subjects), justification = Justification, organizationId = Guid.CreateVersion7() });
        Assert.Equal(HttpStatusCode.BadRequest, smuggled.Status);

        var key = "onb-idem-" + suffix;
        var first = await CreateAsync(dana, name, Owners(subjects), key);
        Assert.True(first.Status == HttpStatusCode.OK, first.Text);
        var requestId = first.Body.GetProperty("request").GetProperty("requestId").GetGuid();
        var replay = await CreateAsync(dana, name, Owners(subjects), key);
        Assert.True(replay.Body.GetProperty("replayed").GetBoolean());
        Assert.Equal(requestId, replay.Body.GetProperty("request").GetProperty("requestId").GetGuid());
        Assert.Equal("IDEMPOTENCY_CONFLICT", (await CreateAsync(dana, name + " Two", Owners(subjects), key)).Code);
        Assert.Equal(1, Count($"SELECT count(*) FROM organization_onboarding_requests WHERE organization_display_name = '{name}';"));

        // Duplicate names: another open request, and an existing organization (case and spacing ignored).
        Assert.Equal("ORGANIZATION_NAME_CONFLICT", (await CreateAsync(ellis, "  " + name.ToUpperInvariant() + " ", Owners(subjects))).Code);
        var s = new StageBScenario(fixture, "onb-existing-" + suffix);
        await s.BootstrapAsync();
        Assert.Equal("ORGANIZATION_NAME_CONFLICT", (await CreateAsync(ellis, ("stage b onb-existing-" + suffix).ToUpperInvariant(), Owners(subjects))).Code);

        // A stale fingerprint is refused before anything is signed.
        var other = await CreateAsync(ellis, "Other Office " + suffix, Owners("other-a-" + suffix, "other-b-" + suffix));
        var otherSha = other.Body.GetProperty("request").GetProperty("statementSha256").GetString();
        var usesBefore = Count("SELECT count(*) FROM platform_operator_evidence_uses;");
        Assert.Equal("APPROVAL_STATEMENT_CHANGED", (await DecideAsync(dana, requestId, "/approvals", otherSha)).Code);
        Assert.Equal(usesBefore, Count("SELECT count(*) FROM platform_operator_evidence_uses;"));
        Assert.Equal(0, Count($"SELECT count(*) FROM organization_onboarding_approvals WHERE organization_onboarding_request_id = '{requestId:D}';"));

        // Reject closes the request; nothing is created and it cannot be approved afterwards.
        Assert.Equal("Rejected", (await DecideAsync(ellis, requestId, "/reject")).Body.GetProperty("outcome").GetString());
        Assert.Equal("ORGANIZATION_ONBOARDING_NOT_PENDING", (await DecideAsync(dana, requestId, "/approvals")).Code);
    }

    [Fact]
    public async Task TheCustodyCredentialCanOnlyCreateNewOrganizationKeys()
    {
        EnsureCustodyCredential();
        var s = new StageBScenario(fixture, "onb-custody-" + Suffix());
        await s.BootstrapAsync();
        var existing = Sql($"SELECT provider_key_name FROM integrity_key_registrations WHERE organization_id = '{s.OrganizationId:D}';");
        Assert.StartsWith("org-", existing, StringComparison.Ordinal);
        foreach (var denied in new[]
        {
            $"vault read transit/keys/{existing}",
            $"vault write transit/hmac/{existing}/sha2-256 input=AAAA",
            $"vault write -f transit/keys/{existing}/rotate",
            $"vault write transit/keys/{existing}/config deletion_allowed=true",
            $"vault write transit/keys/{existing} exportable=true",
            $"vault write transit/keys/org-{Guid.CreateVersion7():N}-row-integrity-v1 type=aes256-gcm96",
            $"vault write transit/keys/org-{Guid.CreateVersion7():N}-row-integrity-v1 type=hmac key_size=32 exportable=true",
            $"vault read transit/export/hmac-key/{existing}",
            $"vault delete transit/keys/{existing}",
        })
        {
            var output = PlatformConsoleHarness.Vault(denied, CustodyToken, throwOnError: false);
            Assert.Contains("permission denied", output, StringComparison.OrdinalIgnoreCase);
        }

        // Vault authorizes a key create as update: re-creating the existing key through the custody credential changes nothing.
        var root = PlatformConsoleHarness.RootToken();
        var before = PlatformConsoleHarness.Vault($"vault read -format=json transit/keys/{existing}", root);
        PlatformConsoleHarness.Vault($"vault write transit/keys/{existing} type=hmac key_size=32", CustodyToken);
        var after = PlatformConsoleHarness.Vault($"vault read -format=json transit/keys/{existing}", root);
        static string Facts(string json)
        {
            var data = JsonDocument.Parse(json[json.IndexOf('{', StringComparison.Ordinal)..]).RootElement.GetProperty("data");
            return $"{data.GetProperty("latest_version").GetInt32()}|{data.GetProperty("exportable").GetBoolean()}|{data.GetProperty("deletion_allowed").GetBoolean()}|{data.GetProperty("type").GetString()}";
        }

        Assert.Equal(Facts(before), Facts(after));

        // The console's token cannot create organization keys either.
        var console = PlatformConsoleHarness.Vault($"vault write transit/keys/org-{Guid.CreateVersion7():N}-row-integrity-v1 type=hmac key_size=32", PlatformConsoleHarness.ConsoleVaultToken, throwOnError: false);
        Assert.Contains("permission denied", console, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConcurrentApprovalsAndInitializationsProduceExactlyOneOrganization()
    {
        await PlatformConsoleHarness.EnsureConsoleOperatorsAsync(fixture);
        using var dana = await ConsoleSession.SignInAsync(ConsoleSubjects[0]);
        using var ellis = await ConsoleSession.SignInAsync(ConsoleSubjects[1]);
        var suffix = Suffix();
        var created = await CreateAsync(dana, "Concurrent Office " + suffix, Owners("conc-a-" + suffix, "conc-b-" + suffix, "conc-c-" + suffix));
        var requestId = created.Body.GetProperty("request").GetProperty("requestId").GetGuid();
        var organizationId = created.Body.GetProperty("request").GetProperty("organizationId").GetGuid();

        var approvals = await Task.WhenAll(DecideWithRetryAsync(dana, requestId, "/approvals"), DecideWithRetryAsync(ellis, requestId, "/approvals"));
        Assert.All(approvals, a => Assert.True(a.Status == HttpStatusCode.OK, a.Text));
        Assert.Equal(["Approved", "AwaitingSecondApproval"], approvals.Select(a => a.Body.GetProperty("outcome").GetString()).Order().ToArray());
        Assert.Equal(2, Count($"SELECT count(*) FROM organization_onboarding_approvals WHERE organization_onboarding_request_id = '{requestId:D}' AND approval_state_name = 'Consumed';"));

        ProvisionKey((await DetailAsync(dana, requestId)).GetProperty("keyCustody").GetProperty("keyName").GetString()!);
        var initializations = await Task.WhenAll(DecideWithRetryAsync(dana, requestId, "/initialize"), DecideWithRetryAsync(ellis, requestId, "/initialize"));
        Assert.All(initializations, i => Assert.True(i.Status == HttpStatusCode.OK, i.Text));
        Assert.Equal(["Initialized", "Replayed"], initializations.Select(i => i.Body.GetProperty("outcome").GetString()).Order().ToArray());
        Assert.Equal(1, Count($"SELECT count(*) FROM organization_authorization_states WHERE organization_id = '{organizationId:D}';"));
        Assert.Equal(1, Count($"SELECT count(*) FROM security_commands WHERE organization_id = '{organizationId:D}';"));
        Assert.Equal(3, Count($"SELECT count(*) FROM identity_bindings WHERE organization_id = '{organizationId:D}';"));
    }

    [Fact]
    public async Task ArchiveAndSpiceDbOutagesFailClosedAndAnInterruptedInitializationRetriesCleanly()
    {
        await PlatformConsoleHarness.EnsureConsoleOperatorsAsync(fixture);
        using var dana = await ConsoleSession.SignInAsync(ConsoleSubjects[0]);
        using var ellis = await ConsoleSession.SignInAsync(ConsoleSubjects[1]);
        var suffix = Suffix();
        var name = "Outage Office " + suffix;

        fixture.Stack.Pause("archive-store");
        try
        {
            var refused = await CreateAsync(dana, name, Owners("out-a-" + suffix, "out-b-" + suffix));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.Status);
            Assert.Equal("PLATFORM_JOURNAL_UNVERIFIABLE", refused.Code);
        }
        finally
        {
            fixture.Stack.Unpause("archive-store");
        }

        Assert.Equal(0, Count($"SELECT count(*) FROM organization_onboarding_requests WHERE organization_display_name = '{name}';"));
        var (requestId, organizationId, keyName, _) = await ApprovedRequestAsync(dana, ellis, name, "out-a-" + suffix, "out-b-" + suffix, "out-c-" + suffix);
        ProvisionKey(keyName);

        fixture.Stack.Compose("stop spicedb", 120);
        try
        {
            var interrupted = await DecideAsync(dana, requestId, "/initialize");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, interrupted.Status);
            Assert.Equal(0, Count($"SELECT count(*) FROM organization_authorization_states WHERE organization_id = '{organizationId:D}';"));
            Assert.Equal(0, Count($"SELECT count(*) FROM identity_bindings WHERE organization_id = '{organizationId:D}';"));
        }
        finally
        {
            fixture.Stack.Compose("up -d --no-deps --wait spicedb", 180);
        }

        var retried = await DecideAsync(dana, requestId, "/initialize");
        Assert.True(retried.Status == HttpStatusCode.OK, retried.Text);
        Assert.Equal("Initialized", retried.Body.GetProperty("outcome").GetString());
        await WaitDisplayStateAsync(dana, requestId, "Active", TimeSpan.FromSeconds(90));
        Assert.Equal(3, Count($"SELECT count(*) FROM identity_bindings WHERE organization_id = '{organizationId:D}';"));
    }

    [Fact]
    public async Task TheLegacyFixtureBootstrapStillWorksAndNoLongerAnswersFalseSuccess()
    {
        var s = new StageBScenario(fixture, "onb-legacy-" + Suffix());
        await s.BootstrapAsync(); // the approved fixture path of every Stage B test
        var owner = new { authority_user_id = s.Designer.AuthorityUserId, issuer_uri = TestIdentity.Issuer, subject_identifier = s.Designer.Subject, organization_roles = new[] { "organization_owner" } };
        var conflicting = await fixture.Authority.OperatorAsync(HttpMethod.Post, "/internal/v1/operator/organizations", new { organization_id = s.OrganizationId, organization_display_name = "Someone Else", identities = new[] { owner } });
        Assert.Equal(HttpStatusCode.Conflict, conflicting.Status);
        Assert.Equal("IDEMPOTENCY_CONFLICT", conflicting.Root.GetProperty("error_code").GetString());
        Assert.Equal("Stage B " + s.Name, Sql($"SELECT organization_display_name FROM organization_authorization_states WHERE organization_id = '{s.OrganizationId:D}';"));
    }
}
