using EnterpriseForms.Domain.Features.FormDefinitions;

namespace EnterpriseForms.Application.Features.FormDesigner.ProtectionMigration;

/// <summary>Phase C7 M4: a new reconciliation job, due now. Shared by the migration hand-off and the authorized verify-only request.</summary>
public static class FieldProtectionReconciliationRequests
{
    public static FieldProtectionReconciliation New(Guid tenantId, Guid formDefinitionId, Guid fieldDefinitionId, Guid? migrationId, Guid authorityCommandId, string direction, string mode, Guid? requestedByUserId, string? reason, DateTimeOffset now) => new()
    {
        FieldProtectionReconciliationId = Guid.CreateVersion7(),
        TenantId = tenantId,
        FormDefinitionId = formDefinitionId,
        FieldDefinitionId = fieldDefinitionId,
        FieldProtectionMigrationId = migrationId,
        AuthorityCommandId = authorityCommandId,
        MigrationDirection = direction,
        ReconciliationMode = mode,
        ReconciliationState = FieldProtectionReconciliationStates.Queued,
        RequestedByUserId = requestedByUserId,
        RequestReasonText = reason,
        ReconciliationRequestedOn = now,
        NextAttemptOn = now,
        ReconciliationModifiedOn = now,
    };
}
