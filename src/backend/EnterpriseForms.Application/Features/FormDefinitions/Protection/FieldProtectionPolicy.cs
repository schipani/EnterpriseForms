using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Features.FormRecords.Values;
using EnterpriseForms.Domain.Features.FormDefinitions;
using EnterpriseForms.Domain.Features.Organizations;

namespace EnterpriseForms.Application.Features.FormDefinitions.Protection;

/// <summary>
/// Resolves and enforces a field's classification and storage protection (ADR-PB-004, SECURITY_SIMPLIFICATION Decision 2):
/// four classifications with a monotonic organization floor; Confidential/Restricted always application-encrypted;
/// Public/Internal plaintext only where the organization policy permits it; unclassified free text and documents
/// default to Confidential. Applied on every path that creates a field (designer route and the Stage A route alike) so
/// a hand-crafted request cannot bypass the designer dialog. Protection can be strengthened later, never weakened.
/// </summary>
public static class FieldProtectionPolicy
{
    public static int Rank(string classification) => classification switch
    {
        FieldClassifications.Public => 0,
        FieldClassifications.Internal => 1,
        FieldClassifications.Confidential => 2,
        FieldClassifications.Restricted => 3,
        _ => throw new BusinessException("INVALID_REQUEST", 422, "classification must be Public, Internal, Confidential or Restricted"),
    };

    public static bool RequiresEncryption(string classification) => Rank(classification) >= Rank(FieldClassifications.Confidential);

    /// <summary>Unclassified free text (String) and documents (File) are Confidential; structured typed values default to Internal.</summary>
    public static string DefaultClassification(string dataType) => dataType is FieldDataTypes.String or FieldDataTypes.File ? FieldClassifications.Confidential : FieldClassifications.Internal;

    /// <summary>Resolves the effective (classification, protection mode) for a new field, or refuses with a reason the designer can act on.</summary>
    public static (string Classification, string ProtectionMode) Resolve(string dataType, string? requestedClassification, string? requestedProtectionMode, OrganizationSecurityPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var classification = string.IsNullOrWhiteSpace(requestedClassification) ? DefaultClassification(dataType) : requestedClassification.Trim();
        _ = Rank(classification);
        if (requestedProtectionMode is not (null or "" or FieldProtectionModes.Standard or FieldProtectionModes.ApplicationEncrypted))
        {
            throw new BusinessException("INVALID_REQUEST", 422, "protection mode must be Standard or ApplicationEncrypted");
        }

        if (Rank(classification) < Rank(policy.MinimumFieldClassification))
        {
            throw new BusinessException("CLASSIFICATION_BELOW_FLOOR", 422, $"the organization's minimum classification is {policy.MinimumFieldClassification}; {classification} is not allowed");
        }

        // Documents are always application-encrypted by the Documents pipeline; the field policy records that fact.
        if (dataType == FieldDataTypes.File)
        {
            return (classification, FieldProtectionModes.ApplicationEncrypted);
        }

        // An explicit request for plaintext on Confidential/Restricted content is refused, never silently corrected.
        if (RequiresEncryption(classification) && requestedProtectionMode == FieldProtectionModes.Standard)
        {
            throw new BusinessException("FIELD_PROTECTION_REQUIRED", 422, $"{classification} fields must be application-encrypted");
        }

        var encrypted = RequiresEncryption(classification) || requestedProtectionMode == FieldProtectionModes.ApplicationEncrypted
                        || (string.IsNullOrEmpty(requestedProtectionMode) && dataType == FieldDataTypes.String);

        if (!encrypted && !policy.PlaintextInternalFieldsPermitted)
        {
            throw new BusinessException("PLAINTEXT_NOT_PERMITTED", 422, "the organization's data-protection policy does not permit plaintext storage; choose application encryption or ask the organization owner to review the policy");
        }

        return (classification, encrypted ? FieldProtectionModes.ApplicationEncrypted : FieldProtectionModes.Standard);
    }

    /// <summary>Strengthening only: a higher classification and/or a move from Standard to ApplicationEncrypted. Anything else is refused.</summary>
    public static (string Classification, string ProtectionMode) ResolveStrengthening(FieldDefinition field, string? targetClassification, string? targetProtectionMode, OrganizationSecurityPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(field);
        var classification = string.IsNullOrWhiteSpace(targetClassification) ? field.FieldClassification : targetClassification.Trim();
        if (Rank(classification) < Rank(field.FieldClassification))
        {
            throw new BusinessException("FIELD_PROTECTION_DOWNGRADE_REFUSED", 422, $"'{field.FieldKey}' is {field.FieldClassification}; lowering a classification is not a designer edit (ADR-PB-004: it needs an approved, audited security migration and never happens through this API)");
        }

        var mode = string.IsNullOrWhiteSpace(targetProtectionMode) ? (RequiresEncryption(classification) ? FieldProtectionModes.ApplicationEncrypted : field.FieldProtectionMode) : targetProtectionMode.Trim();
        if (mode is not (FieldProtectionModes.Standard or FieldProtectionModes.ApplicationEncrypted))
        {
            throw new BusinessException("INVALID_REQUEST", 422, "protection mode must be Standard or ApplicationEncrypted");
        }

        if (field.FieldProtectionMode == FieldProtectionModes.ApplicationEncrypted && mode != FieldProtectionModes.ApplicationEncrypted)
        {
            throw new BusinessException("FIELD_PROTECTION_DOWNGRADE_REFUSED", 422, $"'{field.FieldKey}' is application-encrypted; it cannot become plaintext through this API");
        }

        if (RequiresEncryption(classification) && mode != FieldProtectionModes.ApplicationEncrypted)
        {
            throw new BusinessException("FIELD_PROTECTION_REQUIRED", 422, $"{classification} fields must be application-encrypted");
        }

        if (classification == field.FieldClassification && mode == field.FieldProtectionMode)
        {
            throw new BusinessException("NO_PROTECTION_CHANGE", 409, "the field already has that classification and protection");
        }

        _ = policy;
        return (classification, mode);
    }
}
