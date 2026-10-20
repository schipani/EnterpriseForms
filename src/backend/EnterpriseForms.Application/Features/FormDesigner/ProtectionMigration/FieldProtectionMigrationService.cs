using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.Documents.Ports;
using EnterpriseForms.Application.Features.FormRecords.Encryption;
using EnterpriseForms.Application.Features.FormRecords.Integrity;
using EnterpriseForms.Application.Features.FormRecords.Staging;
using EnterpriseForms.Application.Features.FormRecords.Values;
using EnterpriseForms.Domain.Features.Audit;
using EnterpriseForms.Domain.Features.Documents;
using EnterpriseForms.Domain.Features.FormDefinitions;
using EnterpriseForms.Domain.Features.FormRecords;

namespace EnterpriseForms.Application.Features.FormDesigner.ProtectionMigration;

/// <summary>
/// Background plaintext→encrypted migration (B-28). For every record that still holds a plaintext copy of a strengthened
/// field: read the accepted head from the authority (under the migration's command), read the record's immutable
/// committed rows, replace the field's plaintext rows with encrypted rows of the same item identity (self-checked by
/// decrypting them again), carry every other row forward unchanged, and commit through the ordinary prepare → stage →
/// read-back → commit protocol as a reprotection operation. The authority verifies the current snapshot and accepts
/// only that exact shape of change; there is no signing shortcut. Batched, leased, idempotent, resumable; concurrent user
/// edits win (they re-protect on write) and are recognised as completion.
/// </summary>
public sealed class FieldProtectionMigrationService(IBusinessStore store, IAuthorizationAuthorityClient authority, RecordCandidateStager stager, IFieldEncryptionService encryption, TimeProvider clock)
{
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);
    public const int BatchSize = 25;
    public const int MaxItemAttempts = 5;

    /// <summary>Processes one batch of one due migration; returns false when none was due.</summary>
    public async Task<bool> ProcessOnceAsync(string workerName, CancellationToken cancellationToken)
    {
        FieldProtectionMigration migration;
        List<FieldProtectionMigrationItem> batch;
        FieldDefinition field;
        List<FieldOption> options;
        await using (var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            var leased = await scope.LeaseNextProtectionMigrationAsync(workerName, LeaseDuration, cancellationToken).ConfigureAwait(false);
            if (leased is null)
            {
                return false;
            }

            migration = leased;
            migration.MigrationState = FieldProtectionMigrationStates.Running;
            field = await scope.FindFieldDefinitionAsync(migration.FormDefinitionId, migration.FieldDefinitionId, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("field missing for migration " + migration.FieldProtectionMigrationId);
            options = await scope.GetFieldOptionsAsync(migration.FormDefinitionId, cancellationToken).ConfigureAwait(false);
            batch = await scope.GetPendingProtectionMigrationItemsAsync(migration.FieldProtectionMigrationId, BatchSize, cancellationToken).ConfigureAwait(false);
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return await ProcessLeasedAsync(migration, field, options, batch, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Graceful shutdown (e.g. `docker compose restart worker`) mid-batch: hand the lease back now instead of letting it expire,
            // so another worker resumes immediately. Items already recorded stay recorded; the rest are still Pending.
            await ReleaseLeaseAsync(migration.FieldProtectionMigrationId).ConfigureAwait(false);
            throw;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // An unexpected failure must not pin the lease for its full duration: park the migration as Blocked and retry shortly.
            await SettleAsync(migration.FieldProtectionMigrationId, FieldProtectionMigrationStates.Blocked, "UNEXPECTED_WORKER_FAILURE", TimeSpan.FromSeconds(30), CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<bool> ProcessLeasedAsync(FieldProtectionMigration migration, FieldDefinition field, List<FieldOption> options, List<FieldProtectionMigrationItem> batch, CancellationToken cancellationToken)
    {
        var decrypting = migration.MigrationDirection == FieldProtectionMigrationDirections.Decrypt;
        var expectedMode = decrypting ? FieldProtectionModes.Standard : FieldProtectionModes.ApplicationEncrypted;
        if (field.FieldProtectionMode != expectedMode)
        {
            // The field's live mode no longer matches the migration (e.g. a cancelled downgrade superseded it): never convert against the current decision.
            await SettleAsync(migration.FieldProtectionMigrationId, FieldProtectionMigrationStates.Failed, decrypting ? "FIELD_NOT_RELEASED" : "FIELD_NOT_ENCRYPTED", TimeSpan.FromHours(1), cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (!encryption.IsAvailable)
        {
            // Key provider outage: nothing is attempted, nothing is lost; retried when the provider returns.
            await SettleAsync(migration.FieldProtectionMigrationId, FieldProtectionMigrationStates.Blocked, "ENCRYPTION_UNAVAILABLE", TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (batch.Count == 0)
        {
            await FinalizeAsync(migration, field, cancellationToken).ConfigureAwait(false);
            return true;
        }

        var optionsById = options.ToDictionary(o => o.FieldOptionId);
        string? blockedBy = null;
        foreach (var item in batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var outcome = await MigrateRecordAsync(migration, field, options, optionsById, item, cancellationToken).ConfigureAwait(false);
                await RecordItemAsync(migration.FieldProtectionMigrationId, item.FieldProtectionMigrationItemId, outcome, cancellationToken).ConfigureAwait(false);
            }
            catch (EncryptionUnavailableException)
            {
                blockedBy = "ENCRYPTION_UNAVAILABLE";
                break;
            }
            catch (AuthorityClientException ex) when (ex.Retryable || ex.HttpStatusCode == 503)
            {
                blockedBy = ex.ErrorCode; // authority/signer unavailable or gate closed: retry the whole batch later
                break;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or System.Net.Http.HttpRequestException)
            {
                blockedBy = "TRANSIENT:" + ex.GetType().Name;
                break;
            }
        }

        if (blockedBy is not null)
        {
            await SettleAsync(migration.FieldProtectionMigrationId, FieldProtectionMigrationStates.Blocked, blockedBy, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            return true;
        }

        await SettleAsync(migration.FieldProtectionMigrationId, FieldProtectionMigrationStates.Running, null, TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private sealed record ItemOutcome(string State, string? ErrorCode, Guid? OperationId, long? ContentRevision, bool ByUserCommit, bool Retry);

    private async Task<ItemOutcome> MigrateRecordAsync(FieldProtectionMigration migration, FieldDefinition field, IReadOnlyList<FieldOption> options, IReadOnlyDictionary<Guid, FieldOption> optionsById, FieldProtectionMigrationItem item, CancellationToken cancellationToken)
    {
        // 1. The authority's accepted head for the record (metadata only), read under the migration's command.
        AcceptedContentRevision head;
        try
        {
            head = await authority.GetCommittedContentRevisionForReprotectionAsync(migration.TenantId, item.FormRecordId, migration.AuthorityCommandId, cancellationToken).ConfigureAwait(false);
        }
        catch (AuthorityClientException ex) when (!ex.Retryable && ex.HttpStatusCode is 403 or 404 or 423)
        {
            return Failure(item, ex.ErrorCode);
        }

        if (head.CommittedOperationId is null || head.CommittedContentRevision == 0)
        {
            return Failure(item, "NO_COMMITTED_CONTENT");
        }

        // 2. The record's immutable committed rows; nothing to do when the field holds no plaintext copy any more (a user's commit re-protected it).
        var snapshot = await stager.ReadStagedAsync(item.FormRecordId, head.CommittedOperationId.Value, cancellationToken).ConfigureAwait(false);
        if (snapshot.Header is null)
        {
            return Failure(item, "COMMITTED_ROWS_MISSING");
        }

        var decrypting = migration.MigrationDirection == FieldProtectionMigrationDirections.Decrypt;
        var sourceRows = snapshot.Rows.OfType<FieldValueRowBase>().Where(v => v.FieldDefinitionId == field.FieldDefinitionId && (decrypting ? v is FieldEncryptedValue : v is not FieldEncryptedValue)).ToList();
        if (sourceRows.Count == 0)
        {
            return new ItemOutcome(FieldProtectionMigrationItemStates.Completed, null, head.CommittedOperationId, head.CommittedContentRevision, ByUserCommit: true, Retry: false);
        }

        // 3. Candidate: same header, same attachments, same other values; the field's rows converted under the same item ids and self-checked
        //    (encrypt: seal then decrypt-compare; decrypt: decrypt, re-encode as the typed row, format-compare).
        var header = CandidateBuilder.CloneHeader(snapshot.Header);
        var rows = new List<ISignedContentRow>();
        foreach (var value in snapshot.Rows.OfType<FieldValueRowBase>())
        {
            if (value.FieldDefinitionId != field.FieldDefinitionId || (decrypting ? value is not FieldEncryptedValue : value is FieldEncryptedValue))
            {
                rows.Add(Documents.UploadSessions.UploadSessionHandlers.CloneValue(value));
                continue;
            }

            FieldValueRowBase converted;
            if (decrypting)
            {
                string wire;
                try
                {
                    wire = FieldValueCodec.Format(value, optionsById, encryption);
                }
                catch (System.Security.Cryptography.CryptographicException)
                {
                    // Tampered or foreign ciphertext (AEAD tag/AAD mismatch): this record fails closed; nothing is written and the worker keeps running.
                    return Failure(item, "DEPROTECT_AUTHENTICATION_FAILED");
                }

                converted = FieldValueCodec.CreateRow(field, null, options, wire, value.TenantId, value.FormRecordId, value.FieldValueItemId, value.CreatedOn, value.CreatedByUserId);
                converted.FormRecordGroupId = value.FormRecordGroupId;
                if (converted is FieldEncryptedValue || FieldValueCodec.Format(converted, optionsById) != wire)
                {
                    return Failure(item, "DEPROTECT_SELF_CHECK_FAILED");
                }
            }
            else
            {
                converted = FieldValueCodec.Protect(value, field, optionsById, encryption);
                if (converted is not FieldEncryptedValue || FieldValueCodec.Format(converted, optionsById, encryption) != FieldValueCodec.Format(value, optionsById))
                {
                    return Failure(item, "REPROTECT_SELF_CHECK_FAILED");
                }
            }

            rows.Add(converted);
        }

        rows.AddRange(snapshot.Rows.OfType<FileAttachmentVersion>().Select(a => CandidateBuilder.CloneAttachment(a)));

        // 4. Prepare (with the verified current snapshot) → stage → read back → commit, as a reprotection operation.
        var idempotencyKey = ReprotectionIdempotencyKey(migration.FieldProtectionMigrationId, item.FormRecordId, head.CommittedContentRevision, item.AttemptCount);
        try
        {
            var accepted = await stager.StageAndCommitReprotectionAsync(migration.TenantId, head, snapshot.Items, new RecordCandidateStager.CandidateInput(header, rows), migration.AuthorityCommandId, idempotencyKey, cancellationToken).ConfigureAwait(false);
            await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
            var record = await scope.FindFormRecordAsync(migration.TenantId, item.FormRecordId, cancellationToken).ConfigureAwait(false);
            if (record is not null && accepted.CommittedOperationId is { } acceptedOperation && record.RecordAcceptedContent(accepted.CommittedContentRevision, acceptedOperation))
            {
                record.ModifiedOn = clock.GetUtcNow();
            }

            scope.AddAuditEvent(ServiceEvent(migration, decrypting ? AuditActions.FieldValuesDeprotected : AuditActions.FieldValuesReprotected, "Success", decrypting ? "encrypted values of the field re-written as typed plaintext in a new accepted content revision (Amendment A1)" : "plaintext values of the field re-sealed as ciphertext in a new accepted content revision", item.FormRecordId, new { contentRevision = accepted.CommittedContentRevision, operationId = accepted.CommittedOperationId, convertedValueCount = sourceRows.Count, direction = migration.MigrationDirection }));
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ItemOutcome(FieldProtectionMigrationItemStates.Completed, null, accepted.CommittedOperationId, accepted.CommittedContentRevision, ByUserCommit: false, Retry: false);
        }
        catch (AuthorityClientException ex) when (ex.ErrorCode is "CONTENT_REVISION_CONFLICT" or "STALE_AUTHORIZATION_REVISION" or "OPERATION_STATE_INVALID" or "OPERATION_EXPIRED")
        {
            // A user committed concurrently (their write re-protected the field) or the authorization revision moved: re-read next pass.
            return new ItemOutcome(FieldProtectionMigrationItemStates.Pending, ex.ErrorCode, null, null, false, Retry: true);
        }
        catch (AuthorityClientException ex) when (ex.ErrorCode is "IDEMPOTENCY_CONFLICT")
        {
            // An earlier, interrupted attempt left a prepared operation under this attempt's key (e.g. its Prepare was still
            // buffered in a paused authority and processed after the worker had given up). The rebuilt candidate differs
            // (fresh row identities, fresh ciphertext), so the authority rightly refuses to reuse the key. Recording this
            // attempt moves the item to the next attempt number and therefore a fresh operation; the abandoned operation
            // expires unused. Not a record failure: nothing about the record's content is wrong.
            return new ItemOutcome(FieldProtectionMigrationItemStates.Pending, ex.ErrorCode, null, null, false, Retry: true);
        }
        catch (AuthorityClientException ex) when (!ex.Retryable)
        {
            // Candidate refused (tampered current rows, integrity quarantine, forbidden): bounded attempts, then Failed; never re-signed silently.
            return Failure(item, Describe(ex));
        }
    }

    /// <summary>Error code plus the authority's reason (e.g. the violated reprotection rule), bounded to the column size; never plaintext.</summary>
    private static string Describe(AuthorityClientException ex)
    {
        var text = string.IsNullOrWhiteSpace(ex.Message) || ex.Message == ex.ErrorCode ? ex.ErrorCode : ex.ErrorCode + ": " + ex.Message;
        return text.Length <= 200 ? text : text[..200];
    }

    /// <summary>
    /// Idempotency key (and so the derived operation id) of one reprotection attempt for one record. Bound to the
    /// migration, the record, the accepted head it builds on and the item's persisted attempt number: a crash or replay
    /// within an attempt reuses the same operation (the authority returns it, or the head already moved and the item
    /// completes as a no-op), while every new attempt gets a fresh operation, so an operation abandoned by an interrupted
    /// attempt can never block the record. Commits stay CAS-protected on the accepted head revision either way.
    /// </summary>
    public static string ReprotectionIdempotencyKey(Guid migrationId, Guid formRecordId, long acceptedContentRevision, int attemptNumber) =>
        $"reprotect:{migrationId:N}:{formRecordId:N}:{acceptedContentRevision}:{attemptNumber}";

    private static ItemOutcome Failure(FieldProtectionMigrationItem item, string code) =>
        item.AttemptCount + 1 >= MaxItemAttempts
            ? new ItemOutcome(FieldProtectionMigrationItemStates.Failed, code, null, null, false, Retry: false)
            : new ItemOutcome(FieldProtectionMigrationItemStates.Pending, code, null, null, false, Retry: true);

    private async Task RecordItemAsync(Guid migrationId, Guid itemId, ItemOutcome outcome, CancellationToken cancellationToken)
    {
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var migration = await scope.FindProtectionMigrationAsync(migrationId, cancellationToken).ConfigureAwait(false);
        var item = await scope.FindProtectionMigrationItemAsync(itemId, cancellationToken).ConfigureAwait(false);
        if (migration is null || item is null || item.ItemState != FieldProtectionMigrationItemStates.Pending)
        {
            return;
        }

        var now = clock.GetUtcNow();
        item.AttemptCount++;
        item.LastAttemptOn = now;
        item.LastErrorCode = outcome.ErrorCode;
        item.ItemModifiedOn = now;
        if (outcome.State == FieldProtectionMigrationItemStates.Completed)
        {
            item.ItemState = FieldProtectionMigrationItemStates.Completed;
            item.CompletedOperationId = outcome.OperationId;
            item.CompletedContentRevision = outcome.ContentRevision;
            item.CompletedByUserCommit = outcome.ByUserCommit;
            migration.CompletedRecordCount++;
        }
        else if (outcome.State == FieldProtectionMigrationItemStates.Failed)
        {
            item.ItemState = FieldProtectionMigrationItemStates.Failed;
            migration.FailedRecordCount++;
            migration.LastErrorCode = outcome.ErrorCode;
            scope.AddAuditEvent(ServiceEvent(migration, AuditActions.FieldProtectionMigrationRecordFailed, "Failed", "a record could not be re-protected after bounded attempts; the field is not fully encrypted", item.FormRecordId, new { errorCode = outcome.ErrorCode, attempts = item.AttemptCount }));
        }

        migration.MigrationModifiedOn = now;
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// No pending item left: the post-check asks the authority for the accepted head of every tracked record AND of every
    /// business record of the form, and counts rows of the old form in exactly the operation the authority reports (never
    /// in a staged-but-unaccepted candidate, and never through the business projection of the accepted operation). Records
    /// found this way that the migration does not track are added and processed. Only zero (and no failed item) completes,
    /// so neither the business estimate nor a forged accepted-operation projection can make a migration claim completion.
    /// Residual: the record list itself comes from the business database; a record a hostile administrator removes from
    /// form_records is not enumerated here (the authority offers no workload enumeration of a form's records; G06).
    /// </summary>
    private async Task FinalizeAsync(FieldProtectionMigration leased, FieldDefinition field, CancellationToken cancellationToken)
    {
        HashSet<Guid> tracked;
        List<Guid> formRecords;
        await using (var readScope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            tracked = await readScope.GetProtectionMigrationItemRecordIdsAsync(leased.FieldProtectionMigrationId, cancellationToken).ConfigureAwait(false);
            formRecords = await readScope.GetFormRecordIdsOfFormAsync(field.FormDefinitionId, cancellationToken).ConfigureAwait(false);
        }

        var decrypting = leased.MigrationDirection == FieldProtectionMigrationDirections.Decrypt;
        var stillPlaintext = 0; // tracked records whose authority-accepted head still holds rows of the old form
        var authorityUntracked = new List<LegacyPlaintextRecord>(); // untracked records whose authority-accepted head holds rows of the old form
        foreach (var recordId in tracked.Union(formRecords))
        {
            AcceptedContentRevision head;
            try
            {
                head = await authority.GetCommittedContentRevisionForReprotectionAsync(leased.TenantId, recordId, leased.AuthorityCommandId, cancellationToken).ConfigureAwait(false);
            }
            catch (AuthorityClientException ex) when (!ex.Retryable && ex.HttpStatusCode == 404 && !tracked.Contains(recordId))
            {
                continue; // no accepted content of this form in the authority (e.g. a record whose creation never committed)
            }

            if (head.CommittedOperationId is { } accepted)
            {
                await using var countScope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
                var oldForm = decrypting
                    ? await countScope.CountEncryptedRowsInOperationAsync(field.FieldDefinitionId, recordId, accepted, cancellationToken).ConfigureAwait(false)
                    : await countScope.CountPlaintextRowsInOperationAsync(field.FieldDefinitionId, recordId, accepted, cancellationToken).ConfigureAwait(false);
                if (oldForm > 0 && tracked.Contains(recordId))
                {
                    stillPlaintext++;
                }
                else if (oldForm > 0)
                {
                    authorityUntracked.Add(new LegacyPlaintextRecord(recordId, oldForm));
                }
            }
        }

        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var migration = await scope.FindProtectionMigrationAsync(leased.FieldProtectionMigrationId, cancellationToken).ConfigureAwait(false);
        if (migration is null)
        {
            return;
        }

        var now = clock.GetUtcNow();
        var remaining = decrypting
            ? await scope.GetLegacyEncryptedRecordsAsync(field.FieldDefinitionId, cancellationToken).ConfigureAwait(false)
            : await scope.GetLegacyPlaintextRecordsAsync(field.FieldDefinitionId, cancellationToken).ConfigureAwait(false);
        var untracked = remaining.Where(r => !tracked.Contains(r.FormRecordId)).Concat(authorityUntracked).GroupBy(r => r.FormRecordId).Select(g => g.MaxBy(r => r.PlaintextValueCount)!).ToList();
        if (untracked.Count > 0)
        {
            // Records that became plaintext holders after enumeration (e.g. a commit that raced the strengthening): track and process them too.
            foreach (var r in untracked)
            {
                scope.AddProtectionMigrationItem(new FieldProtectionMigrationItem { FieldProtectionMigrationItemId = Guid.CreateVersion7(), FieldProtectionMigrationId = migration.FieldProtectionMigrationId, TenantId = migration.TenantId, FormRecordId = r.FormRecordId, ItemModifiedOn = now });
            }

            migration.TotalRecordCount += untracked.Count;
            migration.TotalValueCount += untracked.Sum(r => r.PlaintextValueCount);
            migration.MigrationState = FieldProtectionMigrationStates.Running;
            migration.NextAttemptOn = now;
        }
        else if (migration.FailedRecordCount > 0)
        {
            migration.MigrationState = FieldProtectionMigrationStates.Failed;
            migration.NextAttemptOn = now.AddHours(1);
            scope.AddAuditEvent(ServiceEvent(migration, AuditActions.FieldProtectionMigrationFailed, "Failed", "migration finished with failed records; the field is not fully encrypted", null, new { migration.TotalRecordCount, migration.CompletedRecordCount, migration.FailedRecordCount, migration.LastErrorCode }));
        }
        else if (stillPlaintext > 0)
        {
            // Items say completed but an authority-accepted head still holds a plaintext copy: never claim encryption; surface it.
            migration.MigrationState = FieldProtectionMigrationStates.Failed;
            migration.LastErrorCode = "PLAINTEXT_STILL_PRESENT";
            migration.NextAttemptOn = now.AddHours(1);
            scope.AddAuditEvent(ServiceEvent(migration, AuditActions.FieldProtectionMigrationFailed, "Failed", "post-check against the authority heads found rows of the old form after all items completed", null, new { remaining = stillPlaintext, direction = migration.MigrationDirection }));
        }
        else
        {
            migration.MigrationState = FieldProtectionMigrationStates.Completed;
            migration.MigrationCompletedOn = now;
            migration.MigrationVerifiedOn = now;
            migration.LastErrorCode = null;
            migration.NextAttemptOn = DateTimeOffset.MaxValue;
            scope.AddAuditEvent(ServiceEvent(migration, AuditActions.FieldProtectionMigrationCompleted, "Success", decrypting ? "every listed record decrypted through the authority; post-check against the authority-accepted heads of the records the business database lists found no encrypted row of the field (Amendment A1; not an independent completeness proof, G19)" : "every listed record re-protected through the authority; post-check against the authority-accepted heads of the records the business database lists found no plaintext copy of the field (not an independent completeness proof, G19)", null, new { migration.TotalRecordCount, migration.TotalValueCount, migration.CompletedRecordCount, verifiedRecords = tracked.Count, direction = migration.MigrationDirection }));
        }

        migration.LeaseOwnerName = null;
        migration.LeaseExpiresOn = null;
        migration.MigrationModifiedOn = now;
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns a leased migration to the queue unchanged (state Running stays Running, due now) with a bounded, non-cancellable write.</summary>
    private async Task ReleaseLeaseAsync(Guid migrationId)
    {
        using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await using var scope = await store.BeginScopeAsync(bounded.Token).ConfigureAwait(false);
            var migration = await scope.FindProtectionMigrationAsync(migrationId, bounded.Token).ConfigureAwait(false);
            if (migration is null || migration.MigrationState is FieldProtectionMigrationStates.Completed or FieldProtectionMigrationStates.Cancelled)
            {
                return;
            }

            var now = clock.GetUtcNow();
            migration.LeaseOwnerName = null;
            migration.LeaseExpiresOn = null;
            migration.NextAttemptOn = now;
            migration.MigrationModifiedOn = now;
            await scope.CommitAsync(bounded.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException || ex.GetType().Name.Contains("Npgsql", StringComparison.Ordinal) || ex.GetType().Name.Contains("DbUpdate", StringComparison.Ordinal))
        {
            // Best effort: the lease then expires on its own; nothing is lost.
        }
    }

    private async Task SettleAsync(Guid migrationId, string state, string? errorCode, TimeSpan retryAfter, CancellationToken cancellationToken)
    {
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var migration = await scope.FindProtectionMigrationAsync(migrationId, cancellationToken).ConfigureAwait(false);
        if (migration is null || migration.MigrationState is FieldProtectionMigrationStates.Completed or FieldProtectionMigrationStates.Cancelled)
        {
            return;
        }

        var now = clock.GetUtcNow();
        migration.MigrationState = state;
        migration.LastErrorCode = errorCode ?? (state == FieldProtectionMigrationStates.Running ? null : migration.LastErrorCode);
        migration.AttemptCount++;
        migration.LeaseOwnerName = null;
        migration.LeaseExpiresOn = null;
        migration.NextAttemptOn = now.Add(retryAfter);
        migration.MigrationModifiedOn = now;
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private AuditEvent ServiceEvent(FieldProtectionMigration migration, string action, string outcome, string description, Guid? formRecordId, object metadata) => new()
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
        FormRecordId = formRecordId,
        FieldDefinitionId = migration.FieldDefinitionId,
        TargetEntityType = "field_protection_migration",
        TargetEntityId = migration.FieldProtectionMigrationId,
        CorrelationIdentifier = Guid.CreateVersion7(),
        EventDescription = description,
        EventMetadataJson = System.Text.Json.JsonSerializer.Serialize(metadata),
    };
}
