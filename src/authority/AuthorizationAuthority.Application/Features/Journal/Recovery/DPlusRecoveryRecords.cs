using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AuthorizationAuthority.Application.Common;

namespace AuthorizationAuthority.Application.Features.Journal.Recovery;

/// <summary>
/// SEC-BL-01 G14-R2 (contract amendment A-4, §24.2): names of the create-only recovery records and evidence commitments. Each record exists in
/// the journal-verification store (<c>recovery/</c>, <c>intents/</c>) and byte-identically in the archive's reserved prefix
/// (<c>meta/dplus-recoveries/</c>, <c>meta/dplus-intents/</c>); evidence copies (row and restore bytes) exist in the archive only. The relative
/// names below are identical in both stores.
/// </summary>
public static class DPlusRecoveryNames
{
    public const string ArchiveRecoveryPrefix = "meta/dplus-recoveries/";
    public const string ArchiveCommitmentPrefix = "meta/dplus-intents/";
    public const string StoreRecoveryPrefix = "recovery/";
    public const string StoreCommitmentPrefix = "intents/";

    public static string StreamRoot(Guid organizationId, string streamName) => organizationId.ToString("D") + "/" + streamName + "/";

    public static string Statement(Guid organizationId, string streamName, Guid recoveryId) => RecoveryRoot(organizationId, streamName, recoveryId) + "statement.json";

    public static string Investigation(Guid organizationId, string streamName, Guid recoveryId) => RecoveryRoot(organizationId, streamName, recoveryId) + "investigation.json";

    public static string Evidence(Guid organizationId, string streamName, Guid recoveryId) => RecoveryRoot(organizationId, streamName, recoveryId) + "evidence.json";

    /// <summary>Archive-only: the failed row's body bytes as read when the statement was built.</summary>
    public static string RowCopy(Guid organizationId, string streamName, Guid recoveryId) => RecoveryRoot(organizationId, streamName, recoveryId) + "evidence/row.json";

    /// <summary>Archive-only (SR-C1): the bytes proposed for the absent predecessor, bound by digest in the statement.</summary>
    public static string RestoreCopy(Guid organizationId, string streamName, Guid recoveryId) => RecoveryRoot(organizationId, streamName, recoveryId) + "evidence/restore.json";

    public static string Decision(Guid organizationId, string streamName, Guid recoveryId, Guid jti) => RecoveryRoot(organizationId, streamName, recoveryId) + "decisions/" + jti.ToString("D") + ".jws";

    public static string Action(Guid organizationId, string streamName, Guid recoveryId) => RecoveryRoot(organizationId, streamName, recoveryId) + "action.json";

    public static string Outcome(Guid organizationId, string streamName, Guid recoveryId) => RecoveryRoot(organizationId, streamName, recoveryId) + "outcome.json";

    /// <summary>One execution record per stop set (create-only): a second execution of the same stop set conflicts.</summary>
    public static string Execution(Guid organizationId, string streamName, string stopSetSha256Hex) => StreamRoot(organizationId, streamName) + "executions/" + stopSetSha256Hex + ".json";

    /// <summary>W2-S commitment key of (revision, generation): <c>{revision:D12}.json</c> or <c>{revision:D12}.r{g:D4}.json</c>.</summary>
    public static string Commitment(Guid organizationId, string streamName, long revision, int stopGeneration) =>
        StreamRoot(organizationId, streamName) + revision.ToString("D12", CultureInfo.InvariantCulture)
        + (stopGeneration == 0 ? string.Empty : ".r" + stopGeneration.ToString("D4", CultureInfo.InvariantCulture)) + ".json";

    private static string RecoveryRoot(Guid organizationId, string streamName, Guid recoveryId) => StreamRoot(organizationId, streamName) + recoveryId.ToString("D") + "/";
}

/// <summary>Strategy and classification vocabulary (A-4 §24.4). Only SR-A and SR-C1 are operational in G14-R2; every other value is refused.</summary>
public static class DPlusRecoveryStrategies
{
    public const string FalseAlarm = "SR-A";
    public const string RestorePredecessor = "SR-C1";

    /// <summary>Deferred strategies (named so that a statement for them is refused explicitly, never silently treated as another).</summary>
    public static IReadOnlyList<string> Deferred { get; } = ["SR-B", "SR-B-UNDELIVERABLE", "SR-C2", "SR-D", "SR-E"];

