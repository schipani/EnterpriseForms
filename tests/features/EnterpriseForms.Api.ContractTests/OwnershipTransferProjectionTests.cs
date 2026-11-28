using System.Reflection;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.FormRecords.TransferOwnership;
using EnterpriseForms.Contracts.Features;
using EnterpriseForms.Domain.Features.Audit;
using EnterpriseForms.Domain.Features.FormRecords;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// C10 M1-WP11 (WP-0 Section 3.7): an Effective ownership transfer is projected once as <c>RECORD_OWNERSHIP_TRANSFERRED</c>, keyed by
/// its security command id; an idempotent replay of the same command adds nothing; a command that is not yet Effective changes
/// neither the owner projection nor the audit timeline.
/// </summary>
public sealed class OwnershipTransferProjectionTests
{
    private static readonly Guid Organization = Guid.CreateVersion7();
    private static readonly Guid Record = Guid.CreateVersion7();
    private static readonly Guid NewOwner = Guid.CreateVersion7();
    private static readonly RequestContext Context = new(Organization, Guid.CreateVersion7(), "https://issuer.invalid", "subject", "Tester", "assertion", Guid.CreateVersion7());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TransferOwnershipRequest Request() => new(NewOwner, null, "Owner is leaving the team", UseDevelopmentStepUp: true);

    [Fact]
    public async Task AnEffectiveTransferIsProjectedOnceKeyedByItsCommand()
    {
        var authority = new FakeAuthority(SecurityCommandStatus.Effective);
        var store = new FakeStore();
        var handler = new TransferOwnershipHandler(store.Proxy, authority.Proxy, new AuthorityCommandAwaiter(authority.Proxy, TimeProvider.System), TimeProvider.System);

        await handler.HandleAsync(Context, Record, Request(), "transfer-key-1", Ct);
        await handler.HandleAsync(Context, Record, Request(), "transfer-key-1", Ct); // replay: the Authority returns the stored command

        var projected = Assert.Single(store.Events);
        Assert.Equal(AuditActions.RecordOwnershipTransferred, projected.EventAction);
        Assert.Equal(TransferOwnershipHandler.SecurityCommandTarget, projected.TargetEntityType);
        Assert.Equal(authority.CommandId, projected.TargetEntityId);
        Assert.Equal(Record, projected.FormRecordId);
        Assert.Equal(NewOwner, store.Record.OwnerUserId);
    }

    [Fact]
    public async Task APendingTransferIsNotProjected()
    {
        var authority = new FakeAuthority(SecurityCommandStatus.CommittedAwaitingProjection);
        var store = new FakeStore();
        var handler = new TransferOwnershipHandler(store.Proxy, authority.Proxy, new AuthorityCommandAwaiter(authority.Proxy, new JumpingClock()), TimeProvider.System);

        var response = await handler.HandleAsync(Context, Record, Request(), "transfer-key-2", Ct);

        Assert.False(response.Command.Effective);
        Assert.Empty(store.Events);
        Assert.Null(store.Record.OwnerUserId);
    }

    /// <summary>Each read is a minute later, so the awaiter's budget passes after one look.</summary>
    private sealed class JumpingClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => now = now.AddMinutes(1);
    }

    private sealed class FakeAuthority
    {
        private readonly SecurityCommandStatus status;

        public FakeAuthority(SecurityCommandStatus status)
        {
            this.status = status;
            Proxy = Create<IAuthorizationAuthorityClient>((method, _) => method.Name switch
            {
                nameof(IAuthorizationAuthorityClient.CheckPermissionAsync) => Task.FromResult(new PermissionDecision(true, null, 7, 7, null, Guid.CreateVersion7())),
                nameof(IAuthorizationAuthorityClient.SubmitSecurityCommandAsync) => Task.FromResult(Result()),
                nameof(IAuthorizationAuthorityClient.GetSecurityCommandStatusAsync) => Task.FromResult(Result()),
                _ => throw new NotSupportedException(method.Name),
            });
        }

        public IAuthorizationAuthorityClient Proxy { get; }

        public Guid CommandId { get; } = Guid.CreateVersion7();

        private SecurityCommandResult Result() => new(CommandId, status, 8, status == SecurityCommandStatus.Effective ? 8 : 7, "Open", null, Guid.CreateVersion7());
    }

    private sealed class FakeStore
    {
        public FakeStore() => Proxy = Create<IBusinessStore>((method, _) => method.Name == nameof(IBusinessStore.BeginScopeAsync)
            ? Task.FromResult(Create<IBusinessScope>((m, args) => m.Name switch
            {
                nameof(IBusinessScope.FindFormRecordAsync) => Task.FromResult<FormRecord?>(Record),
                nameof(IBusinessScope.GetTargetAuditEventsAsync) => Task.FromResult(Events.Where(e => e.TargetEntityType == (string)args[1]! && e.TargetEntityId == (Guid)args[2]!).ToList()),
                nameof(IBusinessScope.AddAuditEvent) => Add((AuditEvent)args[0]!),
                nameof(IBusinessScope.CommitAsync) => Task.CompletedTask,
                nameof(IAsyncDisposable.DisposeAsync) => ValueTask.CompletedTask,
                _ => throw new NotSupportedException(m.Name),
            }))
            : throw new NotSupportedException(method.Name));

        public IBusinessStore Proxy { get; }

        public FormRecord Record { get; } = new() { FormRecordId = OwnershipTransferProjectionTests.Record, TenantId = Organization };

        public List<AuditEvent> Events { get; } = [];

        private object? Add(AuditEvent auditEvent)
        {
            Events.Add(auditEvent);
            return null;
        }
    }

    private static T Create<T>(Func<MethodInfo, object?[], object?> handler)
        where T : class
    {
        var proxy = System.Reflection.DispatchProxy.Create<T, Forwarder<T>>();
        ((Forwarder<T>)(object)proxy).Handler = handler;
        return proxy;
    }
}
