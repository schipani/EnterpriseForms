using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EnterpriseForms.Api.Composition;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Features.Search.FieldSearch;
using EnterpriseForms.Contracts.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// Phase C7 M0 (C-56): Advanced Field Search contracts, request validation and the server-side gate. Trusted execution
/// (Authority AC-5/AC-6) does not exist, so no request may ever produce results — and none may produce an empty 200 that
/// reads as "no records match". These tests run without the Authority or a database on purpose: the gate must not need them.
/// </summary>
public sealed class PhaseC7FieldSearchTests
{
    /// <summary>A predicate value that must never appear in a response, a log line or a URL.</summary>
    private const string Sentinel = "Zq7SentinelValueX";

    private static readonly RequestContext Context = new(Guid.CreateVersion7(), Guid.CreateVersion7(), "https://issuer.invalid", "subject", "Tester", "assertion", Guid.CreateVersion7());
    private static readonly Guid Form = Guid.CreateVersion7();
    private static readonly Guid Field = Guid.CreateVersion7();
    private static readonly FieldSearchLimits Limits = new();

    private static FieldSearchCondition Text(string op = "equals", string value = Sentinel) => new(Field, op, "String", value);

    private static FieldSearchRequest Request(params FieldSearchCondition[] conditions) => new(Form, conditions);

    private static SearchException Refusal(FieldSearchRequest? request) => Assert.Throws<SearchException>(() => new FieldSearchHandler(Limits).Search(Context, request));

    // ---- the gate ----

    public static TheoryData<FieldSearchCondition> WellFormedConditions => new()
    {
        new FieldSearchCondition(Field, "equals", "String", "Jordan"),
        new FieldSearchCondition(Field, "startsWith", "String", "Jor"),
        new FieldSearchCondition(Field, "contains", "String", "ord"),
        new FieldSearchCondition(Field, "greaterThanOrEqual", "Integer", "-42"),
        new FieldSearchCondition(Field, "between", "Decimal", null, "1.50", "99.75"),
        new FieldSearchCondition(Field, "before", "Date", "2026-10-23"),
        new FieldSearchCondition(Field, "between", "Date", null, "2026-01-01", "2026-12-31"),
        new FieldSearchCondition(Field, "after", "DateTime", "2026-10-23T14:30:00Z"),
        new FieldSearchCondition(Field, "between", "DateTime", null, "2026-10-23T09:00:00-04:00", "2026-10-23T17:00:00-04:00"),
        new FieldSearchCondition(Field, "isTrue", "Boolean"),
        new FieldSearchCondition(Field, "is", "Option", "0199a2b3-c4d5-7e6f-8123-456789abcdf1"),
        new FieldSearchCondition(Field, "includes", "Option", "0199a2b3-c4d5-7e6f-8123-456789abcdf2"),
    };

    [Theory]
    [MemberData(nameof(WellFormedConditions))]
    public void EveryWellFormedSearchIsRefusedAsUnavailableNeverAnsweredEmpty(FieldSearchCondition condition)
    {
        var refusal = Refusal(Request(condition));
        Assert.Equal(SearchErrorCodes.VerificationUnavailable, refusal.ErrorCode);
        Assert.Equal(503, refusal.HttpStatusCode);
        Assert.Equal(SearchUnavailableReasons.TrustedSearchNotEnabled, refusal.UnavailableReason);
        Assert.False(refusal.Retryable);
    }

    [Fact]
    public void FiveConditionsWithMetadataFiltersAreWellFormedButStillRefused()
    {
        var request = new FieldSearchRequest(Form, [Text(), Text("startsWith", "Jor"), new(Field, "lessThan", "Integer", "10"), new(Field, "isFalse", "Boolean"), new(Field, "after", "Date", "2026-01-01")],
            "Submitted", IncludeArchived: true, IncludeChildren: true, SortBy: "createdOn", SortDirection: "asc", PageSize: 50);
        Assert.Equal(SearchErrorCodes.VerificationUnavailable, Refusal(request).ErrorCode);
    }

