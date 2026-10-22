using EnterpriseForms.Application.Common.Persistence;

namespace EnterpriseForms.Application.Features.FormRecords.Projection;

/// <summary>
/// The business database's local projection of each record's Authority-accepted head
/// (<c>form_records.accepted_content_change_operation_id</c> at <c>form_records.record_revision</c>; revision 0 with no
/// operation = the Authority reported no accepted content). It is an untrusted hint: protected reads, writes and migration
/// post-checks always use the Authority head. Phase C6 M0 (S2, S3): it is repaired whenever an Authority head is observed,
/// and it is never replaced by a guess.
/// </summary>
public static class AcceptedHeadProjection
{
    /// <summary>A staged header (candidate) of a record: its operation and the content revision it was staged for.</summary>
    public sealed record StagedHeader(Guid OperationId, long ContentRevision);

    public enum ResolutionKind
    {
        /// <summary>The projection names an operation corroborated by a staged header at the projected revision.</summary>
        Resolved,

        /// <summary>The projection records that the Authority reported no accepted content (revision 0, no operation).</summary>
        NoAcceptedContent,

        /// <summary>Missing, dangling or uncorroborated projection: the accepted operation is not known locally.</summary>
        Unresolved,
    }

    public readonly record struct Resolution(ResolutionKind Kind, Guid? OperationId);

    /// <summary>
    /// Resolves the record's accepted operation from its local projection. Never falls back to the highest staged revision:
    /// several candidates can share a revision (an abandoned retry, a commit aborted by a revocation or a lost
    /// compare-and-swap), and only the Authority knows which one, if any, was accepted.
    /// </summary>
    public static Resolution Resolve(Guid? projectedOperationId, long projectedRevision, IEnumerable<StagedHeader> stagedHeaders)
    {
        ArgumentNullException.ThrowIfNull(stagedHeaders);
        if (projectedOperationId is null && projectedRevision == 0)
        {
            return new Resolution(ResolutionKind.NoAcceptedContent, null);
        }

        return projectedOperationId is { } pointer && stagedHeaders.Any(h => h.OperationId == pointer && h.ContentRevision == projectedRevision)
            ? new Resolution(ResolutionKind.Resolved, pointer)
            : new Resolution(ResolutionKind.Unresolved, null);
    }

    /// <summary>
    /// Records an Authority head the caller observed directly (a verified read, a commit response, a post-check) in its own
    /// short transaction. Monotonic and atomic (<see cref="IBusinessScope.ProjectAcceptedHeadAsync"/>). A head at revision 0
    /// records "no accepted content". Does not touch the record's modification metadata: observing a head is not a change.
    /// Returns true when the projection changed.
    /// </summary>
    public static async Task<bool> RepairAsync(IBusinessStore store, Guid tenantId, Guid formRecordId, long acceptedContentRevision, Guid? acceptedOperationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var changed = await scope.ProjectAcceptedHeadAsync(tenantId, formRecordId, acceptedContentRevision, acceptedContentRevision == 0 ? null : acceptedOperationId, cancellationToken).ConfigureAwait(false);
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return changed;
    }
}
