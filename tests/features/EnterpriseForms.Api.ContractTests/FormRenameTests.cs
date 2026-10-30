using System.Reflection;
using System.Text.Json;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.FormDesigner;
using EnterpriseForms.Contracts.Features;
using EnterpriseForms.Domain.Features.Audit;
using EnterpriseForms.Domain.Features.FormDefinitions;
using EnterpriseForms.Domain.Features.RelatedForms;
using Xunit;

namespace EnterpriseForms.Api.ContractTests;

/// <summary>
/// Form rename: the shared naming rule, the Authority design permission checked before any store access, optimistic
/// concurrency on form_definition_revision, an audited single transaction, and no change to identity or versions.
/// The real-stack proof (published versions, records and permissions unchanged) is FormRenameStackTests in Stage B.
/// </summary>
public sealed class FormRenameTests
{
    private static readonly Guid Org = Guid.CreateVersion7();
    private static readonly Guid Me = Guid.CreateVersion7();
    private static readonly RequestContext Context = new(Org, Me, "https://issuer.invalid", "subject", "Tester", "assertion", Guid.CreateVersion7());

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Line\nbreak")]
    [InlineData("Tab\tname")]
    [InlineData(null)]
    public void NamesThatBreakTheRuleAreRejected(string? name) => Assert.Null(FormDefinitionNameRules.Normalize(name));

    [Fact]
    public void NamesAreTrimmedAndLimitedTo200Characters()
    {
        Assert.Equal("Intake form", FormDefinitionNameRules.Normalize("  Intake form  "));
        Assert.NotNull(FormDefinitionNameRules.Normalize(new string('a', 200)));
        Assert.Null(FormDefinitionNameRules.Normalize(new string('a', 201)));
    }

    [Fact]
    public async Task AnInvalidNameIsRejectedBeforeTheAuthorityOrTheStoreIsAsked()
    {
        var fake = new FakeForm();
        var handlers = fake.Handlers(designAllowed: true);
        var error = await Assert.ThrowsAsync<BusinessException>(() => handlers.RenameAsync(Context, fake.Definition.FormDefinitionId, new RenameFormDefinitionRequest(" ", 1), CancellationToken.None));
        Assert.Equal(422, error.HttpStatusCode);
        Assert.Equal(0, fake.AuthorityChecks);
        Assert.Equal(0, fake.ScopesOpened);
    }

    [Fact]
    public async Task WithoutTheDesignPermissionTheFormIsNotFoundAndNothingIsRead()
    {
        var fake = new FakeForm();
        var error = await Assert.ThrowsAsync<BusinessException>(() => fake.Handlers(designAllowed: false).RenameAsync(Context, fake.Definition.FormDefinitionId, new RenameFormDefinitionRequest("New name", 1), CancellationToken.None));
        Assert.Equal(404, error.HttpStatusCode);
        Assert.Equal(0, fake.ScopesOpened);
        Assert.Equal("Original name", fake.Definition.FormDefinitionName);
    }

    [Fact]
    public async Task ARenameChangesOnlyTheNameAndRevisionAndIsAuditedInTheSameCommit()
    {
        var fake = new FakeForm();
        var versionId = fake.Definition.CurrentFormVersionId;
        var result = await fake.Handlers(designAllowed: true).RenameAsync(Context, fake.Definition.FormDefinitionId, new RenameFormDefinitionRequest("  Renamed form ", 1), CancellationToken.None);

        Assert.Equal("Renamed form", result.Summary.FormDefinitionName);
        Assert.Equal(2, result.Summary.FormDefinitionRevision);
        Assert.Equal(fake.Definition.FormDefinitionId, result.Summary.FormDefinitionId);
        Assert.Equal(versionId, fake.Definition.CurrentFormVersionId);
        Assert.Equal(1, fake.Commits);
        var audit = Assert.Single(fake.AuditEvents);
        Assert.Equal(AuditActions.FormRenamed, audit.EventAction);
        Assert.Equal(fake.Definition.FormDefinitionId, audit.FormDefinitionId);
        using var metadata = JsonDocument.Parse(audit.EventMetadataJson);
        Assert.Equal("Original name", metadata.RootElement.GetProperty("previousFormDefinitionName").GetString());
        Assert.Equal("Renamed form", metadata.RootElement.GetProperty("formDefinitionName").GetString());
        Assert.Equal(2, metadata.RootElement.GetProperty("formDefinitionRevision").GetInt64());
    }

