using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.FormDesigner.ProtectionMigration;
using EnterpriseForms.Application.Features.FormRecords.Projection;
using EnterpriseForms.Domain.Features.FormDefinitions;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// Phase C6 Milestone 0 — field-protection status-count stabilization (architecture §1.1.3, S1–S4), pure logic:
/// the accepted-operation resolver never guesses from the highest staged revision; operation-identity attribution;
/// the status evaluator keeps the Authority-checked result and the local estimate apart and claims nothing on either alone.
/// The end-to-end reproductions with real fault injection are in the Docker suites (FieldProtectionMigrationTests,
/// FieldProtectionDowngradeTests).
/// </summary>
public sealed class PhaseC6StabilizationTests
{
    private static readonly Guid Organization = Guid.CreateVersion7();
    private static readonly Guid Record = Guid.CreateVersion7();

    // ---- S3: resolver ----

    [Fact]
    public void ACorroboratedProjectionResolvesToItsOperation()
    {
        var accepted = Guid.CreateVersion7();
        var resolution = AcceptedHeadProjection.Resolve(accepted, 2, [new(Guid.CreateVersion7(), 1), new(accepted, 2)]);
        Assert.Equal(AcceptedHeadProjection.ResolutionKind.Resolved, resolution.Kind);
        Assert.Equal(accepted, resolution.OperationId);
    }

    [Fact]
    public void AnAbandonedCandidateAtTheSameRevisionNeverReplacesTheProjectedAcceptedOperation()
    {
        var accepted = Guid.CreateVersion7();
        var abandoned = Guid.CreateVersion7();
        // Both candidates were staged for revision 2; only one was accepted. Staging order must not matter.
        foreach (var headers in new[] { new AcceptedHeadProjection.StagedHeader[] { new(accepted, 2), new(abandoned, 2) }, [new(abandoned, 2), new(accepted, 2)] })
        {
            var resolution = AcceptedHeadProjection.Resolve(accepted, 2, headers);
            Assert.Equal(accepted, resolution.OperationId);
        }
    }

    [Fact]
    public void AMissingDanglingOrUncorroboratedProjectionIsUnresolvedNeverTheHighestStagedRevision()
    {
        var accepted = Guid.CreateVersion7();
        var abandoned = Guid.CreateVersion7();
        AcceptedHeadProjection.StagedHeader[] headers = [new(accepted, 2), new(abandoned, 2), new(Guid.CreateVersion7(), 3)];

        // Missing pointer (e.g. a lost projection update after creation): the old code took the highest staged revision.
        Assert.Equal(AcceptedHeadProjection.ResolutionKind.Unresolved, AcceptedHeadProjection.Resolve(null, 2, headers).Kind);
        // Dangling pointer (no staged header of that operation).
        Assert.Equal(AcceptedHeadProjection.ResolutionKind.Unresolved, AcceptedHeadProjection.Resolve(Guid.CreateVersion7(), 2, headers).Kind);
        // Pointer and projected revision disagree (a header of the operation exists, but at another revision).
        Assert.Equal(AcceptedHeadProjection.ResolutionKind.Unresolved, AcceptedHeadProjection.Resolve(accepted, 3, headers).Kind);
        // No staged header at all.
        Assert.Equal(AcceptedHeadProjection.ResolutionKind.Unresolved, AcceptedHeadProjection.Resolve(accepted, 2, []).Kind);
        Assert.Null(AcceptedHeadProjection.Resolve(null, 3, headers).OperationId);
    }

    [Fact]
    public void RevisionZeroWithoutAnOperationRecordsThatTheAuthorityHoldsNoAcceptedContent()
    {
        var resolution = AcceptedHeadProjection.Resolve(null, 0, [new(Guid.CreateVersion7(), 1)]);
        Assert.Equal(AcceptedHeadProjection.ResolutionKind.NoAcceptedContent, resolution.Kind);
        Assert.Null(resolution.OperationId);
        // A pointer with revision 0 is not a valid projection: unresolved, not "no content".
        Assert.Equal(AcceptedHeadProjection.ResolutionKind.Unresolved, AcceptedHeadProjection.Resolve(Guid.CreateVersion7(), 0, []).Kind);
    }

