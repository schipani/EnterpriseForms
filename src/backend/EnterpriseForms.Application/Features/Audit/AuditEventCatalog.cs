using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using EnterpriseForms.Domain.Features.Audit;

namespace EnterpriseForms.Application.Features.Audit;

/// <summary>How an allowlisted metadata value is rendered and the shape it must have (anything else is withheld).</summary>
public enum AuditMetadataKind
{
    /// <summary>A uuid, a lower-case hex digest or an integer id.</summary>
    Identifier,

    /// <summary>An integer, or the number of items of an array (never the items).</summary>
    Count,

    /// <summary>A short status word or a boolean.</summary>
    Status,

    /// <summary>A structured code (classification, role, reason code, IANA zone, ISO time …), or a short list of codes.</summary>
    Code,

    /// <summary>A directory display name: form, team or user (D8-67a). Never a field label.</summary>
    DirectoryName,
}

/// <summary>Where an event's description comes from.</summary>
public enum AuditDescriptionPolicy
{
    /// <summary>The catalog's fixed sentence; the stored text is never shown.</summary>
    Catalog,

    /// <summary>The stored text, only when it matches the entry's exact pattern (composed from codes); otherwise the catalog sentence.</summary>
    StoredSafe,
}

public sealed record AuditMetadataRule(string Key, string Label, AuditMetadataKind Kind);

public sealed record AuditWithheldRule(string Key, string Label, string Policy);

public sealed record AuditCatalogEntry(string Action, string Label, string Description, AuditDescriptionPolicy DescriptionPolicy, IReadOnlyList<AuditMetadataRule> Shown, IReadOnlyList<AuditWithheldRule> Withheld, Regex? StoredDescriptionPattern = null);

public sealed record RenderedAuditMetadataItem(string Key, string Label, AuditMetadataKind Kind, string Value);

public sealed record RenderedAuditWithheldItem(string Label, string Policy);

public sealed record RenderedAuditEvent(bool Recognized, string ActionLabel, string Description, IReadOnlyList<RenderedAuditMetadataItem> Metadata, IReadOnlyList<RenderedAuditWithheldItem> Withheld);

/// <summary>
/// C8 M4-2 redaction catalog (AC-8 §6.3, owner decisions D8-67 and D8-67a). The audit explorer renders an event only through this
/// catalog: each action has a label, a description policy and an explicit metadata allowlist. Only allowlisted keys whose value
/// has the declared shape are rendered; listed sensitive keys appear as "withheld by policy" (label only, never the value or its
/// length); every other key is dropped. An action not in the catalog (forged or future) renders as the envelope only.
/// Shown: directory display names (form, team, user), structured codes, identifiers and counts. Withheld: free text (reasons,
/// notes, hints, approval references), field and group keys, field labels, option text, design diffs (counts only) and record
/// scopes. Matched and evaluated search counts never appear here (they are not in the business projection). Metadata keys are
/// matched case-insensitively because service-written events serialize with default (Pascal-case) names.
/// </summary>
public static partial class AuditEventCatalog
{
    public const string FreeTextPolicy = "Free text — withheld by policy (D8-67)";
    public const string DesignLabelPolicy = "Field or group key, label or option text — withheld by policy (D8-67a)";
    public const string DesignDiffPolicy = "Design or content change detail — withheld by policy (D8-67a)";
    public const string ScopePolicy = "Record content scope — withheld by policy (D8-67)";
    public const string ShapePolicy = "Value not in the expected form — withheld";

    public const string UnrecognizedLabel = "Unrecognized action";
    public const string UnrecognizedDescription = "This action is not in the redaction catalog, so only the event envelope is shown. It may be forged or written by a newer version.";

    // Built on first use, after every static rule field below has been initialized.
    private static readonly Lazy<Dictionary<string, AuditCatalogEntry>> Catalog = new(Build);

    public static IReadOnlyDictionary<string, AuditCatalogEntry> Entries => Catalog.Value;

    public static AuditCatalogEntry? Find(string action) => action is not null && Catalog.Value.TryGetValue(action, out var entry) ? entry : null;

    /// <summary>Label for a list row (never metadata).</summary>
    public static string LabelFor(string action) => Find(action)?.Label ?? UnrecognizedLabel;

