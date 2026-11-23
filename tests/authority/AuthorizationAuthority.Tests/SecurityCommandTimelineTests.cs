using System.Text.Json;
using System.Text.RegularExpressions;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.Audit;
using AuthorizationAuthority.Application.Features.Authorization;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// C8 M4-3 (AC-8.2, AC-M4-3.1): the security-command timeline through the real catalog and service over the in-memory store.
/// Classification is exhaustive; each included kind's envelope has exactly its allowlisted keys; prohibited fields are withheld
/// by fixed name and their values never appear (sentinel scan); values of the wrong shape are dropped; command rows are
/// journal-backed or command-row-only without duplicates; the keyset is stable; filters apply in the store; the page is read
/// inside the AC-8.3 transaction and returned only with a committed AuditAccessAuthorized (served_by authority). PostgreSQL
/// (row comparison, indexes, NOWAIT, timeouts) and the business path run in Stage B (SecurityCommandTimelineTests), not run here.
/// </summary>
public sealed partial class SecurityCommandTimelineTests : IDisposable
{
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly Guid OtherOrg = Guid.CreateVersion7();
    private static readonly Guid Auditor = Guid.CreateVersion7();
    private static readonly Guid Member = Guid.CreateVersion7();
    private const string Issuer = "https://idp.test.invalid";
    private static readonly DateTimeOffset T0 = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryAuthorityStore store = new();
    private readonly AuditAccessTelemetry telemetry = new();
    private readonly Guid memberBinding = Guid.CreateVersion7();

    public SecurityCommandTimelineTests()
    {
        store.Organizations.Add(new OrganizationAuthorizationState { OrganizationId = Org, OrganizationDisplayName = "Timeline Org", AuthorizationRevision = 9, ProjectionAppliedRevision = 9 });
        store.Organizations.Add(new OrganizationAuthorizationState { OrganizationId = OtherOrg, OrganizationDisplayName = "Other Org", AuthorizationRevision = 3, ProjectionAppliedRevision = 3 });
        store.Bindings.Add(new IdentityBinding { IdentityBindingId = Guid.CreateVersion7(), OrganizationId = Org, AuthorityUserId = Auditor, IdentityIssuerUri = Issuer, IdentitySubjectIdentifier = Subject(Auditor), IdentityBindingEnabled = true });
        store.Bindings.Add(new IdentityBinding { IdentityBindingId = memberBinding, OrganizationId = Org, AuthorityUserId = Member, IdentityIssuerUri = Issuer, IdentitySubjectIdentifier = Subject(Member), IdentityBindingEnabled = true });
    }

    public void Dispose() => telemetry.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Subject(Guid user) => "subject-" + user.ToString("N");

    private static VerifiedActor Actor(Guid user) => new(Issuer, Subject(user), "enterpriseforms-api");

    private SecurityCommandTimelineService Service(HashSet<Guid>? auditors = null) =>
        new(new AuditAccessService(store, new ViewAuditCheck(store, auditors ?? [Auditor]), new Unlimited(),
            new AuditAccessOptions { RetryBackoffMinimumMilliseconds = 1, RetryBackoffMaximumMilliseconds = 2 }, telemetry, TimeProvider.System));

    private static SecurityCommandTimelineRequest Request(int pageSize = 50, string? after = null, IReadOnlyList<string>? kinds = null, IReadOnlyList<string>? states = null, DateTimeOffset? from = null, DateTimeOffset? to = null) =>
        new(Org, after, pageSize, kinds, states, from, to, Guid.CreateVersion7());

    // ---- classification (R-2) ----

    [Fact]
    public void EveryCommandKindIsClassifiedExactlyOnce()
    {
        Assert.Empty(SecurityCommandTimelineCatalog.CommandKinds.Keys.Intersect(SecurityCommandTimelineCatalog.ExcludedCommandKinds));
        foreach (var kind in Enum.GetNames<SecurityCommandKind>())
        {
            Assert.True(SecurityCommandTimelineCatalog.CommandKinds.ContainsKey(kind) ^ SecurityCommandTimelineCatalog.ExcludedCommandKinds.Contains(kind), kind);
        }

        Assert.Empty(SecurityCommandTimelineCatalog.RejectionEvidenceKinds.Intersect(SecurityCommandTimelineCatalog.AuditKinds.Keys));
        Assert.Contains("EnvelopeCapabilityRefused", SecurityCommandTimelineCatalog.AuditKinds.Keys); // PD-09 option B
        Assert.True(SecurityCommandTimelineCatalog.AuditKinds["EnvelopeCapabilityRefused"].ReportedByBusinessRuntime);
        foreach (var excluded in new[] { "RegisterRecord", "UnlinkChildRecord", "RegisterFormDefinition", "RegisterPublishedFormVersion" })
        {
            Assert.Contains(excluded, SecurityCommandTimelineCatalog.ExcludedCommandKinds);
        }
    }

