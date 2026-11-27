using System.Text.Json;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.SecurityCommands;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Content;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using AuthorizationAuthority.Domain.Features.Sharing;
using AuthorizationAuthority.Infrastructure.Faults;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// C10 WP8-A (WP-0 Sections 3.2, 3.4, 3.6): UpdateShareScope with documents. The new scope is complete: documents are added, kept with
/// new flags, or removed (edge and binding); a kept document's boundary moves to its current verified version; an empty scope is
/// refused; narrowing a document or its download flag invalidates delegated shares it no longer covers, in the same command, while
/// covered ones and independent grants stay; a refused update changes no binding.
/// </summary>
public sealed class ShareDocumentUpdateTests
{
    private static readonly HumanCommandStepUpGate Gate = HumanCommandStepUpGate.Create(HumanCommandStepUpGate.SyntheticMode, "Testing", "TestIdentityProvider");
    private readonly Guid org = Guid.NewGuid();
    private readonly Guid record = Guid.NewGuid();
    private readonly Guid owner = Guid.NewGuid();
    private readonly Guid manager = Guid.NewGuid();
    private readonly Guid reader = Guid.NewGuid();
    private readonly Guid statement = Guid.NewGuid();
    private readonly Guid photo = Guid.NewGuid();
    private readonly Guid commit = Guid.NewGuid();
    private readonly InMemoryAuthorityStore store = new();

