using System.Security.Cryptography;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Journal;
using AuthorizationAuthority.Application.Features.SecurityCommands;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Content;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Keys;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using EnterpriseForms.Integrity;
using EnterpriseForms.Integrity.Canonical;
using EnterpriseForms.Integrity.Merkle;

namespace AuthorizationAuthority.Application.Features.Content;

/// <summary>
/// Immutable content-change protocol (CROSS_SYSTEM §3, MERKLE §4): prepare validates and signs the exact candidate,
/// commit proves the staged bytes equal the approved candidate and CAS-advances the trusted head, verify compares an
/// observed projection with the complete accepted leaf index. No generic signing path exists (A9).
/// </summary>
public sealed class ContentService(IAuthorityStore store, IIntegrityMacProvider macProvider, ISpiceDbProjection spiceDb, IFaultInjector faultInjector, TimeProvider clock)
{
    private const int MaxCandidateItems = 500;
    private static readonly TimeSpan OperationLifetime = TimeSpan.FromMinutes(10);

    private static readonly Dictionary<ManifestItemKind, string[]> AllowedEntityKinds = new()
    {
        [ManifestItemKind.RecordHeader] = ["form_records"],
        [ManifestItemKind.FieldValue] = ["field_string_values", "field_integer_values", "field_decimal_values", "field_date_values", "field_date_time_values", "field_boolean_values", "field_guid_values", "field_encrypted_values"],
        [ManifestItemKind.FieldSelection] = ["field_option_values"],
        [ManifestItemKind.RepeatedGroup] = ["form_record_groups"],
        [ManifestItemKind.Attachment] = ["file_attachment_versions"],
        [ManifestItemKind.AttachmentLifecycle] = ["file_attachment_lifecycle_events"],
    };

    public async Task<PreparedContentOperation> PrepareAsync(VerifiedActor? actor, PrepareContentChangeRequest request, string idempotencyKey, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length is < 8 or > 128)
        {
            throw AuthorityException.Invalid("Idempotency-Key must be 8..128 characters");
        }

        // Idempotent identity excludes the authorization-revision CAS precondition (see SecurityCommandService); content revision/root stay part of the identity.
        var requestDigest = CanonicalJson.Digest(CanonicalJson.SerializeBytes(request with { ExpectedAuthorizationRevision = 0 }));
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var state = await scope.LockOrganizationAsync(request.OrganizationId, cancellationToken).ConfigureAwait(false)
                    ?? throw AuthorityException.NotRegistered("organization is not registered");
        var (bound, reprotection) = await ResolveActorAsync(scope, state, actor, request.ReprotectionCommandId, cancellationToken).ConfigureAwait(false);
        OrganizationGate.RequireOpen(await OrganizationGate.EvaluateAsync(scope, state, cancellationToken).ConfigureAwait(false));

        var existing = await scope.FindOperationByIdempotencyKeyAsync(state.OrganizationId, idempotencyKey, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (!existing.OperationRequestDigest.AsSpan().SequenceEqual(requestDigest) || existing.ActorIdentityBindingId != bound.Binding.IdentityBindingId)
            {
                throw new AuthorityException(AuthorityErrorCodes.IdempotencyConflict, 409, false, "idempotency key reused with a different request or actor");
            }

            var leaves = await scope.GetCandidateLeavesAsync(existing.ContentChangeOperationId, cancellationToken).ConfigureAwait(false);
            return ToPrepared(existing, leaves, correlationId);
        }

        if (request.ExpectedAuthorizationRevision != state.AuthorizationRevision)
        {
            throw new AuthorityException(AuthorityErrorCodes.StaleAuthorizationRevision, 409, false, "expected_authorization_revision is stale");
        }

        var resource = await scope.FindResourceAsync(state.OrganizationId, ResourceTypeNames.FormRecord, request.FormRecordId, cancellationToken).ConfigureAwait(false);
        if (resource is null || resource.ResourceLifecycleState != ResourceLifecycleStates.Active)
        {
            throw AuthorityException.NotRegistered("record is not registered");
        }

        if (reprotection is not null && resource.ParentResourceEntityId != reprotection.FormDefinitionId)
        {
            throw AuthorityException.Forbidden("the record does not belong to the form of the protection requirement");
        }

        var head = await scope.FindContentHeadAsync(state.OrganizationId, request.FormRecordId, lockForUpdate: true, cancellationToken).ConfigureAwait(false)
                   ?? throw AuthorityException.NotRegistered("record has no content head");
        if (head.ContentHeadQuarantined)
        {
            throw AuthorityException.Quarantined("record content is quarantined");
        }

        if (request.ExpectedContentRevision != head.CommittedContentRevision || !SafeBase64(request.ExpectedManifestRootBase64).AsSpan().SequenceEqual(head.CommittedManifestRoot))
        {
            throw new AuthorityException(AuthorityErrorCodes.ContentRevisionConflict, 409, false, "expected content revision/root does not match the accepted head");
        }

        var key = await scope.FindKeyRegistrationAsync(state.OrganizationId, IntegrityKeyPurposes.RowIntegrityV1, cancellationToken).ConfigureAwait(false);
        if (key is null || key.KeyUsagePaused)
        {
            throw new AuthorityException(AuthorityErrorCodes.KeyUnavailable, 503, true, "organization integrity key unavailable");
        }

