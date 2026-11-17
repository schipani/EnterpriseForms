using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Content;
using EnterpriseForms.Integrity.Canonical;
using Xunit;
using static AuthorizationAuthority.Tests.ContributorWriteFixtures;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// E3-M2d (CARV 2; owner decisions D-6, D-7, D-10, D-12) on the real <see cref="ContentService"/> write paths: the Prepare-only
/// Envelope v2 rules. A v2 head is modelled as accepted history (<see cref="Harness.AcceptDirectlyAsync"/>): no build accepts v2
/// before E3-M2e. Carried v2 rows (byte-identical to the current head) pass Prepare; E3-M2e: their Commit is decided by the data-key
/// ledger (P-4), and a head accepted outside it has no binding, so they are refused there as a ledger mismatch; every new v2 row,
/// a data-key id reused by a new row (DK-3), a v2-to-v1 downgrade and a v1 row reusing a current v2 wrapped key are refused before
/// anything is signed. The rules themselves are in EnvelopeV2RulesTests.
/// </summary>
public sealed partial class ContributorWriteProtocolTests
{
    private static readonly Guid SecondSealedField = Guid.CreateVersion7();

    /// <summary>One item of the harness's v2 field batch: shared id, commitment, wrapped key and reference; its own nonce and ciphertext.</summary>
    private static Row V2Field(Harness h, Guid field, Guid item, byte seed, Guid batch, byte wrapped = 0xaa) =>
        AsSchema2(Sealed(h.Org, h.Record, field, item, seed), h.Org, dataKeyIdentifier: batch).With("wrapped_data_key_bytes", RowValue.Bytes(Fill(60, wrapped)));

    /// <summary>The harness rows with the sealed item and a second sealed item as one v2 batch, and the document as v2.</summary>
    private static List<Row> V2Rows(Harness h, Guid batch, Guid secondItem, Guid documentBatch) =>
    [
        Header(h.Org, h.Record),
        Text(h.Org, h.Record, h.SharedField, h.Shared, "before"),
        Text(h.Org, h.Record, h.OtherField, h.Other, "not shared"),
        V2Field(h, h.SealedField, h.SealedItem, 10, batch),
        V2Field(h, SecondSealedField, secondItem, 20, batch),
        AsSchema2(Attachment(h.Org, h.Record, h.Document), h.Org, dataKeyIdentifier: documentBatch, commitment: 0xee),
    ];

    private sealed record V2Head(Harness H, Guid Batch, Guid SecondItem, Guid DocumentBatch)
    {
        public List<Row> Rows() => V2Rows(H, Batch, SecondItem, DocumentBatch);
    }

