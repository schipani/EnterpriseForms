using System.Text.Json;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Domain.Features.Audit;

namespace EnterpriseForms.Application.Features.Audit;

/// <summary>
/// Appends a sanitized audit projection row inside an existing business scope (same transaction as the projection
/// write where one exists). Never receives protected values; metadata is limited to identifiers and status words.
/// </summary>
public static class AuditRecorder
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void Record(IBusinessScope scope, RequestContext context, TimeProvider clock, string category, string action, string description, Guid? formRecordId = null, Guid? formDefinitionId = null, Guid? fieldDefinitionId = null, Guid? fileAttachmentId = null, string? targetEntityType = null, Guid? targetEntityId = null, object? metadata = null, string outcome = "Success")
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(clock);
        scope.AddAuditEvent(new AuditEvent
        {
            AuditEventId = Guid.CreateVersion7(),
            TenantId = context.OrganizationId,
            EventOccurredOn = clock.GetUtcNow(),
            EventCategory = category,
            EventAction = action,
            EventOutcome = outcome,
            ActorUserId = context.LocalUserId,
            ActorType = "User",
            FormDefinitionId = formDefinitionId,
            FormRecordId = formRecordId,
            FieldDefinitionId = fieldDefinitionId,
            FileAttachmentId = fileAttachmentId,
            TargetEntityType = targetEntityType,
            TargetEntityId = targetEntityId,
            CorrelationIdentifier = context.CorrelationId,
            EventDescription = description.Length > 500 ? description[..500] : description,
            EventMetadataJson = metadata is null ? "{}" : JsonSerializer.Serialize(metadata, Json),
            EventSchemaVersion = 1,
        });
    }

    /// <summary>Convenience: own scope + commit (for reads and failures that have no accompanying projection write).</summary>
    public static async Task RecordAsync(IBusinessStore store, RequestContext context, TimeProvider clock, string category, string action, string description, Guid? formRecordId, string outcome, object? metadata, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        Record(scope, context, clock, category, action, description, formRecordId, metadata: metadata, outcome: outcome);
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}

public static class AuditCategories
{
    public const string Record = "Record";
    public const string Form = "Form";
    public const string Document = "Document";
    public const string Access = "Access";
    public const string Relationship = "Relationship";
}
