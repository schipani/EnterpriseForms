using System.Text.Json;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.FieldSearch;
using AuthorizationAuthority.Domain.Features.Organizations;

namespace AuthorizationAuthority.Application.Features.SecurityCommands;

/// <summary>
/// AC-6 eligibility (§4.2, §4.3, §4.7), decided only from Authority-held facts: the registered published version's field
/// entry, the organization protection policy and the field's protection resources. Shared by the approval command and the
/// capability/configuration view so both always agree.
/// </summary>
public static class FieldSearchRules
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static IReadOnlyList<PublishedFieldEntry> ReadManifest(PublishedFormVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return JsonSerializer.Deserialize<List<PublishedFieldEntry>>(version.FieldManifestJson, Json) ?? [];
    }

    /// <summary>Fields in id order; options keep the published order (what searchers are shown).</summary>
    public static string WriteManifest(IEnumerable<PublishedFieldEntry> fields) =>
        JsonSerializer.Serialize(fields.OrderBy(f => f.FieldDefinitionId).ToList(), Json);

    public static IReadOnlyList<string> ReadOperators(FieldSearchApproval approval) =>
        JsonSerializer.Deserialize<List<string>>(approval.ApprovedOperatorsJson) ?? [];

    public static IReadOnlyList<FieldSearchOption> ReadOptions(FieldSearchApproval approval) =>
        JsonSerializer.Deserialize<List<FieldSearchOption>>(approval.ApprovedOptionSnapshotJson, Json) ?? [];

    /// <summary>The approval's option snapshot, in the published order.</summary>
    public static string WriteOptions(IEnumerable<PublishedOptionEntry>? options) =>
        JsonSerializer.Serialize((options ?? []).Select(o => new FieldSearchOption(o.FieldOptionId, o.OptionLabel)).ToList(), Json);

    /// <summary>
    /// Whether a published option set equals the approval's snapshot: the same option ids with the same labels, in any
    /// order. Compared as values, never as JSON text (the snapshot is stored as jsonb, which PostgreSQL reformats).
    /// </summary>
    public static bool SameOptions(FieldSearchApproval approval, IEnumerable<PublishedOptionEntry>? options)
    {
        static IEnumerable<(Guid, string)> Key(IEnumerable<(Guid Id, string Label)> items) => items.OrderBy(i => i.Id).ThenBy(i => i.Label, StringComparer.Ordinal);
        return Key(ReadOptions(approval).Select(o => (o.FieldOptionId, o.OptionLabel))).SequenceEqual(Key((options ?? []).Select(o => (o.FieldOptionId, o.OptionLabel))));
    }

    public static IReadOnlyList<string> PermittedOperators(string fieldDataType) =>
        FieldSearchVocabulary.OperatorsByType.TryGetValue(fieldDataType, out var operators) ? operators : [];

    /// <summary>The Authority-held protection facts of a field.</summary>
    public sealed record ProtectionFacts(bool RequirementActive, bool ReclassificationActive, bool ReleaseActive);

    public static async Task<ProtectionFacts> ReadProtectionFactsAsync(IAuthorityScope scope, Guid organizationId, Guid fieldDefinitionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        static bool Active(AuthorityResource? r) => r is { ResourceLifecycleState: ResourceLifecycleStates.Active };
        var requirement = await scope.FindResourceAsync(organizationId, ResourceTypeNames.FieldProtectionRequirement, fieldDefinitionId, cancellationToken).ConfigureAwait(false);
        var reclassification = await scope.FindResourceAsync(organizationId, ResourceTypeNames.FieldReclassification, fieldDefinitionId, cancellationToken).ConfigureAwait(false);
        var release = await scope.FindResourceAsync(organizationId, ResourceTypeNames.FieldProtectionRelease, fieldDefinitionId, cancellationToken).ConfigureAwait(false);
        return new ProtectionFacts(Active(requirement), Active(reclassification), Active(release));
    }

    /// <summary>
    /// Eligible only for an explicitly plaintext Public field, or Internal where the organization permits plaintext Internal
    /// fields, at or above the organization's minimum classification, with search enabled by the organization, no
    /// Authority-held encryption requirement and no pending protection change. A field released from encryption (A1
    /// downgrade) stays ProtectionPending until migration completion can be verified (G19/M4): approval never exposes
    /// formerly encrypted history.
    /// </summary>
    public static string Eligibility(PublishedFieldEntry field, OrganizationProtectionPolicy? policy, ProtectionFacts facts)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.RequirementActive || !string.Equals(field.ProtectionMode, "Standard", StringComparison.Ordinal))
        {
            return FieldSearchEligibility.Encrypted;
        }

        if (facts.ReclassificationActive || facts.ReleaseActive)
        {
            return FieldSearchEligibility.ProtectionPending;
        }

        if (PermittedOperators(field.FieldDataType).Count == 0)
        {
            return FieldSearchEligibility.TypeNotSearchable;
        }

        if (field.InRepeatingGroup)
        {
            return FieldSearchEligibility.RepeatingGroupNotSearchable;
        }

        if (policy is null)
        {
            return FieldSearchEligibility.OrganizationPolicyMissing;
        }

        if (!policy.PlaintextSearchPermitted)
        {
            return FieldSearchEligibility.OrganizationSearchDisabled;
        }

        return ClassificationPermitted(field.FieldClassification, policy) ? FieldSearchEligibility.Eligible : FieldSearchEligibility.ClassificationNotPermitted;
    }

    public static bool ClassificationPermitted(string classification, OrganizationProtectionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return classification switch
        {
            "Public" => CommandPlanner.Rank("Public") >= CommandPlanner.Rank(policy.MinimumFieldClassification),
            "Internal" => policy.PlaintextInternalFieldsPermitted && CommandPlanner.Rank("Internal") >= CommandPlanner.Rank(policy.MinimumFieldClassification),
            _ => false,
        };
    }

    public static void ValidateEntry(PublishedFieldEntry field)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (field.FieldDefinitionId == Guid.Empty || !FieldSearchVocabulary.DataTypes.Contains(field.FieldDataType) || !FieldSearchVocabulary.Classifications.Contains(field.FieldClassification)
            || !FieldSearchVocabulary.ProtectionModes.Contains(field.ProtectionMode) || string.IsNullOrWhiteSpace(field.FieldLabel) || field.FieldLabel.Length > 300
            || (field.Options ?? []).Any(o => o.FieldOptionId == Guid.Empty || string.IsNullOrWhiteSpace(o.OptionLabel) || o.OptionLabel.Length > 300)
            || (field.Options ?? []).Select(o => o.FieldOptionId).Distinct().Count() != (field.Options ?? []).Count
            || (field.Options is { Count: > 0 } && field.FieldDataType != "Option") || (field.Options?.Count ?? 0) > 500)
        {
            throw AuthorityException.Invalid($"field entry {field.FieldDefinitionId} is not a valid published field (type, classification, protection, label, options)");
        }
    }
}
