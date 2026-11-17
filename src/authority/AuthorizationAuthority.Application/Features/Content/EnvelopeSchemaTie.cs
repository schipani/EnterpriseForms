using System.Buffers.Binary;
using EnterpriseForms.Integrity.Canonical;

namespace AuthorizationAuthority.Application.Features.Content;

/// <summary>
/// E3-M2b-2 R-M2b2-11 (contract §4.4 S-4, §23.14; owner amendment of D-3): the Authority tie between the signed schema version
/// of an envelope row and its <c>encryption_envelope_version</c>. Only the closed pairs schema 1 / envelope 1 and schema 2 /
/// envelope 2 pass; every other pair of an envelope kind (including a missing, NULL or non-Int32 envelope version, which
/// cannot prove the tie) is refused with <see cref="MismatchReason"/>.
/// <para>
/// Deliberately separate from <see cref="EnvelopeRules"/> and called at its own sites (Prepare, before the envelope rules;
/// Commit, on the verified staged bytes; the protection census), so that a schema-2 / envelope-1 or schema-1 / envelope-2 row
/// is refused even if the E3-M2a schema refusal or the envelope-version allow-list were weakened. It does not accept anything:
/// a passing schema-2 / envelope-2 row is still refused by <see cref="EnvelopeRules"/> (Envelope v2 is not authorized).
/// Decided from the canonical bytes alone, so Commit re-derives it and the content acceptance rule version is unchanged.
/// </para>
/// </summary>
public static class EnvelopeSchemaTie
{
    /// <summary>The contract-defined reason code (stable, identifier-free).</summary>
    public const string MismatchReason = "ENVELOPE_SCHEMA_MISMATCH";

    /// <summary>Abort reason recorded on an operation whose verified staged bytes fail the tie at commit.</summary>
    public const string MismatchAtCommitReason = "ENVELOPE_SCHEMA_MISMATCH_AT_COMMIT";

    /// <summary>The only (signed schema version, envelope version) pairs the tie admits.</summary>
    public static readonly IReadOnlySet<(int SchemaVersion, int EnvelopeVersion)> TiedPairs = new HashSet<(int, int)> { (1, 1), (2, 2) };

    private const string EnvelopeVersionColumn = "encryption_envelope_version";

    /// <summary>Null when the row satisfies the tie or carries no envelope; otherwise <see cref="MismatchReason"/>.</summary>
    public static string? Violation(RowCanonicalDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Header.EntityKind is not (EnvelopeRules.FieldEntityKind or EnvelopeRules.DocumentEntityKind))
        {
            return null;
        }

        var column = document.Column(EnvelopeVersionColumn);
        if (column is null || column.Type != RowColumnType.Int32 || column.FramedValue.Length != 5)
        {
            return MismatchReason; // the tie cannot be proven: fail closed
        }

        var envelopeVersion = BinaryPrimitives.ReadInt32BigEndian(column.FramedValue.Span[1..]);
        return TiedPairs.Contains((document.Header.SchemaVersion, envelopeVersion)) ? null : MismatchReason;
    }

    /// <summary>Every envelope row of a candidate (Prepare) or of the verified staged rows (Commit). Throws a 422 invalid-command error; nothing has been signed when it is thrown at Prepare.</summary>
    public static void Enforce(IEnumerable<RowCanonicalDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        foreach (var document in documents)
        {
            if (Violation(document) is { } reason)
            {
                throw EnvelopeRules.Refused(reason);
            }
        }
    }
}
