using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using EnterpriseForms.Api.Composition;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.FormDefinitions.CreateFormDefinition;
using EnterpriseForms.Application.Features.Sharing;
using EnterpriseForms.Application.Features.Sharing.UpdateShare;
using EnterpriseForms.Contracts.Features;
using EnterpriseForms.Domain.Features.Audit;
using EnterpriseForms.Domain.Features.FormDefinitions;
using EnterpriseForms.Domain.Features.FormRecords;
using EnterpriseForms.Domain.Features.Sharing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// C10 M2: <c>PATCH /api/v1/form-records/{formRecordId}/shares/{recordShareId}</c> — the actual route (bearer authentication, strong
/// share-bound <c>If-Match</c> with 428/412, the new <c>ETag</c>), and the handler (the exact <c>UpdateShareScope</c> command and payload,
/// accepted-manifest field binding, 412 for a stale share revision fresh or replayed, 409 for an organization conflict, concealment
/// for callers who cannot manage access, the Authority's own refusals, step-up only through the human-command payload, pending
/// commands, and a projection that changes only after Effective). The Authority and the business store are stubs.
/// </summary>
public sealed class ShareUpdateTests
{
    private static readonly Guid Organization = Guid.CreateVersion7();
    private static readonly Guid LocalUser = Guid.CreateVersion7();
    private static readonly Guid Record = Guid.CreateVersion7();
    private static readonly Guid Share = Guid.CreateVersion7();
    private static readonly Guid Form = Guid.CreateVersion7();
    private static readonly Guid FirstField = Guid.CreateVersion7();
    private static readonly Guid SecondField = Guid.CreateVersion7();
    private static readonly Guid FirstItem = Guid.CreateVersion7();
    private static readonly Guid SecondItem = Guid.CreateVersion7();
    private static readonly Guid EmptyField = Guid.CreateVersion7();
    private static readonly Guid Operation = Guid.CreateVersion7();
    private static readonly RequestContext Context = new(Organization, LocalUser, "https://issuer.invalid", "subject", "Tester", "assertion", Guid.CreateVersion7());
    private const string Key = "share-update-key-1";
    private const string SyntheticMarker = "synthetic-development-step-up";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- the route ----