    [Fact]
    public async Task AStaleRevisionIsAConflictAndChangesNothing()
    {
        var fake = new FakeForm();
        fake.Definition.FormDefinitionRevision = 3;
        var error = await Assert.ThrowsAsync<BusinessException>(() => fake.Handlers(designAllowed: true).RenameAsync(Context, fake.Definition.FormDefinitionId, new RenameFormDefinitionRequest("Other name", 2), CancellationToken.None));
        Assert.Equal(409, error.HttpStatusCode);
        Assert.Equal("FORM_DEFINITION_MODIFIED_CONCURRENTLY", error.ErrorCode);
        Assert.Equal("Original name", fake.Definition.FormDefinitionName);
        Assert.Equal(3, fake.Definition.FormDefinitionRevision);
        Assert.Equal(0, fake.Commits);
        Assert.Empty(fake.AuditEvents);
    }

    [Fact]
    public async Task ResendingARenameThatAlreadyTookEffectIsNotASecondChange()
    {
        var fake = new FakeForm();
        fake.Definition.FormDefinitionRevision = 2;
        var result = await fake.Handlers(designAllowed: true).RenameAsync(Context, fake.Definition.FormDefinitionId, new RenameFormDefinitionRequest("Original name", 1), CancellationToken.None);
        Assert.Equal(2, result.Summary.FormDefinitionRevision);
        Assert.Equal(0, fake.Commits);
        Assert.Empty(fake.AuditEvents);
    }

    [Fact]
    public async Task ADeletedFormCannotBeRenamed()
    {
        var fake = new FakeForm();
        fake.Definition.LifecycleStatus = "Deleted";
        var error = await Assert.ThrowsAsync<BusinessException>(() => fake.Handlers(designAllowed: true).RenameAsync(Context, fake.Definition.FormDefinitionId, new RenameFormDefinitionRequest("New name", 1), CancellationToken.None));
        Assert.Equal(404, error.HttpStatusCode);
        Assert.Equal(0, fake.Commits);
    }

    private sealed class FakeForm
    {
        public FormDefinition Definition { get; } = new() { FormDefinitionId = Guid.CreateVersion7(), TenantId = Org, FormDefinitionName = "Original name", CurrentFormVersionId = Guid.CreateVersion7(), FormDefinitionRevision = 1 };

        public List<AuditEvent> AuditEvents { get; } = [];

        public int AuthorityChecks { get; private set; }

        public int ScopesOpened { get; private set; }

        public int Commits { get; private set; }

        public FormDesignerHandlers Handlers(bool designAllowed)
        {
            var authority = Stubs.Create<IAuthorizationAuthorityClient>((method, args) =>
            {
                AuthorityChecks++;
                return method.Name switch
                {
                    nameof(IAuthorizationAuthorityClient.CheckPermissionAsync) => Task.FromResult(new PermissionDecision(designAllowed, null, 9, 9, null, Guid.Empty)),
                    nameof(IAuthorizationAuthorityClient.CheckPermissionsBatchAsync) => Task.FromResult<IReadOnlyList<PermissionDecision>>(((IReadOnlyList<PermissionCheckRequest>)args[1]!).Select(_ => new PermissionDecision(designAllowed, null, 9, 9, null, Guid.Empty)).ToList()),
                    _ => throw new NotSupportedException(method.Name),
                };
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
            nameof(IBusinessScope.FindFormVersionAsync) => Task.FromResult<FormVersion?>(new FormVersion { FormVersionId = (Guid)args[0]!, FormDefinitionId = Definition.FormDefinitionId, VersionNumber = 1 }),
            nameof(IBusinessScope.FindDraftAsync) => Task.FromResult<FormDraft?>(null),
            nameof(IBusinessScope.AddAuditEvent) => Record((AuditEvent)args[0]!),
            nameof(IBusinessScope.CommitAsync) => Commit(),
            nameof(IAsyncDisposable.DisposeAsync) => ValueTask.CompletedTask,
            _ => throw new NotSupportedException(method.Name),
        });

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
