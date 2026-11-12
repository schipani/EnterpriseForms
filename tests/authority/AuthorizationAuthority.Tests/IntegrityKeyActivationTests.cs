using System.Reflection;
using System.Text.Json;
using AuthorizationAuthority.Api.Composition;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Application.Features.Keys;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Content;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Keys;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using AuthorizationAuthority.Infrastructure.Faults;
using AuthorizationAuthority.Infrastructure.Signing;
using EnterpriseForms.Integrity.Merkle;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;
using static AuthorizationAuthority.Tests.ContributorWriteFixtures;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// E-5 controlled HMAC signing-key activation (E5-7) with the real <see cref="ContentService"/>, the real canonical encoding, the
/// in-process HMAC provider and the in-memory store. Version 1 evidence is written, version 2 is provisioned (not active), activated
/// with <see cref="IntegrityKeyActivationService"/>, and the evidence of both versions is verified with exactly its recorded version.
/// Real Vault behaviour is in <see cref="IntegrityKeyActivationVaultIntegrationTests"/>.
/// </summary>
public sealed class IntegrityKeyActivationTests
{
    private const string Issuer = "https://issuer.test";

    /// <summary>Records every key reference the Authority hands the provider; can make chosen versions unavailable (a Vault-like refusal).</summary>
    private sealed class RecordingMacProvider(IIntegrityMacProvider inner) : IIntegrityMacProvider
    {
        public List<(string Operation, Guid Organization, int Version)> Calls { get; } = [];

        public HashSet<int> UnavailableVersions { get; } = [];

        public bool Down { get; set; }

        public string ProviderName => inner.ProviderName;

        public Task<IntegrityMacResult> SignAsync(IntegrityKeyReference keyReference, ReadOnlyMemory<byte> canonicalPayload, CancellationToken cancellationToken)
        {
            Calls.Add(("sign", keyReference.OrganizationId, keyReference.KeyVersion));
            Refuse(keyReference);
            return inner.SignAsync(keyReference, canonicalPayload, cancellationToken);
        }

        public Task<bool> VerifyAsync(IntegrityKeyReference keyReference, ReadOnlyMemory<byte> canonicalPayload, ReadOnlyMemory<byte> expectedMac, CancellationToken cancellationToken)
        {
            Calls.Add(("verify", keyReference.OrganizationId, keyReference.KeyVersion));
            Refuse(keyReference);
            return inner.VerifyAsync(keyReference, canonicalPayload, expectedMac, cancellationToken);
        }

        public Task<int> ProvisionKeyAsync(Guid organizationId, string keyPurpose, string providerKeyName, CancellationToken cancellationToken) => inner.ProvisionKeyAsync(organizationId, keyPurpose, providerKeyName, cancellationToken);

        public Task<(bool Ready, string Detail)> ProbeAsync(CancellationToken cancellationToken) => inner.ProbeAsync(cancellationToken);

        public Task<IntegrityKeyInspection> InspectKeyAsync(string providerKeyName, CancellationToken cancellationToken) =>
            Down ? throw new IntegrityProviderUnavailableException("provider down") : inner.InspectKeyAsync(providerKeyName, cancellationToken);

        private void Refuse(IntegrityKeyReference reference)
        {
            if (Down || UnavailableVersions.Contains(reference.KeyVersion))
            {
                throw new IntegrityProviderUnavailableException("Vault returned 400 for verify"); // what the Vault adapter reports for a missing/retired version
            }
        }
    }

