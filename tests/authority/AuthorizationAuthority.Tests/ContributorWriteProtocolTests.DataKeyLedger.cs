using System.Text;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Application.Features.SearchAccess;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Content;
using AuthorizationAuthority.Domain.Features.Journal;
using EnterpriseForms.Integrity.Canonical;
using Xunit;
using static AuthorizationAuthority.Tests.ContributorWriteFixtures;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// E3-M2e-1 (docs/security/E3_M2E_CONTRACT_CLOSURE.md §3, §6.1) on the real <see cref="ContentService"/> write paths, with Envelope v2
/// acceptance <b>off</b> (the dark increment never enables it; acceptance end to end belongs to E3-M2e-2). Covered: the unchanged Prepare
/// refusal of New v2 groups (DK-4 and DK-5 are never reached), DK-6 and P-4 at Commit on operations recorded outside this build's
/// Prepare (forged or legacy), the incident they journal, the switch re-check, L-13 before the ledger is consulted, the environment
/// wrap codes (AM-9), permanent reservations, CARV 3 and the DK-8 journal body. The pure rules are in EnvelopeV2RulesTests.
/// </summary>
public sealed partial class ContributorWriteProtocolTests
{
    private static int Incidents(Harness h) => h.Store.Journal.Count(j => j.JournalEventKind == "IntegrityVerificationFailed");

    /// <summary>A Prepared operation (current stamp) holding the harness rows with the sealed item as a New two-item v2 batch.</summary>
    private static async Task<(Guid Operation, List<StagedContentItem> Staged, Guid Batch)> SeedNewV2GroupAsync(Harness h)
    {
        var batch = Guid.CreateVersion7();
        var operation = Guid.CreateVersion7();
        var rows = Change(h.Rows(), h.SealedItem, _ => V2Field(h, h.SealedField, h.SealedItem, 10, batch));
        rows.Add(V2Field(h, SecondSealedField, Guid.CreateVersion7(), 20, batch));
        var staged = await SeedPreparedWithoutPrepareRulesAsync(h, operation, h.Stage(rows, operation));
        return (operation, staged, batch);
    }

    private static ContentDataKeyBinding LedgerRow(Harness h, Guid id, Guid operation, int count = 2, byte commitment = 0xdd, Guid? record = null, Guid? organization = null) => new()
    {
        ContentDataKeyBindingId = Guid.CreateVersion7(),
        DataKeyIdentifier = id,
        OrganizationId = organization ?? h.Org,
        FormRecordId = record ?? h.Record,
        EnvelopePayloadKindCode = ContentDataKeyBindingKinds.FieldValueBatch,
        DataKeyCommitmentBytes = Fill(32, commitment),
        BindingItemCount = count,
        BindingContentChangeOperationId = operation,
        DataKeyBoundOn = DateTimeOffset.UtcNow,
    };

    // E3-M2f-2: these tests bind organizations, so their business build declares level 2 (as every E3-M2f-1+ build does); a level-1 caller
    // would be refused by the capability floor before any ledger rule is reached (ContributorWriteProtocolTests.EnvelopeCapability).
    private static Task<ContentRevision> CommitSeededAsync(Harness h, Guid operation, List<StagedContentItem> staged) =>
        h.Service.CommitAsync(Harness.Actor(h.Owner), operation, new CommitContentChangeRequest(h.Org, h.Record, staged), Guid.CreateVersion7(), CancellationToken.None, ClientEnvelopeCapability.Current);

    [Fact]
    public async Task WithAcceptanceOffANewV2GroupIsRefusedAtPrepareBeforeTheLedgerIsConsultedAndNothingIsBound()
    {
        var h = await new Harness().CreatedAsync();
        var operation = Guid.CreateVersion7();
        var rows = Change(h.Rows(), h.SealedItem, _ => V2Field(h, h.SealedField, h.SealedItem, 10, Guid.CreateVersion7()));
        var signatures = h.Signer.SignatureCount;

        var ex = await Assert.ThrowsAsync<AuthorityException>(() => h.PrepareAsync(h.Owner, operation, h.Stage(rows, operation), withCurrent: true));
        Assert.Equal("ENVELOPE_INVALID: " + EnvelopeV2Rules.Reasons.VersionNotEnabled, ex.Message); // unchanged from E3-M2d
        Assert.Equal(signatures, h.Signer.SignatureCount);
        Assert.Equal(0, h.Store.DataKeyLedgerChecks); // DK-4/DK-5 are reachable only with acceptance on
        Assert.Empty(h.Store.DataKeyBindings);
        Assert.DoesNotContain(h.Store.Operations, o => o.ContentChangeOperationId == operation);
    }

