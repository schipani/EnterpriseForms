using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using EnterpriseForms.Api.Composition;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.Sharing.GetShare;
using EnterpriseForms.Domain.Features.FormDefinitions;
using EnterpriseForms.Domain.Features.FormRecords;
using EnterpriseForms.Domain.Features.Identity;
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
/// C10 M2 (WP-0 Section 3.5, D10-3): <c>GET /api/v1/form-records/{formRecordId}/shares/{recordShareId}</c>. The ETag carries the
/// Authority's committed share revision (never the local projection's), distinct from the organization revision; an Authority
/// refusal is the same concealing 404 as the share list; a closed gate is never answered with stale details; field keys come only
/// from authoritative items; the response is not cacheable. The Authority and the business store are stubs.
/// </summary>
public sealed class ShareDetailsTests
{
    private static readonly Guid Organization = Guid.CreateVersion7();
    private static readonly Guid LocalUser = Guid.CreateVersion7();
    private static readonly Guid Record = Guid.CreateVersion7();
    private static readonly Guid Share = Guid.CreateVersion7();
    private static readonly Guid Form = Guid.CreateVersion7();
    private static readonly Guid Recipient = Guid.CreateVersion7();
    private static readonly Guid FirstField = Guid.CreateVersion7();
    private static readonly Guid SecondField = Guid.CreateVersion7();
    private static readonly Guid FirstItem = Guid.CreateVersion7();
    private static readonly Guid StaleItem = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AuthorityShareDetails Details(long shareRevision = 5, string state = "Active", Guid? share = null) => new(
        share ?? Share, Record, state, state == "Active" ? "User" : null, state == "Active" ? Recipient : null, state == "Active" ? "Reader" : null,
        state == "Active" ? [FirstItem] : [], null, null, shareRevision, null, 1, false, null, LocalUser, 41);