    [Fact]
    public void AMigrationVisitsEveryHolderAndEveryUnresolvedRecordOnce()
    {
        var holder = Guid.CreateVersion7();
        var unresolved = Guid.CreateVersion7();
        var estimate = new LegacyRecordEstimate([new(holder, 2)], [unresolved, holder]);
        var visit = estimate.RecordsToVisit();
        Assert.Equal(2, visit.Count);
        Assert.Equal(2, visit.Single(r => r.FormRecordId == holder).PlaintextValueCount);
        Assert.Equal(0, visit.Single(r => r.FormRecordId == unresolved).PlaintextValueCount); // value count unknown, never guessed
        Assert.Equal(1, estimate.HolderRecordCount);
        Assert.Equal(2, estimate.HolderValueCount);
    }

    // ---- S4: attribution from the accepted operation's identity ----

    [Fact]
    public void TheWorkersOwnAcceptedOperationIsRecognisedAfterALostCommitResponse()
    {
        var migration = Guid.CreateVersion7();
        // The worker's attempt 1 was built on accepted revision 4, so it committed revision 5; its response was lost.
        var head = ReprotectionAttribution.OperationIdOf(Organization, Record, migration, 4, 1);
        Assert.Equal(FieldProtectionCompletionKinds.MigrationWorker, ReprotectionAttribution.Classify(Organization, Record, head, 5, migration, [new(migration, 1)]));
        // A crash can also lose the attempt-counter update: attempts just past the recorded counter are still recognised.
        var unrecorded = ReprotectionAttribution.OperationIdOf(Organization, Record, migration, 4, FieldProtectionMigrationService.MaxItemAttempts + 1);
        Assert.Equal(FieldProtectionCompletionKinds.MigrationWorker, ReprotectionAttribution.Classify(Organization, Record, unrecorded, 5, migration, [new(migration, 0)]));
    }

    [Fact]
    public void AUserOperationAnotherMigrationAndNoContentAreDistinguished()
    {
        var migration = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        ProtectionMigrationAttempt[] attempts = [new(migration, 2), new(other, 1)];

        Assert.Equal(FieldProtectionCompletionKinds.UserOperation, ReprotectionAttribution.Classify(Organization, Record, Guid.CreateVersion7(), 5, migration, attempts));
        Assert.Equal(FieldProtectionCompletionKinds.OtherMigration, ReprotectionAttribution.Classify(Organization, Record, ReprotectionAttribution.OperationIdOf(Organization, Record, other, 4, 0), 5, migration, attempts));
        Assert.Equal(FieldProtectionCompletionKinds.NoAcceptedContent, ReprotectionAttribution.Classify(Organization, Record, null, 0, migration, attempts));
        // The same attempt built on another revision is a different operation: not attributed to the worker.
        Assert.Equal(FieldProtectionCompletionKinds.UserOperation, ReprotectionAttribution.Classify(Organization, Record, ReprotectionAttribution.OperationIdOf(Organization, Record, migration, 3, 0), 5, migration, attempts));
        // Another record's operation never matches.
        Assert.Equal(FieldProtectionCompletionKinds.UserOperation, ReprotectionAttribution.Classify(Organization, Record, ReprotectionAttribution.OperationIdOf(Organization, Guid.CreateVersion7(), migration, 4, 0), 5, migration, attempts));
    }

    // ---- S1/S3: status evaluation ----

    private static readonly LegacyRecordEstimate Clean = new([], []);

    private static FieldProtectionMigration Migration(string direction, string state, bool verifiedClean, int remaining = 0, int unverified = 0) => new()
    {
        MigrationDirection = direction,
        MigrationState = state,
        MigrationVerifiedOn = state == FieldProtectionMigrationStates.Completed ? DateTimeOffset.UtcNow : null,
        VerificationCheckedOn = verifiedClean || remaining > 0 || unverified > 0 ? DateTimeOffset.UtcNow : null,
        VerifiedRemainingRecordCount = verifiedClean ? 0 : remaining,
        UnverifiedRecordCount = verifiedClean ? 0 : unverified,
        VerifiedRemainingValueCount = remaining,
    };

    [Fact]
    public void AVerifiedCleanMigrationWithAnAgreeingEstimateIsFullyEncrypted()
    {
        var status = FieldProtectionStatusEvaluator.Evaluate(true, false, Clean, Migration("Encrypt", "Completed", verifiedClean: true));
        Assert.True(status.FullyEncrypted);
        Assert.Equal("Encrypted", status.State);
    }