    [GeneratedRegex(@"CommandKindName = ""([A-Za-z]+)""|const string \w*CommandKind = ""([A-Za-z]+)""|\? ""([A-Za-z]+)"" : ""([A-Za-z]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex CommandKindLiteral();

    /// <summary>Every command-kind literal the Authority writes to security_commands (outside the enum) is classified too.</summary>
    [Fact]
    public void EveryCommandKindNameWrittenByTheAuthorityIsClassified()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src", "authority")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root.FullName, "src", "authority", "AuthorizationAuthority.Application"), "*.cs", SearchOption.AllDirectories))
        {
            foreach (var line in File.ReadLines(file).Where(l => l.Contains("CommandKind", StringComparison.Ordinal)))
            {
                foreach (Match match in CommandKindLiteral().Matches(line))
                {
                    foreach (var group in match.Groups.Values.Skip(1).Where(g => g.Success))
                    {
                        found.Add(group.Value);
                    }
                }
            }
        }

        Assert.Contains("ApproveOrganizationMembership", found);
        Assert.Contains("RecoverOrganizationOwners", found);
        foreach (var kind in found)
        {
            Assert.True(SecurityCommandTimelineCatalog.CommandKinds.ContainsKey(kind) || SecurityCommandTimelineCatalog.ExcludedCommandKinds.Contains(kind), kind);
        }
    }

    // ---- envelopes (AC-M4-3.1, T08 at unit level) ----

    private static readonly Dictionary<string, string[]> ExpectedCommandKeys = new(StringComparer.Ordinal)
    {
        ["GrantSystemRole"] = ["target_user_id", "role_name", "reason_code"],
        ["RevokeSystemRole"] = ["target_user_id", "role_name", "reason_code"],
        ["GrantOrganizationRole"] = ["target_user_id", "role_name", "proposal_id"],
        ["RevokeOrganizationRole"] = ["target_user_id", "role_name", "proposal_id"],
        ["ProposeOrganizationOwnerChange"] = ["target_user_id", "proposed_change", "proposal_id"],
        ["DisableIdentityBinding"] = ["target_user_id", "proposal_id"],
        ["EnableIdentityBinding"] = ["target_user_id", "proposal_id"],
        ["RequestOrganizationMembership"] = ["membership_request_id", "requested_initial_role"],
        ["CancelOrganizationMembershipRequest"] = ["membership_request_id"],
        ["ReplaceOrganizationMembershipRequest"] = ["membership_request_id", "replaced_membership_request_id", "requested_initial_role"],
        ["RegisterTeam"] = ["team_id"],
        ["AddTeamMember"] = ["team_id", "member_user_id"],
        ["RemoveTeamMember"] = ["team_id", "member_user_id"],
        ["UpdateFormUsers"] = ["form_definition_id", "added_principals", "removed_principals"],
        ["UpdateMandatoryPolicy"] = ["minimum_field_classification", "plaintext_internal_fields_permitted", "plaintext_downgrade_permitted", "plaintext_search_permitted"],
        ["ReclassifyField"] = ["form_definition_id", "field_definition_id", "target_classification"],
        ["PublishFormSecurityRevision"] = ["form_definition_id", "field_definition_id", "required_protection_mode", "field_classification", "protection_policy_revision"],
        ["ApproveFieldSearch"] = ["form_definition_id", "field_definition_id"],
        ["WithdrawFieldSearch"] = ["form_definition_id", "field_definition_id"],
        ["UpdateFieldSearchers"] = ["form_definition_id", "added_searchers", "removed_searchers"],
        ["TransferOwnership"] = ["form_record_id", "new_owner"],
        ["CreateShare"] = ["form_record_id", "record_share_id", "recipient", "recipient_role"],
        ["RevokeShare"] = ["form_record_id", "record_share_id"],
        ["UpdateShareScope"] = ["form_record_id", "record_share_id"],
    };

    private static readonly Guid ScopeSentinel = Guid.Parse("5e5e5e5e-5e5e-7e5e-8e5e-5e5e5e5e5e5e");

    /// <summary>One payload carrying every allowlisted source key and every prohibited field (sentinels).</summary>
    private static string UniversalPayload() => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["authority_user_id"] = Guid.CreateVersion7(), ["role_name"] = "security_auditor", ["reason_code"] = "AUDIT_COVERAGE_ASSIGNMENT", ["proposal_id"] = Guid.CreateVersion7(),
        ["proposed_change"] = "GrantOwner", ["initial_role_name"] = "designer", ["membership_request_id"] = Guid.CreateVersion7(), ["team_id"] = Guid.CreateVersion7(),
        ["member_user_id"] = Guid.CreateVersion7(), ["form_definition_id"] = Guid.CreateVersion7(), ["grant_user_ids"] = new[] { Guid.CreateVersion7() },
        ["grant_team_ids"] = new[] { Guid.CreateVersion7() }, ["revoke_user_ids"] = Array.Empty<Guid>(), ["revoke_team_ids"] = new[] { Guid.CreateVersion7() },
        ["minimum_field_classification"] = "Confidential", ["plaintext_internal_fields_permitted"] = true, ["plaintext_downgrade_permitted"] = false, ["plaintext_search_permitted"] = false,
        ["field_definition_id"] = Guid.CreateVersion7(), ["target_classification"] = "Internal", ["required_protection_mode"] = "ApplicationEncrypted", ["field_classification"] = "Confidential",
        ["protection_policy_revision"] = 3, ["form_record_id"] = Guid.CreateVersion7(), ["new_owner_user_id"] = Guid.CreateVersion7(), ["record_share_id"] = Guid.CreateVersion7(),
        ["recipient_team_id"] = Guid.CreateVersion7(), ["recipient_role"] = "Reader",
        ["reason"] = "SENT-FREE-REASON", ["reason_note"] = "SENT-REASON-NOTE", ["step_up_evidence"] = "SENT-STEP-UP", ["change_reason"] = "SENT-CHANGE-REASON",
        ["display_name_hint"] = "SENT-NAME-HINT", ["approval_reference"] = "SENT-APPROVAL-REF", ["withdrawal_reason"] = "SENT-WITHDRAWAL", ["field_item_ids"] = new[] { ScopeSentinel },
        ["issuer_uri"] = "https://SENT-ISSUER.invalid", ["subject_identifier"] = "SENT-SUBJECT", ["expected_request_digest"] = "SENT-REQUEST-DIGEST", ["operators"] = new List<string> { "SENT-OPERATOR" },
    });

    private static void AssertNoSentinel(object value)
    {
        var json = JsonSerializer.Serialize(value);
        Assert.DoesNotContain("SENT-", json, StringComparison.Ordinal);
        Assert.DoesNotContain(ScopeSentinel.ToString("D"), json, StringComparison.Ordinal);
    }

    [Fact]
    public void EachIncludedCommandKindShowsExactlyItsAllowlistedFieldsAndWithholdsTheRest()
    {
        Assert.Equal(SecurityCommandTimelineCatalog.CommandKinds.Keys.Order(StringComparer.Ordinal), ExpectedCommandKeys.Keys.Order(StringComparer.Ordinal));
        foreach (var (kind, expected) in ExpectedCommandKeys)
        {
            var envelope = SecurityCommandTimelineCatalog.BuildCommandEnvelope(kind, UniversalPayload(), Guid.CreateVersion7(), "Effective", null);
            Assert.Equal(expected.Order(StringComparer.Ordinal), envelope.Details.Select(d => d.Key).Order(StringComparer.Ordinal));
            AssertNoSentinel(envelope);
            Assert.Contains("reason", envelope.WithheldFields);
            Assert.Contains("reason_note", envelope.WithheldFields);
            Assert.Contains("step_up_evidence", envelope.WithheldFields);
            Assert.Contains("share_scope", envelope.WithheldFields);
            Assert.Contains("identity_identifiers", envelope.WithheldFields);
        }
    }

    [Fact]
    public void ARejectedCommandShowsNoDerivedIdentifiersAndARawCamelCasePayloadIsReadByName()
    {
        var raw = JsonSerializer.Serialize(new { formRecordId = Guid.CreateVersion7(), recordShareId = Guid.CreateVersion7(), recipientUserId = Guid.CreateVersion7(), recipientRole = "Manager", fieldItemIds = new[] { ScopeSentinel } });
        var share = SecurityCommandTimelineCatalog.BuildCommandEnvelope("CreateShare", raw, Guid.CreateVersion7(), "Rejected", null);
        Assert.Equal(new List<string> { "form_record_id", "record_share_id", "recipient", "recipient_role" }.Order(StringComparer.Ordinal), share.Details.Select(d => d.Key).Order(StringComparer.Ordinal));
        Assert.StartsWith("user:", share.Details.Single(d => d.Key == "recipient").Value, StringComparison.Ordinal);
        Assert.Equal(["share_scope"], share.WithheldFields);
        AssertNoSentinel(share);

        var request = SecurityCommandTimelineCatalog.BuildCommandEnvelope("RequestOrganizationMembership", UniversalPayload(), Guid.CreateVersion7(), "Rejected", null);
        Assert.DoesNotContain(request.Details, d => d.Key == "membership_request_id"); // a rejected request created no request
    }

    [Fact]
    public void ValuesOfTheWrongShapeAreDroppedAndAPrincipalNeedsExactlyOneSide()
    {
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["authority_user_id"] = "not-a-uuid SENT-SHAPE", ["role_name"] = "security auditor; SENT-SHAPE", ["reason_code"] = 42,
            ["form_record_id"] = Guid.CreateVersion7(), ["new_owner_user_id"] = Guid.CreateVersion7(), ["new_owner_team_id"] = Guid.CreateVersion7(),
            ["form_definition_id"] = Guid.CreateVersion7(), ["grant_user_ids"] = new object[] { Guid.CreateVersion7(), "SENT-SHAPE" }, ["revoke_team_ids"] = new[] { Guid.CreateVersion7() },
            ["protection_policy_revision"] = -1, ["plaintext_search_permitted"] = "yes",
        });
        var role = SecurityCommandTimelineCatalog.BuildCommandEnvelope("GrantSystemRole", payload, Guid.CreateVersion7(), "Effective", null);
        Assert.Empty(role.Details);
        var transfer = SecurityCommandTimelineCatalog.BuildCommandEnvelope("TransferOwnership", payload, Guid.CreateVersion7(), "Effective", null);
        Assert.Equal(["form_record_id"], transfer.Details.Select(d => d.Key)); // both owner sides set: neither is shown
        var users = SecurityCommandTimelineCatalog.BuildCommandEnvelope("UpdateFormUsers", payload, Guid.CreateVersion7(), "Effective", null);
        Assert.Equal(["form_definition_id", "removed_principals"], users.Details.Select(d => d.Key)); // one malformed element drops the list
        Assert.DoesNotContain(SecurityCommandTimelineCatalog.BuildCommandEnvelope("PublishFormSecurityRevision", payload, Guid.CreateVersion7(), "Effective", null).Details, d => d.Key == "protection_policy_revision");
        Assert.Empty(SecurityCommandTimelineCatalog.BuildCommandEnvelope("UpdateMandatoryPolicy", payload, Guid.CreateVersion7(), "Effective", null).Details);
        AssertNoSentinel(new object[] { role, transfer, users });
        Assert.Empty(SecurityCommandTimelineCatalog.BuildCommandEnvelope("GrantSystemRole", "not json SENT-X", Guid.CreateVersion7(), "Rejected", null).Details);
    }

    /// <summary>The stored shape of a real journal entry: the v1 security-journal envelope with the event under <c>body</c>.</summary>
    private static string V1Envelope(object body) => JsonSerializer.Serialize(new { envelope_format = "DynamicForms/SecurityJournal/v1", organization_id = Org, body });

    [Fact]
    public void AuditEnvelopesWithholdCountsDetailsAndIdentityIdentifiers()
    {
        Guid snapshot = Guid.CreateVersion7(), form = Guid.CreateVersion7(), user = Guid.CreateVersion7(), field = Guid.CreateVersion7(), correlation = Guid.CreateVersion7();
        var search = SecurityCommandTimelineCatalog.BuildAuditEnvelope("FieldSearchExecuted", V1Envelope(new
        {
            search_result_snapshot_id = snapshot, form_definition_id = form, authority_user_id = user, search_engine_name = "SENT-ENGINE",
            field_definition_ids = new[] { field }, operators = new List<string> { "equals" }, condition_count = 1, outcome = "COMPLETED",
            evaluated_record_count = 917, matched_record_count = 4, elapsed_milliseconds = 12, authorization_revision = 9, correlation_id = correlation,
        }));
        // Exactly the permitted details, each with its own value (so neither withheld count is shown under any key) ...
        Assert.Equal(
            new[]
            {
                "authorization_revision=9", "authority_user_id=" + user.ToString("D"), "condition_count=1", "correlation_id=" + correlation.ToString("D"),
                "field_definition_ids=[" + field.ToString("D") + "]", "form_definition_id=" + form.ToString("D"), "operators=[equals]", "outcome=COMPLETED",
                "search_result_snapshot_id=" + snapshot.ToString("D"),
            }.Order(StringComparer.Ordinal),
            search.Details.Select(DetailText).Order(StringComparer.Ordinal));
        Assert.Equal(["evaluated_record_count", "matched_record_count"], search.WithheldFields);
        // ... and neither withheld value appears anywhere in the envelope as a value of its own. The scan matches whole tokens,
        // so the digits of a random identifier (for example 01a206b2-97dc-737e-9178-...) are not mistaken for a disclosure.
        Assert.False(ExposesValue(search, "917"));
        Assert.False(ExposesValue(search, "4"));
        AssertNoSentinel(search);

        var refusal = SecurityCommandTimelineCatalog.BuildAuditEnvelope("EnvelopeCapabilityRefused", V1Envelope(new
        {
            evidence_schema_version = 1, refusal_code = "CLIENT_ENVELOPE_CAPABILITY_INSUFFICIENT", organization_id = Org, workload_name = "business-api", entry_point = "prepare",
            client_capability_level = 1, evidence_database_time_utc = T0, evidence_bucket_start_utc = T0, refusal_observed_on_utc = T0,
        }));
        Assert.Equal(["client_capability_level", "entry_point", "evidence_database_time_utc", "refusal_code", "workload_name"], refusal.Details.Select(d => d.Key).Order(StringComparer.Ordinal));
        Assert.Equal(["refusal_observed_on_utc"], refusal.WithheldFields);

        var rejected = SecurityCommandTimelineCatalog.BuildAuditEnvelope("OrganizationMembershipRejected", V1Envelope(new { membership_request_id = Guid.CreateVersion7(), reason = "SENT-FREE-REASON", platform_operator_id = Guid.CreateVersion7() }));
        Assert.Equal(["reason"], rejected.WithheldFields);
        AssertNoSentinel(rejected);
        Assert.Empty(SecurityCommandTimelineCatalog.BuildAuditEnvelope("SearchIndexRebuilt", "{\"x\":1}").Details); // not allowlisted
    }

    [Fact]
    public void TheWithheldValueScanFindsAnExposedValueAndIgnoresIdentifierDigits()
    {
        var identifier = Guid.ParseExact("01a206b2-97dc-737e-9178-8657c6d871d8", "D"); // the identifier that failed CI run 45112841517
        var clean = new SecurityCommandTimelineCatalog.Envelope([new SecurityCommandTimelineDetail("correlation_id", "Identifier", identifier.ToString("D"), null)], ["evaluated_record_count"]);
        Assert.False(ExposesValue(clean, "917"));
        Assert.False(ExposesValue(new SecurityCommandTimelineCatalog.Envelope([new SecurityCommandTimelineDetail("field_definition_ids", "IdentifierList", null, [identifier.ToString("D")])], []), "917"));

        Assert.True(ExposesValue(new SecurityCommandTimelineCatalog.Envelope([new SecurityCommandTimelineDetail("condition_count", "Count", "917", null)], []), "917"));
        Assert.True(ExposesValue(new SecurityCommandTimelineCatalog.Envelope([new SecurityCommandTimelineDetail("operators", "CodeList", null, ["equals", "917"])], []), "917"));
        Assert.True(ExposesValue(new { evaluated_record_count = 917 }, "917")); // a raw JSON number
        Assert.True(ExposesValue(new { note = "matched 917 records" }, "917")); // inside free text
    }

    private static string DetailText(SecurityCommandTimelineDetail detail) =>
        detail.Key + "=" + (detail.Values is null ? detail.Value : "[" + string.Join(",", detail.Values) + "]");

    /// <summary>
    /// True when <paramref name="value"/> appears in the serialized envelope as a whole token: not adjacent to a letter,
    /// digit, hyphen or dot, so the digits inside a UUID or a longer number are not a match.
    /// </summary>
    private static bool ExposesValue(object envelope, string value) =>
        Regex.IsMatch(JsonSerializer.Serialize(envelope), "(?<![0-9A-Za-z.-])" + Regex.Escape(value) + "(?![0-9A-Za-z.-])", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    // ---- the service: protocol, evidence, keyset, filters ----

    private SecurityCommand Command(string kind, string state, DateTimeOffset at, string? payload = null, long? committed = null, string? rejection = null, Guid? org = null) =>
        Add(new SecurityCommand
        {
            SecurityCommandId = Guid.CreateVersion7(), OrganizationId = org ?? Org, ActorIdentityBindingId = memberBinding, CommandKindName = kind, CommandIdempotencyKey = Guid.NewGuid().ToString("N"),
            CommandRequestDigest = new byte[32], ExpectedAuthorizationRevision = 1, CommittedAuthorizationRevision = committed, CommandStateName = state, CommandPayloadJson = payload ?? UniversalPayload(),
            CommandReasonText = "SENT-COMMAND-REASON-TEXT", RejectionReasonCode = rejection, CorrelationIdentifier = Guid.CreateVersion7(), CommandCreatedOn = at, CommandModifiedOn = at,
            CommandEffectiveOn = state == "Effective" ? at : null,
        });

    private SecurityCommand Add(SecurityCommand command)
    {
        store.Commands.Add(command);
        return command;
    }

    private SecurityJournalEntry Journal(string stream, long revision, string kind, DateTimeOffset at, object body, Guid? command = null, Guid? org = null)
    {
        var entry = new SecurityJournalEntry
        {
            SecurityJournalEntryId = Guid.CreateVersion7(), OrganizationId = org ?? Org, JournalStreamName = stream, JournalStreamRevision = revision, JournalEventKind = kind, SecurityCommandId = command,
            JournalEventBodyJson = JsonSerializer.Serialize(new
            {
                // the stored shape of every real entry: the v1 security-journal envelope, the event's fields under "body"
                envelope_format = "DynamicForms/SecurityJournal/v1", organization_id = org ?? Org, stream_name = stream, stream_revision = revision, event_kind = kind,
                previous_digest_hex = new string('0', 64), created_on_utc = at, body,
            }), JournalEventDigest = Enumerable.Repeat((byte)0xab, 32).ToArray(), ArchiveDeliveryState = ArchiveDeliveryStates.Pending, JournalCreatedOn = at,
        };
        store.Journal.Add(entry);
        return entry;
    }

    [Fact]
    public async Task APageIsReadInsideTheAccessTransactionAndServedOnlyWithACommittedEntryServedByTheAuthority()
    {
        Command("RegisterTeam", "Effective", T0, committed: 2);
        var request = Request();
        var page = await Service().ReadAsync(Actor(Auditor), request, Ct);

        var entry = Assert.Single(store.Journal, j => j.JournalEventKind == AuditAccessService.AuthorizedEventKind);
        Assert.Equal(entry.JournalStreamRevision, page.AuditJournalRevision);
        Assert.Equal(9, page.AuthorizationRevision);
        Assert.Equal(("authority", "not_attested"), (page.ServedBy, page.Delivery));
        using var body = JsonDocument.Parse(entry.JournalEventBodyJson);
        var root = body.RootElement.GetProperty("body");
        Assert.Equal("CommandTimelinePage", root.GetProperty("access_kind").GetString());
        Assert.Equal("authority", root.GetProperty("served_by").GetString());
        Assert.Equal("not_attested", root.GetProperty("delivery").GetString());
        Assert.Equal(SecurityCommandTimelineService.Validate(request).Digest, root.GetProperty("filter_digest").GetString());
        Assert.DoesNotContain(root.EnumerateObject(), p => p.Name.Contains("count", StringComparison.OrdinalIgnoreCase));
        Assert.Equal((200, 500), Assert.Single(store.AppliedAuditAccessTimeouts));
        Assert.Single(page.Items);
    }

    [Fact]
    public async Task ACommitFailureBusyLockOrDenialServesNoPage()
    {
        Command("RegisterTeam", "Effective", T0, committed: 2);
        store.FailNextCommit = new InvalidOperationException("simulated commit failure");
        Assert.Equal(AuthorityErrorCodes.AuditEvidenceUnavailable, (await Assert.ThrowsAsync<AuthorityException>(() => Service().ReadAsync(Actor(Auditor), Request(), Ct))).ErrorCode);

        store.NoWaitLockBusyCount = 3;
        Assert.Equal(AuthorityErrorCodes.AuditEvidenceBusy, (await Assert.ThrowsAsync<AuthorityException>(() => Service().ReadAsync(Actor(Auditor), Request(), Ct))).ErrorCode);

        var readsBefore = store.TimelineReads;
        var denied = await Assert.ThrowsAsync<AuthorityException>(() => Service().ReadAsync(Actor(Member), Request(), Ct));
        Assert.Equal((AuthorityErrorCodes.AuditViewForbidden, 403), (denied.ErrorCode, denied.HttpStatusCode));
        Assert.Equal(readsBefore, store.TimelineReads); // nothing was read for a denied caller

        // CodeQL #4 remediation: the AC-8.3 route cannot request the timeline mode (its former wire value is undefined and refused),
        // so an AC-8.3 allow alone never serves a timeline page.
        var direct = await Assert.ThrowsAsync<AuthorityException>(() => new AuditAccessService(store, new ViewAuditCheck(store, [Auditor]), new Unlimited(), new AuditAccessOptions(), telemetry, TimeProvider.System)
            .AuthorizeAsync(Actor(Auditor), new AuditAccessRequest(Org, (AuditAccessKind)2, new string('a', 64), 10, null, Guid.CreateVersion7()), Ct));
        Assert.Equal(422, direct.HttpStatusCode);
    }

    /// <summary>
    /// CodeQL #4 remediation: the timeline request carries no access kind, and a caller-supplied <c>access_kind</c> property is
    /// not bound; the mode recorded is always the server's CommandTimelinePage with served_by authority.
    /// </summary>
    [Fact]
    public async Task TheTimelineModeIsSetByTheServerAndACallerSuppliedKindIsNotBound()
    {
        Assert.DoesNotContain(typeof(SecurityCommandTimelineRequest).GetProperties(), p => p.PropertyType == typeof(AuditAccessKind) || (p.Name.Contains("Kind", StringComparison.Ordinal) && p.Name != "Kinds"));
        Command("RegisterTeam", "Effective", T0, committed: 2);
        var options = new JsonSerializerOptions();
        AuthorizationAuthority.Api.Composition.AuthorityJson.Configure(options);
        foreach (var supplied in new[] { "ExplorerPage", "EventDetail" })
        {
            var json = $$"""{"organization_id":"{{Org}}","after":null,"page_size":10,"kinds":null,"states":null,"from":null,"to":null,"correlation_id":"{{Guid.CreateVersion7()}}","access_kind":"{{supplied}}"}""";
            var request = JsonSerializer.Deserialize<SecurityCommandTimelineRequest>(json, options)!;
            var page = await Service().ReadAsync(Actor(Auditor), request, Ct);
            Assert.Equal("authority", page.ServedBy);
        }

        var recorded = store.Journal.Where(j => j.JournalEventKind == AuditAccessService.AuthorizedEventKind)
            .Select(j => JsonDocument.Parse(j.JournalEventBodyJson).RootElement.GetProperty("body"))
            .Select(b => (b.GetProperty("access_kind").GetString(), b.GetProperty("served_by").GetString())).ToList();
        Assert.Equal([("CommandTimelinePage", "authority"), ("CommandTimelinePage", "authority")], recorded);
    }

    [Fact]
    public async Task CommandRowsAreJournalBackedOrCommandRowOnlyWithoutDuplicates()
    {
        var committed = Command("TransferOwnership", "Effective", T0.AddSeconds(1), committed: 5);
        var recordId = JsonDocument.Parse(committed.CommandPayloadJson).RootElement.GetProperty("form_record_id").GetGuid();
        var previousOwner = Guid.CreateVersion7();
        Journal(JournalStreamNames.Authorization, 5, "SecurityCommandCommitted", T0.AddSeconds(1), new
        {
            security_command_id = committed.SecurityCommandId, change_reason = "SENT-JOURNAL-REASON", actor_issuer_uri = "https://SENT-ISSUER.invalid",
            relationship_delta = new[] { $"delete form_record:{recordId:D}#owner@user:{previousOwner:D}", $"touch form_record:{recordId:D}#owner@user:{Guid.CreateVersion7():D}" },
        }, committed.SecurityCommandId);
        var mismatched = Command("RegisterTeam", "Effective", T0.AddSeconds(2), committed: 6);
        Journal(JournalStreamNames.Authorization, 6, "SecurityCommandCommitted", T0.AddSeconds(2), new { }, Guid.CreateVersion7()); // names another command
        var rejectedShare = Command("CreateShare", "Rejected", T0.AddSeconds(3), rejection: "STALE_AUTHORIZATION_REVISION");
        var rejectedRole = Command("GrantSystemRole", "Rejected", T0.AddSeconds(4), rejection: "SELF_CHANGE_FORBIDDEN");
        Journal(JournalStreamNames.Audit, 40, "SecurityAuditorCommandRejected", T0.AddSeconds(4), new { security_command_id = rejectedRole.SecurityCommandId, rejection_reason = "SELF_CHANGE_FORBIDDEN" });
        var failed = Command("AddTeamMember", "FailedReconciliation", T0.AddSeconds(5), committed: 7);
        Command("RegisterRecord", "Effective", T0.AddSeconds(6), committed: 8); // excluded kind

        var page = await Service().ReadAsync(Actor(Auditor), Request(), Ct);
        var byId = page.Items.Where(i => i.CommandId is not null).ToDictionary(i => i.CommandId!.Value);

        Assert.Equal(("JournalBacked", "authorization", 5L), (byId[committed.SecurityCommandId].EvidenceBasis, byId[committed.SecurityCommandId].Evidence!.Stream, byId[committed.SecurityCommandId].Evidence!.StreamRevision));
        Assert.Equal("user:" + previousOwner.ToString("D"), byId[committed.SecurityCommandId].Details.Single(d => d.Key == "previous_owner").Value);
        Assert.Equal(Member, byId[committed.SecurityCommandId].ActorUserId);
        Assert.Equal(("CommandRowOnly", (SecurityCommandTimelineEvidence?)null), (byId[mismatched.SecurityCommandId].EvidenceBasis, byId[mismatched.SecurityCommandId].Evidence));
        Assert.Equal(("CommandRowOnly", "STALE_AUTHORIZATION_REVISION"), (byId[rejectedShare.SecurityCommandId].EvidenceBasis, byId[rejectedShare.SecurityCommandId].RejectionCode));
        Assert.Equal(("JournalBacked", "audit", 40L), (byId[rejectedRole.SecurityCommandId].EvidenceBasis, byId[rejectedRole.SecurityCommandId].Evidence!.Stream, byId[rejectedRole.SecurityCommandId].Evidence!.StreamRevision));
        Assert.Equal("FailedReconciliation", byId[failed.SecurityCommandId].CommandState);
        Assert.DoesNotContain(page.Items, i => i.Kind is "RegisterRecord" or "SecurityAuditorCommandRejected" or "SecurityCommandCommitted"); // excluded, attached as evidence, never duplicated
        Assert.Equal(5, page.Items.Count);
        AssertNoSentinel(page);
    }

    /// <summary>
    /// C-166 (Stage B finding F-M43-V1): event fields are read only from the v1 envelope's <c>body</c>. A flat or foreign-format
    /// entry shows no detail and links no rejected command; an envelope-level property is never read as an event field.
    /// </summary>
    [Fact]
    public async Task OnlyTheEnvelopeBodyIsReadAndAnEntryOfAnotherShapeShowsNothing()
    {
        var rejected = Command("GrantSystemRole", "Rejected", T0.AddSeconds(1), rejection: "SELF_CHANGE_FORBIDDEN");
        var flat = new SecurityJournalEntry
        {
            SecurityJournalEntryId = Guid.CreateVersion7(), OrganizationId = Org, JournalStreamName = JournalStreamNames.Audit, JournalStreamRevision = 50, JournalEventKind = "SecurityAuditorCommandRejected",
            JournalEventBodyJson = JsonSerializer.Serialize(new { security_command_id = rejected.SecurityCommandId }), JournalEventDigest = new byte[32], ArchiveDeliveryState = ArchiveDeliveryStates.Pending, JournalCreatedOn = T0.AddSeconds(1),
        };
        store.Journal.Add(flat);
        var alert = JsonSerializer.Serialize(new { envelope_format = "DynamicForms/SecurityJournal/v2", body = new { alert_kind = "Probe", severity = "High" } });
        Assert.Empty(SecurityCommandTimelineCatalog.BuildAuditEnvelope("SecurityAlertRaised", alert).Details);
        Assert.Empty(SecurityCommandTimelineCatalog.BuildAuditEnvelope("SecurityAlertRaised", JsonSerializer.Serialize(new { alert_kind = "Probe", severity = "High" })).Details);
        var outside = JsonSerializer.Serialize(new { envelope_format = "DynamicForms/SecurityJournal/v1", alert_kind = "Outside", body = new { severity = "High" } });
        Assert.Equal(["severity"], SecurityCommandTimelineCatalog.BuildAuditEnvelope("SecurityAlertRaised", outside).Details.Select(d => d.Key));

        var page = await Service().ReadAsync(Actor(Auditor), Request(), Ct);
        Assert.Equal(("CommandRowOnly", (SecurityCommandTimelineEvidence?)null), (page.Items.Single(i => i.CommandId == rejected.SecurityCommandId).EvidenceBasis, page.Items.Single(i => i.CommandId == rejected.SecurityCommandId).Evidence));
    }

    [Fact]
    public async Task TheKeysetMergesBothSourcesWithoutGapsOrRepeatsAtEqualTimes()
    {
        var expected = new List<string>();
        for (var i = 0; i < 7; i++)
        {
            var at = T0.AddSeconds(i / 3); // equal times across sources
            expected.Add(Command("RegisterTeam", "Effective", at, committed: 100 + i).SecurityCommandId.ToString("D"));
            expected.Add("A" + Journal(JournalStreamNames.Audit, 200 + i, "SecurityAlertRaised", at, new { alert_kind = "Probe", severity = "High" }).JournalStreamRevision);
        }

        var served = new List<string>();
        string? after = null;
        var kinds = new[] { "RegisterTeam", "SecurityAlertRaised" };
        do
        {
            var page = await Service().ReadAsync(Actor(Auditor), Request(pageSize: 3, after: after, kinds: kinds), Ct);
            served.AddRange(page.Items.Select(i => i.CommandId?.ToString("D") ?? "A" + i.Evidence!.StreamRevision));
            Assert.True(page.Items.Count <= 3);
            after = page.After;
        }
        while (after is not null);

        Assert.Equal(expected.Order(StringComparer.Ordinal), served.Order(StringComparer.Ordinal));
        Assert.Equal(served.Count, served.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task FiltersApplyInTheStoreAndInvalidFiltersAreRefused()
    {
        Command("RegisterTeam", "Effective", T0, committed: 2);
        Command("CreateShare", "Rejected", T0.AddMinutes(1), rejection: "FORBIDDEN");
        Journal(JournalStreamNames.Audit, 10, "SecurityAlertRaised", T0.AddMinutes(2), new { alert_kind = "Probe", severity = "High" });
        Command("RegisterTeam", "Effective", T0.AddMinutes(3), committed: 3, org: OtherOrg);

        var rejected = await Service().ReadAsync(Actor(Auditor), Request(states: ["Rejected"]), Ct);
        Assert.Equal(["CreateShare"], rejected.Items.Select(i => i.Kind)); // a state filter selects command rows only
        var ranged = await Service().ReadAsync(Actor(Auditor), Request(from: T0.AddSeconds(30), to: T0.AddMinutes(3), kinds: ["RegisterTeam", "SecurityAlertRaised"]), Ct);
        Assert.Equal(["SecurityAlertRaised"], ranged.Items.Select(i => i.Kind));
        var all = await Service().ReadAsync(Actor(Auditor), Request(kinds: ["RegisterTeam"]), Ct);
        Assert.Single(all.Items); // the other organization's command is never read

        foreach (var invalid in new[] { Request(kinds: ["RegisterRecord"]), Request(kinds: ["SecurityAuditorCommandRejected"]), Request(states: ["Done"]), Request(pageSize: 51), Request(after: "v1:1:C:not-a-guid"), Request(after: "SENT-TOKEN"), Request(from: T0, to: T0) })
        {
            Assert.Equal(422, (await Assert.ThrowsAsync<AuthorityException>(() => Service().ReadAsync(Actor(Auditor), invalid, Ct))).HttpStatusCode);
        }
    }

    [Fact]
    public void PositionsRoundTripAndTheDigestCoversTheFilterButNotThePosition()
    {
        var position = new Application.Common.Ports.TimelinePosition(T0.AddTicks(30), true, Guid.CreateVersion7(), 0);
        Assert.Equal(position, SecurityCommandTimelineService.DecodePosition(SecurityCommandTimelineService.EncodePosition(position)));
        var entry = new Application.Common.Ports.TimelinePosition(T0, false, Guid.Empty, 77);
        Assert.Equal(entry, SecurityCommandTimelineService.DecodePosition(SecurityCommandTimelineService.EncodePosition(entry)));
        Assert.Null(SecurityCommandTimelineService.DecodePosition("v1:1:A:0"));

        var first = SecurityCommandTimelineService.Validate(Request(kinds: ["RegisterTeam"])).Digest;
        Assert.Equal(first, SecurityCommandTimelineService.Validate(Request(kinds: ["RegisterTeam"], after: SecurityCommandTimelineService.EncodePosition(entry))).Digest);
        Assert.NotEqual(first, SecurityCommandTimelineService.Validate(Request(kinds: ["RegisterTeam"], states: ["Effective"])).Digest);
        Assert.Matches("^[0-9a-f]{64}$", first);
    }

    private sealed class ViewAuditCheck(InMemoryAuthorityStore store, HashSet<Guid> auditors) : IOrganizationPermissionCheck
    {
        public Task<PermissionDecision> CheckAsync(VerifiedActor actor, PermissionCheckRequest request, Guid correlationId, CancellationToken cancellationToken)
        {
            Assert.Equal(RequestedAction.ViewAudit, request.RequestedAction);
            var state = store.Organizations.Single(o => o.OrganizationId == request.OrganizationId);
            var binding = store.Bindings.Single(b => b.OrganizationId == request.OrganizationId && b.IdentitySubjectIdentifier == actor.SubjectIdentifier);
            var allowed = auditors.Contains(binding.AuthorityUserId);
            return Task.FromResult(new PermissionDecision(allowed, allowed ? null : AuthorityErrorCodes.Forbidden, state.AuthorizationRevision, state.ProjectionAppliedRevision, null, correlationId));
        }
    }

    private sealed class Unlimited : IAuditAccessRateLimiter
    {
        public bool TryAcquire(Guid organizationId, VerifiedActor actor) => true;
    }
}
