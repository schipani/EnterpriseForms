using System.Text.Json;
using System.Text.Json.Serialization;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Features.Journal.Reconciliation;

namespace AuthorizationAuthority.Application.Features.Journal;

/// <summary>
/// SEC-BL-01 WP-5a.2 (contract amendment A-3, SM-1/SM-2): the durable D+ stop marker, a create-only archive object at
/// <c>meta/dplus-stops/{organization:D}/{stream}/{revision:D12}.json</c>. It names the failed revision, the exact D+ classification and the
/// deterministic identity of the matching finding. It carries no event body or digest of a body. Any valid marker stops the stream until an
/// authorized recovery action (not implemented); D+ never deletes, rewrites or supersedes one.
/// </summary>
/// <remarks>
/// G14-R2 (amendment A-4, §24, stop generations): a stop raised after an executed recovery carries its generation <c>g ≥ 1</c>, uses schema
/// v2 (v1 plus <c>stop_generation</c>) and the key <c>{revision:D12}.r{g:D4}.json</c>, so it never collides with the resolved stop. Generation 0
/// keeps the A-3 key and the byte-identical v1 content.
/// </remarks>
public sealed record DPlusStreamStop(string MarkerSchema, Guid OrganizationId, string StreamName, long FailedRevision, string FindingCode, string? Subtype, string FindingIdentity,
    Guid SecurityJournalEntryId, string ArchiveInstanceId, DateTimeOffset DetectedAtUtc, string ToolVersion,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int StopGeneration = 0)
{
    public const string SchemaV1 = "DynamicForms/DPlusStreamStop/v1";

    public const string SchemaV2 = "DynamicForms/DPlusStreamStop/v2";

    public static string SchemaFor(int stopGeneration) => stopGeneration == 0 ? SchemaV1 : SchemaV2;
}

public static class DPlusStreamStops
{
    private static readonly JsonSerializerOptions Strict = new(CanonicalJson.Options) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    /// <summary>The A-3 stop-marker prefix (identical in the Blob and in-memory adapters).</summary>
    public const string MarkerPrefix = "meta/dplus-stops/";

    /// <summary>
    /// The D+ classifications that stop a stream durably (A-3 SM-4), plus the G14-R2 additions (A-4 §24): an evidence change after the W2-S
    /// commitment, a recovery-evidence mismatch, and an archive-instance substitution or contradictory instance anchor. Other archive-level
    /// refusals (<c>ARCHIVE_REGRESSION</c> sentinel and container subtypes, an unpinned instance) do not (DD-11).
    /// </summary>
    public static bool StopsStream(string findingCode, string? subtype) => findingCode switch
    {
        JournalFindingCodes.PredecessorMissing or JournalFindingCodes.DeliveryLinkMismatch or JournalFindingCodes.ChainIntegrityFailure => true,
        JournalFindingCodes.DPlusEvidenceChangedAfterCommitment or JournalFindingCodes.DPlusRecoveryEvidenceMismatch => true,
        JournalFindingCodes.ArchiveRegression => subtype is JournalFindingCodes.InstanceChangedSubtype or JournalFindingCodes.InstanceAnchorContradictorySubtype,
        _ => false,
    };

    /// <summary>The marker object name of a stop of <paramref name="stopGeneration"/> (A-3 SM-1; A-4 §24 generations).</summary>
    public static string MarkerObjectName(Guid organizationId, string streamName, long failedRevision, int stopGeneration) =>
        MarkerPrefix + organizationId.ToString("D") + "/" + streamName + "/" + failedRevision.ToString("D12", System.Globalization.CultureInfo.InvariantCulture)
        + (stopGeneration == 0 ? string.Empty : ".r" + stopGeneration.ToString("D4", System.Globalization.CultureInfo.InvariantCulture)) + ".json";

