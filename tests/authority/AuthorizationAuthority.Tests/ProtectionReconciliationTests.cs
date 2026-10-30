using System.Reflection;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Catalogue;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Catalogue;
using AuthorizationAuthority.Domain.Features.Content;
using AuthorizationAuthority.Domain.Features.FieldSearch;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Keys;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using AuthorizationAuthority.Infrastructure.Faults;
using AuthorizationAuthority.Infrastructure.Signing;
using EnterpriseForms.Integrity.Canonical;
using EnterpriseForms.Integrity.Merkle;
using Xunit;
using static AuthorizationAuthority.Tests.ContributorWriteFixtures;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// Phase C7 M4 — G19 protection reconciliation at unit level: the real catalogue, content and reconciliation services, real
/// signed manifests (LocalHmacProvider) and the in-memory store. Counts must come from verified canonical bytes and Authority
/// state only; the real PostgreSQL/SpiceDB/Vault path and the hostile business DBA run in the Stage B integration suite
/// (ProtectionReconciliationTests).
/// </summary>
public sealed class ProtectionReconciliationTests
{
    private const string Issuer = "https://issuer.test";
    private const string Verifier = "field-protection-verifier";

    private sealed class Harness
    {
        public Guid Org { get; } = Guid.CreateVersion7();
        public Guid FormA { get; } = Guid.CreateVersion7();
        public Guid FormB { get; } = Guid.CreateVersion7();
        public Guid Field { get; } = Guid.CreateVersion7();
        public Guid OtherField { get; } = Guid.CreateVersion7();
        public Guid Owner { get; } = Guid.CreateVersion7();
        public InMemoryAuthorityStore Store { get; } = new();
        public SearchCatalogueTests.SpiceDb Spice { get; }
        public ContentService Content { get; }
        public SearchCatalogueService Catalogue { get; }
        public ProtectionReconciliationService Reconciliation { get; }
        public Dictionary<Guid, List<StagedContentItem>> Accepted { get; } = [];
        public Dictionary<Guid, Guid> AcceptedOperation { get; } = [];

        private readonly Dictionary<Guid, Guid> items = [];
        private byte seed = 10;

        public Harness()
        {
            var mac = new LocalHmacProvider();
            mac.ProvisionKeyAsync(Org, IntegrityKeyPurposes.RowIntegrityV1, "org-key", CancellationToken.None).GetAwaiter().GetResult();
            var spice = DispatchProxy.Create<ISpiceDbProjection, SearchCatalogueTests.SpiceDb>();
            Spice = (SearchCatalogueTests.SpiceDb)(object)spice;
            Content = new ContentService(Store, mac, spice, new NoOpFaultInjector(), TimeProvider.System, new ShareContributorWriteSwitch(false, testingOverrideAllowed: true));
            Catalogue = new SearchCatalogueService(Store, spice, TimeProvider.System, new CatalogueOptions { MigrationPageSize = 1000 });
            Reconciliation = new ProtectionReconciliationService(Store, Content, TimeProvider.System);
            Store.Organizations.Add(new OrganizationAuthorizationState { OrganizationId = Org, AuthorizationRevision = 7, ProjectionAppliedRevision = 7, ProjectionZedToken = "zed-7", CheckpointEnforcementModeName = CheckpointEnforcementModes.Asynchronous });
            Store.Bindings.Add(new IdentityBinding { IdentityBindingId = Guid.CreateVersion7(), OrganizationId = Org, AuthorityUserId = Owner, IdentityIssuerUri = Issuer, IdentitySubjectIdentifier = Owner.ToString("D"), IdentityBindingEnabled = true });
            Store.Keys.Add(new IntegrityKeyRegistration { IntegrityKeyRegistrationId = Guid.CreateVersion7(), OrganizationId = Org, ProviderName = "LocalDevelopment", ProviderKeyName = "org-key", CurrentSigningKeyVersion = 1, MinimumVerificationKeyVersion = 1 });
            foreach (var form in new[] { FormA, FormB })
            {
                Store.Resources.Add(new AuthorityResource { AuthorityResourceId = Guid.CreateVersion7(), OrganizationId = Org, ResourceTypeName = ResourceTypeNames.FormDefinition, ResourceEntityId = form, ResourceLifecycleState = ResourceLifecycleStates.Active });
            }
        }

        public OrganizationAuthorizationState State => Store.Organizations.Single();

        public Guid ItemFor(Guid record) => items.TryGetValue(record, out var item) ? item : items[record] = Guid.CreateVersion7();

        public RecordContentHead Head(Guid record) => Store.Heads.Single(h => h.FormRecordId == record);

