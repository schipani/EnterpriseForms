using System.Reflection;
using System.Text.Json;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Audit;
using AuthorizationAuthority.Application.Features.SecurityCommands;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Content;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using AuthorizationAuthority.Domain.Features.Sharing;
using AuthorizationAuthority.Infrastructure.Faults;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// C10 M1-WP9: named share lifecycle evidence (<c>ShareScopeUpdated</c>, <c>ShareInvalidated</c>) on the audit stream, staged in the
/// accepted command's own transaction, attributed to the exact command and committed revision, redacted to an allowlist, and never
/// written for a rejected or replayed command. Offline: the in-memory store and a recording scope.
/// </summary>
public sealed class ShareLifecycleJournalTests
{
    private static readonly Guid Organization = Guid.NewGuid();
    private static readonly Guid Record = Guid.NewGuid();
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Recipient = Guid.NewGuid();
    private static readonly Guid Item = Guid.NewGuid();
    private static readonly Guid OtherItem = Guid.NewGuid();
    private const string Prose = "confidential reason prose";
    private static readonly HumanCommandStepUpGate Gate =
        HumanCommandStepUpGate.Create(HumanCommandStepUpGate.SyntheticMode, "Testing", "TestIdentityProvider");

    /// <summary>The canonical authorization-stream body fields; WP9 adds none (historical bodies and digests are unchanged).</summary>
    private static readonly string[] CanonicalCommandBodyFields =
    [
        "security_command_id", "command_kind", "actor_user_id", "actor_issuer_uri", "actor_subject_identifier", "resource_kind", "resource_id",
        "expected_authorization_revision", "committed_authorization_revision", "request_digest_hex", "normalized_payload", "relationship_delta",
        "registered_resources", "revoked_resources", "change_reason", "correlation_id",
    ];

    private readonly InMemoryAuthorityStore store = new();
    private readonly RecordingStore recording;

    public ShareLifecycleJournalTests()
    {
        recording = new RecordingStore(store);
        store.Organizations.Add(new OrganizationAuthorizationState { OrganizationId = Organization, AuthorizationRevision = 5, ProjectionAppliedRevision = 5 });
        store.Bindings.Add(new IdentityBinding
        {
            IdentityBindingId = Guid.NewGuid(), OrganizationId = Organization, AuthorityUserId = Owner,
            IdentityIssuerUri = "https://idp", IdentitySubjectIdentifier = Owner.ToString("N"), IdentityBindingEnabled = true,
        });
        store.Resources.Add(new AuthorityResource
        {
            OrganizationId = Organization, ResourceEntityId = Record, ResourceTypeName = ResourceTypeNames.FormRecord, ResourceLifecycleState = ResourceLifecycleStates.Active,
        });
        var commit = Guid.NewGuid();
        store.Heads.Add(new RecordContentHead { OrganizationId = Organization, FormRecordId = Record, CurrentRecordManifestCommitId = commit });
        store.ManifestLeaves.Add(new RecordManifestLeaf { RecordManifestCommitId = commit, ManifestItemId = Item, ManifestItemKindCode = (byte)EnterpriseForms.Integrity.Merkle.MerkleItemKind.FieldValue, ManifestLeafPosition = 0 });
        store.ManifestLeaves.Add(new RecordManifestLeaf { RecordManifestCommitId = commit, ManifestItemId = OtherItem, ManifestItemKindCode = (byte)EnterpriseForms.Integrity.Merkle.MerkleItemKind.FieldValue, ManifestLeafPosition = 1 });
        AddEdge(ResourceTypeNames.FormRecord, Record, "owner", ResourceTypeNames.User, Owner);
    }

