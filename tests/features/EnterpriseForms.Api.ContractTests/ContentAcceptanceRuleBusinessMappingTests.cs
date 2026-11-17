using EnterpriseForms.Application.Features.FormDesigner.ProtectionMigration;
using EnterpriseForms.Application.Features.FormRecords.CreateFormRecord;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// E3-M2b-1 (M2-CARV-08, contract §19.8): the business recovers from the Authority's 409 OPERATION_RULES_OUTDATED with a fresh
/// operation, never by retrying the refused one. Record creation treats it like OPERATION_EXPIRED in step 1 (a staged operation
/// that can never be accepted) and like IDEMPOTENCY_CONFLICT in step 2 (the creation's own key is unusable), so step 3's fresh
/// retry key follows; the protection migration keeps the item Pending and the next attempt number prepares a fresh operation.
/// These are the exact predicates the handlers' catch filters use; every pre-existing code keeps its classification.
/// </summary>
public sealed class ContentAcceptanceRuleBusinessMappingTests
{
    private const string RulesOutdated = "OPERATION_RULES_OUTDATED";

    [Fact]
    public void RecordCreationStepOneTreatsOutdatedRulesLikeAnExpiredOperation()
    {
        Assert.True(CreateFormRecordHandler.IsAbandonedOperation(RulesOutdated));
        Assert.True(CreateFormRecordHandler.IsAbandonedOperation("OPERATION_EXPIRED"));
        Assert.True(CreateFormRecordHandler.IsAbandonedOperation("OPERATION_STATE_INVALID"));
        foreach (var other in new[] { "CONTENT_REVISION_CONFLICT", "IDEMPOTENCY_CONFLICT", "CANDIDATE_MISMATCH", "INTEGRITY_QUARANTINE", "AUTHORITY_NOT_READY" })
        {
            Assert.False(CreateFormRecordHandler.IsAbandonedOperation(other)); // integrity failures propagate; conflicts keep their own path
        }
    }

    [Fact]
    public void RecordCreationStepTwoTreatsOutdatedRulesLikeAnUnusableKey()
    {
        Assert.True(CreateFormRecordHandler.IsUnusableOperationKey(RulesOutdated));
        Assert.True(CreateFormRecordHandler.IsUnusableOperationKey("IDEMPOTENCY_CONFLICT"));
        foreach (var other in new[] { "CONTENT_REVISION_CONFLICT", "OPERATION_EXPIRED", "CANDIDATE_MISMATCH", "INTEGRITY_QUARANTINE", "AUTHORITY_NOT_READY" })
        {
            Assert.False(CreateFormRecordHandler.IsUnusableOperationKey(other));
        }
    }

    [Fact]
    public void TheProtectionMigrationRetriesOutdatedRulesWithAFreshAttempt()
    {
        Assert.True(FieldProtectionMigrationService.IsRetryableConflict(RulesOutdated));
        foreach (var existing in new[] { "CONTENT_REVISION_CONFLICT", "STALE_AUTHORIZATION_REVISION", "OPERATION_STATE_INVALID", "OPERATION_EXPIRED" })
        {
            Assert.True(FieldProtectionMigrationService.IsRetryableConflict(existing));
        }

        foreach (var other in new[] { "IDEMPOTENCY_CONFLICT", "CANDIDATE_MISMATCH", "INTEGRITY_QUARANTINE", "FORBIDDEN" })
        {
            Assert.False(FieldProtectionMigrationService.IsRetryableConflict(other));
        }

        // A fresh attempt is a fresh operation: the next attempt number changes the idempotency key (and so the operation id).
        var migration = Guid.CreateVersion7();
        var record = Guid.CreateVersion7();
        Assert.NotEqual(FieldProtectionMigrationService.ReprotectionIdempotencyKey(migration, record, 3, 1), FieldProtectionMigrationService.ReprotectionIdempotencyKey(migration, record, 3, 2));
    }
}