        /// <summary>A record of <paramref name="form"/> (not discoverable by anyone) with one plaintext value of <see cref="Field"/> unless <paramref name="withContent"/> is false.</summary>
        public async Task<Guid> RecordAsync(Guid form, bool withContent = true, string lifecycle = ResourceLifecycleStates.Active)
        {
            var record = Guid.CreateVersion7();
            Store.Resources.Add(new AuthorityResource { AuthorityResourceId = Guid.CreateVersion7(), OrganizationId = Org, ResourceTypeName = ResourceTypeNames.FormRecord, ResourceEntityId = record, ParentResourceEntityId = form, ResourceLifecycleState = ResourceLifecycleStates.Active });
            Store.Heads.Add(new RecordContentHead { RecordContentHeadId = Guid.CreateVersion7(), OrganizationId = Org, FormRecordId = record, CommittedManifestRoot = MerkleManifestV1.EmptyRoot(), SignatureKeyVersion = 1 });
            Store.Edges.Add(new AuthorityRelationshipEdge { AuthorityRelationshipEdgeId = Guid.CreateVersion7(), OrganizationId = Org, ResourceTypeName = ResourceTypeNames.FormRecord, ResourceEntityId = record, RelationshipName = "owner", SubjectTypeName = ResourceTypeNames.User, SubjectEntityId = Owner, SubjectRelationName = string.Empty });
            if (withContent)
            {
                await WritePlainAsync(record, "plain value");
            }

            Store.Resources.Single(r => r.ResourceEntityId == record).ResourceLifecycleState = lifecycle;
            return record;
        }

        public Task WritePlainAsync(Guid record, string value, string status = "Draft") =>
            WriteAsync(record, [Header(Org, record, status), Text(Org, record, Field, ItemFor(record), value), Integer(Org, record, OtherField, Guid.CreateVersion7(), 3)]);

        /// <summary>The field's value as an encrypted row of the same item (what a completed reprotection leaves), plus extra rows.</summary>
        public Task WriteSealedAsync(Guid record, Func<Row, Row>? alter = null, params Row[] extra)
        {
            var sealedRow = Sealed(Org, record, Field, ItemFor(record), seed++);
            return WriteAsync(record, [Header(Org, record), alter is null ? sealedRow : alter(sealedRow), .. extra]);
        }

        /// <summary>An owner content commit (Phase C5 path); the protection barrier applies once a decision exists.</summary>
        public async Task WriteAsync(Guid record, List<Row> rows)
        {
            var head = Head(record);
            var operation = Guid.CreateVersion7();
            Spice.Allowed.Add((ResourceTypeNames.FormRecord, record.ToString("D"), "edit", Owner.ToString("D")));
            Spice.Allowed.Add((ResourceTypeNames.FormRecord, record.ToString("D"), "read", Owner.ToString("D")));
            var candidate = rows.Select(r => Encode(r, Org, operation, head.CommittedContentRevision + 1)).ToList();
            var current = Accepted.GetValueOrDefault(record);
            var request = new PrepareContentChangeRequest(Org, record, operation, State.AuthorizationRevision, head.CommittedContentRevision, Convert.ToBase64String(head.CommittedManifestRoot),
                candidate.Select(c => new CandidateContentItem(c.Kind, c.ItemId, c.Base64)).ToList(), CurrentItems: current);
            var prepared = await Content.PrepareAsync(new VerifiedActor(Issuer, Owner.ToString("D"), "test"), request, "key-" + operation.ToString("N"), Guid.CreateVersion7(), CancellationToken.None);
            var signatures = prepared.SignedItems.ToDictionary(s => (s.ItemKind, s.ItemId), s => s.RowSignatureBase64);
            var staged = candidate.Select(c => new StagedContentItem(c.Kind, c.ItemId, c.Base64, signatures[(c.Kind, c.ItemId)])).ToList();
            await Content.CommitAsync(new VerifiedActor(Issuer, Owner.ToString("D"), "test"), prepared.OperationId, new CommitContentChangeRequest(Org, record, staged), Guid.CreateVersion7(), CancellationToken.None);
            Spice.Allowed.Remove((ResourceTypeNames.FormRecord, record.ToString("D"), "edit", Owner.ToString("D")));
            Spice.Allowed.Remove((ResourceTypeNames.FormRecord, record.ToString("D"), "read", Owner.ToString("D")));
            Accepted[record] = staged;
            AcceptedOperation[record] = prepared.OperationId;
            foreach (var entry in Store.Journal)
            {
                entry.ArchiveDeliveryState = ArchiveDeliveryStates.Anchored;
            }
        }

