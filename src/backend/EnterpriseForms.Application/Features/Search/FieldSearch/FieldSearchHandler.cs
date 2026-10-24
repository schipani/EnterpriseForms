using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Contracts.Features;

namespace EnterpriseForms.Application.Features.Search.FieldSearch;

/// <summary>
/// Phase C7 Advanced Field Search (docs/architecture/PHASE_C7_ADVANCED_FIELD_SEARCH.md, decisions C-56, C-60, C-67).
/// <para>
/// M2: the fields a caller may search, their operators, labels and option choices come only from the Authority (AC-6
/// approvals with their frozen snapshots, the caller's per-form <c>field_searcher</c> relation, the organization policy).
/// A search request is authorized condition by condition against that list (forged field ids, operators and injected
/// options are refused with the single indistinguishable denial) and then refused because trusted execution (M3) is not
/// enabled — never an empty HTTP 200 that could read as "no records match". It has no business-store or record dependency:
/// there is no fallback that could filter business PostgreSQL rows (enforced by an architecture test).
/// </para>
/// </summary>
public sealed class FieldSearchHandler(FieldSearchLimits limits, IFieldSearchPolicyAuthority policy)
{
    public const string ConditionJoinAnd = "And";

    /// <summary>
    /// Searchable fields only with an active approval and the caller's query permission; the not-searchable list only for
    /// callers who may see the form's search configuration (designers, security administrators). A form the caller may not
    /// know about yields exactly the answer of a form that does not exist.
    /// </summary>
    public async Task<FieldSearchCapabilitiesResponse> CapabilitiesAsync(RequestContext context, Guid formDefinitionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (formDefinitionId == Guid.Empty)
        {
            throw SearchException.RequestInvalid("select a form: advanced field search runs over one form");
        }

        var capabilities = await policy.GetFieldSearchCapabilitiesAsync(context.ToDelegatedUser(), new FieldSearchCapabilitiesQuery(context.OrganizationId, formDefinitionId), cancellationToken).ConfigureAwait(false);
        var fields = capabilities.Fields.Select(f => new FieldSearchCapabilityField(f.FieldDefinitionId, f.FieldLabel, f.FieldDataType, f.Operators,
            f.Options.Select(o => new FieldSearchOptionChoice(o.FieldOptionId, o.OptionLabel)).ToList(),
            f.FieldDataType == FieldSearchValueTypes.StringType ? limits.MinimumPartialTextLength : null,
            f.FieldDataType == FieldSearchValueTypes.StringType ? limits.MaximumTextValueLength : null)).ToList();
        var notSearchable = (capabilities.Configuration?.Fields ?? [])
            .Where(f => f.ApprovalState != "Active" || !capabilities.PlaintextSearchPermitted)
            .Select(f => new FieldSearchNotSearchableField(f.FieldDefinitionId, f.FieldLabel, f.Eligibility == "Encrypted" ? "ENCRYPTED" : "NOT_APPROVED"))
            .ToList();
        // Execution stays off until M3: the fields are offered so the conditions can be composed and checked, never run.
        return new FieldSearchCapabilitiesResponse(formDefinitionId, false, SearchUnavailableReasons.TrustedSearchNotEnabled, limits.MaximumConditions, ConditionJoinAnd, fields, notSearchable, capabilities.SearchPolicyRevision);
    }

    /// <summary>Validates the request shape, authorizes every condition against the Authority's capabilities, then refuses execution.</summary>
    public async Task<FieldSearchResponse> SearchAsync(RequestContext context, FieldSearchRequest? request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        FieldSearchRequestValidator.Validate(request, limits);
        if (request!.Conditions is { Count: > 0 } conditions)
        {
            var capabilities = await policy.GetFieldSearchCapabilitiesAsync(context.ToDelegatedUser(), new FieldSearchCapabilitiesQuery(context.OrganizationId, request.FormDefinitionId), cancellationToken).ConfigureAwait(false);
            var allowed = capabilities.PlaintextSearchPermitted ? capabilities.Fields.ToDictionary(f => f.FieldDefinitionId) : [];
            for (var index = 0; index < conditions.Count; index++)
            {
                var condition = conditions[index];
                if (!allowed.TryGetValue(condition.FieldDefinitionId, out var field) || field.FieldDataType != condition.ValueType)
                {
                    throw SearchException.FieldNotAllowed(index + 1);
                }

                if (!field.Operators.Contains(condition.Operator, StringComparer.Ordinal))
                {
                    throw SearchException.OperatorNotSupported(index + 1);
                }

                if (field.FieldDataType == FieldSearchValueTypes.OptionType && !field.Options.Any(o => string.Equals(o.FieldOptionId.ToString("D"), condition.Value, StringComparison.OrdinalIgnoreCase)))
                {
                    // An option the approval did not freeze (e.g. injected into business metadata, G21) is never searchable.
                    throw SearchException.OperatorNotSupported(index + 1);
                }
            }
        }

        throw SearchException.TrustedSearchNotEnabled();
    }

    /// <summary>No verified result can exist in M0, so no cursor can be valid; the page request is refused the same way.</summary>
    public static FieldSearchResponse Page(RequestContext context, FieldSearchPageRequest? request)
    {
        ArgumentNullException.ThrowIfNull(context);
        FieldSearchRequestValidator.ValidatePage(request);
        throw SearchException.TrustedSearchNotEnabled();
    }
}
