using EnterpriseForms.Api.Composition;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Features.FormDefinitions.CreateFormDefinition;
using EnterpriseForms.Contracts.Features;
using Microsoft.AspNetCore.Http.HttpResults;

namespace EnterpriseForms.Api.Features.FormDefinitions;

public static class FormDefinitionEndpoints
{
    public const string OwnerName = "FormDefinitions";

    public static IEndpointRouteBuilder MapFormDefinitionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(ApiVersions.V1Prefix + "/form-definitions").WithTags(OwnerName).WithFeatureOwner(OwnerName).RequireAuthorization(BusinessAuthentication.Policy);

        group.MapPost("/", static async Task<Created<FormDefinitionResponse>> (CreateFormDefinitionRequest request, HttpContext http, CreateFormDefinitionHandler handler, CancellationToken cancellationToken) =>
            {
                var context = BusinessAuthentication.RequireContext(http);
                var response = await handler.HandleAsync(context, request, RequireIdempotencyKey(http), cancellationToken).ConfigureAwait(false);
                return TypedResults.Created($"{ApiVersions.V1Prefix}/form-definitions/{response.FormDefinitionId:D}", response);
            })
            .WithName("CreateFormDefinition")
            .WithSummary("Registers a form with the authority (designer role) and publishes a minimal v1 layout. Requires Idempotency-Key.");

        return endpoints;
    }

    public static string RequireIdempotencyKey(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        var key = http.Request.Headers["Idempotency-Key"].ToString();
        if (string.IsNullOrWhiteSpace(key) || key.Length is < 8 or > 100)
        {
            throw new BusinessException("IDEMPOTENCY_KEY_REQUIRED", 428, "Idempotency-Key header (8..100 chars) is required for this command");
        }

        return key;
    }
}