    public ShareDocumentUpdateTests()
    {
        store.Organizations.Add(new OrganizationAuthorizationState { OrganizationId = org, AuthorizationRevision = 5, ProjectionAppliedRevision = 5 });
        foreach (var user in new[] { owner, manager, reader })
        {
            store.Bindings.Add(new IdentityBinding { IdentityBindingId = Guid.NewGuid(), OrganizationId = org, AuthorityUserId = user, IdentityIssuerUri = "https://idp", IdentitySubjectIdentifier = user.ToString("N"), IdentityBindingEnabled = true });
        }

        store.Resources.Add(new AuthorityResource { OrganizationId = org, ResourceEntityId = record, ResourceTypeName = ResourceTypeNames.FormRecord, ResourceLifecycleState = ResourceLifecycleStates.Active });
        store.Edges.Add(new AuthorityRelationshipEdge { AuthorityRelationshipEdgeId = Guid.NewGuid(), OrganizationId = org, ResourceTypeName = ResourceTypeNames.FormRecord, ResourceEntityId = record, RelationshipName = "owner", SubjectTypeName = ResourceTypeNames.User, SubjectEntityId = owner });
        store.Heads.Add(new RecordContentHead { OrganizationId = org, FormRecordId = record, CurrentRecordManifestCommitId = commit });
        foreach (var (attachment, version) in new[] { (statement, 1), (photo, 1) })
        {
            store.ManifestLeaves.Add(new RecordManifestLeaf { RecordManifestCommitId = commit, ManifestItemId = attachment, ManifestItemKindCode = (byte)EnterpriseForms.Integrity.Merkle.MerkleItemKind.Attachment, AttachmentVersionNumber = version });
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SecurityCommandService Service() => new(store, new NoOpFaultInjector(), TimeProvider.System, FieldSearchApprovalGate.Unavailable, humanCommandGate: Gate);

    private async Task<Guid> CreateAsync(Guid actor, Guid recipient, string role, ShareDocumentEntry[] documents, Guid? parent = null)
    {
        var share = Guid.NewGuid();
        await Submit(actor, SecurityCommandKind.CreateShare, new CreateSharePayload(share, record, recipient, null, role, [], HumanCommandStepUpGate.SyntheticStepUpEvidence, parent, null, documents));
        return share;
    }

    private Task<OperationStatus> Submit(Guid actor, SecurityCommandKind kind, object payload) =>
        Service().SubmitAsync(new VerifiedActor("https://idp", actor.ToString("N"), "test"),
            new SecurityCommandRequest(org, kind, new ResourceRef(ResourceKind.FormRecord, record), store.Organizations.Single().AuthorizationRevision, JsonSerializer.SerializeToElement(payload, CanonicalJson.Options)),
            "doc-update-" + Guid.NewGuid().ToString("N")[..12], Guid.NewGuid(), Ct);

    private Task<OperationStatus> UpdateAsync(Guid share, string role, ShareDocumentEntry[] documents) =>
        Submit(owner, SecurityCommandKind.UpdateShareScope, new UpdateShareScopePayload(share, record, role, [], store.ShareMetadata.Single(m => m.RecordShareId == share).ShareRevision,
            HumanCommandStepUpGate.SyntheticStepUpEvidence, null, null, documents));

    private ShareDocumentBinding? Binding(Guid share, Guid attachment) => store.DocumentBindings.SingleOrDefault(b => b.RecordShareId == share && b.FileAttachmentId == attachment);

    private bool HasEdge(Guid share, Guid attachment) =>
        store.Edges.Any(e => e.ResourceTypeName == ResourceTypeNames.DocumentItem && e.ResourceEntityId == attachment && e.SubjectEntityId == share && e.RemovedBySecurityCommandId is null);

    private string State(Guid share) => store.Resources.Single(r => r.ResourceEntityId == share).ResourceLifecycleState;

    [Fact]
    public async Task TheNewScopeReplacesDocumentsAndAKeptDocumentsBoundaryMovesToItsCurrentVersion()
    {
        var share = await CreateAsync(owner, reader, RecipientRoles.Reader, [new(statement, true, false, false)]);
        store.ManifestLeaves.Single(l => l.ManifestItemId == statement).AttachmentVersionNumber = 2; // the owner added version 2

        await UpdateAsync(share, RecipientRoles.Reader, [new(statement, true, true, false), new(photo, true, false, false)]);

        Assert.Equal((true, 2), (Binding(share, statement)!.CanDownloadDocument, Binding(share, statement)!.SharedThroughVersionNumber));
        Assert.True(HasEdge(share, photo));
        var update = store.Commands.Last(c => c.CommandKindName == SecurityCommandKinds.UpdateShareScope);
        Assert.Equal(update.SecurityCommandId, Binding(share, statement)!.BoundBySecurityCommandId);
        Assert.Contains("\"documents\"", update.CommandPayloadJson, StringComparison.Ordinal);

        await UpdateAsync(share, RecipientRoles.Reader, [new(photo, true, false, false)]);
        Assert.Null(Binding(share, statement));
        Assert.False(HasEdge(share, statement));
        Assert.True(HasEdge(share, photo));
    }

    [Fact]
    public async Task AnEmptyScopeIsRefusedAndChangesNothing()
    {
        var share = await CreateAsync(owner, reader, RecipientRoles.Reader, [new(statement, true, true, false)]);

        await Assert.ThrowsAsync<AuthorityException>(() => UpdateAsync(share, RecipientRoles.Reader, []));

        Assert.True(Binding(share, statement)!.CanDownloadDocument);
        Assert.True(HasEdge(share, statement));
    }

    [Fact]
    public async Task NarrowingADocumentOrItsDownloadFlagInvalidatesOnlyUncoveredDelegations()
    {
        var parent = await CreateAsync(owner, manager, RecipientRoles.Manager, [new(statement, true, true, false), new(photo, true, false, false)]);
        var downloads = await CreateAsync(manager, reader, RecipientRoles.Reader, [new(statement, true, true, false)], parent);
        var views = await CreateAsync(manager, reader, RecipientRoles.Reader, [new(photo, true, false, false)], parent);
        var independent = await CreateAsync(owner, reader, RecipientRoles.Reader, [new(statement, true, true, false)]);

        await UpdateAsync(parent, RecipientRoles.Manager, [new(statement, true, false, false), new(photo, true, false, false)]); // drop download

        Assert.Equal(ResourceLifecycleStates.Invalidated, State(downloads));
        Assert.Equal(ShareInvalidationReasons.UpstreamNarrowed, store.ShareMetadata.Single(m => m.RecordShareId == downloads).ShareInvalidationReasonCode);
        Assert.False(HasEdge(downloads, statement));
        Assert.Equal(ResourceLifecycleStates.Active, State(views));
        Assert.Equal(ResourceLifecycleStates.Active, State(independent));
        Assert.True(Binding(independent, statement)!.CanDownloadDocument);

        await UpdateAsync(parent, RecipientRoles.Manager, [new(statement, true, false, false)]); // drop the photo
        Assert.Equal(ResourceLifecycleStates.Invalidated, State(views));
    }
}