    public static bool IsOperational(string strategy) => strategy is FalseAlarm or RestorePredecessor;

    /// <summary>Non-adversarial causes accepted for SR-A (the investigation's classification).</summary>
    public static IReadOnlyList<string> FalseAlarmCauses { get; } = ["OperationalFalseAlarm", "DefectCorrected", "DevelopmentReset", "BenignEvidenceChange"];

    public const string PredecessorLostCause = "PredecessorLostGenuineCopyAvailable";
}

/// <summary>One stop record covered by a statement (A-4 §24.3): a marker, an invalid object under the marker prefix, or a D+ finding.</summary>
public sealed record DPlusStopRecordReference(string RecordKind, string ObjectName, string Sha256Hex, long? FailedRevision, int? StopGeneration)
{
    public const string Marker = "Marker";
    public const string InvalidMarkerObject = "InvalidMarkerObject";
    public const string Finding = "Finding";
}

/// <summary>The investigation record (<c>DynamicForms/DPlusStreamInvestigation/v1</c>). The narrative itself stays outside; only its digest is bound.</summary>
public sealed record DPlusStreamInvestigation(string InvestigationSchema, Guid RecoveryId, Guid OrganizationId, string StreamName, long FailedRevision, string CauseClassification,
    bool ContainmentAttested, string? ContainmentNoteSha256Hex, string AnalystLabel, string NarrativeSha256Hex)
{
    public const string SchemaV1 = "DynamicForms/DPlusStreamInvestigation/v1";
}

/// <summary>The SR-C1 anchor: the trusted checkpoint whose verified revision is the absent predecessor.</summary>
public sealed record DPlusRecoveryAnchor(long CheckpointSequence, string CheckpointSha256Hex, long VerifiedRevision, string VerifiedDigestHex, string AssuranceLevel, string ArchiveInstanceId);

/// <summary>
/// The evidence snapshot (<c>DynamicForms/DPlusStreamRecoveryEvidence/v1</c>). Deterministic: no time, no AuthorityDb delivery state, no
/// checkpoint head sequence (they may change without changing the evidence). Re-read and recomputed by the executor; any difference fails.
/// </summary>
public sealed record DPlusRecoveryEvidence(string EvidenceSchema, Guid OrganizationId, string StreamName, long FailedRevision, int StopGeneration, Guid RowEntryId, string RowBodySha256Hex,
    string? RowPreviousDigestHex, string PredecessorObjectName, string PredecessorState, string? PredecessorSha256Hex, string PinnedArchiveInstanceId, string SentinelArchiveInstanceId,
    string SentinelSha256Hex, string? ProvisioningRecordSha256Hex, string? CommitmentObjectName, string? CommitmentSha256Hex, DPlusRecoveryAnchor? Anchor, string? RestoreSha256Hex,
    IReadOnlyList<string> StopRecordIntegrityCodes, string EvaluationVerdict, string RuleSetVersion)
{
    public const string SchemaV1 = "DynamicForms/DPlusStreamRecoveryEvidence/v1";
}

/// <summary>What D+ must observe at the recovered revision after execution (A-4 §24.5).</summary>
public sealed record DPlusRecoveryExpectation(string ArchiveInstanceId, string RowBodySha256Hex, string PredecessorState, string? PredecessorSha256Hex);

/// <summary>The statement every approver signs (<c>DynamicForms/DPlusStreamRecoveryStatement/v1</c>); its SHA-256 is the <c>statement_sha256</c> claim.</summary>
public sealed record DPlusRecoveryStatement(string StatementSchema, Guid RecoveryId, Guid OrganizationId, string StreamName, long FailedRevision, int StopGeneration,
    IReadOnlyList<DPlusStopRecordReference> StopSet, string StopSetSha256Hex, string Strategy, string EligibilityClass, string EvidenceSha256Hex, DPlusRecoveryExpectation Expected,
    string InvestigationSha256Hex, string RequesterLabel, string RequestReasonSha256Hex, DateTimeOffset NotBeforeUtc, DateTimeOffset NotAfterUtc, string ToolVersion, string? SourceCommit)
{
    public const string SchemaV1 = "DynamicForms/DPlusStreamRecoveryStatement/v1";

    /// <summary>A-4 §24.3: the execution window is at most 24 hours.</summary>
    public static TimeSpan MaximumWindow { get; } = TimeSpan.FromHours(24);
}