        var currentLeaves = head.CurrentRecordManifestCommitId is { } commitId
            ? await scope.GetManifestLeavesAsync(commitId, cancellationToken).ConfigureAwait(false)
            : [];
        var candidateRevision = head.CommittedContentRevision + 1;
        var operationId = request.OperationId;
        if (operationId == Guid.Empty || await scope.FindOperationAsync(operationId, cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new AuthorityException(AuthorityErrorCodes.IdempotencyConflict, 409, false, "operation_id is missing or already used");
        }

        var parsed = ParseCandidate(request, state.OrganizationId, key.CurrentSigningKeyVersion);
        Dictionary<string, string> mutations;
        if (reprotection is null)
        {
            mutations = ClassifyMutations(parsed, currentLeaves);
            AuthorizeMutations(await scope.GetActiveEdgesAsync(state.OrganizationId, cancellationToken).ConfigureAwait(false), bound, request.FormRecordId, mutations);
        }
        else
        {
            mutations = await AuthorizeReprotectionAsync(request, parsed, currentLeaves, head, key, reprotection, cancellationToken).ConfigureAwait(false);
        }

        var keyReference = new IntegrityKeyReference(state.OrganizationId, key.KeyPurposeName, key.ProviderKeyName, key.CurrentSigningKeyVersion);
        var signed = new List<(ParsedCandidateItem Item, byte[] Signature, string Mutation)>(parsed.Count);
        foreach (var item in parsed)
        {
            IntegrityMacResult mac;
            try
            {
                mac = await macProvider.SignAsync(keyReference, item.CanonicalBytes, cancellationToken).ConfigureAwait(false);
            }
            catch (IntegrityProviderUnavailableException ex)
            {
                throw new AuthorityException(AuthorityErrorCodes.SigningUnavailable, 503, true, ex.Message);
            }

            signed.Add((item, mac.Mac, mutations[Convert.ToHexStringLower(item.Key)]));
        }

        var manifest = MerkleManifestV1.Build(signed.Select(s => new MerkleLeafInput(ToMerkleKind(s.Item.Kind), s.Item.ItemId, s.Signature, s.Item.Digest, (ulong)candidateRevision, operationId)));
        var operation = new ContentChangeOperation
        {
            ContentChangeOperationId = operationId,
            OrganizationId = state.OrganizationId,
            FormRecordId = request.FormRecordId,
            ActorIdentityBindingId = bound.Binding.IdentityBindingId,
            OperationIdempotencyKey = idempotencyKey,
            OperationRequestDigest = requestDigest,
            ExpectedAuthorizationRevision = request.ExpectedAuthorizationRevision,
            ExpectedContentRevision = request.ExpectedContentRevision,
            CandidateContentRevision = candidateRevision,
            CandidateKeyVersion = key.CurrentSigningKeyVersion,
            CandidateManifestRoot = manifest.MerkleRoot,
            CandidateLeafIndexDigest = manifest.LeafIndexDigest,
            CandidateLeafCount = (long)manifest.LeafCount,
            OperationStateName = ContentOperationStates.Prepared,
            CorrelationIdentifier = correlationId,
            OperationCreatedOn = clock.GetUtcNow(),
            OperationExpiresOn = clock.GetUtcNow().Add(OperationLifetime),
            ReprotectionCommandId = reprotection?.CommandId,
        };
        scope.AddOperation(operation);

        var candidateLeaves = new List<ContentCandidateLeaf>();
        for (var i = 0; i < manifest.SortedLeaves.Count; i++)
        {
            var leaf = manifest.SortedLeaves[i];
            var source = signed.Single(s => s.Item.ItemId == leaf.ItemId && ToMerkleKind(s.Item.Kind) == leaf.Kind);
            candidateLeaves.Add(new ContentCandidateLeaf
            {
                ContentCandidateLeafId = Guid.CreateVersion7(),
                ContentChangeOperationId = operationId,
                ManifestItemKey = leaf.ItemKey,
                ManifestItemKindCode = (byte)leaf.Kind,
                ManifestItemId = leaf.ItemId,
                MutationKindName = source.Mutation,
                CanonicalPayloadDigest = source.Item.Digest,
                RowSignature = source.Signature,
                LeafDigest = manifest.LeafHashes[i],
            });
        }

        scope.AddCandidateLeaves(candidateLeaves);
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToPrepared(operation, candidateLeaves, correlationId);
    }

    public async Task<ContentRevision> CommitAsync(VerifiedActor? actor, Guid operationId, CommitContentChangeRequest request, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var state = await scope.LockOrganizationAsync(request.OrganizationId, cancellationToken).ConfigureAwait(false)
                    ?? throw AuthorityException.NotRegistered("organization is not registered");
        OrganizationGate.RequireOpen(await OrganizationGate.EvaluateAsync(scope, state, cancellationToken).ConfigureAwait(false));

        var operation = await scope.FindOperationAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (operation is null || operation.OrganizationId != state.OrganizationId || operation.FormRecordId != request.FormRecordId)
        {
            throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false);
        }

        // A reprotection operation is committed by the workload under its command; a user operation by the same user.
        var (bound, reprotection) = await ResolveActorAsync(scope, state, actor, operation.ReprotectionCommandId, cancellationToken).ConfigureAwait(false);
        if (operation.ActorIdentityBindingId != bound.Binding.IdentityBindingId)
        {
            throw AuthorityException.Forbidden("operation belongs to another actor");
        }

        var head = await scope.FindContentHeadAsync(state.OrganizationId, request.FormRecordId, lockForUpdate: true, cancellationToken).ConfigureAwait(false)
                   ?? throw AuthorityException.NotRegistered("record has no content head");

        if (operation.OperationStateName == ContentOperationStates.Committed)
        {
            return await ToRevisionAsync(scope, state, head, correlationId, cancellationToken).ConfigureAwait(false); // idempotent replay
        }

        if (operation.OperationStateName != ContentOperationStates.Prepared)
        {
            throw new AuthorityException(AuthorityErrorCodes.OperationStateInvalid, 409, false, $"operation is {operation.OperationStateName}");
        }