    [Fact]
    public async Task Dk6ANewGroupMatchingItsBindingIsStillRefusedAtCommitWhileAcceptanceIsOff()
    {
        var h = await new Harness().CreatedAsync();
        var head = (h.Head.CommittedContentRevision, h.Head.CommittedManifestRoot);
        var (operation, staged, batch) = await SeedNewV2GroupAsync(h);
        h.Store.DataKeyBindings.Add(LedgerRow(h, batch, operation)); // exactly the derived tuple: DK-6 holds
        var incidents = Incidents(h);

        var ex = await Assert.ThrowsAsync<AuthorityException>(() => CommitSeededAsync(h, operation, staged));
        Assert.Equal("ENVELOPE_INVALID: " + EnvelopeV2Rules.Reasons.VersionNotEnabled, ex.Message); // the switch re-check: tightening wins
        var stored = h.Store.Operations.Single(o => o.ContentChangeOperationId == operation);
        Assert.Equal((ContentOperationStates.Aborted, EnvelopeV2Rules.NotEnabledAtCommitReason, (int?)null), (stored.OperationStateName, stored.OperationQuarantineReasonCode, stored.CommitAcceptanceRuleVersion));
        Assert.Equal(incidents, Incidents(h)); // a consistent ledger is no incident
        Assert.Equal(head, (h.Head.CommittedContentRevision, h.Head.CommittedManifestRoot));
        Assert.Single(h.Store.DataKeyBindings); // the reservation stays (OD-M2e-03)
        Assert.True(h.Store.DataKeyLedgerChecks > 0); // L-13 before the ledger was read
    }

    [Theory]
    [InlineData("no binding")]
    [InlineData("commitment differs")]
    [InlineData("item count differs")]
    [InlineData("another record")]
    [InlineData("another organization")]
    [InlineData("an extra binding of this operation")]
    public async Task Dk6AForgedOrInconsistentOperationIsAbortedWithAMinimalIncident(string variant)
    {
        var h = await new Harness().CreatedAsync();
        var head = (h.Head.CommittedContentRevision, h.Head.CommittedManifestRoot, h.Head.CommittedOperationId);
        var (operation, staged, batch) = await SeedNewV2GroupAsync(h);
        switch (variant)
        {
            case "commitment differs": h.Store.DataKeyBindings.Add(LedgerRow(h, batch, operation, commitment: 0x01)); break;
            case "item count differs": h.Store.DataKeyBindings.Add(LedgerRow(h, batch, operation, count: 3)); break;
            case "another record": h.Store.DataKeyBindings.Add(LedgerRow(h, batch, operation, record: Guid.CreateVersion7())); break;
            case "another organization": h.Store.DataKeyBindings.Add(LedgerRow(h, batch, operation, organization: Guid.CreateVersion7())); break;
            case "an extra binding of this operation":
                h.Store.DataKeyBindings.Add(LedgerRow(h, batch, operation));
                h.Store.DataKeyBindings.Add(LedgerRow(h, Guid.CreateVersion7(), operation));
                break;
        }

        var incidents = Incidents(h);
        var integrityRevision = h.State.ContentIntegrityRevision;
        var ex = await Assert.ThrowsAsync<AuthorityException>(() => CommitSeededAsync(h, operation, staged));
        Assert.Equal((AuthorityErrorCodes.DataKeyBindingMismatch, 409, false), (ex.ErrorCode, ex.HttpStatusCode, ex.Retryable));
        Assert.Equal(DataKeyLedgerFailures.BindingMismatchReason, h.Store.Operations.Single(o => o.ContentChangeOperationId == operation).OperationQuarantineReasonCode);
        Assert.Equal(incidents + 1, Incidents(h));
        Assert.Equal(integrityRevision + 1, h.State.ContentIntegrityRevision); // W4: the incident advances the content-integrity revision only
        var incident = h.Store.Journal.Last(j => j.JournalEventKind == "IntegrityVerificationFailed");
        Assert.Contains("\"failure_reason_code\":\"DATA_KEY_BINDING_MISMATCH\"", incident.JournalEventBodyJson, StringComparison.Ordinal);
        Assert.DoesNotContain(batch.ToString("D"), incident.JournalEventBodyJson, StringComparison.Ordinal); // identifier-free incident
        Assert.Equal(head, (h.Head.CommittedContentRevision, h.Head.CommittedManifestRoot, h.Head.CommittedOperationId));
        Assert.DoesNotContain(h.Store.Commits, c => c.CommittedOperationId == operation);
    }

