using System.Text;
using System.Text.Json;
using AuthorizationAuthority.Application.Features.Journal;
using AuthorizationAuthority.Application.Features.Journal.Verification;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Organizations;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// SEC-BL-01 WP-1 (C-155): known-answer vectors for the security-journal canonical envelope. The vectors in
/// <c>trust_authority/journal-envelope-v1.test-vectors.json</c> are produced by an independent Python implementation
/// (<c>scripts/integrity/generate_journal_envelope_vectors.py</c>); the real <see cref="JournalChain.Append"/> must reproduce every one byte
/// for byte, and the chain rules must accept them as rows, as archived bytes and as a chain.
/// </summary>
public sealed class JournalEnvelopeKnownAnswerTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Mirrors the generator's body (snake_case through CanonicalJson).</summary>
    private sealed record KatBody(string NoteText, long CounterValue, bool FlagValue, string? NullableValue);

    private sealed record Vector(string Id, Guid Organization, string Stream, long Revision, string Kind, string? PreviousHex, long Ticks, KatBody Body, string Expected, string ExpectedDigestHex);

    private static IReadOnlyList<Vector> Vectors { get; } = Load();

    private static IReadOnlyList<Vector> Load()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "EnterpriseForms.All.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root.FullName, "trust_authority", "journal-envelope-v1.test-vectors.json")));
        Assert.Equal(JournalEnvelopeFormats.SecurityJournalV1, document.RootElement.GetProperty("format").GetString());
        return [.. document.RootElement.GetProperty("vectors").EnumerateArray().Select(v =>
        {
            var b = v.GetProperty("body");
            return new Vector(v.GetProperty("id").GetString()!, v.GetProperty("organization_id").GetGuid(), v.GetProperty("stream_name").GetString()!, v.GetProperty("stream_revision").GetInt64(),
                v.GetProperty("event_kind").GetString()!, v.GetProperty("previous_digest_hex").GetString(), v.GetProperty("created_ticks_after_2026_11_17_12_00_utc").GetInt64(),
                new KatBody(b.GetProperty("note_text").GetString()!, b.GetProperty("counter_value").GetInt64(), b.GetProperty("flag_value").GetBoolean(), b.GetProperty("nullable_value").GetString()),
                v.GetProperty("expected_canonical_utf8").GetString()!, v.GetProperty("expected_sha256_hex").GetString()!);
        })];
    }

    private static SecurityJournalEntry Append(Vector v)
    {
        var previous = v.PreviousHex is null ? null : Convert.FromHexString(v.PreviousHex);
        var state = new OrganizationAuthorizationState { OrganizationId = v.Organization };
        switch (v.Stream)
        {
            case JournalStreamNames.Audit:
                state.LatestAuditJournalDigest = previous;
                break;
            case JournalStreamNames.Authorization:
                state.LatestSecurityJournalDigest = previous;
                break;
            default:
                state.LatestContentJournalDigest = previous;
                break;
        }

        return JournalChain.Append(state, v.Stream, v.Revision, v.Kind, v.Body, null, null, new ManualClock(Noon.AddTicks(v.Ticks)));
    }

    [Fact]
    public void TheVectorFileCoversGenesisLinksStreamsAndOrganizations()
    {
        Assert.Equal(5, Vectors.Count);
        Assert.Contains(Vectors, v => v.Revision == 1 && v.PreviousHex is null);
        Assert.Contains(Vectors, v => v.Revision > 1 && v.PreviousHex is not null);
        Assert.Equal(2, Vectors.Select(v => v.Stream).Distinct().Count());
        Assert.Equal(2, Vectors.Select(v => v.Organization).Distinct().Count());
    }

    [Fact]
    public void JournalChainReproducesEveryVectorByteForByte()
    {
        foreach (var vector in Vectors)
        {
            var entry = Append(vector);
            Assert.Equal(vector.Expected, entry.JournalEventBodyJson);
            Assert.Equal(vector.ExpectedDigestHex, Convert.ToHexStringLower(entry.JournalEventDigest));
            Assert.Equal(vector.PreviousHex, entry.JournalPreviousDigest is null ? null : Convert.ToHexStringLower(entry.JournalPreviousDigest));
            Assert.Equal(vector.ExpectedDigestHex, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(vector.Expected))));
        }
    }

    [Fact]
    public void EveryVectorSatisfiesR1ToR5AsARowAndAsArchivedBytes()
    {
        foreach (var vector in Vectors)
        {
            var entry = Append(vector);
            var asRow = JournalChainRules.InspectEntry(JournalEvidence.FromRow(entry));
            Assert.True(asRow.IsValid, vector.Id + ": " + asRow.FailureDetail);
            Assert.Equal(JournalEnvelopeFormats.SecurityJournalV1, asRow.Header!.EnvelopeFormat);
            Assert.Equal(Noon.AddTicks(vector.Ticks), asRow.Header.CreatedOnUtc);

            var key = new JournalStorageKey(vector.Organization, vector.Stream, vector.Revision);
            var asArchived = JournalChainRules.InspectEntry(JournalEvidence.FromArchive(key, Encoding.UTF8.GetBytes(vector.Expected)));
            Assert.True(asArchived.IsValid, vector.Id + ": " + asArchived.FailureDetail);
            Assert.Equal(vector.ExpectedDigestHex, Convert.ToHexStringLower(asArchived.BodyDigest!));
        }
    }

    [Fact]
    public void TheAuditVectorsFormAValidChainUnderR6AndR7()
    {
        var audit = Vectors.Where(v => v.Stream == JournalStreamNames.Audit && v.Organization == Vectors[0].Organization).OrderBy(v => v.Revision).ToList();
        Assert.Equal(3, audit.Count);
        var result = JournalChainRules.VerifySequence(audit.Select(v => JournalEvidence.FromRow(Append(v))),
            new JournalSequenceExpectation(audit[0].Organization, JournalStreamNames.Audit, 1, LastRevision: 3), TestContext.Current.CancellationToken);

        Assert.True(result.IsValid, string.Join("; ", result.Findings.Select(f => f.Code)));
        Assert.Equal(3, result.LastContiguousValidRevision);
        Assert.Equal(audit[2].ExpectedDigestHex, Convert.ToHexStringLower(result.LastContiguousValidDigest!));
    }

    [Fact]
    public void TheSameBodyForAnotherOrganizationIsADifferentEntryAndCannotBeTransplanted()
    {
        var a = Vectors.Single(v => v.Id == "JEV1-AUDIT-GENESIS");
        var b = Vectors.Single(v => v.Id == "JEV1-OTHER-ORGANIZATION-GENESIS");
        Assert.NotEqual(a.ExpectedDigestHex, b.ExpectedDigestHex);

        // Organization B's archived bytes placed under organization A's key fail R-3.
        var transplanted = JournalChainRules.InspectEntry(JournalEvidence.FromArchive(new JournalStorageKey(a.Organization, a.Stream, a.Revision), Encoding.UTF8.GetBytes(b.Expected)));
        Assert.Equal((JournalCheckOutcome.IntegrityFailure, JournalFailureCodes.EnvelopeColumnMismatch), (transplanted.Outcome, transplanted.FailureCode));
    }
}
