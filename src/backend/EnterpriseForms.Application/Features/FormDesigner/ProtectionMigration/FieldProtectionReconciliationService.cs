using System.Security.Cryptography;
using System.Text.Json;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.Documents.Ports;
using EnterpriseForms.Application.Features.FormRecords.Encryption;
using EnterpriseForms.Application.Features.FormRecords.Staging;
using EnterpriseForms.Application.Features.FormRecords.Values;
using EnterpriseForms.Domain.Features.Audit;
using EnterpriseForms.Domain.Features.FormDefinitions;
using EnterpriseForms.Domain.Features.FormRecords;

namespace EnterpriseForms.Application.Features.FormDesigner.ProtectionMigration;

/// <summary>
/// Phase C7 M4 — G19 reconciliation of a field's protection decision against the Authorization Authority's complete record
/// population, under the separate field-protection verifier identity (Worker only, OD-1). One leased step at a time:
/// <list type="number">
/// <item><b>Round start.</b> The Authority's migration catalogue (complete registered population of the form, records no
/// user can discover included) is read in full and compared with the business form_records rows in both directions; the
/// counts are reported, never used to narrow the population.</item>
/// <item><b>Census pages.</b> For every accepted, non-quarantined entry the immutable rows of its accepted operation are sent
/// to the Authority, which verifies them completely and counts the field's rows in the old and the target form from the
/// verified bytes (the first round re-verifies every record; a catch-up round reuses what the Authority verified for an
/// unchanged operation without re-sending rows). The worker
/// also decrypts every target-form ciphertext row (AEAD tag and record/field/item AAD) and checks its logical type. The page
/// offset is a durable checkpoint.</item>
/// <item><b>Attestation.</b> The Authority computes the report from its own state under the organization lock. A round in
/// which heads moved or records were registered restarts (previous census results carry over); after a bounded number of
/// rounds the reconciliation ends incomplete rather than claiming anything.</item>
/// </list>
/// VerifyOnly never converts anything. MigrationCompletion hands records whose verified head still holds the old form back
/// to the migration (as pending items) and marks the migration Completed only on a complete attestation with every decrypt
/// check passing. Nothing is written to record content here; tampering detected by a census is quarantined by the Authority.
/// </summary>
public sealed class FieldProtectionReconciliationService(IBusinessStore store, IProtectionVerifierAuthority verifier, RecordCandidateStager stager, IFieldEncryptionService encryption, TimeProvider clock)
{
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    /// <summary>Census calls carry at most this many records (the Authority's batch bound, OD-6).</summary>
    public const int CensusBatchSize = 50;

    /// <summary>
    /// Absolute bound on catalogue rounds. A round ends with a snapshot expiry (a large form) or with records changed or added
    /// since the snapshot; each further round reuses what this reconciliation already verified and verifies the rest. A round
    /// that verifies nothing new also ends the reconciliation (no progress). Either way it ends Incomplete, never complete.
    /// </summary>
    public const int MaximumRounds = 20;

    /// <summary>Reconciliations per migration before the migration fails instead of looping (records keep reverting to the old form).</summary>
    public const int MaximumReconciliationsPerMigration = 10;

    private const string HeadChanged = "HEAD_CHANGED";

