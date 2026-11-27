using System.Text.Json;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.SecurityCommands;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Content;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using AuthorizationAuthority.Infrastructure.Faults;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// C10 WP8-A (WP-0 Section 3.6): document entries of a share. Document-only, field-only and mixed shares are accepted and an empty
/// scope is refused; each document must be an attachment of the accepted content with Authority-verified version evidence, whose
/// version becomes the share's boundary; view is required; replacement needs an editing role (WP8-B); a delegated share stays within the
/// parent's documents, flags and version boundary; revocation removes document edges through the existing cascade.
/// </summary>
public sealed class ShareDocumentCreationTests
{
    private static readonly HumanCommandStepUpGate Gate = HumanCommandStepUpGate.Create(HumanCommandStepUpGate.SyntheticMode, "Testing", "TestIdentityProvider");
    private readonly Guid org = Guid.NewGuid();
    private readonly Guid record = Guid.NewGuid();
    private readonly Guid owner = Guid.NewGuid();
    private readonly Guid manager = Guid.NewGuid();
    private readonly Guid reader = Guid.NewGuid();
    private readonly Guid field = Guid.NewGuid();
    private readonly Guid statement = Guid.NewGuid();
    private readonly Guid photo = Guid.NewGuid();
    private readonly Guid legacy = Guid.NewGuid();
    private readonly Guid commit = Guid.NewGuid();
    private readonly InMemoryAuthorityStore store = new();

