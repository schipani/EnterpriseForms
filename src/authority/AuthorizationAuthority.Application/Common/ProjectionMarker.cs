using System.Globalization;
using AuthorizationAuthority.Domain.Features.Organizations;

namespace AuthorizationAuthority.Application.Common;

/// <summary>
/// C8 M4 S1 (Design B §2.3, B-Q5): the SpiceDB projection marker names the authorization revision it represents and the
/// Authority's hash-chained authorization-stream journal digest of that revision:
/// <c>organization:{org}#projection_cursor@projection_step:step-{org}-{n}-{h}</c>, where <c>h</c> is the 64 lowercase hex
/// characters of the digest (64 zeros for revision 0). The projector moves it in the same WriteRelationships call as every
/// delta, so a matching marker means SpiceDB holds this history's projection of revision <c>n</c>; a restored, replaced or
/// foreign datastore carries an older, missing or different marker. The ZedToken is never integrity evidence.
/// </summary>
public static class ProjectionMarker
{
    public const string ZeroDigestHex = "0000000000000000000000000000000000000000000000000000000000000000";

    /// <summary>The v2 marker subject id of revision <paramref name="revision"/>; <paramref name="journalDigest"/> is null only for revision 0.</summary>
    public static string SubjectId(Guid organizationId, long revision, byte[]? journalDigest)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(revision);
        if (journalDigest is null && revision != 0)
        {
            throw new ArgumentException("only revision 0 has no journal digest", nameof(journalDigest));
        }

        if (journalDigest is not null && journalDigest.Length != 32)
        {
            throw new ArgumentException("journal digests are 32 bytes", nameof(journalDigest));
        }

        var digest = journalDigest is null ? ZeroDigestHex : Convert.ToHexStringLower(journalDigest);
        return Prefix(organizationId) + revision.ToString(CultureInfo.InvariantCulture) + "-" + digest;
    }

    /// <summary>
    /// The marker a decision must observe: the gate requires <c>A = P</c>, so the latest authorization-stream digest on the
    /// organization row is the digest of revision <c>P</c> (one entry per revision, written by <c>SecurityCommandService</c>).
    /// </summary>
    public static string ExpectedFor(OrganizationAuthorizationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.AuthorizationRevision != state.ProjectionAppliedRevision)
        {
            throw new InvalidOperationException("the expected marker is defined only while the projection is current");
        }

        return SubjectId(state.OrganizationId, state.ProjectionAppliedRevision, state.ProjectionAppliedRevision == 0 ? null : state.LatestSecurityJournalDigest);
    }

    /// <summary>Parses a marker subject id of the organization: v2 (<c>step-{org}-{n}-{h}</c>) or the pre-S1 v1 form (<c>step-{org}-{n}</c>).</summary>
    public static bool TryParse(Guid organizationId, string subjectId, out long revision, out string? digestHex)
    {
        revision = -1;
        digestHex = null;
        var prefix = Prefix(organizationId);
        if (string.IsNullOrEmpty(subjectId) || !subjectId.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = subjectId[prefix.Length..];
        var dash = rest.IndexOf('-', StringComparison.Ordinal);
        var number = dash < 0 ? rest : rest[..dash];
        if (number.Length == 0 || (number.Length > 1 && number[0] == '0') || !long.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out revision))
        {
            revision = -1;
            return false;
        }

        if (dash < 0)
        {
            return true; // v1
        }

        var digest = rest[(dash + 1)..];
        if (digest.Length != 64 || !digest.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')))
        {
            revision = -1;
            return false;
        }

        digestHex = digest;
        return true;
    }

    /// <summary>
    /// Null when the observation is exactly the expected marker; otherwise the quarantine reason it would justify
    /// (Design B §2.5): missing, invalid (several, malformed or legacy v1 at the expected revision), behind, ahead, or
    /// foreign history (the expected revision with a different digest).
    /// </summary>
    public static string? Classify(Guid organizationId, long expectedRevision, string expectedSubjectId, IReadOnlyList<string> observed)
    {
        ArgumentNullException.ThrowIfNull(observed);
        if (observed.Count == 0)
        {
            return ProjectionIntegrityReasons.MarkerMissing;
        }

        if (observed.Count > 1 || !TryParse(organizationId, observed[0], out var revision, out var digest))
        {
            return ProjectionIntegrityReasons.MarkerInvalid;
        }

        if (string.Equals(observed[0], expectedSubjectId, StringComparison.Ordinal))
        {
            return null;
        }

        if (revision < expectedRevision)
        {
            return ProjectionIntegrityReasons.MarkerBehind;
        }

        if (revision > expectedRevision)
        {
            return ProjectionIntegrityReasons.MarkerAhead;
        }

        return digest is null ? ProjectionIntegrityReasons.MarkerInvalid : ProjectionIntegrityReasons.ForeignHistory;
    }

    private static string Prefix(Guid organizationId) => "step-" + organizationId.ToString("D") + "-";
}

/// <summary>Integrity-quarantine reason codes set by S1 (persisted in <c>integrity_quarantine_reason_code</c>, at most 100 characters).</summary>
public static class ProjectionIntegrityReasons
{
    public const string MarkerBehind = "PROJECTION_MARKER_BEHIND";
    public const string MarkerAhead = "PROJECTION_MARKER_AHEAD";
    public const string MarkerMissing = "PROJECTION_MARKER_MISSING";
    public const string MarkerInvalid = "PROJECTION_MARKER_INVALID";
    public const string ForeignHistory = "PROJECTION_FOREIGN_HISTORY";
    public const string SchemaMissing = "PROJECTION_SCHEMA_MISSING";
}
