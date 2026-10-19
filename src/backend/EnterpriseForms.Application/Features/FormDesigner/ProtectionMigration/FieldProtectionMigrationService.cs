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

        if (field.FieldProtectionMode != FieldProtectionModes.ApplicationEncrypted)
        {
            await SettleAsync(migration.FieldProtectionMigrationId, FieldProtectionMigrationStates.Failed, "FIELD_NOT_ENCRYPTED", TimeSpan.FromHours(1), cancellationToken).ConfigureAwait(false);
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
                var outcome = await MigrateRecordAsync(migration, field, optionsById, item, cancellationToken).ConfigureAwait(false);
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

    private async Task<ItemOutcome> MigrateRecordAsync(FieldProtectionMigration migration, FieldDefinition field, IReadOnlyDictionary<Guid, FieldOption> optionsById, FieldProtectionMigrationItem item, CancellationToken cancellationToken)
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

        var plaintextRows = snapshot.Rows.OfType<FieldValueRowBase>().Where(v => v.FieldDefinitionId == field.FieldDefinitionId && v is not FieldEncryptedValue).ToList();
        if (plaintextRows.Count == 0)
        {
            return new ItemOutcome(FieldProtectionMigrationItemStates.Completed, null, head.CommittedOperationId, head.CommittedContentRevision, ByUserCommit: true, Retry: false);
        }

        // 3. Candidate: same header, same attachments, same other values; the field's plaintext rows re-sealed under the same item ids and self-checked.
        var header = CandidateBuilder.CloneHeader(snapshot.Header);
        var rows = new List<ISignedContentRow>();
        foreach (var value in snapshot.Rows.OfType<FieldValueRowBase>())
        {
            if (value.FieldDefinitionId != field.FieldDefinitionId || value is FieldEncryptedValue)
            {
                rows.Add(Documents.UploadSessions.UploadSessionHandlers.CloneValue(value));
                continue;
            }

            var sealedRow = FieldValueCodec.Protect(value, field, optionsById, encryption);
            if (sealedRow is not FieldEncryptedValue || FieldValueCodec.Format(sealedRow, optionsById, encryption) != FieldValueCodec.Format(value, optionsById))
            {
                return Failure(item, "REPROTECT_SELF_CHECK_FAILED");
            }

            rows.Add(sealedRow);
        }

        rows.AddRange(snapshot.Rows.OfType<FileAttachmentVersion>().Select(a => CandidateBuilder.CloneAttachment(a)));

        // 4. Prepare (with the verified current snapshot) → stage → read back → commit, as a reprotection operation.
        var idempotencyKey = $"reprotect:{migration.FieldProtectionMigrationId:N}:{item.FormRecordId:N}:{head.CommittedContentRevision}";
        try
        {
            var accepted = await stager.StageAndCommitReprotectionAsync(migration.TenantId, head, snapshot.Items, new RecordCandidateStager.CandidateInput(header, rows), migration.AuthorityCommandId, idempotencyKey, cancellationToken).ConfigureAwait(false);
            await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
            var record = await scope.FindFormRecordAsync(migration.TenantId, item.FormRecordId, cancellationToken).ConfigureAwait(false);
            if (record is not null && record.RecordRevision < accepted.CommittedContentRevision)
            {
                record.RecordRevision = accepted.CommittedContentRevision;
                record.ModifiedOn = clock.GetUtcNow();
            }

            scope.AddAuditEvent(ServiceEvent(migration, AuditActions.FieldValuesReprotected, "Success", "plaintext values of the field re-sealed as ciphertext in a new accepted content revision", item.FormRecordId, new { contentRevision = accepted.CommittedContentRevision, operationId = accepted.CommittedOperationId, reprotectedValueCount = plaintextRows.Count }));
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new ItemOutcome(FieldProtectionMigrationItemStates.Completed, null, accepted.CommittedOperationId, accepted.CommittedContentRevision, ByUserCommit: false, Retry: false);
        }
        catch (AuthorityClientException ex) when (ex.ErrorCode is "CONTENT_REVISION_CONFLICT" or "STALE_AUTHORIZATION_REVISION" or "OPERATION_STATE_INVALID" or "OPERATION_EXPIRED")
        {
            // A user committed concurrently (their write re-protected the field) or the authorization revision moved: re-read next pass.
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
    /// No pending item left: the post-check asks the authority for every tracked record's accepted head and counts plaintext
    /// rows of the field in exactly that operation (never in a staged-but-unaccepted candidate), then re-enumerates the
    /// projection for records that became plaintext holders after the enumeration. Only zero (and no failed item) completes.
    /// </summary>
    private async Task FinalizeAsync(FieldProtectionMigration leased, FieldDefinition field, CancellationToken cancellationToken)
    {
        HashSet<Guid> tracked;
        await using (var readScope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false))
        {
            tracked = await readScope.GetProtectionMigrationItemRecordIdsAsync(leased.FieldProtectionMigrationId, cancellationToken).ConfigureAwait(false);
        }

        var stillPlaintext = 0;
        foreach (var recordId in tracked)
        {
            var head = await authority.GetCommittedContentRevisionForReprotectionAsync(leased.TenantId, recordId, leased.AuthorityCommandId, cancellationToken).ConfigureAwait(false);
            if (head.CommittedOperationId is { } accepted)
            {
                await using var countScope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
                stillPlaintext += await countScope.CountPlaintextRowsInOperationAsync(field.FieldDefinitionId, recordId, accepted, cancellationToken).ConfigureAwait(false) > 0 ? 1 : 0;
            }
        }

        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var migration = await scope.FindProtectionMigrationAsync(leased.FieldProtectionMigrationId, cancellationToken).ConfigureAwait(false);
        if (migration is null)
        {
            return;
        }

        var now = clock.GetUtcNow();
        var remaining = await scope.GetLegacyPlaintextRecordsAsync(field.FieldDefinitionId, cancellationToken).ConfigureAwait(false);
        var untracked = remaining.Where(r => !tracked.Contains(r.FormRecordId)).ToList();
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
            scope.AddAuditEvent(ServiceEvent(migration, AuditActions.FieldProtectionMigrationFailed, "Failed", "post-check against the authority heads found plaintext copies after all items completed", null, new { remaining = stillPlaintext }));
        }
        else
        {
            migration.MigrationState = FieldProtectionMigrationStates.Completed;
            migration.MigrationCompletedOn = now;
            migration.MigrationVerifiedOn = now;
            migration.LastErrorCode = null;
            migration.NextAttemptOn = DateTimeOffset.MaxValue;
            scope.AddAuditEvent(ServiceEvent(migration, AuditActions.FieldProtectionMigrationCompleted, "Success", "every record re-protected through the authority; post-check against the authority-accepted heads found no plaintext copy of the field", null, new { migration.TotalRecordCount, migration.TotalValueCount, migration.CompletedRecordCount, verifiedRecords = tracked.Count }));
        }

        migration.LeaseOwnerName = null;
        migration.LeaseExpiresOn = null;
        migration.MigrationModifiedOn = now;
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task SettleAsync(Guid migrationId, string state, string? errorCode, TimeSpan retryAfter, CancellationToken cancellationToken)
    {
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var migration = await scope.FindProtectionMigrationAsync(migrationId, cancellationToken).ConfigureAwait(false);
        if (migration is null || migration.MigrationState is FieldProtectionMigrationStates.Completed)
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