    public ShareDocumentCreationTests()
    {
        store.Organizations.Add(new OrganizationAuthorizationState { OrganizationId = org, AuthorizationRevision = 5, ProjectionAppliedRevision = 5 });
        foreach (var user in new[] { owner, manager, reader })
        {
            store.Bindings.Add(new IdentityBinding { IdentityBindingId = Guid.NewGuid(), OrganizationId = org, AuthorityUserId = user, IdentityIssuerUri = "https://idp", IdentitySubjectIdentifier = user.ToString("N"), IdentityBindingEnabled = true });
        }

        store.Resources.Add(new AuthorityResource { OrganizationId = org, ResourceEntityId = record, ResourceTypeName = ResourceTypeNames.FormRecord, ResourceLifecycleState = ResourceLifecycleStates.Active });
        store.Edges.Add(new AuthorityRelationshipEdge { AuthorityRelationshipEdgeId = Guid.NewGuid(), OrganizationId = org, ResourceTypeName = ResourceTypeNames.FormRecord, ResourceEntityId = record, RelationshipName = "owner", SubjectTypeName = ResourceTypeNames.User, SubjectEntityId = owner });
        store.Heads.Add(new RecordContentHead { OrganizationId = org, FormRecordId = record, CurrentRecordManifestCommitId = commit });
        Leaf(field, EnterpriseForms.Integrity.Merkle.MerkleItemKind.FieldValue, null);
        Leaf(statement, EnterpriseForms.Integrity.Merkle.MerkleItemKind.Attachment, 2);
        Leaf(photo, EnterpriseForms.Integrity.Merkle.MerkleItemKind.Attachment, 1);
        Leaf(legacy, EnterpriseForms.Integrity.Merkle.MerkleItemKind.Attachment, null); // accepted before WP8: no evidence
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void Leaf(Guid item, EnterpriseForms.Integrity.Merkle.MerkleItemKind kind, int? version) =>
        store.ManifestLeaves.Add(new RecordManifestLeaf { RecordManifestCommitId = commit, ManifestItemId = item, ManifestItemKindCode = (byte)kind, AttachmentVersionNumber = version });

    private static ShareDocumentEntry View(Guid id) => new(id, true, false, false);

    private static ShareDocumentEntry Download(Guid id) => new(id, true, true, false);

    private async Task<Guid> CreateAsync(Guid actor, Guid recipient, string role, Guid[] fields, ShareDocumentEntry[]? documents, Guid? parent = null)
    {
        var share = Guid.NewGuid();
        await new SecurityCommandService(store, new NoOpFaultInjector(), TimeProvider.System, FieldSearchApprovalGate.Unavailable, humanCommandGate: Gate).SubmitAsync(
            new VerifiedActor("https://idp", actor.ToString("N"), "test"),
            new SecurityCommandRequest(org, SecurityCommandKind.CreateShare, new ResourceRef(ResourceKind.FormRecord, record), store.Organizations.Single().AuthorizationRevision,
                JsonSerializer.SerializeToElement(new CreateSharePayload(share, record, recipient, null, role, fields, HumanCommandStepUpGate.SyntheticStepUpEvidence, parent, null, documents), CanonicalJson.Options)),
            "doc-share-" + share.ToString("N")[..12], Guid.NewGuid(), Ct);
        return share;
    }

    private bool HasDocumentEdge(Guid share, Guid attachment) =>
        store.Edges.Any(e => e.ResourceTypeName == ResourceTypeNames.DocumentItem && e.ResourceEntityId == attachment && e.RelationshipName == "shared_via" && e.SubjectEntityId == share && e.RemovedBySecurityCommandId is null);

    private static async Task<string> RefusedAsync(Func<Task> act) => (await Assert.ThrowsAsync<AuthorityException>(act)).ErrorCode;

    [Fact]
    public async Task ADocumentOnlyShareBindsTheVerifiedVersionAndExposesNoField()
    {
        var share = await CreateAsync(owner, reader, RecipientRoles.Reader, [], [Download(statement)]);

        Assert.True(HasDocumentEdge(share, statement));
        Assert.False(HasDocumentEdge(share, photo));
        Assert.DoesNotContain(store.Edges, e => e.ResourceTypeName == ResourceTypeNames.FieldItem && e.SubjectEntityId == share);
        var binding = Assert.Single(store.DocumentBindings);
        Assert.Equal((statement, true, true, false, 2), (binding.FileAttachmentId, binding.CanViewDocument, binding.CanDownloadDocument, binding.CanReplaceDocument, binding.SharedThroughVersionNumber));
        var command = store.Commands.Single(c => c.CommandKindName == SecurityCommandKinds.CreateShare);
        Assert.Equal(command.SecurityCommandId, binding.BoundBySecurityCommandId);
        Assert.Contains("shared_through_version_number", command.CommandPayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FieldOnlyAndMixedSharesAreAcceptedAndAFieldOnlyPayloadNamesNoDocuments()
    {
        await CreateAsync(owner, reader, RecipientRoles.Reader, [field], null);
        var mixed = await CreateAsync(owner, reader, RecipientRoles.Reader, [field], [View(photo)]);

        Assert.True(HasDocumentEdge(mixed, photo));
        Assert.DoesNotContain("documents", store.Commands.First().CommandPayloadJson, StringComparison.Ordinal);
        Assert.Single(store.DocumentBindings);
    }

    [Fact]
    public async Task InvalidDocumentScopesAreRefusedAndNothingIsBound()
    {
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, await RefusedAsync(() => CreateAsync(owner, reader, RecipientRoles.Reader, [], [])));
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, await RefusedAsync(() => CreateAsync(owner, reader, RecipientRoles.Reader, [], [View(Guid.NewGuid())])));
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, await RefusedAsync(() => CreateAsync(owner, reader, RecipientRoles.Reader, [], [View(field)]))); // a field item is not a document
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, await RefusedAsync(() => CreateAsync(owner, reader, RecipientRoles.Reader, [], [new(statement, false, true, false)])));
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, await RefusedAsync(() => CreateAsync(owner, reader, RecipientRoles.Reader, [], [View(statement), View(statement)])));
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, await RefusedAsync(() => CreateAsync(owner, reader, RecipientRoles.Reader, [], [new(statement, true, false, true)]))); // replace needs an editing role
        Assert.Equal(AuthorityErrorCodes.DocumentVersionEvidenceUnavailable, await RefusedAsync(() => CreateAsync(owner, reader, RecipientRoles.Reader, [], [View(legacy)])));
        Assert.Empty(store.DocumentBindings);
        Assert.DoesNotContain(store.Edges, e => e.ResourceTypeName == ResourceTypeNames.DocumentItem);
    }

    [Fact]
    public async Task ADelegatedShareStaysWithinTheParentsDocumentsFlagsAndVersion()
    {
        var parent = await CreateAsync(owner, manager, RecipientRoles.Manager, [], [View(statement), Download(photo)]);

        var child = await CreateAsync(manager, reader, RecipientRoles.Reader, [], [View(statement), Download(photo)], parent);
        Assert.True(HasDocumentEdge(child, statement) && HasDocumentEdge(child, photo));

        Assert.Equal(AuthorityErrorCodes.InvalidCommand, await RefusedAsync(() => CreateAsync(manager, reader, RecipientRoles.Reader, [], [Download(statement)], parent))); // parent may only view
        store.ManifestLeaves.Add(new RecordManifestLeaf { RecordManifestCommitId = commit, ManifestItemId = Guid.NewGuid(), ManifestItemKindCode = (byte)EnterpriseForms.Integrity.Merkle.MerkleItemKind.Attachment, AttachmentVersionNumber = 1 });
        var unshared = store.ManifestLeaves.Last().ManifestItemId;
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, await RefusedAsync(() => CreateAsync(manager, reader, RecipientRoles.Reader, [], [View(unshared)], parent))); // not in the parent

        // The owner adds version 3 of the statement: the parent covers only version 2, so the newer version cannot be delegated.
        store.ManifestLeaves.Single(l => l.ManifestItemId == statement).AttachmentVersionNumber = 3;
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, await RefusedAsync(() => CreateAsync(manager, reader, RecipientRoles.Reader, [], [View(statement)], parent)));
    }

    [Fact]
    public async Task RevokingAShareRemovesItsDocumentEdgesThroughTheExistingCascade()
    {
        var parent = await CreateAsync(owner, manager, RecipientRoles.Manager, [], [View(statement)]);
        var child = await CreateAsync(manager, reader, RecipientRoles.Reader, [], [View(statement)], parent);

        await new SecurityCommandService(store, new NoOpFaultInjector(), TimeProvider.System, FieldSearchApprovalGate.Unavailable, humanCommandGate: Gate).SubmitAsync(
            new VerifiedActor("https://idp", owner.ToString("N"), "test"),
            new SecurityCommandRequest(org, SecurityCommandKind.RevokeShare, new ResourceRef(ResourceKind.FormRecord, record), store.Organizations.Single().AuthorizationRevision,
                JsonSerializer.SerializeToElement(new RevokeSharePayload(parent, record, HumanCommandStepUpGate.SyntheticStepUpEvidence), CanonicalJson.Options)),
            "doc-revoke-" + parent.ToString("N")[..12], Guid.NewGuid(), Ct);

        Assert.False(HasDocumentEdge(parent, statement));
        Assert.False(HasDocumentEdge(child, statement));
        Assert.Equal(ResourceLifecycleStates.Invalidated, store.Resources.Single(r => r.ResourceEntityId == child).ResourceLifecycleState);
    }

    [Fact]
    public async Task OnlyAnOwnerGrantedEditingShareMayAllowReplacementAndADelegatedRequestIsRejectedNotStripped()
    {
        var direct = await CreateAsync(owner, manager, RecipientRoles.Manager, [], [new(statement, true, true, true)]);
        Assert.True(store.DocumentBindings.Single(b => b.RecordShareId == direct).CanReplaceDocument);

        var refused = await RefusedAsync(() => CreateAsync(manager, reader, RecipientRoles.Contributor, [], [new(statement, true, false, true)], direct));
        Assert.Equal(AuthorityErrorCodes.DocumentReplaceRequiresOwnerGrant, refused);
        var viewOnlyChild = await CreateAsync(manager, reader, RecipientRoles.Reader, [], [View(statement)], direct); // view and download still delegate
        Assert.False(store.DocumentBindings.Single(b => b.RecordShareId == viewOnlyChild).CanReplaceDocument);

        var update = new UpdateShareScopePayload(viewOnlyChild, record, RecipientRoles.Contributor, [], store.ShareMetadata.Single(m => m.RecordShareId == viewOnlyChild).ShareRevision,
            HumanCommandStepUpGate.SyntheticStepUpEvidence, direct, null, [new(statement, true, false, true)]);
        Assert.Equal(AuthorityErrorCodes.DocumentReplaceRequiresOwnerGrant, await RefusedAsync(() => new SecurityCommandService(store, new NoOpFaultInjector(), TimeProvider.System, FieldSearchApprovalGate.Unavailable, humanCommandGate: Gate).SubmitAsync(
            new VerifiedActor("https://idp", owner.ToString("N"), "test"),
            new SecurityCommandRequest(org, SecurityCommandKind.UpdateShareScope, new ResourceRef(ResourceKind.FormRecord, record), store.Organizations.Single().AuthorizationRevision, JsonSerializer.SerializeToElement(update, CanonicalJson.Options)),
            "doc-update-replace-1", Guid.NewGuid(), Ct)));
        Assert.False(store.DocumentBindings.Single(b => b.RecordShareId == viewOnlyChild).CanReplaceDocument);
    }
}
