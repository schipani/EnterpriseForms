using EnterpriseForms.Api.Composition;
using EnterpriseForms.Api.Features.FormDefinitions;
using EnterpriseForms.Application.Features.FormDefinitions.FormAccess;
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

        group.MapPatch("/{formDefinitionId:guid}/fields/{fieldDefinitionId:guid}", static async Task<Ok<FieldDefinitionResponse>> (Guid formDefinitionId, Guid fieldDefinitionId, UpdateFieldDefinitionRequest request, HttpContext http, FormDesignerHandlers handlers, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handlers.UpdateFieldAsync(BusinessAuthentication.RequireContext(http), formDefinitionId, fieldDefinitionId, request, cancellationToken).ConfigureAwait(false)))
            .WithName("UpdateFieldDefinition")
            .WithSummary("Edits a field's catalog label and description (expected revision required); key and storage type are permanent and a differing value is refused.");

        group.MapGet("/{formDefinitionId:guid}/fields/{fieldDefinitionId:guid}/protection", static async Task<Ok<FieldProtectionResponse>> (Guid formDefinitionId, Guid fieldDefinitionId, HttpContext http, FormDesignerHandlers handlers, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handlers.GetProtectionAsync(BusinessAuthentication.RequireContext(http), formDefinitionId, fieldDefinitionId, cancellationToken).ConfigureAwait(false)))
            .WithName("GetFieldProtection")
            .WithSummary("Classification, storage protection, search impact and migration state of a field.");

        group.MapPost("/{formDefinitionId:guid}/fields/{fieldDefinitionId:guid}/protection", static async Task<Ok<FieldProtectionResponse>> (Guid formDefinitionId, Guid fieldDefinitionId, StrengthenFieldProtectionRequest request, HttpContext http, FormDesignerHandlers handlers, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handlers.StrengthenProtectionAsync(BusinessAuthentication.RequireContext(http), formDefinitionId, fieldDefinitionId, request, cancellationToken).ConfigureAwait(false)))
            .WithName("StrengthenFieldProtection")
            .WithSummary("Audited migration: raise classification and/or switch a field to application encryption (starts the authority-governed background re-encryption of existing plaintext values). Downgrades are refused.");

        group.MapPost("/{formDefinitionId:guid}/fields/{fieldDefinitionId:guid}/protection/retry", static async Task<Ok<FieldProtectionResponse>> (Guid formDefinitionId, Guid fieldDefinitionId, RetryFieldProtectionMigrationRequest request, HttpContext http, FormDesignerHandlers handlers, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handlers.RetryProtectionMigrationAsync(BusinessAuthentication.RequireContext(http), formDefinitionId, fieldDefinitionId, request, cancellationToken).ConfigureAwait(false)))
            .WithName("RetryFieldProtectionMigration")
            .WithSummary("Re-queues the failed records of a field's encryption migration (design permission; audited).");

        group.MapPost("/{formDefinitionId:guid}/fields/{fieldDefinitionId:guid}/protection/reclassify", static async Task<Ok<FieldProtectionResponse>> (Guid formDefinitionId, Guid fieldDefinitionId, ReclassifyFieldRequest request, HttpContext http, FormDesignerHandlers handlers, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handlers.ReclassifyFieldAsync(BusinessAuthentication.RequireContext(http), formDefinitionId, fieldDefinitionId, request, cancellationToken).ConfigureAwait(false)))
            .WithName("ReclassifyField")
            .WithSummary("Amendment A1: declassifies an encrypted field to Public/Internal through the authority's ReclassifyField command (organization owner or system administrator; governance approval reference).");

        group.MapPost("/{formDefinitionId:guid}/fields/{fieldDefinitionId:guid}/protection/downgrade", static async Task<Ok<FieldProtectionResponse>> (Guid formDefinitionId, Guid fieldDefinitionId, DowngradeFieldProtectionRequest request, HttpContext http, FormDesignerHandlers handlers, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handlers.DowngradeProtectionAsync(BusinessAuthentication.RequireContext(http), formDefinitionId, fieldDefinitionId, request, cancellationToken).ConfigureAwait(false)))
            .WithName("DowngradeFieldProtection")
            .WithSummary("Amendment A1: releases a Public/Internal field from application encryption (organization owner or system administrator; distinct principal for declassified fields) and starts the audited background decryption migration.");

        group.MapPost("/{formDefinitionId:guid}/fields/{fieldDefinitionId:guid}/protection/cancel-downgrade", static async Task<Ok<FieldProtectionResponse>> (Guid formDefinitionId, Guid fieldDefinitionId, CancelFieldProtectionDowngradeRequest request, HttpContext http, FormDesignerHandlers handlers, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handlers.CancelDowngradeAsync(BusinessAuthentication.RequireContext(http), formDefinitionId, fieldDefinitionId, request, cancellationToken).ConfigureAwait(false)))
            .WithName("CancelFieldProtectionDowngrade")
            .WithSummary("Amendment A1 rollback: re-requires encryption and re-encrypts already converted active values (organization owner or system administrator).");

        group.MapGet("/{formDefinitionId:guid}/access", static async Task<Ok<FormAccessResponse>> (Guid formDefinitionId, HttpContext http, FormAccessHandlers handlers, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handlers.GetAsync(BusinessAuthentication.RequireContext(http), formDefinitionId, cancellationToken).ConfigureAwait(false)))
            .WithName("GetFormAccess")
            .WithSummary("Who may design and who may create records, read from the independent Authorization Authority (design permission or organization administration required).");

        group.MapPost("/{formDefinitionId:guid}/access", static async Task<Ok<FormAccessResponse>> (Guid formDefinitionId, UpdateFormAccessRequest request, HttpContext http, FormAccessHandlers handlers, CancellationToken cancellationToken) =>
                TypedResults.Ok(await handlers.UpdateAsync(BusinessAuthentication.RequireContext(http), formDefinitionId, request, FormDefinitionEndpoints.RequireIdempotencyKey(http), cancellationToken).ConfigureAwait(false)))
            .WithName("UpdateFormAccess")
            .WithSummary("Grants/revokes create-record permission as an authority command; no form version is created and no existing record access changes.");

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