    /// <summary>Processes one step of one due reconciliation; returns false when none was due.</summary>
    public async Task<bool> ProcessOnceAsync(string workerName, CancellationToken cancellationToken)
    {
        FieldProtectionReconciliation leased;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            var next = await scope.LeaseNextProtectionReconciliationAsync(workerName, LeaseDuration, cancellationToken).ConfigureAwait(false);
            if (next is null)
            {
                return false;
            }

            next.ReconciliationState = FieldProtectionReconciliationStates.Running;
            leased = next;
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await StepAsync(leased, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await UpdateAsync(leased.FieldProtectionReconciliationId, r => { r.LeaseOwnerName = null; r.LeaseExpiresOn = null; r.NextAttemptOn = clock.GetUtcNow(); }, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            await BlockAsync(leased.FieldProtectionReconciliationId, "UNEXPECTED_WORKER_FAILURE", CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return true;
    }

    private async Task StepAsync(FieldProtectionReconciliation reconciliation, CancellationToken cancellationToken)
    {
        if (!verifier.IsConfigured)
        {
            await FinishAsync(reconciliation.FieldProtectionReconciliationId, FieldProtectionReconciliationStates.Unavailable, "VERIFIER_NOT_CONFIGURED", null, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            if (reconciliation.CatalogueSnapshotId is null)
            {
                await StartRoundAsync(reconciliation, cancellationToken).ConfigureAwait(false);
            }
            else if (reconciliation.CensusNextOffset < (reconciliation.CatalogueEntryCount ?? 0))
            {
                await CensusPageAsync(reconciliation, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await AttestAsync(reconciliation, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (AuthorityClientException ex) when (ex.ErrorCode == "CONTINUATION_INVALIDATED")
        {
            // The snapshot expired or the decision no longer scopes it: a new round re-reads everything (a superseded decision is refused there).
            await RestartRoundAsync(reconciliation.FieldProtectionReconciliationId, ex.ErrorCode, cancellationToken).ConfigureAwait(false);
        }
        catch (AuthorityClientException ex) when (ex.HttpStatusCode == 403)
        {
            await FinishAsync(reconciliation.FieldProtectionReconciliationId, FieldProtectionReconciliationStates.Superseded, "DECISION_SUPERSEDED", null, cancellationToken).ConfigureAwait(false);
        }
        catch (AuthorityClientException ex) when (ex.HttpStatusCode == 422 && ex.ErrorCode == "LIMIT_EXCEEDED")
        {
            // More records than the migration catalogue may list: nothing partial is attested or claimed.
            await FinishAsync(reconciliation.FieldProtectionReconciliationId, FieldProtectionReconciliationStates.Incomplete, "RECONCILIATION_LIMIT", null, cancellationToken).ConfigureAwait(false);
        }
        catch (AuthorityClientException ex) when (ex.Retryable || ex.HttpStatusCode == 503)
        {
            await BlockAsync(reconciliation.FieldProtectionReconciliationId, ex.ErrorCode, cancellationToken).ConfigureAwait(false);
        }
        catch (EncryptionUnavailableException)
        {
            await BlockAsync(reconciliation.FieldProtectionReconciliationId, "ENCRYPTION_UNAVAILABLE", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or System.Net.Http.HttpRequestException)
        {
            await BlockAsync(reconciliation.FieldProtectionReconciliationId, "TRANSIENT:" + ex.GetType().Name, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Round start: the complete Authority population, compared with the business rows in both directions (reported, never trusted).</summary>
    private async Task StartRoundAsync(FieldProtectionReconciliation reconciliation, CancellationToken cancellationToken)
    {
        if (reconciliation.ReconciliationRoundCount >= MaximumRounds || (reconciliation.ReconciliationRoundCount >= 2 && reconciliation.CensusVerifiedRecordCount == reconciliation.RoundStartVerifiedRecordCount))
        {
            await FinishAsync(reconciliation.FieldProtectionReconciliationId, FieldProtectionReconciliationStates.Incomplete, "RECONCILIATION_DID_NOT_CONVERGE", null, cancellationToken).ConfigureAwait(false);
            return;
        }

        var first = await verifier.IssueMigrationCatalogueAsync(reconciliation.TenantId, reconciliation.FormDefinitionId, reconciliation.AuthorityCommandId, cancellationToken).ConfigureAwait(false);
        var catalogue = new HashSet<Guid>(first.Entries.Select(e => e.FormRecordId));
        for (var page = first; page.NextOffset is { } offset;)
        {
            page = await verifier.ReadMigrationCatalogueAsync(first.SearchCatalogueSnapshotId, offset, cancellationToken).ConfigureAwait(false);
            catalogue.UnionWith(page.Entries.Select(e => e.FormRecordId));
        }

        if (catalogue.Count != first.TotalEntryCount)
        {
            throw new AuthorityClientException("CONTINUATION_INVALIDATED", 409, false, "the catalogue pages do not add up to its entry count");
        }

        HashSet<Guid> business;
        await using (var read = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            business = (await read.GetFormRecordIdsOfFormAsync(reconciliation.FormDefinitionId, cancellationToken).ConfigureAwait(false)).ToHashSet();
        }

        await UpdateAsync(reconciliation.FieldProtectionReconciliationId, r =>
        {
            r.CatalogueSnapshotId = first.SearchCatalogueSnapshotId;
            r.FirstRoundSnapshotId ??= first.SearchCatalogueSnapshotId;
            r.RoundStartVerifiedRecordCount = r.CensusVerifiedRecordCount;
            r.CatalogueEntryCount = first.TotalEntryCount;
            r.CensusNextOffset = 0;
            if (r.ResumeAfterFormRecordId is null)
            {
                // A full round: every accepted record is censused and decrypt-checked again (a resumed round keeps the earlier part's counts).
                r.CensusFailedRecordCount = 0;
                r.DecryptCheckedValueCount = 0;
                r.DecryptFailedValueCount = 0;
                r.LogicalTypeMismatchCount = 0;
            }

            r.ReconciliationRoundCount++;
            r.BusinessOnlyRecordCount = business.Count(id => !catalogue.Contains(id));
            // Registered records the business database does not list under this form (row deleted, or moved to another form).
            r.BusinessMissingRecordCount = catalogue.Count(id => !business.Contains(id));
            r.LastErrorCode = null;
            r.NextAttemptOn = clock.GetUtcNow();
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One catalogue page: census of every accepted entry, decrypt checks, hand-back of old-form records, then the durable offset.</summary>
    private async Task CensusPageAsync(FieldProtectionReconciliation reconciliation, CancellationToken cancellationToken)
    {
        FieldDefinition field;
        Dictionary<Guid, FieldOption> optionsById;
        List<FieldOption> options;
        await using (var read = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            field = await read.FindFieldDefinitionAsync(reconciliation.FormDefinitionId, reconciliation.FieldDefinitionId, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("field missing for reconciliation " + reconciliation.FieldProtectionReconciliationId);
            options = await read.GetFieldOptionsAsync(reconciliation.FormDefinitionId, cancellationToken).ConfigureAwait(false);
            optionsById = options.ToDictionary(o => o.FieldOptionId);
        }

        var snapshotId = reconciliation.CatalogueSnapshotId!.Value;
        var page = await verifier.ReadMigrationCatalogueAsync(snapshotId, reconciliation.CensusNextOffset, cancellationToken).ConfigureAwait(false);
        var encrypting = reconciliation.MigrationDirection == FieldProtectionMigrationDirections.Encrypt;
        // The first round verifies every record again; catch-up rounds may reuse what this reconciliation verified since its first snapshot.
        var reuseSince = reconciliation.ReconciliationRoundCount > 1 ? reconciliation.FirstRoundSnapshotId : null;
        // After a snapshot expiry the next round resumes after the last record this reconciliation verified (catalogue order is
        // record id order); the attestation still re-derives every current head, so a record changed meanwhile is caught.
        var resumeAfter = reconciliation.ResumeAfterFormRecordId;
        var progress = new CensusProgress();
        // Pending creations and quarantined records are classified by the Authority's attestation, not here.
        var accepted = page.Entries.Where(e => e.CommittedOperationId is not null && e.CommittedContentRevision > 0 && !e.ContentHeadQuarantined && !e.ResourceQuarantined
                                               && (resumeAfter is not { } after || e.FormRecordId.CompareTo(after) > 0)).ToList();
        // Rows are read and sent one census batch at a time, so progress starts at once even when the snapshot lifetime is short.
        foreach (var chunk in accepted.Chunk(CensusBatchSize))
        {
            var batch = new List<ProtectionCensusItem>(chunk.Length);
            int decryptChecked = 0, decryptFailed = 0, typeMismatch = 0;
            foreach (var entry in chunk)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var operation = entry.CommittedOperationId!.Value;
                var staged = await stager.ReadStagedAsync(entry.FormRecordId, operation, cancellationToken).ConfigureAwait(false);
                batch.Add(new ProtectionCensusItem(entry.FormRecordId, operation, staged.Items)); // rows always sent; the Authority decides whether a held result may stand
                if (!encrypting)
                {
                    continue;
                }

                foreach (var sealedRow in staged.Rows.OfType<FieldEncryptedValue>().Where(v => v.FieldDefinitionId == field.FieldDefinitionId))
                {
                    decryptChecked++;
                    if (!DecryptsToTheFieldType(sealedRow, field, options, optionsById, out var authenticated))
                    {
                        if (authenticated)
                        {
                            typeMismatch++;
                        }
                        else
                        {
                            decryptFailed++;
                        }
                    }
                }
            }

            try
            {
                var response = await verifier.TakeCensusAsync(snapshotId, batch, reuseSince, cancellationToken).ConfigureAwait(false);
                progress.Results.AddRange(response.Results);
                progress.DecryptChecked += decryptChecked;
                progress.DecryptFailed += decryptFailed;
                progress.TypeMismatch += typeMismatch;
                progress.LastRecordId = chunk[^1].FormRecordId;
            }
            catch (AuthorityClientException ex) when (ex.ErrorCode == "CONTINUATION_INVALIDATED" && progress.LastRecordId is not null)
            {
                // The snapshot expired mid-page: the Authority holds the census of the batches already done. Record that progress
                // (counts, hand-back, resume position) so the next round continues after it, then renew the round.
                await RecordProgressAsync(reconciliation, snapshotId, progress, advanceOffsetBy: null, cancellationToken).ConfigureAwait(false);
                throw;
            }
        }

        // The whole page is done (verified, or skipped as verified earlier in this reconciliation): resume after its last entry.
        progress.LastRecordId = page.Entries.Count > 0 ? page.Entries[^1].FormRecordId : progress.LastRecordId;
        await RecordProgressAsync(reconciliation, snapshotId, progress, advanceOffsetBy: page.Entries.Count, cancellationToken).ConfigureAwait(false);
    }

    private sealed class CensusProgress
    {
        public List<ProtectionCensusRecordResult> Results { get; } = [];

        public int DecryptChecked { get; set; }

        public int DecryptFailed { get; set; }

        public int TypeMismatch { get; set; }

        public Guid? LastRecordId { get; set; }
    }

    /// <summary>Durable checkpoint of census progress: counters, hand-back of old-form records, resume position and (at a page end) the offset.</summary>
    private async Task RecordProgressAsync(FieldProtectionReconciliation reconciliation, Guid snapshotId, CensusProgress progress, int? advanceOffsetBy, CancellationToken cancellationToken)
    {
        var results = progress.Results;
        var stillOldForm = results.Where(r => r.Verified && r.OldFormOccurrenceCount > 0).ToList();
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var current = await scope.FindProtectionReconciliationAsync(reconciliation.FieldProtectionReconciliationId, cancellationToken).ConfigureAwait(false);
        if (current is null || current.ReconciliationState != FieldProtectionReconciliationStates.Running || current.CatalogueSnapshotId != snapshotId)
        {
            return; // superseded meanwhile; nothing recorded
        }

        if (current.ReconciliationMode == FieldProtectionReconciliationModes.MigrationCompletion && current.FieldProtectionMigrationId is { } migrationId && stillOldForm.Count > 0)
        {
            await HandBackAsync(scope, migrationId, stillOldForm, cancellationToken).ConfigureAwait(false);
        }

        var now = clock.GetUtcNow();
        current.CensusVerifiedRecordCount += results.Count(r => r.Verified && !r.Reused);
        current.CensusReusedRecordCount += results.Count(r => r.Verified && r.Reused);
        current.CensusFailedRecordCount += results.Count(r => !r.Verified && r.Outcome != HeadChanged);
        current.DecryptCheckedValueCount += progress.DecryptChecked;
        current.DecryptFailedValueCount += progress.DecryptFailed;
        current.LogicalTypeMismatchCount += progress.TypeMismatch;
        current.CensusNextOffset += advanceOffsetBy ?? 0;
        current.ResumeAfterFormRecordId = progress.LastRecordId ?? current.ResumeAfterFormRecordId;
        current.LeaseOwnerName = null;
        current.LeaseExpiresOn = null;
        current.NextAttemptOn = now;
        current.ReconciliationModifiedOn = now;
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Records whose Authority-verified current head still holds the old form go back to the migration as pending items
    /// (an item wrongly marked completed is reopened). A record without a business row cannot be converted here; it stays
    /// reported by the attestation as old form remaining.
    /// </summary>
    private async Task HandBackAsync(IBusinessScope scope, Guid migrationId, List<ProtectionCensusRecordResult> stillOldForm, CancellationToken cancellationToken)
    {
        var migration = await scope.FindProtectionMigrationAsync(migrationId, cancellationToken).ConfigureAwait(false);
        if (migration is null || migration.MigrationState != FieldProtectionMigrationStates.Verifying)
        {
            return;
        }

        var present = await scope.GetExistingFormRecordIdsAsync(stillOldForm.Select(r => r.FormRecordId).ToList(), cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        foreach (var result in stillOldForm.Where(r => present.Contains(r.FormRecordId)))
        {
            var item = await scope.FindProtectionMigrationItemForRecordAsync(migrationId, result.FormRecordId, cancellationToken).ConfigureAwait(false);
            if (item is null)
            {
                scope.AddProtectionMigrationItem(new FieldProtectionMigrationItem { FieldProtectionMigrationItemId = Guid.CreateVersion7(), FieldProtectionMigrationId = migrationId, TenantId = migration.TenantId, FormRecordId = result.FormRecordId, LastErrorCode = "FOUND_BY_RECONCILIATION", ItemModifiedOn = now });
                migration.TotalRecordCount++;
                migration.TotalValueCount += result.OldFormOccurrenceCount;
            }
            else if (item.ItemState == FieldProtectionMigrationItemStates.Completed)
            {
                item.ItemState = FieldProtectionMigrationItemStates.Pending;
                item.LastErrorCode = "REOPENED_BY_RECONCILIATION";
                item.ItemModifiedOn = now;
                migration.CompletedRecordCount = Math.Max(0, migration.CompletedRecordCount - 1);
            }
        }

        migration.MigrationModifiedOn = now;
    }

    /// <summary>Attestation: the Authority's report decides; a round whose heads or population moved restarts (bounded).</summary>
    private async Task AttestAsync(FieldProtectionReconciliation reconciliation, CancellationToken cancellationToken)
    {
        var attestation = await verifier.AttestAsync(reconciliation.CatalogueSnapshotId!.Value, cancellationToken).ConfigureAwait(false);
        int retainedOldFormRows;
        await using (var read = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            var oldFormRows = await read.CountOldFormRowsOfFieldAsync(reconciliation.FieldDefinitionId, encryptedForm: reconciliation.MigrationDirection == FieldProtectionMigrationDirections.Decrypt, cancellationToken).ConfigureAwait(false);
            retainedOldFormRows = Math.Max(0, oldFormRows - attestation.OldFormRemainingOccurrenceCount);
        }

        if (attestation.NotYetCensusedRecordCount > 0 && reconciliation.ReconciliationRoundCount < MaximumRounds)
        {
            // Heads changed or records were registered since the snapshot: a new round censuses only what is new (held census results carry over).
            await RecordAttestationAsync(reconciliation.FieldProtectionReconciliationId, attestation, retainedOldFormRows, cancellationToken).ConfigureAwait(false);
            await RestartRoundAsync(reconciliation.FieldProtectionReconciliationId, "SCOPE_MOVED", cancellationToken, fullRound: true).ConfigureAwait(false);
            return;
        }

        string? reason = attestation.AttestationIncompleteReason;
        if (reason is null && reconciliation.MigrationDirection == FieldProtectionMigrationDirections.Encrypt)
        {
            var current = await ReloadAsync(reconciliation.FieldProtectionReconciliationId, cancellationToken).ConfigureAwait(false);
            reason = current.DecryptFailedValueCount > 0 ? "DECRYPT_CHECK_FAILED"
                : current.LogicalTypeMismatchCount > 0 ? "LOGICAL_TYPE_MISMATCH"
                : current.DecryptCheckedValueCount != attestation.TargetFormOccurrenceCount ? "DECRYPT_CHECK_INCOMPLETE"
                : null;
        }

        await RecordAttestationAsync(reconciliation.FieldProtectionReconciliationId, attestation, retainedOldFormRows, cancellationToken).ConfigureAwait(false);
        await FinishAsync(reconciliation.FieldProtectionReconciliationId, reason is null ? FieldProtectionReconciliationStates.Complete : FieldProtectionReconciliationStates.Incomplete, reason, attestation, cancellationToken).ConfigureAwait(false);
    }

    private async Task RecordAttestationAsync(Guid reconciliationId, ProtectionAttestation attestation, int retainedOldFormRows, CancellationToken cancellationToken) =>
        await UpdateAsync(reconciliationId, r =>
        {
            r.AuthorityAttestationId = attestation.ProtectionReconciliationAttestationId;
            r.AttestationDigestHex = attestation.AttestationDigestHex;
            r.AttestationAuditJournalRevision = attestation.AuditJournalRevision;
            r.AttestedOn = attestation.AttestedOn;
            r.AttestationComplete = attestation.AttestationComplete;
            r.AttestationIncompleteReason = attestation.AttestationIncompleteReason;
            r.AttestationReportJson = JsonSerializer.Serialize(attestation);
            r.RetainedOldFormRowCount = retainedOldFormRows;
        }, cancellationToken).ConfigureAwait(false);

    /// <summary>A new round. <paramref name="fullRound"/>: heads changed or records were added, so nothing is skipped (resume position cleared); otherwise (snapshot expiry) the round resumes after the last record already verified.</summary>
    private async Task RestartRoundAsync(Guid reconciliationId, string reason, CancellationToken cancellationToken, bool fullRound = false) =>
        await UpdateAsync(reconciliationId, r =>
        {
            r.CatalogueSnapshotId = null;
            r.CatalogueEntryCount = null;
            r.CensusNextOffset = 0;
            if (fullRound)
            {
                r.ResumeAfterFormRecordId = null;
            }

            r.LastErrorCode = reason;
            r.NextAttemptOn = clock.GetUtcNow();
        }, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Terminal state, the business audit event (counts only) and, for MigrationCompletion, the migration's outcome. The
    /// migration is touched only while it is still Verifying (a cancelled downgrade or a retry has moved on).
    /// </summary>
    private async Task FinishAsync(Guid reconciliationId, string state, string? reason, ProtectionAttestation? attestation, CancellationToken cancellationToken)
    {
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var reconciliation = await scope.FindProtectionReconciliationAsync(reconciliationId, cancellationToken).ConfigureAwait(false);
        if (reconciliation is null || reconciliation.ReconciliationState is not (FieldProtectionReconciliationStates.Running or FieldProtectionReconciliationStates.Queued or FieldProtectionReconciliationStates.Blocked))
        {
            return;
        }

        var now = clock.GetUtcNow();
        reconciliation.ReconciliationState = state;
        reconciliation.LastErrorCode = reason;
        reconciliation.ReconciliationCompletedOn = now;
        reconciliation.ReconciliationModifiedOn = now;
        reconciliation.LeaseOwnerName = null;
        reconciliation.LeaseExpiresOn = null;
        reconciliation.NextAttemptOn = DateTimeOffset.MaxValue;
        scope.AddAuditEvent(Audit(reconciliation, AuditActions.FieldProtectionReconciliationAttested, state == FieldProtectionReconciliationStates.Complete ? "Success" : "Failed",
            state switch
            {
                FieldProtectionReconciliationStates.Complete => "the Authorization Authority attested that every accepted record's current revision holds the field only in the required form (current versions only; retained history not migrated)",
                FieldProtectionReconciliationStates.Unavailable => "no independent reconciliation: the field-protection verifier is not configured in this deployment (G19 not addressed here)",
                FieldProtectionReconciliationStates.Superseded => "the protection decision changed while the reconciliation ran; nothing was attested under it",
                _ => "reconciliation finished without a completeness claim",
            },
            new { mode = reconciliation.ReconciliationMode, state, reason, attestationId = attestation?.ProtectionReconciliationAttestationId, attestation?.ExpectedRecordCount, attestation?.CompliantRecordCount, attestation?.OldFormRemainingRecordCount, attestation?.QuarantinedRecordCount, attestation?.UnverifiedRecordCount, attestation?.PendingCreationRecordCount, attestation?.RetainedHistoryRevisionCount, reconciliation.BusinessOnlyRecordCount, reconciliation.BusinessMissingRecordCount, reconciliation.DecryptFailedValueCount }));

        if (reconciliation.ReconciliationMode == FieldProtectionReconciliationModes.MigrationCompletion && reconciliation.FieldProtectionMigrationId is { } migrationId)
        {
            var migration = await scope.FindProtectionMigrationAsync(migrationId, cancellationToken).ConfigureAwait(false);
            if (migration is not null && migration.MigrationState == FieldProtectionMigrationStates.Verifying)
            {
                var rounds = await scope.CountProtectionReconciliationsOfMigrationAsync(migrationId, cancellationToken).ConfigureAwait(false);
                var pending = await scope.CountPendingProtectionMigrationItemsAsync(migrationId, cancellationToken).ConfigureAwait(false);
                ApplyToMigration(scope, migration, reconciliation, state, reason, attestation, rounds, pending, now);
            }
        }

        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void ApplyToMigration(IBusinessScope scope, FieldProtectionMigration migration, FieldProtectionReconciliation reconciliation, string state, string? reason, ProtectionAttestation? attestation, int rounds, int pending, DateTimeOffset now)
    {
        migration.LeaseOwnerName = null;
        migration.LeaseExpiresOn = null;
        migration.MigrationModifiedOn = now;
        var decrypting = migration.MigrationDirection == FieldProtectionMigrationDirections.Decrypt;
        if (state == FieldProtectionReconciliationStates.Complete || state == FieldProtectionReconciliationStates.Unavailable)
        {
            migration.MigrationState = FieldProtectionMigrationStates.Completed;
            migration.MigrationCompletedOn = now;
            migration.MigrationVerifiedOn = now;
            migration.LastErrorCode = null;
            migration.NextAttemptOn = DateTimeOffset.MaxValue;
            var description = state == FieldProtectionReconciliationStates.Complete
                ? (decrypting ? "every accepted record decrypted; the Authorization Authority attested its complete record population (current versions; Amendment A1)" : "every accepted record re-protected; the Authorization Authority attested its complete record population (current versions; retained history not migrated)")
                : (decrypting ? "every listed record decrypted through the authority; post-check against the authority-accepted heads of the records the business database lists found no encrypted row of the field (Amendment A1; not an independent completeness proof, G19)" : "every listed record re-protected through the authority; post-check against the authority-accepted heads of the records the business database lists found no plaintext copy of the field (not an independent completeness proof, G19)");
            scope.AddAuditEvent(MigrationEvent(migration, AuditActions.FieldProtectionMigrationCompleted, "Success", description, new { migration.TotalRecordCount, migration.TotalValueCount, migration.CompletedRecordCount, direction = migration.MigrationDirection, reconciliationId = reconciliation.FieldProtectionReconciliationId, attestationId = attestation?.ProtectionReconciliationAttestationId, independentlyReconciled = state == FieldProtectionReconciliationStates.Complete }));
            return;
        }

        if (reason == "OLD_FORM_REMAINING" && pending > 0 && rounds < MaximumReconciliationsPerMigration)
        {
            // The census handed records back as pending items: convert them, then reconcile again.
            migration.MigrationState = FieldProtectionMigrationStates.Running;
            migration.LastErrorCode = "RECONCILIATION_FOUND_OLD_FORM";
            migration.NextAttemptOn = now;
            return;
        }

        migration.MigrationState = FieldProtectionMigrationStates.Failed;
        migration.LastErrorCode = rounds >= MaximumReconciliationsPerMigration ? "RECONCILIATION_DID_NOT_CONVERGE" : "RECONCILIATION_INCOMPLETE:" + (reason ?? state);
        migration.NextAttemptOn = now.AddHours(1);
        scope.AddAuditEvent(MigrationEvent(migration, AuditActions.FieldProtectionMigrationFailed, "Failed", "the Authorization Authority's reconciliation of the field's complete record population did not attest completion; the field is not claimed converted", new { reason, state, reconciliationId = reconciliation.FieldProtectionReconciliationId, attestationId = attestation?.ProtectionReconciliationAttestationId, direction = migration.MigrationDirection }));
    }

    /// <summary>
    /// The worker's own check of one target-form ciphertext row: authenticated decryption (AEAD tag; AAD binds tenant, record,
    /// field and item) and a logical type that the field accepts. The plaintext is discarded immediately and never logged.
    /// </summary>
    private bool DecryptsToTheFieldType(FieldEncryptedValue sealedRow, FieldDefinition field, IReadOnlyList<FieldOption> options, IReadOnlyDictionary<Guid, FieldOption> optionsById, out bool authenticated)
    {
        string wire;
        try
        {
            wire = FieldValueCodec.Format(sealedRow, optionsById, encryption);
        }
        catch (CryptographicException)
        {
            authenticated = false;
            return false;
        }

        authenticated = true;
        if (!string.Equals(sealedRow.EncryptedLogicalType, field.FieldDataType, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var typed = FieldValueCodec.CreateRow(field, null, options, wire, sealedRow.TenantId, sealedRow.FormRecordId, sealedRow.FieldValueItemId, sealedRow.CreatedOn, sealedRow.CreatedByUserId);
            return typed is not FieldEncryptedValue && FieldValueCodec.Format(typed, optionsById) == wire;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not EncryptionUnavailableException)
        {
            return false;
        }
    }

    private async Task BlockAsync(Guid reconciliationId, string code, CancellationToken cancellationToken) =>
        await UpdateAsync(reconciliationId, r =>
        {
            r.ReconciliationState = FieldProtectionReconciliationStates.Blocked;
            r.LastErrorCode = code.Length <= 200 ? code : code[..200];
            r.TransientFailureCount++;
            r.NextAttemptOn = clock.GetUtcNow().AddSeconds(30);
        }, cancellationToken).ConfigureAwait(false);

    private async Task<FieldProtectionReconciliation> ReloadAsync(Guid reconciliationId, CancellationToken cancellationToken)
    {
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        return await scope.FindProtectionReconciliationAsync(reconciliationId, cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidOperationException("reconciliation vanished: " + reconciliationId);
    }

    /// <summary>Applies a change to a non-terminal reconciliation and releases its lease; a terminal one is never changed again.</summary>
    private async Task UpdateAsync(Guid reconciliationId, Action<FieldProtectionReconciliation> change, CancellationToken cancellationToken)
    {
        using var bounded = cancellationToken.CanBeCanceled ? null : new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = bounded?.Token ?? cancellationToken;
        await using var scope = await store.BeginScopeAsync(token).ConfigureAwait(false);
        var reconciliation = await scope.FindProtectionReconciliationAsync(reconciliationId, token).ConfigureAwait(false);
        if (reconciliation is null || reconciliation.ReconciliationState is not (FieldProtectionReconciliationStates.Running or FieldProtectionReconciliationStates.Queued or FieldProtectionReconciliationStates.Blocked))
        {
            return;
        }

        change(reconciliation);
        reconciliation.LeaseOwnerName = null;
        reconciliation.LeaseExpiresOn = null;
        reconciliation.ReconciliationModifiedOn = clock.GetUtcNow();
        await scope.CommitAsync(token).ConfigureAwait(false);
    }

    private AuditEvent Audit(FieldProtectionReconciliation reconciliation, string action, string outcome, string description, object metadata) => new()
    {
        AuditEventId = Guid.CreateVersion7(),
        TenantId = reconciliation.TenantId,
        EventOccurredOn = clock.GetUtcNow(),
        EventCategory = "Security",
        EventAction = action,
        EventOutcome = outcome,
        ActorUserId = reconciliation.RequestedByUserId,
        ActorType = "Service",
        FormDefinitionId = reconciliation.FormDefinitionId,
        FieldDefinitionId = reconciliation.FieldDefinitionId,
        TargetEntityType = "field_protection_reconciliation",
        TargetEntityId = reconciliation.FieldProtectionReconciliationId,
        CorrelationIdentifier = Guid.CreateVersion7(),
        EventDescription = description,
        EventMetadataJson = JsonSerializer.Serialize(metadata),
    };

    private AuditEvent MigrationEvent(FieldProtectionMigration migration, string action, string outcome, string description, object metadata) => new()
    {
        AuditEventId = Guid.CreateVersion7(),
        TenantId = migration.TenantId,
        EventOccurredOn = clock.GetUtcNow(),
        EventCategory = "Security",
        EventAction = action,
        EventOutcome = outcome,
        ActorUserId = migration.StartedByUserId,
        ActorType = "Service",
        FormDefinitionId = migration.FormDefinitionId,
        FieldDefinitionId = migration.FieldDefinitionId,
        TargetEntityType = "field_protection_migration",
        TargetEntityId = migration.FieldProtectionMigrationId,
        CorrelationIdentifier = Guid.CreateVersion7(),
        EventDescription = description,
        EventMetadataJson = JsonSerializer.Serialize(metadata),
    };
}
