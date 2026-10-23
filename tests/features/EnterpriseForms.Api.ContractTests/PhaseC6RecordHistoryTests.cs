using System.Reflection;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.FormRecords.History;
using EnterpriseForms.Domain.Features.Audit;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// Phase C6 M2 business rules of the version list and the version view: which facts are the Authority's, when an action label
/// may be shown and with which source, and that an unlisted or unverifiable version fails before any business row is read.
/// </summary>
public sealed class PhaseC6RecordHistoryTests
{
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly Guid Owner = Guid.CreateVersion7();
    private static readonly RequestContext Context = new(Org, Owner, "https://issuer.invalid", "subject", "Owner", "assertion", Guid.CreateVersion7());
    private static readonly Dictionary<Guid, string> Names = new() { [Owner] = "Avery Owner" };

    private static ContentHistoryEntry Entry(long revision, string actorKind = "User", Guid? actor = null, bool current = false, bool verifiable = true) =>
        new(revision, Guid.CreateVersion7(), DateTimeOffset.UtcNow, actorKind, actorKind == "User" ? actor ?? Owner : null, actorKind == "ProtectionMigration" ? Guid.CreateVersion7() : null, 4, 1, verifiable, current);

    [Fact]
    public void TheFirstVersionAndProtectionMigrationsAreAuthorityLabels()
    {
        var none = new Dictionary<long, (string, Guid?, string)>();
        var created = RecordHistoryHandler.ToItem(Entry(1), Names, none);
        Assert.Equal(("Created", RecordHistoryHandler.ActionSources.Authority), (created.ActionLabel, created.ActionSource));
        Assert.Equal("Avery Owner", created.ActorDisplayName);

        var migration = RecordHistoryHandler.ToItem(Entry(5, "ProtectionMigration"), Names, none);
        Assert.Equal(("Protection update (system)", RecordHistoryHandler.ActionSources.Authority), (migration.ActionLabel, migration.ActionSource));
        Assert.Null(migration.ActorUserId);
        Assert.Equal("System (field protection update)", migration.ActorDisplayName);
    }

    [Fact]
    public void OtherLabelsComeFromTheActivityLogAndAreMarkedAsSuchOnlyWhenTheLoggedActorIsTheAuthoritysActor()
    {
        var labels = new Dictionary<long, (string, Guid?, string)>
        {
            [2] = (AuditActions.RecordUpdated, Owner, "record content revision committed"),
            [3] = (AuditActions.RecordSubmitted, Owner, "status changed"),
            [4] = (AuditActions.RecordUpdated, Guid.CreateVersion7(), "someone else in the local log"),
            [6] = (AuditActions.RecordUpdated, Owner, Application.Features.FormRecords.UpdateFormRecord.UpdateFormRecordHandler.SharedEditAuditDescription),
        };

        Assert.Equal(("Edited", "ActivityLog"), Label(RecordHistoryHandler.ToItem(Entry(2), Names, labels)));
        Assert.Equal(("Submitted", "ActivityLog"), Label(RecordHistoryHandler.ToItem(Entry(3), Names, labels)));
        Assert.Equal((null, "None"), Label(RecordHistoryHandler.ToItem(Entry(4), Names, labels))); // actor mismatch: not shown
        Assert.Equal((null, "None"), Label(RecordHistoryHandler.ToItem(Entry(5), Names, labels))); // nothing logged
        Assert.Equal(("Shared fields edited", "ActivityLog"), Label(RecordHistoryHandler.ToItem(Entry(6), Names, labels)));
        Assert.Equal((null, "None"), Label(RecordHistoryHandler.ToItem(Entry(2, "Unknown"), Names, labels))); // unknown actor: no label, no name
        Assert.Null(RecordHistoryHandler.ToItem(Entry(2, "Unknown"), Names, labels).ActorDisplayName);
    }

    [Fact]
    public void AnActorMissingFromTheLocalDirectoryIsShownAsUnknownUserNotGuessed()
    {
        var item = RecordHistoryHandler.ToItem(Entry(1, actor: Guid.CreateVersion7()), Names, new Dictionary<long, (string, Guid?, string)>());
        Assert.Equal("Unknown user", item.ActorDisplayName);
    }

