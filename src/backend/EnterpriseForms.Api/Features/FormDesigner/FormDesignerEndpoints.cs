using EnterpriseForms.Api.Composition;
using EnterpriseForms.Api.Features.FormDefinitions;
using EnterpriseForms.Application.Features.FormDesigner;
using EnterpriseForms.Contracts.Features;
using Microsoft.AspNetCore.Http.HttpResults;

namespace EnterpriseForms.Api.Features.FormDesigner;

/// <summary>Designer and catalog routes (PG-012/013/027–033/038/039). Every write requires the authority design permission.</summary>
public static class FormDesignerEndpoints
{
    public const string OwnerName = "FormDesigner";

    public static IEndpointRouteBuilder MapFormDesignerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(ApiVersions.V1Prefix + "/form-definitions").WithTags(OwnerName).WithFeatureOwner(OwnerName).RequireAuthorization(BusinessAuthentication.Policy);

        group.MapGet("/", static async Task<Ok<IReadOnlyList<FormDefinitionSummaryResponse>>> (HttpContext http, FormDesignerHandlers handlers, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handlers.ListAsync(BusinessAuthentication.RequireContext(http), cancellationToken).ConfigureAwait(false)))
            .WithName("ListFormDefinitions")
            .WithSummary("Forms the caller may design or use (authority-filtered; no other forms are disclosed).");

        group.MapGet("/{formDefinitionId:guid}", static async Task<Ok<FormDefinitionDetailResponse>> (Guid formDefinitionId, HttpContext http, FormDesignerHandlers handlers, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handlers.GetAsync(BusinessAuthentication.RequireContext(http), formDefinitionId, cancellationToken).ConfigureAwait(false)))
            .WithName("GetFormDefinition");

        group.MapPost("/designer", static async Task<Created<FormDefinitionDetailResponse>> (CreateFormDefinitionV2Request request, HttpContext http, FormDesignerHandlers handlers, CancellationToken cancellationToken) =>
            {
                var response = await handlers.CreateAsync(BusinessAuthentication.RequireContext(http), request, FormDefinitionEndpoints.RequireIdempotencyKey(http), cancellationToken).ConfigureAwait(false);
                return TypedResults.Created($"{ApiVersions.V1Prefix}/form-definitions/{response.Summary.FormDefinitionId:D}", response);
            })
            .WithName("CreateFormDefinitionDraft")
            .WithSummary("Registers a new form with the authority and opens an empty draft (no published version yet). Requires Idempotency-Key.");

        group.MapPost("/{formDefinitionId:guid}/fields", static async Task<Created<FieldDefinitionResponse>> (Guid formDefinitionId, AddFieldDefinitionRequest request, HttpContext http, FormDesignerHandlers handlers, CancellationToken cancellationToken) =>
            {
                var response = await handlers.AddFieldAsync(BusinessAuthentication.RequireContext(http), formDefinitionId, request, cancellationToken).ConfigureAwait(false);
                return TypedResults.Created($"{ApiVersions.V1Prefix}/form-definitions/{formDefinitionId:D}", response);
            })
            .WithName("AddFieldDefinition")
            .WithSummary("Adds a stable field identity (storage type immutable after creation).");

        group.MapPut("/{formDefinitionId:guid}/fields/{fieldDefinitionId:guid}/options", static async Task<Ok<FieldDefinitionResponse>> (Guid formDefinitionId, Guid fieldDefinitionId, UpdateFieldOptionsRequest request, HttpContext http, FormDesignerHandlers handlers, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handlers.UpdateOptionsAsync(BusinessAuthentication.RequireContext(http), formDefinitionId, fieldDefinitionId, request, cancellationToken).ConfigureAwait(false)))
            .WithName("UpdateFieldOptions")
            .WithSummary("Adds, relabels, reorders or deactivates options; keys and ids are stable, nothing is deleted.");

        group.MapPut("/{formDefinitionId:guid}/draft", static async Task<Ok<FormDraftResponse>> (Guid formDefinitionId, SaveDraftRequest request, HttpContext http, FormDesignerHandlers handlers, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handlers.SaveDraftAsync(BusinessAuthentication.RequireContext(http), formDefinitionId, request, cancellationToken).ConfigureAwait(false)))
            .WithName("SaveFormDraft")
            .WithSummary("Saves the draft layout (schema v2, validated) with draft-revision CAS.");

        group.MapPost("/{formDefinitionId:guid}/publish", static async Task<Created<FormVersionResponse>> (Guid formDefinitionId, PublishFormRequest request, HttpContext http, FormDesignerHandlers handlers, CancellationToken cancellationToken) =>
            {
                var response = await handlers.PublishAsync(BusinessAuthentication.RequireContext(http), formDefinitionId, request, cancellationToken).ConfigureAwait(false);
                return TypedResults.Created($"{ApiVersions.V1Prefix}/form-definitions/{formDefinitionId:D}/versions", response);
            })
            .WithName("PublishFormVersion")
            .WithSummary("Atomically publishes the draft as an immutable version and advances the current pointer (expected draft revision + current version number).");

        group.MapPost("/{formDefinitionId:guid}/restore-draft", static async Task<Ok<FormDraftResponse>> (Guid formDefinitionId, RestoreVersionRequest request, HttpContext http, FormDesignerHandlers handlers, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handlers.RestoreToDraftAsync(BusinessAuthentication.RequireContext(http), formDefinitionId, request, cancellationToken).ConfigureAwait(false)))
            .WithName("RestoreVersionToDraft")
            .WithSummary("Copies a published version into the draft; publishing creates a NEW version (history is never rewritten).");

        group.MapGet("/{formDefinitionId:guid}/versions", static async Task<Ok<IReadOnlyList<FormVersionResponse>>> (Guid formDefinitionId, HttpContext http, FormDesignerHandlers handlers, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handlers.ListVersionsAsync(BusinessAuthentication.RequireContext(http), formDefinitionId, cancellationToken).ConfigureAwait(false)))
            .WithName("ListFormVersions");

        group.MapPost("/{formDefinitionId:guid}/relationships", static async Task<Created<RelationshipDefinitionResponse>> (Guid formDefinitionId, CreateRelationshipDefinitionRequest request, HttpContext http, FormDesignerHandlers handlers, CancellationToken cancellationToken) =>
            {
                var response = await handlers.CreateRelationshipAsync(BusinessAuthentication.RequireContext(http), formDefinitionId, request, cancellationToken).ConfigureAwait(false);
                return TypedResults.Created($"{ApiVersions.V1Prefix}/form-definitions/{formDefinitionId:D}", response);
            })
            .WithName("CreateRelationshipDefinition")
            .WithSummary("Defines a reusable related-form list (e.g. AccusedAddressList): parent → child form, bounds, cycle-checked.");

        return endpoints;
    }
}