        /// <summary>An effective protection decision of <paramref name="form"/>'s <see cref="Field"/> (Encrypt = requirement, Decrypt = release), the field's current one.</summary>
        public Guid Decision(Guid form, bool decrypt = false)
        {
            var command = new SecurityCommand
            {
                SecurityCommandId = Guid.CreateVersion7(),
                OrganizationId = Org,
                ActorIdentityBindingId = Store.Bindings[0].IdentityBindingId,
                CommandKindName = SecurityCommandKinds.PublishFormSecurityRevision,
                CommandStateName = SecurityCommandStates.Effective,
                CommandPayloadJson = System.Text.Encoding.UTF8.GetString(CanonicalJson.SerializeBytes(new PublishFormSecurityRevisionPayload(form, Field, decrypt ? "Standard" : "ApplicationEncrypted", 2))),
            };
            Store.Commands.Add(command);
            var type = decrypt ? ResourceTypeNames.FieldProtectionRelease : ResourceTypeNames.FieldProtectionRequirement;
            var existing = Store.Resources.FirstOrDefault(r => r.ResourceTypeName == type && r.ResourceEntityId == Field);
            if (existing is not null)
            {
                existing.RegisteredBySecurityCommandId = command.SecurityCommandId; // a later decision supersedes the earlier one
                existing.ParentResourceEntityId = form;
            }
            else
            {
                Store.Resources.Add(new AuthorityResource { AuthorityResourceId = Guid.CreateVersion7(), OrganizationId = Org, ResourceTypeName = type, ResourceEntityId = Field, ParentResourceEntityId = form, ResourceLifecycleState = ResourceLifecycleStates.Active, RegisteredBySecurityCommandId = command.SecurityCommandId });
            }

            return command.SecurityCommandId;
        }

        public async Task<List<CatalogueEntry>> CatalogueAsync(Guid form, Guid command, string workload = Verifier)
        {
            var page = await Catalogue.IssueMigrationCatalogueAsync(workload, new MigrationCatalogueRequest(Org, form, command), Guid.CreateVersion7(), CancellationToken.None);
            LastSnapshot = page.SearchCatalogueSnapshotId;
            return [.. page.Entries];
        }

        public Guid LastSnapshot { get; private set; }

        public ProtectionCensusItem Item(Guid record, IReadOnlyList<StagedContentItem>? observed = null) => new(record, AcceptedOperation[record], observed ?? Accepted[record]);

        public Task<ProtectionCensusResponse> CensusAsync(params ProtectionCensusItem[] records) =>
            Reconciliation.CensusAsync(Verifier, LastSnapshot, new ProtectionCensusRequest(records), Guid.CreateVersion7(), CancellationToken.None);

        /// <summary>A catch-up census of the same reconciliation: results verified since <paramref name="since"/> may be reused.</summary>
        public Task<ProtectionCensusResponse> CatchUpAsync(Guid since, params ProtectionCensusItem[] records) =>
            Reconciliation.CensusAsync(Verifier, LastSnapshot, new ProtectionCensusRequest(records, since), Guid.CreateVersion7(), CancellationToken.None);

        /// <summary>Catalogue, census of every contentful entry with its accepted rows, attestation.</summary>
        public async Task<ProtectionAttestation> ReconcileAsync(Guid form, Guid command)
        {
            var entries = await CatalogueAsync(form, command);
            var contentful = entries.Where(e => e.CommittedOperationId is not null && !e.ContentHeadQuarantined && !e.ResourceQuarantined).ToList();
            foreach (var chunk in contentful.Chunk(50))
            {
                await CensusAsync([.. chunk.Select(e => new ProtectionCensusItem(e.FormRecordId, e.CommittedOperationId!.Value, Accepted[e.FormRecordId]))]);
            }

            return await AttestAsync();
        }

        public Task<ProtectionAttestation> AttestAsync() => Reconciliation.AttestAsync(Verifier, LastSnapshot, Guid.CreateVersion7(), CancellationToken.None);
    }