    private static async Task<V2Head> AcceptedV2HeadAsync(bool contributorWritesEnabled = true)
    {
        var h = new Harness(contributorWritesEnabled);
        var head = new V2Head(h, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        await h.AcceptDirectlyAsync(head.Rows());
        return head;
    }

    /// <summary>
    /// E3-M2e (replaces the E3-M2d not-enabled refusal): carried v2 rows of a head accepted outside the data-key ledger pass Prepare
    /// (OA-M2d-2) but cannot be committed: P-4 finds no binding, the operation is aborted and a minimal content-stream incident is
    /// journaled (OD-M2e-11). Nothing is accepted.
    /// </summary>
    private static async Task CommitRefusedAsUnboundAsync(Harness h, Guid actor, List<Row> rows)
    {
        var head = (h.Head.CommittedContentRevision, h.Head.CommittedOperationId, h.Head.CommittedManifestRoot);
        var commits = h.Store.Commits.Count;
        var operation = Guid.CreateVersion7();
        var candidate = h.Stage(rows, operation);
        var prepared = await h.PrepareAsync(actor, operation, candidate, withCurrent: true); // carried v2 rows pass Prepare

        var incidents = h.Store.Journal.Count(j => j.JournalEventKind == "IntegrityVerificationFailed");
        var ex = await Assert.ThrowsAsync<AuthorityException>(() => h.CommitAsync(actor, prepared, candidate));
        Assert.Equal((AuthorityErrorCodes.DataKeyBindingMismatch, 409), (ex.ErrorCode, ex.HttpStatusCode));
        var stored = h.Store.Operations.Single(o => o.ContentChangeOperationId == operation);
        Assert.Equal((ContentOperationStates.Aborted, DataKeyLedgerFailures.BindingMismatchReason, (int?)ContentAcceptanceRules.CurrentVersion, (int?)null),
            (stored.OperationStateName, stored.OperationQuarantineReasonCode, stored.PrepareAcceptanceRuleVersion, stored.CommitAcceptanceRuleVersion));
        var incident = h.Store.Journal.Last(j => j.JournalEventKind == "IntegrityVerificationFailed");
        Assert.Equal(incidents + 1, h.Store.Journal.Count(j => j.JournalEventKind == "IntegrityVerificationFailed"));
        Assert.Contains("\"failure_reason_code\":\"" + DataKeyLedgerFailures.BindingMismatchReason + "\"", incident.JournalEventBodyJson, StringComparison.Ordinal);
        Assert.Empty(h.Store.DataKeyBindings);
        Assert.Equal(head, (h.Head.CommittedContentRevision, h.Head.CommittedOperationId, h.Head.CommittedManifestRoot));
        Assert.Equal(commits, h.Store.Commits.Count);
    }

    [Fact]
    public async Task AnOwnerEditCarryingATwoItemV2BatchAndAV2DocumentPassesPrepareAndWithoutALedgerBindingIsNotCommitted()
    {
        var v2 = await AcceptedV2HeadAsync();
        var rows = Change(v2.Rows(), v2.H.Other, r => r.With("string_value", RowValue.Text("owner edit")));
        await CommitRefusedAsUnboundAsync(v2.H, v2.H.Owner, rows);
    }

    [Fact]
    public async Task AShareEditCarryingTheV2RowsPassesPrepareAndWithoutALedgerBindingIsNotCommitted()
    {
        var v2 = await AcceptedV2HeadAsync();
        v2.H.ShareWithContributor("contributor", false, v2.H.Shared);
        await CommitRefusedAsUnboundAsync(v2.H, v2.H.Contributor, Change(v2.Rows(), v2.H.Shared, r => r.With("string_value", RowValue.Text("contributor edit"))));
    }

    public static TheoryData<string, string> NewV2Rows => new()
    {
        { "fresh data-key id for an existing v2 item", EnvelopeV2Rules.Reasons.VersionNotEnabled },
        { "nonce changed under the carried id", EnvelopeV2Rules.Reasons.DataKeyIdentifierReused },
        { "ciphertext changed under the carried id", EnvelopeV2Rules.Reasons.DataKeyIdentifierReused },
        { "a new item joining the current batch", EnvelopeV2Rules.Reasons.DataKeyIdentifierReused },
        { "a new item taking the document's id", EnvelopeV2Rules.Reasons.DataKeyIdentifierReused },
        { "a new item in a fresh batch", EnvelopeV2Rules.Reasons.VersionNotEnabled },
        { "document re-sealed under a fresh id", EnvelopeV2Rules.Reasons.VersionNotEnabled },
    };

    private static List<Row> NewV2Candidate(V2Head v2, string variant)
    {
        var h = v2.H;
        var newItem = Guid.CreateVersion7();
        return variant switch
        {
            "fresh data-key id for an existing v2 item" => Change(v2.Rows(), h.SealedItem, _ => V2Field(h, h.SealedField, h.SealedItem, 10, Guid.CreateVersion7(), wrapped: 0xab)),
            "nonce changed under the carried id" => Change(v2.Rows(), h.SealedItem, r => r.With("encryption_nonce_bytes", RowValue.Bytes(Fill(12, 0x77)))),
            "ciphertext changed under the carried id" => Change(v2.Rows(), h.SealedItem, r => r.With("encrypted_value_bytes", RowValue.Bytes(Fill(24, 0x78)))),
            "a new item joining the current batch" => [.. v2.Rows(), V2Field(h, h.OtherField, newItem, 30, v2.Batch)],
            // The document is removed (permitted), so only the current snapshot holds its id: DK-3 against the current revision.
            "a new item taking the document's id" => [.. v2.Rows().Where(r => r.ItemId != h.Document), V2Field(h, h.OtherField, newItem, 30, v2.DocumentBatch, wrapped: 0xac)],
            "a new item in a fresh batch" => [.. v2.Rows(), V2Field(h, h.OtherField, newItem, 30, Guid.CreateVersion7(), wrapped: 0xad)],
            "document re-sealed under a fresh id" => Change(v2.Rows(), h.Document, r => AsSchema2(Attachment(h.Org, h.Record, h.Document), h.Org, commitment: 0xef).With("wrapped_data_key", RowValue.Bytes(Fill(60, 0x55)))),
            _ => throw new ArgumentOutOfRangeException(nameof(variant)),
        };
    }

    [Theory]
    [MemberData(nameof(NewV2Rows))]
    public async Task ANewV2RowIsRefusedBeforeSigningOnAnOwnerEdit(string variant, string reason)
    {
        var v2 = await AcceptedV2HeadAsync();
        await RefusedBeforeSigningAsync(v2.H, v2.H.Owner, NewV2Candidate(v2, variant), reason);
    }

    [Fact]
    public async Task ANewV2RowIsRefusedBeforeSigningOnAShareEdit()
    {
        var v2 = await AcceptedV2HeadAsync();
        v2.H.ShareWithContributor("contributor", false, v2.H.SealedItem);
        await RefusedBeforeSigningAsync(v2.H, v2.H.Contributor, NewV2Candidate(v2, "fresh data-key id for an existing v2 item"), EnvelopeV2Rules.Reasons.VersionNotEnabled);
    }

    [Fact]
    public async Task AV1ItemCannotBecomeAV2RowOrStartAV2Batch()
    {
        // v1 -> v2 is a new v2 row (the M4 writers' transition): not enabled in E3-M2d, on the v1 head the harness creates.
        var h = await new Harness().CreatedAsync();
        await RefusedBeforeSigningAsync(h, h.Owner, Change(h.Rows(), h.SealedItem, _ => V2Field(h, h.SealedField, h.SealedItem, 10, Guid.CreateVersion7())), EnvelopeV2Rules.Reasons.VersionNotEnabled);
    }

    public static TheoryData<string> Downgrades => new() { "field", "document" };

    [Theory]
    [MemberData(nameof(Downgrades))]
    public async Task AV2ItemCannotBeDowngradedToAV1EnvelopeByTheOwner(string kind)
    {
        var v2 = await AcceptedV2HeadAsync();
        var h = v2.H;
        var rows = kind == "field"
            ? Change(v2.Rows(), h.SealedItem, _ => Sealed(h.Org, h.Record, h.SealedField, h.SealedItem, 40))
            : Change(v2.Rows(), h.Document, _ => Attachment(h.Org, h.Record, h.Document, "objects/v1-again").With("wrapped_data_key", RowValue.Bytes(Fill(60, 0x66))));
        await RefusedBeforeSigningAsync(h, h.Owner, rows, EnvelopeV2Rules.Reasons.Downgrade);
    }

    [Fact]
    public async Task AV2ItemCannotBeDowngradedToAV1EnvelopeByAShareEditor()
    {
        var v2 = await AcceptedV2HeadAsync();
        v2.H.ShareWithContributor("contributor", false, v2.H.SealedItem);
        await RefusedBeforeSigningAsync(v2.H, v2.H.Contributor, Change(v2.Rows(), v2.H.SealedItem, _ => Sealed(v2.H.Org, v2.H.Record, v2.H.SealedField, v2.H.SealedItem, 41)), EnvelopeV2Rules.Reasons.Downgrade);
    }

    [Fact]
    public async Task AV1RowCannotReuseAWrappedKeyOfTheCurrentV2Batch()
    {
        // The v2 items are removed (permitted) and a new v1 row takes their wrapped key: only the against-current rule can see it.
        var v2 = await AcceptedV2HeadAsync();
        var h = v2.H;
        var rows = v2.Rows().Where(r => r.ItemId != h.SealedItem && r.ItemId != v2.SecondItem).ToList();
        rows.Add(Sealed(h.Org, h.Record, h.SealedField, Guid.CreateVersion7(), 50).With("wrapped_data_key_bytes", RowValue.Bytes(Fill(60, 0xaa))));
        await RefusedBeforeSigningAsync(h, h.Owner, rows, EnvelopeRules.Reasons.WrappedKeyReused);
    }

    [Fact]
    public async Task RemovingEveryV2ItemCommitsBecauseNoV2RowIsStaged()
    {
        var v2 = await AcceptedV2HeadAsync();
        var h = v2.H;
        var rows = v2.Rows().Where(r => r.ItemId != h.SealedItem && r.ItemId != v2.SecondItem && r.ItemId != h.Document).ToList();
        Assert.Equal(2, (await h.WriteAsync(h.Owner, rows, withCurrent: true)).CommittedContentRevision);
    }

    [Fact]
    public async Task AnInconsistentV2BatchIsRefusedAtPrepareBeforeTheAcceptanceDecision()
    {
        // Batch consistency is re-derivable from the candidate bytes, so it is reported before the (Prepare-only) not-enabled refusal.
        var h = new Harness();
        var batch = Guid.CreateVersion7();
        var rows = new List<Row>
        {
            Header(h.Org, h.Record),
            V2Field(h, h.SealedField, h.SealedItem, 10, batch),
            V2Field(h, SecondSealedField, Guid.CreateVersion7(), 20, batch).With("data_key_commitment_bytes", RowValue.Bytes(Fill(32, 0x01))),
        };
        var operation = Guid.CreateVersion7();
        var signatures = h.Signer.SignatureCount;
        var ex = await Assert.ThrowsAsync<AuthorityException>(() => h.PrepareAsync(h.Owner, operation, h.Stage(rows, operation), withCurrent: true));
        Assert.Equal("ENVELOPE_INVALID: " + EnvelopeV2Rules.Reasons.BatchInconsistent, ex.Message);
        Assert.Equal(signatures, h.Signer.SignatureCount);
        Assert.Empty(h.Store.Operations);
    }

    [Fact]
    public async Task AtCommitTheV2RulesAreReappliedToTheStagedBytesBeforeTheNotEnabledRefusal()
    {
        var h = await new Harness().CreatedAsync();
        var head = h.Head.CommittedContentRevision;
        var batch = Guid.CreateVersion7();
        var operation = Guid.CreateVersion7();
        var rows = Change(h.Rows(), h.SealedItem, _ => V2Field(h, h.SealedField, h.SealedItem, 10, batch));
        rows.Add(V2Field(h, SecondSealedField, Guid.CreateVersion7(), 20, batch).With("encryption_nonce_bytes", RowValue.Bytes(Fill(12, 12)))); // seed 10's nonce
        var staged = await SeedPreparedWithoutPrepareRulesAsync(h, operation, h.Stage(rows, operation));

        var ex = await Assert.ThrowsAsync<AuthorityException>(() => h.Service.CommitAsync(Harness.Actor(h.Owner), operation, new CommitContentChangeRequest(h.Org, h.Record, staged), Guid.CreateVersion7(), CancellationToken.None));
        Assert.Equal("ENVELOPE_INVALID: " + EnvelopeRules.Reasons.NonceReused, ex.Message);
        Assert.Equal("ENVELOPE_INVALID_AT_COMMIT", h.Store.Operations.Single(o => o.ContentChangeOperationId == operation).OperationQuarantineReasonCode);
        Assert.Equal(head, h.Head.CommittedContentRevision);
    }
}
