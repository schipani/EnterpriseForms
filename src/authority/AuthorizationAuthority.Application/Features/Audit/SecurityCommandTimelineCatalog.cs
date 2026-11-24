using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AuthorizationAuthority.Contracts.V1;

namespace AuthorizationAuthority.Application.Features.Audit;

/// <summary>
/// C8 M4-3 (AC-8.2, contract §5.2 as amended by C-165): what the security-command timeline may show, and nothing else.
/// <list type="bullet">
/// <item>Every command kind is classified exactly once: included with an allowlist, or excluded (record registration, child
/// unlinking, form registration and publication, and the operator-plane command rows whose audit-stream entries already appear).
/// A new kind that is not classified fails the exhaustive classification test and is never shown.</item>
/// <item>Audit-stream entries are shown only for allowlisted kinds. The four <c>*CommandRejected</c> kinds are never shown on
/// their own: they are the journal evidence of their rejected command row (no duplicate items).</item>
/// <item>Each shown field is read by name from an explicit list and must have its declared shape (identifier, code, count, flag,
/// timestamp, digest, or lists of those); a value of any other shape is dropped. Unknown fields are dropped silently. Fields of a
/// prohibited class (free-text reasons and notes, step-up evidence, share scopes, identity identifiers, rejection details,
/// matched and evaluated counts) are reported by a fixed name in <c>WithheldFields</c>, never with their value or length.</item>
/// </list>
/// </summary>
public static partial class SecurityCommandTimelineCatalog
{
    public const string CommandSource = "Command";

    public const string AuditStreamSource = "AuditStream";

    public const string JournalBacked = "JournalBacked";

    public const string CommandRowOnly = "CommandRowOnly";

    public const string ReportedByAuthority = "Authority";

    public const string ReportedByBusinessRuntime = "BusinessRuntime";

    private const int MaximumListLength = 200;

    public enum Shape
    {
        Identifier,
        IdentifierList,
        Principal,
        PrincipalList,
        Code,
        CodeList,
        Count,
        Flag,
        Timestamp,
        Digest,
    }

    /// <summary>A shown field: its output name, its shape and where it comes from (one key, or a user key and a team key for principals).</summary>
    public sealed record FieldRule(string Key, Shape Shape, string Source, string? TeamSource = null);

    public sealed record KindRule(IReadOnlyList<FieldRule> Fields, bool ReportedByBusinessRuntime = false);

    private static FieldRule Id(string key, string? source = null) => new(key, Shape.Identifier, source ?? key);

    private static FieldRule Ids(string key, string? source = null) => new(key, Shape.IdentifierList, source ?? key);

    private static FieldRule Code(string key, string? source = null) => new(key, Shape.Code, source ?? key);

    private static FieldRule Codes(string key, string? source = null) => new(key, Shape.CodeList, source ?? key);

    private static FieldRule Count(string key, string? source = null) => new(key, Shape.Count, source ?? key);

    private static FieldRule Flag(string key, string? source = null) => new(key, Shape.Flag, source ?? key);

    private static FieldRule Time(string key, string? source = null) => new(key, Shape.Timestamp, source ?? key);

    private static FieldRule Digest(string key, string? source = null) => new(key, Shape.Digest, source ?? key);

    private static FieldRule Principal(string key, string userSource, string teamSource) => new(key, Shape.Principal, userSource, teamSource);

    private static FieldRule Principals(string key, string userSource, string teamSource) => new(key, Shape.PrincipalList, userSource, teamSource);

    private static KindRule Rule(params FieldRule[] fields) => new(fields);

