using System.Reflection;
using System.Text.Json;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.FormDefinitions.Layout;
using EnterpriseForms.Application.Features.FormDesigner;
using EnterpriseForms.Application.Features.FormRecords.Projection;
using EnterpriseForms.Contracts.Features;
using EnterpriseForms.Domain.Features.Audit;
using EnterpriseForms.Domain.Features.FormDefinitions;
using EnterpriseForms.Domain.Features.FormRecords;
using EnterpriseForms.Domain.Features.RelatedForms;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// Phase C9 M1: version behaviour (CLAUDE.md §3). The selection rule for every behaviour and purpose; the edit layout is
/// never substituted (missing, foreign or unresolvable versions fail closed); the D9-5 stale-layout guard; and the
/// Designer setting (validation before any access, the Authority design permission, revision CAS, audit in the same commit,
/// idempotent resend) with its impact preview counted only over discoverable records. The real-stack proof of every
/// record write path is FormVersionBehaviorStackTests in Stage B.
/// </summary>
public sealed class FormVersionBehaviorTests
{
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly Guid Me = Guid.CreateVersion7();
    private static readonly RequestContext Context = new(Org, Me, "https://issuer.invalid", "subject", "Tester", "assertion", Guid.CreateVersion7());
    private static readonly Guid Created = Guid.CreateVersion7();
    private static readonly Guid Current = Guid.CreateVersion7();

    // ---- the rule ----

    [Theory]
    [InlineData(VersionBehaviors.Historical, RecordLayoutPurpose.View, "created")]
    [InlineData(VersionBehaviors.Historical, RecordLayoutPurpose.Edit, "created")]
    [InlineData(VersionBehaviors.Latest, RecordLayoutPurpose.View, "current")]
    [InlineData(VersionBehaviors.Latest, RecordLayoutPurpose.Edit, "current")]
    [InlineData(VersionBehaviors.Hybrid, RecordLayoutPurpose.View, "created")]
    [InlineData(VersionBehaviors.Hybrid, RecordLayoutPurpose.Edit, "current")]
    public void EachBehaviourSelectsTheSpecifiedVersionForViewingAndEditing(string behavior, RecordLayoutPurpose purpose, string expected) =>
        Assert.Equal(expected == "created" ? Created : Current, FormVersionResolution.Resolve(behavior, purpose, Created, Current));

