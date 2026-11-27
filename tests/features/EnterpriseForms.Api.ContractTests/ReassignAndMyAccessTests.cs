using System.Reflection;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.FormRecords.TransferOwnership;
using EnterpriseForms.Application.Features.Sharing.MyAccess;
using EnterpriseForms.Contracts.Features;
using EnterpriseForms.Domain.Features.Audit;
using EnterpriseForms.Domain.Features.FormDefinitions;
using EnterpriseForms.Domain.Features.FormRecords;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// C10 M2 (WP-0 Sections 3.5 and 3.7, D8-4): reassignment submits one TransferOwnership per Authority-listed record, each under its own
/// idempotency key, reports each outcome (a refusal of one does not hide the others; a pending command is not Effective), and maps an
/// Authority refusal of the listing to 404; "my access" passes the Authority's sources through with form names only.
/// </summary>
public sealed class ReassignAndMyAccessTests
{
    private static readonly Guid Organization = Guid.CreateVersion7();
    private static readonly Guid Member = Guid.CreateVersion7();
    private static readonly Guid NewOwner = Guid.CreateVersion7();
    private static readonly Guid First = Guid.CreateVersion7();
    private static readonly Guid Second = Guid.CreateVersion7();
    private static readonly Guid Form = Guid.CreateVersion7();
    private static readonly RequestContext Context = new(Organization, Guid.CreateVersion7(), "https://issuer.invalid", "subject", "Admin", "assertion", Guid.CreateVersion7());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static T Create<T>(Func<MethodInfo, object?[], object?> handler)
        where T : class
    {
        var proxy = DispatchProxy.Create<T, Forwarder<T>>();
        ((Forwarder<T>)(object)proxy).Handler = handler;
        return proxy;
    }

    private static IShareDetailsAuthority Details(AuthorityClientException? listingRefusal = null) => Create<IShareDetailsAuthority>((method, _) => method.Name switch
    {
        nameof(IShareDetailsAuthority.GetMemberOwnedRecordsAsync) => listingRefusal is null
            ? Task.FromResult(new AuthorityMemberOwnedRecords(Member, [First, Second], false, 7))
            : Task.FromException<AuthorityMemberOwnedRecords>(listingRefusal),
        nameof(IShareDetailsAuthority.GetMyAccessAsync) => Task.FromResult(new AuthorityMyAccess([new(First, "DirectShare", Guid.CreateVersion7(), null, "Reader", null, false)], false, 7, 7)),
        _ => throw new NotSupportedException(method.Name),
    });

    private static IBusinessStore Store() => Create<IBusinessStore>((method, _) => method.Name == nameof(IBusinessStore.BeginScopeAsync)
        ? Task.FromResult(Create<IBusinessScope>((m, args) => m.Name switch
        {
            nameof(IBusinessScope.FindFormRecordAsync) => Task.FromResult<FormRecord?>(new FormRecord { FormRecordId = (Guid)args[1]!, TenantId = Organization, FormDefinitionId = Form }),
            nameof(IBusinessScope.FindFormDefinitionAsync) => Task.FromResult<FormDefinition?>(new FormDefinition { FormDefinitionId = Form, FormDefinitionName = "Inspection" }),
            nameof(IBusinessScope.GetTargetAuditEventsAsync) => Task.FromResult(new List<AuditEvent>()),
            nameof(IBusinessScope.AddAuditEvent) => null,
            nameof(IBusinessScope.CommitAsync) => Task.CompletedTask,
            nameof(IAsyncDisposable.DisposeAsync) => ValueTask.CompletedTask,
            _ => throw new NotSupportedException(m.Name),
        }))
        : throw new NotSupportedException(method.Name));

    private sealed class Commands
    {
        public List<(Guid Record, string Key)> Submitted { get; } = [];