    /// <summary>Included command kinds and their envelopes (contract §5.2; R-2: UpdateShareScope and PublishFormSecurityRevision).</summary>
    public static IReadOnlyDictionary<string, KindRule> CommandKinds { get; } = new Dictionary<string, KindRule>(StringComparer.Ordinal)
    {
        [nameof(SecurityCommandKind.GrantSystemRole)] = Rule(Id("target_user_id", "authority_user_id"), Code("role_name"), Code("reason_code")),
        [nameof(SecurityCommandKind.RevokeSystemRole)] = Rule(Id("target_user_id", "authority_user_id"), Code("role_name"), Code("reason_code")),
        [nameof(SecurityCommandKind.GrantOrganizationRole)] = Rule(Id("target_user_id", "authority_user_id"), Code("role_name"), Id("proposal_id")),
        [nameof(SecurityCommandKind.RevokeOrganizationRole)] = Rule(Id("target_user_id", "authority_user_id"), Code("role_name"), Id("proposal_id")),
        [nameof(SecurityCommandKind.ProposeOrganizationOwnerChange)] = Rule(Id("target_user_id", "authority_user_id"), Code("proposed_change")),
        [nameof(SecurityCommandKind.DisableIdentityBinding)] = Rule(Id("target_user_id", "authority_user_id"), Id("proposal_id")),
        [nameof(SecurityCommandKind.EnableIdentityBinding)] = Rule(Id("target_user_id", "authority_user_id"), Id("proposal_id")),
        [nameof(SecurityCommandKind.RequestOrganizationMembership)] = Rule(Code("requested_initial_role", "initial_role_name")),
        [nameof(SecurityCommandKind.CancelOrganizationMembershipRequest)] = Rule(Id("membership_request_id")),
        [nameof(SecurityCommandKind.ReplaceOrganizationMembershipRequest)] = Rule(Id("replaced_membership_request_id", "membership_request_id"), Code("requested_initial_role", "initial_role_name")),
        [nameof(SecurityCommandKind.RegisterTeam)] = Rule(Id("team_id")),
        [nameof(SecurityCommandKind.AddTeamMember)] = Rule(Id("team_id"), Id("member_user_id")),
        [nameof(SecurityCommandKind.RemoveTeamMember)] = Rule(Id("team_id"), Id("member_user_id")),
        [nameof(SecurityCommandKind.UpdateFormUsers)] = Rule(Id("form_definition_id"), Principals("added_principals", "grant_user_ids", "grant_team_ids"), Principals("removed_principals", "revoke_user_ids", "revoke_team_ids")),
        [nameof(SecurityCommandKind.UpdateMandatoryPolicy)] = Rule(Code("minimum_field_classification"), Flag("plaintext_internal_fields_permitted"), Flag("plaintext_downgrade_permitted"), Flag("plaintext_search_permitted")),
        [nameof(SecurityCommandKind.ReclassifyField)] = Rule(Id("form_definition_id"), Id("field_definition_id"), Code("target_classification")),
        [nameof(SecurityCommandKind.PublishFormSecurityRevision)] = Rule(Id("form_definition_id"), Id("field_definition_id"), Code("required_protection_mode"), Code("field_classification"), Count("protection_policy_revision")),
        [nameof(SecurityCommandKind.ApproveFieldSearch)] = Rule(Id("form_definition_id"), Id("field_definition_id")),
        [nameof(SecurityCommandKind.WithdrawFieldSearch)] = Rule(Id("form_definition_id"), Id("field_definition_id")),
        [nameof(SecurityCommandKind.UpdateFieldSearchers)] = Rule(Id("form_definition_id"), Principals("added_searchers", "grant_user_ids", "grant_team_ids"), Principals("removed_searchers", "revoke_user_ids", "revoke_team_ids")),
        [nameof(SecurityCommandKind.TransferOwnership)] = Rule(Id("form_record_id"), Principal("new_owner", "new_owner_user_id", "new_owner_team_id")),
        [nameof(SecurityCommandKind.CreateShare)] = Rule(Id("form_record_id"), Id("record_share_id"), Principal("recipient", "recipient_user_id", "recipient_team_id"), Code("recipient_role")),
        [nameof(SecurityCommandKind.RevokeShare)] = Rule(Id("form_record_id"), Id("record_share_id")),
        [nameof(SecurityCommandKind.UpdateShareScope)] = Rule(Id("form_record_id"), Id("record_share_id")),
    };

    /// <summary>
    /// Command kinds never shown as command rows: record-scoped registration and unlinking, form registration and publication
    /// (§5.2), and the operator-plane kinds whose allowlisted audit-stream entries already record them (R-2, no duplicates).
    /// </summary>
    public static IReadOnlySet<string> ExcludedCommandKinds { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(SecurityCommandKind.RegisterRecord),
        nameof(SecurityCommandKind.UnlinkChildRecord),
        nameof(SecurityCommandKind.RegisterFormDefinition),
        nameof(SecurityCommandKind.RegisterPublishedFormVersion),
        nameof(SecurityCommandKind.PrepareFormVersionPublication),
        nameof(SecurityCommandKind.ResolveFormVersionPublication),
        "ApproveOrganizationMembership",
        "BootstrapOrganization",
        "ProvisionFirstSecurityAdministrator",
        "RecoverSecurityAdministrator",
        "InitializeOrganization",
        "RecoverOrganizationOwners",
    };

