using System.Reflection;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.Audit.Explorer;
using EnterpriseForms.Domain.Features.Identity;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// C8 M4-3 business side without Docker: the timeline handler asks the Authority with the session's organization only, writes
/// nothing (R-5), renders exactly the Authority's envelopes with directory names (D8-67a), binds its cursor to caller, organization
/// and filter, maps every Authority refusal to a fail-closed problem and refuses an answer without served_by authority. The real
/// Authority, PostgreSQL and the browser run in Stage B (SecurityCommandTimelineTests) and Playwright, which are outstanding.
/// </summary>
public sealed class SecurityCommandTimelineHandlerTests
{
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly Guid Auditor = Guid.CreateVersion7();
    private static readonly Guid Clerk = Guid.CreateVersion7();
    private static readonly RequestContext Context = new(Org, Auditor, "https://issuer.invalid", "auditor-subject", "Ada Auditor", "assertion", Guid.CreateVersion7());
    private static readonly RequestContext OtherCaller = new(Org, Clerk, "https://issuer.invalid", "clerk-subject", "Casey Clerk", "assertion", Guid.CreateVersion7());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SecurityCommandTimelineQuery Query(string? kind = null, string? state = null) => SecurityCommandTimelineHandler.Normalize(kind, state, null, null, 10);