    private sealed class Harness
    {
        public Harness(InMemoryAuthorityStore? store = null, LocalHmacProvider? local = null)
        {
            Store = store ?? new InMemoryAuthorityStore();
            Local = local ?? new LocalHmacProvider();
            KeyName = "org-" + Org.ToString("N") + "-row-integrity-v1";
            Local.ProvisionKeyAsync(Org, IntegrityKeyPurposes.RowIntegrityV1, KeyName, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
            Mac = new RecordingMacProvider(Local);
            var spiceDb = DispatchProxy.Create<ISpiceDbProjection, ContentHistoryTests.SpiceDb>();
            var control = (ContentHistoryTests.SpiceDb)(object)spiceDb;
            foreach (var permission in new[] { "read", "edit", "discover" })
            {
                control.Allowed.Add((permission, Owner.ToString("D")));
            }

            control.Markers = org => TestProjection.MarkerFor(Store.Organizations.Single(o => o.OrganizationId == org));
            Service = new ContentService(Store, Mac, TestProjection.Verifier(Store, spiceDb), new NoOpFaultInjector(), TimeProvider.System, new ShareContributorWriteSwitch(false, testingOverrideAllowed: true));
            Activation = new IntegrityKeyActivationService(Store, Mac, TimeProvider.System);
            State = new OrganizationAuthorizationState { OrganizationId = Org, AuthorizationRevision = 7, ProjectionAppliedRevision = 7, ProjectionZedToken = "zed-7", CheckpointEnforcementModeName = CheckpointEnforcementModes.Asynchronous }.WithJournalDigest();
            Store.Organizations.Add(State);
            Store.Bindings.Add(new IdentityBinding { IdentityBindingId = Guid.CreateVersion7(), OrganizationId = Org, AuthorityUserId = Owner, IdentityIssuerUri = Issuer, IdentitySubjectIdentifier = Owner.ToString("D"), IdentityBindingEnabled = true });
            Store.Keys.Add(new IntegrityKeyRegistration { IntegrityKeyRegistrationId = Guid.CreateVersion7(), OrganizationId = Org, ProviderName = "LocalDevelopment", ProviderKeyName = KeyName, CurrentSigningKeyVersion = 1, MinimumVerificationKeyVersion = 1 });
            Store.Resources.Add(new AuthorityResource { AuthorityResourceId = Guid.CreateVersion7(), OrganizationId = Org, ResourceTypeName = ResourceTypeNames.FormRecord, ResourceEntityId = Record, ParentResourceEntityId = Ids.Form });
            Store.Heads.Add(new RecordContentHead { RecordContentHeadId = Guid.CreateVersion7(), OrganizationId = Org, FormRecordId = Record, CommittedManifestRoot = MerkleManifestV1.EmptyRoot(), SignatureKeyVersion = 1 });
            Store.Edges.Add(new AuthorityRelationshipEdge { AuthorityRelationshipEdgeId = Guid.CreateVersion7(), OrganizationId = Org, ResourceTypeName = ResourceTypeNames.FormRecord, ResourceEntityId = Record, RelationshipName = "owner", SubjectTypeName = ResourceTypeNames.User, SubjectEntityId = Owner, SubjectRelationName = string.Empty });
        }

        public Guid Org { get; } = Guid.CreateVersion7();
        public Guid Record { get; } = Guid.CreateVersion7();
        public Guid Owner { get; } = Guid.CreateVersion7();
        public Guid Field { get; } = Guid.CreateVersion7();
        public Guid Item { get; } = Guid.CreateVersion7();
        public string KeyName { get; }
        public InMemoryAuthorityStore Store { get; }
        public LocalHmacProvider Local { get; }
        public RecordingMacProvider Mac { get; }
        public ContentService Service { get; }
        public IntegrityKeyActivationService Activation { get; }
        public OrganizationAuthorizationState State { get; }
        public Dictionary<long, List<StagedContentItem>> Snapshots { get; } = [];

        public RecordContentHead Head => Store.Heads.Single(h => h.FormRecordId == Record);

        public IntegrityKeyRegistration Key => Store.Keys.Single(k => k.OrganizationId == Org);

        public static VerifiedActor Actor(Guid user) => new(Issuer, user.ToString("D"), "test");

        /// <summary>Stages with the signing version the Authority currently reports (as the business stager does), unless one is forced.</summary>
        public async Task<(Guid Operation, PreparedContentOperation Prepared, List<Staged> Candidate)> PrepareAsync(string value, uint? keyVersion = null)
        {
            var operation = Guid.CreateVersion7();
            var version = keyVersion ?? (uint)Key.CurrentSigningKeyVersion;
            var candidate = new List<Row> { Header(Org, Record), Text(Org, Record, Field, Item, value) }.Select(r => Encode(r, Org, operation, Head.CommittedContentRevision + 1, version)).ToList();
            var request = new PrepareContentChangeRequest(Org, Record, operation, State.AuthorizationRevision, Head.CommittedContentRevision, Convert.ToBase64String(Head.CommittedManifestRoot),
                candidate.Select(c => new CandidateContentItem(c.Kind, c.ItemId, c.Base64)).ToList(), CurrentItems: Snapshots.GetValueOrDefault(Head.CommittedContentRevision));
            return (operation, await Service.PrepareAsync(Actor(Owner), request, "key-" + operation.ToString("N"), Guid.CreateVersion7(), TestContext.Current.CancellationToken), candidate);
        }

        public async Task<long> CommitAsync((Guid Operation, PreparedContentOperation Prepared, List<Staged> Candidate) prepared)
        {
            var signatures = prepared.Prepared.SignedItems.ToDictionary(s => (s.ItemKind, s.ItemId), s => s.RowSignatureBase64);
            var staged = prepared.Candidate.Select(c => new StagedContentItem(c.Kind, c.ItemId, c.Base64, signatures[(c.Kind, c.ItemId)])).ToList();
            var revision = await Service.CommitAsync(Actor(Owner), prepared.Operation, new CommitContentChangeRequest(Org, Record, staged), Guid.CreateVersion7(), TestContext.Current.CancellationToken);
            Snapshots[revision.CommittedContentRevision] = staged;
            foreach (var entry in Store.Journal)
            {
                entry.ArchiveDeliveryState = ArchiveDeliveryStates.Anchored;
            }

            return revision.CommittedContentRevision;
        }

        public async Task<long> WriteAsync(string value) => await CommitAsync(await PrepareAsync(value));

        public Guid OperationOf(long revision) => Store.Commits.Single(c => c.FormRecordId == Record && c.CommittedContentRevision == revision).CommittedOperationId;

        public RecordManifestCommit CommitOf(long revision) => Store.Commits.Single(c => c.FormRecordId == Record && c.CommittedContentRevision == revision);

        /// <summary>Verifies a revision as a full reader: the current one by the head, an earlier one by its accepted operation.</summary>
        public Task<VerifyRecordProjectionResult> VerifyAsync(long revision, List<StagedContentItem>? observed = null) =>
            Service.VerifyProjectionAsync(Actor(Owner), new VerifyRecordProjectionRequest(Org, Record, RequestedAction.Read, observed ?? Snapshots[revision], revision == Head.CommittedContentRevision ? null : OperationOf(revision)), Guid.CreateVersion7(), TestContext.Current.CancellationToken);

        public Task<IntegrityKeyActivationResult> ActivateAsync(int expected, int version) => ActivateWithAsync(expected, version, TestContext.Current.CancellationToken);

        public Task<IntegrityKeyActivationResult> ActivateWithAsync(int expected, int version, CancellationToken cancellationToken) =>
            Activation.ActivateAsync(Org, IntegrityKeyPurposes.RowIntegrityV1, expected, version, "e5-test-operator", "E-5 focused test", Guid.CreateVersion7(), cancellationToken);

        /// <summary>Every persisted piece of integrity evidence of the record (signatures, digests, roots, versions, journal digests).</summary>
        public string EvidenceFingerprint(long upToRevision)
        {
            var commits = Store.Commits.Where(c => c.FormRecordId == Record && c.CommittedContentRevision <= upToRevision).OrderBy(c => c.CommittedContentRevision).ToList();
            var leaves = Store.ManifestLeaves.Where(l => commits.Any(c => c.RecordManifestCommitId == l.RecordManifestCommitId)).OrderBy(l => Convert.ToHexString(l.ManifestItemKey)).ThenBy(l => l.RecordManifestCommitId);
            var operations = commits.Select(c => c.CommittedOperationId).ToHashSet();
            var journal = Store.Journal.Where(j => j.OrganizationId == Org && j.JournalStreamName == JournalStreamNames.Content && j.ContentChangeOperationId is { } op && operations.Contains(op)).OrderBy(j => j.JournalStreamRevision);
            return string.Join('|', commits.Select(c => $"{c.CommittedContentRevision}:{c.RowSignatureKeyVersion}:{Convert.ToHexString(c.ManifestRootDigest)}:{Convert.ToHexString(c.ManifestLeafIndexDigest)}"))
                + "#" + string.Join('|', leaves.Select(l => Convert.ToHexString(l.RowSignature) + ":" + Convert.ToHexString(l.CanonicalPayloadDigest)))
                + "#" + string.Join('|', journal.Select(j => j.JournalStreamRevision + ":" + Convert.ToHexString(j.JournalEventDigest)))
                + "#" + string.Join('|', Snapshots.Where(s => s.Key <= upToRevision).OrderBy(s => s.Key).SelectMany(s => s.Value).Select(i => i.RowSignatureBase64 + ":" + i.CanonicalBytesBase64));
        }
    }

