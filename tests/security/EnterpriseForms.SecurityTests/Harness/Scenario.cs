using System.Net;
using System.Text.Json;
using Xunit;

namespace EnterpriseForms.SecurityTests.Harness;

/// <summary>
/// Per-test organization: bootstrapped through the authority operator plane with fresh synthetic principals, then a
/// form and a record created through the business API. Every test starts from its own organization so hostile writes and
/// injected faults never leak between tests.
/// </summary>
public sealed class Scenario
{
    public Scenario(StageAFixture fixture, string testId, string description)
    {
        Fixture = fixture;
        Evidence = new Evidence(testId, description);
        OrganizationId = Guid.CreateVersion7();
        Owner = TestUser.New("Owner");
        Attacker = TestUser.New("Attacker");
        Reader = TestUser.New("Reader");
        Administrator = TestUser.New("Administrator");
    }

    public StageAFixture Fixture { get; }

    public Evidence Evidence { get; }

    public Guid OrganizationId { get; }

    public TestUser Owner { get; }

    public TestUser Attacker { get; }

    public TestUser Reader { get; }

    public TestUser Administrator { get; }

    public Guid FormDefinitionId { get; private set; }

    public Guid FormRecordId { get; private set; }

    public Dictionary<string, Guid> FieldItemIds { get; } = new(StringComparer.Ordinal);

    public string Token(TestUser user) => TestIdentity.UserToken(user, OrganizationId);

    public async Task BootstrapAsync(params TestUser[] extraMembers)
    {
        var identities = new List<object>
        {
            new { authority_user_id = Owner.AuthorityUserId, issuer_uri = TestIdentity.Issuer, subject_identifier = Owner.Subject, organization_roles = new[] { "organization_owner", "designer" } },
            new { authority_user_id = Administrator.AuthorityUserId, issuer_uri = TestIdentity.Issuer, subject_identifier = Administrator.Subject, organization_roles = new[] { "system_administrator" } },
            new { authority_user_id = Attacker.AuthorityUserId, issuer_uri = TestIdentity.Issuer, subject_identifier = Attacker.Subject, organization_roles = Array.Empty<string>() },
            new { authority_user_id = Reader.AuthorityUserId, issuer_uri = TestIdentity.Issuer, subject_identifier = Reader.Subject, organization_roles = Array.Empty<string>() },
        };
        identities.AddRange(extraMembers.Select(m => new { authority_user_id = m.AuthorityUserId, issuer_uri = TestIdentity.Issuer, subject_identifier = m.Subject, organization_roles = Array.Empty<string>() }));
        var bootstrap = await Fixture.Authority.OperatorAsync(HttpMethod.Post, "/internal/v1/operator/organizations", new { organization_id = OrganizationId, organization_display_name = "SEC " + Evidence.TestId, identities }).ConfigureAwait(false);
        Evidence.ObserveHttp("bootstrap organization", bootstrap, "authorization_revision", "integrity_key_name", "signing_key_version");
        Assert.True(bootstrap.IsSuccess, "bootstrap failed: " + bootstrap.Body);
        await WaitGateReadyAsync().ConfigureAwait(false);

        foreach (var user in new[] { Owner, Attacker, Reader, Administrator }.Concat(extraMembers))
        {
            var me = await Fixture.Api.GetAsync("/api/v1/users/me", Token(user)).ConfigureAwait(false);
            Assert.True(me.IsSuccess, "users/me failed for " + user.DisplayName + ": " + me.Body);
        }
    }

