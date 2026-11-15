using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Content;
using EnterpriseForms.Integrity.Canonical;
using Xunit;
using static AuthorizationAuthority.Tests.ContributorWriteFixtures;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// E3-M2b-2 R-M2b2-11 (contract §4.4 S-4, §23.14): the Authority schema/envelope tie on the real <see cref="ContentService"/>
/// write paths. Prepare refuses a schema-2 / envelope-1 or schema-1 / envelope-2 envelope row (field values and documents)
/// with <c>ENVELOPE_SCHEMA_MISMATCH</c> on record creation, owner edits and share edits, before anything is signed or stored;
/// Commit re-derives the tie on the verified staged bytes of an operation prepared without it, aborts it and accepts nothing.
/// Schema 1 / envelope 1 is unchanged and schema 2 / envelope 2 stays unsupported (EnvelopeRules). The protection-migration
/// path and the census are in ProtectionReconciliationTests; the rule itself in EnvelopeSchemaTieTests.
/// </summary>
public sealed partial class ContributorWriteProtocolTests
{
    /// <summary>The harness rows with the sealed field item (<paramref name="kind"/> "field") or the document re-encoded as (schema, envelope).</summary>
    private static List<Row> WithPair(Harness h, string kind, string pair) => pair switch
    {
        "schema 2 / envelope 1" => WithSchema2(h, kind, 1),
        "schema 2 / envelope 2" => WithSchema2(h, kind, 2),
        "schema 1 / envelope 2" => Change(h.Rows(), kind == "field" ? h.SealedItem : h.Document,
            r => (kind == "field" ? Sealed(h.Org, h.Record, h.SealedField, h.SealedItem, 71) : r).With("encryption_envelope_version", RowValue.Int32(2))),
        _ => throw new ArgumentOutOfRangeException(nameof(pair)),
    };

    public static TheoryData<string, string> MismatchedPairs => new()
    {
        { "field", "schema 2 / envelope 1" },
        { "field", "schema 1 / envelope 2" },
        { "document", "schema 2 / envelope 1" },
        { "document", "schema 1 / envelope 2" },
    };

    private const string Mismatch = "ENVELOPE_INVALID: " + EnvelopeSchemaTie.MismatchReason;

    [Theory]
    [MemberData(nameof(MismatchedPairs))]
    public async Task S4RefusesAMismatchedPairOnRecordCreationBeforeAnythingIsSigned(string kind, string pair)
    {
        var h = new Harness();
        var operation = Guid.CreateVersion7();
        var signatures = h.Signer.SignatureCount;
        var ex = await Assert.ThrowsAsync<AuthorityException>(() => h.PrepareAsync(h.Owner, operation, h.Stage(WithPair(h, kind, pair), operation), withCurrent: true));

        Assert.Equal(AuthorityErrorCodes.InvalidCommand, ex.ErrorCode);
        Assert.Equal(422, ex.HttpStatusCode);
        Assert.False(ex.Retryable);
        Assert.Equal(Mismatch, ex.Message);
        Assert.Equal(signatures, h.Signer.SignatureCount);
        Assert.Empty(h.Store.Operations);
        Assert.Empty(h.Store.CandidateLeaves);
        Assert.Empty(h.Store.Commits);
        Assert.Equal(0, h.Head.CommittedContentRevision);
    }

    [Theory]
    [MemberData(nameof(MismatchedPairs))]
    public async Task S4RefusesAMismatchedPairOnAnOwnerEditAndTheHeadStays(string kind, string pair)
    {
        var h = await new Harness().CreatedAsync();
        var commits = h.Store.Commits.Count;
        await RefusedBeforeSigningAsync(h, h.Owner, WithPair(h, kind, pair), EnvelopeSchemaTie.MismatchReason);
        Assert.Equal(commits, h.Store.Commits.Count);
    }