    private static uint HeaderVersion(StagedContentItem item) => EnterpriseForms.Integrity.Canonical.RowCanonicalHeader.Parse(Convert.FromBase64String(item.CanonicalBytesBase64)).KeyVersion;

    // ------------------------------------------------------------------ 1-5. Before, provisioning, activation, after, no rewrite

    [Fact]
    public async Task VersionOneEvidenceVerifiesBeforeAndAfterVersionTwoIsProvisionedAndActivatedAndNewSignaturesUseVersionTwo()
    {
        var h = new Harness();
        await h.WriteAsync("first");
        await h.WriteAsync("second");

        // 1. Before: version-1 evidence verifies (current and earlier revision).
        Assert.True((await h.VerifyAsync(2)).Verified);
        Assert.True((await h.VerifyAsync(1)).Verified);
        Assert.Equal((1, 1), (h.CommitOf(1).RowSignatureKeyVersion, h.Head.SignatureKeyVersion));

        // 2. Provisioning version 2 does not activate it: the registration, the reported version and new signatures stay on version 1,
        //    and a candidate staged for version 2 is refused.
        Assert.Equal(2, h.Local.ProvisionKeyVersion(h.KeyName));
        Assert.Equal(2, (await h.Local.InspectKeyAsync(h.KeyName, TestContext.Current.CancellationToken)).LatestVersion);
        Assert.Equal(1, h.Key.CurrentSigningKeyVersion);
        var early = await Assert.ThrowsAsync<AuthorityException>(() => h.PrepareAsync("staged for v2 too early", keyVersion: 2));
        Assert.Equal(422, early.HttpStatusCode);
        await h.WriteAsync("third, still v1");
        Assert.Equal(1, h.CommitOf(3).RowSignatureKeyVersion);

        var evidence = h.EvidenceFingerprint(3);
        Assert.All(evidence.Split('#'), part => Assert.NotEmpty(part)); // commits, leaves, content journal and stored rows all covered
        Assert.Equal(3, evidence.Split('#')[2].Split('|').Length);
        var contentJournal = h.Store.Journal.Count(j => j.JournalStreamName == JournalStreamNames.Content);

        // Activation: one registration field changes, and one audit entry records it.
        var result = await h.ActivateAsync(expected: 1, version: 2);
        Assert.Equal((1, 2, 1), (result.PreviousSigningKeyVersion, result.CurrentSigningKeyVersion, result.MinimumVerificationKeyVersion));
        Assert.Equal(2, h.Key.CurrentSigningKeyVersion);
        var audit = Assert.Single(h.Store.Journal, j => j.JournalEventKind == IntegrityKeyActivationService.ActivatedEventKind);
        Assert.Equal(JournalStreamNames.Audit, audit.JournalStreamName);
        using (var body = JsonDocument.Parse(audit.JournalEventBodyJson))
        {
            var recorded = body.RootElement.GetProperty("body");
            Assert.Equal(IntegrityKeyActivationService.ActivatedEventKind, body.RootElement.GetProperty("event_kind").GetString());
            Assert.Equal((1, 2), (recorded.GetProperty("previous_signing_key_version").GetInt32(), recorded.GetProperty("signing_key_version").GetInt32()));
            Assert.Equal(h.KeyName, recorded.GetProperty("provider_key_name").GetString());
        }

        // 5. Nothing that already existed was rewritten: commits, leaves, signatures, roots, content journal, stored rows.
        Assert.Equal(evidence, h.EvidenceFingerprint(3));
        Assert.Equal(contentJournal, h.Store.Journal.Count(j => j.JournalStreamName == JournalStreamNames.Content));

        // 3. New signatures use version 2 (row header, prepared operation, commit, head) and verify.
        h.Mac.Calls.Clear();
        var fourth = await h.WriteAsync("fourth, v2");
        Assert.All(h.Snapshots[fourth], i => Assert.Equal(2u, HeaderVersion(i)));
        Assert.Equal((2, 2), (h.CommitOf(fourth).RowSignatureKeyVersion, h.Head.SignatureKeyVersion));
        Assert.All(h.Mac.Calls.Where(c => c.Operation == "sign"), c => Assert.Equal(2, c.Version));
        Assert.True((await h.VerifyAsync(fourth)).Verified);

        // 4. Version-1 evidence still verifies, with exactly version 1.
        foreach (var revision in new long[] { 1, 2, 3 })
        {
            h.Mac.Calls.Clear();
            Assert.True((await h.VerifyAsync(revision)).Verified, revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.All(h.Mac.Calls, c => Assert.Equal(("verify", 1), (c.Operation, c.Version)));
        }

        // The history lists every revision with its own version, all verifiable; nothing was quarantined.
        var history = await h.Service.GetHistoryAsync(Harness.Actor(h.Owner), h.Org, h.Record, null, null, Guid.CreateVersion7(), TestContext.Current.CancellationToken);
        Assert.Equal([1, 1, 1, 2], history.Entries.Select(e => e.RowSignatureKeyVersion));
        Assert.All(history.Entries, e => Assert.True(e.VerifiableNow));
        Assert.False(h.Head.ContentHeadQuarantined);
        Assert.Equal(evidence, h.EvidenceFingerprint(3));
    }

    [Fact]
    public async Task AnOperationPreparedBeforeActivationCommitsUnderItsPreparedVersionAndStaleStagingIsRefused()
    {
        var h = new Harness();
        await h.WriteAsync("first");
        var inFlight = await h.PrepareAsync("prepared under v1");
        h.Local.ProvisionKeyVersion(h.KeyName);
        await h.ActivateAsync(1, 2);

        // The prepared operation keeps the version it was signed with; it is never re-signed or upgraded.
        var revision = await h.CommitAsync(inFlight);
        Assert.Equal((1, 1), (h.CommitOf(revision).RowSignatureKeyVersion, h.Head.SignatureKeyVersion));
        Assert.True((await h.VerifyAsync(revision)).Verified);

        // A writer that staged with the version it read before the activation is refused (fail closed; it re-stages).
        var stale = await Assert.ThrowsAsync<AuthorityException>(() => h.PrepareAsync("staged with the old version", keyVersion: 1));
        Assert.Equal((AuthorityErrorCodes.InvalidCommand, 422), (stale.ErrorCode, stale.HttpStatusCode));
        Assert.Equal(2, h.CommitOf(await h.WriteAsync("re-staged")).RowSignatureKeyVersion);
    }

    [Fact]
    public async Task RollingBackAnActivationKeepsEveryVersionsEvidenceVerifiable()
    {
        var h = new Harness();
        await h.WriteAsync("v1 data");
        h.Local.ProvisionKeyVersion(h.KeyName);
        await h.ActivateAsync(1, 2);
        var v2 = await h.WriteAsync("v2 data");

        var back = await h.ActivateAsync(expected: 2, version: 1);
        Assert.Equal((2, 1), (back.PreviousSigningKeyVersion, back.CurrentSigningKeyVersion));
        var v1Again = await h.WriteAsync("v1 again");
        Assert.Equal(1, h.CommitOf(v1Again).RowSignatureKeyVersion);
        Assert.True((await h.VerifyAsync(v2)).Verified);
        Assert.True((await h.VerifyAsync(1)).Verified);
        Assert.True((await h.VerifyAsync(v1Again)).Verified);
        Assert.Equal(2, h.Store.Journal.Count(j => j.JournalEventKind == IntegrityKeyActivationService.ActivatedEventKind));
    }

    // ------------------------------------------------------------------ 6. Organization isolation

    [Fact]
    public async Task ActivationChangesOnlyItsOwnOrganizationAndKeysDoNotCrossOrganizations()
    {
        var a = new Harness();
        var b = new Harness(a.Store, a.Local); // same store and provider, another organization and key
        await a.WriteAsync("A v1");
        await b.WriteAsync("B v1");

        a.Local.ProvisionKeyVersion(a.KeyName);
        await a.ActivateAsync(1, 2);
        Assert.Equal((2, 1), (a.Key.CurrentSigningKeyVersion, b.Key.CurrentSigningKeyVersion));
        Assert.Equal(1, b.CommitOf(await b.WriteAsync("B still v1")).RowSignatureKeyVersion);
        Assert.Equal(2, a.CommitOf(await a.WriteAsync("A v2")).RowSignatureKeyVersion);

        // B has no version 2 (A's provisioning did not give it one): activating it is refused and B is unchanged.
        var refused = await Assert.ThrowsAsync<AuthorityException>(() => b.ActivateAsync(1, 2));
        Assert.Equal(AuthorityErrorCodes.OrganizationKeyNotProvisioned, refused.ErrorCode);
        Assert.Equal(1, b.Key.CurrentSigningKeyVersion);

        // A's version-2 MAC does not verify under B's key reference (another key), nor under A's reference with B's organization.
        var probe = "cross-organization probe"u8.ToArray();
        var aReference = new IntegrityKeyReference(a.Org, IntegrityKeyPurposes.RowIntegrityV1, a.KeyName, 2);
        var aMac = await a.Local.SignAsync(aReference, probe, TestContext.Current.CancellationToken);
        Assert.False(await a.Local.VerifyAsync(new IntegrityKeyReference(b.Org, IntegrityKeyPurposes.RowIntegrityV1, b.KeyName, 2), probe, aMac.Mac, TestContext.Current.CancellationToken));
        Assert.False(await a.Local.VerifyAsync(new IntegrityKeyReference(b.Org, IntegrityKeyPurposes.RowIntegrityV1, b.KeyName, 1), probe, aMac.Mac, TestContext.Current.CancellationToken));

        // A's accepted rows presented as B's record fail the organization binding and never verify.
        var aRows = a.Snapshots[a.Head.CommittedContentRevision];
        var asB = await b.VerifyAsync(b.Head.CommittedContentRevision, aRows);
        Assert.False(asB.Verified);
        Assert.True(b.Head.ContentHeadQuarantined); // unchanged rule: foreign rows are tamper evidence
        Assert.False(a.Head.ContentHeadQuarantined);
    }

    // ------------------------------------------------------------------ 7-9. Wrong version, missing key, disabled version, no fallback

    [Fact]
    public async Task AWrongRecordedVersionIsRejectedAndNeverSubstituted()
    {
        var h = new Harness();
        await h.WriteAsync("v1");
        h.Local.ProvisionKeyVersion(h.KeyName);
        await h.ActivateAsync(1, 2);
        var revision = await h.WriteAsync("v2");

        // A hostile edit of the head's recorded version (2 -> 1): the rows say version 2, so verification refuses the binding; the
        // version-2 key is never used in place of the recorded one, and the head is quarantined (unchanged rule).
        h.Head.SignatureKeyVersion = 1;
        h.Mac.Calls.Clear();
        var result = await h.VerifyAsync(revision);
        Assert.False(result.Verified);
        Assert.Equal("ITEM_BINDING_MISMATCH", result.FailureReasonCode);
        Assert.DoesNotContain(h.Mac.Calls, c => c.Version == 2);
        Assert.True(h.Head.ContentHeadQuarantined);
    }

    [Fact]
    public async Task AMissingHistoricalKeyFailsClosedWithoutFallbackToTheActiveVersion()
    {
        // Provider refusal (how the Vault adapter reports a version it does not have or has retired): 503, nothing verified, nothing
        // tried under version 2, no quarantine and no incident (a key outage is not tamper evidence).
        var h = new Harness();
        await h.WriteAsync("v1 first");
        await h.WriteAsync("v1 second");
        h.Local.ProvisionKeyVersion(h.KeyName);
        await h.ActivateAsync(1, 2);
        await h.WriteAsync("v2");
        h.Mac.UnavailableVersions.Add(1);
        var journal = h.Store.Journal.Count;
        h.Mac.Calls.Clear();
        var outage = await Assert.ThrowsAsync<AuthorityException>(() => h.VerifyAsync(1));
        Assert.Equal((AuthorityErrorCodes.SigningUnavailable, 503), (outage.ErrorCode, outage.HttpStatusCode));
        Assert.All(h.Mac.Calls, c => Assert.Equal(1, c.Version));
        Assert.False(h.Head.ContentHeadQuarantined);
        Assert.Equal(journal, h.Store.Journal.Count);
        h.Mac.UnavailableVersions.Clear();
        Assert.True((await h.VerifyAsync(1)).Verified); // the key is back: the untouched evidence verifies again

        // The local provider has no refusal path: a key it does not hold (here a fresh process, all keys gone) verifies nothing,
        // so the evidence is reported as not verified and the head is quarantined (existing local behaviour, never "valid").
        var lostService = new ContentService(h.Store, new LocalHmacProvider(), TestProjectionVerifier(h), new NoOpFaultInjector(), TimeProvider.System, new ShareContributorWriteSwitch(false, testingOverrideAllowed: true));
        var notValid = await lostService.VerifyProjectionAsync(Harness.Actor(h.Owner), new VerifyRecordProjectionRequest(h.Org, h.Record, RequestedAction.Read, h.Snapshots[1], h.OperationOf(1)), Guid.CreateVersion7(), TestContext.Current.CancellationToken);
        Assert.False(notValid.Verified);
        Assert.Equal("ITEM_SIGNATURE_INVALID", notValid.FailureReasonCode);
    }

    private static ProjectionIntegrityVerifier TestProjectionVerifier(Harness h)
    {
        var spiceDb = DispatchProxy.Create<ISpiceDbProjection, ContentHistoryTests.SpiceDb>();
        var control = (ContentHistoryTests.SpiceDb)(object)spiceDb;
        foreach (var permission in new[] { "read", "edit", "discover" })
        {
            control.Allowed.Add((permission, h.Owner.ToString("D")));
        }

        control.Markers = org => TestProjection.MarkerFor(h.Store.Organizations.Single(o => o.OrganizationId == org));
        return TestProjection.Verifier(h.Store, spiceDb);
    }

    [Fact]
    public async Task AVersionBelowTheVerificationFloorIsDisabledForVerificationAndActivation()
    {
        var h = new Harness();
        await h.WriteAsync("v1");
        h.Local.ProvisionKeyVersion(h.KeyName);
        await h.ActivateAsync(1, 2);
        var current = await h.WriteAsync("v2");
        h.Key.MinimumVerificationKeyVersion = 2; // version 1 disabled for verification (existing floor semantics)

        var historic = await Assert.ThrowsAsync<AuthorityException>(() => h.VerifyAsync(1));
        Assert.Equal((AuthorityErrorCodes.KeyUnavailable, 503), (historic.ErrorCode, historic.HttpStatusCode));
        Assert.True((await h.VerifyAsync(current)).Verified);
        var history = await h.Service.GetHistoryAsync(Harness.Actor(h.Owner), h.Org, h.Record, null, null, Guid.CreateVersion7(), TestContext.Current.CancellationToken);
        Assert.Equal([false, true], history.Entries.Select(e => e.VerifiableNow));

        // Rolling back onto the disabled version is refused.
        var refused = await Assert.ThrowsAsync<AuthorityException>(() => h.ActivateAsync(2, 1));
        Assert.Equal(AuthorityErrorCodes.KeyUnavailable, refused.ErrorCode);
        Assert.Equal(2, h.Key.CurrentSigningKeyVersion);
    }

    // ------------------------------------------------------------------ 10. Invalid activation fails closed

    [Fact]
    public async Task InvalidActivationsAreRefusedAndChangeNothing()
    {
        var h = new Harness();
        await h.WriteAsync("v1");
        h.Local.ProvisionKeyVersion(h.KeyName); // version 2 exists; version 3 does not

        var registration = h.Key;

        async Task RefusedAsync(string code, Func<Task> act)
        {
            var before = (registration.CurrentSigningKeyVersion, registration.MinimumVerificationKeyVersion, h.Store.Journal.Count, h.State.AuditJournalRevision);
            var thrown = await Assert.ThrowsAsync<AuthorityException>(act);
            Assert.Equal(code, thrown.ErrorCode);
            Assert.Equal(before, (registration.CurrentSigningKeyVersion, registration.MinimumVerificationKeyVersion, h.Store.Journal.Count, h.State.AuditJournalRevision));
        }

        await RefusedAsync(AuthorityErrorCodes.OrganizationKeyNotProvisioned, () => h.ActivateAsync(1, 3));        // unprovisioned version
        await RefusedAsync(AuthorityErrorCodes.OperatorStateConflict, () => h.ActivateAsync(2, 2 + 1));             // stale expected version
        await RefusedAsync(AuthorityErrorCodes.OperatorStateConflict, () => h.ActivateAsync(3, 2));
        await RefusedAsync(AuthorityErrorCodes.InvalidCommand, () => h.ActivateAsync(1, 1));                         // no change
        await RefusedAsync(AuthorityErrorCodes.InvalidCommand, () => h.ActivateAsync(1, 0));
        await RefusedAsync(AuthorityErrorCodes.InvalidCommand, () => h.ActivateAsync(0, 2));
        await RefusedAsync(AuthorityErrorCodes.InvalidCommand, () => h.Activation.ActivateAsync(h.Org, "audit-signing", 1, 2, "op", "r", Guid.CreateVersion7(), TestContext.Current.CancellationToken));
        await RefusedAsync(AuthorityErrorCodes.InvalidCommand, () => h.Activation.ActivateAsync(Guid.Empty, IntegrityKeyPurposes.RowIntegrityV1, 1, 2, "op", "r", Guid.CreateVersion7(), TestContext.Current.CancellationToken));
        await RefusedAsync(AuthorityErrorCodes.InvalidCommand, () => h.Activation.ActivateAsync(h.Org, IntegrityKeyPurposes.RowIntegrityV1, 1, 2, "op", "", Guid.CreateVersion7(), TestContext.Current.CancellationToken));
        await RefusedAsync(AuthorityErrorCodes.InvalidCommand, () => h.Activation.ActivateAsync(h.Org, IntegrityKeyPurposes.RowIntegrityV1, 1, 2, "op", "line\nbreak", Guid.CreateVersion7(), TestContext.Current.CancellationToken));
        await RefusedAsync(AuthorityErrorCodes.NotFound, () => h.Activation.ActivateAsync(Guid.CreateVersion7(), IntegrityKeyPurposes.RowIntegrityV1, 1, 2, "op", "r", Guid.CreateVersion7(), TestContext.Current.CancellationToken));

        h.Key.KeyUsagePaused = true;
        await RefusedAsync(AuthorityErrorCodes.KeyUnavailable, () => h.ActivateAsync(1, 2));
        h.Key.KeyUsagePaused = false;

        h.Key.ProviderName = "VaultTransit"; // registered with another provider than the one configured
        await RefusedAsync(AuthorityErrorCodes.KeyUnavailable, () => h.ActivateAsync(1, 2));
        h.Key.ProviderName = "LocalDevelopment";

        h.Mac.Down = true;                    // provider unavailable or permission denied
        await RefusedAsync(AuthorityErrorCodes.SigningUnavailable, () => h.ActivateAsync(1, 2));
        h.Mac.Down = false;
        h.Mac.UnavailableVersions.Add(2);     // listed by the provider but not usable for signing
        await RefusedAsync(AuthorityErrorCodes.SigningUnavailable, () => h.ActivateAsync(1, 2));
        h.Mac.UnavailableVersions.Clear();

        h.Store.Keys.Remove(registration);    // no registration
        await RefusedAsync(AuthorityErrorCodes.KeyUnavailable, () => h.ActivateAsync(1, 2));
        h.Store.Keys.Add(registration);

        // Concurrent operators: the second one, still expecting version 1, is refused.
        await h.ActivateAsync(1, 2);
        await RefusedAsync(AuthorityErrorCodes.OperatorStateConflict, () => h.ActivateAsync(1, 2));
        Assert.True((await h.VerifyAsync(1)).Verified);
    }

    // ------------------------------------------------------------------ 13. Cancellation

    [Fact]
    public async Task CancellationPropagatesAndChangesNothing()
    {
        var h = new Harness();
        await h.WriteAsync("v1");
        h.Local.ProvisionKeyVersion(h.KeyName);
        using var source = new CancellationTokenSource();
        await source.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.ActivateWithAsync(1, 2, source.Token));
        Assert.Equal(1, h.Key.CurrentSigningKeyVersion);
        Assert.DoesNotContain(h.Store.Journal, j => j.JournalEventKind == IntegrityKeyActivationService.ActivatedEventKind);

        var reference = new IntegrityKeyReference(h.Org, IntegrityKeyPurposes.RowIntegrityV1, h.KeyName, 2);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Local.SignAsync(reference, new byte[] { 1 }, source.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Local.VerifyAsync(reference, new byte[] { 1 }, new byte[32], source.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Local.InspectKeyAsync(h.KeyName, source.Token));
    }

    // ------------------------------------------------------------------ Local provider provisioning and the operator command

    [Fact]
    public async Task TheLocalProviderAddsVersionsOnlyByTheExplicitActionAndSignsWithExactlyTheGivenVersion()
    {
        var local = new LocalHmacProvider();
        var org = Guid.CreateVersion7();
        var name = "org-" + org.ToString("N") + "-row-integrity-v1";
        Assert.Throws<IntegrityProviderUnavailableException>(() => local.ProvisionKeyVersion(name)); // never creates a key
        Assert.Equal(1, await local.ProvisionKeyAsync(org, IntegrityKeyPurposes.RowIntegrityV1, name, TestContext.Current.CancellationToken));
        var payload = "payload"u8.ToArray();
        var v1 = await local.SignAsync(new IntegrityKeyReference(org, IntegrityKeyPurposes.RowIntegrityV1, name, 1), payload, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<IntegrityProviderUnavailableException>(() => local.SignAsync(new IntegrityKeyReference(org, IntegrityKeyPurposes.RowIntegrityV1, name, 2), payload, TestContext.Current.CancellationToken));

        Assert.Equal(2, local.ProvisionKeyVersion(name));
        Assert.Equal(2, await local.ProvisionKeyAsync(org, IntegrityKeyPurposes.RowIntegrityV1, name, TestContext.Current.CancellationToken)); // confirms, adds nothing
        var v2 = await local.SignAsync(new IntegrityKeyReference(org, IntegrityKeyPurposes.RowIntegrityV1, name, 2), payload, TestContext.Current.CancellationToken);
        Assert.Equal((1, 2), (v1.KeyVersion, v2.KeyVersion));
        Assert.NotEqual(v1.Mac, v2.Mac);
        Assert.True(await local.VerifyAsync(new IntegrityKeyReference(org, IntegrityKeyPurposes.RowIntegrityV1, name, 1), payload, v1.Mac, TestContext.Current.CancellationToken));
        Assert.False(await local.VerifyAsync(new IntegrityKeyReference(org, IntegrityKeyPurposes.RowIntegrityV1, name, 2), payload, v1.Mac, TestContext.Current.CancellationToken)); // no substitution
        Assert.False(await local.VerifyAsync(new IntegrityKeyReference(org, IntegrityKeyPurposes.RowIntegrityV1, name, 1), payload, v2.Mac, TestContext.Current.CancellationToken));
        Assert.False(await local.VerifyAsync(new IntegrityKeyReference(org, IntegrityKeyPurposes.RowIntegrityV1, name, 3), payload, v2.Mac, TestContext.Current.CancellationToken));
    }

    private sealed class TestHostEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Fact]
    public void TheOperatorCommandParsesStrictlyRemovesItsArgumentsAndIsRefusedOutsideDevelopmentAndTesting()
    {
        var org = Guid.CreateVersion7();
        string[] args = ["--urls", "http://127.0.0.1:1", IntegrityKeyActivationCommand.Flag, "--organization", org.ToString("D"), "--expected-current-version", "1", "--signing-key-version", "2", "--reason", "rotate"];
        Assert.True(IntegrityKeyActivationCommand.Requested(args, out var parsed, out var host));
        Assert.Equal(new IntegrityKeyActivationCommand.Arguments(org, 1, 2, "rotate", IntegrityKeyPurposes.RowIntegrityV1), parsed);
        Assert.Equal(["--urls", "http://127.0.0.1:1"], host);

        Assert.False(IntegrityKeyActivationCommand.Requested(["--urls", "x"], out _, out var untouched));
        Assert.Equal(["--urls", "x"], untouched);
        foreach (var bad in new[] { "-1", "01x", "" })
        {
            Assert.True(IntegrityKeyActivationCommand.Requested([IntegrityKeyActivationCommand.Flag, "--organization", org.ToString("D"), "--expected-current-version", "1", "--signing-key-version", bad, "--reason", "r"], out var invalid, out _));
            Assert.Null(invalid);
        }

        var services = new ServiceCollection().BuildServiceProvider();
        Assert.Equal(3, IntegrityKeyActivationCommand.Run(services, new TestHostEnvironment("Production"), parsed));
        Assert.Equal(3, IntegrityKeyActivationCommand.Run(services, new TestHostEnvironment("Staging"), parsed));
        Assert.Equal(2, IntegrityKeyActivationCommand.Run(services, new TestHostEnvironment("Development"), null));
    }
}
