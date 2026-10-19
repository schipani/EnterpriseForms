using System.Text.Json;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.SecurityCommands;

namespace AuthorizationAuthority.Application.Features.Projection;

/// <summary>
/// Strictly ordered per-organization SpiceDB projection (A4) with an external marker CAS on every write (§5) and
/// idempotent reconciliation after ambiguous outcomes (§4). Applies revision applied+1 only, and only after the
/// revision's journal entry holds an archive receipt (G14 refinement).
/// </summary>
public sealed class ProjectionProcessor(IAuthorityStore store, ISpiceDbProjection spiceDb, IFaultInjector faultInjector, TimeProvider clock)
{
    public async Task<ProjectionStepResult> ProcessOrganizationAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var state = await scope.LockOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false);
        if (state is null || state.ProjectionAppliedRevision >= state.AuthorizationRevision)
        {
            return ProjectionStepResult.Idle;
        }

        var target = state.ProjectionAppliedRevision + 1;
        var outbox = await scope.FindOutboxEventAsync(organizationId, target, cancellationToken).ConfigureAwait(false);
        if (outbox is null)
        {
            state.IntegrityQuarantineEnabled = true;
            state.IntegrityQuarantineReasonCode = "OUTBOX_GAP";
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ProjectionStepResult.Quarantined;
        }

        var journal = await scope.FindJournalEntryAsync(organizationId, JournalStreamNames.Authorization, target, cancellationToken).ConfigureAwait(false);
        if (journal is null || journal.ArchiveDeliveryState != ArchiveDeliveryStates.Anchored)
        {
            return ProjectionStepResult.AwaitingArchive; // gate stays closed until the body is independently durable
        }

        var command = await scope.FindCommandAsync(outbox.SecurityCommandId, cancellationToken).ConfigureAwait(false);
        var delta = JsonSerializer.Deserialize<List<RelationshipDeltaEntry>>(outbox.RelationshipDeltaJson, CanonicalJson.Options) ?? [];
        state.ProjectionWorkerFencingEpoch++;
        outbox.ClaimedWorkerFencingEpoch = state.ProjectionWorkerFencingEpoch;
        outbox.DeliveryAttemptCount++;
        outbox.LastDeliveryAttemptOn = clock.GetUtcNow();

        string zedToken;
        try
        {
            if (target == 1)
            {
                await spiceDb.InitializeCursorAsync(organizationId, cancellationToken).ConfigureAwait(false);
            }

            zedToken = await spiceDb.ApplyDeltaAsync(organizationId, state.ProjectionAppliedRevision, target, delta, cancellationToken).ConfigureAwait(false);
        }
        catch (SpiceDbPreconditionFailedException)
        {
            // Ambiguous earlier outcome or a competing writer: never acknowledge without verifying the exact projected state.
            var (cursorStep, cursorToken) = await spiceDb.ReadCursorStepAsync(organizationId, cancellationToken).ConfigureAwait(false);
            if (cursorStep == target && await DeltaIsReflectedAsync(delta, cancellationToken).ConfigureAwait(false))
            {
                zedToken = cursorToken;
                outbox.LastProjectionErrorCode = "RECONCILED_AFTER_AMBIGUOUS_WRITE";
            }
            else
            {
                outbox.LastProjectionErrorCode = AuthorityErrorCodes.ProjectionDivergence;
                outbox.ProjectionDeliveryState = ProjectionDeliveryStates.Failed;
                if (command is not null)
                {
                    command.CommandStateName = SecurityCommandStates.FailedReconciliation;
                    command.CommandModifiedOn = clock.GetUtcNow();
                }

                state.IntegrityQuarantineEnabled = true;
                state.IntegrityQuarantineReasonCode = AuthorityErrorCodes.ProjectionDivergence;
                await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
                return ProjectionStepResult.Quarantined;
            }
        }
        catch (SpiceDbUnavailableException ex)
        {
            outbox.LastProjectionErrorCode = "SPICEDB_UNAVAILABLE";
            await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
            _ = ex;
            return ProjectionStepResult.Unavailable;
        }

        faultInjector.CrashIfArmed(FaultNames.CrashAfterProjectionWrite);

        state.ProjectionAppliedRevision = target;
        state.ProjectionZedToken = zedToken;
        state.StateModifiedOn = clock.GetUtcNow();
        outbox.ProjectionDeliveryState = ProjectionDeliveryStates.Delivered;
        outbox.ProjectionConfirmedZedToken = zedToken;
        outbox.ProjectionAppliedOn = clock.GetUtcNow();
        if (command is not null)
        {
            command.CommandStateName = SecurityCommandStates.Effective;
            command.CommandEffectiveOn = clock.GetUtcNow();
            command.CommandModifiedOn = clock.GetUtcNow();
        }

        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ProjectionStepResult.Applied;
    }

    private async Task<bool> DeltaIsReflectedAsync(IReadOnlyList<RelationshipDeltaEntry> delta, CancellationToken cancellationToken)
    {
        foreach (var entry in delta)
        {
            var exists = await spiceDb.TupleExistsAsync(entry.Tuple, cancellationToken).ConfigureAwait(false);
            var expected = entry.Operation == RelationshipDeltaOperations.Touch;
            if (exists != expected)
            {
                return false;
            }
        }

        return true;
    }
}

public enum ProjectionStepResult
{
    Idle,
    Applied,
    AwaitingArchive,
    Unavailable,
    Quarantined,
}