    [Theory]
    [InlineData("unbound")]
    [InlineData("bound to an aborted operation")]
    [InlineData("bound to a still-Prepared operation")]
    [InlineData("bound to another record")]
    [InlineData("substituted commitment")]
    [InlineData("more rows than the batch")]
    public async Task P4ACarriedGroupThatTheLedgerDoesNotConfirmIsAbortedWithAnIncident(string variant)
    {
        var v2 = await AcceptedV2HeadAsync();
        var h = v2.H;
        h.EnvelopeCapability = ClientEnvelopeCapability.Current; // E3-M2f-2: a level-2 business build (the organization is bound below)
        var earlier = Guid.CreateVersion7();
        h.Store.Operations.Add(new ContentChangeOperation
        {
            ContentChangeOperationId = earlier,
            OrganizationId = h.Org,
            FormRecordId = variant == "bound to another record" ? Guid.CreateVersion7() : h.Record,
            OperationStateName = variant == "bound to a still-Prepared operation" ? ContentOperationStates.Prepared : ContentOperationStates.Aborted,
            PrepareAcceptanceRuleVersion = 3,
            OperationIdempotencyKey = "earlier-" + earlier.ToString("N"),
            OperationRequestDigest = new byte[32],
        });
        switch (variant)
        {
            case "bound to an aborted operation" or "bound to a still-Prepared operation" or "bound to another record": h.Store.DataKeyBindings.Add(LedgerRow(h, v2.Batch, earlier)); break;
            case "substituted commitment": h.Store.DataKeyBindings.Add(LedgerRow(h, v2.Batch, earlier, commitment: 0x02)); break;
            case "more rows than the batch": h.Store.DataKeyBindings.Add(LedgerRow(h, v2.Batch, earlier, count: 1)); break;
        }

        if (variant is "substituted commitment" or "more rows than the batch")
        {
            h.Store.Operations.Single(o => o.ContentChangeOperationId == earlier).OperationStateName = ContentOperationStates.Committed; // isolate the tuple check
        }

        var head = (h.Head.CommittedContentRevision, h.Head.CommittedManifestRoot);
        var operation = Guid.CreateVersion7();
        var candidate = h.Stage(Change(v2.Rows(), h.Other, r => r.With("string_value", RowValue.Text("owner edit"))), operation);
        var prepared = await h.PrepareAsync(h.Owner, operation, candidate, withCurrent: true); // carried rows pass Prepare (DK-1)

        var ex = await Assert.ThrowsAsync<AuthorityException>(() => h.CommitAsync(h.Owner, prepared, candidate));
        Assert.Equal(AuthorityErrorCodes.DataKeyBindingMismatch, ex.ErrorCode);
        Assert.Equal(DataKeyLedgerFailures.BindingMismatchReason, h.Store.Operations.Single(o => o.ContentChangeOperationId == operation).OperationQuarantineReasonCode);
        Assert.Equal(head, (h.Head.CommittedContentRevision, h.Head.CommittedManifestRoot));
    }

    [Fact]
    public async Task TheLedgerIsVerifiedBeforeItIsConsultedAndAnUnverifiedLedgerLeavesTheOperationPrepared()
    {
        var h = await new Harness().CreatedAsync();
        var (operation, staged, batch) = await SeedNewV2GroupAsync(h);
        h.Store.DataKeyBindings.Add(LedgerRow(h, batch, operation));
        h.EnvelopeCapability = ClientEnvelopeCapability.Current; // E3-M2f-2: a level-2 business build in a bound organization
        h.Store.DataKeyLedgerRefused = true;
        var incidents = Incidents(h);

        var ex = await Assert.ThrowsAsync<AuthorityException>(() => CommitSeededAsync(h, operation, staged));
        Assert.Equal((AuthorityErrorCodes.ContentDataKeyLedgerNotEnforced, 503, false), (ex.ErrorCode, ex.HttpStatusCode, ex.Retryable));
        Assert.Equal(ContentOperationStates.Prepared, h.Store.Operations.Single(o => o.ContentChangeOperationId == operation).OperationStateName); // nothing decided
        Assert.Equal(incidents, Incidents(h));

        // A v1-only write never needs the ledger (no v2 row, no binding of its operation).
        Assert.Equal(2, (await h.WriteAsync(h.Owner, Change(h.Rows(), h.Other, r => r.With("string_value", RowValue.Text("v1 only"))), withCurrent: true)).CommittedContentRevision);
    }