    [Theory]
    [InlineData("schema 2 / envelope 1")]
    [InlineData("schema 1 / envelope 2")]
    public async Task S4RefusesAMismatchedPairOnAShareEditBeforeTheShareClassifier(string pair)
    {
        var h = await new Harness().CreatedAsync();
        h.ShareWithContributor("contributor", false, h.SealedItem);
        await RefusedBeforeSigningAsync(h, h.Contributor, WithPair(h, "field", pair), EnvelopeSchemaTie.MismatchReason);
    }

    [Theory]
    [InlineData("field")]
    [InlineData("document")]
    public async Task SchemaTwoWithEnvelopeTwoPassesTheTieAndStaysUnsupported(string kind)
    {
        var created = new Harness();
        var operation = Guid.CreateVersion7();
        var ex = await Assert.ThrowsAsync<AuthorityException>(() => created.PrepareAsync(created.Owner, operation, created.Stage(WithPair(created, kind, "schema 2 / envelope 2"), operation), withCurrent: true));
        Assert.Equal("ENVELOPE_INVALID: " + EnvelopeRules.Reasons.SchemaUnsupported, ex.Message);
        Assert.Empty(created.Store.Operations);

        var edited = await new Harness().CreatedAsync();
        await RefusedBeforeSigningAsync(edited, edited.Owner, WithPair(edited, kind, "schema 2 / envelope 2"), EnvelopeRules.Reasons.SchemaUnsupported);
    }

    [Fact]
    public async Task SchemaOneWithEnvelopeOneIsStillAcceptedOnCreationOwnerAndShareEdits()
    {
        var h = new Harness();
        Assert.Equal(1, (await h.WriteAsync(h.Owner, h.Rows(), withCurrent: true)).CommittedContentRevision); // creation: field + document envelopes
        Assert.Equal(2, (await h.WriteAsync(h.Owner, Change(h.Rows(), h.SealedItem, _ => Sealed(h.Org, h.Record, h.SealedField, h.SealedItem, 60)), withCurrent: true)).CommittedContentRevision);
        Assert.Equal(3, (await h.WriteAsync(h.Owner, Change(h.Rows(), h.SealedItem, _ => Sealed(h.Org, h.Record, h.SealedField, h.SealedItem, 60)), withCurrent: true)).CommittedContentRevision); // byte-identical carry
        h.ShareWithContributor("contributor", false, h.SealedItem);
        Assert.Equal(4, (await h.WriteAsync(h.Contributor, Change(h.Rows(), h.SealedItem, _ => Sealed(h.Org, h.Record, h.SealedField, h.SealedItem, 61)), withCurrent: true)).CommittedContentRevision);
        Assert.All(h.Accepted.Where(a => EnvelopeRules.IsEnvelopeRow(RowCanonicalDecoder.Decode(a.Row.Bytes))), a =>
        {
            var document = RowCanonicalDecoder.Decode(a.Row.Bytes);
            Assert.Equal(1, document.Header.SchemaVersion);
            Assert.Null(EnvelopeSchemaTie.Violation(document));
        });
    }