    [Theory]
    [InlineData(VersionBehaviors.Latest)]
    [InlineData(VersionBehaviors.Hybrid)]
    public void WithoutACurrentVersionEditingHasNoLayoutButViewingFallsBackToTheCreatedVersion(string behavior)
    {
        Assert.Null(FormVersionResolution.Resolve(behavior, RecordLayoutPurpose.Edit, Created, null));
        Assert.Equal(Created, FormVersionResolution.Resolve(behavior, RecordLayoutPurpose.View, Created, null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("Newest")]
    public void AnUnknownBehaviourNeverResolvesAnEditLayout(string? behavior)
    {
        Assert.Null(FormVersionResolution.Resolve(behavior, RecordLayoutPurpose.Edit, Created, Current));
        Assert.Equal(Current, FormVersionResolution.Resolve(behavior, RecordLayoutPurpose.View, Created, Current));
        Assert.False(VersionBehaviors.IsKnown(behavior));
    }

    [Fact]
    public void LatestIsTheDefaultForNewForms() => Assert.Equal(VersionBehaviors.Latest, new FormDefinition().VersionBehavior);

    // ---- selection with stored versions ----

    [Theory]
    [InlineData(VersionBehaviors.Historical, 1, 1)]
    [InlineData(VersionBehaviors.Latest, 3, 3)]
    [InlineData(VersionBehaviors.Hybrid, 1, 3)]
    public async Task SelectionLoadsTheViewAndEditLayoutsOfTheForm(string behavior, int viewNumber, int editNumber)
    {
        var form = Form(behavior);
        var view = await RecordLayoutSelection.SelectAsync(form.Definition, Created, RecordLayoutPurpose.View, form.Find);
        var edit = await RecordLayoutSelection.SelectAsync(form.Definition, Created, RecordLayoutPurpose.Edit, form.Find);
        Assert.Equal(viewNumber, view.Layout.VersionNumber);
        Assert.Equal(editNumber, view.EditLayout!.VersionNumber);
        Assert.Equal(editNumber, edit.Layout.VersionNumber);
        Assert.Same(edit.Layout, edit.EditLayout);
        Assert.Equal(1, view.CreatedVersionNumber);
    }

    [Theory]
    [InlineData(VersionBehaviors.Latest)]
    [InlineData(VersionBehaviors.Hybrid)]
    public async Task AMissingCurrentVersionFailsAnEditClosedAndNeverSubstitutesTheCreatedVersion(string behavior)
    {
        var form = Form(behavior);
        form.Versions.Remove(Current);
        var error = await Assert.ThrowsAsync<BusinessException>(() => RecordLayoutSelection.SelectAsync(form.Definition, Created, RecordLayoutPurpose.Edit, form.Find));
        Assert.Equal(409, error.HttpStatusCode);
        Assert.Equal("EDIT_LAYOUT_UNAVAILABLE", error.ErrorCode);
        var view = await RecordLayoutSelection.SelectAsync(form.Definition, Created, RecordLayoutPurpose.View, form.Find);
        Assert.Equal(1, view.Layout.VersionNumber);
        Assert.Null(view.EditLayout);
    }

    [Fact]
    public async Task ACurrentPointerToAnotherFormsVersionIsNeverUsed()
    {
        var form = Form(VersionBehaviors.Latest);
        form.Versions[Current].FormDefinitionId = Guid.CreateVersion7(); // the business row points at a foreign version
        var error = await Assert.ThrowsAsync<BusinessException>(() => RecordLayoutSelection.SelectAsync(form.Definition, Created, RecordLayoutPurpose.Edit, form.Find));
        Assert.Equal("EDIT_LAYOUT_UNAVAILABLE", error.ErrorCode);
        Assert.Equal(1, (await RecordLayoutSelection.SelectAsync(form.Definition, Created, RecordLayoutPurpose.View, form.Find)).Layout.VersionNumber);
    }

    [Fact]
    public async Task AHistoricalRecordWhoseCreatedVersionIsMissingCannotBeEdited()
    {
        var form = Form(VersionBehaviors.Historical);
        form.Versions.Remove(Created);
        Assert.Equal("EDIT_LAYOUT_UNAVAILABLE", (await Assert.ThrowsAsync<BusinessException>(() => RecordLayoutSelection.SelectAsync(form.Definition, Created, RecordLayoutPurpose.Edit, form.Find))).ErrorCode);
    }

    [Fact]
    public async Task AnUnknownStoredBehaviourCannotBeEdited()
    {
        var form = Form("Newest");
        Assert.Equal("EDIT_LAYOUT_UNAVAILABLE", (await Assert.ThrowsAsync<BusinessException>(() => RecordLayoutSelection.SelectAsync(form.Definition, Created, RecordLayoutPurpose.Edit, form.Find))).ErrorCode);
    }

    // ---- D9-5 stale edit layout ----

    [Fact]
    public void AStaleExpectedEditLayoutIsAConflictAndTheCurrentOneOrNoneIsAccepted()
    {
        var verified = Verified(RecordLayoutPurpose.Edit, Current);
        EditLayoutGuard.Require(verified, Current);
        EditLayoutGuard.Require(verified, null);
        var error = Assert.Throws<BusinessException>(() => EditLayoutGuard.Require(verified, Created));
        Assert.Equal(409, error.HttpStatusCode);
        Assert.Equal("EDIT_LAYOUT_CHANGED", error.ErrorCode);
    }

    [Fact]
    public void TheGuardRefusesAViewReadSoAWriteCannotValidateAgainstTheViewLayout() =>
        Assert.Throws<InvalidOperationException>(() => EditLayoutGuard.Require(Verified(RecordLayoutPurpose.View, Created), Created));

    // ---- the Designer setting ----

    [Theory]
    [InlineData("latest")]
    [InlineData("Newest")]
    [InlineData("")]
    public async Task AnUnknownBehaviourIsRejectedBeforeTheAuthorityOrTheStoreIsAsked(string behavior)
    {
        var fake = new FakeForm();
        var error = await Assert.ThrowsAsync<BusinessException>(() => fake.Handlers(designAllowed: true).ChangeVersionBehaviorAsync(Context, fake.Definition.FormDefinitionId, new ChangeVersionBehaviorRequest(behavior, 1, "a valid reason"), CancellationToken.None));
        Assert.Equal(422, error.HttpStatusCode);
        Assert.Equal(0, fake.AuthorityChecks);
        Assert.Equal(0, fake.ScopesOpened);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ab  ")]
    [InlineData("two\nlines")]
    public async Task AReasonOutsideTheRuleIsRejectedBeforeAnyAccess(string reason)
    {
        var fake = new FakeForm();
        var error = await Assert.ThrowsAsync<BusinessException>(() => fake.Handlers(designAllowed: true).ChangeVersionBehaviorAsync(Context, fake.Definition.FormDefinitionId, new ChangeVersionBehaviorRequest(VersionBehaviors.Hybrid, 1, reason), CancellationToken.None));
        Assert.Equal(422, error.HttpStatusCode);
        Assert.Equal(0, fake.AuthorityChecks);
        Assert.Equal(VersionBehaviors.Latest, fake.Definition.VersionBehavior);
    }

    [Fact]
    public async Task AReasonOfMoreThan500CharactersIsRejected()
    {
        var fake = new FakeForm();
        await Assert.ThrowsAsync<BusinessException>(() => fake.Handlers(designAllowed: true).ChangeVersionBehaviorAsync(Context, fake.Definition.FormDefinitionId, new ChangeVersionBehaviorRequest(VersionBehaviors.Hybrid, 1, new string('r', 501)), CancellationToken.None));
        var accepted = await fake.Handlers(designAllowed: true).ChangeVersionBehaviorAsync(Context, fake.Definition.FormDefinitionId, new ChangeVersionBehaviorRequest(VersionBehaviors.Hybrid, 1, new string('r', 500)), CancellationToken.None);
        Assert.Equal(VersionBehaviors.Hybrid, accepted.Summary.VersionBehavior);
    }

    [Fact]
    public async Task WithoutTheDesignPermissionTheFormIsNotFoundAndNothingIsRead()
    {
        var fake = new FakeForm();
        var error = await Assert.ThrowsAsync<BusinessException>(() => fake.Handlers(designAllowed: false).ChangeVersionBehaviorAsync(Context, fake.Definition.FormDefinitionId, new ChangeVersionBehaviorRequest(VersionBehaviors.Historical, 1, "keep records on their version"), CancellationToken.None));
        Assert.Equal(404, error.HttpStatusCode);
        Assert.Equal(0, fake.ScopesOpened);
        Assert.Equal(VersionBehaviors.Latest, fake.Definition.VersionBehavior);
    }

    [Fact]
    public async Task AChangeUpdatesOnlyTheBehaviourAndRevisionAndIsAuditedInTheSameCommit()
    {
        var fake = new FakeForm();
        var result = await fake.Handlers(designAllowed: true).ChangeVersionBehaviorAsync(Context, fake.Definition.FormDefinitionId, new ChangeVersionBehaviorRequest(VersionBehaviors.Historical, 1, "  keep records on their version  "), CancellationToken.None);

        Assert.Equal(VersionBehaviors.Historical, result.Summary.VersionBehavior);
        Assert.Equal(2, result.Summary.FormDefinitionRevision);
        Assert.Equal(Current, fake.Definition.CurrentFormVersionId);
        Assert.Equal("Original name", fake.Definition.FormDefinitionName);
        Assert.Equal(1, fake.Commits);
        var audit = Assert.Single(fake.AuditEvents);
        Assert.Equal(AuditActions.FormVersionBehaviorChanged, audit.EventAction);
        Assert.Equal(fake.Definition.FormDefinitionId, audit.FormDefinitionId);
        using var metadata = JsonDocument.Parse(audit.EventMetadataJson);
        Assert.Equal(VersionBehaviors.Latest, metadata.RootElement.GetProperty("previousVersionBehavior").GetString());
        Assert.Equal(VersionBehaviors.Historical, metadata.RootElement.GetProperty("versionBehavior").GetString());
        Assert.Equal(2, metadata.RootElement.GetProperty("formDefinitionRevision").GetInt64());
        Assert.Equal("keep records on their version", metadata.RootElement.GetProperty("changeReason").GetString());
    }

    [Fact]
    public async Task AStaleRevisionIsAConflictAndChangesNothing()
    {
        var fake = new FakeForm();
        fake.Definition.FormDefinitionRevision = 4;
        var error = await Assert.ThrowsAsync<BusinessException>(() => fake.Handlers(designAllowed: true).ChangeVersionBehaviorAsync(Context, fake.Definition.FormDefinitionId, new ChangeVersionBehaviorRequest(VersionBehaviors.Hybrid, 3, "edit with the current layout"), CancellationToken.None));
        Assert.Equal(409, error.HttpStatusCode);
        Assert.Equal("FORM_DEFINITION_MODIFIED_CONCURRENTLY", error.ErrorCode);
        Assert.Equal(VersionBehaviors.Latest, fake.Definition.VersionBehavior);
        Assert.Equal(4, fake.Definition.FormDefinitionRevision);
        Assert.Equal(0, fake.Commits);
        Assert.Empty(fake.AuditEvents);
    }

    [Fact]
    public async Task ResendingTheCurrentBehaviourIsNotASecondChange()
    {
        var fake = new FakeForm();
        fake.Definition.VersionBehavior = VersionBehaviors.Hybrid;
        fake.Definition.FormDefinitionRevision = 2;
        var result = await fake.Handlers(designAllowed: true).ChangeVersionBehaviorAsync(Context, fake.Definition.FormDefinitionId, new ChangeVersionBehaviorRequest(VersionBehaviors.Hybrid, 1, "edit with the current layout"), CancellationToken.None);
        Assert.Equal(2, result.Summary.FormDefinitionRevision);
        Assert.Equal(0, fake.Commits);
        Assert.Empty(fake.AuditEvents);
    }

    [Fact]
    public async Task ADeletedFormCannotChange()
    {
        var fake = new FakeForm();
        fake.Definition.LifecycleStatus = "Deleted";
        Assert.Equal(404, (await Assert.ThrowsAsync<BusinessException>(() => fake.Handlers(designAllowed: true).ChangeVersionBehaviorAsync(Context, fake.Definition.FormDefinitionId, new ChangeVersionBehaviorRequest(VersionBehaviors.Hybrid, 1, "edit with the current layout"), CancellationToken.None))).HttpStatusCode);
        Assert.Equal(0, fake.Commits);
    }

    [Fact]
    public void TheAuditCatalogDescribesTheEventWithoutShowingTheReason()
    {
        var entry = Application.Features.Audit.AuditEventCatalog.Find(AuditActions.FormVersionBehaviorChanged);
        Assert.NotNull(entry);
        Assert.Contains(entry.Shown, rule => rule.Key == "versionBehavior");
        Assert.Contains(entry.Withheld, rule => rule.Key == "changeReason");
    }

    // ---- impact preview ----

    [Fact]
    public async Task TheImpactCountsOnlyDiscoverableRecordsOfTheFormAndNeverFieldValues()
    {
        var fake = new FakeForm();
        var v1Draft = fake.AddRecord(Created, "Draft");
        var v1Submitted = fake.AddRecord(Created, "Submitted");
        var v3 = fake.AddRecord(Current, "Draft");
        var hidden = fake.AddRecord(Created, "Draft"); // not discoverable: never counted
        var otherForm = fake.AddRecord(Created, "Draft", Guid.CreateVersion7());
        var undescribed = Guid.CreateVersion7(); // the Authority lists it, the business database has no row
        fake.Discoverable.AddRange([v1Draft, v1Submitted, v3, otherForm, undescribed]);

        var hybrid = await fake.Handlers(designAllowed: true).PreviewVersionBehaviorImpactAsync(Context, fake.Definition.FormDefinitionId, VersionBehaviors.Hybrid, CancellationToken.None);
        Assert.Equal(3, hybrid.VisibleRecordCount);
        Assert.Equal(2, hybrid.ViewLayoutChangeCount); // the two v1 records would be shown with v1 instead of v3
        Assert.Equal(0, hybrid.EditLayoutChangeCount); // Latest and Hybrid both edit with v3
        Assert.Equal(1, hybrid.UnavailableRecordCount);
        Assert.Equal([new VersionRecordCountResponse(1, 2), new VersionRecordCountResponse(3, 1)], hybrid.RecordsByCreatedVersion);
        Assert.True(hybrid.CountsAreApproximate);
        Assert.Equal(VersionBehaviors.Latest, hybrid.CurrentVersionBehavior);
        Assert.Equal(3, hybrid.CurrentVersionNumber);
        Assert.Empty(fake.AuditEvents);
        Assert.Equal(0, fake.Commits);
        Assert.DoesNotContain(hidden, fake.RequestedRecordIds);

        var historical = await fake.Handlers(designAllowed: true).PreviewVersionBehaviorImpactAsync(Context, fake.Definition.FormDefinitionId, VersionBehaviors.Historical, CancellationToken.None);
        Assert.Equal(2, historical.ViewLayoutChangeCount);
        Assert.Equal(2, historical.EditLayoutChangeCount);
    }

    [Fact]
    public async Task ANewDesignerWithoutRecordAccessSeesZeroRecords()
    {
        var fake = new FakeForm();
        fake.AddRecord(Created, "Draft");
        var impact = await fake.Handlers(designAllowed: true).PreviewVersionBehaviorImpactAsync(Context, fake.Definition.FormDefinitionId, VersionBehaviors.Historical, CancellationToken.None);
        Assert.Equal(0, impact.VisibleRecordCount);
        Assert.Empty(impact.RecordsByCreatedVersion);
    }

    [Fact]
    public async Task TheImpactNeedsTheDesignPermissionAndAKnownTarget()
    {
        var fake = new FakeForm();
        Assert.Equal(404, (await Assert.ThrowsAsync<BusinessException>(() => fake.Handlers(designAllowed: false).PreviewVersionBehaviorImpactAsync(Context, fake.Definition.FormDefinitionId, VersionBehaviors.Hybrid, CancellationToken.None))).HttpStatusCode);
        Assert.Equal(0, fake.Discoveries);
        Assert.Equal(422, (await Assert.ThrowsAsync<BusinessException>(() => fake.Handlers(designAllowed: true).PreviewVersionBehaviorImpactAsync(Context, fake.Definition.FormDefinitionId, "Newest", CancellationToken.None))).HttpStatusCode);
    }

    // ---- fakes ----

    private static (FormDefinition Definition, Dictionary<Guid, FormVersion> Versions, Func<Guid, Task<FormVersion?>> Find) Form(string behavior)
    {
        var definition = new FormDefinition { FormDefinitionId = Guid.CreateVersion7(), TenantId = Org, VersionBehavior = behavior, CurrentFormVersionId = Current };
        var versions = new Dictionary<Guid, FormVersion>
        {
            [Created] = new() { FormVersionId = Created, FormDefinitionId = definition.FormDefinitionId, VersionNumber = 1, LayoutJson = "{}" },
            [Current] = new() { FormVersionId = Current, FormDefinitionId = definition.FormDefinitionId, VersionNumber = 3, LayoutJson = "{}" },
        };
        return (definition, versions, id => Task.FromResult(versions.GetValueOrDefault(id)));
    }

    private static VerifiedRecord Verified(RecordLayoutPurpose purpose, Guid layoutVersionId)
    {
        var version = new FormVersion { FormVersionId = layoutVersionId, LayoutJson = "{}" };
        return new VerifiedRecord(null!, null!, null!, [], [], new FormDefinition(), version, new FormLayout(2, "Test", []), [], [], null!, [], []) { LayoutPurpose = purpose, EditLayoutVersion = purpose == RecordLayoutPurpose.Edit ? version : null };
    }

    private sealed class FakeForm
    {
        public FormDefinition Definition { get; } = new() { FormDefinitionId = Guid.CreateVersion7(), TenantId = Org, FormDefinitionName = "Original name", CurrentFormVersionId = Current, FormDefinitionRevision = 1 };

        public List<FormRecord> Records { get; } = [];

        public List<Guid> Discoverable { get; } = [];

        public List<Guid> RequestedRecordIds { get; } = [];

        public List<AuditEvent> AuditEvents { get; } = [];

        public int AuthorityChecks { get; private set; }

        public int Discoveries { get; private set; }

        public int ScopesOpened { get; private set; }

        public int Commits { get; private set; }

        public Guid AddRecord(Guid createdVersion, string status, Guid? formDefinitionId = null)
        {
            var record = new FormRecord { FormRecordId = Guid.CreateVersion7(), TenantId = Org, FormDefinitionId = formDefinitionId ?? Definition.FormDefinitionId, CreatedFormVersionId = createdVersion, RecordStatus = status };
            Records.Add(record);
            return record.FormRecordId;
        }

        public FormDesignerHandlers Handlers(bool designAllowed)
        {
            var authority = Stubs.Create<IAuthorizationAuthorityClient>((method, args) =>
            {
                AuthorityChecks++;
                switch (method.Name)
                {
                    case nameof(IAuthorizationAuthorityClient.CheckPermissionAsync):
                        return Task.FromResult(new PermissionDecision(designAllowed, null, 9, 9, null, Guid.Empty));
                    case nameof(IAuthorizationAuthorityClient.CheckPermissionsBatchAsync):
                        return Task.FromResult<IReadOnlyList<PermissionDecision>>(((IReadOnlyList<PermissionCheckRequest>)args[1]!).Select(_ => new PermissionDecision(designAllowed, null, 9, 9, null, Guid.Empty)).ToList());
                    case nameof(IAuthorizationAuthorityClient.DiscoverAuthorizedRecordsAsync):
                        Discoveries++;
                        return Task.FromResult(new RecordDiscoveryPage(Discoverable.ToList(), 9, null));
                    default:
                        throw new NotSupportedException(method.Name);
                }
            });
            var store = Stubs.Create<IBusinessStore>((_, _) =>
            {
                ScopesOpened++;
                return Task.FromResult(Scope());
            });
            return new FormDesignerHandlers(store, authority, null!, TimeProvider.System, null!);
        }

        private IBusinessScope Scope() => Stubs.Create<IBusinessScope>((method, args) => method.Name switch
        {
            nameof(IBusinessScope.FindFormDefinitionAsync) => (object?)Task.FromResult<FormDefinition?>((Guid)args[1]! == Definition.FormDefinitionId ? Definition : null),
            nameof(IBusinessScope.GetFormDefinitionsAsync) => Task.FromResult(new List<FormDefinition> { Definition }),
            nameof(IBusinessScope.GetFieldDefinitionsAsync) => Task.FromResult(new List<FieldDefinition>()),
            nameof(IBusinessScope.GetFieldOptionsAsync) => Task.FromResult(new List<FieldOption>()),
            nameof(IBusinessScope.GetGroupDefinitionsAsync) => Task.FromResult(new List<GroupDefinition>()),
            nameof(IBusinessScope.GetRelationshipDefinitionsAsync) => Task.FromResult(new List<FormRelationshipDefinition>()),
            nameof(IBusinessScope.FindFormVersionAsync) => Task.FromResult<FormVersion?>(new FormVersion { FormVersionId = (Guid)args[0]!, FormDefinitionId = Definition.FormDefinitionId, VersionNumber = 3 }),
            nameof(IBusinessScope.GetFormVersionsAsync) => Task.FromResult(new List<FormVersion>
            {
                new() { FormVersionId = Created, FormDefinitionId = Definition.FormDefinitionId, VersionNumber = 1 },
                new() { FormVersionId = Current, FormDefinitionId = Definition.FormDefinitionId, VersionNumber = 3 },
            }),
            nameof(IBusinessScope.GetFormRecordsAsync) => RecordsFor(args),
            nameof(IBusinessScope.FindDraftAsync) => Task.FromResult<FormDraft?>(null),
            nameof(IBusinessScope.AddAuditEvent) => Record((AuditEvent)args[0]!),
            nameof(IBusinessScope.CommitAsync) => Commit(),
            nameof(IAsyncDisposable.DisposeAsync) => ValueTask.CompletedTask,
            _ => throw new NotSupportedException(method.Name),
        });

        private Task<List<FormRecord>> RecordsFor(object?[] args)
        {
            var ids = ((IReadOnlyCollection<Guid>)args[1]!).ToHashSet();
            RequestedRecordIds.AddRange(ids);
            return Task.FromResult(Records.Where(r => ids.Contains(r.FormRecordId)).ToList());
        }

        private object? Record(AuditEvent auditEvent)
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