        if (operation.OperationExpiresOn < clock.GetUtcNow())
        {
            operation.OperationStateName = ContentOperationStates.Aborted;
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            throw new AuthorityException(AuthorityErrorCodes.OperationExpired, 409, false, "prepared operation expired");
        }

        if (head.ContentHeadQuarantined)
        {
            throw AuthorityException.Quarantined("record content is quarantined");
        }

        if (head.CommittedContentRevision != operation.ExpectedContentRevision)
        {
            operation.OperationStateName = ContentOperationStates.Aborted;
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            throw new AuthorityException(AuthorityErrorCodes.ContentRevisionConflict, 409, false, "another candidate was committed first");
        }

        var candidateLeaves = await scope.GetCandidateLeavesAsync(operationId, cancellationToken).ConfigureAwait(false);
        var key = await scope.FindKeyRegistrationAsync(state.OrganizationId, IntegrityKeyPurposes.RowIntegrityV1, cancellationToken).ConfigureAwait(false)
                  ?? throw new AuthorityException(AuthorityErrorCodes.KeyUnavailable, 503, true, "organization integrity key unavailable");
        var keyReference = new IntegrityKeyReference(state.OrganizationId, key.KeyPurposeName, key.ProviderKeyName, operation.CandidateKeyVersion);

        // Re-check current permission: a revocation between prepare and commit blocks the commit (CONS-14).
        var currentLeaves = head.CurrentRecordManifestCommitId is { } commitId ? await scope.GetManifestLeavesAsync(commitId, cancellationToken).ConfigureAwait(false) : [];
        var mutations = candidateLeaves.ToDictionary(l => Convert.ToHexStringLower(l.ManifestItemKey), l => l.MutationKindName, StringComparer.Ordinal);
        foreach (var removed in currentLeaves.Where(c => !candidateLeaves.Any(l => l.ManifestItemKey.AsSpan().SequenceEqual(c.ManifestItemKey))))
        {
            mutations[Convert.ToHexStringLower(removed.ManifestItemKey)] = ContentMutationKinds.Remove;
        }

        if (reprotection is null)
        {
            try
            {
                AuthorizeMutations(await scope.GetActiveEdgesAsync(state.OrganizationId, cancellationToken).ConfigureAwait(false), bound, request.FormRecordId, mutations);
            }
            catch (AuthorityException)
            {
                operation.OperationStateName = ContentOperationStates.Aborted;
                operation.OperationQuarantineReasonCode = "PERMISSION_REVOKED_BEFORE_COMMIT";
                await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }
        }
        else if (mutations.Values.Any(m => m is not (ContentMutationKinds.Unchanged or ContentMutationKinds.Reprotect)))
        {
            throw AuthorityException.Forbidden("a reprotection operation may only carry reprotect mutations");
        }

        // Prove the staged bytes are exactly the approved candidate: same key set, same digests, same signatures, signatures verify, same root.
        var verified = await VerifyStagedAgainstLeavesAsync(request.StagedItems, candidateLeaves.Select(l => (l.ManifestItemKey, l.ManifestItemKindCode, l.ManifestItemId, l.CanonicalPayloadDigest, l.RowSignature)).ToList(), state.OrganizationId, keyReference, operation.CandidateContentRevision, operationId, operation.CandidateManifestRoot, cancellationToken).ConfigureAwait(false);
        if (verified is not null)
        {
            throw new AuthorityException(AuthorityErrorCodes.CandidateMismatch, 409, false, verified);
        }

        faultInjector.CrashIfArmed(FaultNames.CrashAfterContentHeadCommit + ":before");

        var now = clock.GetUtcNow();
        var commit = new RecordManifestCommit
        {
            RecordManifestCommitId = Guid.CreateVersion7(),
            OrganizationId = state.OrganizationId,
            FormRecordId = request.FormRecordId,
            CommittedOperationId = operationId,
            CommittedContentRevision = operation.CandidateContentRevision,
            ManifestSchemaVersion = 1,
            ManifestAlgorithmVersion = 1,
            RowSignatureKeyVersion = operation.CandidateKeyVersion,
            ManifestLeafCount = operation.CandidateLeafCount,
            ManifestRootDigest = operation.CandidateManifestRoot,
            ManifestLeafIndexDigest = operation.CandidateLeafIndexDigest,
            ManifestCommittedOn = now,
        };
        scope.AddManifestCommit(commit);
        scope.AddManifestLeaves(candidateLeaves.OrderBy(l => l.ManifestItemKey, UnsignedByteComparer.Instance).Select((l, position) => new RecordManifestLeaf
        {
            RecordManifestLeafId = Guid.CreateVersion7(),
            RecordManifestCommitId = commit.RecordManifestCommitId,
            ManifestLeafPosition = position,
            ManifestItemKey = l.ManifestItemKey,
            ManifestItemKindCode = l.ManifestItemKindCode,
            ManifestItemId = l.ManifestItemId,
            CanonicalPayloadDigest = l.CanonicalPayloadDigest,
            RowSignature = l.RowSignature,
            ManifestLeafDigest = l.LeafDigest,
        }));

        head.CommittedContentRevision = operation.CandidateContentRevision;
        head.CommittedOperationId = operationId;
        head.CurrentRecordManifestCommitId = commit.RecordManifestCommitId;
        head.CommittedManifestRoot = operation.CandidateManifestRoot;
        head.SignatureKeyVersion = operation.CandidateKeyVersion;
        head.ContentHeadModifiedOn = now;

        operation.OperationStateName = ContentOperationStates.Committed;
        operation.OperationCommittedOn = now;