    [Fact]
    public void NeitherEvidenceAloneProducesTheClaim()
    {
        // A stale or forged estimate holder blocks the claim even though the stored check is clean …
        var staleHolder = new LegacyRecordEstimate([new(Record, 1)], []);
        var blockedByEstimate = FieldProtectionStatusEvaluator.Evaluate(true, false, staleHolder, Migration("Encrypt", "Completed", verifiedClean: true));
        Assert.False(blockedByEstimate.FullyEncrypted);
        Assert.Equal("EncryptionMigrationIncomplete", blockedByEstimate.State);

        // … an unresolved record blocks it (unknown, not guessed) …
        Assert.False(FieldProtectionStatusEvaluator.Evaluate(true, false, new LegacyRecordEstimate([], [Record]), Migration("Encrypt", "Completed", verifiedClean: true)).FullyEncrypted);

        // … and a "Completed" row without a recorded post-check (e.g. forged by a database writer) does not produce it either.
        var forged = Migration("Encrypt", "Completed", verifiedClean: false);
        Assert.Null(forged.VerificationCheckedOn);
        Assert.False(FieldProtectionStatusEvaluator.Evaluate(true, false, Clean, forged).FullyEncrypted);
    }

    [Fact]
    public void RemainingOrUnreadableRecordsInTheStoredCheckBlockTheClaim()
    {
        Assert.False(FieldProtectionStatusEvaluator.Evaluate(true, false, Clean, Migration("Encrypt", "Completed", verifiedClean: false, remaining: 1)).FullyEncrypted);
        Assert.False(FieldProtectionStatusEvaluator.Evaluate(true, false, Clean, Migration("Encrypt", "Completed", verifiedClean: false, unverified: 1)).FullyEncrypted);
        Assert.Equal("EncryptionMigrationRunning", FieldProtectionStatusEvaluator.Evaluate(true, false, Clean, Migration("Encrypt", "Running", verifiedClean: false)).State);
        Assert.Equal("EncryptionMigrationFailed", FieldProtectionStatusEvaluator.Evaluate(true, false, Clean, Migration("Encrypt", "Failed", verifiedClean: false, remaining: 1)).State);
    }

    [Fact]
    public void DecryptMigrationsFollowTheSameRules()
    {
        Assert.True(FieldProtectionStatusEvaluator.Evaluate(false, false, Clean, Migration("Decrypt", "Completed", verifiedClean: true)).FullyPlaintext);
        var staleEncrypted = new LegacyRecordEstimate([new(Record, 1)], []);
        var blocked = FieldProtectionStatusEvaluator.Evaluate(false, false, staleEncrypted, Migration("Decrypt", "Completed", verifiedClean: true));
        Assert.False(blocked.FullyPlaintext);
        Assert.NotEqual("Plaintext", blocked.State);
        Assert.False(FieldProtectionStatusEvaluator.Evaluate(false, false, Clean, Migration("Decrypt", "Failed", verifiedClean: false, remaining: 1)).FullyPlaintext);
        // A plaintext field that was never encrypted has nothing to claim.
        var never = FieldProtectionStatusEvaluator.Evaluate(false, false, Clean, null);
        Assert.Equal("Plaintext", never.State);
        Assert.False(never.FullyPlaintext);
    }

    [Fact]
    public void FieldsEncryptedFromCreationAndDocumentFieldsNeedNoMigration()
    {
        Assert.True(FieldProtectionStatusEvaluator.Evaluate(true, false, Clean, null).FullyEncrypted);
        Assert.False(FieldProtectionStatusEvaluator.Evaluate(true, false, new LegacyRecordEstimate([], [Record]), null).FullyEncrypted);
        Assert.True(FieldProtectionStatusEvaluator.Evaluate(true, true, Clean, null).FullyEncrypted);
        // A migration of the other direction (e.g. a cancelled downgrade followed by re-encryption pending) never yields the claim.
        Assert.False(FieldProtectionStatusEvaluator.Evaluate(true, false, Clean, Migration("Decrypt", "Completed", verifiedClean: true)).FullyEncrypted);
    }
}
