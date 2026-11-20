using AuthorizationAuthority.Domain.Features.Organizations;

namespace AuthorizationAuthority.Application.Common.Ports;

/// <summary>
/// C8 M4-2 (AC-8.3, contract §5.3 steps 3–4): the two operations an audit-access transaction needs beyond an ordinary
/// <see cref="IAuthorityScope"/>. A scope that does not implement this interface cannot record audit access: the service
/// refuses (fail closed) rather than falling back to a waiting lock or an unbounded transaction.
/// </summary>
public interface IAuditAccessScope
{
    /// <summary>
    /// Bounds this transaction: <c>SET LOCAL statement_timeout</c> and, where the server supports it (PostgreSQL 17 or later),
    /// <c>SET LOCAL transaction_timeout</c>. Callers also apply an application deadline of the same length, so the bound holds
    /// on a server without <c>transaction_timeout</c>.
    /// </summary>
    Task ApplyAuditAccessTimeoutsAsync(int statementTimeoutMilliseconds, int transactionTimeoutMilliseconds, CancellationToken cancellationToken);

    /// <summary>
    /// <c>SELECT … FOR UPDATE NOWAIT</c> on the organization state row. Never queues behind a lock holder: when the row is locked
    /// the attempt reports <see cref="OrganizationLockAttempt.Busy"/> and this scope's transaction is no longer usable (dispose it).
    /// </summary>
    Task<OrganizationLockAttempt> TryLockOrganizationNoWaitAsync(Guid organizationId, CancellationToken cancellationToken);
}

/// <summary>Outcome of a no-wait organization lock: the locked state, or busy, or (both null/false) no such organization.</summary>
public sealed record OrganizationLockAttempt(OrganizationAuthorizationState? State, bool Busy)
{
    public static OrganizationLockAttempt Locked(OrganizationAuthorizationState state) => new(state, false);

    public static readonly OrganizationLockAttempt LockBusy = new(null, true);

    public static readonly OrganizationLockAttempt NotFound = new(null, false);
}
