using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.FieldSearch;
using AuthorizationAuthority.Domain.Features.Organizations;

namespace AuthorizationAuthority.Application.Features.SecurityCommands;

/// <summary>
/// C-188 (Option B): when an Active approval may confer search <b>now</b>. A CurrentVersion approval is effective as before (its
/// lifecycle is unchanged). A RetainedField approval is effective only when every one of these holds at the moment of use:
/// <list type="number">
/// <item>retained-field approvals are enabled by deployment configuration (D-H18; off by default);</item>
/// <item>the field is still absent from the form's latest registered version (D-H16: a field that came back without a
/// conversion — possible only through a binary that cannot see retained approvals — is not effective until the next registration
/// converts or invalidates it);</item>
/// <item>the field is bound to the approval's form (D-H7);</item>
/// <item>the field is still eligible from Authority-held facts and the current organization policy (D-H4), re-evaluated on the
/// approval's snapshot entry, so a strengthening or policy tightening made while the approval was hidden from an older binary can
/// never leave it effective.</item>
/// </list>
/// Fail closed: any missing fact makes the approval not effective. Nothing here writes.
/// </summary>
public static class RetainedFieldApprovals
{
    /// <summary>The approval's frozen entry, as eligibility is evaluated on it (retained approvals never cover repeating-group fields).</summary>
    public static PublishedFieldEntry EntryOf(FieldSearchApproval approval)
    {
        ArgumentNullException.ThrowIfNull(approval);
        var options = FieldSearchRules.ReadOptions(approval).Select(o => new PublishedOptionEntry(o.FieldOptionId, o.OptionLabel)).ToList();
        return new PublishedFieldEntry(approval.FieldDefinitionId, approval.ApprovedFieldDataType, approval.ApprovedFieldClassification, approval.ApprovedProtectionMode,
            approval.ApprovedFieldLabelSnapshot, options.Count == 0 ? null : options, InRepeatingGroup: false);
    }

    /// <summary>The invalidation reason for an eligibility failure found when a retained field returns (D-H4).</summary>
    public static string InvalidationReasonFor(string eligibility) => eligibility switch
    {
        FieldSearchEligibility.Encrypted or FieldSearchEligibility.ProtectionPending => FieldSearchInvalidationReasons.FieldStrengthened,
        FieldSearchEligibility.ClassificationNotPermitted or FieldSearchEligibility.OrganizationSearchDisabled or FieldSearchEligibility.OrganizationPolicyMissing => FieldSearchInvalidationReasons.OrganizationPolicyChanged,
        _ => FieldSearchInvalidationReasons.FormVersionChanged,
    };

    /// <summary>
    /// The ids of the given Active RetainedField approvals that are <b>not</b> effective now (to be excluded from capabilities and
    /// predicate authorization). Approvals of other scopes or states are never returned.
    /// </summary>
    public static async Task<HashSet<Guid>> IneffectiveAsync(IAuthorityScope scope, Guid organizationId, IEnumerable<FieldSearchApproval> approvals, OrganizationProtectionPolicy? policy, bool retainedEnabled, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(approvals);
        var ineffective = new HashSet<Guid>();
        var latestManifests = new Dictionary<Guid, HashSet<Guid>?>();
        foreach (var approval in approvals.Where(a => a.ApprovalStateName == FieldSearchApprovalStates.Active && a.ApprovalScopeName != FieldSearchApprovalScopes.CurrentVersion))
        {
            if (!retainedEnabled || approval.ApprovalScopeName != FieldSearchApprovalScopes.RetainedField)
            {
                ineffective.Add(approval.FieldSearchApprovalId);
                continue;
            }

            if (!latestManifests.TryGetValue(approval.FormDefinitionId, out var latestFields))
            {
                var latest = await scope.FindLatestPublishedFormVersionAsync(organizationId, approval.FormDefinitionId, cancellationToken).ConfigureAwait(false);
                latestFields = latest is null ? null : FieldSearchRules.ReadManifest(latest).Select(f => f.FieldDefinitionId).ToHashSet();
                latestManifests[approval.FormDefinitionId] = latestFields;
            }

            if (latestFields is null || latestFields.Contains(approval.FieldDefinitionId))
            {
                ineffective.Add(approval.FieldSearchApprovalId);
                continue;
            }

            var binding = await scope.FindFieldFormBindingAsync(organizationId, approval.FieldDefinitionId, cancellationToken).ConfigureAwait(false);
            if (binding is null || binding.FormDefinitionId != approval.FormDefinitionId)
            {
                ineffective.Add(approval.FieldSearchApprovalId);
                continue;
            }

            var facts = await FieldSearchRules.ReadProtectionFactsAsync(scope, organizationId, approval.FieldDefinitionId, cancellationToken).ConfigureAwait(false);
            if (FieldSearchRules.Eligibility(EntryOf(approval), policy, facts) != FieldSearchEligibility.Eligible)
            {
                ineffective.Add(approval.FieldSearchApprovalId);
            }
        }

        return ineffective;
    }
}