    [Fact]
    public async Task TheETagIsTheAuthoritysCommittedShareRevision()
    {
        var authority = new FakeAuthority { Answer = Details(shareRevision: 5) };
        using var api = Api(authority, new FakeStore());
        using var client = Authenticated(api);

        var response = await client.GetAsync($"/api/v1/form-records/{Record:D}/shares/{Share:D}", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"" + Share.ToString("D") + ":5\"", response.Headers.ETag!.ToString());
        Assert.True(response.Headers.CacheControl!.NoStore);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal(5, body.GetProperty("shareRevision").GetInt64());
        Assert.Equal(41, body.GetProperty("authorizationRevision").GetInt64());
        Assert.Equal("Reader", body.GetProperty("recipientRole").GetString());
        Assert.Equal(Recipient, body.GetProperty("recipientUserId").GetGuid());
        Assert.Equal("Recipient Name", body.GetProperty("recipientDisplayName").GetString());
        // The local projection also lists a stale item of another field; only authoritative items are named.
        Assert.Equal(["first"], body.GetProperty("fieldKeys").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal([Share], authority.Requested);
    }

    [Fact]
    public async Task AnAuthorityRefusalIsTheConcealing404AndUnauthenticatedCallersAre401()
    {
        var authority = new FakeAuthority { Refusal = new AuthorityClientException("RESOURCE_NOT_REGISTERED", 404, false) };
        var store = new FakeStore();
        using var api = Api(authority, store);
        using (var anonymous = api.CreateClient())
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/v1/form-records/{Record:D}/shares/{Share:D}", Ct)).StatusCode);
        }

        using var client = Authenticated(api);
        var response = await client.GetAsync($"/api/v1/form-records/{Record:D}/shares/{Share:D}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("RECORD_NOT_FOUND", (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("title").GetString());
        Assert.False(response.Headers.Contains("ETag"));
        Assert.Equal(0, store.Scopes); // nothing local is read for a refused caller
    }

    [Fact]
    public async Task AClosedGateIsNeverAnsweredWithDetails()
    {
        var authority = new FakeAuthority { Refusal = new AuthorityClientException("PROJECTION_NOT_READY", 503, true) };
        using var api = Api(authority, new FakeStore());
        using var client = Authenticated(api);

        var response = await client.GetAsync($"/api/v1/form-records/{Record:D}/shares/{Share:D}", Ct);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
        Assert.False(response.Headers.Contains("ETag"));
    }

    [Fact]
    public async Task AnAnswerForAnotherShareIsRefused()
    {
        var handler = new GetShareHandler(new FakeStore().Proxy, new FakeAuthority { Answer = Details(share: Guid.CreateVersion7()) }.Proxy);

        var refused = await Assert.ThrowsAsync<EnterpriseForms.Application.Common.BusinessException>(() => handler.HandleAsync(Context, Record, Share, Ct));

        Assert.Equal(404, refused.HttpStatusCode);
    }

    [Fact]
    public async Task ARevokedShareShowsItsStateWithoutRecipientOrFields()
    {
        var handler = new GetShareHandler(new FakeStore().Proxy, new FakeAuthority { Answer = Details(state: "Revoked") }.Proxy);

        var details = await handler.HandleAsync(Context, Record, Share, Ct);

        Assert.Equal("Revoked", details.ShareStatus);
        Assert.Null(details.RecipientUserId);
        Assert.Null(details.RecipientRole);
        Assert.Empty(details.FieldValueItemIds);
        Assert.Empty(details.FieldKeys);
        Assert.Equal("Owner Name", details.GrantorDisplayName);
    }

    private static readonly EnterpriseForms.Application.Common.RequestContext Context = new(Organization, LocalUser, "https://issuer.invalid", "subject", "Tester", "assertion", Guid.CreateVersion7());

    private static WebApplicationFactory<Program> Api(FakeAuthority authority, FakeStore store) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<GetShareHandler>();
                s.AddSingleton(_ => new GetShareHandler(store.Proxy, authority.Proxy));
            });
        });

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
                ["sub"] = "c10-m2-details-test",
                ["org"] = Organization.ToString("D"),
                ["authority_user_id"] = LocalUser.ToString("D"),
                ["name"] = "C10 M2 details test",
            },
            SigningCredentials = new SigningCredentials(settings.SymmetricKey(), SecurityAlgorithms.HmacSha256),
        });
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private sealed class FakeAuthority
    {
        public FakeAuthority() => Proxy = Create<IShareDetailsAuthority>((method, args) =>
        {
            Requested.Add((Guid)args[3]!);
            return Refusal is { } refusal ? Task.FromException<AuthorityShareDetails>(refusal) : Task.FromResult(Answer!);
        });

        public IShareDetailsAuthority Proxy { get; }

        public AuthorityShareDetails? Answer { get; init; }

        public AuthorityClientException? Refusal { get; init; }

        public List<Guid> Requested { get; } = [];
    }

    private sealed class FakeStore
    {
        public FakeStore() => Proxy = Create<IBusinessStore>((method, _) => method.Name == nameof(IBusinessStore.BeginScopeAsync)
            ? Task.FromResult(NewScope())
            : throw new NotSupportedException(method.Name));

        public IBusinessStore Proxy { get; }

        public int Scopes { get; private set; }

        private IBusinessScope NewScope()
        {
            Scopes++;
            return Create<IBusinessScope>((method, _) => method.Name switch
            {
                nameof(IBusinessScope.GetUsersAsync) => Task.FromResult(new List<User>
                {
                    new() { UserId = Recipient, TenantId = Organization, DisplayName = "Recipient Name" },
                    new() { UserId = LocalUser, TenantId = Organization, DisplayName = "Owner Name" },
                }),
                nameof(IBusinessScope.FindFormRecordAsync) => Task.FromResult<FormRecord?>(new FormRecord { FormRecordId = Record, TenantId = Organization, FormDefinitionId = Form }),
                nameof(IBusinessScope.GetFieldDefinitionsAsync) => Task.FromResult(new List<FieldDefinition>
                {
                    new() { FieldDefinitionId = FirstField, FormDefinitionId = Form, FieldKey = "first" },
                    new() { FieldDefinitionId = SecondField, FormDefinitionId = Form, FieldKey = "second" },
                }),
                nameof(IBusinessScope.GetShareFieldsOfShareAsync) => Task.FromResult(new List<RecordShareField>
                {
                    new() { RecordShareId = Share, FieldDefinitionId = FirstField, FieldValueItemId = FirstItem },
                    new() { RecordShareId = Share, FieldDefinitionId = SecondField, FieldValueItemId = StaleItem },
                }),
                nameof(IAsyncDisposable.DisposeAsync) => ValueTask.CompletedTask,
                _ => throw new NotSupportedException(method.Name),
            });
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
