using System.Text;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.Journal;
using AuthorizationAuthority.Application.Features.Journal.Verification;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Organizations;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// SEC-BL-01 WP-1 (C-155): chain rules R-1..R-7 (<c>docs/security/SEC_BL_01_DR_CONTRACT_CLOSURE.md</c> §4) over entries written by the real
/// <see cref="JournalChain"/>. Tampering is emulated by editing the entries, as a J1/J2 actor would edit rows.
/// </summary>
public sealed class JournalChainRulesTests
{
    private static readonly Guid Org = Guid.Parse("019a5b2c-5ec1-7b01-8000-0000000000c1");
    private static readonly Guid OtherOrg = Guid.Parse("019a5b2c-5ec1-7b01-8000-0000000000c2");

    private sealed record TestBody(string NoteText, long CounterValue);

    /// <summary>A genuine chain of <paramref name="count"/> entries of one stream, appended exactly as the Authority appends them.</summary>
    private static List<SecurityJournalEntry> Chain(int count, string stream = JournalStreamNames.Audit, Guid? organization = null)
    {
        var state = new OrganizationAuthorizationState { OrganizationId = organization ?? Org };
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero));
        var entries = new List<SecurityJournalEntry>();
        for (var revision = 1; revision <= count; revision++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            entries.Add(JournalChain.Append(state, stream, revision, "TestEvent", new TestBody("entry " + revision, revision), null, null, clock));
        }

        return entries;
    }

    private static JournalEntryInspection Inspect(SecurityJournalEntry entry) => JournalChainRules.InspectEntry(JournalEvidence.FromRow(entry));

    private static JournalEntryInspection InspectBytes(string json, long revision = 1, Guid? organization = null, string stream = JournalStreamNames.Audit) =>
        JournalChainRules.InspectEntry(JournalEvidence.FromArchive(new JournalStorageKey(organization ?? Org, stream, revision), Encoding.UTF8.GetBytes(json)));

    /// <summary>Rewrites the body text and recomputes the digest (a consistent J1 forgery of one row).</summary>
    private static void Redigest(SecurityJournalEntry entry, string from, string to)
    {
        Assert.Contains(from, entry.JournalEventBodyJson, StringComparison.Ordinal);
        entry.JournalEventBodyJson = entry.JournalEventBodyJson.Replace(from, to, StringComparison.Ordinal);
        entry.JournalEventDigest = CanonicalJson.Digest(entry.JournalEventBodyJson);
    }

    private static JournalSequenceResult Verify(IEnumerable<SecurityJournalEntry> rows, long first = 1, long? last = null, byte[]? predecessor = null, byte[]? anchor = null, string stream = JournalStreamNames.Audit) =>
        JournalChainRules.VerifySequence(rows.Select(JournalEvidence.FromRow), new JournalSequenceExpectation(Org, stream, first, predecessor, anchor, last), TestContext.Current.CancellationToken);

    private static void AssertFailure(JournalEntryInspection inspection, JournalCheckOutcome outcome, string code)
    {
        Assert.False(inspection.IsValid);
        Assert.Equal((outcome, code), (inspection.Outcome, inspection.FailureCode));
    }

    // ---- R-1 ----

    [Fact]
    public void R1AnAlteredBodyWithItsOriginalDigestIsABodyDigestMismatch()
    {
        var entry = Chain(1)[0];
        entry.JournalEventBodyJson = entry.JournalEventBodyJson.Replace("entry 1", "entry X", StringComparison.Ordinal);
        AssertFailure(Inspect(entry), JournalCheckOutcome.IntegrityFailure, JournalFailureCodes.BodyDigestMismatch);
    }

    [Fact]
    public void R1ADigestOfTheWrongLengthIsABodyDigestMismatch()
    {
        var entry = Chain(1)[0];
        entry.JournalEventDigest = entry.JournalEventDigest[..31];
        AssertFailure(Inspect(entry), JournalCheckOutcome.IntegrityFailure, JournalFailureCodes.BodyDigestMismatch);
    }

    [Fact]
    public void R1IsCheckedBeforeParsingSoAnUnparseableTamperedBodyIsStillADigestMismatch()
    {
        var entry = Chain(1)[0];
        entry.JournalEventBodyJson = "not json";
        AssertFailure(Inspect(entry), JournalCheckOutcome.IntegrityFailure, JournalFailureCodes.BodyDigestMismatch);
    }

    // ---- R-2: unknown versions are never valid; malformed envelopes are distinguished ----

    [Fact]
    public void R2AnUnknownEnvelopeFormatIsUnsupportedNeverValidEvenWhenConsistentlyRedigested()
    {
        var entry = Chain(1)[0];
        Redigest(entry, "DynamicForms/SecurityJournal/v1", "DynamicForms/SecurityJournal/v2");
        AssertFailure(Inspect(entry), JournalCheckOutcome.Unsupported, JournalFailureCodes.EnvelopeUnsupported);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    [InlineData("{}")]
    [InlineData("{\"envelope_format\":5}")]
    public void R2BytesThatAreNotAV1EnvelopeObjectAreMalformed(string json)
    {
        AssertFailure(InspectBytes(json), JournalCheckOutcome.Malformed, JournalFailureCodes.EnvelopeMalformed);
    }

    [Theory]
    [InlineData("\"stream_revision\":1,", "\"stream_revision\":1,\"stream_revision\":1,")] // duplicate member
    [InlineData(",\"body\":", ",\"extra_member\":1,\"body\":")] // unknown member
    [InlineData("\"event_kind\":\"TestEvent\",", "")] // missing member
    [InlineData("\"stream_revision\":1,", "\"stream_revision\":\"1\",")] // revision not a number
    [InlineData("\"stream_revision\":1,", "\"stream_revision\":1.5,")] // revision not an integer
    [InlineData("\"previous_digest_hex\":null", "\"previous_digest_hex\":7")] // previous digest wrong type
    [InlineData("\"previous_digest_hex\":null", "\"previous_digest_hex\":\"ABCD\"")] // previous digest not 64 lower-case hex
    [InlineData("019a5b2c-5ec1-7b01-8000-0000000000c1", "019A5B2C-5EC1-7B01-8000-0000000000C1")] // non-canonical identifier
    [InlineData("\"created_on_utc\":\"", "\"created_on_utc\":\"x")] // not a timestamp
    public void R2StructurallyInvalidV1EnvelopesAreMalformed(string from, string to)
    {
        var json = Chain(1)[0].JournalEventBodyJson;
        Assert.Contains(from, json, StringComparison.Ordinal);
        AssertFailure(InspectBytes(json.Replace(from, to, StringComparison.Ordinal)), JournalCheckOutcome.Malformed, JournalFailureCodes.EnvelopeMalformed);
    }

    [Fact]
    public void R2TrailingBytesAfterTheEnvelopeAreMalformed()
    {
        AssertFailure(InspectBytes(Chain(1)[0].JournalEventBodyJson + " {}"), JournalCheckOutcome.Malformed, JournalFailureCodes.EnvelopeMalformed);
    }

    [Fact]
    public void R2ABodyThatIsNotAnObjectIsMalformed()
    {
        var json = Chain(1)[0].JournalEventBodyJson;
        var body = json.IndexOf(",\"body\":", StringComparison.Ordinal);
        AssertFailure(InspectBytes(json[..body] + ",\"body\":[1]}"), JournalCheckOutcome.Malformed, JournalFailureCodes.EnvelopeMalformed);
    }

    // ---- R-3, R-4 ----

    [Fact]
    public void R3ArchivedBytesUnderAnotherRevisionOrganizationOrStreamKeyAreAColumnMismatch()
    {
        var json = Chain(1)[0].JournalEventBodyJson;
        AssertFailure(InspectBytes(json, revision: 2), JournalCheckOutcome.IntegrityFailure, JournalFailureCodes.EnvelopeColumnMismatch);
        AssertFailure(InspectBytes(json, organization: OtherOrg), JournalCheckOutcome.IntegrityFailure, JournalFailureCodes.EnvelopeColumnMismatch);
        AssertFailure(InspectBytes(json, stream: JournalStreamNames.Authorization), JournalCheckOutcome.IntegrityFailure, JournalFailureCodes.EnvelopeColumnMismatch);
    }

    [Fact]
    public void R3ARowWhoseColumnsDifferFromItsEnvelopeIsAColumnMismatch()
    {
        foreach (var edit in new Action<SecurityJournalEntry>[]
        {
            e => e.JournalEventKind = "OtherEvent",
            e => e.JournalStreamRevision = 9,
            e => e.OrganizationId = OtherOrg,
            e => e.JournalStreamName = JournalStreamNames.Content,
        })
        {
            var entry = Chain(1)[0];
            edit(entry);
            AssertFailure(Inspect(entry), JournalCheckOutcome.IntegrityFailure, JournalFailureCodes.EnvelopeColumnMismatch);
        }
    }

    [Fact]
    public void R4APreviousDigestColumnThatDiffersFromTheEnvelopeIsAColumnMismatch()
    {
        var entries = Chain(2);
        entries[1].JournalPreviousDigest = new byte[32];
        AssertFailure(Inspect(entries[1]), JournalCheckOutcome.IntegrityFailure, JournalFailureCodes.EnvelopeColumnMismatch);
        entries[1].JournalPreviousDigest = null;
        AssertFailure(Inspect(entries[1]), JournalCheckOutcome.IntegrityFailure, JournalFailureCodes.EnvelopeColumnMismatch);
    }

    // ---- R-5 ----

    [Fact]
    public void R5AValidGenesisHasNoPreviousDigest()
    {
        var genesis = Inspect(Chain(1)[0]);
        Assert.True(genesis.IsValid);
        Assert.Null(genesis.Header!.PreviousDigest);
    }

    [Fact]
    public void R5AGenesisCarryingAPreviousDigestIsAGenesisViolation()
    {
        var entry = Chain(1)[0];
        var hex = new string('a', 64);
        Redigest(entry, "\"previous_digest_hex\":null", "\"previous_digest_hex\":\"" + hex + "\"");
        entry.JournalPreviousDigest = Convert.FromHexString(hex);
        AssertFailure(Inspect(entry), JournalCheckOutcome.IntegrityFailure, JournalFailureCodes.GenesisViolation);
    }

    [Fact]
    public void R5ALaterRevisionWithoutAPreviousDigestIsAGenesisViolation()
    {
        var entries = Chain(2);
        var previousHex = Convert.ToHexStringLower(entries[1].JournalPreviousDigest!);
        Redigest(entries[1], "\"previous_digest_hex\":\"" + previousHex + "\"", "\"previous_digest_hex\":null");
        entries[1].JournalPreviousDigest = null;
        AssertFailure(Inspect(entries[1]), JournalCheckOutcome.IntegrityFailure, JournalFailureCodes.GenesisViolation);
    }

    [Fact]
    public void R5ARevisionBelowOneIsAGenesisViolation()
    {
        var entry = Chain(1)[0];
        Redigest(entry, "\"stream_revision\":1,", "\"stream_revision\":0,");
        entry.JournalStreamRevision = 0;
        AssertFailure(Inspect(entry), JournalCheckOutcome.IntegrityFailure, JournalFailureCodes.GenesisViolation);
    }

    // ---- R-6 ----

    [Fact]
    public void R6AGenuineChainInAnyInputOrderIsValid()
    {
        var entries = Chain(5);
        var result = Verify([entries[3], entries[0], entries[4], entries[2], entries[1]], last: 5);
        Assert.True(result.IsValid);
        Assert.Equal(5, result.LastContiguousValidRevision);
        Assert.Equal(entries[4].JournalEventDigest, result.LastContiguousValidDigest);
    }

    [Fact]
    public void R6AMissingRevisionIsAGapAndStopsTheContiguousPrefix()
    {
        var entries = Chain(5);
        entries.RemoveAt(2);
        var result = Verify(entries, last: 5);
        Assert.Equal(JournalCheckOutcome.IntegrityFailure, result.Outcome);
        Assert.Contains(result.Findings, f => f.Code == JournalFailureCodes.RevisionGap && f.StreamRevision == 3);
        Assert.Equal(2, result.LastContiguousValidRevision);
    }

    [Fact]
    public void R6AMissingTailIsAGapWhenTheExpectedLastRevisionIsKnown()
    {
        var entries = Chain(5);
        var result = Verify(entries.Take(3), last: 5);
        Assert.Contains(result.Findings, f => f.Code == JournalFailureCodes.RevisionGap && f.StreamRevision == 4);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void R6AMissingFirstRevisionIsAGap()
    {
        var result = Verify(Chain(3).Skip(1), last: 3);
        Assert.Contains(result.Findings, f => f.Code == JournalFailureCodes.RevisionGap && f.StreamRevision == 1);
        Assert.Null(result.LastContiguousValidRevision);
    }

    [Fact]
    public void R6ADuplicateRevisionIsReported()
    {
        var entries = Chain(3);
        var copy = Chain(3)[1];
        var result = Verify([.. entries, copy], last: 3);
        Assert.Contains(result.Findings, f => f.Code == JournalFailureCodes.DuplicateRevision && f.StreamRevision == 2);
        Assert.Equal(JournalCheckOutcome.IntegrityFailure, result.Outcome);
    }

    [Fact]
    public void R6EntriesOfAnotherOrganizationOrStreamInARunAreReported()
    {
        var mixed = Chain(2).Concat(Chain(1, organization: OtherOrg)).Concat(Chain(1, JournalStreamNames.Content));
        var result = Verify(mixed, last: 2);
        Assert.Equal(2, result.Findings.Count(f => f.Code == JournalFailureCodes.EnvelopeColumnMismatch));
        Assert.Equal(2, result.LastContiguousValidRevision);
        Assert.False(result.IsValid);
    }

    // ---- R-7 ----

    [Fact]
    public void R7AnIncorrectPredecessorIsALinkMismatch()
    {
        var entries = Chain(3);
        Redigest(entries[1], "entry 2", "entry Z"); // consistent forgery of row 2 that does not re-chain row 3
        var result = Verify(entries, last: 3);
        Assert.Contains(result.Findings, f => f.Code == JournalFailureCodes.LinkMismatch && f.StreamRevision == 3);
        Assert.Equal(2, result.LastContiguousValidRevision);

        var link = JournalChainRules.CheckLink(Inspect(entries[2]), Inspect(entries[1]));
        Assert.Equal((JournalCheckOutcome.IntegrityFailure, JournalFailureCodes.LinkMismatch), (link.Outcome, link.FailureCode));
    }

    [Fact]
    public void R7APredecessorFromAnotherStreamOrNotImmediatelyPrecedingIsRefused()
    {
        var audit = Chain(3);
        var content = Chain(2, JournalStreamNames.Content);
        Assert.Equal(JournalFailureCodes.EnvelopeColumnMismatch, JournalChainRules.CheckLink(Inspect(audit[1]), Inspect(content[0])).FailureCode);
        Assert.Equal(JournalFailureCodes.RevisionGap, JournalChainRules.CheckLink(Inspect(audit[2]), Inspect(audit[0])).FailureCode);
        Assert.True(JournalChainRules.CheckLink(Inspect(audit[2]), Inspect(audit[1])).IsValid);
    }

    [Fact]
    public void R7ARunStartingAboveOneLinksToATrustedPredecessorDigest()
    {
        var entries = Chain(4);
        Assert.True(Verify(entries.Skip(2), first: 3, last: 4, predecessor: entries[1].JournalEventDigest).IsValid);
        var wrong = Verify(entries.Skip(2), first: 3, last: 4, predecessor: entries[0].JournalEventDigest);
        Assert.Contains(wrong.Findings, f => f.Code == JournalFailureCodes.LinkMismatch && f.StreamRevision == 3);
    }

    [Fact]
    public void AConsistentlyRewrittenChainIsInternallyValidAndOnlyATrustedAnchorDetectsIt()
    {
        // Unkeyed SHA-256: a J1 actor who rewrites row 1 and re-chains every later row produces a chain that passes R-1..R-7 (contract §2.1).
        var genuine = Chain(3);
        var state = new OrganizationAuthorizationState { OrganizationId = Org };
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero));
        var forged = Enumerable.Range(1, 3).Select(r =>
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            return JournalChain.Append(state, JournalStreamNames.Audit, r, "TestEvent", new TestBody(r == 1 ? "forged" : "entry " + r, r), null, null, clock);
        }).ToList();

        Assert.True(Verify(forged, last: 3).IsValid);
        var anchored = Verify(forged, last: 3, anchor: genuine[0].JournalEventDigest);
        Assert.Contains(anchored.Findings, f => f.Code == JournalFailureCodes.AnchorMismatch && f.StreamRevision == 1);
        Assert.False(anchored.IsValid);
    }

    // ---- Outcome classes, precedence, cancellation ----

    [Fact]
    public void UnavailableEvidenceIsNeverValidAndIsDistinctFromAnIntegrityFailure()
    {
        var entries = Chain(3);
        var evidence = new[] { JournalEvidence.FromRow(entries[0]), JournalEvidence.Unavailable(new JournalStorageKey(Org, JournalStreamNames.Audit, 2)), JournalEvidence.FromRow(entries[2]) };
        var result = JournalChainRules.VerifySequence(evidence, new JournalSequenceExpectation(Org, JournalStreamNames.Audit, 1, LastRevision: 3), TestContext.Current.CancellationToken);
        Assert.Equal(JournalCheckOutcome.Unavailable, result.Outcome);
        Assert.Contains(result.Findings, f => f.Code == JournalFailureCodes.EvidenceUnavailable && f.StreamRevision == 2);
        Assert.Equal(1, result.LastContiguousValidRevision);

        var single = JournalChainRules.InspectEntry(JournalEvidence.Unavailable(new JournalStorageKey(Org, JournalStreamNames.Audit, 1)));
        AssertFailure(single, JournalCheckOutcome.Unavailable, JournalFailureCodes.EvidenceUnavailable);
    }

    [Fact]
    public void AnIntegrityFailureDominatesMalformedUnavailableAndUnsupportedOutcomes()
    {
        Assert.Equal(JournalCheckOutcome.IntegrityFailure, JournalChainRules.Worst(JournalCheckOutcome.Unavailable, JournalCheckOutcome.IntegrityFailure));
        Assert.Equal(JournalCheckOutcome.Malformed, JournalChainRules.Worst(JournalCheckOutcome.Unsupported, JournalCheckOutcome.Malformed));
        Assert.Equal(JournalCheckOutcome.Unavailable, JournalChainRules.Worst(JournalCheckOutcome.Unavailable, JournalCheckOutcome.Unsupported));
        Assert.Equal(JournalCheckOutcome.Unsupported, JournalChainRules.Worst(JournalCheckOutcome.Valid, JournalCheckOutcome.Unsupported));

        var entries = Chain(3);
        entries[0].JournalEventBodyJson += " ";
        var evidence = entries.Select(JournalEvidence.FromRow).Append(JournalEvidence.Unavailable(new JournalStorageKey(Org, JournalStreamNames.Audit, 4)));
        Assert.Equal(JournalCheckOutcome.IntegrityFailure, JournalChainRules.VerifySequence(evidence, new JournalSequenceExpectation(Org, JournalStreamNames.Audit, 1), TestContext.Current.CancellationToken).Outcome);
    }

    [Fact]
    public void FindingsAreBoundedAndTruncationIsReported()
    {
        var entries = Chain(JournalChainRules.MaximumReportedFindings + 10);
        foreach (var entry in entries)
        {
            entry.JournalEventDigest = new byte[32];
        }

        var result = Verify(entries);
        Assert.Equal(JournalChainRules.MaximumReportedFindings, result.Findings.Count);
        Assert.True(result.FindingsTruncated);
    }

    [Fact]
    public void ACancelledVerificationStops()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            JournalChainRules.VerifySequence(Chain(2).Select(JournalEvidence.FromRow), new JournalSequenceExpectation(Org, JournalStreamNames.Audit, 1), cancelled.Token));
    }

    [Fact]
    public void InspectingDoesNotChangeTheRow()
    {
        var entry = Chain(1)[0];
        var before = (entry.JournalEventBodyJson, Convert.ToHexString(entry.JournalEventDigest), entry.ArchiveDeliveryState);
        _ = Inspect(entry);
        Assert.Equal(before, (entry.JournalEventBodyJson, Convert.ToHexString(entry.JournalEventDigest), entry.ArchiveDeliveryState));
    }
}