    [Fact]
    public async Task TheCensusCountsTheFieldsRowsFromVerifiedBytesAndTheAttestationIsCompleteOnlyAfterEveryRecordIsConverted()
    {
        var h = new Harness();
        var first = await h.RecordAsync(h.FormA);
        var second = await h.RecordAsync(h.FormA);
        var command = h.Decision(h.FormA);

        var before = await h.ReconcileAsync(h.FormA, command);
        Assert.False(before.AttestationComplete);
        Assert.Equal(ProtectionAttestationReasons.OldFormRemaining, before.AttestationIncompleteReason);
        Assert.Equal(2, before.ExpectedRecordCount);
        Assert.Equal(2, before.OldFormRemainingRecordCount);
        Assert.Equal(2, before.OldFormRemainingOccurrenceCount);
        Assert.Equal(0, before.TargetFormOccurrenceCount);

        // A repeating section: two occurrences of the field in one record are both counted and both must be converted.
        var occurrence = Guid.CreateVersion7();
        await h.WriteSealedAsync(first, extra: [Group(h.Org, first, occurrence, 1), Sealed(h.Org, first, h.Field, Guid.CreateVersion7(), 90).With("form_record_group_id", RowValue.Uuid(occurrence))]);
        await h.WriteSealedAsync(second);
        var after = await h.ReconcileAsync(h.FormA, command);
        Assert.True(after.AttestationComplete, after.AttestationIncompleteReason);
        Assert.Equal(2, after.CompliantRecordCount);
        Assert.Equal(3, after.TargetFormOccurrenceCount);
        Assert.Equal(0, after.OldFormRemainingOccurrenceCount);
        Assert.Equal(2, after.RetainedHistoryRevisionCount); // the plaintext revisions remain as immutable history, reported, not claimed
        Assert.Contains("2 retained earlier revision(s)", after.CoverageStatement, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePopulationComesFromTheRegistryUndiscoverablePendingQuarantinedAndRetiredRecordsAreEachAccountedFor()
    {
        var h = new Harness();
        var converted = await h.RecordAsync(h.FormA);
        var pendingRegistered = await h.RecordAsync(h.FormA, withContent: false, lifecycle: ResourceLifecycleStates.Registered);
        var pendingActive = await h.RecordAsync(h.FormA, withContent: false);
        var quarantined = await h.RecordAsync(h.FormA);
        await h.RecordAsync(h.FormA, lifecycle: ResourceLifecycleStates.Revoked);
        await h.RecordAsync(h.FormB); // another form: never in this population
        var command = h.Decision(h.FormA);
        await h.WriteSealedAsync(converted);
        await h.WriteSealedAsync(quarantined);
        h.Head(quarantined).ContentHeadQuarantined = true;

        var attestation = await h.ReconcileAsync(h.FormA, command);
        Assert.Equal(4, attestation.ExpectedRecordCount);
        Assert.Equal(2, attestation.PendingCreationRecordCount);
        Assert.Equal(1, attestation.QuarantinedRecordCount);
        Assert.Equal(1, attestation.CompliantRecordCount);
        Assert.Equal(1, attestation.RetiredRecordCount);
        Assert.False(attestation.AttestationComplete);
        Assert.Equal(ProtectionAttestationReasons.Quarantined, attestation.AttestationIncompleteReason);
        _ = (pendingRegistered, pendingActive);

        // Without the quarantined record, pending creations alone do not prevent completion (they hold no accepted content).
        h.Head(quarantined).ContentHeadQuarantined = false;
        var later = await h.ReconcileAsync(h.FormA, command);
        Assert.True(later.AttestationComplete, later.AttestationIncompleteReason);
        Assert.Equal(2, later.PendingCreationRecordCount);
    }

    [Fact]
    public async Task TamperedMissingOrRolledBackRowsFailTheCensusQuarantineTheRecordAndBlockTheAttestation()
    {
        var h = new Harness();
        var tampered = await h.RecordAsync(h.FormA);
        var missing = await h.RecordAsync(h.FormA);
        var rolledBack = await h.RecordAsync(h.FormA);
        var plaintextRevision = h.Accepted[rolledBack]; // the earlier, validly signed plaintext revision a hostile DBA could restore
        var command = h.Decision(h.FormA);
        foreach (var record in new[] { tampered, missing, rolledBack })
        {
            await h.WriteSealedAsync(record);
        }

        await h.CatalogueAsync(h.FormA, command);
        // The DBA rewrites the converted row as plaintext with the accepted operation's binding: the digest no longer matches.
        var forged = h.Accepted[tampered].Select(i => i.ItemKind == ManifestItemKind.FieldValue && RowCanonicalDecoder.Decode(Convert.FromBase64String(i.CanonicalBytesBase64)).Column("field_definition_id")?.AsUuid() == h.Field
            ? i with { CanonicalBytesBase64 = Encode(Text(h.Org, tampered, h.Field, i.ItemId, "forged plaintext"), h.Org, h.AcceptedOperation[tampered], 2).Base64 }
            : i).ToList();
        var response = await h.CensusAsync(h.Item(tampered, forged), h.Item(missing, h.Accepted[missing].Take(1).ToList()), h.Item(rolledBack, plaintextRevision));
        var results = response.Results.ToDictionary(r => r.FormRecordId);
        Assert.Equal("ITEM_DIGEST_MISMATCH", results[tampered].Outcome);
        Assert.Equal("MISSING_ITEM", results[missing].Outcome);
        Assert.NotEqual(ProtectionCensusOutcomes.Verified, results[rolledBack].Outcome); // valid old signatures, wrong revision: never counted
        Assert.All(new[] { tampered, missing, rolledBack }, r => Assert.True(h.Head(r).ContentHeadQuarantined));
        Assert.Equal(3, h.Store.Journal.Count(j => j.JournalStreamName == JournalStreamNames.Content && j.JournalEventKind == "IntegrityVerificationFailed"));
        Assert.All(response.Results, r => Assert.Equal(0, r.OldFormOccurrenceCount));

        var attestation = await h.AttestAsync();
        Assert.False(attestation.AttestationComplete);
        Assert.Equal(3, attestation.QuarantinedRecordCount);
    }

    [Fact]
    public async Task ACensusOfAnEarlierHeadNeverCountsForANewerOneAndHeldResultsAreReusedOnlyWithinTheSameReconciliation()
    {
        var h = new Harness();
        var record = await h.RecordAsync(h.FormA);
        var command = h.Decision(h.FormA);
        await h.WriteSealedAsync(record);
        Assert.True((await h.ReconcileAsync(h.FormA, command)).AttestationComplete);
        var firstRound = h.LastSnapshot;

        // A new reconciliation (snapshot issued after the held census) never reuses the earlier result: the rows are verified again.
        await Task.Delay(5, TestContext.Current.CancellationToken);
        var entries = await h.CatalogueAsync(h.FormA, command);
        var laterRound = h.LastSnapshot;
        Assert.Equal(ProtectionCensusOutcomes.Verified, Assert.Single(entries).ProtectionCensusOutcome); // a hint only
        var fresh = Assert.Single((await h.CatchUpAsync(laterRound, h.Item(record))).Results);
        Assert.False(fresh.Reused);
        Assert.Equal(ProtectionCensusOutcomes.Verified, fresh.Outcome);
        // Verified again within this reconciliation: a catch-up round may now reuse it (the earlier round's cutoff also covers it).
        Assert.True(Assert.Single((await h.CatchUpAsync(laterRound, h.Item(record))).Results).Reused);
        Assert.True(Assert.Single((await h.CatchUpAsync(firstRound, h.Item(record))).Results).Reused);
        Assert.False(Assert.Single((await h.CensusAsync(h.Item(record))).Results).Reused); // no cutoff: never reused
        Assert.Single(h.Store.CensusResults);
        // A cutoff from another decision or form is ignored (no reuse).
        var otherForm = await h.RecordAsync(h.FormB);
        var otherCommand = h.Decision(h.FormB);
        await h.CatalogueAsync(h.FormB, otherCommand);
        var foreignCutoff = h.LastSnapshot;
        h.Store.Resources.Single(r => r.ResourceTypeName == ResourceTypeNames.FieldProtectionRequirement).RegisteredBySecurityCommandId = command;
        h.Store.Resources.Single(r => r.ResourceTypeName == ResourceTypeNames.FieldProtectionRequirement).ParentResourceEntityId = h.FormA;
        await h.CatalogueAsync(h.FormA, command);
        Assert.False(Assert.Single((await h.CatchUpAsync(foreignCutoff, h.Item(record))).Results).Reused);
        _ = otherForm;

        // Without reuse the rows are verified: a business copy tampered with after the earlier census is caught on the unchanged head.
        var rewritten = h.Accepted[record].Select(i => i.ItemKind == ManifestItemKind.FieldValue ? i with { CanonicalBytesBase64 = Encode(Text(h.Org, record, h.Field, i.ItemId, "plaintext again"), h.Org, h.AcceptedOperation[record], 2).Base64 } : i).ToList();
        var caught = Assert.Single((await h.CensusAsync(h.Item(record, rewritten))).Results);
        Assert.False(caught.Reused);
        Assert.Equal("ITEM_DIGEST_MISMATCH", caught.Outcome);
        Assert.True(h.Head(record).ContentHeadQuarantined);
        Assert.Equal(ProtectionAttestationReasons.Quarantined, (await h.AttestAsync()).AttestationIncompleteReason);
        h.Head(record).ContentHeadQuarantined = false; // (test only) continue with an intact head

        // A newer accepted revision (a user's edit) has no census yet: the attestation is incomplete until it is censused.
        await h.WriteSealedAsync(record);
        var stale = await h.AttestAsync();
        Assert.False(stale.AttestationComplete);
        Assert.Equal(ProtectionAttestationReasons.NotYetCensused, stale.AttestationIncompleteReason);
        Assert.Null(Assert.Single(await h.CatalogueAsync(h.FormA, command)).ProtectionCensusOutcome);
    }

    /// <summary>The production default ceiling, with synthetic registry rows: exactly 50,000 records are catalogued; one more is refused with nothing partial.</summary>
    [Fact]
    public async Task TheMigrationCatalogueAcceptsExactly50000RecordsAndRefuses50001WithNothingPartial()
    {
        var h = new Harness();
        var command = h.Decision(h.FormA);
        Assert.Equal(50_000, new CatalogueOptions().MaximumMigrationEntries);
        h.Store.Resources.AddRange(Enumerable.Range(0, 50_000).Select(_ => new AuthorityResource { AuthorityResourceId = Guid.CreateVersion7(), OrganizationId = h.Org, ResourceTypeName = ResourceTypeNames.FormRecord, ResourceEntityId = Guid.CreateVersion7(), ParentResourceEntityId = h.FormA, ResourceLifecycleState = ResourceLifecycleStates.Registered }));
        var page = await h.Catalogue.IssueMigrationCatalogueAsync(Verifier, new MigrationCatalogueRequest(h.Org, h.FormA, command), Guid.CreateVersion7(), CancellationToken.None);
        Assert.Equal(50_000, page.TotalEntryCount);
        Assert.Equal(1000, page.Entries.Count);
        var snapshots = h.Store.Snapshots.Count;

        h.Store.Resources.Add(new AuthorityResource { AuthorityResourceId = Guid.CreateVersion7(), OrganizationId = h.Org, ResourceTypeName = ResourceTypeNames.FormRecord, ResourceEntityId = Guid.CreateVersion7(), ParentResourceEntityId = h.FormA, ResourceLifecycleState = ResourceLifecycleStates.Registered });
        var refused = await Assert.ThrowsAsync<AuthorityException>(() => h.Catalogue.IssueMigrationCatalogueAsync(Verifier, new MigrationCatalogueRequest(h.Org, h.FormA, command), Guid.CreateVersion7(), CancellationToken.None));
        Assert.Equal(AuthorityErrorCodes.LimitExceeded, refused.ErrorCode);
        Assert.Equal(snapshots, h.Store.Snapshots.Count); // no snapshot, no entries
        Assert.Contains(h.Store.Journal, j => j.JournalEventKind == "MigrationCatalogueRefused");
    }

    [Fact]
    public async Task OnlyTheSnapshotsOwnWorkListAtItsHeadIsCensusedAndOnlyByTheIssuingVerifier()
    {
        var h = new Harness();
        var inScope = await h.RecordAsync(h.FormA);
        var otherForm = await h.RecordAsync(h.FormB);
        var command = h.Decision(h.FormA);
        await h.CatalogueAsync(h.FormA, command);

        var results = (await h.CensusAsync(h.Item(otherForm), new ProtectionCensusItem(inScope, Guid.CreateVersion7(), h.Accepted[inScope]))).Results;
        Assert.All(results, r => Assert.Equal(ContentService.NotInScope, r.Outcome));
        Assert.Empty(h.Store.CensusResults);
        await Assert.ThrowsAsync<AuthorityException>(() => h.Reconciliation.CensusAsync("another-verifier", h.LastSnapshot, new ProtectionCensusRequest([h.Item(inScope)]), Guid.CreateVersion7(), CancellationToken.None));
        await Assert.ThrowsAsync<AuthorityException>(() => h.Reconciliation.AttestAsync("another-verifier", h.LastSnapshot, Guid.CreateVersion7(), CancellationToken.None));
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, (await Assert.ThrowsAsync<AuthorityException>(() => h.CensusAsync())).ErrorCode);
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, (await Assert.ThrowsAsync<AuthorityException>(() => h.CensusAsync(h.Item(inScope), h.Item(inScope)))).ErrorCode);
    }

