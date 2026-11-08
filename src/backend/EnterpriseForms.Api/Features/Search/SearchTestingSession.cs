using EnterpriseForms.Api.Composition;
using EnterpriseForms.Application.Common;

namespace EnterpriseForms.Api.Features.Search;

/// <summary>
/// IX-4: the request context of the field-search endpoints. In the <c>Testing</c> environment only, a bearer caller (the integration
/// harness has no cookie session) may name an Authority session it registered for the same identity with
/// <c>X-Testing-Session-Digest</c>, so the indexed engine can be exercised through the public endpoints. The Authority refuses a
/// session of another identity (D-IX3-5), so the header cannot borrow one. In every other environment the header is ignored and
/// only the browser session counts.
/// </summary>
public static class SearchTestingSession
{
    public const string HeaderName = "X-Testing-Session-Digest";

    public static RequestContext Context(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        var context = BusinessAuthentication.RequireContext(http);
        if (context.SessionDigestBase64 is null
            && http.RequestServices.GetRequiredService<IHostEnvironment>().IsEnvironment("Testing")
            && http.Request.Headers[HeaderName].ToString() is { Length: > 0 } digest)
        {
            return context with { SessionDigestBase64 = digest };
        }

        return context;
    }
}