    [Fact]
    public async Task TheRouteRequiresAuthenticationThenAStrongShareBoundIfMatch()
    {
        var authority = new FakeAuthority();
        var store = new FakeStore();
        using var api = Api(authority, store);
        using (var anonymous = api.CreateClient())
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(Patch(Valid(), "\"" + Share.ToString("D") + ":1\""), Ct)).StatusCode);
        }

        using var client = Authenticated(api);
        var missing = await client.SendAsync(Patch(Valid(), ifMatch: null), Ct);
        Assert.Equal(HttpStatusCode.PreconditionRequired, missing.StatusCode);
        Assert.Equal("PRECONDITION_REQUIRED", (await missing.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("title").GetString());

        foreach (var tag in new[] { "*", "W/\"" + Share.ToString("D") + ":1\"", "\"" + Share.ToString("D") + ":0\"", "\"" + Guid.CreateVersion7().ToString("D") + ":1\"", "\"" + Share.ToString("D") + ":1\", \"" + Share.ToString("D") + ":2\"" })
        {
            var malformed = await client.SendAsync(Patch(Valid(), tag), Ct);
            Assert.Equal(HttpStatusCode.PreconditionFailed, malformed.StatusCode);
            Assert.Equal("PRECONDITION_FAILED", (await malformed.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("title").GetString());
        }

        Assert.Empty(authority.Submitted); // nothing reached the Authority without a valid precondition
    }

    [Fact]
    public async Task AnAcceptedUpdateReturnsTheNewStrongETagAndTheUpdatedShare()
    {
        var authority = new FakeAuthority();
        var store = new FakeStore();
        using var api = Api(authority, store);
        using var client = Authenticated(api);

        var response = await client.SendAsync(Patch(Valid(), "\"" + Share.ToString("D") + ":4\""), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"" + Share.ToString("D") + ":5\"", response.Headers.ETag?.ToString());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(5, body.GetProperty("shareRevision").GetInt64());
        Assert.Equal("Contributor", body.GetProperty("recipientRole").GetString());
        var (request, key) = Assert.Single(authority.Submitted);
        Assert.Equal("share-update:" + Key, key);
        Assert.Equal(4, request.CommandPayload.GetProperty("expected_share_revision").GetInt64());
    }

    // ---- the handler ----

    [Fact]
    public async Task TheCommandIsUpdateShareScopeWithTheManifestBoundItemsAndTheDecisionRevision()
    {
        var authority = new FakeAuthority();
        var store = new FakeStore();
        var expiry = DateTimeOffset.UtcNow.AddDays(10);
        var parent = Guid.CreateVersion7();

        await Handler(authority, store).HandleAsync(Context, Record, Share, 2, new UpdateShareRequest("Reader", ["first", "second"], expiry, null, parent), Key, Ct);

        var (request, _) = Assert.Single(authority.Submitted);
        Assert.Equal(SecurityCommandKind.UpdateShareScope, request.CommandKind);
        Assert.Equal(new ResourceReference(ProtectedResourceKind.FormRecord, Record), request.Resource);
        Assert.Equal(7, request.ExpectedAuthorizationRevision); // the ManageAccess decision's revision is the organization CAS
        var payload = request.CommandPayload;
        Assert.Equal(Share, payload.GetProperty("record_share_id").GetGuid());
        Assert.Equal(Record, payload.GetProperty("form_record_id").GetGuid());
        Assert.Equal("Reader", payload.GetProperty("recipient_role").GetString());
        Assert.Equal(new[] { FirstItem, SecondItem }, payload.GetProperty("field_item_ids").EnumerateArray().Select(i => i.GetGuid()));
        Assert.Equal(2, payload.GetProperty("expected_share_revision").GetInt64());
        Assert.Equal(parent, payload.GetProperty("parent_record_share_id").GetGuid());
        Assert.Equal(expiry.ToUniversalTime(), payload.GetProperty("share_expires_on").GetDateTimeOffset());
        Assert.False(payload.TryGetProperty("step_up_evidence", out _));
        // C10 review F-6: the field keys are resolved only after the caller's full-read check (the owner here, so no scope narrowing).
        Assert.Equal(new[] { ProtectedAction.ManageAccess, ProtectedAction.Read }, authority.Checks);
    }

    [Fact]
    public async Task ADocumentOnlyScopeSendsTheDocumentsWithViewAndNoFieldsAndAnEmptyScopeIsRefused()
    {
        var authority = new FakeAuthority();
        var handler = Handler(authority, new FakeStore());
        var attachment = Guid.CreateVersion7();

        await handler.HandleAsync(Context, Record, Share, 1, new UpdateShareRequest("Reader", [], null, true, Documents: [new ShareDocumentRequest(attachment, CanDownloadDocument: true)]), Key, Ct);

        var payload = Assert.Single(authority.Submitted).Request.CommandPayload;
        Assert.Empty(payload.GetProperty("field_item_ids").EnumerateArray());
        var document = Assert.Single(payload.GetProperty("documents").EnumerateArray());
        Assert.Equal(attachment, document.GetProperty("file_attachment_id").GetGuid());
        Assert.True(document.GetProperty("can_view_document").GetBoolean());
        Assert.True(document.GetProperty("can_download_document").GetBoolean());
        Assert.False(document.GetProperty("can_replace_document").GetBoolean());

        var refused = await Assert.ThrowsAsync<BusinessException>(() => handler.HandleAsync(Context, Record, Share, 1, new UpdateShareRequest("Reader", [], null, true), Key, Ct));
        Assert.Equal(422, refused.HttpStatusCode);
        Assert.Single(authority.Submitted);
    }

    [Fact]
    public async Task NoExpiryOmitsTheExpiryAndAnAbsentDecisionIsRefusedBeforeTheAuthority()
    {
        var authority = new FakeAuthority();
        var handler = Handler(authority, new FakeStore());

        await handler.HandleAsync(Context, Record, Share, 1, new UpdateShareRequest("Reader", ["first"], null, true), Key, Ct);
        Assert.False(Assert.Single(authority.Submitted).Request.CommandPayload.TryGetProperty("share_expires_on", out _));

        foreach (var undecided in new[] { new UpdateShareRequest("Reader", ["first"]), new UpdateShareRequest("Reader", ["first"], DateTimeOffset.UtcNow.AddDays(1), true) })
        {
            var refused = await Assert.ThrowsAsync<BusinessException>(() => handler.HandleAsync(Context, Record, Share, 1, undecided, Key, Ct));
            Assert.Equal(422, refused.HttpStatusCode);
        }

        foreach (var invalid in new[] { new UpdateShareRequest("Owner", ["first"], null, true), new UpdateShareRequest("Reader", [], null, true), new UpdateShareRequest("Reader", [" "], null, true) })
        {
            Assert.Equal(422, (await Assert.ThrowsAsync<BusinessException>(() => handler.HandleAsync(Context, Record, Share, 1, invalid, Key, Ct))).HttpStatusCode);
        }

        Assert.Single(authority.Submitted);
    }

    [Theory]
    [InlineData(412)] // refused fresh
    [InlineData(422)] // the stored refusal replayed under the same key (the awaiter's rejected path)
    public async Task AStaleShareRevisionIsAPreconditionFailureFreshOrReplayed(int authorityStatus)
    {
        var store = new FakeStore();
        var authority = new FakeAuthority { Refusal = new AuthorityClientException(UpdateShareHandler.StaleShareRevision, authorityStatus, false, "expected_share_revision is stale") };

        var refused = await Assert.ThrowsAsync<BusinessException>(() => Handler(authority, store).HandleAsync(Context, Record, Share, 3, Valid(), Key, Ct));

        Assert.Equal(412, refused.HttpStatusCode);
        Assert.Equal("PRECONDITION_FAILED", refused.ErrorCode);
        Assert.Equal(0, store.Commits);
    }

    [Theory]
    [InlineData(409)]
    [InlineData(422)]
    public async Task AnOrganizationRevisionConflictIs409AndNotRetried(int authorityStatus)
    {
        var store = new FakeStore();
        var authority = new FakeAuthority { Refusal = new AuthorityClientException(UpdateShareHandler.StaleAuthorizationRevision, authorityStatus, false, "expected_authorization_revision is stale") };

        var refused = await Assert.ThrowsAsync<BusinessException>(() => Handler(authority, store).HandleAsync(Context, Record, Share, 3, Valid(), Key, Ct));

        Assert.Equal(409, refused.HttpStatusCode);
        Assert.Equal(UpdateShareHandler.StaleAuthorizationRevision, refused.ErrorCode);
        Assert.Single(authority.Submitted); // exactly one submission: no automatic retry under the key
        Assert.Equal(0, store.Commits);
    }

    [Fact]
    public async Task ACallerWhoCannotManageAccessLearnsNothingAndNothingIsSubmitted()
    {
        var authority = new FakeAuthority { ManageAccess = false };
        var store = new FakeStore();

        var refused = await Assert.ThrowsAsync<BusinessException>(() => Handler(authority, store).HandleAsync(Context, Record, Share, 1, Valid(), Key, Ct));

        Assert.Equal(404, refused.HttpStatusCode);
        Assert.Equal("RECORD_NOT_FOUND", refused.ErrorCode);
        Assert.Empty(authority.Submitted);
        Assert.Equal(0, store.Scopes); // not even the record's content was read
    }

    [Fact]
    public async Task TheAuthoritysOwnRefusalsPassThroughAndAnotherRecordsProjectionIsNeverChanged()
    {
        // A share of another record, or one the caller may not update: the Authority's uniform refusal is the answer.
        var forbidden = new FakeAuthority { Refusal = new AuthorityClientException("FORBIDDEN", 403, false, "actor cannot update this share") };
        var store = new FakeStore();
        var refused = await Assert.ThrowsAsync<AuthorityClientException>(() => Handler(forbidden, store).HandleAsync(Context, Record, Share, 1, Valid(), Key, Ct));
        Assert.Equal(403, refused.HttpStatusCode);
        Assert.Equal(0, store.Commits);

        // Even an Effective command never rewrites a projection row of another record.
        var other = new FakeStore { Projection = new RecordShare { RecordShareId = Share, TenantId = Organization, FormRecordId = Guid.CreateVersion7(), ShareStatus = "Active", ShareRevision = 1 } };
        var response = await Handler(new FakeAuthority(), other).HandleAsync(Context, Record, Share, 1, Valid(), Key, Ct);
        Assert.Equal(2, response.ShareRevision);
        Assert.Equal(1, other.Projection.ShareRevision);
        Assert.Equal(0, other.Commits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StepUpTravelsOnlyThroughTheHumanCommandPayloadAndIsNeverStored(bool useDevelopmentStepUp)
    {
        var authority = new FakeAuthority();
        var store = new FakeStore();

        await Handler(authority, store).HandleAsync(Context, Record, Share, 1, Valid() with { UseDevelopmentStepUp = useDevelopmentStepUp }, Key, Ct);

        var payload = Assert.Single(authority.Submitted).Request.CommandPayload;
        Assert.Equal(useDevelopmentStepUp, payload.TryGetProperty("step_up_evidence", out var evidence));
        if (useDevelopmentStepUp)
        {
            Assert.Equal(SyntheticMarker, evidence.GetString());
        }

        var audit = Assert.Single(store.AuditEvents);
        Assert.Equal(AuditActions.RecordAccessGranted, audit.EventAction);
        Assert.DoesNotContain(SyntheticMarker, audit.EventMetadataJson, StringComparison.Ordinal);
        Assert.DoesNotContain("step_up", audit.EventMetadataJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("scope_updated", audit.EventMetadataJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEffectiveUpdateReplacesTheProjectionScopeByDiffAndAdvancesItsRevision()
    {
        var store = new FakeStore();
        var kept = new RecordShareField { RecordShareFieldId = Guid.CreateVersion7(), TenantId = Organization, RecordShareId = Share, FieldDefinitionId = FirstField, FieldValueItemId = FirstItem, CanViewField = true };
        var dropped = new RecordShareField { RecordShareFieldId = Guid.CreateVersion7(), TenantId = Organization, RecordShareId = Share, FieldDefinitionId = Guid.CreateVersion7(), FieldValueItemId = Guid.CreateVersion7(), CanViewField = true };
        store.ExistingFields.AddRange([kept, dropped]);

        var response = await Handler(new FakeAuthority(), store).HandleAsync(Context, Record, Share, 1, new UpdateShareRequest("Contributor", ["first", "second"], null, true), Key, Ct);

        Assert.Equal(2, response.ShareRevision);
        Assert.Equal(2, store.Projection!.ShareRevision);
        Assert.Equal("Contributor", store.Projection.RecipientRole);
        Assert.Equal(new[] { dropped }, store.Removed);
        Assert.Equal(new[] { SecondItem }, store.Added.Select(f => f.FieldValueItemId));
        Assert.True(kept.CanEditField);
        Assert.All(store.Added, f => Assert.True(f.CanEditField));
        Assert.Equal(1, store.Commits);

        // A replay of the same accepted update finds the projection already current and leaves it unchanged.
        await Handler(new FakeAuthority(), store).HandleAsync(Context, Record, Share, 1, new UpdateShareRequest("Contributor", ["first", "second"], null, true), Key, Ct);
        Assert.Equal(1, store.Commits);
    }

    [Fact]
    public async Task ACommittedButNotYetEffectiveUpdateIsPendingAndLeavesTheProjectionUnchanged()
    {
        var authority = new FakeAuthority { Status = SecurityCommandStatus.CommittedAwaitingProjection };
        var store = new FakeStore();
        var handler = new UpdateShareHandler(store.Proxy, authority.Proxy, new AuthorityCommandAwaiter(authority.Proxy, new JumpingClock()), TimeProvider.System);

        var pending = await Assert.ThrowsAsync<PendingCommandException>(() => handler.HandleAsync(Context, Record, Share, 1, Valid(), Key, Ct));

        Assert.False(pending.Outcome.Effective);
        Assert.Equal(nameof(SecurityCommandStatus.CommittedAwaitingProjection), pending.Outcome.CommandStatus);
        Assert.Equal(1, store.Projection!.ShareRevision);
        Assert.Equal(0, store.Commits);
    }

    [Fact]
    public async Task AFieldOutsideTheAcceptedContentIsRefusedBeforeTheAuthority()
    {
        var authority = new FakeAuthority();
        var refused = await Assert.ThrowsAsync<BusinessException>(() =>
            Handler(authority, new FakeStore()).HandleAsync(Context, Record, Share, 1, new UpdateShareRequest("Reader", ["unknown"], null, true), Key, Ct));
        Assert.Equal(422, refused.HttpStatusCode);
        Assert.Empty(authority.Submitted);
    }

    [Fact]
    public async Task ADelegatingManagerGetsOneAnswerForEveryFieldOutsideWhatItCanRead()
    {
        // C10 review F-6: keys are resolved against the values the caller can read itself (one verified read, whatever keys were asked),
        // so a field holding values it cannot read, an empty field and an unknown key are indistinguishable; nothing reaches the Authority.
        var parent = Guid.CreateVersion7();
        var authority = new FakeAuthority { FullRead = false };
        var reads = 0;
        Task<IReadOnlyCollection<Guid>> Readable(CancellationToken _)
        {
            reads++;
            return Task.FromResult<IReadOnlyCollection<Guid>>([FirstItem]);
        }

        Task<ShareFieldItems.Resolved> Resolve(string[] keys, Guid? named = null, Func<CancellationToken, Task<IReadOnlyCollection<Guid>>>? readable = null) =>
            ShareFieldItems.ResolveAsync(new FakeStore().Proxy, authority.Proxy, readable ?? Readable, Context.ToDelegatedUser(), Organization, Record, keys, named ?? parent, Ct);

        var answers = new List<(string Code, int Status, string Message)>();
        foreach (var keys in new[] { new[] { "second" }, ["empty"], ["unknown"], ["first", "second"] })
        {
            var refused = await Assert.ThrowsAsync<BusinessException>(() => Resolve(keys));
            answers.Add((refused.ErrorCode, refused.HttpStatusCode, refused.Message));
        }

        Assert.Equal(4, reads); // the same single read for every request
        Assert.Single(answers.Distinct());
        Assert.Equal((ShareFieldItems.FieldOutOfScope, 403), (answers[0].Code, answers[0].Status));
        var unavailable = await Assert.ThrowsAsync<BusinessException>(() => Resolve(["first"], readable: _ => Task.FromException<IReadOnlyCollection<Guid>>(new BusinessException("NOT_FOUND", 404, "record not found"))));
        Assert.Equal(answers[0], (unavailable.ErrorCode, unavailable.HttpStatusCode, unavailable.Message));

        Assert.Equal(new[] { FirstItem }, (await Resolve(["first"])).Items.Select(i => i.ItemId));

        // Without a parent share or a verified reader, a caller without full read is refused the same way (the handler test host has no reader).
        var withoutReader = await Assert.ThrowsAsync<BusinessException>(() => Handler(authority, new FakeStore()).HandleAsync(Context, Record, Share, 1, new UpdateShareRequest("Reader", ["first"], null, true, parent), Key, Ct));
        Assert.Equal(answers[0], (withoutReader.ErrorCode, withoutReader.HttpStatusCode, withoutReader.Message));
        Assert.Empty(authority.Submitted);
    }

    // ---- fixtures ----

    private static UpdateShareRequest Valid() => new("Contributor", ["first"], null, true);

    private static UpdateShareHandler Handler(FakeAuthority authority, FakeStore store) =>
        new(store.Proxy, authority.Proxy, new AuthorityCommandAwaiter(authority.Proxy, TimeProvider.System), TimeProvider.System);

    private static HttpRequestMessage Patch(UpdateShareRequest body, string? ifMatch)
    {
        var message = new HttpRequestMessage(HttpMethod.Patch, new Uri($"/api/v1/form-records/{Record:D}/shares/{Share:D}", UriKind.Relative))
        {
            Content = JsonContent.Create(body),
        };
        message.Headers.Add("Idempotency-Key", Key);
        if (ifMatch is not null)
        {
            message.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        return message;
    }

    private static WebApplicationFactory<Program> Api(FakeAuthority authority, FakeStore store) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<IAuthorizationAuthorityClient>();
                s.AddSingleton(authority.Proxy);
                s.RemoveAll<UpdateShareHandler>();
                s.AddSingleton(sp => new UpdateShareHandler(store.Proxy, authority.Proxy, new AuthorityCommandAwaiter(authority.Proxy, TimeProvider.System), TimeProvider.System));
            });
        });

    /// <summary>A bearer assertion signed with the Development test-IdP key (the same token the development login issues).</summary>
    private static HttpClient Authenticated(WebApplicationFactory<Program> api)
    {
        var settings = api.Services.GetRequiredService<IOptions<UserAuthenticationOptions>>().Value;
        var now = DateTime.UtcNow;
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = settings.Issuer,
            Audience = settings.Audience,
            IssuedAt = now,
            NotBefore = now.AddSeconds(-5),
            Expires = now.AddMinutes(10),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = "c10-m2-contract-test",
                ["org"] = Organization.ToString("D"),
                ["authority_user_id"] = LocalUser.ToString("D"),
                ["name"] = "C10 M2 contract test",
            },
            SigningCredentials = new SigningCredentials(settings.SymmetricKey(), SecurityAlgorithms.HmacSha256),
        });
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>Each read is a minute later, so the awaiter's budget passes after one look.</summary>
    private sealed class JumpingClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => now = now.AddMinutes(1);
    }

    private sealed class FakeAuthority
    {
        public FakeAuthority() => Proxy = Stub.Create<IAuthorizationAuthorityClient>(Handle);

        public IAuthorizationAuthorityClient Proxy { get; }

        public bool ManageAccess { get; init; } = true;

        /// <summary>C10 review F-6: false makes the caller a delegating Manager (ManageAccess without full read).</summary>
        public bool FullRead { get; init; } = true;

        public AuthorityClientException? Refusal { get; init; }

        public SecurityCommandStatus Status { get; init; } = SecurityCommandStatus.Effective;

        public List<(SecurityCommandRequest Request, string Key)> Submitted { get; } = [];

        public List<ProtectedAction> Checks { get; } = [];

        private object? Handle(MethodInfo method, object?[] args) => method.Name switch
        {
            nameof(IAuthorizationAuthorityClient.CheckPermissionAsync) => Check((PermissionCheckRequest)args[1]!),
            nameof(IAuthorizationAuthorityClient.GetCommittedContentRevisionAsync) =>
                Task.FromResult(new AcceptedContentRevision(Record, 1, Operation, string.Empty, 7, 1, 1, "Anchored", Guid.CreateVersion7())),
            nameof(IAuthorizationAuthorityClient.SubmitSecurityCommandAsync) => Submit((SecurityCommandRequest)args[1]!, (string)args[2]!),
            nameof(IAuthorizationAuthorityClient.GetSecurityCommandStatusAsync) => Task.FromResult(Result()),
            _ => throw new NotSupportedException(method.Name),
        };

        private Task<PermissionDecision> Check(PermissionCheckRequest request)
        {
            Checks.Add(request.RequestedAction);
            var allowed = request.RequestedAction == ProtectedAction.Read ? ManageAccess && FullRead : ManageAccess;
            return Task.FromResult(new PermissionDecision(allowed, allowed ? null : "DENIED", 7, 7, null, Guid.CreateVersion7()));
        }

        private Task<SecurityCommandResult> Submit(SecurityCommandRequest request, string key)
        {
            Submitted.Add((request, key));
            return Refusal is { } refusal ? Task.FromException<SecurityCommandResult>(refusal) : Task.FromResult(Result());
        }

        private SecurityCommandResult Result() => new(CommandId, Status, 8, 8, "Open", null, Guid.CreateVersion7());

        private Guid CommandId { get; } = Guid.CreateVersion7();
    }

    private sealed class FakeStore
    {
        public FakeStore() => Proxy = Stub.Create<IBusinessStore>((method, _) => method.Name == nameof(IBusinessStore.BeginScopeAsync)
            ? Task.FromResult(NewScope())
            : throw new NotSupportedException(method.Name));

        public IBusinessStore Proxy { get; }

        public RecordShare? Projection { get; init; } = new() { RecordShareId = Share, TenantId = Organization, FormRecordId = Record, ShareStatus = "Active", ShareRevision = 1, RecipientRole = "Reader" };

        public List<RecordShareField> ExistingFields { get; } = [];

        public List<RecordShareField> Added { get; } = [];

        public List<RecordShareField> Removed { get; } = [];

        public List<AuditEvent> AuditEvents { get; } = [];

        public int Commits { get; private set; }

        public int Scopes { get; private set; }

        private IBusinessScope NewScope()
        {
            Scopes++;
            return Stub.Create<IBusinessScope>((method, args) => method.Name switch
            {
                nameof(IBusinessScope.FindStagedHeaderAsync) => Task.FromResult<FormRecordRevision?>(new FormRecordRevision { FormRecordId = Record, FormDefinitionId = Form }),
                nameof(IBusinessScope.GetStagedRowsAsync) => Task.FromResult(new List<ISignedContentRow>
                {
                    new FieldStringValue { FormRecordId = Record, FieldDefinitionId = FirstField, FieldValueItemId = FirstItem, StringValue = "a" },
                    new FieldStringValue { FormRecordId = Record, FieldDefinitionId = SecondField, FieldValueItemId = SecondItem, StringValue = "b" },
                }),
                nameof(IBusinessScope.GetFieldDefinitionsAsync) => Task.FromResult(new List<FieldDefinition>
                {
                    new() { FieldDefinitionId = FirstField, FormDefinitionId = Form, FieldKey = "first" },
                    new() { FieldDefinitionId = SecondField, FormDefinitionId = Form, FieldKey = "second" },
                    new() { FieldDefinitionId = EmptyField, FormDefinitionId = Form, FieldKey = "empty" }, // no persisted value
                }),
                nameof(IBusinessScope.FindShareProjectionAsync) => Task.FromResult(Projection),
                nameof(IBusinessScope.GetShareFieldsOfShareAsync) => Task.FromResult(ExistingFields.ToList()),
                nameof(IBusinessScope.AddShareFields) => Capture(Added, args[0]),
                nameof(IBusinessScope.RemoveShareFields) => Capture(Removed, args[0]),
                nameof(IBusinessScope.AddAuditEvent) => AddAudit((AuditEvent)args[0]!),
                nameof(IBusinessScope.CommitAsync) => Commit(),
                nameof(IAsyncDisposable.DisposeAsync) => ValueTask.CompletedTask,
                _ => throw new NotSupportedException(method.Name),
            });
        }

        private static object? Capture(List<RecordShareField> sink, object? fields)
        {
            sink.AddRange((IEnumerable<RecordShareField>)fields!);
            return null;
        }

        private object? AddAudit(AuditEvent auditEvent)
        {
            AuditEvents.Add(auditEvent);
            return null;
        }

        private Task Commit()
        {
            Commits++;
            return Task.CompletedTask;
        }
    }

    private static class Stub
    {
        public static T Create<T>(Func<MethodInfo, object?[], object?> handler)
            where T : class
        {
            var proxy = DispatchProxy.Create<T, Forwarder<T>>();
            ((Forwarder<T>)(object)proxy).Handler = handler;
            return proxy;
        }
    }
}

/// <summary>Dispatches every interface call of a ShareUpdateTests stub to its handler.</summary>
public class Forwarder<T> : DispatchProxy
{
    internal Func<MethodInfo, object?[], object?> Handler { get; set; } = (method, _) => throw new NotSupportedException(method.Name);

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args ?? []);
}