    [Fact]
    public void APageRequestIsRefusedTheSameWayBecauseNoVerifiedResultCanExist()
    {
        var refusal = Assert.Throws<SearchException>(() => FieldSearchHandler.Page(Context, new FieldSearchPageRequest("opaque-cursor")));
        Assert.Equal(SearchErrorCodes.VerificationUnavailable, refusal.ErrorCode);
        Assert.Equal(SearchErrorCodes.RequestInvalid, Assert.Throws<SearchException>(() => FieldSearchHandler.Page(Context, new FieldSearchPageRequest(" "))).ErrorCode);
    }

    [Fact]
    public void CapabilitiesOfferNoFieldsAndSayWhyForAnyForm()
    {
        var handler = new FieldSearchHandler(Limits);
        foreach (var form in new[] { Form, Guid.CreateVersion7() })
        {
            var capabilities = handler.Capabilities(Context, form);
            Assert.Equal(form, capabilities.FormDefinitionId);
            Assert.False(capabilities.FieldValueSearchAvailable);
            Assert.Equal(SearchUnavailableReasons.TrustedSearchNotEnabled, capabilities.UnavailableReasonCode);
            Assert.Empty(capabilities.Fields);
            Assert.Empty(capabilities.NotSearchableFields);
            Assert.Null(capabilities.PolicyRevision);
            Assert.Equal(5, capabilities.MaximumConditions);
            Assert.Equal("And", capabilities.ConditionJoin);
        }

        Assert.Equal(SearchErrorCodes.RequestInvalid, Assert.Throws<SearchException>(() => handler.Capabilities(Context, Guid.Empty)).ErrorCode);
    }

    [Fact]
    public void TheHandlerHasNoDependencyThatCouldReadBusinessRecords()
    {
        var parameters = typeof(FieldSearchHandler).GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToList();
        Assert.Equal([typeof(FieldSearchLimits)], parameters);
    }

    // ---- request validation (422, never echoing values) ----

