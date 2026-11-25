using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.Audit;
using EnterpriseForms.Application.Features.Audit.Explorer;
using EnterpriseForms.Application.Features.Dashboard;
using EnterpriseForms.Domain.Features.Audit;
using EnterpriseForms.Domain.Features.FormDefinitions;
using EnterpriseForms.Domain.Features.Identity;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// C8 M4-2 business side without Docker: the redaction catalog (completeness, allowlists, forged rows, sentinel leaks; AC-M4-2.4),
/// the explorer's fail-closed dependence on the Authority's committed evidence (AC-M4-2.1), business-reported delivery outcome
/// (AC-M4-2.2), the keyset cursor bound to caller, organization and filter (R-8), keyset stability, the organization predicate
/// passed to the store (AC-M4-2.5 at the handler; the SQL predicate with a second organization present is Stage B), filter
/// validation and the audit.view capability. The real Authority, PostgreSQL and the browser run in Stage B (AuditExplorerTests,
/// SecurityAuditAccessTests) and Playwright, which are outstanding.
/// </summary>
public sealed class AuditExplorerTests
{
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly Guid OtherOrg = Guid.CreateVersion7();
    private static readonly Guid Auditor = Guid.CreateVersion7();
    private static readonly Guid Clerk = Guid.CreateVersion7();
    private static readonly Guid Form = Guid.CreateVersion7();
    private static readonly RequestContext Context = new(Org, Auditor, "https://issuer.invalid", "auditor-subject", "Ada Auditor", "assertion", Guid.CreateVersion7());
    private static readonly DateTimeOffset Start = new(2026, 11, 19, 9, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AuditExplorerQuery Query(int pageSize = 3, Guid? actor = null, string? action = null, bool exclude = false) =>
        AuditExplorerHandlers.Normalize(null, null, actor, action, null, null, null, null, null, null, exclude, pageSize);

    private static AuditEvent Event(string action, object? metadata = null, Guid? tenant = null, DateTimeOffset? at = null, long sequence = 1, string description = "stored description", Guid? actor = null) => new()
    {
        AuditEventId = Guid.CreateVersion7(), TenantId = tenant ?? Org, AuditSequence = sequence, EventOccurredOn = at ?? Start, EventCategory = "Record", EventAction = action, EventOutcome = "Success",
        ActorUserId = actor ?? Clerk, ActorType = "User", FormDefinitionId = Form, FormRecordId = Guid.CreateVersion7(), FieldDefinitionId = Guid.CreateVersion7(), CorrelationIdentifier = Guid.CreateVersion7(),
        EventDescription = description, EventMetadataJson = metadata is null ? "{}" : JsonSerializer.Serialize(metadata, WebJson), EventSchemaVersion = 1,
    };

    // ---- redaction catalog ----

    [Fact]
    public void EveryAuditActionHasExactlyOneCatalogEntry()
    {
        var actions = typeof(AuditActions).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(82, actions.Count); // C9 M1 (C-179) added FORM_VERSION_BEHAVIOR_CHANGED; C-188 BF-1 added FORM_VERSION_AUTHORITY_PUBLICATION_ABANDONED
        var missing = actions.Where(a => AuditEventCatalog.Find(a) is null).ToList();
        Assert.True(missing.Count == 0, "actions without a catalog entry: " + string.Join(", ", missing));
        var stray = AuditEventCatalog.Entries.Keys.Where(k => !actions.Contains(k)).ToList();
        Assert.True(stray.Count == 0, "catalog entries for no action: " + string.Join(", ", stray));
        Assert.All(AuditEventCatalog.Entries.Values, e => Assert.Empty(e.Shown.Select(s => s.Key).Intersect(e.Withheld.Select(w => w.Key), StringComparer.OrdinalIgnoreCase)));
    }

    /// <summary>Each allowlisted or withheld key is written by some call site (no dead or misspelt rule); keys are matched case-insensitively.</summary>
    [Fact]
    public void EveryCatalogKeyExistsAtSomeCallSite()
    {
        var root = Path.Combine(RepositoryPaths.Root, "src", "backend");
        var source = string.Join('\n', Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !p.EndsWith("AuditEventCatalog.cs", StringComparison.Ordinal))
            .Select(File.ReadAllText));
        var keys = AuditEventCatalog.Entries.Values.SelectMany(e => e.Shown.Select(s => s.Key).Concat(e.Withheld.Select(w => w.Key))).SelectMany(k => k.Split('.')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var absent = keys.Where(k => !Regex.IsMatch(source, @"\b" + Regex.Escape(k) + @"\b", RegexOptions.IgnoreCase)).ToList();
        Assert.True(absent.Count == 0, "catalog keys no call site writes: " + string.Join(", ", absent));
    }

    [Fact]
    public void AnUnknownOrForgedActionRendersAsTheEnvelopeOnly()
    {
        var forged = Event("TOTALLY_FORGED_ACTION", new { reason = "SENTINEL-forged", fieldKey = "SENTINEL-key" }, description: "SENTINEL-description");
        var rendered = AuditEventCatalog.Render(forged);
        Assert.False(rendered.Recognized);
        Assert.Equal(AuditEventCatalog.UnrecognizedLabel, rendered.ActionLabel);
        Assert.Equal(AuditEventCatalog.UnrecognizedDescription, rendered.Description);
        Assert.Empty(rendered.Metadata);
        Assert.Empty(rendered.Withheld);
    }

    [Fact]
    public void UnknownKeysAreDroppedAndAWrongShapeIsWithheld()
    {
        var rendered = AuditEventCatalog.Render(Event(AuditActions.SecurityAuditorAppointed, new { reasonCode = "free text with spaces and SENTINEL", injected = "SENTINEL-injected" }));
        Assert.Empty(rendered.Metadata);
        var withheld = Assert.Single(rendered.Withheld);
        Assert.Equal(AuditEventCatalog.ShapePolicy, withheld.Policy);
        Assert.DoesNotContain("SENTINEL", JsonSerializer.Serialize(rendered), StringComparison.Ordinal);
    }

    [Fact]
    public void CodesIdentifiersCountsAndDirectoryNamesAreShownAndListsBecomeCounts()
    {
        var rendered = AuditEventCatalog.Render(Event(AuditActions.FieldOptionsChanged, new { fieldKey = "secret_key", changes = 4, added = new List<string> { "opt-a", "opt-b" }, optionListId = Guid.CreateVersion7() }));
        Assert.Contains(rendered.Metadata, m => m.Key == "added" && m.Value == "2" && m.Kind == AuditMetadataKind.Count);
        Assert.Contains(rendered.Metadata, m => m.Key == "changes" && m.Value == "4");
        Assert.Contains(rendered.Withheld, w => w.Label == "Field key" && w.Policy == AuditEventCatalog.DesignLabelPolicy);
        Assert.DoesNotContain("opt-a", JsonSerializer.Serialize(rendered), StringComparison.Ordinal);

        var team = AuditEventCatalog.Render(Event(AuditActions.TeamRenamed, new { previousTeamName = "Night shift", teamName = "Evening shift" }));
        Assert.Contains(team.Metadata, m => m.Kind == AuditMetadataKind.DirectoryName && m.Value == "Evening shift"); // D8-67a

        var regional = AuditEventCatalog.Render(Event(AuditActions.OrganizationSettingsUpdated, new { revision = 3, current = new { defaultIanaTimeZoneId = "America/Toronto", defaultCountryCode = "CA" } }));
        Assert.Contains(regional.Metadata, m => m.Key == "current.defaultIanaTimeZoneId" && m.Value == "America/Toronto");
    }

    [Fact]
    public void AStoredDescriptionIsShownOnlyWhenTheCatalogMarksItSafeAndItMatchesExactly()
    {
        Assert.Equal("record status changed from Draft to Submitted", AuditEventCatalog.Render(Event(AuditActions.RecordSubmitted, description: "record status changed from Draft to Submitted")).Description);
        Assert.Equal("The record's status changed.", AuditEventCatalog.Render(Event(AuditActions.RecordSubmitted, description: "record status changed from Draft to Submitted SENTINEL")).Description);
        Assert.Equal(AuditEventCatalog.Find(AuditActions.RecordUpdated)!.Description, AuditEventCatalog.Render(Event(AuditActions.RecordUpdated, description: "edited SENTINEL")).Description);
    }

    /// <summary>
    /// AC-M4-2.4 sentinel test: events written as the call sites write them, carrying protected and Public values, a PIN, a file
    /// name, a search term, free-text reasons, field keys and labels, option text and a membership display-name hint, plus a
    /// forged row; no list or detail response contains any of them.
    /// </summary>
    [Fact]
    public async Task NoListOrDetailResponseContainsASentinel()
    {
        string[] sentinels = ["SENT-CONFIDENTIAL-VALUE", "SENT-PUBLIC-VALUE", "SENT-PIN-4471", "SENT-FILE.pdf", "SENT-SEARCH-TERM", "SENT-FREE-REASON", "SENT-FIELD-KEY", "SENT-FIELD-LABEL", "SENT-OPTION-TEXT", "SENT-NAME-HINT"];
        var events = new List<AuditEvent>
        {
            Event(AuditActions.RecordUpdated, new { contentRevision = 4, changedFieldKeys = new List<string> { "SENT-FIELD-KEY" }, complete = true, editBasis = "Owner", detail = new { value = "SENT-CONFIDENTIAL-VALUE" } }, description: "SENT-PUBLIC-VALUE edited", sequence: 1),
            Event(AuditActions.FieldConfigurationChanged, new { fieldKey = "SENT-FIELD-KEY", dataType = "String", previousDisplayName = "SENT-FIELD-LABEL", newDisplayName = "SENT-FIELD-LABEL", descriptionChanged = true }, sequence: 2),
            Event(AuditActions.FieldOptionsChanged, new { fieldKey = "SENT-FIELD-KEY", changes = 1, added = new List<string> { "SENT-OPTION-TEXT" }, relabeled = new List<string> { "SENT-OPTION-TEXT" } }, sequence: 3),
            Event(AuditActions.RecordAccessGranted, new { role = "Reader", recipientUserId = Guid.CreateVersion7(), fieldKeys = new List<string> { "SENT-FIELD-KEY" } }, sequence: 4),
            Event(AuditActions.MembershipRequested, new { requestId = Guid.CreateVersion7(), initialRole = "designer", reason = "SENT-FREE-REASON", display_name_hint = "SENT-NAME-HINT" }, sequence: 5),
            Event(AuditActions.DocumentRemoved, new { reason = "SENT-FREE-REASON", contentRevision = 5, fileName = "SENT-FILE.pdf" }, sequence: 6),
            Event(AuditActions.FieldSearchApproved, new { operators = new List<string> { "equals" }, approvalReference = "SENT-FREE-REASON", searchTerm = "SENT-SEARCH-TERM" }, sequence: 7),
            Event(AuditActions.RecordVersionRestored, new { contentRevision = 6, sourceContentRevision = 2, changedFieldKeys = new List<string> { "SENT-FIELD-KEY" }, reason = "SENT-FREE-REASON", pin = "SENT-PIN-4471" }, description: "earlier version 2 restored as a new revision", sequence: 8),
            Event(AuditActions.SecurityIncidentNoteAdded, new { securityIncidentNoteId = Guid.CreateVersion7(), noteLength = "17" }, sequence: 9),
            Event("FORGED_ACTION", new { value = "SENT-CONFIDENTIAL-VALUE", pin = "SENT-PIN-4471" }, description: "SENT-PUBLIC-VALUE", sequence: 10),
            Event(AuditActions.FormVersionPublished, new { versionNumber = 2, changeReason = "SENT-FREE-REASON", elements = 9 }, sequence: 11),
            Event(AuditActions.ChildRecordUnlinked, new { reason = "SENT-FREE-REASON", commandId = Guid.CreateVersion7() }, sequence: 12),
        };
        var world = new World(events);
        var handlers = world.Handlers();

        var payloads = new List<string>();
        string? cursor = null;
        do
        {
            var page = await handlers.SearchAsync(Context, Query(pageSize: 5), cursor, Ct);
            payloads.Add(JsonSerializer.Serialize(page));
            cursor = page.NextCursor;
        }
        while (cursor is not null);

        foreach (var e in events)
        {
            payloads.Add(JsonSerializer.Serialize(await handlers.GetAsync(Context, e.AuditEventId, Ct)));
        }

        var all = string.Join('\n', payloads);
        Assert.All(sentinels, s => Assert.DoesNotContain(s, all, StringComparison.Ordinal));
        Assert.Contains("withheld by policy", all, StringComparison.Ordinal); // the withheld items are listed by label
    }

    // ---- fail-closed dependence on the Authority (AC-M4-2.1) ----

    [Theory]
    [InlineData("AUDIT_VIEW_FORBIDDEN", 403, "AUDIT_VIEW_FORBIDDEN", 403)]
    [InlineData("ACTOR_NOT_BOUND", 403, "AUDIT_VIEW_FORBIDDEN", 403)]
    [InlineData("RATE_LIMITED", 429, "RATE_LIMITED", 429)]
    [InlineData("AUDIT_EVIDENCE_BUSY", 503, "AUDIT_EVIDENCE_BUSY", 503)]
    [InlineData("AUDIT_EVIDENCE_UNAVAILABLE", 503, "AUDIT_EVIDENCE_UNAVAILABLE", 503)]
    [InlineData("AUTHORITY_NOT_READY", 503, "AUTHORITY_GATE_CLOSED", 503)]
    [InlineData("INTEGRITY_QUARANTINE", 423, "AUTHORITY_GATE_CLOSED", 503)]
    [InlineData("AUTHORITY_UNAVAILABLE", 503, "AUTHORITY_UNAVAILABLE", 503)]
    public async Task ARefusedOrFailedAuthorityAnswerServesNothingAndReadsNoRow(string authorityCode, int authorityStatus, string expectedCode, int expectedStatus)
    {
        var world = new World([Event(AuditActions.RecordViewed)]) { Refusal = new AuthorityClientException(authorityCode, authorityStatus, authorityStatus >= 500) };
        var list = await Assert.ThrowsAsync<BusinessException>(() => world.Handlers().SearchAsync(Context, Query(), null, Ct));
        var detail = await Assert.ThrowsAsync<BusinessException>(() => world.Handlers().GetAsync(Context, world.Events[0].AuditEventId, Ct));
        Assert.Equal((expectedCode, expectedStatus), (list.ErrorCode, list.HttpStatusCode));
        Assert.Equal((expectedCode, expectedStatus), (detail.ErrorCode, detail.HttpStatusCode));
        Assert.Equal(0, world.ScopesOpened);
        Assert.Empty(world.Written);
    }

    [Fact]
    public async Task AnAnswerWithoutCommittedEvidenceIsNotTrusted()
    {
        var world = new World([Event(AuditActions.RecordViewed)]) { Decision = new AuthorityAuditAccessDecision(true, 0, 7, "not_attested", Guid.Empty) };
        var refused = await Assert.ThrowsAsync<BusinessException>(() => world.Handlers().SearchAsync(Context, Query(), null, Ct));
        Assert.Equal("AUDIT_EVIDENCE_UNAVAILABLE", refused.ErrorCode);
        Assert.Equal(0, world.ScopesOpened);
    }

    [Fact]
    public async Task AnAuthorizedPageIsRequestedWithTheFilterDigestAndRecordsTheDeliveryOutcome()
    {
        var world = new World([Event(AuditActions.RecordViewed, new { contentRevision = 2 }, sequence: 1)]);
        var query = Query(pageSize: 10, actor: Clerk);
        var page = await world.Handlers().SearchAsync(Context, query, null, Ct);

        var asked = Assert.Single(world.AuthorityRequests);
        Assert.Equal(("ExplorerPage", query.Digest(), 10, (Guid?)null, Org, Context.CorrelationId), (asked.AccessKind, asked.FilterDigest, asked.PageSize, asked.AuditEventId, asked.OrganizationId, asked.CorrelationId));
        Assert.Matches("^[0-9a-f]{64}$", asked.FilterDigest);
        Assert.Equal(41, page.AccessEvidence.AuthorityJournalRevision);
        Assert.Single(page.Items);
        Assert.DoesNotContain(page.Items[0].GetType().GetProperties(), p => p.Name is "Metadata" or "Description"); // list items are envelopes

        // AC-M4-2.2: the business row states the delivery outcome with the Authority's revision; the Authority states authorization only.
        var delivery = Assert.Single(world.Written);
        Assert.Equal((AuditActions.AuditEventsViewed, Org, Auditor, Context.CorrelationId), (delivery.EventAction, delivery.TenantId, delivery.ActorUserId, delivery.CorrelationIdentifier));
        using var metadata = JsonDocument.Parse(delivery.EventMetadataJson);
        Assert.Equal("Delivered", metadata.RootElement.GetProperty("deliveryOutcome").GetString());
        Assert.Equal(41, metadata.RootElement.GetProperty("authorityJournalRevision").GetInt64());
        Assert.Equal(1, world.Commits);
        Assert.All(world.QueriedTenants, t => Assert.Equal(Org, t)); // the session's organization, never a request value
    }

    [Fact]
    public async Task AReadFailureAfterAuthorizationServesNothingAndAttemptsAFailedDeliveryRow()
    {
        var world = new World([Event(AuditActions.RecordViewed)]) { FailReads = true };
        var refused = await Assert.ThrowsAsync<BusinessException>(() => world.Handlers().SearchAsync(Context, Query(), null, Ct));
        Assert.Equal(("AUDIT_DELIVERY_FAILED", 503), (refused.ErrorCode, refused.HttpStatusCode));
        Assert.DoesNotContain("store down", refused.Message, StringComparison.Ordinal);
        Assert.Null(refused.InnerException);
        var failed = Assert.Single(world.Written);
        Assert.Contains("\"deliveryOutcome\":\"Failed\"", failed.EventMetadataJson, StringComparison.Ordinal);
        Assert.Equal("Failed", failed.EventOutcome);
        Assert.DoesNotContain("store down", failed.EventMetadataJson, StringComparison.Ordinal);
        Assert.DoesNotContain("store down", failed.EventDescription, StringComparison.Ordinal);

        var detail = await Assert.ThrowsAsync<BusinessException>(() => world.Handlers().GetAsync(Context, Guid.CreateVersion7(), Ct));
        Assert.Equal(("AUDIT_DELIVERY_FAILED", 503), (detail.ErrorCode, detail.HttpStatusCode));
        Assert.DoesNotContain("store down", detail.Message, StringComparison.Ordinal);
        Assert.Null(detail.InnerException);
    }

    [Fact]
    public async Task AnotherOrganizationsEventAndAnUnknownIdAreTheSame404()
    {
        var foreign = Event(AuditActions.RecordViewed, tenant: OtherOrg);
        var world = new World([foreign]);
        var other = await Assert.ThrowsAsync<BusinessException>(() => world.Handlers().GetAsync(Context, foreign.AuditEventId, Ct));
        var unknown = await Assert.ThrowsAsync<BusinessException>(() => world.Handlers().GetAsync(Context, Guid.CreateVersion7(), Ct));
        Assert.Equal((other.ErrorCode, other.HttpStatusCode, other.Message), (unknown.ErrorCode, unknown.HttpStatusCode, unknown.Message));
        Assert.Equal(404, other.HttpStatusCode);
        Assert.All(world.Written, w => Assert.Contains("\"deliveryOutcome\":\"NotFound\"", w.EventMetadataJson, StringComparison.Ordinal));
        Assert.All(world.Written, w => Assert.Equal("Success", w.EventOutcome)); // ck_audit_events_outcome: Success/Denied/Failed only
        Assert.All(world.AuthorityRequests, r => Assert.Equal("EventDetail", r.AccessKind));
    }

    [Fact]
    public async Task TheDetailShowsAllowlistedMetadataWithheldLabelsLinksAndNoReceipt()
    {
        var shared = Event(AuditActions.RecordAccessGranted, new { role = "Contributor", recipientTeamId = Guid.CreateVersion7(), fieldKeys = new List<string> { "salary" }, commandId = Guid.CreateVersion7() });
        var world = new World([shared]);
        var detail = await world.Handlers().GetAsync(Context, shared.AuditEventId, Ct);
        Assert.Contains(detail.Metadata, m => m.Key == "role" && m.Value == "Contributor" && m.Kind == "Code");
        Assert.Contains(detail.WithheldMetadata, w => w.Label == "Shared fields" && w.Policy == AuditEventCatalog.ScopePolicy);
        Assert.Equal(("None", "AUD-C7-01/G24"), (detail.Receipt.State, detail.Receipt.Reason));
        Assert.Contains(detail.Links, l => l.Relation == "record" && l.Href == "/app/records/" + shared.FormRecordId!.Value.ToString("D"));
        Assert.Equal("Form one", detail.Event.FormDisplayName);
        Assert.Equal("Casey Clerk", detail.Event.Actor.DisplayName);
        Assert.Equal("BusinessProjection", detail.Event.Evidence);
        Assert.Equal(AuditActions.AuditEventViewed, Assert.Single(world.Written).EventAction);
    }

    // ---- keyset paging and cursor binding (R-8) ----

    [Fact]
    public async Task KeysetPagingIsStableWhenNewerEventsArriveBetweenPages()
    {
        var events = Enumerable.Range(1, 7).Select(i => Event(AuditActions.RecordViewed, at: Start.AddMinutes(i / 2), sequence: i)).ToList(); // equal times exercise the sequence tie-break
        var world = new World([.. events]);
        var handlers = world.Handlers();
        var first = await handlers.SearchAsync(Context, Query(), null, Ct);
        world.Events.Add(Event(AuditActions.RecordViewed, at: Start.AddHours(1), sequence: 99)); // newer than everything served
        var second = await handlers.SearchAsync(Context, Query(), first.NextCursor, Ct);
        var third = await handlers.SearchAsync(Context, Query(), second.NextCursor, Ct);

        var served = first.Items.Concat(second.Items).Concat(third.Items).Select(i => i.AuditEventId).ToList();
        Assert.Equal(events.OrderByDescending(e => e.EventOccurredOn).ThenByDescending(e => e.AuditSequence).Select(e => e.AuditEventId), served);
        Assert.Null(third.NextCursor);
        Assert.Equal(3, world.AuthorityRequests.Count); // every page is authorized and recorded again
    }

    [Fact]
    public async Task ACursorIsRejectedWhenTamperedOrUsedByAnotherCallerOrganizationOrFilterBeforeTheAuthorityIsAsked()
    {
        var world = new World(Enumerable.Range(1, 5).Select(i => Event(AuditActions.RecordViewed, sequence: i)).ToList());
        var handlers = world.Handlers();
        var cursor = (await handlers.SearchAsync(Context, Query(), null, Ct)).NextCursor!;
        var askedBefore = world.AuthorityRequests.Count;

        var attempts = new (RequestContext Caller, AuditExplorerQuery Filter, string Token)[]
        {
            (Context, Query(), cursor[..^2] + "AA"),
            (Context, Query(), "not-a-cursor"),
            (Context with { LocalUserId = Guid.CreateVersion7() }, Query(), cursor),
            (Context with { OrganizationId = OtherOrg }, Query(), cursor),
            (Context, Query(action: "RECORD_VIEWED"), cursor),
            (Context, Query(pageSize: 4), cursor),
        };
        foreach (var (caller, filter, token) in attempts)
        {
            var refused = await Assert.ThrowsAsync<BusinessException>(() => handlers.SearchAsync(caller, filter, token, Ct));
            Assert.Equal(("CURSOR_INVALID", 400), (refused.ErrorCode, refused.HttpStatusCode));
        }

        Assert.Equal(askedBefore, world.AuthorityRequests.Count);
    }

    [Fact]
    public async Task HidingAuditAccessExcludesTheExplorersOwnDeliveryRows()
    {
        var world = new World([Event(AuditActions.AuditEventsViewed, sequence: 2), Event(AuditActions.RecordViewed, sequence: 1)]);
        var page = await world.Handlers().SearchAsync(Context, Query(exclude: true), null, Ct);
        Assert.Equal(AuditActions.RecordViewed, Assert.Single(page.Items).Action);
    }

    // ---- filter validation ----

    [Theory]
    [InlineData("record viewed", null, null)]
    [InlineData("RECORD_VIEWED; DROP", null, null)]
    [InlineData(null, "Re cord", null)]
    [InlineData(null, null, "Form-Record")]
    public void FiltersAcceptOnlyCodes(string? action, string? category, string? entityType)
    {
        var refused = Assert.Throws<BusinessException>(() => AuditExplorerHandlers.Normalize(null, null, null, action, category, null, entityType, null, null, null, null, null));
        Assert.Equal(("INVALID_REQUEST", 422), (refused.ErrorCode, refused.HttpStatusCode));
    }

    [Fact]
    public void PeriodsPageSizesAndEmptyIdsAreValidatedAndTheDigestIsCanonical()
    {
        Assert.Throws<BusinessException>(() => AuditExplorerHandlers.Normalize(Start, Start, null, null, null, null, null, null, null, null, null, null));
        Assert.Throws<BusinessException>(() => AuditExplorerHandlers.Normalize(null, null, null, null, null, null, null, null, null, null, null, 51));
        Assert.Throws<BusinessException>(() => AuditExplorerHandlers.Normalize(null, null, Guid.Empty, null, null, null, null, null, null, null, null, null));
        Assert.Equal(25, AuditExplorerHandlers.Normalize(null, null, null, null, null, null, null, null, null, null, null, null).PageSize);

        var local = AuditExplorerHandlers.Normalize(new DateTimeOffset(2026, 11, 19, 4, 0, 0, TimeSpan.FromHours(-5)), null, null, " RECORD_VIEWED ", null, null, null, null, null, null, null, 10);
        var utc = AuditExplorerHandlers.Normalize(new DateTimeOffset(2026, 11, 19, 9, 0, 0, TimeSpan.Zero), null, null, "RECORD_VIEWED", null, null, null, null, null, null, null, 10);
        Assert.Equal(utc.Digest(), local.Digest());
        Assert.NotEqual(utc.Digest(), (utc with { ExcludeAuditAccess = true }).Digest());
        Assert.NotEqual(utc.Digest(), (utc with { PageSize = 11 }).Digest());
    }

    // ---- capability ----

    [Fact]
    public async Task TheAuditViewCapabilityFollowsTheAuthoritysViewAuditDecisionOnly()
    {
        IReadOnlyList<ProtectedAction>? checkedActions = null;
        var authority = Stub.Create<IAuthorizationAuthorityClient>((method, args) =>
        {
            var requests = (IReadOnlyList<PermissionCheckRequest>)args[1]!;
            checkedActions = requests.Select(r => r.RequestedAction).ToList();
            return Task.FromResult<IReadOnlyList<PermissionDecision>>(requests.Select(r => new PermissionDecision(r.RequestedAction == ProtectedAction.ViewAudit, null, 9, 9, null, Guid.Empty)).ToList());
        });
        var handler = (DashboardHandler)Activator.CreateInstance(typeof(DashboardHandler), typeof(DashboardHandler).GetConstructors()[0].GetParameters().Select(p => p.ParameterType == typeof(IAuthorizationAuthorityClient) ? authority : null).ToArray())!;
        var capabilities = await handler.CapabilitiesAsync(Context, Ct);
        Assert.Contains("audit.view", capabilities);
        Assert.DoesNotContain("security.administer", capabilities);
        Assert.Contains(ProtectedAction.ViewAudit, checkedActions!);
    }

    // ---- stand-ins ----

    private sealed class World(List<AuditEvent> events)
    {
        public List<AuditEvent> Events { get; } = events;

        public List<AuditEvent> Written { get; } = [];

        public List<AuditAccessQuery> AuthorityRequests { get; } = [];

        public List<Guid> QueriedTenants { get; } = [];

        public AuthorityClientException? Refusal { get; init; }

        public AuthorityAuditAccessDecision? Decision { get; init; }

        public bool FailReads { get; init; }

        public int ScopesOpened { get; private set; }

        public int Commits { get; private set; }

        public AuditExplorerHandlers Handlers()
        {
            var authority = Stub.Create<IAuditAccessAuthority>((_, args) =>
            {
                var query = (AuditAccessQuery)args[1]!;
                AuthorityRequests.Add(query);
                return Refusal is { } refusal ? Task.FromException<AuthorityAuditAccessDecision>(refusal) : Task.FromResult(Decision ?? new AuthorityAuditAccessDecision(true, 41, 7, "not_attested", query.CorrelationId));
            });
            var store = Stub.Create<IBusinessStore>((method, _) => method.Name == nameof(IBusinessStore.BeginScopeAsync) ? Task.FromResult(NewScope()) : throw new NotSupportedException(method.Name));
            return new AuditExplorerHandlers(store, authority, new TestProtector(), TimeProvider.System);
        }

        private IBusinessScope NewScope()
        {
            ScopesOpened++;
            var pending = new List<AuditEvent>();
            return Stub.Create<IBusinessScope>((method, args) => method.Name switch
            {
                nameof(IBusinessScope.QueryOrganizationAuditEventsAsync) => FailReads ? throw new InvalidOperationException("store down") : Task.FromResult(Query((Guid)args[0]!, (OrganizationAuditEventFilter)args[1]!, (AuditEventPosition?)args[2], (int)args[3]!)),
                nameof(IBusinessScope.FindOrganizationAuditEventAsync) => FailReads ? throw new InvalidOperationException("store down") : Task.FromResult(Events.FirstOrDefault(e => e.TenantId == (Guid)args[0]! && e.AuditEventId == (Guid)args[1]!)),
                nameof(IBusinessScope.GetUsersAsync) => Task.FromResult(new List<User> { new() { UserId = Clerk, TenantId = Org, DisplayName = "Casey Clerk" }, new() { UserId = Auditor, TenantId = Org, DisplayName = "Ada Auditor" } }),
                nameof(IBusinessScope.GetFormDefinitionsAsync) => Task.FromResult(new List<FormDefinition> { new() { FormDefinitionId = Form, TenantId = Org, FormDefinitionName = "Form one" } }),
                nameof(IBusinessScope.AddAuditEvent) => Record(pending, (AuditEvent)args[0]!),
                nameof(IBusinessScope.CommitAsync) => Commit(pending),
                nameof(IAsyncDisposable.DisposeAsync) => ValueTask.CompletedTask,
                _ => throw new NotSupportedException(method.Name),
            });
        }

        private static object? Record(List<AuditEvent> pending, AuditEvent e)
        {
            pending.Add(e);
            return null;
        }

        private Task Commit(List<AuditEvent> pending)
        {
            Commits++;
            Written.AddRange(pending);
            pending.Clear();
            return Task.CompletedTask;
        }

        private List<AuditEvent> Query(Guid tenant, OrganizationAuditEventFilter filter, AuditEventPosition? after, int limit)
        {
            QueriedTenants.Add(tenant);
            return Events.Where(e => e.TenantId == tenant)
                .Where(e => filter.ActorUserId is null || e.ActorUserId == filter.ActorUserId)
                .Where(e => filter.EventAction is null || e.EventAction == filter.EventAction)
                .Where(e => !filter.ExcludedActions.Contains(e.EventAction))
                .Where(e => after is null || e.EventOccurredOn < after.OccurredOn || (e.EventOccurredOn == after.OccurredOn && e.AuditSequence < after.Sequence))
                .OrderByDescending(e => e.EventOccurredOn).ThenByDescending(e => e.AuditSequence).Take(limit).ToList();
        }
    }

    private sealed class TestProtector : IOpaqueCursorProtector
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
                return null; // an altered token fails authentication in the real protector
            }
        }
    }

    public class Stub<T> : DispatchProxy
        where T : class
    {
        public Func<MethodInfo, object?[], object?> Handler { get; set; } = (method, _) => throw new NotSupportedException(method.Name);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args ?? []);
    }

    private static class Stub
    {
        public static T Create<T>(Func<MethodInfo, object?[], object?> handler)
            where T : class
        {
            var proxy = DispatchProxy.Create<T, Stub<T>>();
            ((Stub<T>)(object)proxy).Handler = handler;
            return proxy;
        }
    }
}
