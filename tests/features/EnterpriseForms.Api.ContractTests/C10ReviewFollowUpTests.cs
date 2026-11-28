using System.Reflection;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.Documents.Documents;
using EnterpriseForms.Domain.Features.Documents;
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

    [Fact]
    public async Task TheAcceptedCopyOfAVersionNumberIsTheOneTheAuthorityCommittedNotTheNewestStagedCopy()
    {
        // C10 owner decision 2 (re-review defect 1): version 1 is accepted at revision 5 (operation A). An edit that carried it was aborted
        // at revision 6 (operation B) because the owner's replacement (operation C, version 2) committed revision 6 first. The newest copy
        // of version 1 is B's, untied, and must not be chosen; a tie at one revision is settled the same way.
        Guid a = Guid.CreateVersion7(), b = Guid.CreateVersion7(), c = Guid.CreateVersion7(), d = Guid.CreateVersion7();
        var committed = new Dictionary<long, Guid> { [5] = a, [6] = c, [7] = d };
        var asked = new List<long>();
        var authority = Create<IAuthorizationAuthorityClient>((method, args) =>
        {
            if (method.Name != nameof(IAuthorizationAuthorityClient.GetContentHistoryAsync))
            {
                throw new NotSupportedException(method.Name);
            }

            var revision = (long)args[3]! + 1;
            asked.Add(revision);
            return Task.FromResult(new ContentHistoryPage(Organization, Record, 7,
                committed.TryGetValue(revision, out var op) ? [new ContentHistoryEntry(revision, op, DateTimeOffset.UtcNow, "User", null, null, 3, 1, true, revision == 7)] : [], null, 9, Guid.CreateVersion7()));
        });
        FileAttachmentVersion Copy(Guid operation, long revision, int number = 1) =>
            new() { FileAttachmentVersionId = Guid.CreateVersion7(), FormRecordId = Record, VersionNumber = number, ContentChangeOperationId = operation, ContentRevision = revision };

        var accepted = Copy(a, 5);
        var chosen = await DocumentHandlers.AcceptedCopyAsync(authority, Context, Record, [accepted, Copy(b, 6), Copy(Guid.Empty, 0)], [], Ct);
        Assert.Same(accepted, chosen);
        Assert.Equal(new long[] { 6, 5 }, asked);

        var tiedAccepted = Copy(d, 7);
        Assert.Same(tiedAccepted, await DocumentHandlers.AcceptedCopyAsync(authority, Context, Record, [Copy(Guid.CreateVersion7(), 7), tiedAccepted], [], Ct));
        Assert.Null(await DocumentHandlers.AcceptedCopyAsync(authority, Context, Record, [Copy(b, 6)], [], Ct)); // never accepted: no copy
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
