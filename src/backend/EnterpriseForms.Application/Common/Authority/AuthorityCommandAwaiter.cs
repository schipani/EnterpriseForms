namespace EnterpriseForms.Application.Common.Authority;

/// <summary>
/// Waits a bounded time for a submitted command to become Effective (projection applied and archive receipt recorded).
/// Pending/CommittedAwaitingProjection/timeout is never treated as success (invariant A11); the caller surfaces the
/// command id so the client can poll. Rejected/FailedReconciliation surface as errors.
/// </summary>
public sealed class AuthorityCommandAwaiter(IAuthorizationAuthorityClient authority, TimeProvider clock)
{
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(15);

    public IAuthorizationAuthorityClient Authority => authority;

    public async Task<SecurityCommandResult> SubmitAndAwaitAsync(DelegatedUserContext user, SecurityCommandRequest request, string idempotencyKey, CancellationToken cancellationToken, TimeSpan? budget = null)
    {
        var result = await authority.SubmitSecurityCommandAsync(user, request, idempotencyKey, cancellationToken).ConfigureAwait(false);
        return await AwaitAsync(user, result, cancellationToken, budget).ConfigureAwait(false);
    }

    public async Task<SecurityCommandResult> AwaitAsync(DelegatedUserContext user, SecurityCommandResult result, CancellationToken cancellationToken, TimeSpan? budget = null)
    {
        var deadline = clock.GetUtcNow().Add(budget ?? DefaultBudget);
        var delay = TimeSpan.FromMilliseconds(60);
        while (true)
        {
            switch (result.CommandStatus)
            {
                case SecurityCommandStatus.Effective:
                    return result;
                case SecurityCommandStatus.Rejected:
                    throw new AuthorityClientException(result.RejectionReasonCode ?? "COMMAND_REJECTED", 422, false, "security command rejected: " + result.RejectionReasonCode);
                case SecurityCommandStatus.FailedReconciliation:
                    throw new AuthorityClientException("PROJECTION_DIVERGENCE", 503, false, "security command failed reconciliation; operator intervention required");
            }

            if (clock.GetUtcNow() >= deadline)
            {
                return result; // committed but not effective within budget: caller reports pending, never success
            }

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            delay = TimeSpan.FromMilliseconds(Math.Min(500, delay.TotalMilliseconds * 1.5));
            result = await authority.GetSecurityCommandStatusAsync(user, result.CommandId, cancellationToken).ConfigureAwait(false);
        }
    }
}
