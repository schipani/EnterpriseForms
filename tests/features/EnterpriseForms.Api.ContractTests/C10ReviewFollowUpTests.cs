using System.Reflection;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.FormRecords.TransferOwnership;
using EnterpriseForms.Application.Features.Sharing.ListShares;
using EnterpriseForms.Application.Features.Sharing.MyAccess;
using EnterpriseForms.Domain.Features.Audit;
using EnterpriseForms.Domain.Features.FormDefinitions;
using EnterpriseForms.Domain.Features.FormRecords;
using EnterpriseForms.Domain.Features.Identity;
using EnterpriseForms.Domain.Features.Sharing;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// C10 final bounded corrections on the business side: F6 (the share list shows the Authority's lifecycle state of a locally Active share,
/// so a cascade-invalidated share is not presented as Active) and N-3 (a Security Administrator's listing of a member's owned records is
/// audited after the Authority allowed it).
/// </summary>
public sealed class C10ReviewFollowUpTests
{
    private static readonly Guid Organization = Guid.CreateVersion7();
    private static readonly Guid Record = Guid.CreateVersion7();
    private static readonly Guid Form = Guid.CreateVersion7();
    private static readonly Guid Owner = Guid.CreateVersion7();
    private static readonly Guid Invalidated = Guid.CreateVersion7();
    private static readonly Guid Live = Guid.CreateVersion7();
    private static readonly Guid Revoked = Guid.CreateVersion7();
    private static readonly RequestContext Context = new(Organization, Owner, "https://issuer.invalid", "subject", "Owner", "assertion", Guid.CreateVersion7());
    private static readonly CancellationToken Ct = CancellationToken.None;

    [Fact]
    public async Task TheShareListShowsTheAuthoritysStateOfALocallyActiveShare()
    {
        var asked = new List<Guid>();
        var shares = Create<IShareDetailsAuthority>((method, args) =>
        {
            if (method.Name != nameof(IShareDetailsAuthority.GetShareDetailsAsync))
            {
                throw new NotSupportedException(method.Name);
            }

            var id = (Guid)args[3]!;
            asked.Add(id);
            return Task.FromResult(new AuthorityShareDetails(id, Record, id == Invalidated ? "Invalidated" : "Active", "User", Guid.CreateVersion7(), "Reader", [], null, null, 1, null, 1, false,
                id == Invalidated ? "UpstreamNarrowed" : null, Owner, 9));
        });
        var authority = Create<IAuthorizationAuthorityClient>((method, _) => method.Name == nameof(IAuthorizationAuthorityClient.CheckPermissionAsync)
            ? Task.FromResult(new PermissionDecision(true, null, 9, 9, null, Guid.CreateVersion7()))
            : throw new NotSupportedException(method.Name));

        var list = await new ListSharesHandler(ShareStore(), authority, shares).HandleAsync(Context, Record, Ct);

        Assert.Equal(new[] { ("Invalidated", Invalidated), ("Active", Live), ("Revoked", Revoked) }, list.Items.Select(i => (i.ShareStatus, i.RecordShareId)));
        Assert.Equal(new[] { Invalidated, Live }, asked); // only locally Active shares are asked about
    }

    [Fact]
    public async Task ListingAMembersOwnedRecordsIsAudited()
    {
        var audited = new List<AuditEvent>();
        var member = Guid.CreateVersion7();
        var details = Create<IShareDetailsAuthority>((method, _) => method.Name == nameof(IShareDetailsAuthority.GetMemberOwnedRecordsAsync)
            ? Task.FromResult(new AuthorityMemberOwnedRecords(member, [Record, Guid.CreateVersion7()], false, 7))
            : throw new NotSupportedException(method.Name));
        var store = Create<IBusinessStore>((method, _) => method.Name == nameof(IBusinessStore.BeginScopeAsync)
            ? Task.FromResult(Create<IBusinessScope>((m, args) => m.Name switch
            {
                nameof(IBusinessScope.AddAuditEvent) => Capture(audited, (AuditEvent)args[0]!),
                nameof(IBusinessScope.CommitAsync) => Task.CompletedTask,
                nameof(IAsyncDisposable.DisposeAsync) => ValueTask.CompletedTask,
                _ => throw new NotSupportedException(m.Name),
            }))
            : throw new NotSupportedException(method.Name));
        var authority = Create<IAuthorizationAuthorityClient>((method, _) => throw new NotSupportedException(method.Name));
        var handler = new ReassignRecordsHandler(details, new TransferOwnershipHandler(store, authority, new AuthorityCommandAwaiter(authority, TimeProvider.System), TimeProvider.System), store, TimeProvider.System);

        var listed = await handler.ListAsync(Context, member, Ct);

        Assert.Equal(2, listed.FormRecordIds.Count);
        var entry = Assert.Single(audited);
        Assert.Equal(AuditActions.MemberOwnedRecordsViewed, entry.EventAction);
        Assert.Contains(member.ToString(), entry.EventMetadataJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"returnedCount\":2", entry.EventMetadataJson, StringComparison.Ordinal);
    }

    private static IBusinessStore ShareStore() => Create<IBusinessStore>((method, _) => method.Name == nameof(IBusinessStore.BeginScopeAsync)
        ? Task.FromResult(Create<IBusinessScope>((m, args) => m.Name switch
        {
            nameof(IBusinessScope.FindFormRecordAsync) => Task.FromResult<FormRecord?>(new FormRecord { FormRecordId = Record, TenantId = Organization, FormDefinitionId = Form }),
            nameof(IBusinessScope.GetSharesOfRecordAsync) => Task.FromResult(new List<RecordShare>
            {
                Share(Invalidated, "Active"), Share(Live, "Active"), Share(Revoked, "Revoked"),
            }),
            nameof(IBusinessScope.GetShareFieldsOfRecordAsync) => Task.FromResult(new List<RecordShareField>()),
            nameof(IBusinessScope.GetFieldDefinitionsAsync) => Task.FromResult(new List<FieldDefinition>()),
            nameof(IBusinessScope.GetUsersAsync) => Task.FromResult(new List<User>()),
            nameof(IAsyncDisposable.DisposeAsync) => ValueTask.CompletedTask,
            _ => throw new NotSupportedException(m.Name),
        }))
        : throw new NotSupportedException(method.Name));

    private static RecordShare Share(Guid id, string status) =>
        new() { RecordShareId = id, TenantId = Organization, FormRecordId = Record, SharedByUserId = Owner, RecipientUserId = Guid.CreateVersion7(), ShareStatus = status };

    private static object? Capture(List<AuditEvent> sink, AuditEvent entry)
    {
        sink.Add(entry);
        return null;
    }

    private static T Create<T>(Func<MethodInfo, object?[], object?> handler)
        where T : class
    {
        var proxy = DispatchProxy.Create<T, Forwarder<T>>();
        ((Forwarder<T>)(object)proxy).Handler = handler;
        return proxy;
    }
}