    [Fact]
    public async Task RevokeStagesOneAttributedShareInvalidatedEntryPerDependentInTheCommandTransaction()
    {
        var root = AddShare();
        var child = AddShare(root.Metadata);
        var grandchild = AddShare(child.Metadata);
        AddShare(); // an independent grant: never named

        var status = await RevokeAsync(root.Resource.ResourceEntityId, "revoke-cascade-1");

        var entries = Entries(ShareLifecycleJournal.ShareInvalidated);
        Assert.Equal(new[] { child.Resource.ResourceEntityId, grandchild.Resource.ResourceEntityId }.Order(), entries.Select(e => Body(e).GetProperty("record_share_id").GetGuid()));
        foreach (var entry in entries)
        {
            var body = Body(entry);
            Assert.Equal(JournalStreamNames.Audit, entry.JournalStreamName);
            Assert.Equal(status.CommandId, body.GetProperty("security_command_id").GetGuid());
            Assert.Equal(nameof(SecurityCommandKind.RevokeShare), body.GetProperty("command_kind").GetString());
            Assert.Equal(Record, body.GetProperty("form_record_id").GetGuid());
            Assert.Equal(root.Resource.ResourceEntityId, body.GetProperty("upstream_record_share_id").GetGuid());
            Assert.Equal(ShareInvalidationReasons.UpstreamRevoked, body.GetProperty("invalidation_reason").GetString());
            Assert.Equal(6, body.GetProperty("authorization_revision").GetInt64());
            Assert.Equal(SecurityCommandStates.CommittedAwaitingProjection, body.GetProperty("command_state_at_commit").GetString());
        }

        // Staged through the command's one scope before its single commit, together with the command row and its authorization entry.
        var calls = recording.Calls;
        var commit = calls.IndexOf(nameof(IAuthorityScope.CommitAsync));
        Assert.Equal(1, calls.Count(c => c == nameof(IAuthorityScope.CommitAsync)));
        Assert.True(calls.LastIndexOf(nameof(IAuthorityScope.AddJournalEntry)) < commit);
        Assert.True(calls.IndexOf(nameof(IAuthorityScope.AddCommand)) < commit);
        Assert.Equal(3, calls.Take(commit).Count(c => c == nameof(IAuthorityScope.AddJournalEntry))); // 1 authorization + 2 audit

        AssertCanonicalCommandBodyUnchanged(6);
        Assert.Equal(new long[] { 1, 2 }, entries.Select(e => e.JournalStreamRevision).Order());
    }

    [Fact]
    public async Task ARevokeWithoutDependentsWritesNoInvalidationEntry()
    {
        var root = AddShare();
        await RevokeAsync(root.Resource.ResourceEntityId, "revoke-leaf-1");
        Assert.Empty(Entries(ShareLifecycleJournal.ShareInvalidated));
        Assert.DoesNotContain(store.Journal, e => e.JournalStreamName == JournalStreamNames.Audit);
    }

    [Fact]
    public async Task AReplayAndARejectedCommandWriteNoShareLifecycleEntry()
    {
        var root = AddShare();
        var child = AddShare(root.Metadata);
        var first = await RevokeAsync(root.Resource.ResourceEntityId, "revoke-replay-1");
        var auditCount = store.Journal.Count(e => e.JournalStreamName == JournalStreamNames.Audit);

        var replay = await RevokeAsync(root.Resource.ResourceEntityId, "revoke-replay-1", revision: 6);
        Assert.Equal(first.CommandId, replay.CommandId);
        Assert.Equal(auditCount, store.Journal.Count(e => e.JournalStreamName == JournalStreamNames.Audit));

        // A stale organization revision under a new key: a rejected command row, no lifecycle evidence.
        var other = AddShare();
        AddShare(other.Metadata);
        var stale = await Assert.ThrowsAsync<AuthorityException>(() => RevokeAsync(other.Resource.ResourceEntityId, "revoke-stale-1", revision: 5));
        Assert.Equal(AuthorityErrorCodes.StaleAuthorizationRevision, stale.ErrorCode);
        Assert.Equal(auditCount, store.Journal.Count(e => e.JournalStreamName == JournalStreamNames.Audit));
        Assert.Single(Entries(ShareLifecycleJournal.ShareInvalidated), e => Body(e).GetProperty("record_share_id").GetGuid() == child.Resource.ResourceEntityId);
    }

