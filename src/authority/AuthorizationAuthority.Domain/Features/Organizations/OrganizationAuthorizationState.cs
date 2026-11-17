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

    /// <summary>
    /// Authority-held ZedToken for the applied projection. Never accepted from a caller. Since C8 M4 S1 it is only the freshness
    /// selector of the marker pin read, never integrity evidence (column <c>projection_snapshot_zed_token</c>).
    /// </summary>
    public string? ProjectionZedToken { get; set; }

    /// <summary>
    /// C8 M4 S1 rollback fence: the pre-S1 column <c>projection_zed_token</c>, kept NULL by a check constraint. An Authority build
    /// without S1 reads its token from this column (so its gate stays closed) and cannot write one (so it cannot open the
    /// gate): a deployment rollback to such a build fails closed instead of silently losing S1. Never written.
    /// </summary>
    public string? LegacyProjectionZedToken { get; set; }

    private long contentIntegrityRevision;

    /// <summary>
    /// Separate monotonic content stream (MERKLE_RECORD_MANIFEST_PROTOCOL.md §2.2 step 6). IX-1 (INV-R, design §18.2): set only
    /// at creation; afterwards advanced only through <see cref="AdvanceContentIntegrityRevision"/>, which requires the caller to
    /// state what happens to the search projection in the same transaction. The database enforces the same invariant.
    /// </summary>
    public long ContentIntegrityRevision
    {
        get => contentIntegrityRevision;
        init => contentIntegrityRevision = value;
    }

    /// <summary>
    /// INV-R: the only way to advance <see cref="ContentIntegrityRevision"/>. The caller (the Application's
    /// ContentRevisionCoordinator, enforced by an architecture test) applies <paramref name="projectionAction"/> to the search
    /// projection in the same transaction. Returns the new revision.
    /// </summary>
    public long AdvanceContentIntegrityRevision(ContentRevisionProjectionAction projectionAction)
    {
        if (!Enum.IsDefined(projectionAction))
        {
            throw new ArgumentOutOfRangeException(nameof(projectionAction));
        }

        return ++contentIntegrityRevision;
    }

    public string CheckpointEnforcementModeName { get; set; } = CheckpointEnforcementModes.RequiredBeforeEffective;

    public long ProjectionWorkerFencingEpoch { get; set; }

    public bool IntegrityQuarantineEnabled { get; set; }

    public string? IntegrityQuarantineReasonCode { get; set; }

    public byte[]? LatestSecurityJournalDigest { get; set; }

    public byte[]? LatestContentJournalDigest { get; set; }

    /// <summary>
    /// Phase C7 M1 (OD-8): revision of the separate audit stream (search catalogue issuance and denials). It is archived and
    /// anchored like the other streams but never gates access and never advances the content-integrity revision, so audit
    /// writes cannot invalidate other snapshots or delay reads.
    /// </summary>
    public long AuditJournalRevision { get; set; }

    public byte[]? LatestAuditJournalDigest { get; set; }

    public DateTimeOffset StateModifiedOn { get; set; }

    /// <summary>C8 onboarding: the request that created this organization through the Platform Console (null for legacy fixtures).</summary>
    public Guid? OrganizationOnboardingRequestId { get; set; }
}

public static class CheckpointEnforcementModes
{
    public const string Asynchronous = "Asynchronous";
    public const string RequiredBeforeEffective = "RequiredBeforeEffective";
}

/// <summary>INV-R (design §18.2.2): what the transaction that advances the content-integrity revision does to the search projection.</summary>
public enum ContentRevisionProjectionAction
{
    /// <summary>W1: a content commit; the record's index is re-derived in the same transaction and the projection revision advances.</summary>
    AdvanceWithDerivation = 1,

    /// <summary>W2-W4: no posting change (quarantine incidents, snapshot mismatch); the projection revision advances.</summary>
    AdvanceWithoutPostingChange = 2,

    /// <summary>A path that cannot derive atomically: the projection is marked ReconciliationRequired from the previous revision.</summary>
    ReconciliationRequired = 3,
}
