using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Domain.Features.Organizations;

namespace AuthorizationAuthority.Application.Common;

public enum ProjectionSnapshotState
{
    Open,
    Completed,
    Failed,
}

/// <summary>
/// C8 M4 S1 (Design B §2.4): the single-use, single-request SpiceDB snapshot of one protected decision. Only
/// <see cref="ProjectionIntegrityVerifier.PinAsync"/> creates one, after the pin read found the expected marker; every
/// decision read requires it to be <see cref="ProjectionSnapshotState.Open"/>; <see cref="ProjectionIntegrityVerifier.CompleteAsync"/>
/// is the only way to <see cref="ProjectionSnapshotState.Completed"/>, after which nothing accepts it. A decision path therefore
/// cannot read SpiceDB outside one pin, decide, verify cycle, nor release a decision without the verify step.
/// </summary>
public sealed class VerifiedProjectionSnapshot
{
    internal VerifiedProjectionSnapshot(IAuthorityScope scope, OrganizationAuthorizationState state, bool organizationLocked, string expectedMarkerSubjectId, string pin, Guid correlationId)
    {
        Scope = scope;
        LockedState = organizationLocked ? state : null;
        OrganizationId = state.OrganizationId;
        ExpectedRevision = state.ProjectionAppliedRevision;
        AuthorizationRevision = state.AuthorizationRevision;
        JournalDigest = state.LatestSecurityJournalDigest is null ? null : (byte[])state.LatestSecurityJournalDigest.Clone();
        ExpectedMarkerSubjectId = expectedMarkerSubjectId;
        Pin = pin;
        CorrelationId = correlationId;
    }

    public Guid OrganizationId { get; }

    /// <summary>The Authority's committed and applied revision P₁ the decision is bound to.</summary>
    public long ExpectedRevision { get; }

    /// <summary>The pinned SpiceDB snapshot S (the pin read's <c>read_at</c>). Used only by the SpiceDB adapter; never leaves the Authority.</summary>
    public string Pin { get; }

    public ProjectionSnapshotState State { get; private set; } = ProjectionSnapshotState.Open;

    internal long AuthorizationRevision { get; }

    internal byte[]? JournalDigest { get; }

    internal string ExpectedMarkerSubjectId { get; }

    internal IAuthorityScope Scope { get; }

    /// <summary>The caller's locked, tracked organization row when it holds the organization lock through the decision.</summary>
    internal OrganizationAuthorizationState? LockedState { get; }

    internal bool OrganizationLocked => LockedState is not null;

    internal Guid CorrelationId { get; }

    /// <summary>C10 M1-WP2: the decision instant (the verifier's clock at the pin); share expiry is evaluated at it.</summary>
    internal DateTimeOffset DecisionInstant { get; set; }

    /// <summary>C10 M1-WP2: the access-time share expiry overlay of this decision, loaded on first use.</summary>
    internal ShareExpiryOverlay? ShareExpiry { get; set; }

    /// <summary>Throws unless the snapshot is open: decision reads are only valid between pin and verify.</summary>
    public void RequireOpen()
    {
        if (State != ProjectionSnapshotState.Open)
        {
            throw new InvalidOperationException("projection snapshot is " + State + "; a decision read needs an open snapshot");
        }
    }

    /// <summary>Throws unless the verify step succeeded: a decision may be released only after it.</summary>
    public void RequireCompleted()
    {
        if (State != ProjectionSnapshotState.Completed)
        {
            throw new InvalidOperationException("projection snapshot is " + State + "; a decision may be released only after verification");
        }
    }

    internal void MarkCompleted()
    {
        RequireOpen();
        State = ProjectionSnapshotState.Completed;
    }

    internal void MarkFailed() => State = ProjectionSnapshotState.Failed;

    internal bool StateMoved(OrganizationAuthorizationState? now) =>
        now is null
        || now.AuthorizationRevision != AuthorizationRevision
        || now.ProjectionAppliedRevision != ExpectedRevision
        || !DigestsEqual(now.LatestSecurityJournalDigest, JournalDigest);

    internal static bool DigestsEqual(byte[]? a, byte[]? b) => a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);
}
