using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Domain.Features.FormDefinitions;

namespace EnterpriseForms.Application.Features.FormDesigner.ProtectionMigration;

/// <summary>
/// Field-protection status (Phase C6 M0, S1/S3). Two kinds of evidence are kept apart:
/// <list type="bullet">
/// <item><b>Worker-verified result</b> — the migration worker's last post-check read each listed record's
/// Authority-accepted head and counted rows of the old form in exactly that operation. Stored in the business database.</item>
/// <item><b>Local estimate</b> — the business database's own projection, which reports records it cannot resolve as
/// unresolved instead of guessing.</item>
/// </list>
/// "All listed records encrypted" (<c>fullyEncrypted</c>) and "… migrated to plaintext" (<c>fullyPlaintext</c>) are
/// claimed only when a completed migration's verified result shows nothing remaining and nothing unverified <b>and</b>
/// the local estimate agrees. Requiring both keeps a business-database forgery of the stored result from producing the
/// claim on its own. It is still not a guarantee against a hostile business-database administrator, who can alter both,
/// and the list of records comes from the business database (G19). Display/status only: never used for authorization,
/// decryption, reads or search.
/// </summary>
public static class FieldProtectionStatusEvaluator
{
    public sealed record Result(string State, bool FullyEncrypted, bool FullyPlaintext, bool VerifiedClean);

    public static Result Evaluate(bool encrypted, bool isFile, LegacyRecordEstimate estimate, FieldProtectionMigration? migration)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        var migrationMatchesMode = migration is null || (encrypted ? migration.MigrationDirection == FieldProtectionMigrationDirections.Encrypt : migration.MigrationDirection == FieldProtectionMigrationDirections.Decrypt);
        var estimateClean = estimate.HolderRecordCount == 0 && estimate.UnresolvedRecordIds.Count == 0;
        var verifiedClean = migration is not null && IsVerifiedClean(migration);
        var fullyEncrypted = encrypted && migrationMatchesMode && estimateClean && (migration is null || verifiedClean);
        var fullyPlaintext = !encrypted && !isFile && migration is not null && migrationMatchesMode && estimateClean && verifiedClean;

        string state;
        if (encrypted)
        {
            state = fullyEncrypted ? "Encrypted" : migration is null || !migrationMatchesMode ? "EncryptionPendingRewrite" : InProgressState(migration, "Encryption");
        }
        else
        {
            state = estimateClean && (migration is null || fullyPlaintext) ? "Plaintext" : migration is null || !migrationMatchesMode ? "DecryptionPendingRewrite" : InProgressState(migration, "Decryption");
        }

        return new Result(state, fullyEncrypted, fullyPlaintext, verifiedClean);
    }

    /// <summary>A completed migration whose stored post-check found no record holding the old form and no record it could not read.</summary>
    public static bool IsVerifiedClean(FieldProtectionMigration migration)
    {
        ArgumentNullException.ThrowIfNull(migration);
        return migration.MigrationState == FieldProtectionMigrationStates.Completed
               && migration.MigrationVerifiedOn is not null
               && migration.VerificationCheckedOn is not null
               && migration.VerifiedRemainingRecordCount == 0
               && migration.UnverifiedRecordCount == 0;
    }

    private static string InProgressState(FieldProtectionMigration migration, string noun) => migration.MigrationState switch
    {
        FieldProtectionMigrationStates.Queued => noun + "MigrationQueued",
        FieldProtectionMigrationStates.Running => noun + "MigrationRunning",
        FieldProtectionMigrationStates.Blocked => noun + "MigrationBlocked",
        FieldProtectionMigrationStates.Failed => noun + "MigrationFailed",
        _ => noun + "MigrationIncomplete",
    };
}