    [Fact]
    public async Task OutsideDevelopmentOrTestingNoV2RowIsAdmittedAtPrepareOrCommitAndV1IsUnaffected()
    {
        var staging = EnvelopeV2AcceptanceSwitch.Create(null, "Staging", ContentAcceptancePolicy.Resolve("ReleaseA", null));
        Assert.Empty(staging.AllowedWrapCodes);
        var head = new V2Head(new Harness(envelopeV2Acceptance: staging), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var h = head.H;
        await h.AcceptDirectlyAsync(head.Rows());

        var operation = Guid.CreateVersion7();
        var ex = await Assert.ThrowsAsync<AuthorityException>(() => h.PrepareAsync(h.Owner, operation, h.Stage(head.Rows(), operation), withCurrent: true));
        Assert.Equal("ENVELOPE_INVALID: " + EnvelopeV2Rules.Reasons.WrapAlgorithmUnsupported, ex.Message); // even a byte-identical carry (AM-9)

        var seeded = Guid.CreateVersion7();
        var staged = await SeedPreparedWithoutPrepareRulesAsync(h, seeded, h.Stage(Change(head.Rows(), h.Other, r => r.With("string_value", RowValue.Text("x"))), seeded));
        var atCommit = await Assert.ThrowsAsync<AuthorityException>(() => CommitSeededAsync(h, seeded, staged));
        Assert.Equal("ENVELOPE_INVALID: " + EnvelopeV2Rules.Reasons.WrapAlgorithmUnsupported, atCommit.Message);
        Assert.Equal("ENVELOPE_INVALID_AT_COMMIT", h.Store.Operations.Single(o => o.ContentChangeOperationId == seeded).OperationQuarantineReasonCode);

        var v1 = new Harness(envelopeV2Acceptance: staging);
        Assert.Equal(1, (await v1.WriteAsync(v1.Owner, v1.Rows(), withCurrent: true)).CommittedContentRevision);
    }

    [Fact]
    public async Task AReservationSurvivesTheExpiryAbortOfItsOperation()
    {
        var h = await new Harness().CreatedAsync();
        var (operation, staged, batch) = await SeedNewV2GroupAsync(h);
        h.Store.DataKeyBindings.Add(LedgerRow(h, batch, operation));
        h.Store.Operations.Single(o => o.ContentChangeOperationId == operation).OperationExpiresOn = DateTimeOffset.UtcNow.AddMinutes(-1);

        var ex = await Assert.ThrowsAsync<AuthorityException>(() => CommitSeededAsync(h, operation, staged));
        Assert.Equal(AuthorityErrorCodes.OperationExpired, ex.ErrorCode);
        Assert.Equal(ContentOperationStates.Aborted, h.Store.Operations.Single(o => o.ContentChangeOperationId == operation).OperationStateName);
        Assert.Equal(batch, Assert.Single(h.Store.DataKeyBindings).DataKeyIdentifier); // permanent (OD-M2e-03): DK-4 refuses it forever
        Assert.Equal(EnvelopeV2Rules.Reasons.DataKeyIdentifierReused, DataKeyLedgerRules.PrepareViolation(DataKeyLedgerRules.GroupsOf(staged.Select(s => RowCanonicalDecoder.Decode(Convert.FromBase64String(s.CanonicalBytesBase64)))), h.Store.DataKeyBindings));
    }

    [Fact]
    public async Task AVersion2PreparedOperationCannotBeCommittedByAVersion3Build()
    {
        var h = await new Harness().CreatedAsync();
        var operation = Guid.CreateVersion7();
        var staged = await SeedPreparedWithoutPrepareRulesAsync(h, operation, h.Stage(Change(h.Rows(), h.Other, r => r.With("string_value", RowValue.Text("v2 build"))), operation));
        h.Store.Operations.Single(o => o.ContentChangeOperationId == operation).PrepareAcceptanceRuleVersion = 2;

        var ex = await Assert.ThrowsAsync<AuthorityException>(() => CommitSeededAsync(h, operation, staged));
        Assert.Equal(AuthorityErrorCodes.OperationRulesOutdated, ex.ErrorCode);
        Assert.Equal(ContentAcceptanceRules.OutdatedAbortReason, h.Store.Operations.Single(o => o.ContentChangeOperationId == operation).OperationQuarantineReasonCode);
    }

    [Fact]
    public async Task Dk8EveryCommitArchivesTheBindingTupleArrayAndTheBodyStillChainsForTheJournalVerifier()
    {
        var h = new Harness();
        await h.WriteAsync(h.Owner, h.Rows(), withCurrent: true);
        await h.WriteAsync(h.Owner, Change(h.Rows(), h.Other, r => r.With("string_value", RowValue.Text("second"))), withCurrent: true);
        var commits = h.Store.Journal.Where(j => j.JournalStreamName == JournalStreamNames.Content).OrderBy(j => j.JournalStreamRevision).ToList();
        Assert.Equal(2, commits.Count);
        byte[]? previous = null;
        foreach (var entry in commits)
        {
            Assert.Equal("ContentHeadCommitted", entry.JournalEventKind);
            Assert.Contains("\"new_data_key_bindings\":[]", entry.JournalEventBodyJson, StringComparison.Ordinal); // present (empty) from CARV 3
            var verified = SearchAccessJournalVerifier.VerifyContent(entry, h.Org, entry.JournalStreamRevision, previous ?? entry.JournalPreviousDigest);
            Assert.Equal(h.Record, verified.FormRecordId);
            previous = entry.JournalEventDigest;
        }
    }
}