    [Fact]
    public async Task ASupersededDecisionCanNeitherBeCensusedNorAttested()
    {
        var h = new Harness();
        var record = await h.RecordAsync(h.FormA);
        var command = h.Decision(h.FormA);
        await h.CatalogueAsync(h.FormA, command);
        h.Store.Resources.Single(r => r.ResourceTypeName == ResourceTypeNames.FieldProtectionRequirement).RegisteredBySecurityCommandId = Guid.CreateVersion7();
        Assert.Equal(AuthorityErrorCodes.ContinuationInvalidated, (await Assert.ThrowsAsync<AuthorityException>(() => h.CensusAsync(h.Item(record)))).ErrorCode);
        Assert.Equal(AuthorityErrorCodes.ContinuationInvalidated, (await Assert.ThrowsAsync<AuthorityException>(() => h.AttestAsync())).ErrorCode);
        Assert.Empty(h.Store.Attestations);
    }

    [Fact]
    public async Task EnvelopeShapeAndNonceReuseAreReportedAndBlockCompletion()
    {
        var h = new Harness();
        var shortNonce = await h.RecordAsync(h.FormA);
        var reused = await h.RecordAsync(h.FormA);
        var command = h.Decision(h.FormA);
        await h.WriteSealedAsync(shortNonce, r => r.With("encryption_nonce_bytes", RowValue.Bytes(Fill(8, 1))));
        var twin = Sealed(h.Org, reused, h.Field, Guid.CreateVersion7(), 77);
        await h.WriteAsync(reused, [Header(h.Org, reused), Sealed(h.Org, reused, h.Field, h.ItemFor(reused), 77), twin]);

        var attestation = await h.ReconcileAsync(h.FormA, command);
        Assert.False(attestation.AttestationComplete);
        Assert.Equal(ProtectionAttestationReasons.EnvelopeIssues, attestation.AttestationIncompleteReason);
        Assert.Equal(2, attestation.EnvelopeIssueRecordCount);
    }