    /// <summary>Renders an event for the detail view: catalog description, allowlisted metadata and withheld labels only.</summary>
    public static RenderedAuditEvent Render(AuditEvent auditEvent)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        var entry = Find(auditEvent.EventAction);
        if (entry is null)
        {
            return new RenderedAuditEvent(false, UnrecognizedLabel, UnrecognizedDescription, [], []);
        }

        var values = Flatten(auditEvent.EventMetadataJson);
        var shown = new List<RenderedAuditMetadataItem>();
        var withheld = new List<RenderedAuditWithheldItem>();
        foreach (var rule in entry.Shown)
        {
            if (!values.TryGetValue(rule.Key, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                continue;
            }

            if (Format(rule.Kind, value) is { } text)
            {
                shown.Add(new RenderedAuditMetadataItem(rule.Key, rule.Label, rule.Kind, text));
            }
            else
            {
                withheld.Add(new RenderedAuditWithheldItem(rule.Label, ShapePolicy));
            }
        }

        foreach (var rule in entry.Withheld)
        {
            if (values.TryGetValue(rule.Key, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                withheld.Add(new RenderedAuditWithheldItem(rule.Label, rule.Policy));
            }
        }

        var description = entry.DescriptionPolicy == AuditDescriptionPolicy.StoredSafe && entry.StoredDescriptionPattern is { } pattern && pattern.IsMatch(auditEvent.EventDescription ?? string.Empty)
            ? auditEvent.EventDescription!
            : entry.Description;
        return new RenderedAuditEvent(true, entry.Label, description, shown, withheld);
    }

    /// <summary>Top-level properties and, for nested objects, one level of "parent.child" paths; case-insensitive. Invalid JSON yields nothing.</summary>
    private static Dictionary<string, JsonElement> Flatten(string? json)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json) || json.Length > 65_536)
        {
            return values;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return values;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                values.TryAdd(property.Name, property.Value.Clone());
                if (property.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var child in property.Value.EnumerateObject())
                    {
                        values.TryAdd(property.Name + "." + child.Name, child.Value.Clone());
                    }
                }
            }
        }
        catch (JsonException)
        {
            values.Clear();
        }

        return values;
    }

    [GeneratedRegex("^[0-9a-f]{16,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex HexPattern();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_ -]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex StatusPattern();

    [GeneratedRegex("^[A-Za-z0-9_.:/+-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();

    private static string? Format(AuditMetadataKind kind, JsonElement value)
    {
        switch (kind)
        {
            case AuditMetadataKind.Identifier:
                if (value.ValueKind == JsonValueKind.String && value.GetString() is { } id && (Guid.TryParseExact(id, "D", out _) || HexPattern().IsMatch(id)))
                {
                    return id;
                }

                return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number.ToString(CultureInfo.InvariantCulture) : null;
            case AuditMetadataKind.Count:
                if (value.ValueKind == JsonValueKind.Array)
                {
                    return value.GetArrayLength().ToString(CultureInfo.InvariantCulture);
                }

                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var count))
                {
                    return count.ToString(CultureInfo.InvariantCulture);
                }

                return value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed.ToString(CultureInfo.InvariantCulture) : null;
            case AuditMetadataKind.Status:
                if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    return value.GetBoolean() ? "Yes" : "No";
                }

                return value.ValueKind == JsonValueKind.String && value.GetString() is { } status && StatusPattern().IsMatch(status) ? status : null;
            case AuditMetadataKind.Code:
                if (value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString() is { } code && CodePattern().IsMatch(code) ? code : null;
                }

                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var numeric))
                {
                    return numeric.ToString(CultureInfo.InvariantCulture);
                }

                if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() is > 0 and <= 10)
                {
                    var codes = value.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String && e.GetString() is { } c && CodePattern().IsMatch(c) ? c : null).ToList();
                    return codes.Any(c => c is null) ? null : string.Join(", ", codes);
                }

                return null;
            case AuditMetadataKind.DirectoryName:
                return value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 and <= 200 } name && !name.Any(char.IsControl) ? name : null;
            default:
                return null;
        }
    }

    // ---- rule helpers ----

    private static AuditMetadataRule Id(string key, string label) => new(key, label, AuditMetadataKind.Identifier);

    private static AuditMetadataRule Count(string key, string label) => new(key, label, AuditMetadataKind.Count);

    private static AuditMetadataRule Status(string key, string label) => new(key, label, AuditMetadataKind.Status);

    private static AuditMetadataRule Code(string key, string label) => new(key, label, AuditMetadataKind.Code);

    private static AuditMetadataRule Name(string key, string label) => new(key, label, AuditMetadataKind.DirectoryName);

    private static AuditWithheldRule FreeText(string key, string label) => new(key, label, FreeTextPolicy);

    private static AuditWithheldRule DesignLabel(string key, string label) => new(key, label, DesignLabelPolicy);

    private static AuditWithheldRule DesignDiff(string key, string label) => new(key, label, DesignDiffPolicy);

    private static AuditWithheldRule Scope(string key, string label) => new(key, label, ScopePolicy);

    private static readonly AuditMetadataRule CommandId = Id("commandId", "Authority command");
    private static readonly AuditMetadataRule AuthorityCommandId = Id("authorityCommandId", "Authority command");
    private static readonly AuditMetadataRule ContentRevision = Count("contentRevision", "Content revision");
    private static readonly AuditMetadataRule OperationId = Id("operationId", "Content operation");
    private static readonly AuditMetadataRule Direction = Status("direction", "Direction");
    private static readonly AuditWithheldRule Reason = FreeText("reason", "Reason");
    private static readonly AuditWithheldRule ChangeReason = FreeText("changeReason", "Change reason");
    private static readonly AuditWithheldRule FieldKey = DesignLabel("fieldKey", "Field key");

    private static Dictionary<string, AuditCatalogEntry> Build()
    {
        var entries = new List<AuditCatalogEntry>();

        void Add(string action, string label, string description, AuditMetadataRule[] shown, AuditWithheldRule[]? withheld = null, Regex? storedPattern = null) =>
            entries.Add(new AuditCatalogEntry(action, label, description, storedPattern is null ? AuditDescriptionPolicy.Catalog : AuditDescriptionPolicy.StoredSafe, shown, withheld ?? [], storedPattern));

        // Form design
        Add(AuditActions.FormCreated, "Form created", "A form definition was registered with the Authorization Authority.", [CommandId]);
        Add(AuditActions.FormDraftUpdated, "Form draft changed", "The form's draft changed (layout saved, restored from a version, a field, repeating group or related list defined).",
            [Count("draftRevision", "Draft revision"), Count("elements", "Layout elements"), Count("fromVersionNumber", "Restored from version"), Id("childFormDefinitionId", "Child form"), Id("groupDefinitionId", "Repeating group"),
                Code("dataType", "Data type"), Code("classification", "Classification"), Code("protectionMode", "Protection"), AuthorityCommandId, Code("optionSelectionMode", "Option selection"), Id("optionListId", "Option list")],
            [FieldKey, DesignLabel("groupKey", "Group key"), DesignLabel("key", "Related list key")]);
        Add(AuditActions.FormVersionPublished, "Form version published", "An immutable form version was published.", [Count("versionNumber", "Version"), Count("elements", "Layout elements")], [ChangeReason]);
        Add(AuditActions.FormRenamed, "Form renamed", "The form's display name changed; identity, versions, records and permissions are unchanged.",
            [Name("previousFormDefinitionName", "Previous name"), Name("formDefinitionName", "New name"), Count("formDefinitionRevision", "Form revision"), Id("currentFormVersionId", "Current version")]);
        Add(AuditActions.FormVersionBehaviorChanged, "Form version behaviour changed", "Which published layout the form's records are viewed and edited with changed; no record, value or version changed.",
            [Code("previousVersionBehavior", "Previous behaviour"), Code("versionBehavior", "New behaviour"), Count("formDefinitionRevision", "Form revision"), Id("currentFormVersionId", "Current version")], [ChangeReason]);
        Add(AuditActions.FieldConfigurationChanged, "Field settings changed", "A field's catalog settings changed; its identity, key and type are unchanged.",
            [Code("dataType", "Data type"), Status("descriptionChanged", "Description changed"), Count("fieldDefinitionRevision", "Field revision")],
            [FieldKey, DesignLabel("previousDisplayName", "Previous field label"), DesignLabel("newDisplayName", "New field label")]);
        Add(AuditActions.FieldOptionsChanged, "Field options changed", "A field's dropdown options changed or were synchronized from a shared list; ids, keys and stored selections are unchanged.",
            [Count("changes", "Changes"), Count("added", "Options added"), Count("relabeled", "Options relabeled"), Count("deactivated", "Options deactivated"), Count("reactivated", "Options reactivated"),
                Id("optionListId", "Option list"), Count("optionListRevision", "Option list revision")],
            [FieldKey]);
        Add(AuditActions.OptionListChanged, "Shared option list changed", "A shared option list was created or changed; fields keep their own copies until they sync.",
            [Count("revision", "List revision"), Count("changes", "Changes"), Count("added", "Options added"), Count("deactivated", "Options deactivated"), Count("items", "Options")]);
        Add(AuditActions.FormVersionAuthorityRegistered, "Form version registered", "A published form version was registered with the Authorization Authority.", [Count("versionNumber", "Version"), Count("fields", "Fields")]);
        Add(AuditActions.FormVersionAuthorityRegistrationFailed, "Form version registration failed", "A published form version could not be registered with the Authorization Authority.", [Count("versionNumber", "Version"), Count("fields", "Fields")], [Reason]);

        // Field protection and governance
        Add(AuditActions.FieldProtectionStrengthened, "Field protection strengthened", "A field's classification or protection was strengthened (audited migration).",
            [Code("previous.fieldClassification", "Previous classification"), Code("previous.fieldProtectionMode", "Previous protection"), Count("previous.protectionPolicyRevision", "Previous policy revision"),
                Code("classification", "Classification"), Code("protectionMode", "Protection"), Count("protectionPolicyRevision", "Policy revision"), AuthorityCommandId],
            [FieldKey, ChangeReason]);
        Add(AuditActions.FieldProtectionMigrationStarted, "Protection migration started", "A background migration of the field's existing values was queued.",
            [AuthorityCommandId, Direction, Count("totalRecordCount", "Records"), Count("totalValueCount", "Values"), Id("rollbackOfMigrationId", "Rolls back migration")]);
        Add(AuditActions.FieldValuesReprotected, "Field values encrypted", "Plaintext values of a field were re-sealed as ciphertext in a new accepted content revision.",
            [ContentRevision, OperationId, Count("convertedValueCount", "Values converted"), Direction]);
        Add(AuditActions.FieldValuesDeprotected, "Field values decrypted", "Encrypted values of a field were re-written as typed plaintext in a new accepted content revision.",
            [ContentRevision, OperationId, Count("convertedValueCount", "Values converted"), Direction]);
        Add(AuditActions.FieldProtectionMigrationRecordFailed, "Protection migration record failed", "A record could not be converted after bounded attempts; the field is not fully converted.",
            [Code("errorCode", "Error code"), Count("attempts", "Attempts")]);
        Add(AuditActions.FieldProtectionMigrationCompleted, "Protection migration completed", "The protection migration of a field completed.",
            [Count("totalRecordCount", "Records"), Count("totalValueCount", "Values"), Count("completedRecordCount", "Records completed"), Direction, Id("reconciliationId", "Reconciliation"), Id("attestationId", "Attestation"), Status("independentlyReconciled", "Independently reconciled")]);
        Add(AuditActions.FieldProtectionMigrationFailed, "Protection migration failed", "The protection migration of a field did not complete; the field is not claimed converted.",
            [Count("totalRecordCount", "Records"), Count("completedRecordCount", "Records completed"), Count("failedRecordCount", "Records failed"), Code("lastErrorCode", "Last error code"), Count("verifiedRemaining", "Verified remaining"),
                Count("unverified", "Unverified"), Count("remaining", "Remaining"), Direction, Status("state", "State"), Id("reconciliationId", "Reconciliation"), Id("attestationId", "Attestation")],
            [Reason]);
        Add(AuditActions.FieldProtectionMigrationRetried, "Protection migration retried", "Failed records of a protection migration were queued again.", [Count("requeued", "Records requeued"), Direction], [FreeText("retryReason", "Retry reason")]);
        Add(AuditActions.FieldProtectionReconciliationRequested, "Protection reconciliation requested", "A reconciliation of the field against the Authority's complete record population was queued.",
            [Direction, AuthorityCommandId, Id("reconciliationId", "Reconciliation"), Count("totalRecordCount", "Records"), Count("completedRecordCount", "Records completed"), Count("verifiedRecords", "Records verified")],
            [FreeText("requestReason", "Request reason")]);
        Add(AuditActions.FieldProtectionReconciliationAttested, "Protection reconciliation finished", "The Authority's reconciliation of a field finished; see the state for whether completion was attested (current versions only).",
            [Status("mode", "Mode"), Status("state", "State"), Id("attestationId", "Attestation"), Count("expectedRecordCount", "Expected records"), Count("compliantRecordCount", "Compliant records"), Count("oldFormRemainingRecordCount", "Records in the old form"),
                Count("quarantinedRecordCount", "Quarantined records"), Count("unverifiedRecordCount", "Unverified records"), Count("pendingCreationRecordCount", "Records pending creation"), Count("retainedHistoryRevisionCount", "Retained history revisions"),
                Count("businessOnlyRecordCount", "Records only in the application"), Count("businessMissingRecordCount", "Records missing in the application"), Count("decryptFailedValueCount", "Values that failed to decrypt")],
            [Reason]);
        Add(AuditActions.FieldDeclassified, "Field declassified", "A field was declassified by an organization administrator; it stays encrypted until a distinct administrator releases it.",
            [Code("previousClassification", "Previous classification"), Code("targetClassification", "New classification"), AuthorityCommandId, Count("protectionPolicyRevision", "Policy revision")],
            [FieldKey, ChangeReason, FreeText("approvalReference", "Approval reference")]);
        Add(AuditActions.FieldProtectionDowngraded, "Field encryption released", "A field was released from application encryption; a decryption migration was queued.",
            [Code("classification", "Classification"), AuthorityCommandId, Count("protectionPolicyRevision", "Policy revision"), Count("totalRecordCount", "Records"), Count("totalValueCount", "Values")],
            [FieldKey, ChangeReason]);
        Add(AuditActions.FieldProtectionDowngradeCancelled, "Encryption release cancelled", "An encryption release was cancelled; values already decrypted are re-encrypted by a rollback migration.",
            [AuthorityCommandId, Id("cancelledMigrationId", "Cancelled migration"), Code("exposureWindowStart", "Exposure window start"), Code("exposureWindowEnd", "Exposure window end"), Count("decryptedRecordsToReencrypt", "Records to re-encrypt")],
            [FieldKey, ChangeReason]);
        Add(AuditActions.OrganizationSecurityPolicyUpdated, "Data-protection policy changed", "The organization's data-protection policy was changed by an organization owner.",
            [Code("previous.minimumFieldClassification", "Previous minimum classification"), Status("previous.plaintextInternalFieldsPermitted", "Previously: plaintext Internal fields"), Status("previous.plaintextDowngradePermitted", "Previously: encryption release"),
                Status("previous.plaintextSearchPermitted", "Previously: plaintext search"), Code("minimumFieldClassification", "Minimum classification"), Status("plaintextInternalFieldsPermitted", "Plaintext Internal fields"),
                Status("plaintextDowngradePermitted", "Encryption release"), Status("plaintextSearchPermitted", "Plaintext search"), Status("loosened", "Loosened"), Count("revision", "Policy revision"), AuthorityCommandId]);
        Add(AuditActions.FieldSearchApproved, "Field search approved", "Plaintext value search was approved for a field.", [Code("operators", "Operators")], [FreeText("approvalReference", "Approval reference")]);
        Add(AuditActions.FieldSearchWithdrawn, "Field search withdrawn", "A plaintext value search approval was withdrawn for a field.", [], [Reason]);
        Add(AuditActions.FieldSearchersChanged, "Field search access changed", "Who may search the form's fields changed.",
            [Count("grantUsers", "Users granted"), Count("grantTeams", "Teams granted"), Count("revokeUsers", "Users revoked"), Count("revokeTeams", "Teams revoked")]);
        Add(AuditActions.FormAccessGranted, "Form access granted", "A user or team may now create records with the form (committed by the Authority).", [Code("subjectKind", "Principal kind"), Id("subjectId", "Principal"), CommandId], [ChangeReason]);
        Add(AuditActions.FormAccessRevoked, "Form access revoked", "A user or team may no longer create records with the form (committed by the Authority).", [Code("subjectKind", "Principal kind"), Id("subjectId", "Principal"), CommandId], [ChangeReason]);

        // Records
        Add(AuditActions.RecordCreated, "Record created", "A record was registered with the Authorization Authority.", [CommandId, Count("authorizationRevision", "Authorization revision")], [DesignDiff("groupChanges", "Repeating group changes")]);
        Add(AuditActions.RecordViewed, "Record viewed", "A verified record was read.", [Status("complete", "Complete projection"), ContentRevision]);
        Add(AuditActions.RecordUpdated, "Record edited", "A new accepted content revision of the record was committed.",
            [ContentRevision, Status("complete", "Complete projection"), Status("editBasis", "Edit basis")],
            [DesignLabel("changedFieldKeys", "Changed fields"), DesignDiff("detail", "Change detail")]);
        var lifecycle = LifecycleDescription();
        foreach (var (action, label) in new[] { (AuditActions.RecordSubmitted, "Record submitted"), (AuditActions.RecordReopened, "Record reopened"), (AuditActions.RecordArchived, "Record archived"), (AuditActions.RecordUnarchived, "Record unarchived") })
        {
            Add(action, label, "The record's status changed.", [Status("transition", "Transition"), Status("fromStatus", "From"), Status("toStatus", "To"), ContentRevision, OperationId], [Reason], lifecycle);
        }

        Add(AuditActions.RecordLifecycleDenied, "Record status change refused", "A record status change was refused.", [Status("transition", "Transition"), Code("code", "Refusal code")]);
        Add(AuditActions.RecordVersionViewed, "Earlier record version viewed", "An earlier verified version of the record was opened read-only.",
            [Count("viewedContentRevision", "Viewed revision"), OperationId, Count("currentContentRevision", "Current revision")]);
        Add(AuditActions.RecordVersionRestored, "Earlier record version restored", "An earlier version of the record was restored as a new revision.",
            [ContentRevision, OperationId, Count("sourceContentRevision", "Restored from revision"), Id("sourceOperationId", "Restored from operation"), Status("plaintextAcknowledged", "Plaintext acknowledged")],
            [DesignLabel("changedFieldKeys", "Changed fields"), DesignLabel("clearedFieldKeys", "Cleared fields"), DesignDiff("groupChanges", "Repeating group changes"), Reason], RestoreDescription());
        Add(AuditActions.ChildRecordLinked, "Child record linked", "A child record was created and linked to a parent record.", [CommandId], [DesignLabel("relationship", "Related list key")]);
        Add(AuditActions.ChildRecordUnlinked, "Child record unlinked", "A child record was unlinked from its parent; its data is retained.", [CommandId], [Reason]);
        Add(AuditActions.RecordAccessGranted, "Record shared", "A field-scoped share was committed by the Authority.", [Code("role", "Access role"), Id("recipientUserId", "Recipient user"), Id("recipientTeamId", "Recipient team"), CommandId], [Scope("fieldKeys", "Shared fields")]);
        Add(AuditActions.RecordAccessRevoked, "Record share revoked", "A share was revoked at the Authority barrier.", [CommandId]);
        Add(AuditActions.RecordOwnershipTransferred, "Record ownership transferred", "The record's owner changed.", [CommandId], [Reason]);
        Add(AuditActions.AccessDenied, "Access denied", "An operation was refused.", [Code("code", "Refusal code")]);

        // Documents
        Add(AuditActions.DocumentUploadStarted, "Upload started", "An upload session was opened.", [Count("sizeBytes", "Size (bytes)"), Count("chunkCount", "Chunks")]);
        Add(AuditActions.DocumentProcessed, "Document processed", "A document was scanned clean and encrypted and awaits its manifest commit.",
            [Code("scanner", "Scanner"), Count("versionNumber", "Version"), Code("detectedContentType", "Detected content type"), Count("plaintextSizeBytes", "Size (bytes)")]);
        Add(AuditActions.DocumentRejected, "Document rejected", "A document was rejected (malware scanner verdict or expired upload session).",
            [Code("verdict", "Verdict"), Code("scanner", "Scanner"), Code("signature", "Detection"), Count("receivedBytes", "Bytes received")], [Reason]);
        Add(AuditActions.DocumentReady, "Document added", "A document version was committed into the record manifest.", [Count("versionNumber", "Version"), ContentRevision]);
        Add(AuditActions.DocumentDownloaded, "Document downloaded", "An authorized document download was released.", [Id("fileAttachmentId", "Document"), Count("versionNumber", "Version")]);
        Add(AuditActions.DocumentRemoved, "Document removed", "A document was removed from the accepted content; its bytes are retained.", [ContentRevision], [Reason]);

        // Governance and membership
        Add(AuditActions.SecurityAdministratorAppointed, "Security Administrator appointed", "A member was appointed Security Administrator.", [], [Reason]);
        Add(AuditActions.SecurityAdministratorRemoved, "Security Administrator removed", "A member's Security Administrator role was removed.", [], [Reason]);
        Add(AuditActions.SecurityAuditorAppointed, "Security Auditor appointed", "A member was appointed Security Auditor.", [Code("reasonCode", "Reason code")]);
        Add(AuditActions.SecurityAuditorRemoved, "Security Auditor removed", "A member's Security Auditor role was removed.", [Code("reasonCode", "Reason code")]);
        Add(AuditActions.OrganizationRoleGranted, "Organization role granted", "An organization role was granted (committed by the Authority).", [Code("roleName", "Role"), CommandId], [Reason]);
        Add(AuditActions.OrganizationRoleRevoked, "Organization role revoked", "An organization role was revoked (committed by the Authority).", [Code("roleName", "Role"), CommandId], [Reason]);
        Add(AuditActions.OrganizationOwnerChangeProposed, "Owner change proposed", "An organization owner change was proposed and needs a second owner.", [Code("change", "Change"), Id("proposalId", "Proposal"), CommandId], [Reason]);
        Add(AuditActions.MembershipDisabled, "Membership disabled", "A membership was disabled; sessions were revoked and roles and shares kept.", [CommandId], [Reason]);
        Add(AuditActions.MembershipEnabled, "Membership re-enabled", "A membership was re-enabled.", [CommandId], [Reason]);
        Add(AuditActions.MembershipRequested, "Membership requested", "A membership was requested and awaits two operator approvals.",
            [Id("requestId", "Request"), Code("initialRole", "Initial role"), CommandId, Code("subjectIdentifier", "Identity subject")], [Reason]);
        Add(AuditActions.MembershipRequestCancelled, "Membership request cancelled", "A pending membership request was cancelled.", [Id("requestId", "Request"), CommandId], [Reason]);
        Add(AuditActions.MembershipRequestReplaced, "Membership request edited", "A pending membership request was replaced by a new request awaiting two operator approvals.",
            [Id("originalRequestId", "Original request"), Id("replacementRequestId", "Replacement request"), Code("initialRole", "Initial role"), CommandId]);
        Add(AuditActions.UserProfileUpdated, "Profile updated", "A member's profile was updated.", [Code("changedFields", "Changed profile fields"), Count("revision", "Profile revision"), Status("self", "Own profile")]);
        Add(AuditActions.TeamRenamed, "Team renamed", "A team's display label changed; Authority membership is unchanged.", [Name("previousTeamName", "Previous name"), Name("teamName", "New name")]);
        Add(AuditActions.TeamMembershipReconciliationFailed, "Team membership reconciliation failed", "A team membership command could not be reconciled with the Authority.",
            [Id("authorityCommandOperationId", "Operation"), Code("failureCode", "Failure code"), Code("commandKind", "Command kind"), Id("initiatedByUserId", "Initiated by"), Id("memberUserId", "Member"), Id("securityCommandId", "Authority command")]);
        Add(AuditActions.TeamMembershipProjectionDrift, "Team roster corrected", "The team roster differed from the Authority at the same membership revision and was corrected to the Authority state.",
            [Id("authorityCommandOperationId", "Operation"), Id("initiatedByUserId", "Initiated by"), Id("memberUserId", "Member"), Count("effectiveMembershipRevision", "Membership revision"), Status("effectiveMember", "Member at that revision")]);
        Add(AuditActions.TeamMembershipOperationRechecked, "Team membership operation rechecked", "A team membership operation was checked again; no command was submitted.",
            [Id("authorityCommandOperationId", "Operation"), Status("previousOperationState", "Previous state"), Code("previousErrorCode", "Previous error code")]);
        Add(AuditActions.TeamMembershipOperationReviewed, "Team membership failure reviewed", "A team membership operation failure was reviewed by an administrator.",
            [Id("authorityCommandOperationId", "Operation"), Status("operationState", "State"), Code("errorCode", "Error code"), Status("lateCommitDetected", "Late commit detected")]);

        // Organization settings and preferences
        Add(AuditActions.OrganizationLogoUploaded, "Organization logo uploaded", "The organization logo was uploaded.", [Count("revision", "Branding revision"), Count("logoSizeBytes", "Size (bytes)"), Id("logoSha256", "Logo SHA-256")]);
        Add(AuditActions.OrganizationLogoReplaced, "Organization logo replaced", "The organization logo was replaced.", [Count("revision", "Branding revision"), Count("logoSizeBytes", "Size (bytes)"), Id("logoSha256", "Logo SHA-256")]);
        Add(AuditActions.OrganizationSettingsUpdated, "Organization settings changed", "The organization's regional defaults or branding changed.",
            [Count("revision", "Settings revision"), Code("previous.defaultCountryCode", "Previous country"), Code("previous.defaultIanaTimeZoneId", "Previous time zone"), Code("previous.defaultLanguageCode", "Previous language"),
                Code("previous.defaultDateFormatPresetKey", "Previous date format"), Code("previous.defaultNumberFormatPresetKey", "Previous number format"), Code("current.defaultCountryCode", "Country"),
                Code("current.defaultIanaTimeZoneId", "Time zone"), Code("current.defaultLanguageCode", "Language"), Code("current.defaultDateFormatPresetKey", "Date format"), Code("current.defaultNumberFormatPresetKey", "Number format")]);
        Add(AuditActions.UserPreferencesUpdated, "Personal preferences changed", "A member changed their regional preferences (modes only).",
            [Count("revision", "Preference revision"), Status("timeZone", "Time zone mode"), Status("dateFormat", "Date format mode"), Status("numberFormat", "Number format mode"), Status("language", "Language mode")]);

        // Security monitoring
        Add(AuditActions.SecurityIncidentDetected, "Security incident detected", "A verification problem was observed while serving a request.",
            [Code("incidentType", "Incident type"), Code("detectionSource", "Detection source"), Code("verificationErrorCode", "Verification error")], null, IncidentDetectedDescription());
        Add(AuditActions.SecurityIncidentViewed, "Security incident viewed", "A security incident was viewed.", []);
        Add(AuditActions.SecurityIncidentStatusChanged, "Security incident status changed", "A security incident's status changed.",
            [Status("previousStatus", "Previous status"), Status("incidentStatus", "Status"), Status("explanationProvided", "Explanation provided")], null, IncidentStatusDescription());
        Add(AuditActions.SecurityIncidentAssigned, "Security incident assigned", "A security incident's investigator changed.", [Id("previousAssigneeUserId", "Previous investigator"), Id("assigneeUserId", "Investigator")]);
        Add(AuditActions.SecurityIncidentNoteAdded, "Investigation note added", "An investigation note was added; its text is not in the audit.", [Id("securityIncidentNoteId", "Note")], [FreeText("noteLength", "Note length")]);
        Add(AuditActions.DecryptionFailed, "Decryption failed", "A protected value or document could not be decrypted; nothing was released.",
            [Code("reasonCode", "Reason code"), Code("protectedItemKind", "Item kind"), Id("fieldValueItemId", "Field value item"), Count("versionNumber", "Version")], null, DecryptionDescription());

        // Audit access (this explorer)
        foreach (var (action, label, description) in new[]
                 {
                     (AuditActions.AuditEventsViewed, "Audit explorer page viewed", "A page of the audit explorer was served after the Authority authorized and recorded the access."),
                     (AuditActions.AuditEventViewed, "Audit event viewed", "An audit event's detail was served after the Authority authorized and recorded the access."),
                 })
        {
            Add(action, label, description, [Status("deliveryOutcome", "Delivery (business-reported)"), Count("authorityJournalRevision", "Authority audit-stream revision"), Code("accessKind", "Access kind"), Id("filterDigest", "Filter digest")]);
        }

        return entries.ToDictionary(e => e.Action, StringComparer.Ordinal);
    }

    [GeneratedRegex("^record status changed from [A-Za-z]{1,32} to [A-Za-z]{1,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex LifecycleDescription();

    [GeneratedRegex("^earlier version [0-9]{1,18} restored as a new revision$", RegexOptions.CultureInvariant)]
    private static partial Regex RestoreDescription();

    [GeneratedRegex("^security incident detected: [A-Za-z]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex IncidentDetectedDescription();

    [GeneratedRegex("^security incident status changed from [A-Za-z]{1,32} to [A-Za-z]{1,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex IncidentStatusDescription();

    [GeneratedRegex("^a protected (document|field value) could not be decrypted \\([A-Z_]{1,64}\\); nothing was released$", RegexOptions.CultureInvariant)]
    private static partial Regex DecryptionDescription();
}