    [Fact]
    public void MoreThanFiveConditionsIsRejected()
    {
        var refusal = Refusal(Request([.. Enumerable.Range(0, 6).Select(_ => Text())]));
        Assert.Equal(SearchErrorCodes.RequestInvalid, refusal.ErrorCode);
        Assert.Equal(422, refusal.HttpStatusCode);
        Assert.Contains("at most 5", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AConfiguredLowerConditionLimitIsEnforcedAndTheApprovedCeilingCannotBeRaised()
    {
        var limits = new FieldSearchLimits { MaximumConditions = 2 };
        limits.Validate();
        Assert.Equal(SearchErrorCodes.RequestInvalid, Assert.Throws<SearchException>(() => new FieldSearchHandler(limits).Search(Context, Request(Text(), Text(), Text()))).ErrorCode);
        Assert.Throws<InvalidOperationException>(() => new FieldSearchLimits { MaximumConditions = 6 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new FieldSearchLimits { MaximumConditions = 0 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new FieldSearchLimits { MaximumEligibleRecords = 0 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new FieldSearchLimits { MinimumPartialTextLength = 300 }.Validate());
        new FieldSearchLimits().Validate();
    }

    public static TheoryData<FieldSearchRequest?, string> InvalidRequests => new()
    {
        { null, SearchErrorCodes.RequestInvalid },
        { new FieldSearchRequest(Guid.Empty, [Text()]), SearchErrorCodes.RequestInvalid },
        { new FieldSearchRequest(Form, null), SearchErrorCodes.RequestInvalid },
        { new FieldSearchRequest(Form, []), SearchErrorCodes.RequestInvalid },
        { new FieldSearchRequest(Form, [new FieldSearchCondition(Guid.Empty, "equals", "String", Sentinel)]), SearchErrorCodes.RequestInvalid },
        { new FieldSearchRequest(Form, [new FieldSearchCondition(Field, "regex", "String", Sentinel)]), SearchErrorCodes.OperatorNotSupported },
        { new FieldSearchRequest(Form, [new FieldSearchCondition(Field, "semantic", "String", Sentinel)]), SearchErrorCodes.OperatorNotSupported },
        { new FieldSearchRequest(Form, [new FieldSearchCondition(Field, "contains", "Integer", "12345")]), SearchErrorCodes.OperatorNotSupported },
        { new FieldSearchRequest(Form, [new FieldSearchCondition(Field, "equals", "DateTime", "2026-10-23T10:00:00Z")]), SearchErrorCodes.OperatorNotSupported },
        { new FieldSearchRequest(Form, [new FieldSearchCondition(Field, "equals", "Ciphertext", Sentinel)]), SearchErrorCodes.RequestInvalid },
        { new FieldSearchRequest(Form, [new FieldSearchCondition(Field, "equals", "Integer", "1,234")]), SearchErrorCodes.RequestInvalid },
        { new FieldSearchRequest(Form, [new FieldSearchCondition(Field, "equals", "Decimal", "1.5e3")]), SearchErrorCodes.RequestInvalid },
        { new FieldSearchRequest(Form, [new FieldSearchCondition(Field, "equals", "Date", "2026-13-01")]), SearchErrorCodes.RequestInvalid },
        { new FieldSearchRequest(Form, [new FieldSearchCondition(Field, "after", "DateTime", "2026-10-23T10:00:00")]), SearchErrorCodes.RequestInvalid },
        { new FieldSearchRequest(Form, [new FieldSearchCondition(Field, "is", "Option", "active")]), SearchErrorCodes.RequestInvalid },
        { new FieldSearchRequest(Form, [new FieldSearchCondition(Field, "equals", "String", new string('x', 201))]), SearchErrorCodes.RequestInvalid },
        { new FieldSearchRequest(Form, [new FieldSearchCondition(Field, "equals", "String", "   ")]), SearchErrorCodes.RequestInvalid },
        { new FieldSearchRequest(Form, [new FieldSearchCondition(Field, "contains", "String", "ab")]), SearchErrorCodes.RequestInvalid },
        { new FieldSearchRequest(Form, [new FieldSearchCondition(Field, "equals", "String", null)]), SearchErrorCodes.RequestInvalid },
        { new FieldSearchRequest(Form, [new FieldSearchCondition(Field, "equals", "String", Sentinel, "a", "b")]), SearchErrorCodes.RequestInvalid },
        { new FieldSearchRequest(Form, [new FieldSearchCondition(Field, "between", "Integer", null, "10", null)]), SearchErrorCodes.RequestInvalid },
        { new FieldSearchRequest(Form, [new FieldSearchCondition(Field, "between", "Integer", null, "10", "5")]), SearchErrorCodes.RequestInvalid },
        { new FieldSearchRequest(Form, [new FieldSearchCondition(Field, "isTrue", "Boolean", "true")]), SearchErrorCodes.RequestInvalid },
        { new FieldSearchRequest(Form, [Text()], RecordStatus: "Deleted"), SearchErrorCodes.RequestInvalid },
        { new FieldSearchRequest(Form, [Text()], SortBy: Field.ToString("D")), SearchErrorCodes.RequestInvalid },
        { new FieldSearchRequest(Form, [Text()], SortDirection: "sideways"), SearchErrorCodes.RequestInvalid },
        { new FieldSearchRequest(Form, [Text()], PageSize: 1000), SearchErrorCodes.RequestInvalid },
    };

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public void MalformedRequestsAreRejectedWithATypedCodeAndNeverEchoTheValue(FieldSearchRequest? request, string expectedCode)
    {
        var refusal = Refusal(request);
        Assert.Equal(expectedCode, refusal.ErrorCode);
        Assert.Equal(422, refusal.HttpStatusCode);
        Assert.DoesNotContain(Sentinel, refusal.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Field.ToString("D"), refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnInvalidSecondConditionIsIdentifiedByPositionOnly()
    {
        var refusal = Refusal(Request(Text(), new FieldSearchCondition(Field, "equals", "Integer", Sentinel)));
        Assert.Equal("condition 2: the value is not a valid integer", refusal.Message);
    }

    // ---- over HTTP: authentication, status mapping, headers, no leakage into responses or logs ----

    [Fact]
    public async Task UnauthenticatedCallersAreRejectedBeforeAnythingElse()
    {
        using var api = Api(new CapturingLoggerProvider());
        using var client = api.CreateClient();
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(new Uri($"/api/v1/form-records/search-capabilities?formDefinitionId={Form:D}", UriKind.Relative), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(new Uri("/api/v1/form-records/field-search", UriKind.Relative), Request(Text()), ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(new Uri("/api/v1/form-records/field-search/page", UriKind.Relative), new FieldSearchPageRequest("x"), ct)).StatusCode);
    }

    [Fact]
    public async Task OverHttpTheGateAndTheContractErrorsAreTypedNoStoreAndLeakNoValue()
    {
        var logs = new CapturingLoggerProvider();
        using var api = Api(logs);
        using var client = Authenticated(api);
        var ct = TestContext.Current.CancellationToken;

        var capabilities = await client.GetAsync(new Uri($"/api/v1/form-records/search-capabilities?formDefinitionId={Form:D}", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.OK, capabilities.StatusCode);
        Assert.True(capabilities.Headers.CacheControl?.NoStore);
        var body = await capabilities.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.False(body.GetProperty("fieldValueSearchAvailable").GetBoolean());
        Assert.Equal("TRUSTED_SEARCH_NOT_ENABLED", body.GetProperty("unavailableReasonCode").GetString());
        Assert.Equal(0, body.GetProperty("fields").GetArrayLength());

        var refused = await client.PostAsJsonAsync(new Uri("/api/v1/form-records/field-search", UriKind.Relative), Request(Text(), Text("contains", Sentinel)), ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, refused.StatusCode);
        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal("SEARCH_VERIFICATION_UNAVAILABLE", problem.GetProperty("title").GetString());
        Assert.Equal("TRUSTED_SEARCH_NOT_ENABLED", problem.GetProperty("unavailable_reason").GetString());
        Assert.False(problem.GetProperty("retryable").GetBoolean());
        Assert.False(problem.TryGetProperty("items", out _));

        var tooMany = await client.PostAsJsonAsync(new Uri("/api/v1/form-records/field-search", UriKind.Relative), Request([.. Enumerable.Range(0, 6).Select(_ => Text())]), ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, tooMany.StatusCode);
        Assert.Equal("SEARCH_REQUEST_INVALID", (await tooMany.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("title").GetString());

        var badOperator = await client.PostAsJsonAsync(new Uri("/api/v1/form-records/field-search", UriKind.Relative), Request(new FieldSearchCondition(Field, "regex", "String", Sentinel)), ct);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badOperator.StatusCode);
        Assert.Equal("SEARCH_OPERATOR_NOT_SUPPORTED", (await badOperator.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("title").GetString());

        var page = await client.PostAsJsonAsync(new Uri("/api/v1/form-records/field-search/page", UriKind.Relative), new FieldSearchPageRequest("opaque"), ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, page.StatusCode);

        foreach (var response in new[] { refused, tooMany, badOperator, page })
        {
            Assert.DoesNotContain(Sentinel, await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
            Assert.True(response.Headers.CacheControl?.NoStore ?? false);
        }

        Assert.NotEmpty(logs.Messages);
        Assert.DoesNotContain(logs.Messages, m => m.Contains(Sentinel, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AMissingOrMalformedFormIdOnCapabilitiesIsAContractErrorNotAnEmptyAnswer()
    {
        using var api = Api(new CapturingLoggerProvider());
        using var client = Authenticated(api);
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await client.GetAsync(new Uri($"/api/v1/form-records/search-capabilities?formDefinitionId={Guid.Empty:D}", UriKind.Relative), ct)).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await client.GetAsync(new Uri("/api/v1/form-records/search-capabilities", UriKind.Relative), ct)).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await client.GetAsync(new Uri("/api/v1/form-records/search-capabilities?formDefinitionId=not-a-guid", UriKind.Relative), ct)).StatusCode);
    }

    private static WebApplicationFactory<Program> Api(CapturingLoggerProvider logs) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Development");
            b.ConfigureLogging(l => l.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
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
                ["sub"] = "c7-contract-test",
                ["org"] = Context.OrganizationId.ToString("D"),
                ["authority_user_id"] = Context.LocalUserId.ToString("D"),
                ["name"] = "C7 contract test",
            },
            SigningCredentials = new SigningCredentials(settings.SymmetricKey(), SecurityAlgorithms.HmacSha256),
        });
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Capturing(this, categoryName);

        public void Dispose()
        {
        }

        private sealed class Capturing(CapturingLoggerProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Messages.Enqueue($"{category}: {formatter(state, exception)} {exception}");
        }
    }
}