    /// <summary>
    /// An operation prepared without S-4 (its leaves signed as by a build without the tie, stamped at this build's CARV version so
    /// it passes the CARV Commit rule) cannot commit a mismatched row: Commit re-derives the tie from the verified staged bytes
    /// (before the envelope rules), aborts the operation with <see cref="EnvelopeSchemaTie.MismatchAtCommitReason"/> and accepts
    /// nothing. A replay of the commit stays refused and changes nothing.
    /// </summary>
    [Theory]
    [MemberData(nameof(MismatchedPairs))]
    public async Task S4IsReappliedAtCommitOnTheVerifiedStagedBytesAndAReplayChangesNothing(string kind, string pair)
    {
        var h = await new Harness().CreatedAsync();
        var head = (h.Head.CommittedContentRevision, h.Head.CommittedOperationId, Root: h.Head.CommittedManifestRoot.ToArray());
        var commits = h.Store.Commits.Count;
        var leaves = h.Store.ManifestLeaves.Count;
        var operation = Guid.CreateVersion7();
        var staged = await SeedPreparedWithoutPrepareRulesAsync(h, operation, h.Stage(WithPair(h, kind, pair), operation));
        var request = new CommitContentChangeRequest(h.Org, h.Record, staged);

        var ex = await Assert.ThrowsAsync<AuthorityException>(() => h.Service.CommitAsync(Harness.Actor(h.Owner), operation, request, Guid.CreateVersion7(), CancellationToken.None));
        Assert.Equal(AuthorityErrorCodes.InvalidCommand, ex.ErrorCode);
        Assert.Equal(Mismatch, ex.Message);
        AssertAbortedAndNothingAccepted();

        // Replay of the same commit: refused (the operation is no longer Prepared), still nothing accepted.
        await Assert.ThrowsAsync<AuthorityException>(() => h.Service.CommitAsync(Harness.Actor(h.Owner), operation, request, Guid.CreateVersion7(), CancellationToken.None));
        AssertAbortedAndNothingAccepted();

        void AssertAbortedAndNothingAccepted()
        {
            var aborted = h.Store.Operations.Single(o => o.ContentChangeOperationId == operation);
            Assert.Equal(ContentOperationStates.Aborted, aborted.OperationStateName);
            Assert.Equal(EnvelopeSchemaTie.MismatchAtCommitReason, aborted.OperationQuarantineReasonCode);
            Assert.Null(aborted.CommitAcceptanceRuleVersion);
            Assert.Equal(head.CommittedContentRevision, h.Head.CommittedContentRevision);
            Assert.Equal(head.CommittedOperationId, h.Head.CommittedOperationId);
            Assert.Equal(head.Root, h.Head.CommittedManifestRoot);
            Assert.Equal(commits, h.Store.Commits.Count);
            Assert.Equal(leaves, h.Store.ManifestLeaves.Count);
            Assert.DoesNotContain(h.Store.Commits, c => c.CommittedOperationId == operation);
        }
    }

    /// <summary>At Commit a tied schema-2 / envelope-2 row passes S-4 and is still refused by the unchanged E3-M2a envelope rule.</summary>
    [Theory]
    [InlineData("field")]
    [InlineData("document")]
    public async Task AtCommitATiedSchemaTwoRowIsStillRefusedByTheEnvelopeRules(string kind)
    {
        var h = await new Harness().CreatedAsync();
        var head = h.Head.CommittedContentRevision;
        var operation = Guid.CreateVersion7();
        var staged = await SeedPreparedWithoutPrepareRulesAsync(h, operation, h.Stage(WithPair(h, kind, "schema 2 / envelope 2"), operation));

        var ex = await Assert.ThrowsAsync<AuthorityException>(() => h.Service.CommitAsync(Harness.Actor(h.Owner), operation, new CommitContentChangeRequest(h.Org, h.Record, staged), Guid.CreateVersion7(), CancellationToken.None));
        Assert.Equal("ENVELOPE_INVALID: " + EnvelopeRules.Reasons.SchemaUnsupported, ex.Message);
        Assert.Equal("ENVELOPE_INVALID_AT_COMMIT", h.Store.Operations.Single(o => o.ContentChangeOperationId == operation).OperationQuarantineReasonCode);
        Assert.Equal(head, h.Head.CommittedContentRevision);
    }

    /// <summary>A refused Prepare stores nothing, so a retry under the same idempotency key is evaluated afresh and refused again.</summary>
    [Fact]
    public async Task APrepareReplayOfAMismatchedCandidateIsRefusedAgainAndStoresNothing()
    {
        var h = await new Harness().CreatedAsync();
        var operation = Guid.CreateVersion7();
        var candidate = h.Stage(WithPair(h, "field", "schema 2 / envelope 1"), operation);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var ex = await Assert.ThrowsAsync<AuthorityException>(() => h.PrepareAsync(h.Owner, operation, candidate, withCurrent: true, key: "s4-replay-key"));
            Assert.Equal(Mismatch, ex.Message);
            Assert.DoesNotContain(h.Store.Operations, o => o.ContentChangeOperationId == operation || o.OperationIdempotencyKey == "s4-replay-key");
        }

        Assert.Equal(1, h.Head.CommittedContentRevision);
    }
}
