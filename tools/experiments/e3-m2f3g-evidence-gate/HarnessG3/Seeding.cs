// E3-M2f-3G controlled test setup (disposable databases only). Organizations are seeded as the onboarding and content services would
// leave them (superuser session with session_replication_role = replica, as in the E3-M2e-2 harness); census snapshots, earlier-hour
// evidence and malformed evidence are written through the REAL runtime store and the REAL AuditJournal (digest chain intact). No
// data-key binding is ever written: every organization of this gate is unbound (level-1 organization floor).
using System.Security.Cryptography;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Application.Features.Journal;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Catalogue;
using AuthorizationAuthority.Domain.Features.Content;
using AuthorizationAuthority.Domain.Features.Keys;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using AuthorizationAuthority.Infrastructure.Persistence;
using EnterpriseForms.Integrity.Merkle;
using Microsoft.EntityFrameworkCore;
using Npgsql;

internal sealed class Org(Guid id, Guid owner, Guid binding, string database)
{
    public Guid Id { get; } = id;

    public Guid Owner { get; } = owner;

    public Guid Binding { get; } = binding;

    public string Database { get; } = database;

    public List<WRec> Records { get; } = [];

    /// <summary>A migration-verification snapshot owned by the verifier workload (census route), if seeded.</summary>
    public Guid? Snapshot { get; set; }

    public string Short => Id.ToString("N")[..8];
}

/// <summary>A record of a seeded organization, with the state an in-process level-2 writer needs for its next v1 change.</summary>
internal sealed class WRec(Org org, Guid id)
{
    public Org Org { get; } = org;

    public Guid Id { get; } = id;

    public Guid TextField { get; } = Guid.CreateVersion7();

    public Guid TextItem { get; } = Guid.CreateVersion7();

    public Guid SecretField { get; } = Guid.CreateVersion7();

    public Guid SecretItem { get; } = Guid.CreateVersion7();

    public List<(Staged Row, byte[] Signature)> Accepted { get; set; } = [];

    private int writes;

    public int NextWrite() => Interlocked.Increment(ref writes);

    public List<StagedContentItem> AcceptedStaged() => [.. Accepted.Select(a => new StagedContentItem(a.Row.Kind, a.Row.ItemId, a.Row.Base64, Convert.ToBase64String(a.Signature)))];
}

internal sealed partial class Lab
{
    internal const string HostUserIssuer = "https://test-idp.m2f3g.invalid";

    /// <summary>A disposable, unbound organization with <paramref name="records"/> records (resource, empty content head, owner edge each).</summary>
    private async Task<Org> SeedOrgAsync(string database, int records = 2)
    {
        var org = new Org(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), database);
        var seedCs = Target.Require(new NpgsqlConnectionStringBuilder(adminCs) { Options = "-c session_replication_role=replica", ApplicationName = "g3-seed", Database = database, Pooling = false }.ConnectionString);
        await using var context = new AuthorityDbContext(new DbContextOptionsBuilder<AuthorityDbContext>().UseNpgsql(seedCs).Options);
        var now = DateTimeOffset.UtcNow;
        context.OrganizationStates.Add(new OrganizationAuthorizationState
        {
            OrganizationAuthorizationStateId = Guid.CreateVersion7(), OrganizationId = org.Id, OrganizationDisplayName = "M2f3g gate", AuthorizationRevision = 7, ProjectionAppliedRevision = 7, ProjectionZedToken = "zed-7",
            CheckpointEnforcementModeName = CheckpointEnforcementModes.Asynchronous, StateModifiedOn = now, LatestSecurityJournalDigest = SHA256.HashData(org.Id.ToByteArray()),
        });
        context.IdentityBindings.Add(new IdentityBinding { IdentityBindingId = org.Binding, OrganizationId = org.Id, AuthorityUserId = org.Owner, IdentityIssuerUri = HostUserIssuer, IdentitySubjectIdentifier = org.Owner.ToString("D"), IdentityBindingEnabled = true, IdentityCreatedOn = now });
        context.KeyRegistrations.Add(new IntegrityKeyRegistration { IntegrityKeyRegistrationId = Guid.CreateVersion7(), OrganizationId = org.Id, ProviderName = "LocalDevelopment", ProviderKeyName = "m2f3g-key-" + org.Id.ToString("N"), CurrentSigningKeyVersion = 1, MinimumVerificationKeyVersion = 1, KeyRegisteredOn = now, KeyModifiedOn = now });
        for (var i = 0; i < records; i++)
        {
            var record = new WRec(org, Guid.CreateVersion7());
            org.Records.Add(record);
            context.Resources.Add(new AuthorityResource { AuthorityResourceId = Guid.CreateVersion7(), OrganizationId = org.Id, ResourceTypeName = ResourceTypeNames.FormRecord, ResourceEntityId = record.Id, ParentResourceEntityId = Rows.Form, RegisteredBySecurityCommandId = Guid.CreateVersion7(), ResourceRegisteredOn = now });
            context.ContentHeads.Add(new RecordContentHead { RecordContentHeadId = Guid.CreateVersion7(), OrganizationId = org.Id, FormRecordId = record.Id, CommittedManifestRoot = MerkleManifestV1.EmptyRoot(), SignatureKeyVersion = 1, ContentHeadModifiedOn = now });
            context.RelationshipEdges.Add(new AuthorityRelationshipEdge { AuthorityRelationshipEdgeId = Guid.CreateVersion7(), OrganizationId = org.Id, ResourceTypeName = ResourceTypeNames.FormRecord, ResourceEntityId = record.Id, RelationshipName = "owner", SubjectTypeName = ResourceTypeNames.User, SubjectEntityId = org.Owner, SubjectRelationName = string.Empty, CreatedBySecurityCommandId = Guid.CreateVersion7(), RelationshipCreatedOn = now });
        }

