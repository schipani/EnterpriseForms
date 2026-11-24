using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.FormDesigner;
using EnterpriseForms.Application.Features.FormRecords.Projection;
using EnterpriseForms.Contracts.Features;
using EnterpriseForms.Domain.Features.Audit;
using EnterpriseForms.Domain.Features.FormDefinitions;
using EnterpriseForms.Domain.Features.RelatedForms;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// Phase C9 M3 (WP-7): CLAUDE.md §11 #1–#5 at the handler contract — the designer lifecycle V1 → V2 → V3 (restored from
/// V1) → V4 (the original email field re-added) through the real <see cref="FormDesignerHandlers"/> (save draft, publish
/// impact, publish, restore to draft, compare) over an in-memory store: stable field identities, immutable snapshots,
/// <c>RestoredFromFormVersionId</c>, semantic comparisons, the counts-only publish audit, the Authority registration of
/// each version, concurrency and authorization refusals that change nothing; then the Historical / Latest / Hybrid layout
/// selection over that four-version history. The real-stack proof is <c>FormVersionAcceptanceTests</c> in Stage B.
/// </summary>
public sealed class FormVersionLifecycleContractTests
{
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly Guid Me = Guid.CreateVersion7();
    private static readonly RequestContext Context = new(Org, Me, "https://issuer.invalid", "subject", "Tester", "assertion", Guid.CreateVersion7());

    private static readonly Guid First = Guid.CreateVersion7();
    private static readonly Guid Last = Guid.CreateVersion7();
    private static readonly Guid Phone = Guid.CreateVersion7();
    private static readonly Guid Email = Guid.CreateVersion7();

    /// <summary>The audit outcomes the database accepts (ck_audit_events_outcome).</summary>
    private static readonly string[] AllowedOutcomes = ["Success", "Denied", "Failed"];

    private static string Element(Guid fieldId, string label, bool required = false) =>
        JsonSerializer.Serialize(new { kind = "field", fieldDefinitionId = fieldId, control = "text", label, required, layout = new { columnSpan = 4, startOnNewRow = false }, validation = new { } });

    private static string Layout(params string[] elements) => $$"""{"layoutSchemaVersion":2,"title":"Intake","sections":[{"sectionKey":"main","title":"Main","gridColumns":4,"elements":[{{string.Join(",", elements)}}]}]}""";

    private static readonly string V1Layout = Layout(Element(First, "First name", true), Element(Last, "Last name", true), Element(Phone, "Phone"));
    private static readonly string V2Layout = Layout(Element(First, "First name", true), Element(Last, "Last name", true), Element(Email, "Email", true));

    private static List<Guid> FieldIds(string layoutJson) =>
        JsonNode.Parse(layoutJson)!["sections"]!.AsArray().SelectMany(s => s!["elements"]!.AsArray()).Select(e => Guid.Parse(e!["fieldDefinitionId"]!.GetValue<string>())).ToList();

    private static List<(string ChangeType, string ElementId)> Structural(LayoutComparisonResponse comparison) =>
        comparison.Changes.Where(c => c.ChangeType is "Added" or "Removed").Select(c => (c.ChangeType, c.ElementId)).OrderBy(c => c.ChangeType, StringComparer.Ordinal).ToList();

    private static async Task<FormVersionResponse> PublishAsync(Store store, FormDesignerHandlers handlers, string reason) =>
        await handlers.PublishAsync(Context, store.Definition.FormDefinitionId, new PublishFormRequest(store.Draft!.DraftRevision, store.Versions.Count, reason), CancellationToken.None);

    private static async Task SaveAsync(Store store, FormDesignerHandlers handlers, string layout) =>
        await handlers.SaveDraftAsync(Context, store.Definition.FormDefinitionId, new SaveDraftRequest(store.Draft?.DraftRevision ?? 0, layout), CancellationToken.None);

    /// <summary>Runs the whole designer lifecycle once and returns the store with its four published versions.</summary>
    private static async Task<(Store Store, FormVersionResponse V1, FormVersionResponse V2, FormVersionResponse V3, FormVersionResponse V4)> LifecycleAsync()
    {
        var store = new Store();
        var handlers = store.Handlers(designAllowed: true);
        await SaveAsync(store, handlers, V1Layout);
        var v1 = await PublishAsync(store, handlers, "v1");
        await SaveAsync(store, handlers, V2Layout);
        var v2 = await PublishAsync(store, handlers, "v2: email added, phone removed");
        await handlers.RestoreToDraftAsync(Context, store.Definition.FormDefinitionId, new RestoreVersionRequest(v1.FormVersionId), CancellationToken.None);
        var v3 = await PublishAsync(store, handlers, "v3: restore v1");
        var v4Layout = JsonNode.Parse(store.Draft!.LayoutJson)!;
        v4Layout["sections"]![0]!["elements"]!.AsArray().Add(JsonNode.Parse(Element(Email, "Email", true)));
        await SaveAsync(store, handlers, v4Layout.ToJsonString());
        var v4 = await PublishAsync(store, handlers, "v4: original email re-added");
        return (store, v1, v2, v3, v4);
    }