    [Fact]
    public async Task ARetiredVerificationKeyAndAnActiveSearchApprovalEachPreventACompleteAttestation()
    {
        var h = new Harness();
        var record = await h.RecordAsync(h.FormA);
        var command = h.Decision(h.FormA);
        await h.WriteSealedAsync(record);
        h.Store.Approvals.Add(new FieldSearchApproval { FieldSearchApprovalId = Guid.CreateVersion7(), OrganizationId = h.Org, FormDefinitionId = h.FormA, FieldDefinitionId = h.Field, ApprovalStateName = FieldSearchApprovalStates.Active });
        var approved = await h.ReconcileAsync(h.FormA, command);
        Assert.Equal(ProtectionAttestationReasons.SearchApprovalActive, approved.AttestationIncompleteReason);
        Assert.Equal(1, approved.ActiveSearchApprovalCount);
        h.Store.Approvals.Clear();

        h.Store.Keys.Single().MinimumVerificationKeyVersion = 2;
        var unverifiable = await h.AttestAsync();
        Assert.False(unverifiable.AttestationComplete);
        Assert.Equal(ProtectionAttestationReasons.Unverified, unverifiable.AttestationIncompleteReason);
        Assert.Equal(1, unverifiable.UnverifiedRecordCount);
    }

    [Fact]
    public async Task ADecryptDecisionCountsEncryptedRowsAsTheOldForm()
    {
        var h = new Harness();
        var record = await h.RecordAsync(h.FormA, withContent: false);
        await h.WriteSealedAsync(record);
        var command = h.Decision(h.FormA, decrypt: true);
        var attestation = await h.ReconcileAsync(h.FormA, command);
        Assert.Equal("Decrypt", attestation.ProtectionDirection);
        Assert.Equal(1, attestation.OldFormRemainingRecordCount);
        Assert.Equal(ProtectionAttestationReasons.OldFormRemaining, attestation.AttestationIncompleteReason);
    }

