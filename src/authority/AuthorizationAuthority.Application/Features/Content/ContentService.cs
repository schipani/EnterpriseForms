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
public sealed class ContentService(IAuthorityStore store, IIntegrityMacProvider macProvider, ISpiceDbProjection spiceDb, IFaultInjector faultInjector, TimeProvider clock, ShareContributorWriteSwitch contributorWrites)
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
            // Desired-state edges read under the organization lock (A6): the same lock every security command takes.
            var graph = new DesiredStateGraph(await scope.GetActiveEdgesAsync(state.OrganizationId, cancellationToken).ConfigureAwait(false));
            if (graph.IsRecordOwner(bound.AuthorityUserId, request.FormRecordId))
            {
                mutations = ClassifyMutations(parsed, currentLeaves);
            }
            else
            {
                // Phase C5 share editor: switch, scope, current-snapshot proof (D4) and column-class classification (D14/D15).
                RequireShareEditor(graph, bound, request.FormRecordId);
                mutations = await ClassifyShareScopedAsync(scope, state, request, parsed, currentLeaves, head, key, graph, bound, operationId, candidateRevision, correlationId, cancellationToken).ConfigureAwait(false);
            }

            AuthorizeMutations(graph, bound, request.FormRecordId, mutations);
            await EnforceProtectionBarrierAsync(scope, state.OrganizationId, resource.ParentResourceEntityId, parsed.Select(p => (p.Kind, (ReadOnlyMemory<byte>)p.CanonicalBytes)), cancellationToken).ConfigureAwait(false);
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
        faultInjector.CrashIfArmed(FaultNames.CrashAfterPrepareCommit);
        return ToPrepared(operation, candidateLeaves, correlationId);
    }

    public async Task<ContentRevision> CommitAsync(VerifiedActor? actor, Guid operationId, CommitContentChangeRequest request, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Testing only (no-op elsewhere): holds a commit before it takes the organization lock, so integration tests can
        // revoke a grant between prepare and commit deterministically (Phase C5 revocation-race tests).
        await faultInjector.PauseIfArmedAsync(FaultNames.PauseBeforeContentCommit, cancellationToken).ConfigureAwait(false);
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
        if (reprotection is null)
        {
            // Atomic cutover (Amendment A1 req. 7): a candidate prepared before a protection command became effective may not commit rows of the old form.
            var recordResource = await scope.FindResourceAsync(state.OrganizationId, ResourceTypeNames.FormRecord, request.FormRecordId, cancellationToken).ConfigureAwait(false);
            await EnforceProtectionBarrierAsync(scope, state.OrganizationId, recordResource?.ParentResourceEntityId, request.StagedItems.Select(i => (i.ItemKind, (ReadOnlyMemory<byte>)Convert.FromBase64String(i.CanonicalBytesBase64))), cancellationToken).ConfigureAwait(false);
        }
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

        var shareEditorOperation = false;
        if (reprotection is null)
        {
            try
            {
                // Commit-time authorization against the CURRENT desired edges, inside the transaction that holds the
                // organization lock and advances the head: a revocation committed before this point aborts the write.
                var commitGraph = new DesiredStateGraph(await scope.GetActiveEdgesAsync(state.OrganizationId, cancellationToken).ConfigureAwait(false));
                AuthorizeMutations(commitGraph, bound, request.FormRecordId, mutations);
                shareEditorOperation = !commitGraph.IsRecordOwner(bound.AuthorityUserId, request.FormRecordId);
            }
            catch (AuthorityException)
            {
                operation.OperationStateName = ContentOperationStates.Aborted;
                operation.OperationQuarantineReasonCode = "PERMISSION_REVOKED_BEFORE_COMMIT";
                await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }
        }
        else if (mutations.Values.Any(m => m is not (ContentMutationKinds.Unchanged or ContentMutationKinds.Reprotect or ContentMutationKinds.Deprotect)))
        {
            throw AuthorityException.Forbidden("a protection migration operation may only carry reprotect/deprotect mutations");
        }

        // Prove the staged bytes are exactly the approved candidate: same key set, same digests, same signatures, signatures verify, same root.
        var verified = await VerifyStagedAgainstLeavesAsync(request.StagedItems, candidateLeaves.Select(l => (l.ManifestItemKey, l.ManifestItemKindCode, l.ManifestItemId, l.CanonicalPayloadDigest, l.RowSignature)).ToList(), state.OrganizationId, keyReference, operation.CandidateContentRevision, operationId, operation.CandidateManifestRoot, cancellationToken).ConfigureAwait(false);
        if (verified is not null)
        {
            throw new AuthorityException(AuthorityErrorCodes.CandidateMismatch, 409, false, verified);
        }

        if (shareEditorOperation)
        {
            // AC-2 at commit, on the verified staged header (equal to the accepted header except the operation binding, D15).
            // The head compare-and-swap above already rejects a candidate prepared before a lifecycle transition committed;
            // this re-check keeps the lock independent of that reasoning.
            try
            {
                RequireShareEditableStatus(RowCanonicalDecoder.Decode(Convert.FromBase64String(request.StagedItems.Single(i => i.ItemKind == ManifestItemKind.RecordHeader).CanonicalBytesBase64)), "commit");
            }
            catch (AuthorityException)
            {
                operation.OperationStateName = ContentOperationStates.Aborted;
                operation.OperationQuarantineReasonCode = "RECORD_LOCKED_BEFORE_COMMIT";
                await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }
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

    public const int DefaultHistoryPageSize = 50;

    public const int MaximumHistoryPageSize = 100;

    /// <summary>
    /// AC-1 (Phase C6): the accepted content revisions of a record, ascending, from the Authority's own rows only (accepted
    /// manifest commits, their operations, the actors' identity bindings). Requires full read of the record now (effective
    /// owner) at the projection ZedToken with the gate open, and re-reads the authorization state afterwards like
    /// <see cref="VerifyProjectionAsync"/>. Anyone else gets the same 404 as for a record that does not exist. Read-only: no state
    /// change, no journal entry, no row bytes and no signature work (each revision is verified when it is opened).
    /// </summary>
    public async Task<ContentHistoryPage> GetHistoryAsync(VerifiedActor actor, Guid organizationId, Guid formRecordId, long? afterRevision, int? limit, Guid correlationId, CancellationToken cancellationToken)
    {
        if (afterRevision is < 0)
        {
            throw AuthorityException.Invalid("after must be zero or a positive revision");
        }

        var pageSize = Math.Clamp(limit ?? DefaultHistoryPageSize, 1, MaximumHistoryPageSize);
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var state = await scope.ReadOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false)
                    ?? throw AuthorityException.NotRegistered("organization is not registered");
        var bound = await ActorResolver.BindAsync(scope, state.OrganizationId, actor, cancellationToken).ConfigureAwait(false);
        OrganizationGate.RequireOpen(await OrganizationGate.EvaluateAsync(scope, state, cancellationToken).ConfigureAwait(false));

        var resource = await scope.FindResourceAsync(state.OrganizationId, ResourceTypeNames.FormRecord, formRecordId, cancellationToken).ConfigureAwait(false);
        var head = await scope.FindContentHeadAsync(state.OrganizationId, formRecordId, false, cancellationToken).ConfigureAwait(false);
        if (resource is null || resource.ResourceLifecycleState != ResourceLifecycleStates.Active || head is null)
        {
            throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false);
        }

        // History is part of the record's content: only a full reader now (V1, architecture section 3.3), never a share reader,
        // a revoked recipient or a former owner, whatever they could see when a revision was accepted.
        var read = await spiceDb.CheckAsync(new SpiceDbCheck(ResourceTypeNames.FormRecord, formRecordId.ToString("D"), "read", ResourceTypeNames.User, bound.AuthorityUserId.ToString("D")), state.ProjectionZedToken!, cancellationToken).ConfigureAwait(false);
        if (read != SpiceDbCheckOutcome.Allowed)
        {
            throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false); // concealment-safe
        }

        if (head.ContentHeadQuarantined)
        {
            throw AuthorityException.Quarantined("record content is quarantined");
        }

        var commits = await scope.GetManifestCommitsAsync(state.OrganizationId, formRecordId, afterRevision ?? 0, pageSize + 1, cancellationToken).ConfigureAwait(false);
        var hasMore = commits.Count > pageSize;
        if (hasMore)
        {
            commits = commits.Take(pageSize).ToList();
        }

        var operations = (await scope.GetOperationsAsync(commits.Select(c => c.CommittedOperationId).ToList(), cancellationToken).ConfigureAwait(false)).ToDictionary(o => o.ContentChangeOperationId);
        var key = await scope.FindKeyRegistrationAsync(state.OrganizationId, IntegrityKeyPurposes.RowIntegrityV1, cancellationToken).ConfigureAwait(false);
        var users = new Dictionary<Guid, Guid?>();
        var entries = new List<ContentHistoryEntry>(commits.Count);
        foreach (var commit in commits)
        {
            // An accepted commit always has its committed operation of the same record; anything else is reported, not guessed.
            var operation = operations.GetValueOrDefault(commit.CommittedOperationId);
            var consistent = operation is not null && operation.OrganizationId == state.OrganizationId && operation.FormRecordId == formRecordId && operation.OperationStateName == ContentOperationStates.Committed;
            string actorKind;
            Guid? actorUser = null;
            if (!consistent)
            {
                actorKind = ContentHistoryActorKinds.Unknown;
            }
            else if (operation!.ReprotectionCommandId is not null)
            {
                actorKind = ContentHistoryActorKinds.ProtectionMigration;
            }
            else
            {
                if (!users.TryGetValue(operation.ActorIdentityBindingId, out actorUser))
                {
                    var binding = await scope.FindIdentityBindingByIdAsync(operation.ActorIdentityBindingId, cancellationToken).ConfigureAwait(false);
                    actorUser = binding is not null && binding.OrganizationId == state.OrganizationId ? binding.AuthorityUserId : null;
                    users[operation.ActorIdentityBindingId] = actorUser;
                }

                actorKind = actorUser is null ? ContentHistoryActorKinds.Unknown : ContentHistoryActorKinds.User;
            }

            entries.Add(new ContentHistoryEntry(commit.CommittedContentRevision, commit.CommittedOperationId, commit.ManifestCommittedOn, actorKind, actorUser,
                consistent ? operation!.ReprotectionCommandId : null, commit.ManifestLeafCount, commit.RowSignatureKeyVersion,
                key is not null && commit.RowSignatureKeyVersion >= key.MinimumVerificationKeyVersion, commit.CommittedOperationId == head.CommittedOperationId));
        }

        var after = await scope.ReadOrganizationAsync(state.OrganizationId, cancellationToken).ConfigureAwait(false);
        if (after is null || after.AuthorizationRevision != state.AuthorizationRevision || after.ProjectionAppliedRevision != state.ProjectionAppliedRevision)
        {
            throw AuthorityException.NotReady("authorization state moved during the history read");
        }

        return new ContentHistoryPage(state.OrganizationId, formRecordId, head.CommittedContentRevision, entries, hasMore ? entries[^1].ContentRevision : null, state.AuthorizationRevision, correlationId);
    }

    public async Task<VerifyRecordProjectionResult> VerifyProjectionAsync(VerifiedActor actor, VerifyRecordProjectionRequest request, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var state = await scope.LockOrganizationAsync(request.OrganizationId, cancellationToken).ConfigureAwait(false)
                    ?? throw AuthorityException.NotRegistered("organization is not registered");
        var bound = await ActorResolver.BindAsync(scope, state.OrganizationId, actor, cancellationToken).ConfigureAwait(false);
        OrganizationGate.RequireOpen(await OrganizationGate.EvaluateAsync(scope, state, cancellationToken).ConfigureAwait(false));

        var (result, quarantined) = await VerifyRecordCoreAsync(scope, state, bound, request.FormRecordId, request.RequestedAction, request.ObservedItems, request.HistoricCommittedOperationId, null, includeEditable: true, correlationId, cancellationToken).ConfigureAwait(false);
        if (quarantined)
        {
            // Tamper evidence is committed and the failure returned (the authorization re-read cannot change that outcome).
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }

        if (!result.Verified)
        {
            return result;
        }

        var after = await scope.ReadOrganizationAsync(state.OrganizationId, cancellationToken).ConfigureAwait(false);
        if (after is null || after.AuthorizationRevision != state.AuthorizationRevision || after.ProjectionAppliedRevision != state.ProjectionAppliedRevision)
        {
            throw AuthorityException.NotReady("authorization state moved during verification");
        }

        return result;
    }

    /// <summary>Phase C7 OD-6: at most this many records per batch verification call.</summary>
    public const int MaximumBatchRecords = 50;

    /// <summary>The caller's expected head is no longer the accepted head (batch verification): nothing was verified.</summary>
    public const string HeadChanged = "HEAD_CHANGED";

    /// <summary>Not registered, not active or not discoverable for the caller (concealment-safe, like the single operation's 404).</summary>
    public const string RecordUnavailable = "RECORD_UNAVAILABLE";

    /// <summary>
    /// Phase C7 M1 (OD-6): verifies 1..50 records in one call. Each record goes through exactly the same verification as
    /// <see cref="VerifyProjectionAsync"/> — registry, discover/read authorization, quarantine, archive receipt, complete leaf
    /// set, every signature, root, readable field scope — against the head the caller expects; nothing is shared between
    /// records except the organization lock and the projection token. Per-record problems are typed results (the caller
    /// must fail its whole search on any of them, D3); provider outages fail the whole call; tamper evidence is committed
    /// even when the authorization state moved meanwhile (then the call fails as not ready). Read action only.
    /// </summary>
    public async Task<VerifyRecordProjectionsResponse> VerifyProjectionsAsync(VerifiedActor actor, VerifyRecordProjectionsRequest request, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Records is null || request.Records.Count is 0 or > MaximumBatchRecords)
        {
            throw AuthorityException.Invalid($"records must contain 1..{MaximumBatchRecords} items");
        }

        if (request.Records.Select(r => r.FormRecordId).Distinct().Count() != request.Records.Count)
        {
            throw AuthorityException.Invalid("each record may appear once per batch");
        }

        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var state = await scope.LockOrganizationAsync(request.OrganizationId, cancellationToken).ConfigureAwait(false)
                    ?? throw AuthorityException.NotRegistered("organization is not registered");
        var bound = await ActorResolver.BindAsync(scope, state.OrganizationId, actor, cancellationToken).ConfigureAwait(false);
        OrganizationGate.RequireOpen(await OrganizationGate.EvaluateAsync(scope, state, cancellationToken).ConfigureAwait(false));

        var results = new List<BatchVerificationResult>(request.Records.Count);
        var anyQuarantined = false;
        foreach (var item in request.Records)
        {
            try
            {
                var (result, quarantined) = await VerifyRecordCoreAsync(scope, state, bound, item.FormRecordId, RequestedAction.Read, item.ObservedItems ?? [], null, item.ExpectedCommittedOperationId, includeEditable: false, correlationId, cancellationToken).ConfigureAwait(false);
                anyQuarantined |= quarantined;
                var readable = !result.Verified ? [] : result.IsCompleteProjection ? result.AuthorizedItems : result.ReadableItems ?? [];
                results.Add(new BatchVerificationResult(item.FormRecordId, result.Verified, result.FailureReasonCode, result.CommittedContentRevision, result.CommittedOperationId, result.IsCompleteProjection, readable));
            }
            catch (AuthorityException ex) when (ex.ErrorCode is AuthorityErrorCodes.NotFound or AuthorityErrorCodes.IntegrityQuarantine || (ex.ErrorCode == AuthorityErrorCodes.AuthorityNotReady && ex.Message == "AwaitingArchive") || (ex.ErrorCode == AuthorityErrorCodes.KeyUnavailable && !ex.Retryable))
            {
                var code = ex.ErrorCode switch
                {
                    AuthorityErrorCodes.NotFound => RecordUnavailable,
                    AuthorityErrorCodes.IntegrityQuarantine => "QUARANTINED",
                    AuthorityErrorCodes.AuthorityNotReady => "AWAITING_ARCHIVE",
                    _ => AuthorityErrorCodes.KeyUnavailable,
                };
                results.Add(new BatchVerificationResult(item.FormRecordId, false, code, 0, null, false, []));
            }
        }

        var after = await scope.ReadOrganizationAsync(state.OrganizationId, cancellationToken).ConfigureAwait(false);
        var moved = after is null || after.AuthorizationRevision != state.AuthorizationRevision || after.ProjectionAppliedRevision != state.ProjectionAppliedRevision;
        if (anyQuarantined)
        {
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (moved)
        {
            throw AuthorityException.NotReady("authorization state moved during verification");
        }

        return new VerifyRecordProjectionsResponse(results, state.AuthorizationRevision, correlationId);
    }

    /// <summary>Phase C7 M4: the result of one record's protection census (counts only; never contents).</summary>
    public sealed record ProtectionCensusVerification(string Outcome, long CommittedContentRevision, Guid? CommittedOperationId, int FieldOccurrenceCount, int OldFormOccurrenceCount, int EnvelopeIssueCount, string? RecordStatus, bool Quarantined)
    {
        public bool Verified => Outcome == Domain.Features.Catalogue.ProtectionCensusOutcomes.Verified;
    }

    /// <summary>Not a registered record of the scoped form (or its registration was revoked).</summary>
    public const string NotInScope = "NOT_IN_SCOPE";

    /// <summary>The only AEAD envelope the business encrypts with (B-28); anything else in a target-form row is an envelope issue.</summary>
    private const string ApprovedEncryptionAlgorithm = "AES-256-GCM";

    /// <summary>
    /// Phase C7 M4 (G19, verifier workload only — no user, no SpiceDB): verifies one record of the scoped form exactly as
    /// <see cref="VerifyRecordCoreAsync"/> does — registry, quarantine, archive receipt, verification key, complete leaf set,
    /// every HMAC, root — against the head the verifier expects, then counts the rows of <paramref name="fieldDefinitionId"/>
    /// in the verified canonical bytes: all occurrences (top level and repeating sections), those still in the form the
    /// decision removes, and target-form encrypted rows whose envelope is not the approved shape or reuses a nonce under the
    /// same wrapped key. Integrity failures quarantine the head and journal a minimal incident naming the workload, exactly
    /// like a user verification; nothing is repaired or re-signed. Provider outages throw (the caller's whole call fails).
    /// The caller holds the organization lock and commits the scope when <c>Quarantined</c> is true.
    /// </summary>
    public async Task<ProtectionCensusVerification> VerifyForProtectionCensusAsync(
        IAuthorityScope scope,
        OrganizationAuthorizationState state,
        Guid formDefinitionId,
        Guid formRecordId,
        Guid expectedCommittedOperationId,
        IReadOnlyList<StagedContentItem> observedItems,
        Guid fieldDefinitionId,
        ProtectionDirection direction,
        string workloadName,
        Guid scopingSecurityCommandId,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(observedItems);
        ProtectionCensusVerification Failed(string code, long revision = 0, Guid? operation = null, bool quarantined = false) => new(code, revision, operation, 0, 0, 0, null, quarantined);

        var resource = await scope.FindResourceAsync(state.OrganizationId, ResourceTypeNames.FormRecord, formRecordId, cancellationToken).ConfigureAwait(false);
        if (resource is null || resource.ParentResourceEntityId != formDefinitionId || resource.ResourceLifecycleState == ResourceLifecycleStates.Revoked)
        {
            return Failed(NotInScope);
        }

        var head = await scope.FindContentHeadAsync(state.OrganizationId, formRecordId, lockForUpdate: true, cancellationToken).ConfigureAwait(false);
        if (resource.ResourceLifecycleState == ResourceLifecycleStates.Quarantined || head?.ContentHeadQuarantined == true)
        {
            return Failed("QUARANTINED", head?.CommittedContentRevision ?? 0, head?.CommittedOperationId);
        }

        if (resource.ResourceLifecycleState != ResourceLifecycleStates.Active || head is null || head.CommittedContentRevision == 0 || head.CurrentRecordManifestCommitId is null)
        {
            return Failed("NO_COMMITTED_CONTENT");
        }

        if (head.CommittedOperationId != expectedCommittedOperationId)
        {
            return Failed(HeadChanged, head.CommittedContentRevision, head.CommittedOperationId);
        }

        var checkpointJournal = await FindContentJournalForHeadAsync(scope, state.OrganizationId, head, cancellationToken).ConfigureAwait(false);
        if (checkpointJournal is null || checkpointJournal.ArchiveDeliveryState != ArchiveDeliveryStates.Anchored)
        {
            return Failed("AWAITING_ARCHIVE", head.CommittedContentRevision, head.CommittedOperationId);
        }

        var key = await scope.FindKeyRegistrationAsync(state.OrganizationId, IntegrityKeyPurposes.RowIntegrityV1, cancellationToken).ConfigureAwait(false)
                  ?? throw new AuthorityException(AuthorityErrorCodes.KeyUnavailable, 503, true, "organization integrity key unavailable");
        if (head.SignatureKeyVersion < key.MinimumVerificationKeyVersion)
        {
            // A retired verification key: the revision can no longer be verified, so it is reported, never assumed compliant.
            return Failed(AuthorityErrorCodes.KeyUnavailable, head.CommittedContentRevision, head.CommittedOperationId);
        }

        var keyReference = new IntegrityKeyReference(state.OrganizationId, key.KeyPurposeName, key.ProviderKeyName, head.SignatureKeyVersion);
        var expected = await scope.GetManifestLeavesAsync(head.CurrentRecordManifestCommitId.Value, cancellationToken).ConfigureAwait(false);
        var failure = await VerifyStagedAgainstLeavesAsync(observedItems, expected.Select(l => (l.ManifestItemKey, l.ManifestItemKindCode, l.ManifestItemId, l.CanonicalPayloadDigest, l.RowSignature)).ToList(), state.OrganizationId, keyReference, head.CommittedContentRevision, head.CommittedOperationId!.Value, head.CommittedManifestRoot, cancellationToken).ConfigureAwait(false);
        if (failure is not null)
        {
            // Tamper evidence (same rule as every verified read): quarantine the head, journal a minimal incident, never repair.
            head.ContentHeadQuarantined = true;
            head.QuarantineReasonCode = failure;
            head.ContentHeadModifiedOn = clock.GetUtcNow();
            state.ContentIntegrityRevision++;
            var incident = new VerifierIntegrityIncidentBody(formRecordId, head.CommittedContentRevision, head.CommittedOperationId, failure, workloadName, scopingSecurityCommandId, correlationId);
            scope.AddJournalEntry(JournalChain.Append(state, JournalStreamNames.Content, state.ContentIntegrityRevision, "IntegrityVerificationFailed", incident, null, head.CommittedOperationId, clock));
            return Failed(failure, head.CommittedContentRevision, head.CommittedOperationId, quarantined: true);
        }

        // The bytes are now exactly the accepted, signed rows of this revision: count from them alone.
        int occurrences = 0, oldForm = 0, envelopeIssues = 0;
        string? status = null;
        var nonces = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in observedItems)
        {
            var document = RowCanonicalDecoder.Decode(Convert.FromBase64String(item.CanonicalBytesBase64));
            if (item.ItemKind == ManifestItemKind.RecordHeader)
            {
                status = document.Column("record_status")?.AsText();
                continue;
            }

            if (item.ItemKind is not (ManifestItemKind.FieldValue or ManifestItemKind.FieldSelection) || document.Column("field_definition_id")?.AsUuid() != fieldDefinitionId)
            {
                continue;
            }

            occurrences++;
            var encrypted = document.Header.EntityKind == ReprotectionRules.EncryptedEntityKind;
            if (direction == ProtectionDirection.Encrypt ? !encrypted : encrypted)
            {
                oldForm++;
                continue;
            }

            if (encrypted && !EnvelopeLooksApproved(document, nonces))
            {
                envelopeIssues++;
            }
        }

        return new ProtectionCensusVerification(Domain.Features.Catalogue.ProtectionCensusOutcomes.Verified, head.CommittedContentRevision, head.CommittedOperationId, occurrences, oldForm, envelopeIssues, status, false);
    }

    /// <summary>
    /// Shape of a target-form AEAD envelope as far as the Authority can judge without keys: the approved algorithm, a 96-bit
    /// nonce, a 128-bit tag, non-empty ciphertext, wrapped data key and key reference, and no (wrapped key, nonce) pair seen
    /// before in this revision. The tag itself can only be checked by the key holder (the worker's decrypt check).
    /// </summary>
    private static bool EnvelopeLooksApproved(RowCanonicalDocument document, HashSet<string> nonces)
    {
        var algorithm = document.Column("encryption_algorithm_name")?.AsText();
        var nonce = document.Column("encryption_nonce_bytes")?.AsBytes();
        var tag = document.Column("encryption_auth_tag_bytes")?.AsBytes();
        var ciphertext = document.Column("encrypted_value_bytes")?.AsBytes();
        var wrapped = document.Column("wrapped_data_key_bytes")?.AsBytes();
        var keyReference = document.Column("encryption_key_reference")?.AsText();
        if (algorithm != ApprovedEncryptionAlgorithm || nonce is not { Length: 12 } || tag is not { Length: 16 } || ciphertext is not { Length: > 0 } || wrapped is not { Length: > 0 } || string.IsNullOrEmpty(keyReference))
        {
            return false;
        }

        return nonces.Add(Convert.ToHexStringLower(wrapped.Value.Span) + ":" + Convert.ToHexStringLower(nonce.Value.Span));
    }

    /// <summary>
    /// One record's verification (shared by the single and the batch operation). Throws the single operation's typed
    /// failures (not found, quarantined, awaiting archive, key unavailable); returns a failure result for missing content,
    /// a changed head (batch) and integrity mismatches, in which case the quarantine is recorded on the scope and
    /// <c>Quarantined</c> is true so the caller commits it.
    /// </summary>
    private async Task<(VerifyRecordProjectionResult Result, bool Quarantined)> VerifyRecordCoreAsync(
        IAuthorityScope scope,
        OrganizationAuthorizationState state,
        BoundActor bound,
        Guid formRecordId,
        RequestedAction requestedAction,
        IReadOnlyList<StagedContentItem> observedItems,
        Guid? historicCommittedOperationId,
        Guid? expectedCommittedOperationId,
        bool includeEditable,
        Guid correlationId,
        CancellationToken cancellationToken)
    {
        var resource = await scope.FindResourceAsync(state.OrganizationId, ResourceTypeNames.FormRecord, formRecordId, cancellationToken).ConfigureAwait(false);
        var head = await scope.FindContentHeadAsync(state.OrganizationId, formRecordId, lockForUpdate: true, cancellationToken).ConfigureAwait(false);
        if (resource is null || resource.ResourceLifecycleState != ResourceLifecycleStates.Active || head is null)
        {
            throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false);
        }

        var subject = bound.AuthorityUserId.ToString("D");
        var recordId = formRecordId.ToString("D");
        var fullRead = await spiceDb.CheckAsync(new SpiceDbCheck(ResourceTypeNames.FormRecord, recordId, requestedAction == RequestedAction.Edit ? "edit" : "read", ResourceTypeNames.User, subject), state.ProjectionZedToken!, cancellationToken).ConfigureAwait(false) == SpiceDbCheckOutcome.Allowed;
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
            return (new VerifyRecordProjectionResult(false, "NO_COMMITTED_CONTENT", 0, null, state.AuthorizationRevision, [], fullRead, correlationId), false);
        }

        if (expectedCommittedOperationId is { } expectedOperation && head.CommittedOperationId != expectedOperation)
        {
            return (new VerifyRecordProjectionResult(false, HeadChanged, head.CommittedContentRevision, head.CommittedOperationId, state.AuthorizationRevision, [], fullRead, correlationId), false);
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
        if (historicCommittedOperationId is { } historicOperationId && historicOperationId != verifiedOperationId)
        {
            if (!fullRead)
            {
                throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false);
            }

            var historic = await scope.FindManifestCommitByOperationAsync(state.OrganizationId, formRecordId, historicOperationId, cancellationToken).ConfigureAwait(false);
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

        var failure = await VerifyStagedAgainstLeavesAsync(observedItems, expected.Select(l => (l.ManifestItemKey, l.ManifestItemKindCode, l.ManifestItemId, l.CanonicalPayloadDigest, l.RowSignature)).ToList(), state.OrganizationId, keyReference, verifiedRevision, verifiedOperationId, verifiedRoot, cancellationToken).ConfigureAwait(false);
        if (failure is not null)
        {
            // Tamper evidence: quarantine the record head and journal a minimal incident; never auto-repair or re-sign.
            head.ContentHeadQuarantined = true;
            head.QuarantineReasonCode = failure;
            head.ContentHeadModifiedOn = clock.GetUtcNow();
            state.ContentIntegrityRevision++;
            var incident = new IntegrityIncidentBody(formRecordId, verifiedRevision, verifiedOperationId, failure, bound.AuthorityUserId, correlationId);
            scope.AddJournalEntry(JournalChain.Append(state, JournalStreamNames.Content, state.ContentIntegrityRevision, "IntegrityVerificationFailed", incident, null, head.CommittedOperationId, clock));
            return (new VerifyRecordProjectionResult(false, failure, verifiedRevision, verifiedOperationId, state.AuthorizationRevision, [], fullRead, correlationId), true);
        }

        List<VerifiedProjectionItem> authorized;
        List<VerifiedProjectionItem> readable = [];
        List<VerifiedProjectionItem> editable = [];
        if (fullRead)
        {
            authorized = expected.Select(l => new VerifiedProjectionItem((ManifestItemKind)l.ManifestItemKindCode, l.ManifestItemId)).ToList();
        }
        else
        {
            // Field-level shares scope value leaves and selection leaves (a plaintext dropdown / multi-select stores one selection leaf per option) by the same field-item edges.
            // Phase C5 (D7): both the readable and the editable subsets are returned, so a partial editor needs one verification; advisory only — prepare/commit decide.
            var fieldLeaves = expected.Where(l => l.ManifestItemKindCode is (byte)ManifestItemKind.FieldValue or (byte)ManifestItemKind.FieldSelection).ToList();
            readable = await CheckFieldLeavesAsync(fieldLeaves, "read", subject, state.ProjectionZedToken!, cancellationToken).ConfigureAwait(false);
            editable = includeEditable ? await CheckFieldLeavesAsync(fieldLeaves, "edit", subject, state.ProjectionZedToken!, cancellationToken).ConfigureAwait(false) : [];
            authorized = requestedAction == RequestedAction.Edit ? editable : readable;
        }

        return (new VerifyRecordProjectionResult(true, null, verifiedRevision, verifiedOperationId, state.AuthorizationRevision, authorized, fullRead, correlationId, readable, editable, contributorWrites.Enabled), false);
    }

    private async Task<List<VerifiedProjectionItem>> CheckFieldLeavesAsync(List<RecordManifestLeaf> fieldLeaves, string permission, string subject, string zedToken, CancellationToken cancellationToken)
    {
        if (fieldLeaves.Count == 0)
        {
            return [];
        }

        var checks = fieldLeaves.Select(l => new SpiceDbCheck(ResourceTypeNames.FieldItem, l.ManifestItemId.ToString("D"), permission, ResourceTypeNames.User, subject)).ToList();
        var outcomes = await spiceDb.CheckBulkAsync(checks, zedToken, cancellationToken).ConfigureAwait(false);
        return fieldLeaves.Where((_, i) => outcomes[i] == SpiceDbCheckOutcome.Allowed).Select(l => new VerifiedProjectionItem((ManifestItemKind)l.ManifestItemKindCode, l.ManifestItemId)).ToList();
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

            // Identity binding I1–I3 for every user and reprotection write (Phase C5, D8/D14): supported schema, exact
            // column set, item id inside the bytes = manifest item id, tenant and record columns = the request.
            RowCanonicalDocument document;
            try
            {
                document = RowCanonicalDecoder.Decode(bytes);
            }
            catch (IntegrityFormatException ex)
            {
                throw AuthorityException.Invalid("candidate bytes are not RowHmac v1: " + ex.Code);
            }

            ContentDeltaRules.EnforceIdentityBinding(item.ItemKind, item.ItemId, document, organizationId, request.FormRecordId);

            var key = MerkleManifestV1.ItemKey(ToMerkleKind(item.ItemKind), item.ItemId);
            if (!keys.Add(Convert.ToHexStringLower(key)))
            {
                throw AuthorityException.Invalid("duplicate candidate item");
            }

            parsed.Add(new ParsedCandidateItem(item.ItemKind, item.ItemId, key, bytes, SHA256.HashData(bytes), document));
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

    /// <summary>
    /// Owner: any mutation. Share contributor/manager (Phase C5): only when contributor writes are enabled, and only
    /// <c>Update</c> of explicitly shared field items (value or single selection); every other item <c>Unchanged</c>.
    /// Called at prepare (after classification) and again at commit against the current desired edges.
    /// </summary>
    private void AuthorizeMutations(DesiredStateGraph graph, BoundActor actor, Guid formRecordId, IReadOnlyDictionary<string, string> mutations)
    {
        if (graph.IsRecordOwner(actor.AuthorityUserId, formRecordId))
        {
            return;
        }

        var editable = RequireShareEditor(graph, actor, formRecordId);
        foreach (var (keyHex, mutation) in mutations)
        {
            if (mutation == ContentMutationKinds.Unchanged)
            {
                continue;
            }

            var key = Convert.FromHexString(keyHex);
            var kind = (MerkleItemKind)key[0];
            var itemId = new Guid(key.AsSpan(1, 16), bigEndian: true);
            if (mutation != ContentMutationKinds.Update || kind is not (MerkleItemKind.FieldValue or MerkleItemKind.FieldSelection) || !editable.Contains(itemId))
            {
                throw AuthorityException.Forbidden("SHARE_MUTATION_OUT_OF_SCOPE: mutation exceeds the actor's edit scope");
            }
        }
    }

    /// <summary>
    /// AC-2 (Phase C6, approved C-43): a share editor may change content only while the record's signed header status is
    /// Draft. Submitted and Archived records are locked; an unreadable or missing status fails closed. Owners are not
    /// subject to this rule here (their lifecycle transitions are owner content writes; the business API locks their
    /// ordinary edits, R-01). Signed formats, manifests and the journal are unchanged.
    /// </summary>
    public static void RequireShareEditableStatus(RowCanonicalDocument? header, string stage)
    {
        var status = header?.Column("record_status") is { IsNull: false } column ? column.AsText() : null;
        if (!string.Equals(status, "Draft", StringComparison.Ordinal))
        {
            throw new AuthorityException(AuthorityErrorCodes.RecordNotEditable, 409, false, $"RECORD_LOCKED: shared contributors cannot change a record whose accepted status is {status ?? "unknown"} ({stage})");
        }
    }

    /// <summary>A non-owner may write only through an explicit contributor/manager share, and only while the switch is enabled (fail closed).</summary>
    private IReadOnlySet<Guid> RequireShareEditor(DesiredStateGraph graph, BoundActor actor, Guid formRecordId)
    {
        var editable = graph.EditableFieldItemsViaShares(actor.AuthorityUserId, formRecordId);
        if (editable.Count == 0)
        {
            throw AuthorityException.Forbidden("actor may not change this record");
        }

        if (!contributorWrites.Enabled)
        {
            throw new AuthorityException(AuthorityErrorCodes.ContributorWritesDisabled, 403, false, "field-scoped contributor writes are disabled");
        }

        return editable;
    }

    /// <summary>
    /// Share-editor classification (Phase C5 proposal §7). The supplied current snapshot must equal the accepted head
    /// exactly (D4: coverage, SHA-256 per item against the accepted digests, header bindings, recomputed root and leaf-index
    /// digest); a mismatch journals a minimal integrity incident and signs nothing. Then every candidate item is classified
    /// by the column-class rules (D14/D15) against its bound current item.
    /// </summary>
    private async Task<Dictionary<string, string>> ClassifyShareScopedAsync(IAuthorityScope scope, OrganizationAuthorizationState state, PrepareContentChangeRequest request, IReadOnlyList<ParsedCandidateItem> parsed,
        IReadOnlyList<RecordManifestLeaf> currentLeaves, RecordContentHead head, IntegrityKeyRegistration key, DesiredStateGraph graph, BoundActor bound, Guid operationId, long candidateRevision, Guid correlationId, CancellationToken cancellationToken)
    {
        if (request.CurrentItems is null || request.CurrentItems.Count == 0)
        {
            throw AuthorityException.Invalid("CURRENT_SNAPSHOT_REQUIRED: a share-scoped write must carry the complete current snapshot");
        }

        if (head.CommittedOperationId is not { } headOperation || head.CurrentRecordManifestCommitId is null || head.CommittedContentRevision == 0)
        {
            throw AuthorityException.Invalid("record has no accepted content");
        }

        if (head.SignatureKeyVersion < key.MinimumVerificationKeyVersion)
        {
            throw new AuthorityException(AuthorityErrorCodes.KeyUnavailable, 503, false, "accepted key version is no longer verifiable");
        }

        var commit = await scope.FindManifestCommitByOperationAsync(state.OrganizationId, request.FormRecordId, headOperation, cancellationToken).ConfigureAwait(false);
        if (commit is null || commit.RecordManifestCommitId != head.CurrentRecordManifestCommitId || commit.CommittedContentRevision != head.CommittedContentRevision
            || commit.RowSignatureKeyVersion != head.SignatureKeyVersion || !commit.ManifestRootDigest.AsSpan().SequenceEqual(head.CommittedManifestRoot))
        {
            throw new AuthorityException(AuthorityErrorCodes.CurrentSnapshotMismatch, 409, false, "ACCEPTED_STATE_INCONSISTENT");
        }

        var accepted = new ContentDeltaRules.AcceptedState(head.CommittedContentRevision, headOperation, head.CommittedManifestRoot, commit.ManifestLeafIndexDigest, commit.ManifestLeafCount, head.SignatureKeyVersion,
            currentLeaves.Select(l => (l.ManifestItemKey, l.ManifestItemKindCode, l.ManifestItemId, l.CanonicalPayloadDigest, l.RowSignature)).ToList());
        var (failure, current) = ContentDeltaRules.BindCurrentSnapshot(request.CurrentItems, accepted, state.OrganizationId);
        if (failure is not null)
        {
            state.ContentIntegrityRevision++;
            var incident = new IntegrityIncidentBody(request.FormRecordId, head.CommittedContentRevision, headOperation, failure, bound.AuthorityUserId, correlationId);
            scope.AddJournalEntry(JournalChain.Append(state, JournalStreamNames.Content, state.ContentIntegrityRevision, "CurrentSnapshotMismatch", incident, null, headOperation, clock));
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            throw new AuthorityException(AuthorityErrorCodes.CurrentSnapshotMismatch, 409, false, failure);
        }

        // AC-2: the lifecycle lock, decided from the signed header of the accepted head (the snapshot was just bound to it).
        RequireShareEditableStatus(current.Where(i => i.Kind == ManifestItemKind.RecordHeader).Select(i => i.Document).SingleOrDefault(), "prepare");

        var candidate = parsed.Select(p => new ContentDeltaRules.DecodedItem(p.Kind, p.ItemId, p.Key, p.Document, p.Digest)).ToList();
        var shareScope = new ContentDeltaRules.ShareScope(state.OrganizationId, request.FormRecordId, head.CommittedContentRevision, headOperation, candidateRevision, operationId,
            graph.EditableFieldItemsViaShares(bound.AuthorityUserId, request.FormRecordId));
        return ContentDeltaRules.ClassifyShareScoped(current, candidate, shareScope);
    }

    private sealed record ReprotectionContext(Guid CommandId, Guid FormDefinitionId, Guid FieldDefinitionId, ProtectionDirection Direction);

    /// <summary>Fields of the form with an active encryption requirement must appear encrypted; fields with an active release must appear plaintext (user operations only).</summary>
    private static async Task EnforceProtectionBarrierAsync(IAuthorityScope scope, Guid organizationId, Guid? formDefinitionId, IEnumerable<(ManifestItemKind Kind, ReadOnlyMemory<byte> Bytes)> items, CancellationToken cancellationToken)
    {
        if (formDefinitionId is null)
        {
            return;
        }

        var protections = await scope.GetActiveResourcesByParentAsync(organizationId, formDefinitionId.Value, [ResourceTypeNames.FieldProtectionRequirement, ResourceTypeNames.FieldProtectionRelease], cancellationToken).ConfigureAwait(false);
        if (protections.Count == 0)
        {
            return;
        }

        var mustBeEncrypted = protections.Where(p => p.ResourceTypeName == ResourceTypeNames.FieldProtectionRequirement).Select(p => p.ResourceEntityId).ToHashSet();
        var mustBePlaintext = protections.Where(p => p.ResourceTypeName == ResourceTypeNames.FieldProtectionRelease).Select(p => p.ResourceEntityId).ToHashSet();
        try
        {
            ReprotectionRules.EnforceBarrier(items, mustBeEncrypted, mustBePlaintext);
        }
        catch (IntegrityFormatException ex)
        {
            throw AuthorityException.Invalid("candidate bytes are not RowHmac v1: " + ex.Code);
        }
    }

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

        var decision = await ProtectionCommandResolver.ResolveAsync(scope, state.OrganizationId, reprotectionCommandId.Value, cancellationToken).ConfigureAwait(false);
        var binding = decision.ActorBinding;
        var direction = decision.Direction;
        var recorded = new VerifiedActor(binding.IdentityIssuerUri, binding.IdentitySubjectIdentifier, direction == ProtectionDirection.Encrypt ? "reprotection-worker" : "deprotection-worker");
        return (new BoundActor(recorded, binding), new ReprotectionContext(decision.CommandId, decision.FormDefinitionId, decision.FieldDefinitionId, direction));
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
            return ReprotectionRules.Validate(current, candidate, reprotection.FieldDefinitionId, reprotection.Direction);
        }
        catch (IntegrityFormatException ex)
        {
            throw AuthorityException.Invalid("REPROTECT_ENCODING_INVALID: " + ex.Code);
        }
    }

    private static async Task<SecurityJournalEntry?> FindContentJournalForHeadAsync(IAuthorityScope scope, Guid organizationId, RecordContentHead head, CancellationToken cancellationToken)
    {
        // The checkpoint for this head is the content-stream ContentHeadCommitted entry whose operation id equals the committed
        // operation. Phase C7 M1: one indexed lookup instead of walking back through every content revision of the organization
        // (that walk made each verification O(revisions since the record's commit) and a whole scope O(N²)). Same result: the
        // latest such entry.
        return head.CommittedOperationId is { } operation
            ? await scope.FindContentCommitJournalEntryAsync(organizationId, operation, cancellationToken).ConfigureAwait(false)
            : null;
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

    private sealed record ParsedCandidateItem(ManifestItemKind Kind, Guid ItemId, byte[] Key, byte[] CanonicalBytes, byte[] Digest, RowCanonicalDocument Document);

    private sealed record ContentCheckpointBody(Guid FormRecordId, long CommittedContentRevision, Guid CommittedOperationId, string ManifestRootHex, string LeafIndexDigestHex, long LeafCount, int KeyVersion, Guid ActorUserId, long AuthorizationRevision, Guid CorrelationId);

    private sealed record IntegrityIncidentBody(Guid FormRecordId, long CommittedContentRevision, Guid? CommittedOperationId, string FailureReasonCode, Guid ObservedByUserId, Guid CorrelationId);

    /// <summary>Phase C7 M4: the same minimal incident, observed by the verifier workload during a protection census.</summary>
    private sealed record VerifierIntegrityIncidentBody(Guid FormRecordId, long CommittedContentRevision, Guid? CommittedOperationId, string FailureReasonCode, string ObservedByWorkloadName, Guid ScopingSecurityCommandId, Guid CorrelationId);

    private sealed class UnsignedByteComparer : IComparer<byte[]>
    {
        public static readonly UnsignedByteComparer Instance = new();

        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y.AsSpan());
    }
}