        public IAuthorizationAuthorityClient Proxy(Func<Guid, SecurityCommandStatus?> outcome) => Create<IAuthorizationAuthorityClient>((method, args) =>
        {
            switch (method.Name)
            {
                case nameof(IAuthorizationAuthorityClient.CheckPermissionAsync):
                    return Task.FromResult(new PermissionDecision(false, "DENIED", 7, 7, null, Guid.CreateVersion7()));
                case nameof(IAuthorizationAuthorityClient.SubmitSecurityCommandAsync):
                    var request = (SecurityCommandRequest)args[1]!;
                    var record = request.Resource.ResourceId;
                    Submitted.Add((record, (string)args[2]!));
                    return outcome(record) is { } status
                        ? Task.FromResult(new SecurityCommandResult(Guid.CreateVersion7(), status, 8, 8, "Open", null, Guid.CreateVersion7()))
                        : Task.FromException<SecurityCommandResult>(new AuthorityClientException("FORBIDDEN", 403, false));
                case nameof(IAuthorizationAuthorityClient.GetSecurityCommandStatusAsync):
                    return Task.FromResult(new SecurityCommandResult(Guid.CreateVersion7(), SecurityCommandStatus.CommittedAwaitingProjection, 8, 7, "Open", null, Guid.CreateVersion7()));
                default:
                    throw new NotSupportedException(method.Name);
            }
        });
    }

    private sealed class JumpingClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => now = now.AddMinutes(1);
    }

    private static ReassignRecordsHandler Handler(IAuthorizationAuthorityClient authority, IShareDetailsAuthority details) =>
        new(details, new TransferOwnershipHandler(Store(), authority, new AuthorityCommandAwaiter(authority, new JumpingClock()), TimeProvider.System));

    [Fact]
    public async Task EachRecordIsItsOwnTransferWithItsOwnKeyAndOutcome()
    {
        var commands = new Commands();
        var authority = commands.Proxy(record => record == First ? SecurityCommandStatus.Effective : null); // the second is refused

        var result = await Handler(authority, Details()).ReassignAsync(Context, Member, new ReassignRecordsRequest(NewOwner, null, "Member is leaving", true), "reassign-key-1", Ct);

        Assert.Equal([First, Second], commands.Submitted.Select(s => s.Record));
        Assert.Equal(2, commands.Submitted.Select(s => s.Key).Distinct().Count());
        Assert.Equal(("Effective", true), (result.Records[0].CommandStatus, result.Records[0].Effective));
        Assert.Equal(("Refused", false, "FORBIDDEN"), (result.Records[1].CommandStatus, result.Records[1].Effective, result.Records[1].RefusalCode));
    }

    [Fact]
    public async Task APendingTransferIsNotReportedAsEffective()
    {
        var commands = new Commands();

        var result = await Handler(commands.Proxy(_ => SecurityCommandStatus.CommittedAwaitingProjection), Details()).ReassignAsync(Context, Member, new ReassignRecordsRequest(NewOwner, null, "Member is leaving"), "reassign-key-2", Ct);

        Assert.All(result.Records, r => Assert.False(r.Effective));
    }

    [Fact]
    public async Task InvalidRequestsAndRefusedListingsSubmitNothing()
    {
        var commands = new Commands();
        var authority = commands.Proxy(_ => SecurityCommandStatus.Effective);

        Assert.Equal(422, (await Assert.ThrowsAsync<BusinessException>(() => Handler(authority, Details()).ReassignAsync(Context, Member, new ReassignRecordsRequest(null, null, "x"), "reassign-key-3", Ct))).HttpStatusCode);
        Assert.Equal(404, (await Assert.ThrowsAsync<BusinessException>(() =>
            Handler(authority, Details(new AuthorityClientException("RESOURCE_NOT_REGISTERED", 404, false))).ReassignAsync(Context, Member, new ReassignRecordsRequest(NewOwner, null, "x"), "reassign-key-4", Ct))).HttpStatusCode);
        Assert.Empty(commands.Submitted);
    }

    [Fact]
    public async Task MyAccessPassesTheAuthoritysSourcesThroughWithFormNames()
    {
        var access = await new MyAccessHandlers(Store(), Details()).GetMyAccessAsync(Context, Ct);

        var entry = Assert.Single(access.Entries);
        Assert.Equal((First, "Inspection", "DirectShare", "Reader"), (entry.FormRecordId, entry.FormDefinitionName, entry.AccessSource, entry.RecipientRole));
    }
}