    private static AuthorityTimelineEntry Entry(string kind = "CreateShare") => new("Command", kind, new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero), Guid.CreateVersion7(), "Rejected", null, "FORBIDDEN",
        Clerk, null, "CommandRowOnly", "Authority", null,
        [new AuthorityTimelineDetail("form_record_id", "Identifier", Guid.CreateVersion7().ToString("D"), null), new AuthorityTimelineDetail("target_user_id", "Identifier", Clerk.ToString("D"), null),
         new AuthorityTimelineDetail("added_principals", "PrincipalList", null, ["user:" + Clerk.ToString("D")])],
        ["share_scope"]);

    [Fact]
    public async Task ThePageIsTheAuthoritysWithDirectoryNamesAndNothingIsWritten()
    {
        var world = new World { Page = new AuthorityTimelinePage([Entry()], "v1:1:A:5", 88, 9, "authority", "not_attested") };
        var page = await world.Handler().ReadAsync(Context, Query(kind: "CreateShare", state: "Rejected"), null, Ct);

        var asked = Assert.Single(world.Requests);
        Assert.Equal((Org, (string?)null, 10, Context.CorrelationId), (asked.OrganizationId, asked.After, asked.PageSize, asked.CorrelationId));
        Assert.Equal(["CreateShare"], asked.Kinds);
        Assert.Equal(["Rejected"], asked.States);
        var item = Assert.Single(page.Items);
        Assert.Equal(("Casey Clerk", "CommandRowOnly", "FORBIDDEN"), (item.ActorDisplayName, item.EvidenceBasis, item.RejectionCode));
        Assert.Equal("Casey Clerk", item.Details.Single(d => d.Key == "target_user_id").DisplayName);
        Assert.Null(item.Details.Single(d => d.Key == "form_record_id").DisplayName);
        Assert.Equal(["share_scope"], item.WithheldFields);
        Assert.Equal(88, page.AccessEvidence.AuthorityJournalRevision);
        Assert.Contains("not attested", page.AccessEvidence.Statement, StringComparison.Ordinal);
        Assert.Contains("not a sign of tampering", page.EvidenceStatement, StringComparison.Ordinal);
        Assert.NotNull(page.NextCursor);
        Assert.Equal(0, world.Writes); // R-5: no business delivery row
    }

    [Fact]
    public async Task TheCursorCarriesTheAuthorityPositionAndIsBoundToCallerOrganizationAndFilter()
    {
        var world = new World { Page = new AuthorityTimelinePage([Entry()], "v1:12345:C:" + Guid.CreateVersion7().ToString("D"), 88, 9, "authority", "not_attested") };
        var first = await world.Handler().ReadAsync(Context, Query(), null, Ct);
        await world.Handler().ReadAsync(Context, Query(), first.NextCursor, Ct);
        Assert.Equal(world.Page.After, world.Requests[^1].After);

        foreach (var (caller, query, cursor) in new[] { (OtherCaller, Query(), first.NextCursor), (Context, Query(kind: "RevokeShare"), first.NextCursor), (Context, Query(), first.NextCursor![..^4] + "AAAA"), (Context, Query(), "not-a-cursor") })
        {
            var refused = await Assert.ThrowsAsync<BusinessException>(() => world.Handler().ReadAsync(caller, query, cursor, Ct));
            Assert.Equal(("CURSOR_INVALID", 400), (refused.ErrorCode, refused.HttpStatusCode));
        }

        Assert.Equal(2, world.Requests.Count); // a refused cursor never reaches the Authority
    }

    [Theory]
    [InlineData(403, "AUDIT_VIEW_FORBIDDEN", "AUDIT_VIEW_FORBIDDEN", 403)]
    [InlineData(422, "INVALID_REQUEST", "INVALID_REQUEST", 422)]
    [InlineData(429, "RATE_LIMITED", "RATE_LIMITED", 429)]
    [InlineData(503, "AUDIT_EVIDENCE_BUSY", "AUDIT_EVIDENCE_BUSY", 503)]
    [InlineData(503, "AUDIT_EVIDENCE_UNAVAILABLE", "AUDIT_EVIDENCE_UNAVAILABLE", 503)]
    [InlineData(503, "AUTHORITY_NOT_READY", "AUTHORITY_GATE_CLOSED", 503)]
    [InlineData(500, "INTERNAL", "AUTHORITY_UNAVAILABLE", 503)]
    public async Task EveryAuthorityRefusalServesNothing(int status, string authorityCode, string code, int httpStatus)
    {
        var world = new World { Refusal = new AuthorityClientException(authorityCode, status, true, "authority detail SENT-DETAIL") };
        var refused = await Assert.ThrowsAsync<BusinessException>(() => world.Handler().ReadAsync(Context, Query(), null, Ct));
        Assert.Equal((code, httpStatus), (refused.ErrorCode, refused.HttpStatusCode));
        Assert.DoesNotContain("SENT-DETAIL", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAnswerWithoutRecordedAuthorityServiceIsRefused()
    {
        foreach (var page in new[] { new AuthorityTimelinePage([Entry()], null, 0, 9, "authority", "not_attested"), new AuthorityTimelinePage([Entry()], null, 5, 9, "business", "not_attested"), new AuthorityTimelinePage([Entry()], null, 5, 9, "authority", "delivered") })
        {
            var refused = await Assert.ThrowsAsync<BusinessException>(() => new World { Page = page }.Handler().ReadAsync(Context, Query(), null, Ct));
            Assert.Equal("AUDIT_EVIDENCE_UNAVAILABLE", refused.ErrorCode);
        }
    }

    [Fact]
    public void FiltersAreCodesAndDatesOnly()
    {
        foreach (var invalid in new Action[]
        {
            () => SecurityCommandTimelineHandler.Normalize("create share", null, null, null, null),
            () => SecurityCommandTimelineHandler.Normalize("SENT-KIND'--", null, null, null, null),
            () => SecurityCommandTimelineHandler.Normalize(null, "Done", null, null, null),
            () => SecurityCommandTimelineHandler.Normalize(null, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null),
            () => SecurityCommandTimelineHandler.Normalize(null, null, null, null, 51),
        })
        {
            Assert.Equal(422, Assert.Throws<BusinessException>(invalid).HttpStatusCode);
        }

        Assert.Equal(25, SecurityCommandTimelineHandler.Normalize(null, null, null, null, null).PageSize);
        Assert.NotEqual(Query().Digest(), Query(kind: "RevokeShare").Digest());
    }

    private sealed class World
    {
        public AuthorityTimelinePage Page { get; init; } = new([], null, 1, 1, "authority", "not_attested");

        public AuthorityClientException? Refusal { get; init; }

        public List<AuthorityTimelineQuery> Requests { get; } = [];

        public int Writes { get; private set; }

        public SecurityCommandTimelineHandler Handler()
        {
            var authority = TimelineStub.Create<ISecurityCommandTimelineAuthority>((_, args) =>
            {
                Requests.Add((AuthorityTimelineQuery)args[1]!);
                return Refusal is { } refusal ? Task.FromException<AuthorityTimelinePage>(refusal) : Task.FromResult(Page);
            });
            var store = TimelineStub.Create<IBusinessStore>((method, _) => method.Name == nameof(IBusinessStore.BeginScopeAsync) ? Task.FromResult(Scope()) : throw new NotSupportedException(method.Name));
            return new SecurityCommandTimelineHandler(store, authority, new Protector());
        }

        private IBusinessScope Scope() => TimelineStub.Create<IBusinessScope>((method, _) => method.Name switch
        {
            nameof(IBusinessScope.GetUsersAsync) => Task.FromResult(new List<User> { new() { UserId = Clerk, TenantId = Org, DisplayName = "Casey Clerk" } }),
            nameof(IAsyncDisposable.DisposeAsync) => ValueTask.CompletedTask,
            _ => CountWrite(method.Name),
        });

        private object CountWrite(string name)
        {
            Writes++;
            throw new NotSupportedException(name);
        }
    }

    private sealed class Protector : IOpaqueCursorProtector
    {
        public string Protect(string payload, TimeSpan lifetime) => "ok." + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payload));

        public string? Unprotect(string token)
        {
            if (!token.StartsWith("ok.", StringComparison.Ordinal))
            {
                return null;
            }

            try
            {
                return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(token[3..]));
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }

    public class TimelineStub<T> : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Handler { get; set; } = (_, _) => null;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args ?? []);
    }

    private static class TimelineStub
    {
        public static T Create<T>(Func<MethodInfo, object?[], object?> handler)
            where T : class
        {
            var proxy = DispatchProxy.Create<T, TimelineStub<T>>();
            ((TimelineStub<T>)(object)proxy).Handler = handler;
            return proxy;
        }
    }
}