/// <summary>The SR-C1 action record (<c>DynamicForms/DPlusStreamRecoveryAction/v1</c>): the archive object created and its digest.</summary>
public sealed record DPlusRecoveryAction(string ActionSchema, Guid RecoveryId, string Strategy, string ArchiveObjectName, string Sha256Hex)
{
    public const string SchemaV1 = "DynamicForms/DPlusStreamRecoveryAction/v1";
}

/// <summary>The execution record (<c>DynamicForms/DPlusStreamRecoveryExecution/v1</c>), written last; no time, so a retry is byte-identical.</summary>
public sealed record DPlusRecoveryExecution(string ExecutionSchema, Guid RecoveryId, Guid OrganizationId, string StreamName, long FailedRevision, int StopGeneration, string StatementSha256Hex,
    string StopSetSha256Hex, string ExecutorLabel, string ToolVersion)
{
    public const string SchemaV1 = "DynamicForms/DPlusStreamRecoveryExecution/v1";
}

/// <summary>The terminal outcome (<c>DynamicForms/DPlusStreamRecoveryOutcome/v1</c>), create-only. Never consulted by D+ (the lift rests on signatures and the execution record).</summary>
public sealed record DPlusRecoveryOutcome(string OutcomeSchema, Guid RecoveryId, string OutcomeKind, string ReasonCode, string Detail, DateTimeOffset RecordedAtUtc, string ToolVersion)
{
    public const string SchemaV1 = "DynamicForms/DPlusStreamRecoveryOutcome/v1";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}

/// <summary>
/// The W2-S evidence commitment (<c>DynamicForms/DPlusEvidenceCommitment/v1</c>): organization, stream, revision, stop generation, archive
/// instance, row identity and digests, predecessor identity, state and digest, rule-set version. No time, so a retry is byte-identical.
/// </summary>
public sealed record DPlusEvidenceCommitment(string CommitmentSchema, Guid OrganizationId, string StreamName, long StreamRevision, int StopGeneration, string ArchiveInstanceId, Guid RowEntryId,
    string RowBodySha256Hex, string? RowPreviousDigestHex, string? PredecessorObjectName, string PredecessorState, string? PredecessorSha256Hex, string RuleSetVersion)
{
    public const string SchemaV1 = "DynamicForms/DPlusEvidenceCommitment/v1";

    /// <summary>The D+ rule-set version (A-4 §24.7; SR-OD-15 unreviewed: a different version is treated as a different commitment).</summary>
    public const string CurrentRuleSetVersion = "DPlus/R1-R7/v1";
}

/// <summary>Canonical serialization and strict parsing of every A-4 record (unknown members refused; the bytes must re-serialize identically).</summary>
public static class DPlusRecoveryCodec
{
    private static readonly JsonSerializerOptions Strict = new(CanonicalJson.Options) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static byte[] Serialize<T>(T record) => CanonicalJson.SerializeBytes(record);

    public static string Sha256Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static string Sha256Hex(string text) => Sha256Hex(System.Text.Encoding.UTF8.GetBytes(text ?? string.Empty));

    public static string? Hex(byte[]? bytes) => bytes is null ? null : Convert.ToHexStringLower(bytes);

    /// <summary>SHA-256 over the canonical form of the stop set (sorted by object name, ordinal).</summary>
    public static string StopSetSha256Hex(IReadOnlyList<DPlusStopRecordReference> stopSet) => Sha256Hex(Serialize(stopSet));

    public static IReadOnlyList<DPlusStopRecordReference> SortStopSet(IEnumerable<DPlusStopRecordReference> records) =>
        [.. records.OrderBy(r => r.ObjectName, StringComparer.Ordinal).ThenBy(r => r.Sha256Hex, StringComparer.Ordinal)];

    public static T? TryParse<T>(ReadOnlySpan<byte> bytes) where T : class
    {
        try
        {
            var record = JsonSerializer.Deserialize<T>(bytes, Strict);
            return record is not null && Serialize(record).AsSpan().SequenceEqual(bytes) ? record : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    public static bool IsSha256Hex(string? value) => value is { Length: 64 } && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}