    [Fact]
    public async Task AVersionTheAuthorityDoesNotListIsNotFoundAndNothingElseIsRead()
    {
        var authority = HistoryAuthority([Entry(1)]);
        var handler = new RecordHistoryHandler(null!, ThrowingStore(), authority, TimeProvider.System);
        foreach (var revision in new long[] { 0, -3, 7 })
        {
            var failure = await Assert.ThrowsAsync<BusinessException>(() => handler.ViewAsync(Context, Guid.CreateVersion7(), revision, CancellationToken.None));
            Assert.Equal(("VERSION_NOT_FOUND", 404), (failure.ErrorCode, failure.HttpStatusCode));
        }
    }

    [Fact]
    public async Task AVersionWhoseKeyIsNoLongerAcceptedFailsClosedBeforeAnyRowIsRead()
    {
        var authority = HistoryAuthority([Entry(1, verifiable: false)]);
        var handler = new RecordHistoryHandler(null!, ThrowingStore(), authority, TimeProvider.System);
        var failure = await Assert.ThrowsAsync<BusinessException>(() => handler.ViewAsync(Context, Guid.CreateVersion7(), 1, CancellationToken.None));
        Assert.Equal(("HISTORIC_VERSION_UNVERIFIABLE", 503), (failure.ErrorCode, failure.HttpStatusCode));
    }

    [Fact]
    public async Task TheAuthoritysRefusalIsPassedOnUnchanged()
    {
        var authority = Stub<IAuthorizationAuthorityClient>((method, _) => method.Name == nameof(IAuthorizationAuthorityClient.GetContentHistoryAsync)
            ? Task.FromException<ContentHistoryPage>(new AuthorityClientException("NOT_FOUND", 404, false))
            : throw new NotSupportedException(method.Name));
        var handler = new RecordHistoryHandler(null!, ThrowingStore(), authority, TimeProvider.System);
        Assert.Equal(404, (await Assert.ThrowsAsync<AuthorityClientException>(() => handler.ListAsync(Context, Guid.CreateVersion7(), null, null, CancellationToken.None))).HttpStatusCode);
        Assert.Equal(404, (await Assert.ThrowsAsync<AuthorityClientException>(() => handler.ViewAsync(Context, Guid.CreateVersion7(), 1, CancellationToken.None))).HttpStatusCode);
        Assert.Equal(422, (await Assert.ThrowsAsync<BusinessException>(() => handler.ListAsync(Context, Guid.CreateVersion7(), -1, null, CancellationToken.None))).HttpStatusCode);
    }

    private static (string?, string) Label(Contracts.Features.RecordVersionItemResponse item) => (item.ActionLabel, item.ActionSource);

    private static IAuthorizationAuthorityClient HistoryAuthority(List<ContentHistoryEntry> entries) =>
        Stub<IAuthorizationAuthorityClient>((method, args) =>
        {
            if (method.Name != nameof(IAuthorizationAuthorityClient.GetContentHistoryAsync))
            {
                throw new NotSupportedException(method.Name);
            }

            var after = (long?)args[3] ?? 0;
            var limit = (int)args[4]!;
            var page = entries.Where(e => e.ContentRevision > after).Take(limit).ToList();
            return Task.FromResult(new ContentHistoryPage(Org, (Guid)args[2]!, entries.Max(e => e.ContentRevision), page, null, 3, Guid.Empty));
        });

    /// <summary>Any business-store use fails the test: guards must decide before rows are read.</summary>
    private static IBusinessStore ThrowingStore() => Stub<IBusinessStore>((method, _) => throw new InvalidOperationException("the business store must not be used: " + method.Name));

    private static T Stub<T>(Func<MethodInfo, object?[], object?> handler)
        where T : class
    {
        var proxy = DispatchProxy.Create<T, Proxy<T>>();
        ((Proxy<T>)(object)proxy).Handler = handler;
        return proxy;
    }

    public class Proxy<T> : DispatchProxy
        where T : class
    {
        public Func<MethodInfo, object?[], object?> Handler { get; set; } = (method, _) => throw new NotSupportedException(method.Name);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args ?? []);
    }
}
