namespace AuthorizationAuthority.Domain.Features.Organizations;

/// <summary>Per-organization authoritative revision stream (CROSS_SYSTEM_CONSISTENCY_PROTOCOL.md §1 A2–A4). One row per organization; locked FOR UPDATE on every mutation.</summary>
public sealed class OrganizationAuthorizationState
{
    public Guid OrganizationAuthorizationStateId { get; set; }

    public Guid OrganizationId { get; set; }

    public string OrganizationDisplayName { get; set; } = string.Empty;

    /// <summary>Desired (committed) authorization revision; advances exactly once per committed security mutation.</summary>
    public long AuthorizationRevision { get; set; }

    /// <summary>Highest revision whose exact delta has been verified in SpiceDB. Never greater than <see cref="AuthorizationRevision"/>.</summary>
    public long ProjectionAppliedRevision { get; set; }

    /// <summary>Authority-held ZedToken for the applied projection. Never accepted from a caller.</summary>
    public string? ProjectionZedToken { get; set; }

    /// <summary>Separate monotonic content stream (MERKLE_RECORD_MANIFEST_PROTOCOL.md §2.2 step 6).</summary>
    public long ContentIntegrityRevision { get; set; }

    public string CheckpointEnforcementModeName { get; set; } = CheckpointEnforcementModes.RequiredBeforeEffective;

    public long ProjectionWorkerFencingEpoch { get; set; }

    public bool IntegrityQuarantineEnabled { get; set; }

    public string? IntegrityQuarantineReasonCode { get; set; }

    public byte[]? LatestSecurityJournalDigest { get; set; }

    public byte[]? LatestContentJournalDigest { get; set; }

    public DateTimeOffset StateModifiedOn { get; set; }
}

public static class CheckpointEnforcementModes
{
    public const string Asynchronous = "Asynchronous";
    public const string RequiredBeforeEffective = "RequiredBeforeEffective";
}
