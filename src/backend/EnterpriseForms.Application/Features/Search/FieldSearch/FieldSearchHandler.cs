using EnterpriseForms.Application.Common;
using EnterpriseForms.Contracts.Features;

namespace EnterpriseForms.Application.Features.Search.FieldSearch;

/// <summary>
/// Phase C7 M0 Advanced Field Search (docs/architecture/PHASE_C7_ADVANCED_FIELD_SEARCH.md, decisions C-56).
/// <para>
/// Trusted execution needs the Authority's form-scoped search catalogue (AC-5) and Authority-held field search policy
/// (AC-6), neither of which exists yet. Until they do, this handler is the server-side gate: capabilities report that
/// field-value search is unavailable and offer no fields, and every well-formed search or page request is refused with
/// <c>SEARCH_VERIFICATION_UNAVAILABLE</c> / <c>TRUSTED_SEARCH_NOT_ENABLED</c> — never an empty HTTP 200 that could read as
/// "no records match". It has, by design, no business-store or record dependency: there is no fallback that could filter
/// business PostgreSQL rows (enforced by an architecture test).
/// </para>
/// </summary>
public sealed class FieldSearchHandler(FieldSearchLimits limits)
{
    public const string ConditionJoinAnd = "And";

    /// <summary>
    /// The same response for every form id, existing or not, so it reveals nothing about forms or fields. Field names are not
    /// read from business metadata: until AC-6 holds the policy there is no trustworthy source of searchable fields (D5).
    /// </summary>
    public FieldSearchCapabilitiesResponse Capabilities(RequestContext context, Guid formDefinitionId)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (formDefinitionId == Guid.Empty)
        {
            throw SearchException.RequestInvalid("select a form: advanced field search runs over one form");
        }

        return new FieldSearchCapabilitiesResponse(formDefinitionId, false, SearchUnavailableReasons.TrustedSearchNotEnabled, limits.MaximumConditions, ConditionJoinAnd, [], [], null);
    }

    /// <summary>Validates the request shape (so contract errors are reported precisely), then refuses execution.</summary>
    public FieldSearchResponse Search(RequestContext context, FieldSearchRequest? request)
    {
        ArgumentNullException.ThrowIfNull(context);
        FieldSearchRequestValidator.Validate(request, limits);
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