    /// <summary>Allowlisted audit-stream kinds and their envelopes (contract §5.2; PD-09 option B for EnvelopeCapabilityRefused).</summary>
    public static IReadOnlyDictionary<string, KindRule> AuditKinds { get; } = new Dictionary<string, KindRule>(StringComparer.Ordinal)
    {
        [AuditAccessService.AuthorizedEventKind] = Rule(Code("access_kind"), Id("actor_authority_user_id"), Count("authorization_revision"), Count("projection_revision"), Digest("filter_digest"), Count("page_size"), Id("audit_event_id"), Code("delivery"), Code("served_by"), Id("correlation_id")),
        ["FieldSearchExecuted"] = Rule(Id("search_catalogue_snapshot_id"), Id("search_result_snapshot_id"), Id("form_definition_id"), Id("authority_user_id"), Ids("field_definition_ids"), Codes("operators"), Count("condition_count"), Code("outcome"), Count("authorization_revision"), Id("correlation_id")),
        [Content.CapabilityRefusalEvidence.EventKind] = new KindRule([Code("refusal_code"), Code("workload_name"), Code("entry_point"), Count("client_capability_level"), Time("evidence_database_time_utc")], ReportedByBusinessRuntime: true),
        ["SecurityAlertRaised"] = Rule(Code("alert_kind"), Code("severity"), Id("operation_id"), Id("security_command_id"), Id("correlation_id")),
        ["ProtectionReconciliationAttested"] = Rule(Id("protection_reconciliation_attestation_id"), Id("security_command_id"), Id("form_definition_id"), Id("field_definition_id"), Code("protection_direction"), Flag("attestation_complete"), Code("attestation_incomplete_reason"), Count("authorization_revision"), Count("content_integrity_revision"), Time("attested_on"), Code("workload_name"), Id("correlation_id")),
        ["SecurityAdministratorFirstAppointed"] = Rule(Id("security_command_id"), Id("operation_id"), Id("target_authority_user_id"), Code("role_name"), Code("operator_workload"), Code("approval_assurance"), Count("authorization_revision"), Flag("alert_raised"), Id("correlation_id")),
        ["SecurityAdministratorRecovered"] = Rule(Id("security_command_id"), Id("operation_id"), Id("target_authority_user_id"), Code("role_name"), Code("operator_workload"), Code("approval_assurance"), Count("authorization_revision"), Flag("alert_raised"), Id("correlation_id")),
        ["SecurityAdministratorProvisioningRejected"] = Rule(Id("operation_id"), Code("operation_kind"), Id("target_authority_user_id"), Code("operator_workload"), Code("approval_assurance"), Code("rejection_reason"), Id("correlation_id")),
        ["OrganizationMembershipApproved"] = Rule(Id("security_command_id"), Id("membership_request_id"), Id("new_authority_user_id"), Code("initial_role_name"), Id("requested_by_authority_user_id"), Code("operator_workload"), Code("approval_assurance"), Count("authorization_revision"), Id("correlation_id")),
        ["OrganizationMembershipApprovalRejected"] = Rule(Id("membership_request_id"), Code("operator_workload"), Code("approval_assurance"), Code("rejection_reason"), Id("correlation_id")),
        ["OrganizationMembershipRejected"] = Rule(Id("membership_request_id"), Code("operator_workload"), Id("platform_operator_id"), Id("correlation_id")),
        ["PlatformOperatorApprovalRecorded"] = Rule(Id("platform_operator_approval_id"), Id("membership_request_id"), Id("platform_operator_id"), Id("correlation_id")),
        ["PlatformOperatorApprovalWithdrawn"] = Rule(Id("membership_request_id"), Id("platform_operator_id"), Id("correlation_id")),
        ["PlatformOperatorDecisionRefused"] = Rule(Id("platform_operator_id"), Id("membership_request_id"), Code("refusal_code"), Id("correlation_id")),
        ["PlatformOperatorApprovalsInvalidated"] = Rule(Id("security_command_id"), Code("command_kind"), Id("correlation_id")),
        ["OrganizationInitialized"] = Rule(Id("security_command_id"), Id("organization_onboarding_request_id"), Ids("initial_owner_user_ids"), Ids("approved_by_platform_operator_ids"), Id("initialized_by_platform_operator_id"), Code("approval_assurance"), Id("correlation_id")),
        ["OwnerRecoveryOpened"] = Rule(Id("organization_owner_recovery_case_id"), Ids("declared_unavailable_user_ids"), Ids("replacement_user_ids"), Time("waiting_period_ends_on"), Id("correlation_id")),
        ["OwnerRecoveryObjected"] = Rule(Id("organization_owner_recovery_case_id"), Id("objected_by_authority_user_id"), Id("correlation_id")),
        ["OwnerRecoveryExecuted"] = Rule(Id("organization_owner_recovery_case_id"), Id("security_command_id"), Ids("replacement_user_ids"), Ids("new_binding_user_ids"), Ids("approved_by_platform_operator_ids"), Id("executed_by_platform_operator_id"), Time("probation_ends_on"), Code("approval_assurance"), Id("correlation_id")),
        ["OwnerRecoveryInvalidated"] = Rule(Id("organization_owner_recovery_case_id"), Code("closure"), Id("correlation_id")),
        ["PlatformOrganizationDirectoryViewed"] = Rule(Id("platform_operator_id"), Codes("disclosed"), Id("correlation_id")),
        ["PlatformMembershipRequestViewed"] = Rule(Id("platform_operator_id"), Id("membership_request_id"), Codes("disclosed"), Id("correlation_id")),
        ["PlatformOwnerRecoveryCaseViewed"] = Rule(Id("organization_owner_recovery_case_id"), Id("platform_operator_id"), Codes("disclosed"), Id("correlation_id")),
        ["PlatformOwnerRecoveryMembersViewed"] = Rule(Id("platform_operator_id"), Codes("disclosed"), Count("returned_count"), Id("correlation_id")),
    };

