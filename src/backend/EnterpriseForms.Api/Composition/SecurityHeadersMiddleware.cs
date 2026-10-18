namespace EnterpriseForms.Api.Composition;

/// <summary>Baseline response hardening for every response. Protected downloads add <c>Cache-Control: no-store</c> per operation.</summary>
public static class SecurityHeadersMiddleware
{
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app)
    {
        return app.Use(static (context, next) =>
        {
            var headers = context.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Cache-Control"] = "no-store";
            return next(context);
        });
    }
}