    [Fact]
    public async Task AnAcceptedScopeUpdateStagesOneRedactedScopeEntryAtTheCommittedRevision()
    {
        var share = AddShare();
        var expiry = DateTimeOffset.UtcNow.AddDays(3);
        var status = await UpdateAsync(share.Resource.ResourceEntityId, 1, [Item, OtherItem], RecipientRoles.Contributor, expiry, "update-scope-1");

        var entry = Assert.Single(Entries(ShareLifecycleJournal.ShareScopeUpdated));
        var body = Body(entry);
        Assert.Equal(status.CommandId, body.GetProperty("security_command_id").GetGuid());
        Assert.Equal(share.Resource.ResourceEntityId, body.GetProperty("record_share_id").GetGuid());
        Assert.Equal(Record, body.GetProperty("form_record_id").GetGuid());
        Assert.Equal(1, body.GetProperty("previous_share_revision").GetInt64());
        Assert.Equal(2, body.GetProperty("share_revision").GetInt64());
        Assert.Equal(6, body.GetProperty("authorization_revision").GetInt64());
        Assert.Equal("contributor", body.GetProperty("recipient_role").GetString());
        Assert.True(body.GetProperty("role_changed").GetBoolean());
        Assert.Equal(2, body.GetProperty("field_item_count").GetInt32());
        Assert.Equal(1, body.GetProperty("field_items_added").GetInt32());
        Assert.Equal(0, body.GetProperty("field_items_removed").GetInt32());
        Assert.True(body.GetProperty("expiry_changed").GetBoolean());
        Assert.True(body.GetProperty("has_expiry").GetBoolean());
        Assert.Equal(SecurityCommandStates.CommittedAwaitingProjection, body.GetProperty("command_state_at_commit").GetString());
        Assert.Equal(2, Assert.Single(store.ShareMetadata, m => m.RecordShareId == share.Resource.ResourceEntityId).ShareRevision);

        var json = entry.JournalEventBodyJson;
        foreach (var forbidden in new[] { Item.ToString("D"), OtherItem.ToString("D"), HumanCommandStepUpGate.SyntheticStepUpEvidence, Prose,
                     expiry.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), Recipient.ToString("D") })
        {
            Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Empty(Entries(ShareLifecycleJournal.ShareInvalidated));
        AssertCanonicalCommandBodyUnchanged(6);
    }

    [Fact]
    public async Task ARejectedOrReplayedScopeUpdateWritesNoScopeEntry()
    {
        var share = AddShare();
        var first = await UpdateAsync(share.Resource.ResourceEntityId, 1, [Item, OtherItem], RecipientRoles.Reader, null, "update-scope-2");
        var replay = await UpdateAsync(share.Resource.ResourceEntityId, 1, [Item, OtherItem], RecipientRoles.Reader, null, "update-scope-2", revision: 6);
        Assert.Equal(first.CommandId, replay.CommandId);
        var stale = await Assert.ThrowsAsync<AuthorityException>(() =>
            UpdateAsync(share.Resource.ResourceEntityId, 1, [Item], RecipientRoles.Reader, null, "update-scope-3", revision: 6));
        Assert.Equal(AuthorityErrorCodes.StaleShareRevision, stale.ErrorCode);

        var entry = Assert.Single(Entries(ShareLifecycleJournal.ShareScopeUpdated));
        Assert.False(Body(entry).GetProperty("role_changed").GetBoolean());
        Assert.False(Body(entry).GetProperty("expiry_changed").GetBoolean());
        Assert.Equal(SecurityCommandStates.Rejected, store.Commands.Single(c => c.CommandIdempotencyKey == "update-scope-3").CommandStateName);
    }

