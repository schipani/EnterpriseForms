using System.Reflection;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.Sharing.ShareHistory;
using EnterpriseForms.Domain.Features.Audit;
using EnterpriseForms.Domain.Features.Identity;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// C10 M2 (WP-0 Section 3.5): the business sharing and ownership histories pass the Authority's command states through unchanged
/// (pending is never shown as Effective), keep only owner changes for the ownership history, add display names only, and turn an
/// Authority refusal into the concealing 404 without reading local data.
/// </summary>
public sealed class RecordSecurityHistoryHandlerTests
{
    private static readonly Guid Organization = Guid.CreateVersion7();
    private static readonly Guid Record = Guid.CreateVersion7();
    private static readonly Guid Owner = Guid.CreateVersion7();
    private static readonly Guid NewOwner = Guid.CreateVersion7();
    private static readonly Guid Share = Guid.CreateVersion7();
    private static readonly RequestContext Context = new(Organization, Owner, "https://issuer.invalid", "subject", "Tester", "assertion", Guid.CreateVersion7());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AuthorityRecordSecurityHistory History() => new(Record,
    [
        new(Guid.CreateVersion7(), "TransferOwnership", "CommittedAwaitingProjection", DateTimeOffset.UtcNow, null, 9, Owner,
            [new("OwnerRemoved", null, null, null, "User", Owner), new("OwnerAdded", null, null, null, "User", NewOwner)]),
        new(Guid.CreateVersion7(), "RevokeShare", "Effective", DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow, 8, Owner,
            [new("ShareEnded", Share, "Revoked", null, null, null)]),
    ], false, 9);

    [Fact]
    public async Task StatesPassThroughAndTheOwnershipHistoryKeepsOnlyOwnerChanges()
    {
        var store = new FakeStore();
        var handler = new RecordSecurityHistoryHandler(store.Proxy, Authority(History()));

        var all = await handler.HandleAsync(Context, Record, ownershipOnly: false, Ct);
        var ownership = await handler.HandleAsync(Context, Record, ownershipOnly: true, Ct);

        Assert.Equal(["CommittedAwaitingProjection", "Effective"], all.Items.Select(i => i.CommandState));
        Assert.Equal("Old Owner", all.Items[0].ActorDisplayName);
        Assert.Equal("New Owner", all.Items[0].Changes.Single(c => c.ChangeKind == "OwnerAdded").SubjectDisplayName);
        var transfer = Assert.Single(ownership.Items);
        Assert.Equal("TransferOwnership", transfer.CommandKind);
        Assert.Equal("CommittedAwaitingProjection", transfer.CommandState);
        Assert.All(transfer.Changes, c => Assert.StartsWith("Owner", c.ChangeKind, StringComparison.Ordinal));

        // C10 review N-3: the ownership-history read is audited (identifiers and a count only); the sharing-history read is not.
        var audited = Assert.Single(store.Audited);
        Assert.Equal((AuditActions.RecordOwnershipHistoryViewed, (Guid?)Record), (audited.EventAction, audited.FormRecordId));
        Assert.Contains("\"returnedCount\":1", audited.EventMetadataJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAuthorityRefusalIsTheConcealing404()
    {
        var store = new FakeStore();
        var handler = new RecordSecurityHistoryHandler(store.Proxy, Authority(refusal: new AuthorityClientException("RESOURCE_NOT_REGISTERED", 404, false)));

        var refused = await Assert.ThrowsAsync<BusinessException>(() => handler.HandleAsync(Context, Record, ownershipOnly: false, Ct));

        Assert.Equal((404, "RECORD_NOT_FOUND"), (refused.HttpStatusCode, refused.ErrorCode));
        Assert.Equal(0, store.Scopes);
    }

    [Fact]
    public async Task AHistoryForAnotherRecordIsRefused()
    {
        var handler = new RecordSecurityHistoryHandler(new FakeStore().Proxy, Authority(History() with { FormRecordId = Guid.CreateVersion7() }));

        Assert.Equal(404, (await Assert.ThrowsAsync<BusinessException>(() => handler.HandleAsync(Context, Record, ownershipOnly: false, Ct))).HttpStatusCode);
    }

    [Fact]
    public async Task EachHistoryAsksTheAuthorityForItsOwnView()
    {
        // C10 independent review F-4: the Authority decides the readers per view (sharing: owner or ManageAccess; ownership: owner or Security Auditor).
        var views = new List<bool>();
        var authority = Create<IShareDetailsAuthority>((method, args) =>
        {
            views.Add((bool)args![3]!);
            return Task.FromResult(History());
        });
        var handler = new RecordSecurityHistoryHandler(new FakeStore().Proxy, authority);

        await handler.HandleAsync(Context, Record, ownershipOnly: false, Ct);
        await handler.HandleAsync(Context, Record, ownershipOnly: true, Ct);

        Assert.Equal([false, true], views);
    }

    private static IShareDetailsAuthority Authority(AuthorityRecordSecurityHistory? answer = null, AuthorityClientException? refusal = null) =>
        Create<IShareDetailsAuthority>((method, _) => method.Name == nameof(IShareDetailsAuthority.GetRecordSecurityHistoryAsync)
            ? refusal is null ? Task.FromResult(answer!) : Task.FromException<AuthorityRecordSecurityHistory>(refusal)
            : throw new NotSupportedException(method.Name));

    private sealed class FakeStore
    {
        public FakeStore() => Proxy = Create<IBusinessStore>((method, _) =>
        {
            if (method.Name != nameof(IBusinessStore.BeginScopeAsync))
            {
                throw new NotSupportedException(method.Name);
            }

            Scopes++;
            return Task.FromResult(Create<IBusinessScope>((m, _) => m.Name switch
            {
                nameof(IBusinessScope.GetUsersAsync) => Task.FromResult(new List<User>
                {
                    new() { UserId = Owner, TenantId = Organization, DisplayName = "Old Owner" },
                    new() { UserId = NewOwner, TenantId = Organization, DisplayName = "New Owner" },
                }),
                nameof(IAsyncDisposable.DisposeAsync) => ValueTask.CompletedTask,
                nameof(IBusinessScope.AddAuditEvent) => Record((AuditEvent)_![0]!),
                nameof(IBusinessScope.CommitAsync) => Task.CompletedTask,
                _ => throw new NotSupportedException(m.Name),
            }));
        });

        public IBusinessStore Proxy { get; }

        public int Scopes { get; private set; }

        /// <summary>C10 review N-3: audit rows the handler recorded.</summary>
        public List<AuditEvent> Audited { get; } = [];

        private object? Record(AuditEvent audit)
        {
            Audited.Add(audit);
            return null;
        }
    }

    private static T Create<T>(Func<MethodInfo, object?[], object?> handler)
        where T : class
    {
        var proxy = DispatchProxy.Create<T, Forwarder<T>>();
        ((Forwarder<T>)(object)proxy).Handler = handler;
        return proxy;
    }
}