    /// <summary>
    /// The revision and generation named by a marker file name: <c>{revision:D12}.json</c> (generation 0) or <c>{revision:D12}.r{g:D4}.json</c>
    /// (<c>1 ≤ g ≤ 9999</c>). Anything else is not a canonical marker name.
    /// </summary>
    public static (long Revision, int Generation)? ParseMarkerFileName(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        if ((fileName.Length != 17 && fileName.Length != 23) || !fileName.EndsWith(".json", StringComparison.Ordinal) || fileName.AsSpan(0, 12).ContainsAnyExceptInRange('0', '9'))
        {
            return null;
        }

        var revision = long.Parse(fileName.AsSpan(0, 12), System.Globalization.CultureInfo.InvariantCulture);
        if (revision < 1)
        {
            return null;
        }

        if (fileName.Length == 17)
        {
            return (revision, 0);
        }

        if (fileName[12] != '.' || fileName[13] != 'r' || fileName.AsSpan(14, 4).ContainsAnyExceptInRange('0', '9'))
        {
            return null;
        }

        var generation = int.Parse(fileName.AsSpan(14, 4), System.Globalization.CultureInfo.InvariantCulture);
        return generation >= 1 ? (revision, generation) : null;
    }

    /// <summary>The marker name's revision and generation for <paramref name="objectName"/> under this (organization, stream), or null.</summary>
    public static (long Revision, int Generation)? ParseMarkerObjectName(string objectName, Guid organizationId, string streamName)
    {
        ArgumentNullException.ThrowIfNull(objectName);
        var prefix = MarkerPrefix + organizationId.ToString("D") + "/" + streamName + "/";
        return objectName.StartsWith(prefix, StringComparison.Ordinal) && !objectName.AsSpan(prefix.Length).Contains('/') ? ParseMarkerFileName(objectName[prefix.Length..]) : null;
    }

    public static byte[] Serialize(DPlusStreamStop marker) => CanonicalJson.SerializeBytes(marker);

    /// <summary>
    /// The marker, or null when the bytes are not a canonical, valid marker for <paramref name="organizationId"/>, <paramref name="streamName"/>,
    /// <paramref name="revision"/> and <paramref name="stopGeneration"/> (v1 for generation 0, v2 for generation ≥ 1; A-4 §24).
    /// </summary>
    public static DPlusStreamStop? TryParse(ReadOnlySpan<byte> bytes, Guid organizationId, string streamName, long? revision, int stopGeneration = 0)
    {
        try
        {
            var marker = JsonSerializer.Deserialize<DPlusStreamStop>(bytes, Strict);
            return marker is not null && string.Equals(marker.MarkerSchema, DPlusStreamStop.SchemaFor(stopGeneration), StringComparison.Ordinal) && marker.StopGeneration == stopGeneration
                && marker.OrganizationId == organizationId
                && string.Equals(marker.StreamName, streamName, StringComparison.Ordinal) && revision is { } r && marker.FailedRevision == r && r >= 1 && StopsStream(marker.FindingCode, marker.Subtype)
                && !string.IsNullOrEmpty(marker.FindingIdentity) && !string.IsNullOrEmpty(marker.ArchiveInstanceId) && marker.DetectedAtUtc.Offset == TimeSpan.Zero
                && CanonicalJson.SerializeBytes(marker).AsSpan().SequenceEqual(bytes) ? marker : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A stored finding record, when it is a D+ refusal (<c>DeliveryRefused</c>) of a stream-stopping classification; otherwise null.</summary>
    public static JournalVerificationFinding? TryParseStoppingRefusal(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var finding = JsonSerializer.Deserialize<JournalVerificationFinding>(bytes, CanonicalJson.Options);
            return finding is not null && string.Equals(finding.SweepOutcome, "DeliveryRefused", StringComparison.Ordinal) && StopsStream(finding.FindingCode, finding.Subtype)
                && finding.FirstRevision is >= 1 && finding.StopGeneration is null or >= 1 ? finding : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