    [Fact]
    public async Task TheTimelineShowsOnlyAllowlistedFieldsAndNamesTheWithheldOnes()
    {
        var root = AddShare();
        AddShare(root.Metadata);
        await RevokeAsync(root.Resource.ResourceEntityId, "revoke-timeline-1");
        var entry = Assert.Single(Entries(ShareLifecycleJournal.ShareInvalidated));

        var envelope = SecurityCommandTimelineCatalog.BuildAuditEnvelope(entry.JournalEventKind, entry.JournalEventBodyJson);
        Assert.Equal(["security_command_id", "command_kind", "form_record_id", "record_share_id", "upstream_record_share_id", "invalidation_reason",
            "authorization_revision", "command_state_at_commit", "correlation_id"], envelope.Details.Select(d => d.Key));
        Assert.Empty(envelope.WithheldFields);

        // A body carrying prohibited fields (as a forged or future writer might) shows none of them and names their classes only.
        using var stored = JsonDocument.Parse(entry.JournalEventBodyJson);
        var forged = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["envelope_format"] = stored.RootElement.GetProperty("envelope_format").GetString(),
            ["body"] = new Dictionary<string, object?>
            {
                ["record_share_id"] = Guid.NewGuid(),
                ["field_item_ids"] = new[] { Item },
                ["share_expires_on"] = DateTimeOffset.UtcNow,
                ["step_up_evidence"] = HumanCommandStepUpGate.SyntheticStepUpEvidence,
                ["reason"] = Prose,
                ["invalidation_reason"] = "not a code because it has spaces",
            },
        });
        var redacted = SecurityCommandTimelineCatalog.BuildAuditEnvelope(ShareLifecycleJournal.ShareScopeUpdated, forged);
        Assert.Equal(["record_share_id"], redacted.Details.Select(d => d.Key));
        Assert.Equal(["reason", "share_expiry", "share_scope", "step_up_evidence"], redacted.WithheldFields);
        Assert.DoesNotContain(Prose, JsonSerializer.Serialize(redacted), StringComparison.Ordinal);
    }

    [Fact]
    public void BothKindsAreAllowlistedAuditKindsAndNeitherIsRejectionEvidence()
    {
        Assert.True(SecurityCommandTimelineCatalog.AuditKinds.ContainsKey(ShareLifecycleJournal.ShareScopeUpdated));
        Assert.True(SecurityCommandTimelineCatalog.AuditKinds.ContainsKey(ShareLifecycleJournal.ShareInvalidated));
        Assert.DoesNotContain(ShareLifecycleJournal.ShareScopeUpdated, SecurityCommandTimelineCatalog.RejectionEvidenceKinds);
        Assert.DoesNotContain(ShareLifecycleJournal.ShareInvalidated, SecurityCommandTimelineCatalog.RejectionEvidenceKinds);
    }

    private void AssertCanonicalCommandBodyUnchanged(long revision)
    {
        var committed = Assert.Single(store.Journal, e => e.JournalStreamName == JournalStreamNames.Authorization && e.JournalStreamRevision == revision);
        Assert.Equal("SecurityCommandCommitted", committed.JournalEventKind);
        Assert.Equal(CanonicalCommandBodyFields.Order(StringComparer.Ordinal), Body(committed).EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    private List<SecurityJournalEntry> Entries(string kind) =>
        [.. store.Journal.Where(e => e.JournalStreamName == JournalStreamNames.Audit && e.JournalEventKind == kind)];

    private static JsonElement Body(SecurityJournalEntry entry)
    {
        using var document = JsonDocument.Parse(entry.JournalEventBodyJson);
        return document.RootElement.GetProperty("body").Clone();
    }

    private (AuthorityResource Resource, ShareResourceMetadata Metadata) AddShare(ShareResourceMetadata? parent = null)
    {
        var id = Guid.NewGuid();
        var resource = new AuthorityResource
        {
            OrganizationId = Organization, ResourceEntityId = id, ResourceTypeName = ResourceTypeNames.RecordShare, ParentResourceEntityId = Record,
        };
        var metadata = new ShareResourceMetadata
        {
            OrganizationId = Organization, RecordShareId = id, ParentRecordShareId = parent?.RecordShareId, DelegationDepth = (parent?.DelegationDepth ?? 0) + 1,
        };
        store.Resources.Add(resource);
        store.ShareMetadata.Add(metadata);
        AddEdge(ResourceTypeNames.RecordShare, id, "reader", ResourceTypeNames.User, Recipient);
        AddEdge(ResourceTypeNames.FormRecord, Record, "shared_via", ResourceTypeNames.RecordShare, id);
        AddEdge(ResourceTypeNames.FieldItem, Item, "shared_via", ResourceTypeNames.RecordShare, id);
        return (resource, metadata);
    }

    private void AddEdge(string resourceType, Guid resource, string relation, string subjectType, Guid subject) =>
        store.Edges.Add(new AuthorityRelationshipEdge
        {
            AuthorityRelationshipEdgeId = Guid.NewGuid(), OrganizationId = Organization, ResourceTypeName = resourceType,
            ResourceEntityId = resource, RelationshipName = relation, SubjectTypeName = subjectType, SubjectEntityId = subject,
        });

    private SecurityCommandService Service() =>
        new(recording, new NoOpFaultInjector(), TimeProvider.System, FieldSearchApprovalGate.Unavailable, humanCommandGate: Gate);

    private Task<OperationStatus> RevokeAsync(Guid shareId, string key, long revision = 5)
    {
        var payload = new RevokeSharePayload(shareId, Record, HumanCommandStepUpGate.SyntheticStepUpEvidence);
        return Submit(SecurityCommandKind.RevokeShare, payload, key, revision);
    }

    private Task<OperationStatus> UpdateAsync(Guid shareId, long expectedShareRevision, IReadOnlyList<Guid> items, string role, DateTimeOffset? expiry, string key, long revision = 5)
    {
        var payload = new UpdateShareScopePayload(shareId, Record, role, items, expectedShareRevision, HumanCommandStepUpGate.SyntheticStepUpEvidence,
            ShareExpiresOn: expiry);
        return Submit(SecurityCommandKind.UpdateShareScope, payload, key, revision);
    }

    private Task<OperationStatus> Submit(SecurityCommandKind kind, object payload, string key, long revision)
    {
        var request = new SecurityCommandRequest(Organization, kind, new ResourceRef(ResourceKind.FormRecord, Record), revision,
            JsonSerializer.SerializeToElement(payload, CanonicalJson.Options), Prose);
        return Service().SubmitAsync(new VerifiedActor("https://idp", Owner.ToString("N"), "test"), request, key, Guid.NewGuid(), TestContext.Current.CancellationToken);
    }

    /// <summary>The in-memory store, with every scope wrapped in a recorder of the scope methods called on it, in order.</summary>
    private sealed class RecordingStore(InMemoryAuthorityStore inner) : IAuthorityStore
    {
        public List<string> Calls { get; } = [];

        public async Task<IAuthorityScope> BeginScopeAsync(CancellationToken cancellationToken)
        {
            Calls.Clear();
            var proxy = DispatchProxy.Create<IAuthorityScope, ScopeRecorder>();
            ((ScopeRecorder)(object)proxy).Attach(await inner.BeginScopeAsync(cancellationToken).ConfigureAwait(false), Calls);
            return proxy;
        }

        public Task<IAuthorityScope> BeginReadOnlySnapshotScopeAsync(CancellationToken cancellationToken) => inner.BeginReadOnlySnapshotScopeAsync(cancellationToken);

        public Task<IReadOnlyList<Guid>> GetOrganizationsWithPendingWorkAsync(CancellationToken cancellationToken) => inner.GetOrganizationsWithPendingWorkAsync(cancellationToken);

        public Task<IReadOnlyList<Guid>> GetAllOrganizationIdsAsync(CancellationToken cancellationToken) => inner.GetAllOrganizationIdsAsync(cancellationToken);

        public Task<IReadOnlyList<OrganizationChangeMarker>> GetOrganizationChangeMarkersAsync(CancellationToken cancellationToken) => inner.GetOrganizationChangeMarkersAsync(cancellationToken);
    }
}

/// <summary>Forwards every <see cref="IAuthorityScope"/> call to the real scope and records the method name.</summary>
public class ScopeRecorder : DispatchProxy
{
    private IAuthorityScope? target;
    private List<string>? calls;

    internal void Attach(IAuthorityScope scope, List<string> log)
    {
        target = scope;
        calls = log;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        calls!.Add(targetMethod.Name);
        try
        {
            return targetMethod.Invoke(target, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
    }
}
