namespace AuthorizationAuthority.Contracts.V1;

/// <summary>
/// C10 M1-WP9 (WP-0 Section 3.5, D10-11): the record-scoped sharing and ownership history, computed from the Authority's committed
/// edges (each records the command that created and the command that removed it). Every command keeps its own state: a command that
/// is not Effective is shown as such and is never presented as completed (A11). Identifiers and codes only, never values or names.
/// </summary>
/// <param name="Items">Newest first (command creation time, then command id).</param>
/// <param name="Truncated">True when only the newest <see cref="RecordSecurityHistoryLimits.MaximumCommands"/> commands are returned.</param>
public sealed record RecordSecurityHistory(Guid FormRecordId, IReadOnlyList<RecordSecurityHistoryItem> Items, bool Truncated, long AuthorizationRevision);

/// <param name="CommandState">Pending, CommittedAwaitingProjection, Effective, Rejected or FailedReconciliation.</param>
/// <param name="ActorUserId">The Authority user the command is recorded under (for ExpireShare: the person whose command set the expiry).</param>
/// <param name="JournalBacked">True when the command's entry exists on the organization's authorization journal stream.</param>
public sealed record RecordSecurityHistoryItem(
    Guid SecurityCommandId,
    string CommandKind,
    string CommandState,
    DateTimeOffset OccurredOn,
    DateTimeOffset? EffectiveOn,
    long? CommittedAuthorizationRevision,
    Guid? ActorUserId,
    IReadOnlyList<RecordSecurityChange> Changes,
    bool JournalBacked = false);

/// <param name="ChangeKind">ShareCreated, ShareScopeChanged, ShareEnded, OwnerAdded or OwnerRemoved.</param>
/// <param name="ShareEndState">For ShareEnded: Revoked, Expired or Invalidated.</param>
/// <param name="InvalidationReasonCode">For an Invalidated share: why (for example UpstreamRevoked, UpstreamNarrowed, GrantorPathLost).</param>
/// <param name="SubjectKind">For owner changes: "User" or "Team".</param>
/// <param name="ShareExpiresOn">For ShareCreated and ShareScopeChanged: the expiry the command committed (null: none).</param>
public sealed record RecordSecurityChange(string ChangeKind, Guid? RecordShareId, string? ShareEndState, string? InvalidationReasonCode, string? SubjectKind, Guid? SubjectId, DateTimeOffset? ShareExpiresOn = null);

public static class RecordSecurityHistoryLimits
{
    public const int MaximumCommands = 500;
}