    /// <summary>Audit-stream rejection entries: the journal evidence of their rejected command row, never items of their own.</summary>
    public static IReadOnlySet<string> RejectionEvidenceKinds { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "FieldSearchCommandRejected",
        "SecurityAuditorCommandRejected",
        "SecurityAdministratorCommandRejected",
        "MembershipCommandRejected",
    };

    /// <summary>Command states a filter may name.</summary>
    public static IReadOnlySet<string> CommandStates { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "Pending", "CommittedAwaitingProjection", "Effective", "Rejected", "FailedReconciliation",
    };

    /// <summary>
    /// Prohibited field classes, by normalized name (lower case, no underscores) → the fixed name reported as withheld. The reported
    /// name always comes from this table, never from the stored key, so a stored key can never carry content into a response.
    /// </summary>
    private static readonly Dictionary<string, string> Withheld = new(StringComparer.Ordinal)
    {
        ["reason"] = "reason",
        ["changereason"] = "change_reason",
        ["reasonnote"] = "reason_note",
        ["reasonnoteenvelope"] = "reason_note",
        ["stepupevidence"] = "step_up_evidence",
        ["displaynamehint"] = "display_name_hint",
        ["approvalreference"] = "approval_reference",
        ["withdrawalreason"] = "withdrawal_reason",
        ["justification"] = "justification",
        ["incidentreference"] = "incident_reference",
        ["notification"] = "notification",
        ["rejectiondetail"] = "rejection_detail",
        ["fielditemids"] = "share_scope",
        ["documentitemids"] = "share_scope",
        ["shareexpireson"] = "share_expiry",
        ["evaluatedrecordcount"] = "evaluated_record_count",
        ["matchedrecordcount"] = "matched_record_count",
        ["issueruri"] = "identity_identifiers",
        ["subjectidentifier"] = "identity_identifiers",
        ["targetissueruri"] = "identity_identifiers",
        ["targetsubjectidentifier"] = "identity_identifiers",
        ["operatorissueruri"] = "identity_identifiers",
        ["operatorsubjectidentifier"] = "identity_identifiers",
        ["refusalobservedonutc"] = "refusal_observed_on_utc",
        ["coveragestatement"] = "coverage_statement",
    };

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_.:-]{0,79}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex DigestPattern();

    [GeneratedRegex(@"^(?:delete|delete_relationship|operation_delete)\s+form_record:([0-9a-fA-F-]{36})#owner@(user|team):([0-9a-fA-F-]{36})(?:#[a-z_]+)?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex RemovedOwnerEdge();

    /// <summary>Whether a command kind is shown on the timeline.</summary>
    public static bool IsIncludedCommand(string kind) => CommandKinds.ContainsKey(kind);

    /// <summary>The included kinds a filter may name (commands and audit-stream entries).</summary>
    public static bool IsFilterableKind(string kind) => CommandKinds.ContainsKey(kind) || AuditKinds.ContainsKey(kind);

    /// <summary>The command envelope fields of one command row, from its payload (normalized when committed, raw when rejected).</summary>
    public static Envelope BuildCommandEnvelope(string kind, string payloadJson, Guid commandId, string commandState, string? committedJournalBodyJson)
    {
        if (!CommandKinds.TryGetValue(kind, out var rule))
        {
            return new Envelope([], []); // never reached for listed rows; an unknown kind shows nothing
        }

        var payload = Parse(payloadJson);
        var (details, withheld) = Apply(rule, payload);

        // Derived identifiers: a new membership request's id is its command id (committed or pending rows only).
        if (kind is nameof(SecurityCommandKind.RequestOrganizationMembership) or nameof(SecurityCommandKind.ReplaceOrganizationMembershipRequest)
            && commandState is not "Rejected")
        {
            details.Insert(0, new SecurityCommandTimelineDetail("membership_request_id", nameof(Shape.Identifier), commandId.ToString("D"), null));
        }

        if (kind is nameof(SecurityCommandKind.ProposeOrganizationOwnerChange) && commandState is not "Rejected")
        {
            details.Add(new SecurityCommandTimelineDetail("proposal_id", nameof(Shape.Identifier), commandId.ToString("D"), null));
        }

        // The previous owner is not part of the payload; for a committed transfer it is the owner edge the journal entry removed.
        if (kind is nameof(SecurityCommandKind.TransferOwnership) && committedJournalBodyJson is not null
            && details.FirstOrDefault(d => d.Key == "form_record_id")?.Value is { } recordId
            && PreviousOwner(committedJournalBodyJson, recordId) is { } previous)
        {
            details.Add(new SecurityCommandTimelineDetail("previous_owner", nameof(Shape.Principal), previous, null));
        }

        return new Envelope(details, withheld);
    }

    /// <summary>The envelope fields of one allowlisted audit-stream entry, from its body.</summary>
    public static Envelope BuildAuditEnvelope(string kind, string bodyJson)
    {
        if (!AuditKinds.TryGetValue(kind, out var rule))
        {
            return new Envelope([], []);
        }

        var (details, withheld) = Apply(rule, ParseEventBody(bodyJson));
        return new Envelope(details, withheld);
    }

    /// <summary>The command id an audit-stream entry names (rejection evidence linking), or null.</summary>
    public static Guid? NamedCommandId(string bodyJson) =>
        ParseEventBody(bodyJson).TryGetValue("securitycommandid", out var value) && value.ValueKind == JsonValueKind.String && Guid.TryParseExact(value.GetString(), "D", out var id) ? id : null;

    public sealed record Envelope(IReadOnlyList<SecurityCommandTimelineDetail> Details, IReadOnlyList<string> WithheldFields);

    private static (List<SecurityCommandTimelineDetail> Details, List<string> Withheld) Apply(KindRule rule, Dictionary<string, JsonElement> source)
    {
        var details = new List<SecurityCommandTimelineDetail>();
        foreach (var field in rule.Fields)
        {
            if (Render(field, source) is { } detail)
            {
                details.Add(detail);
            }
        }

        var withheld = source.Keys.Where(Withheld.ContainsKey).Select(k => Withheld[k]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return (details, withheld);
    }

    private static SecurityCommandTimelineDetail? Render(FieldRule field, Dictionary<string, JsonElement> source)
    {
        source.TryGetValue(Normalize(field.Source), out var value);
        switch (field.Shape)
        {
            case Shape.Identifier:
                return AsId(value) is { } id ? Single(field, id) : null;
            case Shape.IdentifierList:
                return AsList(value, AsId) is { } ids ? Many(field, ids) : null;
            case Shape.Code:
                return AsCode(value) is { } code ? Single(field, code) : null;
            case Shape.CodeList:
                return AsList(value, AsCode) is { } codes ? Many(field, codes) : null;
            case Shape.Count:
                return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var count) && count >= 0 ? Single(field, count.ToString(CultureInfo.InvariantCulture)) : null;
            case Shape.Flag:
                return value.ValueKind is JsonValueKind.True or JsonValueKind.False ? Single(field, value.GetBoolean() ? "true" : "false") : null;
            case Shape.Timestamp:
                return value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
                    ? Single(field, at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)) : null;
            case Shape.Digest:
                return value.ValueKind == JsonValueKind.String && value.GetString() is { } hex && DigestPattern().IsMatch(hex) ? Single(field, hex) : null;
            case Shape.Principal:
            {
                var user = AsId(value);
                source.TryGetValue(Normalize(field.TeamSource!), out var teamValue);
                var team = AsId(teamValue);
                return (user, team) switch
                {
                    ({ } u, null) => Single(field, "user:" + u),
                    (null, { } t) => Single(field, "team:" + t),
                    _ => null, // none, both, or malformed: nothing is shown
                };
            }

            case Shape.PrincipalList:
            {
                source.TryGetValue(Normalize(field.TeamSource!), out var teamValue);
                if (value.ValueKind == JsonValueKind.Undefined && teamValue.ValueKind == JsonValueKind.Undefined)
                {
                    return null;
                }

                var users = value.ValueKind == JsonValueKind.Undefined ? [] : AsList(value, AsId);
                var teams = teamValue.ValueKind == JsonValueKind.Undefined ? [] : AsList(teamValue, AsId);
                if (users is null || teams is null || users.Count + teams.Count > MaximumListLength)
                {
                    return null;
                }

                return Many(field, [.. users.Select(u => "user:" + u), .. teams.Select(t => "team:" + t)]);
            }

            default:
                return null;
        }
    }

    private static SecurityCommandTimelineDetail Single(FieldRule field, string value) => new(field.Key, field.Shape.ToString(), value, null);

    private static SecurityCommandTimelineDetail Many(FieldRule field, List<string> values) => new(field.Key, field.Shape.ToString(), null, values);

    private static string? AsId(JsonElement value) =>
        value.ValueKind == JsonValueKind.String && Guid.TryParseExact(value.GetString(), "D", out var id) ? id.ToString("D") : null;

    private static string? AsCode(JsonElement value) =>
        value.ValueKind == JsonValueKind.String && value.GetString() is { } code && CodePattern().IsMatch(code) ? code : null;

    /// <summary>All elements of the declared shape, or null (the whole field is dropped) when any element is not.</summary>
    private static List<string>? AsList(JsonElement value, Func<JsonElement, string?> element)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > MaximumListLength)
        {
            return null;
        }

        var items = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (element(item) is not { } rendered)
            {
                return null;
            }

            items.Add(rendered);
        }

        return items;
    }

    private static string? PreviousOwner(string journalBodyJson, string recordId)
    {
        if (!ParseEventBody(journalBodyJson).TryGetValue("relationshipdelta", out var delta) || delta.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in delta.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && RemovedOwnerEdge().Match(item.GetString()!) is { Success: true } match
                && string.Equals(match.Groups[1].Value, recordId, StringComparison.OrdinalIgnoreCase)
                && Guid.TryParseExact(match.Groups[3].Value, "D", out var subject))
            {
                return match.Groups[2].Value.ToLowerInvariant() + ":" + subject.ToString("D");
            }
        }

        return null;
    }

    /// <summary>
    /// The event fields of a stored journal entry: the stored JSON is the security-journal envelope
    /// (<c>envelope_format</c>, organization, stream, revision, kind, previous digest, time, <c>body</c>), and the event's fields are
    /// the properties of its <c>body</c> object only. Anything else (another format, a flat body, no body object, unreadable JSON)
    /// yields nothing, so an entry whose shape is not the v1 envelope shows no detail and links no command.
    /// </summary>
    private static Dictionary<string, JsonElement> ParseEventBody(string json)
    {
        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("envelope_format", out var format) || format.ValueKind != JsonValueKind.String
                || !string.Equals(format.GetString(), Journal.Verification.JournalEnvelopeFormats.SecurityJournalV1, StringComparison.Ordinal)
                || !root.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Object)
            {
                return properties;
            }

            foreach (var property in body.EnumerateObject())
            {
                properties.TryAdd(Normalize(property.Name), property.Value.Clone());
            }
        }
        catch (JsonException)
        {
            // an unreadable entry shows nothing
        }

        return properties;
    }

    /// <summary>Top-level properties of a command payload by normalized name; a payload that is not a JSON object yields nothing.</summary>
    private static Dictionary<string, JsonElement> Parse(string json)
    {
        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return properties;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                properties.TryAdd(Normalize(property.Name), property.Value.Clone());
            }
        }
        catch (JsonException)
        {
            // an unreadable body shows nothing
        }

        return properties;
    }

    private static string Normalize(string name) => name.Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
}
