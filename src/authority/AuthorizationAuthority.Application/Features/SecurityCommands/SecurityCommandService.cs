using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Journal;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;

namespace AuthorizationAuthority.Application.Features.SecurityCommands;

/// <summary>Authoritative security-commit point (CROSS_SYSTEM_CONSISTENCY_PROTOCOL.md §2 "Command sequence").</summary>
public sealed class SecurityCommandService(IAuthorityStore store, IFaultInjector faultInjector, TimeProvider clock, FieldSearchApprovalGate approvalGate)
{
    public async Task<OperationStatus> SubmitAsync(VerifiedActor actor, SecurityCommandRequest request, string idempotencyKey, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateIdempotencyKey(idempotencyKey);

        // Idempotent identity = (organization, actor, key, canonical body WITHOUT the CAS precondition): a legitimate retry
        // after the original commit observes a newer authorization revision; the precondition is enforced separately below.
        var requestDigest = CanonicalJson.Digest(CanonicalJson.SerializeBytes(request with { ExpectedAuthorizationRevision = 0 }));

        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var state = await scope.LockOrganizationAsync(request.OrganizationId, cancellationToken).ConfigureAwait(false)
                    ?? throw AuthorityException.NotRegistered("organization is not registered");
        var bound = await ActorResolver.BindAsync(scope, state.OrganizationId, actor, cancellationToken).ConfigureAwait(false);

        var existing = await scope.FindCommandByIdempotencyKeyAsync(state.OrganizationId, idempotencyKey, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (!existing.CommandRequestDigest.AsSpan().SequenceEqual(requestDigest) || existing.ActorIdentityBindingId != bound.Binding.IdentityBindingId)
            {
                throw new AuthorityException(AuthorityErrorCodes.IdempotencyConflict, 409, false, "idempotency key reused with a different request or actor");
            }

            return await ToStatusAsync(scope, state, existing, cancellationToken).ConfigureAwait(false);
        }

        var command = new SecurityCommand
        {
            SecurityCommandId = Guid.CreateVersion7(),
            OrganizationId = state.OrganizationId,
            ActorIdentityBindingId = bound.Binding.IdentityBindingId,
            CommandKindName = request.CommandKind.ToString(),
            CommandIdempotencyKey = idempotencyKey,
            CommandRequestDigest = requestDigest,
            ExpectedAuthorizationRevision = request.ExpectedAuthorizationRevision,
            CommandPayloadJson = request.CommandPayload.GetRawText(),
            CommandReasonText = request.ChangeReason,
            CorrelationIdentifier = correlationId,
            CommandCreatedOn = clock.GetUtcNow(),
            CommandModifiedOn = clock.GetUtcNow(),
            CommandStateName = SecurityCommandStates.Rejected,
        };

        try
        {
            if (state.IntegrityQuarantineEnabled)
            {
                throw AuthorityException.Quarantined("organization quarantined");
            }

            if (request.ExpectedAuthorizationRevision != state.AuthorizationRevision)
            {
                throw new AuthorityException(AuthorityErrorCodes.StaleAuthorizationRevision, 409, false, "expected_authorization_revision is stale");
            }

            var graph = new DesiredStateGraph(await scope.GetActiveEdgesAsync(state.OrganizationId, cancellationToken).ConfigureAwait(false));
            var plan = await CommandPlanner.PlanAsync(scope, state.OrganizationId, bound, request, graph, cancellationToken, approvalGate).ConfigureAwait(false);
            command.CommandPayloadJson = plan.NormalizedPayloadJson;
            ApplyPlan(scope, state, command, plan, bound, request, cancellationToken);
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (AuthorityException ex)
        {
            // Durable audit of the rejection (A12); no revision is consumed.
            command.CommandStateName = SecurityCommandStates.Rejected;
            command.RejectionReasonCode = ex.ErrorCode;
            scope.AddCommand(command);
            if (request.CommandKind is SecurityCommandKind.ApproveFieldSearch or SecurityCommandKind.WithdrawFieldSearch or SecurityCommandKind.UpdateFieldSearchers or SecurityCommandKind.RegisterPublishedFormVersion)
            {
                // OD-8: refused AC-6 decisions are also hash-chained, archived evidence on the audit stream (no values, no predicates).
                AuditJournal.Append(scope, state, "FieldSearchCommandRejected", new { security_command_id = command.SecurityCommandId, command_kind = request.CommandKind.ToString(), form_definition_id = request.Resource.ResourceId, actor_identity_binding_id = bound.Binding.IdentityBindingId, rejection_reason = ex.ErrorCode, rejection_detail = ex.ErrorCode is AuthorityErrorCodes.FieldSearchNotEligible ? ex.Message : null, correlation_id = correlationId }, clock);
            }
            else if (request.CommandKind is SecurityCommandKind.GrantSystemRole or SecurityCommandKind.RevokeSystemRole)
            {
                // Refused Security Administrator appointments and removals: independent, archived evidence on the audit stream.
                AuditJournal.Append(scope, state, "SecurityAdministratorCommandRejected", new { security_command_id = command.SecurityCommandId, command_kind = request.CommandKind.ToString(), actor_identity_binding_id = bound.Binding.IdentityBindingId, rejection_reason = ex.ErrorCode, correlation_id = correlationId }, clock);
            }
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }

        faultInjector.CrashIfArmed(FaultNames.CrashAfterJournalCommit);
        return await ToStatusAsync(scope, state, command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OperationStatus> GetStatusAsync(VerifiedActor actor, Guid commandId, Guid correlationId, CancellationToken cancellationToken)
    {
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var command = await scope.FindCommandAsync(commandId, cancellationToken).ConfigureAwait(false)
                      ?? throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false);
        var state = await scope.ReadOrganizationAsync(command.OrganizationId, cancellationToken).ConfigureAwait(false)
                    ?? throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false);
        var bound = await ActorResolver.BindAsync(scope, state.OrganizationId, actor, cancellationToken).ConfigureAwait(false);
        if (command.ActorIdentityBindingId != bound.Binding.IdentityBindingId)
        {
            // Only the originating actor (or a privileged administrator, not implemented in Stage A) may poll a command.
            throw new AuthorityException(AuthorityErrorCodes.NotFound, 404, false);
        }

        var status = await ToStatusAsync(scope, state, command, cancellationToken).ConfigureAwait(false);
        return status with { CorrelationId = correlationId };
    }

    /// <summary>Applies the plan inside the locked organization transaction: desired state, immutable command, outbox and journal — one commit.</summary>
    internal static void ApplyPlan(IAuthorityScope scope, OrganizationAuthorizationState state, SecurityCommand command, CommandPlan plan, BoundActor actor, SecurityCommandRequest request, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var now = command.CommandCreatedOn;
        var revision = state.AuthorizationRevision + 1;
        var delta = new List<RelationshipDeltaEntry>();

        foreach (var edge in plan.RemoveEdges)
        {
            edge.RemovedBySecurityCommandId = command.SecurityCommandId;
            edge.RelationshipRemovedOn = now;
            delta.Add(new RelationshipDeltaEntry(RelationshipDeltaOperations.Delete, edge.ToTuple()));
        }

        foreach (var (resourceType, resourceId, relation, subjectType, subjectId, subjectRelation) in plan.AddEdges)
        {
            var edge = new AuthorityRelationshipEdge
            {
                AuthorityRelationshipEdgeId = Guid.CreateVersion7(),
                OrganizationId = state.OrganizationId,
                ResourceTypeName = resourceType,
                ResourceEntityId = resourceId,
                RelationshipName = relation,
                SubjectTypeName = subjectType,
                SubjectEntityId = subjectId,
                SubjectRelationName = subjectRelation,
                CreatedBySecurityCommandId = command.SecurityCommandId,
                RelationshipCreatedOn = now,
            };
            scope.AddEdge(edge);
            delta.Add(new RelationshipDeltaEntry(RelationshipDeltaOperations.Touch, edge.ToTuple()));
        }

        foreach (var resource in plan.RegisterResources)
        {
            resource.RegisteredBySecurityCommandId = command.SecurityCommandId;
            resource.ResourceRegisteredOn = now;
            scope.AddResource(resource);
            if (resource.ResourceTypeName == Domain.Features.Organizations.ResourceTypeNames.FormRecord)
            {
                scope.AddContentHead(new Domain.Features.Content.RecordContentHead
                {
                    RecordContentHeadId = Guid.CreateVersion7(),
                    OrganizationId = state.OrganizationId,
                    FormRecordId = resource.ResourceEntityId,
                    CommittedContentRevision = 0,
                    CommittedManifestRoot = EnterpriseForms.Integrity.Merkle.MerkleManifestV1.EmptyRoot(),
                    SignatureKeyVersion = 1,
                    ContentHeadModifiedOn = now,
                });
            }
        }

        foreach (var resource in plan.ReactivateResources)
        {
            resource.ResourceLifecycleState = Domain.Features.Organizations.ResourceLifecycleStates.Active;
            resource.RegisteredBySecurityCommandId = command.SecurityCommandId;
            resource.ResourceRegisteredOn = now;
        }

        if (plan.ProtectionPolicy is { } policy)
        {
            policy.UpdatedBySecurityCommandId = command.SecurityCommandId;
            policy.PolicyUpdatedOn = now;
            if (plan.ProtectionPolicyIsNew)
            {
                scope.AddProtectionPolicy(policy);
            }
        }

        if (plan.PublishedVersion is { } publishedVersion)
        {
            publishedVersion.RegisteredBySecurityCommandId = command.SecurityCommandId;
            publishedVersion.RegisteredOn = now;
            scope.AddPublishedFormVersion(publishedVersion);
        }

        foreach (var approval in plan.NewApprovals)
        {
            approval.ApprovedBySecurityCommandId = command.SecurityCommandId;
            approval.ApprovedOn = now;
            scope.AddFieldSearchApproval(approval);
        }

        foreach (var approval in plan.ChangedApprovals)
        {
            approval.ChangedBySecurityCommandId = command.SecurityCommandId;
            approval.StateChangedOn = now;
        }

        if (plan.SearchPolicyToAdvance is { } searchPolicy)
        {
            searchPolicy.SearchPolicyRevision++;
        }

        command.CommandStateName = SecurityCommandStates.CommittedAwaitingProjection;
        command.CommittedAuthorizationRevision = revision;
        command.CommandModifiedOn = now;
        scope.AddCommand(command);

        scope.AddOutboxEvent(new ProjectionOutboxEvent
        {
            ProjectionOutboxEventId = Guid.CreateVersion7(),
            OrganizationId = state.OrganizationId,
            SecurityCommandId = command.SecurityCommandId,
            TargetAuthorizationRevision = revision,
            RelationshipDeltaJson = CanonicalJson.Serialize(delta),
            ProjectionCreatedOn = now,
        });

        state.AuthorizationRevision = revision;
        state.StateModifiedOn = now;

        var body = new SecurityCommandJournalBody(
            command.SecurityCommandId,
            command.CommandKindName,
            actor.AuthorityUserId,
            actor.Actor.IssuerUri,
            actor.Actor.SubjectIdentifier,
            request.Resource.ResourceKind.ToString(),
            request.Resource.ResourceId,
            command.ExpectedAuthorizationRevision,
            revision,
            Convert.ToHexStringLower(command.CommandRequestDigest),
            System.Text.Json.JsonDocument.Parse(plan.NormalizedPayloadJson).RootElement,
            delta.Select(d => $"{d.Operation} {d.Tuple.Canonical}").ToList(),
            plan.RegisterResources.Concat(plan.ReactivateResources).Select(r => $"{r.ResourceTypeName}:{r.ResourceEntityId}").ToList(),
            plan.RevokeResources.Select(r => $"{r.ResourceTypeName}:{r.ResourceEntityId}").ToList(),
            command.CommandReasonText,
            command.CorrelationIdentifier);
        scope.AddJournalEntry(JournalChain.Append(state, JournalStreamNames.Authorization, revision, "SecurityCommandCommitted", body, command.SecurityCommandId, null, TimeProvider.System));
    }

    internal static async Task<OperationStatus> ToStatusAsync(IAuthorityScope scope, OrganizationAuthorizationState state, SecurityCommand command, CancellationToken cancellationToken)
    {
        var gate = await OrganizationGate.EvaluateAsync(scope, state, cancellationToken).ConfigureAwait(false);
        return new OperationStatus(
            command.SecurityCommandId,
            Enum.Parse<CommandStatus>(command.CommandStateName),
            command.CommittedAuthorizationRevision ?? state.AuthorizationRevision,
            state.ProjectionAppliedRevision,
            gate,
            command.RejectionReasonCode,
            command.CorrelationIdentifier);
    }

    private static void ValidateIdempotencyKey(string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length is < 8 or > 128)
        {
            throw AuthorityException.Invalid("Idempotency-Key must be 8..128 characters");
        }
    }

    private sealed record SecurityCommandJournalBody(
        Guid SecurityCommandId,
        string CommandKind,
        Guid ActorUserId,
        string ActorIssuerUri,
        string ActorSubjectIdentifier,
        string ResourceKind,
        Guid ResourceId,
        long ExpectedAuthorizationRevision,
        long CommittedAuthorizationRevision,
        string RequestDigestHex,
        System.Text.Json.JsonElement NormalizedPayload,
        IReadOnlyList<string> RelationshipDelta,
        IReadOnlyList<string> RegisteredResources,
        IReadOnlyList<string> RevokedResources,
        string? ChangeReason,
        Guid CorrelationId);
}
