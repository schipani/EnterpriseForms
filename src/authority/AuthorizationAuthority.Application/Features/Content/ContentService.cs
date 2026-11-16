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
public sealed class ContentService(IAuthorityStore store, IIntegrityMacProvider macProvider, ProjectionIntegrityVerifier verifier, IFaultInjector faultInjector, TimeProvider clock, ShareContributorWriteSwitch contributorWrites, AuthorizationAuthority.Application.Features.SearchIndex.ContentRevisionCoordinator? revisionCoordinator = null, IOperationTimingObserver? timing = null, EnvelopeV2AcceptanceSwitch? envelopeV2Acceptance = null)
{
    // E3-M2e (OD-M2e-07): Envelope v2 acceptance, default off. The Authority host always composes it; without one (unit tests,
    // tools) acceptance is off and the Development/Testing wrap codes apply, so the E3-M2d behaviour of existing fixtures holds.
    private readonly EnvelopeV2AcceptanceSwitch v2Acceptance = envelopeV2Acceptance ?? EnvelopeV2AcceptanceSwitch.DarkForTests;

    // IX-1 (INV-R): the only way the content-integrity revision advances; without a configured index it still keeps INV-R.
    private readonly AuthorizationAuthority.Application.Features.SearchIndex.ContentRevisionCoordinator revisions = revisionCoordinator ?? AuthorizationAuthority.Application.Features.SearchIndex.ContentRevisionCoordinator.WithoutIndex;

    private const int MaxCandidateItems = 500;

