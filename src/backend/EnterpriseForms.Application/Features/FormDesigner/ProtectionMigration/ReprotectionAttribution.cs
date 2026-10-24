using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.FormRecords.Staging;
using EnterpriseForms.Domain.Features.FormDefinitions;

namespace EnterpriseForms.Application.Features.FormDesigner.ProtectionMigration;

/// <summary>
/// Phase C6 M0 (S4): which kind of accepted operation completed a migration item, decided from the operation's identity.
/// A reprotection operation's id is a pure function of its idempotency key
/// (<see cref="FieldProtectionMigrationService.ReprotectionIdempotencyKey"/>: migration, record, the accepted revision it
/// was built on, attempt number), so the worker can recognise its own accepted operation after a lost commit response
/// instead of assuming a user made it. Limitation: only the operation at the head is classified; when a user operation
/// was accepted after the worker's own, the item is attributed to the user operation, because the Authority's head read
/// does not expose earlier accepted operations (that would need the approved-but-not-yet-built AC-1).
/// </summary>
public static class ReprotectionAttribution
{
    /// <summary>Attempt numbers beyond the recorded counter that are still searched (a crash can lose the counter update of the attempt that committed).</summary>
    private const int AttemptSlack = 2;

    /// <summary>Upper bound on attempt numbers searched per item (bounded work for a corrupt counter).</summary>
    private const int MaximumAttemptsSearched = 1000;

    /// <summary>The operation id of one reprotection attempt (the same derivation the stager uses for the prepare).</summary>
    public static Guid OperationIdOf(Guid organizationId, Guid formRecordId, Guid migrationId, long baseContentRevision, int attemptNumber) =>
        RecordCandidateStager.DeriveOperationId(organizationId, formRecordId, FieldProtectionMigrationService.ReprotectionIdempotencyKey(migrationId, formRecordId, baseContentRevision, attemptNumber));

    /// <summary>
    /// Classifies the accepted head (<paramref name="headOperationId"/> at <paramref name="headContentRevision"/>): an
    /// operation of <paramref name="currentMigrationId"/>, of another migration with an item for this record, a user
    /// operation, or no accepted content (revision 0).
    /// </summary>
    public static string Classify(Guid organizationId, Guid formRecordId, Guid? headOperationId, long headContentRevision, Guid currentMigrationId, IEnumerable<ProtectionMigrationAttempt> attempts)
    {
        ArgumentNullException.ThrowIfNull(attempts);
        if (headContentRevision <= 0 || headOperationId is not { } head)
        {
            return FieldProtectionCompletionKinds.NoAcceptedContent;
        }

        var baseRevision = headContentRevision - 1;
        foreach (var attempt in attempts.OrderBy(a => a.FieldProtectionMigrationId == currentMigrationId ? 0 : 1))
        {
            var bound = Math.Min(Math.Max(attempt.AttemptCount, FieldProtectionMigrationService.MaxItemAttempts) + AttemptSlack, MaximumAttemptsSearched);
            for (var number = 0; number <= bound; number++)
            {
                if (OperationIdOf(organizationId, formRecordId, attempt.FieldProtectionMigrationId, baseRevision, number) == head)
                {
                    return attempt.FieldProtectionMigrationId == currentMigrationId ? FieldProtectionCompletionKinds.MigrationWorker : FieldProtectionCompletionKinds.OtherMigration;
                }
            }
        }

        return FieldProtectionCompletionKinds.UserOperation;
    }
}