        state.ContentIntegrityRevision++;
        var checkpointBody = new ContentCheckpointBody(request.FormRecordId, head.CommittedContentRevision, operationId, Convert.ToHexStringLower(head.CommittedManifestRoot), Convert.ToHexStringLower(commit.ManifestLeafIndexDigest), commit.ManifestLeafCount, operation.CandidateKeyVersion, bound.AuthorityUserId, state.AuthorizationRevision, correlationId);
        var journal = JournalChain.Append(state, JournalStreamNames.Content, state.ContentIntegrityRevision, "ContentHeadCommitted", checkpointBody, null, operationId, clock);
        scope.AddJournalEntry(journal);
        scope.AddContentCheckpoint(new ContentIntegrityCheckpoint
        {
            ContentIntegrityCheckpointId = Guid.CreateVersion7(),
            OrganizationId = state.OrganizationId,
            ContentIntegrityRevision = state.ContentIntegrityRevision,
            FormRecordId = request.FormRecordId,
            CommittedContentRevision = head.CommittedContentRevision,
            CommittedOperationId = operationId,
            ManifestRootDigest = head.CommittedManifestRoot,
            PreviousCheckpointDigest = journal.JournalPreviousDigest,
            CheckpointEventDigest = journal.JournalEventDigest,
            CheckpointCreatedOn = now,
        });
        state.StateModifiedOn = now;

        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        faultInjector.CrashIfArmed(FaultNames.CrashAfterContentHeadCommit);
        return await ToRevisionAsync(scope, state, head, correlationId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ContentRevision> GetHeadAsync(VerifiedActor? actor, Guid organizationId, Guid formRecordId, Guid? reprotectionCommandId, Guid correlationId, CancellationToken cancellationToken)
    {
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var state = await scope.ReadOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false)
                    ?? throw AuthorityException.NotRegistered("organization is not registered");
        var (bound, reprotection) = await ResolveActorAsync(scope, state, actor, reprotectionCommandId, cancellationToken).ConfigureAwait(false);
        OrganizationGate.RequireOpen(await OrganizationGate.EvaluateAsync(scope, state, cancellationToken).ConfigureAwait(false));
        var head = await scope.FindContentHeadAsync(organizationId, formRecordId, false, cancellationToken).ConfigureAwait(false)
                   ?? throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false);
        var resource = await scope.FindResourceAsync(organizationId, ResourceTypeNames.FormRecord, formRecordId, cancellationToken).ConfigureAwait(false);
        if (resource is null || resource.ResourceLifecycleState != ResourceLifecycleStates.Active)
        {
            throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false);
        }

        if (reprotection is not null)
        {
            // Head metadata (revision, root, key version) for a record of the command's form; no content, no permission beyond the command.
            if (resource.ParentResourceEntityId != reprotection.FormDefinitionId)
            {
                throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false);
            }

            return await ToRevisionAsync(scope, state, head, correlationId, cancellationToken).ConfigureAwait(false);
        }

        var discover = await spiceDb.CheckAsync(new SpiceDbCheck(ResourceTypeNames.FormRecord, formRecordId.ToString("D"), "discover", ResourceTypeNames.User, bound.AuthorityUserId.ToString("D")), state.ProjectionZedToken!, cancellationToken).ConfigureAwait(false);
        if (discover != SpiceDbCheckOutcome.Allowed)
        {
            throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false); // concealment-safe
        }

        return await ToRevisionAsync(scope, state, head, correlationId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<VerifyRecordProjectionResult> VerifyProjectionAsync(VerifiedActor actor, VerifyRecordProjectionRequest request, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var state = await scope.LockOrganizationAsync(request.OrganizationId, cancellationToken).ConfigureAwait(false)
                    ?? throw AuthorityException.NotRegistered("organization is not registered");
        var bound = await ActorResolver.BindAsync(scope, state.OrganizationId, actor, cancellationToken).ConfigureAwait(false);
        OrganizationGate.RequireOpen(await OrganizationGate.EvaluateAsync(scope, state, cancellationToken).ConfigureAwait(false));

        var resource = await scope.FindResourceAsync(state.OrganizationId, ResourceTypeNames.FormRecord, request.FormRecordId, cancellationToken).ConfigureAwait(false);
        var head = await scope.FindContentHeadAsync(state.OrganizationId, request.FormRecordId, lockForUpdate: true, cancellationToken).ConfigureAwait(false);
        if (resource is null || resource.ResourceLifecycleState != ResourceLifecycleStates.Active || head is null)
        {
            throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false);
        }

        var subject = bound.AuthorityUserId.ToString("D");
        var recordId = request.FormRecordId.ToString("D");
        var fullRead = await spiceDb.CheckAsync(new SpiceDbCheck(ResourceTypeNames.FormRecord, recordId, request.RequestedAction == RequestedAction.Edit ? "edit" : "read", ResourceTypeNames.User, subject), state.ProjectionZedToken!, cancellationToken).ConfigureAwait(false) == SpiceDbCheckOutcome.Allowed;
        if (!fullRead)
        {
            var discover = await spiceDb.CheckAsync(new SpiceDbCheck(ResourceTypeNames.FormRecord, recordId, "discover", ResourceTypeNames.User, subject), state.ProjectionZedToken!, cancellationToken).ConfigureAwait(false);
            if (discover != SpiceDbCheckOutcome.Allowed)
            {
                throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false);
            }
        }

        if (head.ContentHeadQuarantined)
        {
            throw AuthorityException.Quarantined("record content is quarantined");
        }

        if (head.CommittedContentRevision == 0 || head.CurrentRecordManifestCommitId is null)
        {
            return new VerifyRecordProjectionResult(false, "NO_COMMITTED_CONTENT", 0, null, state.AuthorizationRevision, [], fullRead, correlationId);
        }

        // Protected bytes are never released before the content checkpoint holds its archive receipt (SEC-P12).
        var checkpointJournal = await FindContentJournalForHeadAsync(scope, state.OrganizationId, head, cancellationToken).ConfigureAwait(false);
        if (checkpointJournal is null || checkpointJournal.ArchiveDeliveryState != ArchiveDeliveryStates.Anchored)
        {
            throw AuthorityException.NotReady("AwaitingArchive");
        }

        // Target manifest: the current head, or (historic document versions) the accepted manifest of an earlier operation of the
        // same record. Historic verification needs full read permission now; partial readers only ever see the current head.
        var manifestCommitId = head.CurrentRecordManifestCommitId.Value;
        var verifiedRevision = head.CommittedContentRevision;
        var verifiedOperationId = head.CommittedOperationId!.Value;
        var verifiedRoot = head.CommittedManifestRoot;
        var verifiedKeyVersion = head.SignatureKeyVersion;
        if (request.HistoricCommittedOperationId is { } historicOperationId && historicOperationId != verifiedOperationId)
        {
            if (!fullRead)
            {
                throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false);
            }

            var historic = await scope.FindManifestCommitByOperationAsync(state.OrganizationId, request.FormRecordId, historicOperationId, cancellationToken).ConfigureAwait(false);
            if (historic is null || historic.CommittedContentRevision >= head.CommittedContentRevision)
            {
                throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false);
            }

            manifestCommitId = historic.RecordManifestCommitId;
            verifiedRevision = historic.CommittedContentRevision;
            verifiedOperationId = historic.CommittedOperationId;
            verifiedRoot = historic.ManifestRootDigest;
            verifiedKeyVersion = historic.RowSignatureKeyVersion;
        }

        var expected = await scope.GetManifestLeavesAsync(manifestCommitId, cancellationToken).ConfigureAwait(false);
        var key = await scope.FindKeyRegistrationAsync(state.OrganizationId, IntegrityKeyPurposes.RowIntegrityV1, cancellationToken).ConfigureAwait(false)
                  ?? throw new AuthorityException(AuthorityErrorCodes.KeyUnavailable, 503, true, "organization integrity key unavailable");
        var keyReference = new IntegrityKeyReference(state.OrganizationId, key.KeyPurposeName, key.ProviderKeyName, verifiedKeyVersion);
        if (verifiedKeyVersion < key.MinimumVerificationKeyVersion)
        {
            throw new AuthorityException(AuthorityErrorCodes.KeyUnavailable, 503, false, "accepted key version is no longer verifiable");
        }

        var failure = await VerifyStagedAgainstLeavesAsync(request.ObservedItems, expected.Select(l => (l.ManifestItemKey, l.ManifestItemKindCode, l.ManifestItemId, l.CanonicalPayloadDigest, l.RowSignature)).ToList(), state.OrganizationId, keyReference, verifiedRevision, verifiedOperationId, verifiedRoot, cancellationToken).ConfigureAwait(false);
        if (failure is not null)
        {
            // Tamper evidence: quarantine the record head and journal a minimal incident; never auto-repair or re-sign.
            head.ContentHeadQuarantined = true;
            head.QuarantineReasonCode = failure;
            head.ContentHeadModifiedOn = clock.GetUtcNow();
            state.ContentIntegrityRevision++;
            var incident = new IntegrityIncidentBody(request.FormRecordId, verifiedRevision, verifiedOperationId, failure, bound.AuthorityUserId, correlationId);
            scope.AddJournalEntry(JournalChain.Append(state, JournalStreamNames.Content, state.ContentIntegrityRevision, "IntegrityVerificationFailed", incident, null, head.CommittedOperationId, clock));
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new VerifyRecordProjectionResult(false, failure, verifiedRevision, verifiedOperationId, state.AuthorizationRevision, [], fullRead, correlationId);
        }

        List<VerifiedProjectionItem> authorized;
        if (fullRead)
        {
            authorized = expected.Select(l => new VerifiedProjectionItem((ManifestItemKind)l.ManifestItemKindCode, l.ManifestItemId)).ToList();
        }
        else
        {
            var fieldLeaves = expected.Where(l => l.ManifestItemKindCode == (byte)ManifestItemKind.FieldValue).ToList();
            var checks = fieldLeaves.Select(l => new SpiceDbCheck(ResourceTypeNames.FieldItem, l.ManifestItemId.ToString("D"), request.RequestedAction == RequestedAction.Edit ? "edit" : "read", ResourceTypeNames.User, subject)).ToList();
            var outcomes = checks.Count == 0 ? [] : await spiceDb.CheckBulkAsync(checks, state.ProjectionZedToken!, cancellationToken).ConfigureAwait(false);
            authorized = fieldLeaves.Where((_, i) => outcomes[i] == SpiceDbCheckOutcome.Allowed).Select(l => new VerifiedProjectionItem(ManifestItemKind.FieldValue, l.ManifestItemId)).ToList();
        }

        var after = await scope.ReadOrganizationAsync(state.OrganizationId, cancellationToken).ConfigureAwait(false);
        if (after is null || after.AuthorizationRevision != state.AuthorizationRevision || after.ProjectionAppliedRevision != state.ProjectionAppliedRevision)
        {
            throw AuthorityException.NotReady("authorization state moved during verification");
        }

        return new VerifyRecordProjectionResult(true, null, verifiedRevision, verifiedOperationId, state.AuthorizationRevision, authorized, fullRead, correlationId);
    }

    /// <summary>Returns null when the observed set is exactly the expected set with verifying signatures and matching root; otherwise a failure code.</summary>
    private async Task<string?> VerifyStagedAgainstLeavesAsync(
        IReadOnlyList<StagedContentItem> observed,
        List<(byte[] Key, byte KindCode, Guid ItemId, byte[] Digest, byte[] Signature)> expected,
        Guid organizationId,
        IntegrityKeyReference keyReference,
        long revision,
        Guid operationId,
        byte[] expectedRoot,
        CancellationToken cancellationToken)
    {
        if (observed.Count != expected.Count)
        {
            return observed.Count < expected.Count ? "MISSING_ITEM" : "EXTRA_ITEM";
        }

        var expectedByKey = expected.ToDictionary(e => Convert.ToHexStringLower(e.Key), StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var leaves = new List<MerkleLeafInput>(observed.Count);
        foreach (var item in observed)
        {
            var kind = ToMerkleKind(item.ItemKind);
            var keyHex = Convert.ToHexStringLower(MerkleManifestV1.ItemKey(kind, item.ItemId));
            if (!expectedByKey.TryGetValue(keyHex, out var exp))
            {
                return "EXTRA_ITEM";
            }

            if (!seen.Add(keyHex))
            {
                return "DUPLICATE_ITEM";
            }

            byte[] bytes;
            byte[] signature;
            try
            {
                bytes = Convert.FromBase64String(item.CanonicalBytesBase64);
                signature = Convert.FromBase64String(item.RowSignatureBase64);
                var header = RowCanonicalHeader.Parse(bytes);
                if (header.OrganizationId != organizationId || header.KeyVersion != (uint)keyReference.KeyVersion)
                {
                    return "ITEM_BINDING_MISMATCH";
                }
            }
            catch (FormatException)
            {
                return "ITEM_ENCODING_INVALID";
            }
            catch (IntegrityFormatException)
            {
                return "ITEM_ENCODING_INVALID";
            }

            var digest = SHA256.HashData(bytes);
            if (!digest.AsSpan().SequenceEqual(exp.Digest))
            {
                return "ITEM_DIGEST_MISMATCH";
            }

            if (signature.Length != 32 || !signature.AsSpan().SequenceEqual(exp.Signature))
            {
                return "ITEM_SIGNATURE_MISMATCH";
            }

            bool valid;
            try
            {
                valid = await macProvider.VerifyAsync(keyReference, bytes, signature, cancellationToken).ConfigureAwait(false);
            }
            catch (IntegrityProviderUnavailableException ex)
            {
                throw new AuthorityException(AuthorityErrorCodes.SigningUnavailable, 503, true, ex.Message);
            }

            if (!valid)
            {
                return "ITEM_SIGNATURE_INVALID";
            }

            leaves.Add(new MerkleLeafInput(kind, item.ItemId, signature, digest, (ulong)revision, operationId));
        }

        var rebuilt = MerkleManifestV1.Build(leaves);
        return rebuilt.MerkleRoot.AsSpan().SequenceEqual(expectedRoot) ? null : "ROOT_MISMATCH";
    }

    private static List<ParsedCandidateItem> ParseCandidate(PrepareContentChangeRequest request, Guid organizationId, int signingKeyVersion)
    {
        if (request.CandidateItems.Count is 0 or > MaxCandidateItems)
        {
            throw new AuthorityException(AuthorityErrorCodes.LimitExceeded, 422, false, $"1..{MaxCandidateItems} candidate items");
        }

        var parsed = new List<ParsedCandidateItem>(request.CandidateItems.Count);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var headers = 0;
        foreach (var item in request.CandidateItems)
        {
            if (!Enum.IsDefined(item.ItemKind) || !AllowedEntityKinds.TryGetValue(item.ItemKind, out var allowedKinds))
            {
                throw AuthorityException.Invalid("unsupported item kind");
            }

            byte[] bytes;
            RowCanonicalHeader header;
            try
            {
                bytes = Convert.FromBase64String(item.CanonicalBytesBase64);
                header = RowCanonicalHeader.Parse(bytes);
            }
            catch (FormatException)
            {
                throw AuthorityException.Invalid("candidate bytes are not valid base64");
            }
            catch (IntegrityFormatException ex)
            {
                throw AuthorityException.Invalid("candidate bytes are not RowHmac v1: " + ex.Code);
            }

            if (header.OrganizationId != organizationId)
            {
                throw AuthorityException.Invalid("candidate is bound to a different organization");
            }

            if (header.KeyVersion != (uint)signingKeyVersion)
            {
                throw AuthorityException.Invalid("candidate key version must equal the current signing key version");
            }

            if (!allowedKinds.Contains(header.EntityKind, StringComparer.Ordinal))
            {
                throw AuthorityException.Invalid($"entity kind '{header.EntityKind}' is not permitted for {item.ItemKind}");
            }

            if (item.ItemKind == ManifestItemKind.RecordHeader)
            {
                headers++;
                if (item.ItemId != request.FormRecordId || header.PrimaryKeyId != request.FormRecordId)
                {
                    throw AuthorityException.Invalid("record header item must be bound to the record id");
                }
            }

            var key = MerkleManifestV1.ItemKey(ToMerkleKind(item.ItemKind), item.ItemId);
            if (!keys.Add(Convert.ToHexStringLower(key)))
            {
                throw AuthorityException.Invalid("duplicate candidate item");
            }

            parsed.Add(new ParsedCandidateItem(item.ItemKind, item.ItemId, key, bytes, SHA256.HashData(bytes)));
        }

        if (headers != 1)
        {
            throw AuthorityException.Invalid("exactly one record header item is required");
        }

        return parsed;
    }

    private static Dictionary<string, string> ClassifyMutations(IReadOnlyList<ParsedCandidateItem> candidate, IReadOnlyList<RecordManifestLeaf> current)
    {
        var currentByKey = current.ToDictionary(l => Convert.ToHexStringLower(l.ManifestItemKey), StringComparer.Ordinal);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in candidate)
        {
            var keyHex = Convert.ToHexStringLower(item.Key);
            result[keyHex] = currentByKey.TryGetValue(keyHex, out var existing)
                ? existing.CanonicalPayloadDigest.AsSpan().SequenceEqual(item.Digest) ? ContentMutationKinds.Unchanged : ContentMutationKinds.Update
                : ContentMutationKinds.Add;
        }

        foreach (var (keyHex, _) in currentByKey)
        {
            if (!result.ContainsKey(keyHex))
            {
                result[keyHex] = ContentMutationKinds.Remove;
            }
        }

        return result;
    }

    /// <summary>Owner: any mutation. Share contributor/manager: updates of explicitly shared field items only (no adds/removes, no header changes).</summary>
    private static void AuthorizeMutations(IReadOnlyList<Domain.Features.SecurityCommands.AuthorityRelationshipEdge> edges, BoundActor actor, Guid formRecordId, IReadOnlyDictionary<string, string> mutations)
    {
        var graph = new DesiredStateGraph(edges);
        if (graph.IsRecordOwner(actor.AuthorityUserId, formRecordId))
        {
            return;
        }

        var editable = graph.EditableFieldItemsViaShares(actor.AuthorityUserId, formRecordId);
        if (editable.Count == 0)
        {
            throw AuthorityException.Forbidden("actor may not change this record");
        }

        foreach (var (keyHex, mutation) in mutations)
        {
            if (mutation == ContentMutationKinds.Unchanged)
            {
                continue;
            }

            var key = Convert.FromHexString(keyHex);
            var kind = (MerkleItemKind)key[0];
            var itemId = new Guid(key.AsSpan(1, 16), bigEndian: true);
            if (mutation != ContentMutationKinds.Update || kind != MerkleItemKind.FieldValue || !editable.Contains(itemId))
            {
                throw AuthorityException.Forbidden("mutation exceeds the actor's edit scope");
            }
        }
    }

    private sealed record ReprotectionContext(Guid CommandId, Guid FormDefinitionId, Guid FieldDefinitionId);

    /// <summary>
    /// A user operation binds the delegated user. A reprotection operation has no user: it is authorized by an effective
    /// PublishFormSecurityRevision command of the organization, and is recorded under the identity of the designer who
    /// issued that command. Neither path accepts the other's shape (no user + no command ⇒ 401; user + command ⇒ 422).
    /// </summary>
    private static async Task<(BoundActor Bound, ReprotectionContext? Reprotection)> ResolveActorAsync(IAuthorityScope scope, OrganizationAuthorizationState state, VerifiedActor? actor, Guid? reprotectionCommandId, CancellationToken cancellationToken)
    {
        if (reprotectionCommandId is null)
        {
            if (actor is null)
            {
                throw new AuthorityException(AuthorityErrorCodes.Unauthorized, 401, false, "delegated user assertion missing");
            }

            return (await ActorResolver.BindAsync(scope, state.OrganizationId, actor, cancellationToken).ConfigureAwait(false), null);
        }

        if (actor is not null)
        {
            throw AuthorityException.Invalid("a reprotection operation is a workload operation and carries no delegated user");
        }

        var command = await scope.FindCommandAsync(reprotectionCommandId.Value, cancellationToken).ConfigureAwait(false);
        if (command is null || command.OrganizationId != state.OrganizationId || command.CommandKindName != SecurityCommandKinds.PublishFormSecurityRevision || command.CommandStateName != SecurityCommandStates.Effective)
        {
            throw AuthorityException.Forbidden("reprotection requires an effective PublishFormSecurityRevision command of this organization");
        }

        PublishFormSecurityRevisionPayload payload;
        using (var document = System.Text.Json.JsonDocument.Parse(command.CommandPayloadJson))
        {
            payload = CanonicalJson.Deserialize<PublishFormSecurityRevisionPayload>(document.RootElement);
        }

        var binding = await scope.FindIdentityBindingByIdAsync(command.ActorIdentityBindingId, cancellationToken).ConfigureAwait(false)
                      ?? throw AuthorityException.Forbidden("the command's actor binding no longer exists");
        var recorded = new VerifiedActor(binding.IdentityIssuerUri, binding.IdentitySubjectIdentifier, "reprotection-worker");
        return (new BoundActor(recorded, binding), new ReprotectionContext(command.SecurityCommandId, payload.FormDefinitionId, payload.FieldDefinitionId));
    }

    /// <summary>
    /// Reprotection: the current snapshot the worker built on must be exactly the accepted head (complete leaf set, every
    /// signature, root — the same proof as VerifyRecordProjection); then ReprotectionRules decides, from the bytes, that
    /// the candidate replaces plaintext rows of the command's field with encrypted rows of the same items and nothing else.
    /// </summary>
    private async Task<Dictionary<string, string>> AuthorizeReprotectionAsync(PrepareContentChangeRequest request, IReadOnlyList<ParsedCandidateItem> parsed, IReadOnlyList<RecordManifestLeaf> currentLeaves, RecordContentHead head, IntegrityKeyRegistration key, ReprotectionContext reprotection, CancellationToken cancellationToken)
    {
        if (request.CurrentItems is null || request.CurrentItems.Count == 0 || head.CommittedOperationId is null)
        {
            throw AuthorityException.Invalid("REPROTECT_CURRENT_REQUIRED: current_items (the complete accepted snapshot) are required");
        }

        if (head.SignatureKeyVersion < key.MinimumVerificationKeyVersion)
        {
            throw new AuthorityException(AuthorityErrorCodes.KeyUnavailable, 503, false, "accepted key version is no longer verifiable");
        }

        var headKey = new IntegrityKeyReference(head.OrganizationId, key.KeyPurposeName, key.ProviderKeyName, head.SignatureKeyVersion);
        var mismatch = await VerifyStagedAgainstLeavesAsync(request.CurrentItems, currentLeaves.Select(l => (l.ManifestItemKey, l.ManifestItemKindCode, l.ManifestItemId, l.CanonicalPayloadDigest, l.RowSignature)).ToList(), head.OrganizationId, headKey, head.CommittedContentRevision, head.CommittedOperationId.Value, head.CommittedManifestRoot, cancellationToken).ConfigureAwait(false);
        if (mismatch is not null)
        {
            throw new AuthorityException(AuthorityErrorCodes.CandidateMismatch, 409, false, "REPROTECT_CURRENT_MISMATCH: " + mismatch);
        }

        var current = new List<ReprotectionRules.SnapshotItem>(request.CurrentItems.Count);
        foreach (var item in request.CurrentItems)
        {
            var bytes = Convert.FromBase64String(item.CanonicalBytesBase64);
            current.Add(new ReprotectionRules.SnapshotItem(item.ItemKind, item.ItemId, MerkleManifestV1.ItemKey(ToMerkleKind(item.ItemKind), item.ItemId), RowCanonicalDecoder.Decode(bytes), SHA256.HashData(bytes)));
        }

        var candidate = parsed.Select(p => new ReprotectionRules.SnapshotItem(p.Kind, p.ItemId, p.Key, RowCanonicalDecoder.Decode(p.CanonicalBytes), p.Digest)).ToList();
        try
        {
            return ReprotectionRules.Validate(current, candidate, reprotection.FieldDefinitionId);
        }
        catch (IntegrityFormatException ex)
        {
            throw AuthorityException.Invalid("REPROTECT_ENCODING_INVALID: " + ex.Code);
        }
    }

    private static async Task<SecurityJournalEntry?> FindContentJournalForHeadAsync(IAuthorityScope scope, Guid organizationId, RecordContentHead head, CancellationToken cancellationToken)
    {
        // The checkpoint for this head is the content-stream entry whose operation id equals the committed operation.
        var state = await scope.ReadOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            return null;
        }

        for (var revision = state.ContentIntegrityRevision; revision >= 1; revision--)
        {
            var entry = await scope.FindJournalEntryAsync(organizationId, JournalStreamNames.Content, revision, cancellationToken).ConfigureAwait(false);
            if (entry is not null && entry.ContentChangeOperationId == head.CommittedOperationId && entry.JournalEventKind == "ContentHeadCommitted")
            {
                return entry;
            }
        }

        return null;
    }

    private static async Task<ContentRevision> ToRevisionAsync(IAuthorityScope scope, OrganizationAuthorizationState state, RecordContentHead head, Guid correlationId, CancellationToken cancellationToken)
    {
        var key = await scope.FindKeyRegistrationAsync(state.OrganizationId, IntegrityKeyPurposes.RowIntegrityV1, cancellationToken).ConfigureAwait(false);
        var archiveState = ArchiveDeliveryStates.Anchored;
        if (head.CommittedContentRevision > 0)
        {
            var journal = await FindContentJournalForHeadAsync(scope, state.OrganizationId, head, cancellationToken).ConfigureAwait(false);
            archiveState = journal?.ArchiveDeliveryState ?? ArchiveDeliveryStates.Pending;
        }

        return new ContentRevision(
            head.FormRecordId,
            head.CommittedContentRevision,
            head.CommittedOperationId,
            Convert.ToBase64String(head.CommittedManifestRoot),
            state.AuthorizationRevision,
            head.SignatureKeyVersion,
            key?.CurrentSigningKeyVersion ?? 0,
            archiveState,
            correlationId);
    }

    private static PreparedContentOperation ToPrepared(ContentChangeOperation operation, IReadOnlyList<ContentCandidateLeaf> leaves, Guid correlationId) =>
        new(
            operation.ContentChangeOperationId,
            operation.CandidateContentRevision,
            operation.CandidateKeyVersion,
            Convert.ToBase64String(operation.CandidateManifestRoot),
            leaves.Select(l => new SignedCandidateItem((ManifestItemKind)l.ManifestItemKindCode, l.ManifestItemId, Convert.ToBase64String(l.RowSignature), Convert.ToBase64String(l.CanonicalPayloadDigest), l.MutationKindName)).ToList(),
            operation.OperationStateName,
            correlationId);

    private static MerkleItemKind ToMerkleKind(ManifestItemKind kind) => (MerkleItemKind)(byte)kind;

    private static byte[] SafeBase64(string value)
    {
        try
        {
            return Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            throw AuthorityException.Invalid("invalid base64");
        }
    }

    private sealed record ParsedCandidateItem(ManifestItemKind Kind, Guid ItemId, byte[] Key, byte[] CanonicalBytes, byte[] Digest);

    private sealed record ContentCheckpointBody(Guid FormRecordId, long CommittedContentRevision, Guid CommittedOperationId, string ManifestRootHex, string LeafIndexDigestHex, long LeafCount, int KeyVersion, Guid ActorUserId, long AuthorizationRevision, Guid CorrelationId);

    private sealed record IntegrityIncidentBody(Guid FormRecordId, long CommittedContentRevision, Guid? CommittedOperationId, string FailureReasonCode, Guid ObservedByUserId, Guid CorrelationId);

    private sealed class UnsignedByteComparer : IComparer<byte[]>
    {
        public static readonly UnsignedByteComparer Instance = new();

        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y.AsSpan());
    }
}