    /// <summary>E3-M2b-2: abort reason recorded on an operation whose verified staged bytes fail the registered-schema rule (S-9) at commit.</summary>
    public const string SignedSchemaInvalidAtCommitReason = "SIGNED_SCHEMA_INVALID_AT_COMMIT";

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
        var timer = new LockTiming();
        var outcome = "Completed";
        try
        {
            return await PrepareCoreAsync(actor, request, idempotencyKey, correlationId, timer, cancellationToken).ConfigureAwait(false);
        }
        catch (AuthorityException ex)
        {
            outcome = ex.ErrorCode;
            throw;
        }
        catch (Exception ex)
        {
            outcome = ex.GetType().Name;
            throw;
        }
        finally
        {
            timing?.ContentOperation(new ContentOperationTiming(request.OrganizationId, "Prepare", outcome, timer.LockedMilliseconds ?? 0, timer.ElapsedMilliseconds));
        }
    }

    private async Task<PreparedContentOperation> PrepareCoreAsync(VerifiedActor? actor, PrepareContentChangeRequest request, string idempotencyKey, Guid correlationId, LockTiming timer, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length is < 8 or > 128)
        {
            throw AuthorityException.Invalid("Idempotency-Key must be 8..128 characters");
        }

        // Idempotent identity excludes the authorization-revision CAS precondition (see SecurityCommandService); content revision/root stay part of the identity.
        var requestDigest = CanonicalJson.Digest(CanonicalJson.SerializeBytes(request with { ExpectedAuthorizationRevision = 0 }));
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var state = await scope.LockOrganizationAsync(request.OrganizationId, cancellationToken).ConfigureAwait(false)
                    ?? throw AuthorityException.NotRegistered("organization is not registered");
        timer.Locked();
        // RB-CARV-1 (contract §22): Release B verifies the floor in this transaction before anything else, replays included.
        await scope.RequireContentAcceptanceFloorAsync(cancellationToken).ConfigureAwait(false);
        var (bound, reprotection) = await ResolveActorAsync(scope, state, actor, request.ReprotectionCommandId, cancellationToken).ConfigureAwait(false);
        OrganizationGate.RequireOpen(await OrganizationGate.EvaluateAsync(scope, state, cancellationToken).ConfigureAwait(false));

        var existing = await scope.FindOperationByIdempotencyKeyAsync(state.OrganizationId, idempotencyKey, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (!existing.OperationRequestDigest.AsSpan().SequenceEqual(requestDigest) || existing.ActorIdentityBindingId != bound.Binding.IdentityBindingId)
            {
                throw new AuthorityException(AuthorityErrorCodes.IdempotencyConflict, 409, false, "idempotency key reused with a different request or actor");
            }

            // E3-M2b-1 CARV (OD-M2-18): a Prepared operation stamped with other rules (an older build's NULL stamp included) is never
            // handed back for commit; nothing is written. Replays of operations in any other state keep their existing behaviour.
            if (existing.OperationStateName == ContentOperationStates.Prepared && !ContentAcceptanceRules.IsCommittableByThisBuild(existing.PrepareAcceptanceRuleVersion))
            {
                throw new AuthorityException(AuthorityErrorCodes.OperationRulesOutdated, 409, false, "the prepared operation was prepared under other content acceptance rules; start a fresh operation");
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

        // E3-M2e (AM-9): Envelope v2 wrap codes {1, 2} only in Development/Testing; elsewhere no v2 row is admitted at all.
        EnvelopeV2Rules.EnforceEnvironmentWrapCodes(parsed.Select(p => p.Document), v2Acceptance.AllowedWrapCodes);

        // R-53 (C-148): the complete current snapshot is proven equal to the accepted head on every write path, and the
        // candidate's item identities are checked against it and against the permanent bindings (ContentItemIdentityRules).
        var current = await BindAcceptedSnapshotAsync(scope, state, request, currentLeaves, head, key, bound, correlationId, cancellationToken).ConfigureAwait(false);

        // E3-M2a remediation: no data-key or nonce reuse against the verified current revision, on every write path (owner,
        // share editor, protection migration; creation has an empty current revision). Commit binds the same head by its
        // revision compare-and-set, so a candidate checked against an older head can never be accepted on a newer one.
        EnvelopeRules.EnforceAgainstCurrent(parsed.Select(p => (p.ItemId, p.Document)), current.Select(c => (c.ItemId, c.Document)));

        // E3-M2d (CARV 2; Prepare-only, needs the verified current revision): every v2 row is Carried (a byte-identical schema-2 row
        // of the current accepted head) or New; DK-3; every New v2 row is refused (no v2 acceptance in E3-M2d); no v2 item may be
        // downgraded to a v1 envelope; no v1 row may reuse a current v2 wrapped key. Before classification, authorization or signing.
        // E3-M2e (CARV 3): the same Prepare-only rules now return the New rows; with acceptance off (the default, OD-M2e-07) they are
        // refused here exactly as in E3-M2d (ENVELOPE_VERSION_NOT_ENABLED; share-edit codes OA-M2d-3 unchanged).
        var newV2Rows = EnvelopeV2Rules.ClassifyAgainstCurrent(parsed.Select(p => (p.ItemId, p.Document)).ToList(), current.Select(c => (c.ItemId, c.Document)).ToList());
        if (newV2Rows.Count > 0 && !v2Acceptance.Enabled)
        {
            throw EnvelopeRules.Refused(EnvelopeV2Rules.Reasons.VersionNotEnabled);
        }

        await ContentItemIdentityRules.EnforceAtPrepareAsync(scope, state.OrganizationId, request.FormRecordId,
            current.Select(i => (i.Kind, i.ItemId, i.Document)).ToList(), parsed.Select(p => (p.Kind, p.ItemId, p.Document)).ToList(), cancellationToken).ConfigureAwait(false);
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
                mutations = ClassifyShareScoped(request, parsed, current, head, state.OrganizationId, graph, bound, operationId, candidateRevision);
            }

            AuthorizeMutations(graph, bound, request.FormRecordId, mutations);
            await EnforceProtectionBarrierAsync(scope, state.OrganizationId, resource.ParentResourceEntityId, parsed.Select(p => (p.Kind, (ReadOnlyMemory<byte>)p.CanonicalBytes)), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            mutations = await AuthorizeReprotectionAsync(request, parsed, currentLeaves, head, key, reprotection, cancellationToken).ConfigureAwait(false);
        }

        // E3-M2e DK-4 (OD-M2e-12: after authorization, before signing): no New identifier may be bound anywhere, in any state. Reached
        // only with acceptance on. The ledger is verified in this transaction first (L-13); the unique index is the backstop (L-3).
        IReadOnlyList<DataKeyLedgerRules.DataKeyGroup> newV2Groups = newV2Rows.Count == 0 ? [] : DataKeyLedgerRules.GroupsOf(newV2Rows);
        if (newV2Groups.Count > 0)
        {
            await scope.RequireDataKeyLedgerAsync(cancellationToken).ConfigureAwait(false);
            var existingBindings = await scope.FindDataKeyBindingsAsync(newV2Groups.Select(g => g.DataKeyIdentifier).ToList(), cancellationToken).ConfigureAwait(false);
            if (DataKeyLedgerRules.PrepareViolation(newV2Groups, existingBindings) is { } reuse)
            {
                throw EnvelopeRules.Refused(reuse);
            }
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

            // E3-M2b-1 CARV: the rules applied above; immutable after this insert (database state guard).
            PrepareAcceptanceRuleVersion = ContentAcceptanceRules.CurrentVersion,
        };
        scope.AddOperation(operation);

        // E3-M2e DK-5 (OD-M2e-02/03): one permanent binding per New group, in this transaction with the operation and its candidate
        // leaves (EF inserts the operation first; the P-1 guard re-checks the binding against it in the database).
        if (newV2Groups.Count > 0)
        {
            scope.AddDataKeyBindings(DataKeyLedgerRules.BindingsFor(newV2Groups, state.OrganizationId, request.FormRecordId, operationId, clock.GetUtcNow()));
        }

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
        var timer = new LockTiming();
        var outcome = "Completed";
        try
        {
            return await CommitCoreAsync(actor, operationId, request, correlationId, timer, cancellationToken).ConfigureAwait(false);
        }
        catch (AuthorityException ex)
        {
            outcome = ex.ErrorCode;
            throw;
        }
        catch (Exception ex)
        {
            outcome = ex.GetType().Name;
            throw;
        }
        finally
        {
            timing?.ContentOperation(new ContentOperationTiming(request.OrganizationId, "Commit", outcome, timer.LockedMilliseconds ?? 0, timer.ElapsedMilliseconds));
        }
    }

    private async Task<ContentRevision> CommitCoreAsync(VerifiedActor? actor, Guid operationId, CommitContentChangeRequest request, Guid correlationId, LockTiming timer, CancellationToken cancellationToken)
    {

        // Testing only (no-op elsewhere): holds a commit before it takes the organization lock, so integration tests can
        // revoke a grant between prepare and commit deterministically (Phase C5 revocation-race tests).
        await faultInjector.PauseIfArmedAsync(FaultNames.PauseBeforeContentCommit, cancellationToken).ConfigureAwait(false);
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var state = await scope.LockOrganizationAsync(request.OrganizationId, cancellationToken).ConfigureAwait(false)
                    ?? throw AuthorityException.NotRegistered("organization is not registered");
        timer.Locked();
        // RB-CARV-1 (contract §22): Release B verifies the floor in this transaction before anything else, replays included.
        await scope.RequireContentAcceptanceFloorAsync(cancellationToken).ConfigureAwait(false);
        OrganizationGate.RequireOpen(await OrganizationGate.EvaluateAsync(scope, state, cancellationToken).ConfigureAwait(false));

        var operation = await scope.FindOperationAsync(operationId, cancellationToken).ConfigureAwait(false);
        if (operation is null || operation.OrganizationId != state.OrganizationId || operation.FormRecordId != request.FormRecordId)
        {
            throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false);
        }

        // A reprotection operation is committed by the workload under its command; a user operation by the same user.
        var (bound, reprotection) = await ResolveActorAsync(scope, state, actor, operation.ReprotectionCommandId, cancellationToken).ConfigureAwait(false);
        Guid? registeredFormDefinitionId = null; // the record's form from the Authority registry, reused by the index derivation (IX-1 remediation)
        if (reprotection is null)
        {
            // Atomic cutover (Amendment A1 req. 7): a candidate prepared before a protection command became effective may not commit rows of the old form.
            var recordResource = await scope.FindResourceAsync(state.OrganizationId, ResourceTypeNames.FormRecord, request.FormRecordId, cancellationToken).ConfigureAwait(false);
            registeredFormDefinitionId = recordResource?.ParentResourceEntityId;
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

        // E3-M2b-1 CARV (§19.4 step 4): before any verification, an operation prepared under other rules (an older build's NULL
        // stamp included) is aborted; it can never be accepted by this build. The database refuses the same commit independently.
        if (!ContentAcceptanceRules.IsCommittableByThisBuild(operation.PrepareAcceptanceRuleVersion))
        {
            operation.OperationStateName = ContentOperationStates.Aborted;
            operation.OperationQuarantineReasonCode = ContentAcceptanceRules.OutdatedAbortReason;
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            throw new AuthorityException(AuthorityErrorCodes.OperationRulesOutdated, 409, false, "the operation was prepared under other content acceptance rules; start a fresh operation");
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

        // R-53 (C-148): every committed item matches its permanent identity binding; new items are bound in this transaction.
        var stagedDocuments = request.StagedItems.Select(i => (i.ItemKind, i.ItemId, RowCanonicalDecoder.Decode(Convert.FromBase64String(i.CanonicalBytesBase64)))).ToList();

        // E3-M2b-2 (S-9, owner decision D-2): the registered-schema rule (supported schema, exact columns, pinned types and
        // nullability) again on the verified staged bytes, so an operation prepared by a build without S-9 cannot commit a row
        // it refuses. Commit re-derives it from the bytes alone, so the content acceptance rule version is unchanged.
        try
        {
            foreach (var staged in stagedDocuments)
            {
                ContentDeltaRules.EnforceRegisteredSchema(staged.Item3);
            }
        }
        catch (AuthorityException)
        {
            operation.OperationStateName = ContentOperationStates.Aborted;
            operation.OperationQuarantineReasonCode = SignedSchemaInvalidAtCommitReason;
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }

        // E3-M2b-2 R-M2b2-11 (S-4): the schema/envelope tie again on the verified staged bytes, so an operation prepared by a
        // build without S-4 cannot commit a mismatched row. Re-derived from the bytes alone (the CARV version is unchanged). A
        // refusal aborts the operation and accepts nothing.
        try
        {
            EnvelopeSchemaTie.Enforce(stagedDocuments.Select(d => d.Item3));
        }
        catch (AuthorityException)
        {
            operation.OperationStateName = ContentOperationStates.Aborted;
            operation.OperationQuarantineReasonCode = EnvelopeSchemaTie.MismatchAtCommitReason;
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }

        // E3-M2a: the envelope rules again on the verified staged bytes, so an operation prepared before the rules existed (an
        // older Authority build) cannot commit an envelope they refuse. A refusal aborts the operation, nothing is accepted.
        try
        {
            EnvelopeRules.Enforce(stagedDocuments.Select(d => d.Item3));
            EnvelopeV2Rules.EnforceEnvironmentWrapCodes(stagedDocuments.Select(d => d.Item3), v2Acceptance.AllowedWrapCodes); // E3-M2e (AM-9)
        }
        catch (AuthorityException)
        {
            operation.OperationStateName = ContentOperationStates.Aborted;
            operation.OperationQuarantineReasonCode = "ENVELOPE_INVALID_AT_COMMIT";
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }

        // E3-M2e (replaces the E3-M2d blanket refusal, OA-M2d-1): a v2 row is committed only when the data-key ledger confirms it.
        // DK-6: the bindings of this operation equal exactly the New groups of the verified staged bytes. P-4: every other v2 group is
        // bound to a Committed operation of this organization and record, kind and commitment, with no more rows than its binding.
        // A mismatch aborts the operation and journals a minimal content-stream incident (OD-M2e-11). Then the switch re-check: with
        // acceptance off, a New group is refused (tightening wins). Integrity first, so a forged operation is an incident either way.
        var stagedV2Groups = DataKeyLedgerRules.GroupsOf(stagedDocuments.Select(d => d.Item3));
        var bindingsOfOperation = await scope.GetDataKeyBindingsForOperationAsync(operationId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ContentDataKeyBinding> newBindings = [];
        if (stagedV2Groups.Count > 0 || bindingsOfOperation.Count > 0)
        {
            await scope.RequireDataKeyLedgerAsync(cancellationToken).ConfigureAwait(false); // L-13
            var bindingsOfStaged = await scope.FindDataKeyBindingsAsync(stagedV2Groups.Select(g => g.DataKeyIdentifier).ToList(), cancellationToken).ConfigureAwait(false);
            var bindingOperationIds = bindingsOfStaged.Select(b => b.BindingContentChangeOperationId).Where(id => id != operationId).Distinct().ToList();
            var committedOperationIds = bindingOperationIds.Count == 0
                ? new HashSet<Guid>()
                : (await scope.GetOperationsAsync(bindingOperationIds, cancellationToken).ConfigureAwait(false))
                    .Where(o => o.OperationStateName == ContentOperationStates.Committed && o.OrganizationId == state.OrganizationId && o.FormRecordId == request.FormRecordId)
                    .Select(o => o.ContentChangeOperationId).ToHashSet();
            var (bindingViolation, newGroups) = DataKeyLedgerRules.CommitDecision(stagedV2Groups, bindingsOfOperation, bindingsOfStaged, committedOperationIds, state.OrganizationId, request.FormRecordId, operationId);
            if (bindingViolation is not null)
            {
                operation.OperationStateName = ContentOperationStates.Aborted;
                operation.OperationQuarantineReasonCode = DataKeyLedgerFailures.BindingMismatchReason;
                await revisions.AdvanceWithoutIndexChangeAsync(scope, state, cancellationToken).ConfigureAwait(false); // W4 (IX-1, INV-R)
                var incident = new IntegrityIncidentBody(request.FormRecordId, head.CommittedContentRevision, head.CommittedOperationId, bindingViolation, bound.AuthorityUserId, correlationId);
                scope.AddJournalEntry(JournalChain.Append(state, JournalStreamNames.Content, state.ContentIntegrityRevision, "IntegrityVerificationFailed", incident, null, head.CommittedOperationId, clock));
                await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
                throw DataKeyLedgerFailures.BindingMismatch();
            }

            if (newGroups.Count > 0 && !v2Acceptance.Enabled)
            {
                operation.OperationStateName = ContentOperationStates.Aborted;
                operation.OperationQuarantineReasonCode = EnvelopeV2Rules.NotEnabledAtCommitReason;
                await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
                throw EnvelopeRules.Refused(EnvelopeV2Rules.Reasons.VersionNotEnabled);
            }

            newBindings = bindingsOfOperation;
        }

        var newIdentities = await ContentItemIdentityRules.BindAtCommitAsync(scope, state.OrganizationId, request.FormRecordId, operationId, stagedDocuments, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);

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
        scope.AddItemIdentities(newIdentities);
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
        operation.CommitAcceptanceRuleVersion = ContentAcceptanceRules.CurrentVersion; // CARV: C = V (= P, checked above and by the database)

        // W1 (IX-1): derive and seal the record's search index from the bytes verified above, then advance both revisions (INV-R).
        await revisions.AdvanceForCommitAsync(scope, state, new AuthorizationAuthority.Application.Features.SearchIndex.CommitIndexInput(request.FormRecordId, head, request.StagedItems, now, registeredFormDefinitionId), cancellationToken).ConfigureAwait(false);
        if (faultInjector.IsArmed(FaultNames.CrashAfterSearchIndexDerivation))
        {
            // Testing only (D-IX1-8, revised by the IX-1 remediation): with the crash point armed, the queued index rows are first
            // written inside the transaction, so the crash proves they roll back with the head (IX-64). Unarmed, they are written
            // by the commit's final SaveChanges in the same transaction, which saves one round trip.
            await scope.FlushAsync(cancellationToken).ConfigureAwait(false);
            faultInjector.CrashIfArmed(FaultNames.CrashAfterSearchIndexDerivation);
        }
        // E3-M2e DK-8 (AM-3): the complete tuples of the New groups' bindings, always present from CARV 3 (empty when there are none).
        var checkpointBody = new ContentCheckpointBody(request.FormRecordId, head.CommittedContentRevision, operationId, Convert.ToHexStringLower(head.CommittedManifestRoot), Convert.ToHexStringLower(commit.ManifestLeafIndexDigest), commit.ManifestLeafCount, operation.CandidateKeyVersion, bound.AuthorityUserId, state.AuthorizationRevision, correlationId,
            DataKeyLedgerRules.ArchiveTuples(newBindings));
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

        var snapshot = await verifier.PinAsync(scope, state, organizationLocked: false, correlationId, cancellationToken).ConfigureAwait(false);
        var discover = await verifier.CheckAsync(snapshot, new SpiceDbCheck(ResourceTypeNames.FormRecord, formRecordId.ToString("D"), "discover", ResourceTypeNames.User, bound.AuthorityUserId.ToString("D")), cancellationToken).ConfigureAwait(false);
        if (!await verifier.CompleteAsync(snapshot, "head", cancellationToken).ConfigureAwait(false))
        {
            throw AuthorityException.NotReady("authorization state moved during head lookup");
        }

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
        var snapshot = await verifier.PinAsync(scope, state, organizationLocked: false, correlationId, cancellationToken).ConfigureAwait(false);
        var read = await verifier.CheckAsync(snapshot, new SpiceDbCheck(ResourceTypeNames.FormRecord, formRecordId.ToString("D"), "read", ResourceTypeNames.User, bound.AuthorityUserId.ToString("D")), cancellationToken).ConfigureAwait(false);
        if (!await verifier.CompleteAsync(snapshot, "history", cancellationToken).ConfigureAwait(false))
        {
            throw AuthorityException.NotReady("authorization state moved during history");
        }

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

        var snapshot = await verifier.PinAsync(scope, state, organizationLocked: true, correlationId, cancellationToken).ConfigureAwait(false);
        var (result, quarantined) = await VerifyRecordCoreAsync(scope, state, snapshot, bound, request.FormRecordId, request.RequestedAction, request.ObservedItems, request.HistoricCommittedOperationId, null, includeEditable: true, correlationId, cancellationToken).ConfigureAwait(false);
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

        // The organization lock is held, so the state cannot move; the decisive marker re-read still runs (S1).
        await verifier.CompleteAsync(snapshot, "verify_projection", cancellationToken).ConfigureAwait(false);

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

        var snapshot = await verifier.PinAsync(scope, state, organizationLocked: true, correlationId, cancellationToken).ConfigureAwait(false);
        var results = new List<BatchVerificationResult>(request.Records.Count);
        var anyQuarantined = false;
        foreach (var item in request.Records)
        {
            try
            {
                var (result, quarantined) = await VerifyRecordCoreAsync(scope, state, snapshot, bound, item.FormRecordId, RequestedAction.Read, item.ObservedItems ?? [], null, item.ExpectedCommittedOperationId, includeEditable: false, correlationId, cancellationToken).ConfigureAwait(false);
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

        // Decisive marker re-read after every record's decision (S1); a mismatch quarantines and commits any head quarantines too.
        await verifier.CompleteAsync(snapshot, "verify_projections", cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Phase C7 M4 (G19, verifier workload only — no user, no SpiceDB): verifies one record of the scoped form exactly as
    /// <see cref="VerifyRecordCoreAsync"/> does — registry, quarantine, archive receipt, verification key, complete leaf set,
    /// every HMAC, root — against the head the verifier expects, then counts the rows of <paramref name="fieldDefinitionId"/>
    /// in the verified canonical bytes: all occurrences (top level and repeating sections), those still in the form the
    /// decision removes, and target-form encrypted rows whose envelope is not the approved shape or reuses another row's wrapped
    /// data key (E3-M2a: with the same or a different nonce). Integrity failures quarantine the head and journal a minimal incident naming the workload, exactly
    /// like a user verification; nothing is repaired or re-signed. Provider outages throw (the caller's whole call fails).
    /// The caller holds the organization lock and commits the scope when <c>Quarantined</c> is true.
    /// IX-2: the verification itself is <see cref="VerifyWorkloadObservedContentAsync"/>, shared with the search-index builder.
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
        ArgumentNullException.ThrowIfNull(observedItems);
        var verification = await VerifyWorkloadObservedContentAsync(scope, state, formDefinitionId, formRecordId, expectedCommittedOperationId, observedItems, new WorkloadVerificationContext(workloadName, scopingSecurityCommandId, correlationId), cancellationToken).ConfigureAwait(false);
        if (!verification.Verified)
        {
            return new ProtectionCensusVerification(verification.Outcome, verification.CommittedContentRevision, verification.CommittedOperationId, 0, 0, 0, null, verification.Quarantined);
        }

        var head = verification.Head!;
        // The bytes are now exactly the accepted, signed rows of this revision: count from them alone.
        int occurrences = 0, oldForm = 0, envelopeIssues = 0;
        string? status = null;
        var nonces = new EnvelopeV2Rules.CensusState(); // E3-M2d: v1 rows exactly as before; v2 rows by the v2 rules (per revision)
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

    /// <summary>Who submitted observed bytes on the workload plane (journaled with an integrity incident; never contents).</summary>
    /// <param name="ScopingSecurityCommandId">G19 census: the protection decision; null for the IX-2 search-index builder.</param>
    public sealed record WorkloadVerificationContext(string WorkloadName, Guid? ScopingSecurityCommandId, Guid CorrelationId);

    /// <summary>The result of verifying workload-submitted bytes against the current accepted head (no user, no SpiceDB).</summary>
    public sealed record WorkloadContentVerification(string Outcome, long CommittedContentRevision, Guid? CommittedOperationId, bool Quarantined, RecordContentHead? Head)
    {
        public bool Verified => Outcome == Domain.Features.Catalogue.ProtectionCensusOutcomes.Verified;
    }

    /// <summary>
    /// The shared verification of the workload plane (G19 census; IX-2 search-index backfill and deep sweep): one registered
    /// record of <paramref name="formDefinitionId"/> is checked exactly as a user verification checks it — registry, quarantine,
    /// archive receipt, verification key, complete leaf set, every HMAC, root — against the head the caller expects (a moved head
    /// is <c>HEAD_CHANGED</c>, never verified). Integrity failures quarantine the head (W2, INV-R) and journal a minimal incident
    /// naming the workload; nothing is repaired or re-signed. Provider outages throw. The caller holds the organization lock and
    /// commits the scope when <c>Quarantined</c> is true. On success the observed items are exactly the accepted signed rows.
    /// </summary>
    public async Task<WorkloadContentVerification> VerifyWorkloadObservedContentAsync(
        IAuthorityScope scope,
        OrganizationAuthorizationState state,
        Guid formDefinitionId,
        Guid formRecordId,
        Guid expectedCommittedOperationId,
        IReadOnlyList<StagedContentItem> observedItems,
        WorkloadVerificationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(observedItems);
        ArgumentNullException.ThrowIfNull(context);
        WorkloadContentVerification Failed(string code, long revision = 0, Guid? operation = null, bool quarantined = false, RecordContentHead? at = null) => new(code, revision, operation, quarantined, at);

        var resource = await scope.FindResourceAsync(state.OrganizationId, ResourceTypeNames.FormRecord, formRecordId, cancellationToken).ConfigureAwait(false);
        if (resource is null || resource.ParentResourceEntityId != formDefinitionId || resource.ResourceLifecycleState == ResourceLifecycleStates.Revoked)
        {
            return Failed(NotInScope);
        }

        var head = await scope.FindContentHeadAsync(state.OrganizationId, formRecordId, lockForUpdate: true, cancellationToken).ConfigureAwait(false);
        if (resource.ResourceLifecycleState == ResourceLifecycleStates.Quarantined || head?.ContentHeadQuarantined == true)
        {
            return Failed("QUARANTINED", head?.CommittedContentRevision ?? 0, head?.CommittedOperationId, at: head);
        }

        if (resource.ResourceLifecycleState != ResourceLifecycleStates.Active || head is null || head.CommittedContentRevision == 0 || head.CurrentRecordManifestCommitId is null)
        {
            return Failed("NO_COMMITTED_CONTENT");
        }

        if (head.CommittedOperationId != expectedCommittedOperationId)
        {
            return Failed(HeadChanged, head.CommittedContentRevision, head.CommittedOperationId, at: head);
        }

        var checkpointJournal = await FindContentJournalForHeadAsync(scope, state.OrganizationId, head, cancellationToken).ConfigureAwait(false);
        if (checkpointJournal is null || checkpointJournal.ArchiveDeliveryState != ArchiveDeliveryStates.Anchored)
        {
            return Failed("AWAITING_ARCHIVE", head.CommittedContentRevision, head.CommittedOperationId, at: head);
        }

        var key = await scope.FindKeyRegistrationAsync(state.OrganizationId, IntegrityKeyPurposes.RowIntegrityV1, cancellationToken).ConfigureAwait(false)
                  ?? throw new AuthorityException(AuthorityErrorCodes.KeyUnavailable, 503, true, "organization integrity key unavailable");
        if (head.SignatureKeyVersion < key.MinimumVerificationKeyVersion)
        {
            // A retired verification key: the revision can no longer be verified, so it is reported, never assumed compliant.
            return Failed(AuthorityErrorCodes.KeyUnavailable, head.CommittedContentRevision, head.CommittedOperationId, at: head);
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
            await revisions.AdvanceWithoutIndexChangeAsync(scope, state, cancellationToken).ConfigureAwait(false); // W2 (IX-1, INV-R)
            object incident = context.ScopingSecurityCommandId is { } command
                ? new VerifierIntegrityIncidentBody(formRecordId, head.CommittedContentRevision, head.CommittedOperationId, failure, context.WorkloadName, command, context.CorrelationId)
                : new WorkloadIntegrityIncidentBody(formRecordId, head.CommittedContentRevision, head.CommittedOperationId, failure, context.WorkloadName, context.CorrelationId);
            scope.AddJournalEntry(JournalChain.Append(state, JournalStreamNames.Content, state.ContentIntegrityRevision, "IntegrityVerificationFailed", incident, null, head.CommittedOperationId, clock));
            return Failed(failure, head.CommittedContentRevision, head.CommittedOperationId, quarantined: true, at: head);
        }

        return new WorkloadContentVerification(Domain.Features.Catalogue.ProtectionCensusOutcomes.Verified, head.CommittedContentRevision, head.CommittedOperationId, false, head);
    }

    /// <summary>
    /// Shape of a target-form AEAD envelope as far as the Authority can judge without keys: the v1 rules every write enforces
    /// (<see cref="EnvelopeRules"/>, E3-M2a), and no wrapped data key seen before in this revision. Rows accepted before
    /// E3-M2a may still fail them, which is what the census reports. The tag itself can only be checked by the key holder.
    /// </summary>
    private static bool EnvelopeLooksApproved(RowCanonicalDocument document, EnvelopeV2Rules.CensusState census) =>
        EnvelopeSchemaTie.Violation(document) is null // E3-M2b-2 S-4: the schema/envelope tie, as on every write
        && census.Accepts(document); // E3-M2a rules for v1 rows (one value per v1 data key); E3-M2d v2 rules for v2 rows

    /// <summary>
    /// One record's verification (shared by the single and the batch operation). Throws the single operation's typed
    /// failures (not found, quarantined, awaiting archive, key unavailable); returns a failure result for missing content,
    /// a changed head (batch) and integrity mismatches, in which case the quarantine is recorded on the scope and
    /// <c>Quarantined</c> is true so the caller commits it.
    /// </summary>
    private async Task<(VerifyRecordProjectionResult Result, bool Quarantined)> VerifyRecordCoreAsync(
        IAuthorityScope scope,
        OrganizationAuthorizationState state,
        VerifiedProjectionSnapshot snapshot,
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
        var fullRead = await verifier.CheckAsync(snapshot, new SpiceDbCheck(ResourceTypeNames.FormRecord, recordId, requestedAction == RequestedAction.Edit ? "edit" : "read", ResourceTypeNames.User, subject), cancellationToken).ConfigureAwait(false) == SpiceDbCheckOutcome.Allowed;
        if (!fullRead)
        {
            var discover = await verifier.CheckAsync(snapshot, new SpiceDbCheck(ResourceTypeNames.FormRecord, recordId, "discover", ResourceTypeNames.User, subject), cancellationToken).ConfigureAwait(false);
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
            await revisions.AdvanceWithoutIndexChangeAsync(scope, state, cancellationToken).ConfigureAwait(false); // W3 (IX-1, INV-R)
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
            readable = await CheckFieldLeavesAsync(snapshot, fieldLeaves, "read", subject, cancellationToken).ConfigureAwait(false);
            editable = includeEditable ? await CheckFieldLeavesAsync(snapshot, fieldLeaves, "edit", subject, cancellationToken).ConfigureAwait(false) : [];
            authorized = requestedAction == RequestedAction.Edit ? editable : readable;
        }

        return (new VerifyRecordProjectionResult(true, null, verifiedRevision, verifiedOperationId, state.AuthorizationRevision, authorized, fullRead, correlationId, readable, editable, contributorWrites.Enabled), false);
    }

    private async Task<List<VerifiedProjectionItem>> CheckFieldLeavesAsync(VerifiedProjectionSnapshot snapshot, List<RecordManifestLeaf> fieldLeaves, string permission, string subject, CancellationToken cancellationToken)
    {
        if (fieldLeaves.Count == 0)
        {
            return [];
        }

        var checks = fieldLeaves.Select(l => new SpiceDbCheck(ResourceTypeNames.FieldItem, l.ManifestItemId.ToString("D"), permission, ResourceTypeNames.User, subject)).ToList();
        var outcomes = await verifier.CheckBulkAsync(snapshot, checks, cancellationToken).ConfigureAwait(false);
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

        // E3-M2b-2 R-M2b2-11 (S-4): the signed schema version and the envelope version of every encrypted row are tied
        // (1/1 or 2/2 only), independently of and before the envelope rules, on every write path.
        EnvelopeSchemaTie.Enforce(parsed.Select(p => p.Document));

        // E3-M2a: every encrypted row of every write path (owner, share editor, protection migration; new and carried rows)
        // must be a well-formed v1 envelope, before anything is classified or signed.
        EnvelopeRules.Enforce(parsed.Select(p => p.Document));
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
    /// <summary>
    /// Decision D4, applied to every write since R-53 (C-148): for a record with accepted content, the request must carry the
    /// complete current snapshot, proven equal to the accepted head (coverage, digests, bindings, root, leaf index). A mismatch
    /// is journaled as an integrity incident and refused. A record without content has an empty current snapshot.
    /// </summary>
    private async Task<List<ContentDeltaRules.DecodedItem>> BindAcceptedSnapshotAsync(IAuthorityScope scope, OrganizationAuthorizationState state, PrepareContentChangeRequest request,
        IReadOnlyList<RecordManifestLeaf> currentLeaves, RecordContentHead head, IntegrityKeyRegistration key, BoundActor bound, Guid correlationId, CancellationToken cancellationToken)
    {
        if (head.CommittedContentRevision == 0 && head.CurrentRecordManifestCommitId is null)
        {
            if (request.CurrentItems is { Count: > 0 })
            {
                throw AuthorityException.Invalid("CURRENT_SNAPSHOT_UNEXPECTED: the record has no accepted content");
            }

            return [];
        }

        if (request.CurrentItems is null || request.CurrentItems.Count == 0)
        {
            throw AuthorityException.Invalid("CURRENT_SNAPSHOT_REQUIRED: a write to a record with accepted content must carry the complete current snapshot");
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
            await revisions.AdvanceWithoutIndexChangeAsync(scope, state, cancellationToken).ConfigureAwait(false); // W4 (IX-1, INV-R)
            var incident = new IntegrityIncidentBody(request.FormRecordId, head.CommittedContentRevision, headOperation, failure, bound.AuthorityUserId, correlationId);
            scope.AddJournalEntry(JournalChain.Append(state, JournalStreamNames.Content, state.ContentIntegrityRevision, "CurrentSnapshotMismatch", incident, null, headOperation, clock));
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            throw new AuthorityException(AuthorityErrorCodes.CurrentSnapshotMismatch, 409, false, failure);
        }

        return current;
    }

    private static Dictionary<string, string> ClassifyShareScoped(PrepareContentChangeRequest request, IReadOnlyList<ParsedCandidateItem> parsed, List<ContentDeltaRules.DecodedItem> current,
        RecordContentHead head, Guid organizationId, DesiredStateGraph graph, BoundActor bound, Guid operationId, long candidateRevision)
    {
        if (current.Count == 0 || head.CommittedOperationId is not { } headOperation)
        {
            throw AuthorityException.Invalid("record has no accepted content");
        }

        // AC-2: the lifecycle lock, decided from the signed header of the accepted head (the snapshot was just bound to it).
        RequireShareEditableStatus(current.Where(i => i.Kind == ManifestItemKind.RecordHeader).Select(i => i.Document).SingleOrDefault(), "prepare");

        var candidate = parsed.Select(p => new ContentDeltaRules.DecodedItem(p.Kind, p.ItemId, p.Key, p.Document, p.Digest)).ToList();
        var shareScope = new ContentDeltaRules.ShareScope(organizationId, request.FormRecordId, head.CommittedContentRevision, headOperation, candidateRevision, operationId,
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

    /// <summary>
    /// The <c>ContentHeadCommitted</c> body. E3-M2e (AM-3, DK-8): <see cref="NewDataKeyBindings"/> is present on every commit by a CARV 3 or
    /// later build; bodies written before carry no such field and hold no accepted v2 binding by construction (E3-M2d refused every v2 row).
    /// </summary>
    private sealed record ContentCheckpointBody(Guid FormRecordId, long CommittedContentRevision, Guid CommittedOperationId, string ManifestRootHex, string LeafIndexDigestHex, long LeafCount, int KeyVersion, Guid ActorUserId, long AuthorizationRevision, Guid CorrelationId,
        IReadOnlyList<DataKeyLedgerRules.ArchivedBinding> NewDataKeyBindings);

    private sealed record IntegrityIncidentBody(Guid FormRecordId, long CommittedContentRevision, Guid? CommittedOperationId, string FailureReasonCode, Guid ObservedByUserId, Guid CorrelationId);

    /// <summary>Phase C7 M4: the same minimal incident, observed by the verifier workload during a protection census.</summary>
    private sealed record VerifierIntegrityIncidentBody(Guid FormRecordId, long CommittedContentRevision, Guid? CommittedOperationId, string FailureReasonCode, string ObservedByWorkloadName, Guid ScopingSecurityCommandId, Guid CorrelationId);

    /// <summary>IX-2: an integrity incident found on bytes submitted by the search-index builder (no protection decision scopes it).</summary>
    private sealed record WorkloadIntegrityIncidentBody(Guid FormRecordId, long CommittedContentRevision, Guid? CommittedOperationId, string FailureReasonCode, string ObservedByWorkloadName, Guid CorrelationId);

    private sealed class UnsignedByteComparer : IComparer<byte[]>
    {
        public static readonly UnsignedByteComparer Instance = new();

        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y.AsSpan());
    }
}