    public async Task CreateFormAndRecordAsync(IReadOnlyDictionary<string, string>? values = null, Guid? ownerTeamId = null, IReadOnlyList<Guid>? formUserUserIds = null, IReadOnlyList<Guid>? formUserTeamIds = null)
    {
        var form = await Fixture.Api.PostAsync("/api/v1/form-definitions", Token(Owner), new
        {
            formDefinitionName = "Stage A " + Evidence.TestId,
            formDefinitionDescription = "synthetic",
            fields = new[] { new { fieldKey = "given_name", fieldDisplayName = "Given name" }, new { fieldKey = "family_name", fieldDisplayName = "Family name" }, new { fieldKey = "case_note", fieldDisplayName = "Case note" } },
            formUserUserIds = (formUserUserIds ?? [Owner.AuthorityUserId]).ToArray(),
            formUserTeamIds = (formUserTeamIds ?? []).ToArray(),
        }).ConfigureAwait(false);
        Evidence.ObserveHttp("create form definition", form, "formDefinitionId", "authorizationRevision");
        Assert.Equal(HttpStatusCode.Created, form.Status);
        FormDefinitionId = form.Root.GetProperty("formDefinitionId").GetGuid();

        var record = await Fixture.Api.PostAsync($"/api/v1/form-definitions/{FormDefinitionId:D}/records", Token(Owner), new
        {
            values = values ?? new Dictionary<string, string> { ["given_name"] = "Synthetic", ["family_name"] = "Subject", ["case_note"] = "protected note " + Evidence.TestId },
            ownerTeamId,
        }).ConfigureAwait(false);
        Evidence.ObserveHttp("create record", record, "formRecordId", "contentRevision", "committedOperationId", "authorizationRevision", "integrityStatus");
        Assert.Equal(HttpStatusCode.Created, record.Status);
        FormRecordId = record.Root.GetProperty("formRecordId").GetGuid();
        Evidence.AuthorityRevision = record.Root.GetProperty("authorizationRevision").GetInt64();
        Evidence.ContentRevision = record.Root.GetProperty("contentRevision").GetInt64();
        foreach (var value in record.Root.GetProperty("values").EnumerateArray())
        {
            FieldItemIds[value.GetProperty("fieldKey").GetString()!] = value.GetProperty("fieldValueItemId").GetGuid();
        }

        await WaitReadableAsync(Owner).ConfigureAwait(false);
    }

    /// <summary>Owner read of the record; waits for the content checkpoint archive receipt (reads are gated until then).</summary>
    public async Task<HttpResult> WaitReadableAsync(TestUser user, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        HttpResult last;
        do
        {
            last = await Fixture.Api.GetAsync($"/api/v1/form-records/{FormRecordId:D}", Token(user)).ConfigureAwait(false);
            if (last.Status != HttpStatusCode.ServiceUnavailable)
            {
                return last;
            }

            await Task.Delay(250).ConfigureAwait(false);
        }
        while (DateTime.UtcNow < deadline);
        return last;
    }

    /// <summary>
    /// Submits a security command straight to the authority with the business workload credential and the user's
    /// assertion — exactly what the business API does — for faults that block the API's own pre-checks (SpiceDB paused).
    /// </summary>
    public async Task<HttpResult> SubmitAuthorityCommandAsync(TestUser user, string commandKind, string resourceKind, Guid resourceId, object payload, string? idempotencyKey = null)
    {
        var gate = await Fixture.Authority.GateAsync(OrganizationId).ConfigureAwait(false);
        var result = await Fixture.Authority.SendAsync(HttpMethod.Post, "/internal/v1/security-commands", TestIdentity.WorkloadToken(), new
        {
            organization_id = OrganizationId,
            command_kind = commandKind,
            resource = new { resource_kind = resourceKind, resource_id = resourceId },
            expected_authorization_revision = gate.GetProperty("authorization_revision").GetInt64(),
            command_payload = payload,
            change_reason = "harness",
        }, Token(user), idempotencyKey ?? Guid.NewGuid().ToString("N")).ConfigureAwait(false);
        Evidence.ObserveHttp("direct authority command " + commandKind, result, "command_status", "authorization_revision", "projection_gate_state", "error_code");
        return result;
    }

    public Task<HttpResult> ReadAsync(TestUser user) => Fixture.Api.GetAsync($"/api/v1/form-records/{FormRecordId:D}", Token(user));

    public async Task<JsonElement> WaitGateReadyAsync(TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        JsonElement gate;
        do
        {
            gate = await Fixture.Authority.GateAsync(OrganizationId).ConfigureAwait(false);
            if (gate.GetProperty("projection_gate_state").GetString() == "Ready")
            {
                return gate;
            }

            await Task.Delay(200).ConfigureAwait(false);
        }
        while (DateTime.UtcNow < deadline);
        throw new TimeoutException("organization gate did not open: " + gate.GetRawText());
    }

    public string Finish(string outcome, string? failure = null) => Evidence.Write(outcome, Fixture.CommitSha, Fixture.ImageFingerprint, failure);
}

/// <summary>Runs a scenario body and records PASS/FAIL evidence; an exception is a FAIL with the sanitized reason.</summary>
public static class ScenarioRunner
{
    public static async Task RunAsync(Scenario scenario, Func<Task> body)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        ArgumentNullException.ThrowIfNull(body);
        try
        {
            await body().ConfigureAwait(false);
            scenario.Finish("PASS");
        }
        catch (Exception ex)
        {
            scenario.Finish("FAIL", Evidence.Sanitize(ex.GetType().Name + ": " + ex.Message));
            throw;
        }
    }
}