    [Fact]
    public async Task TheAttestationIsJournaledOnTheAuditStreamWithoutRecordIdsAndChangesNoContentState()
    {
        var h = new Harness();
        var record = await h.RecordAsync(h.FormA);
        var command = h.Decision(h.FormA);
        await h.WriteSealedAsync(record);
        var contentRevision = h.State.ContentIntegrityRevision;
        var attestation = await h.ReconcileAsync(h.FormA, command);
        var journal = h.Store.Journal.Single(j => j.JournalEventKind == "ProtectionReconciliationAttested");
        Assert.Equal(JournalStreamNames.Audit, journal.JournalStreamName);
        Assert.DoesNotContain(record.ToString("D"), journal.JournalEventBodyJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(attestation.ProtectionReconciliationAttestationId.ToString("D"), journal.JournalEventBodyJson, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(contentRevision, h.State.ContentIntegrityRevision);
        Assert.Equal(h.State.AuditJournalRevision, attestation.AuditJournalRevision);
        Assert.Equal(64, attestation.AttestationDigestHex.Length);
        var stored = Assert.Single(h.Store.Attestations);
        Assert.True(stored.AttestationComplete);
    }

    [Fact]
    public async Task CoverageListsEveryCurrentDecisionAcrossFormsAndIsCompleteOnlyWhenEachHasACompleteAttestation()
    {
        var h = new Harness();
        var a = await h.RecordAsync(h.FormA);
        var commandA = h.Decision(h.FormA);
        await h.WriteSealedAsync(a);
        // A second field's decision on form B (its own requirement resource).
        var fieldB = Guid.CreateVersion7();
        var commandB = Guid.CreateVersion7();
        h.Store.Commands.Add(new SecurityCommand { SecurityCommandId = commandB, OrganizationId = h.Org, ActorIdentityBindingId = h.Store.Bindings[0].IdentityBindingId, CommandKindName = SecurityCommandKinds.PublishFormSecurityRevision, CommandStateName = SecurityCommandStates.Effective, CommandPayloadJson = System.Text.Encoding.UTF8.GetString(CanonicalJson.SerializeBytes(new PublishFormSecurityRevisionPayload(h.FormB, fieldB, "ApplicationEncrypted", 2))) });
        h.Store.Resources.Add(new AuthorityResource { AuthorityResourceId = Guid.CreateVersion7(), OrganizationId = h.Org, ResourceTypeName = ResourceTypeNames.FieldProtectionRequirement, ResourceEntityId = fieldB, ParentResourceEntityId = h.FormB, ResourceLifecycleState = ResourceLifecycleStates.Active, RegisteredBySecurityCommandId = commandB });

        var none = await h.Reconciliation.CoverageAsync(new ProtectionCoverageRequest(h.Org), Guid.CreateVersion7(), CancellationToken.None);
        Assert.Equal(2, none.DecisionCount);
        Assert.Equal(2, none.RegisteredFormCount);
        Assert.False(none.OrganizationComplete);
        Assert.All(none.Decisions, d => Assert.Null(d.LatestAttestation));

        await h.ReconcileAsync(h.FormA, commandA);
        var partial = await h.Reconciliation.CoverageAsync(new ProtectionCoverageRequest(h.Org), Guid.CreateVersion7(), CancellationToken.None);
        Assert.Equal(1, partial.AttestedCompleteDecisionCount);
        Assert.False(partial.OrganizationComplete); // form B's decision has no attestation: no organization-wide claim

        var catalogueB = await h.Catalogue.IssueMigrationCatalogueAsync(Verifier, new MigrationCatalogueRequest(h.Org, h.FormB, commandB), Guid.CreateVersion7(), CancellationToken.None);
        await h.Reconciliation.AttestAsync(Verifier, catalogueB.SearchCatalogueSnapshotId, Guid.CreateVersion7(), CancellationToken.None); // empty population: complete
        var full = await h.Reconciliation.CoverageAsync(new ProtectionCoverageRequest(h.Org), Guid.CreateVersion7(), CancellationToken.None);
        Assert.True(full.OrganizationComplete);

        // A new decision for form A supersedes the attested one: its coverage needs a new attestation.
        h.Decision(h.FormA);
        var superseded = await h.Reconciliation.CoverageAsync(new ProtectionCoverageRequest(h.Org), Guid.CreateVersion7(), CancellationToken.None);
        Assert.False(superseded.OrganizationComplete);
    }

    [Fact]
    public async Task AnotherOrganizationsRecordsAndDecisionsNeverEnterTheReport()
    {
        var h = new Harness();
        var record = await h.RecordAsync(h.FormA);
        var command = h.Decision(h.FormA);
        await h.WriteSealedAsync(record);
        var foreignOrg = Guid.CreateVersion7();
        h.Store.Resources.Add(new AuthorityResource { AuthorityResourceId = Guid.CreateVersion7(), OrganizationId = foreignOrg, ResourceTypeName = ResourceTypeNames.FormRecord, ResourceEntityId = Guid.CreateVersion7(), ParentResourceEntityId = h.FormA, ResourceLifecycleState = ResourceLifecycleStates.Active });
        h.Store.Resources.Add(new AuthorityResource { AuthorityResourceId = Guid.CreateVersion7(), OrganizationId = foreignOrg, ResourceTypeName = ResourceTypeNames.FieldProtectionRequirement, ResourceEntityId = Guid.CreateVersion7(), ParentResourceEntityId = h.FormA, ResourceLifecycleState = ResourceLifecycleStates.Active, RegisteredBySecurityCommandId = Guid.CreateVersion7() });

        var attestation = await h.ReconcileAsync(h.FormA, command);
        Assert.Equal(1, attestation.ExpectedRecordCount);
        Assert.True(attestation.AttestationComplete);
        Assert.Equal(1, (await h.Reconciliation.CoverageAsync(new ProtectionCoverageRequest(h.Org), Guid.CreateVersion7(), CancellationToken.None)).DecisionCount);
    }
}