        await context.SaveChangesAsync();
        return org;
    }

    /// <summary>
    /// A migration-verification snapshot of the verifier workload fixing the first record's head (an Effective PublishFormSecurityRevision
    /// scoping it), written through the real runtime store in one locked transaction, as the catalogue service would. The census route
    /// loads it before the organization lock; the capability check runs right after that lock.
    /// </summary>
    private async Task SeedCensusSnapshotAsync(Org org)
    {
        var store = Store(org.Database, "g3-census-seed");
        await using var scope = await store.BeginScopeAsync(CancellationToken.None);
        var state = await scope.LockOrganizationAsync(org.Id, CancellationToken.None) ?? throw new InvalidOperationException("organization");
        var now = DateTimeOffset.UtcNow;
        var command = new SecurityCommand
        {
            SecurityCommandId = Guid.CreateVersion7(), OrganizationId = org.Id, ActorIdentityBindingId = org.Binding, CommandKindName = SecurityCommandKinds.PublishFormSecurityRevision,
            CommandIdempotencyKey = "m2f3g-census-" + Guid.CreateVersion7().ToString("N"), CommandRequestDigest = RandomNumberGenerator.GetBytes(32), ExpectedAuthorizationRevision = state.AuthorizationRevision,
            CommittedAuthorizationRevision = state.AuthorizationRevision, CommandStateName = SecurityCommandStates.Effective,
            CommandPayloadJson = CanonicalJson.Serialize(new PublishFormSecurityRevisionPayload(Rows.Form, org.Records[0].SecretField, "ApplicationEncrypted", 1)), CorrelationIdentifier = Guid.CreateVersion7(),
            CommandCreatedOn = now, CommandModifiedOn = now, CommandEffectiveOn = now,
        };
        scope.AddCommand(command);
        var snapshot = new SearchCatalogueSnapshot
        {
            SearchCatalogueSnapshotId = Guid.CreateVersion7(), OrganizationId = org.Id, SnapshotScopeKind = CatalogueScopeKinds.MigrationVerification, RequestingWorkloadName = CapabilityRefusalEvidence.Workloads.FieldProtectionVerifier,
            ScopingSecurityCommandId = command.SecurityCommandId, FormDefinitionId = Rows.Form, SnapshotAuthorizationRevision = state.AuthorizationRevision, SnapshotProjectionRevision = state.ProjectionAppliedRevision,
            SnapshotContentIntegrityRevision = state.ContentIntegrityRevision, CatalogueEntryCount = 1, CatalogueDigest = RandomNumberGenerator.GetBytes(32), SnapshotIssuedOn = now, SnapshotExpiresOn = now.AddHours(6),
        };
        scope.AddCatalogueSnapshot(snapshot, [new SearchCatalogueSnapshotEntry
        {
            SearchCatalogueSnapshotEntryId = Guid.CreateVersion7(), SearchCatalogueSnapshotId = snapshot.SearchCatalogueSnapshotId, OrganizationId = org.Id, FormRecordId = org.Records[0].Id,
            CatalogueEntryPosition = 0, CommittedContentRevision = 0, CommittedOperationId = null, ContentHeadQuarantined = false, ResourceLifecycleState = ResourceLifecycleStates.Active,
        }]);
        await scope.CommitAsync(CancellationToken.None);
        org.Snapshot = snapshot.SearchCatalogueSnapshotId;
    }

    /// <summary>
    /// Controlled setup: an EnvelopeCapabilityRefused audit entry with <paramref name="body"/> appended through the REAL AuditJournal on
    /// the REAL runtime store under the organization row lock (valid digest and chain). Used for earlier-hour history, a future-dated row
    /// (clock regression), duplicate keys and malformed bodies. Never used where the scenario claims the recorder wrote the row.
    /// </summary>
    private async Task AppendAuditAsync(Org org, string eventKind, object body)
    {
        var store = Store(org.Database, "g3-history-seed");
        await using var scope = await store.BeginScopeAsync(CancellationToken.None);
        var state = await scope.LockOrganizationAsync(org.Id, CancellationToken.None) ?? throw new InvalidOperationException("organization");
        AuditJournal.Append(scope, state, eventKind, body, TimeProvider.System);
        await scope.CommitAsync(CancellationToken.None);
    }

    /// <summary>A well-formed evidence body for <paramref name="entryPoint"/> at database time <paramref name="at"/> (bucket = its UTC hour).</summary>
    private static CapabilityRefusalEvidenceBody EvidenceBody(Org org, string entryPoint, DateTimeOffset at) =>
        new(CapabilityRefusalEvidence.SchemaVersion, CapabilityRefusalEvidence.RefusalCode, org.Id, CapabilityRefusalEvidence.WorkloadOf(entryPoint), entryPoint, 1, at, CapabilityRefusalEvidence.BucketOf(at), at);

    /// <summary>
    /// Controlled setup: a mature organization's audit history of <paramref name="rows"/> unrelated entries (kind FieldSearchExecuted,
    /// digest = SHA-256 of the stored bytes, archived), and the organization row's audit head moved to the last one. Plus a smaller number
    /// of unrelated entries spread over <paramref name="otherOrganizations"/> other organizations: a quarter as many in total.
    /// </summary>
    private async Task SeedMatureHistoryAsync(Org org, long rows, IReadOnlyList<Org> otherOrganizations)
    {
        const string body = "{\"synthetic\":\"m2f3g\"}";
        await ExecAsync(org.Database, $"""
            INSERT INTO authority.security_journal_entries (security_journal_entry_id, organization_id, journal_stream_name, journal_stream_revision, journal_event_kind,
                journal_event_body_json, journal_event_digest, archive_delivery_state, archive_attempt_count, journal_created_on)
            SELECT gen_random_uuid(), '{org.Id:D}', 'audit', g, 'FieldSearchExecuted', '{body}', sha256(convert_to('{body}', 'UTF8')), 'Anchored', 1, timestamptz '2026-01-01 00:00:00+00' + g * interval '1 second'
            FROM generate_series(1::bigint, {rows}::bigint) g
            """);
        await ExecAsync(org.Database, $"UPDATE authority.organization_authorization_states SET audit_journal_revision = {rows}, latest_audit_journal_digest = sha256(convert_to('{body}', 'UTF8')) WHERE organization_id = '{org.Id:D}'");
        var each = otherOrganizations.Count == 0 ? 0 : rows / 4 / otherOrganizations.Count;
        foreach (var other in otherOrganizations)
        {
            await ExecAsync(org.Database, $"""
                INSERT INTO authority.security_journal_entries (security_journal_entry_id, organization_id, journal_stream_name, journal_stream_revision, journal_event_kind,
                    journal_event_body_json, journal_event_digest, archive_delivery_state, archive_attempt_count, journal_created_on)
                SELECT gen_random_uuid(), '{other.Id:D}', 'audit', g, 'FieldSearchExecuted', '{body}', sha256(convert_to('{body}', 'UTF8')), 'Anchored', 1, timestamptz '2026-01-01 00:00:00+00' + g * interval '1 second'
                FROM generate_series(1::bigint, {each}::bigint) g
                """);
            await ExecAsync(org.Database, $"UPDATE authority.organization_authorization_states SET audit_journal_revision = {each}, latest_audit_journal_digest = sha256(convert_to('{body}', 'UTF8')) WHERE organization_id = '{other.Id:D}'");
        }
    }
}