    [Fact]
    public async Task TheV1ToV4LifecycleKeepsStableFieldIdentitiesImmutableSnapshotsAndRestoreLineage()
    {
        var (store, v1, v2, v3, v4) = await LifecycleAsync();

        Assert.Equal([1, 2, 3, 4], store.Versions.Select(v => v.VersionNumber));
        Assert.Equal([First, Last, Phone], FieldIds(v1.LayoutJson));
        Assert.Equal([First, Last, Email], FieldIds(v2.LayoutJson));
        Assert.Equal([First, Last, Phone], FieldIds(v3.LayoutJson)); // phone reappears with its original identity
        Assert.Equal([First, Last, Phone, Email], FieldIds(v4.LayoutJson)); // the original email identity, no new field
        Assert.Equal((null, null, v1.FormVersionId, null), (v1.RestoredFromFormVersionId, v2.RestoredFromFormVersionId, v3.RestoredFromFormVersionId, v4.RestoredFromFormVersionId));
        Assert.Equal(v3.LayoutJson, v1.LayoutJson); // restoring reproduces the snapshot exactly
        Assert.Equal(v4.FormVersionId, store.Definition.CurrentFormVersionId);

        // Published snapshots are never rewritten by later drafts or publishes.
        Assert.Equal([v1.LayoutJson, v2.LayoutJson, v3.LayoutJson, v4.LayoutJson], store.Versions.Select(v => v.LayoutJson));
        Assert.Equal(4, store.Catalog.Count); // four logical fields, never a duplicate

        // Every publish is audited with counts only (never labels or values). V1 compares against nothing: its section and
        // three fields are all added (4); later versions keep the section and add or remove one field each.
        var published = store.AuditEvents.Where(e => e.EventAction == AuditActions.FormVersionPublished).Select(e => JsonDocument.Parse(e.EventMetadataJson).RootElement).ToList();
        Assert.Equal([(1, 4, 0), (2, 1, 1), (3, 1, 1), (4, 1, 0)], published.Select(m => (m.GetProperty("versionNumber").GetInt32(), m.GetProperty("addedElements").GetInt32(), m.GetProperty("removedElements").GetInt32())));
        Assert.All(store.AuditEvents, e => Assert.DoesNotContain("Email", e.EventMetadataJson, StringComparison.Ordinal));

        // Each version is registered with the Authority with exactly the fields it places; search approvals bind to that record.
        Assert.Equal([1, 2, 3, 4], store.Registrations.Select(r => r.GetProperty("form_version_number").GetInt32()));
        Assert.Equal([[First, Last, Phone], [First, Last, Email], [First, Last, Phone], [First, Last, Phone, Email]],
            store.Registrations.Select(r => r.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("field_definition_id").GetGuid()).ToList()));
    }

    [Fact]
    public async Task ComparisonsAndThePublishImpactIdentifyChangesByStableIdentity()
    {
        var (store, v1, v2, v3, v4) = await LifecycleAsync();
        var handlers = store.Handlers(designAllowed: true);
        async Task<LayoutComparisonResponse> CompareAsync(FormVersionResponse from, FormVersionResponse to) =>
            (await handlers.CompareVersionsAsync(Context, store.Definition.FormDefinitionId, from.FormVersionId.ToString("D"), to.FormVersionId.ToString("D"), CancellationToken.None)).Comparison;

        Assert.Equal([("Added", Email.ToString("D")), ("Removed", Phone.ToString("D"))], Structural(await CompareAsync(v1, v2)));
        Assert.Equal([("Added", Phone.ToString("D")), ("Removed", Email.ToString("D"))], Structural(await CompareAsync(v2, v3)));
        Assert.False((await CompareAsync(v1, v3)).HasChanges);
        var v3ToV4 = await CompareAsync(v3, v4);
        Assert.Equal([("Added", Email.ToString("D"))], Structural(v3ToV4));
        Assert.Equal((1, 0), (v3ToV4.AddedCount, v3ToV4.RemovedCount));
        Assert.Equal([("Added", Phone.ToString("D"))], Structural(await CompareAsync(v2, v4)));

        // The publish impact of a V1 restore drafted on top of V4: phone kept, email removed; never a data-loss statement.
        await handlers.RestoreToDraftAsync(Context, store.Definition.FormDefinitionId, new RestoreVersionRequest(v1.FormVersionId), CancellationToken.None);
        var impact = await handlers.PreviewPublishImpactAsync(Context, store.Definition.FormDefinitionId, store.Draft!.DraftRevision, CancellationToken.None);
        Assert.True(impact.CanPublish);
        Assert.Equal((4, 5), (impact.CurrentVersionNumber, impact.NextVersionNumber));
        Assert.Equal([("Removed", Email.ToString("D"))], Structural(impact.Comparison));
        Assert.Contains(impact.Notices, n => n.Code == "ELEMENTS_REMOVED" && n.ElementLabels.SequenceEqual(["Email"]));
        Assert.Contains("never deletes or changes stored values", impact.DataStatement, StringComparison.Ordinal);
        Assert.Equal(4, store.Versions.Count); // a preview writes nothing
    }

    [Fact]
    public async Task StalePublishesUnauthorizedCallsAndForeignVersionsChangeNothing()
    {
        var (store, v1, _, _, v4) = await LifecycleAsync();
        var handlers = store.Handlers(designAllowed: true);
        var formId = store.Definition.FormDefinitionId;
        var commits = store.Commits;
        var draftRevision = store.Draft!.DraftRevision;

        // Publishing the draft identical to V4 is refused; stale revisions and version numbers are refused.
        Assert.Equal("NO_DRAFT_CHANGES", (await Assert.ThrowsAsync<BusinessException>(() => handlers.PublishAsync(Context, formId, new PublishFormRequest(draftRevision, 4, "nothing"), CancellationToken.None))).ErrorCode);
        Assert.Equal("DRAFT_REVISION_CONFLICT", (await Assert.ThrowsAsync<BusinessException>(() => handlers.PublishAsync(Context, formId, new PublishFormRequest(draftRevision - 1, 4, "stale draft"), CancellationToken.None))).ErrorCode);
        Assert.Equal("VERSION_CONFLICT", (await Assert.ThrowsAsync<BusinessException>(() => handlers.PublishAsync(Context, formId, new PublishFormRequest(draftRevision, 3, "stale version"), CancellationToken.None))).ErrorCode);
        Assert.Equal("DRAFT_REVISION_CONFLICT", (await Assert.ThrowsAsync<BusinessException>(() => handlers.SaveDraftAsync(Context, formId, new SaveDraftRequest(draftRevision - 1, V1Layout), CancellationToken.None))).ErrorCode);

        // A version of another form, or a field of another form, is never restored or placed.
        Assert.Equal(404, (await Assert.ThrowsAsync<BusinessException>(() => handlers.RestoreToDraftAsync(Context, formId, new RestoreVersionRequest(store.ForeignVersionId), CancellationToken.None))).HttpStatusCode);
        Assert.Equal("LAYOUT_INVALID", (await Assert.ThrowsAsync<BusinessException>(() => handlers.SaveDraftAsync(Context, formId, new SaveDraftRequest(draftRevision, Layout(Element(First, "First name", true), Element(Guid.CreateVersion7(), "Foreign"))), CancellationToken.None))).ErrorCode);

        // Without the design permission: 404 before any store access.
        var denied = store.Handlers(designAllowed: false);
        var scopes = store.ScopesOpened;
        Assert.Equal(404, (await Assert.ThrowsAsync<BusinessException>(() => denied.RestoreToDraftAsync(Context, formId, new RestoreVersionRequest(v1.FormVersionId), CancellationToken.None))).HttpStatusCode);
        Assert.Equal(404, (await Assert.ThrowsAsync<BusinessException>(() => denied.PublishAsync(Context, formId, new PublishFormRequest(draftRevision, 4, "denied"), CancellationToken.None))).HttpStatusCode);
        Assert.Equal(404, (await Assert.ThrowsAsync<BusinessException>(() => denied.CompareVersionsAsync(Context, formId, v1.FormVersionId.ToString("D"), v4.FormVersionId.ToString("D"), CancellationToken.None))).HttpStatusCode);
        Assert.Equal(scopes, store.ScopesOpened);

        Assert.Equal(commits, store.Commits);
        Assert.Equal(4, store.Versions.Count);
        Assert.Equal(v4.FormVersionId, store.Definition.CurrentFormVersionId);
        Assert.Equal(draftRevision, store.Draft!.DraftRevision);
    }

    [Theory]
    [InlineData(VersionBehaviors.Historical, 1, 1, 1, 1)]
    [InlineData(VersionBehaviors.Latest, 1, 4, 4, 4)]
    [InlineData(VersionBehaviors.Hybrid, 1, 1, 4, 4)]
    [InlineData(VersionBehaviors.Historical, 2, 2, 2, 2)]
    [InlineData(VersionBehaviors.Latest, 2, 4, 4, 4)]
    [InlineData(VersionBehaviors.Hybrid, 2, 2, 4, 4)]
    public async Task EachBehaviourSelectsItsLayoutsOverTheFourVersionHistory(string behavior, int createdNumber, int viewNumber, int editNumber, int viewEditNumber)
    {
        var (store, _, _, _, _) = await LifecycleAsync();
        store.Definition.VersionBehavior = behavior;
        var created = store.Versions.Single(v => v.VersionNumber == createdNumber).FormVersionId;
        Task<FormVersion?> Find(Guid id) => Task.FromResult(store.Versions.FirstOrDefault(v => v.FormVersionId == id) ?? (id == store.ForeignVersionId ? store.Foreign : null));

        var view = await RecordLayoutSelection.SelectAsync(store.Definition, created, RecordLayoutPurpose.View, Find);
        var edit = await RecordLayoutSelection.SelectAsync(store.Definition, created, RecordLayoutPurpose.Edit, Find);
        Assert.Equal((viewNumber, viewEditNumber, editNumber, createdNumber), (view.Layout.VersionNumber, view.EditLayout!.VersionNumber, edit.Layout.VersionNumber, view.CreatedVersionNumber));

        // A current pointer to another form's version, or none, fails an edit closed unless the behaviour edits with the created version.
        foreach (var pointer in new Guid?[] { store.ForeignVersionId, null })
        {
            store.Definition.CurrentFormVersionId = pointer;
            if (behavior == VersionBehaviors.Historical)
            {
                Assert.Equal(createdNumber, (await RecordLayoutSelection.SelectAsync(store.Definition, created, RecordLayoutPurpose.Edit, Find)).Layout.VersionNumber);
            }
            else
            {
                var error = await Assert.ThrowsAsync<BusinessException>(() => RecordLayoutSelection.SelectAsync(store.Definition, created, RecordLayoutPurpose.Edit, Find));
                Assert.Equal((409, "EDIT_LAYOUT_UNAVAILABLE"), (error.HttpStatusCode, error.ErrorCode));
                Assert.Equal(createdNumber, (await RecordLayoutSelection.SelectAsync(store.Definition, created, RecordLayoutPurpose.View, Find)).Layout.VersionNumber);
            }
        }
    }

    /// <summary>
    /// A registration the Authority refuses (here: its gate closed by a concurrent command) leaves the version published and reported as
    /// not registered, with a FORM_VERSION_AUTHORITY_REGISTRATION_FAILED audit event whose outcome the database accepts
    /// (ck_audit_events_outcome: Success, Denied or Failed). Found during C-188 verification: the handler wrote "Failure", the insert
    /// failed and the publish answered 503 STORE_UNAVAILABLE without its audit evidence.
    /// </summary>
    [Fact]
    public async Task ARefusedAuthorityRegistrationIsReportedAndAuditedWithAnAllowedOutcome()
    {
        var store = new Store { RegistrationFailure = "AUTHORITY_NOT_READY" };
        var handlers = store.Handlers(designAllowed: true);
        await SaveAsync(store, handlers, V1Layout);
        var v1 = await PublishAsync(store, handlers, "v1 while the Authority is busy");

        Assert.Equal(1, v1.VersionNumber);
        Assert.False(v1.AuthorityRegistered);
        Assert.True(v1.AuthorityRegistrationPending); // C-188 BF-1: staged, so the Authority keeps field search on the form closed
        Assert.NotNull(store.Pending);
        var failed = Assert.Single(store.AuditEvents, e => e.EventAction == AuditActions.FormVersionAuthorityRegistrationFailed);
        Assert.Equal("Failed", failed.EventOutcome);
        Assert.Equal("AUTHORITY_NOT_READY", JsonDocument.Parse(failed.EventMetadataJson).RootElement.GetProperty("reason").GetString());
        Assert.All(store.AuditEvents, e => Assert.Contains(e.EventOutcome, AllowedOutcomes));
    }

    /// <summary>
    /// C-188 BF-1: the Authority stages the version (under the form's publication lock) before the business adds and commits it, and the
    /// publication is resolved only after the commit; each attempt uses a fresh idempotency key.
    /// </summary>
    [Fact]
    public async Task ThePublicationIsStagedBeforeTheCommitAndResolvedAfterIt()
    {
        var store = new Store();
        var handlers = store.Handlers(designAllowed: true);
        await SaveAsync(store, handlers, V1Layout);
        store.Steps.Clear();
        var v1 = await PublishAsync(store, handlers, "v1");

        Assert.Equal(["lock", "prepare", "add-version", "commit", "resolve:Registered", "commit"], store.Steps); // the last commit is the registration audit
        Assert.True(v1.AuthorityRegistered);
        Assert.False(v1.AuthorityRegistrationPending);
        Assert.Null(store.Pending);
        Assert.Equal(1, store.AuthorityRegistered);
        var resolution = Assert.Single(store.Resolutions);
        Assert.Equal(v1.FormVersionId, resolution.GetProperty("form_version_id").GetGuid());
        Assert.Equal(store.Registrations.Single().GetProperty("layout_digest_hex").GetString(), resolution.GetProperty("layout_digest_hex").GetString());
        Assert.Equal(store.IdempotencyKeys.Count, store.IdempotencyKeys.Distinct().Count());
        var registered = Assert.Single(store.AuditEvents, e => e.EventAction == AuditActions.FormVersionAuthorityRegistered);
        Assert.Equal("Publish", JsonDocument.Parse(registered.EventMetadataJson).RootElement.GetProperty("registrationSource").GetString());
    }

    /// <summary>C-188 BF-1: when the Authority refuses to stage the version, nothing is published, committed or registered.</summary>
    [Fact]
    public async Task ARefusedPrepareFailsThePublishAndPublishesNothing()
    {
        var store = new Store { PrepareFailure = "FORM_VERSION_UNTRUSTED" };
        var handlers = store.Handlers(designAllowed: true);
        await SaveAsync(store, handlers, V1Layout);
        var commits = store.Commits;

        var error = await Assert.ThrowsAsync<BusinessException>(() => PublishAsync(store, handlers, "v1"));
        Assert.Equal(("FORM_VERSION_UNTRUSTED", 422), (error.ErrorCode, error.HttpStatusCode));
        Assert.Empty(store.Versions);
        Assert.Equal(commits, store.Commits);
        Assert.Null(store.Definition.CurrentFormVersionId);
        Assert.Empty(store.Resolutions);
        Assert.DoesNotContain(store.AuditEvents, e => e.EventAction == AuditActions.FormVersionPublished);
    }

    /// <summary>C-188 BF-1: a busy Authority (a concurrent security command) is retried with fresh keys; if it stays busy nothing is published.</summary>
    [Fact]
    public async Task ABusyAuthorityIsRetriedWithFreshKeysAndNeverLeadsToAnUnstagedPublication()
    {
        var store = new Store { PrepareBusyAttempts = 2 };
        var handlers = store.Handlers(designAllowed: true);
        await SaveAsync(store, handlers, V1Layout);
        var v1 = await PublishAsync(store, handlers, "v1 after two busy refusals");
        Assert.True(v1.AuthorityRegistered);
        var prepares = store.IdempotencyKeys.Where(k => k.StartsWith("form-version-prepare:", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, prepares.Count);
        Assert.Equal(3, prepares.Distinct().Count()); // a refused key would only replay its refusal

        var busy = new Store { PrepareBusyAttempts = 10 };
        var busyHandlers = busy.Handlers(designAllowed: true);
        await SaveAsync(busy, busyHandlers, V1Layout);
        var error = await Assert.ThrowsAsync<BusinessException>(() => PublishAsync(busy, busyHandlers, "v1 while the Authority stays busy"));
        Assert.Equal(("AUTHORITY_UNAVAILABLE", 503), (error.ErrorCode, error.HttpStatusCode));
        Assert.Empty(busy.Versions);
        Assert.Null(busy.Pending);
    }

    /// <summary>
    /// C-188 BF-1 recovery without a new publication: a staged version this store committed is registered (from the Authority's staged
    /// manifest; same id, number and digest), then the form reports Registered; a second recovery changes nothing.
    /// </summary>
    [Fact]
    public async Task RecoveryRegistersACommittedStagedVersionAndIsIdempotent()
    {
        var store = new Store { RegistrationFailure = "AUTHORITY_NOT_READY", NormalizeStoredLayouts = true }; // the stored text differs from the published text, as with jsonb
        var handlers = store.Handlers(designAllowed: true);
        await SaveAsync(store, handlers, V1Layout);
        var v1 = await PublishAsync(store, handlers, "v1");
        Assert.NotEqual(store.Registrations.Single().GetProperty("layout_digest_hex").GetString(), Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(store.Versions.Single().LayoutJson))));
        Assert.Equal("Pending", (await handlers.GetAuthorityRegistrationAsync(Context, store.Definition.FormDefinitionId, CancellationToken.None)).RegistrationState);

        store.RegistrationFailure = null;
        store.Steps.Clear();
        var recovered = await handlers.RecoverAuthorityRegistrationAsync(Context, store.Definition.FormDefinitionId, CancellationToken.None);
        Assert.Equal(("Registered", "Registered", 1, 1, (int?)null), (recovered.Outcome, recovered.RegistrationState, recovered.CurrentVersionNumber, recovered.RegisteredVersionNumber, recovered.PendingVersionNumber));
        Assert.Equal("lock", store.Steps[0]); // decided under the publication lock
        Assert.Equal(v1.FormVersionId, Assert.Single(store.Resolutions).GetProperty("form_version_id").GetGuid());
        Assert.Contains(store.AuditEvents, e => e.EventAction == AuditActions.FormVersionAuthorityRegistered && JsonDocument.Parse(e.EventMetadataJson).RootElement.GetProperty("registrationSource").GetString() == "Recovery");

        var again = await handlers.RecoverAuthorityRegistrationAsync(Context, store.Definition.FormDefinitionId, CancellationToken.None);
        Assert.Equal(("AlreadyRegistered", "Registered"), (again.Outcome, again.RegistrationState));
        Assert.Single(store.Resolutions); // nothing sent
    }

    /// <summary>C-188 BF-1: a staged version this store never committed is withdrawn (abandoned); the current registered version applies again.</summary>
    [Fact]
    public async Task RecoveryWithdrawsAStagedVersionThatWasNeverCommitted()
    {
        var store = new Store();
        var handlers = store.Handlers(designAllowed: true);
        await SaveAsync(store, handlers, V1Layout);
        await PublishAsync(store, handlers, "v1");
        var neverCommitted = Guid.CreateVersion7();
        store.Pending = new AuthorityPendingFormVersionPublication(neverCommitted, 2, new string('a', 64), DateTimeOffset.UtcNow); // e.g. the commit failed after the prepare

        var recovered = await handlers.RecoverAuthorityRegistrationAsync(Context, store.Definition.FormDefinitionId, CancellationToken.None);
        Assert.Equal(("Abandoned", "Registered", 1), (recovered.Outcome, recovered.RegistrationState, recovered.RegisteredVersionNumber));
        var resolution = store.Resolutions.Last();
        Assert.Equal(("Abandoned", neverCommitted), (resolution.GetProperty("resolution").GetString(), resolution.GetProperty("form_version_id").GetGuid()));
        Assert.Contains(store.AuditEvents, e => e.EventAction == AuditActions.FormVersionAuthorityPublicationAbandoned);
        Assert.Single(store.Versions);
    }

    /// <summary>
    /// C-188 BF-1: recovery never registers a stored version whose layout no longer has the staged digest (fail closed: the form stays
    /// pending), and a current version the Authority never staged is reported as not recoverable (publish again; OD-3) - nothing is sent.
    /// </summary>
    [Fact]
    public async Task RecoveryRefusesAMismatchedStoredVersionAndNeverRegistersAnUnstagedOne()
    {
        var store = new Store { RegistrationFailure = "AUTHORITY_NOT_READY" };
        var handlers = store.Handlers(designAllowed: true);
        await SaveAsync(store, handlers, V1Layout);
        var v1 = await PublishAsync(store, handlers, "v1");
        store.RegistrationFailure = null;
        store.Pending = store.Pending! with { LayoutDigestHex = new string('b', 64) }; // the stored layout no longer matches what was staged
        var refused = await handlers.RecoverAuthorityRegistrationAsync(Context, store.Definition.FormDefinitionId, CancellationToken.None);
        Assert.Equal(("Refused", "STAGED_VERSION_MISMATCH", "Pending"), (refused.Outcome, refused.FailureReason, refused.RegistrationState));
        Assert.Empty(store.Resolutions);
        Assert.NotNull(store.Pending);

        store.Pending = null; // a version published before the safeguard: the Authority never staged it
        var legacy = await handlers.RecoverAuthorityRegistrationAsync(Context, store.Definition.FormDefinitionId, CancellationToken.None);
        Assert.Equal(("NotRecoverable", "NotRegistered"), (legacy.Outcome, legacy.RegistrationState));
        Assert.Empty(store.Resolutions);
        Assert.Equal(v1.FormVersionId, store.Definition.CurrentFormVersionId);
    }

    // ---------------------------------------------------------------- in-memory store

    private sealed class Store
    {
        public Store()
        {
            Catalog =
            [
                Field(First, "first_name", "First name", "Confidential", "ApplicationEncrypted"),
                Field(Last, "last_name", "Last name", "Confidential", "ApplicationEncrypted"),
                Field(Phone, "phone_number", "Phone", "Internal", "Standard"),
                Field(Email, "email_address", "Email", "Confidential", "ApplicationEncrypted"),
            ];
            Foreign = new FormVersion { FormVersionId = ForeignVersionId, FormDefinitionId = Guid.CreateVersion7(), VersionNumber = 9, LayoutJson = V1Layout };
        }

        public FormDefinition Definition { get; } = new() { FormDefinitionId = Guid.CreateVersion7(), TenantId = Org, FormDefinitionName = "Intake", FormDefinitionRevision = 1 };

        public List<FieldDefinition> Catalog { get; }

        public List<FormVersion> Versions { get; } = [];

        public Guid ForeignVersionId { get; } = Guid.CreateVersion7();

        public FormVersion Foreign { get; }

        public FormDraft? Draft { get; private set; }

        public List<AuditEvent> AuditEvents { get; } = [];

        public List<JsonElement> Registrations { get; } = [];

        public int ScopesOpened { get; private set; }

        public int Commits { get; private set; }

        private FieldDefinition Field(Guid id, string key, string name, string classification, string protection) =>
            new() { FieldDefinitionId = id, FormDefinitionId = Definition.FormDefinitionId, FieldKey = key, FieldDisplayName = name, FieldDataType = "String", FieldClassification = classification, FieldProtectionMode = protection };

        public FormDesignerHandlers Handlers(bool designAllowed)
        {
            var authority = Stubs.Create<IAuthorizationAuthorityClient>((method, args) => method.Name switch
            {
                nameof(IAuthorizationAuthorityClient.CheckPermissionAsync) => Task.FromResult(new PermissionDecision(designAllowed, null, 9, 9, null, Guid.Empty)),
                nameof(IAuthorizationAuthorityClient.DiscoverAuthorizedRecordsAsync) => Task.FromResult(new RecordDiscoveryPage([], 9, null)),
                nameof(IAuthorizationAuthorityClient.SubmitSecurityCommandAsync) => Submit((SecurityCommandRequest)args[1]!, (string)args[2]!),
                _ => throw new NotSupportedException(method.Name),
            });
            var fieldSearch = Stubs.Create<IFieldSearchPolicyAuthority>((method, _) => method.Name switch
            {
                nameof(IFieldSearchPolicyAuthority.GetFieldSearchCapabilitiesAsync) => Task.FromResult(new AuthorityFieldSearchCapabilities(Definition.FormDefinitionId, true, 1, 9, [],
                    new AuthorityFieldSearchConfiguration(false, true, string.Empty, AuthorityRegistered, [], PendingPublication: Pending), Guid.Empty, Pending is not null)),
                _ => throw new NotSupportedException(method.Name),
            });
            var store = Stubs.Create<IBusinessStore>((_, _) =>
            {
                ScopesOpened++;
                return Task.FromResult(Scope());
            });
            return new FormDesignerHandlers(store, authority, new AuthorityCommandAwaiter(authority, TimeProvider.System), TimeProvider.System, null!, fieldSearch);
        }

        /// <summary>When set, the Authority refuses the resolution of a publication as registered with this error code (e.g. a gate closed by a concurrent command).</summary>
        public string? RegistrationFailure { get; set; }

        /// <summary>C-188 BF-1: when set, the Authority refuses to stage a version with this error code (non-retryable, 422).</summary>
        public string? PrepareFailure { get; set; }

        /// <summary>C-188 BF-1: the number of prepare attempts refused with AUTHORITY_NOT_READY (503, retryable) before one is accepted.</summary>
        public int PrepareBusyAttempts { get; set; }

        /// <summary>C-188 BF-1: the Authority's view - its pending publication and latest registered version number (kept as the commands run).</summary>
        public AuthorityPendingFormVersionPublication? Pending { get; set; }

        public int? AuthorityRegistered { get; set; }

        /// <summary>C-188 BF-1: resolutions sent (payloads) and every idempotency key used, in order.</summary>
        public List<JsonElement> Resolutions { get; } = [];

        public List<string> IdempotencyKeys { get; } = [];

        /// <summary>C-188 BF-1: the order of publication-relevant operations (lock, prepare, add-version, commit, resolve).</summary>
        public List<string> Steps { get; } = [];

        private Task<SecurityCommandResult> Submit(SecurityCommandRequest request, string idempotencyKey)
        {
            IdempotencyKeys.Add(idempotencyKey);
            var payload = request.CommandPayload;
            switch (request.CommandKind)
            {
                case SecurityCommandKind.PrepareFormVersionPublication:
                    Steps.Add("prepare");
                    if (PrepareBusyAttempts > 0)
                    {
                        PrepareBusyAttempts--;
                        throw new AuthorityClientException("AUTHORITY_NOT_READY", 503, true);
                    }

                    if (PrepareFailure is { } refused)
                    {
                        throw new AuthorityClientException(refused, 422, false);
                    }

                    Registrations.Add(payload.Clone()); // the staged manifest the Authority will register
                    Pending = new AuthorityPendingFormVersionPublication(payload.GetProperty("form_version_id").GetGuid(), payload.GetProperty("form_version_number").GetInt32(), payload.GetProperty("layout_digest_hex").GetString()!, DateTimeOffset.UtcNow);
                    break;
                case SecurityCommandKind.ResolveFormVersionPublication:
                    Steps.Add("resolve:" + payload.GetProperty("resolution").GetString());
                    if (RegistrationFailure is { } code && payload.GetProperty("resolution").GetString() == "Registered")
                    {
                        throw new AuthorityClientException(code, 503, true);
                    }

                    Assert.NotNull(Pending);
                    Assert.Equal((Pending!.FormVersionId, Pending.FormVersionNumber, Pending.LayoutDigestHex),
                        (payload.GetProperty("form_version_id").GetGuid(), payload.GetProperty("form_version_number").GetInt32(), payload.GetProperty("layout_digest_hex").GetString()));
                    Resolutions.Add(payload.Clone());
                    if (payload.GetProperty("resolution").GetString() == "Registered")
                    {
                        AuthorityRegistered = Pending.FormVersionNumber;
                    }

                    Pending = null;
                    break;
                default:
                    throw new NotSupportedException(request.CommandKind.ToString());
            }

            return Task.FromResult(new SecurityCommandResult(Guid.CreateVersion7(), SecurityCommandStatus.Effective, 10, 10, "Open", null, Guid.Empty));
        }

        private IBusinessScope Scope() => Stubs.Create<IBusinessScope>((method, args) => method.Name switch
        {
            nameof(IBusinessScope.FindFormDefinitionAsync) => (object?)Task.FromResult<FormDefinition?>((Guid)args[1]! == Definition.FormDefinitionId ? Definition : null),
            nameof(IBusinessScope.FindDraftAsync) => Task.FromResult(Draft),
            nameof(IBusinessScope.AddDraft) => SetDraft((FormDraft)args[0]!),
            nameof(IBusinessScope.GetFormVersionsAsync) => Task.FromResult(Versions.ToList()),
            nameof(IBusinessScope.FindFormVersionAsync) => Task.FromResult((Guid)args[0]! == ForeignVersionId ? Foreign : Versions.FirstOrDefault(v => v.FormVersionId == (Guid)args[0]!)),
            nameof(IBusinessScope.AddFormVersion) => AddVersion((FormVersion)args[0]!),
            nameof(IBusinessScope.GetFieldDefinitionsAsync) => Task.FromResult(Catalog.ToList()),
            nameof(IBusinessScope.GetFieldOptionsAsync) => Task.FromResult(new List<FieldOption>()),
            nameof(IBusinessScope.GetGroupDefinitionsAsync) => Task.FromResult(new List<GroupDefinition>()),
            nameof(IBusinessScope.GetRelationshipDefinitionsAsync) => Task.FromResult(new List<FormRelationshipDefinition>()),
            nameof(IBusinessScope.GetFormRecordsAsync) => Task.FromResult(new List<Domain.Features.FormRecords.FormRecord>()),
            nameof(IBusinessScope.AddAuditEvent) => Audit((AuditEvent)args[0]!),
            nameof(IBusinessScope.CommitAsync) => Commit(),
            nameof(IBusinessScope.LockFormPublicationAsync) => Step("lock"),
            nameof(IAsyncDisposable.DisposeAsync) => ValueTask.CompletedTask,
            _ => throw new NotSupportedException(method.Name),
        });

        private Task Step(string step)
        {
            Steps.Add(step);
            return Task.CompletedTask;
        }

        private object? SetDraft(FormDraft draft)
        {
            Draft = draft;
            return null;
        }

        /// <summary>C-188 BF-1: store layouts the way a jsonb column returns them (not byte for byte as published).</summary>
        public bool NormalizeStoredLayouts { get; set; }

        private object? AddVersion(FormVersion version)
        {
            Steps.Add("add-version");
            if (NormalizeStoredLayouts)
            {
                version.LayoutJson = JsonNode.Parse(version.LayoutJson)!.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            }

            Versions.Add(version);
            return null;
        }

        private object? Audit(AuditEvent auditEvent)
        {
            AuditEvents.Add(auditEvent);
            return null;
        }

        private Task Commit()
        {
            Steps.Add("commit");
            Commits++;
            return Task.CompletedTask;
        }
    }

    public class Stub<T> : DispatchProxy
        where T : class
    {
        public Func<MethodInfo, object?[], object?> Handler { get; set; } = (method, _) => throw new NotSupportedException(method.Name);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args ?? []);
    }

    private static class Stubs
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
