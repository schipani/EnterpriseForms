namespace AuthorizationAuthority.Domain.Features.SecurityCommands;

/// <summary>Ordered projection work item; one per committed authorization revision (A4 contiguous, no gaps).</summary>
public sealed class ProjectionOutboxEvent
{
    public Guid ProjectionOutboxEventId { get; set; }

    public Guid OrganizationId { get; set; }

    public Guid SecurityCommandId { get; set; }

    public long TargetAuthorizationRevision { get; set; }

    /// <summary>JSON array of <see cref="RelationshipDeltaEntry"/> applied exactly, in order, behind the closed gate.</summary>
    public string RelationshipDeltaJson { get; set; } = "[]";

    public string ProjectionDeliveryState { get; set; } = ProjectionDeliveryStates.Pending;

    public int DeliveryAttemptCount { get; set; }

    public DateTimeOffset? LastDeliveryAttemptOn { get; set; }

    public string? ProjectionConfirmedZedToken { get; set; }

    public DateTimeOffset? ProjectionAppliedOn { get; set; }

    public long? ClaimedWorkerFencingEpoch { get; set; }

    public string? LastProjectionErrorCode { get; set; }

    public DateTimeOffset ProjectionCreatedOn { get; set; }
}

public static class ProjectionDeliveryStates
{
    public const string Pending = "Pending";
    public const string Delivered = "Delivered";
    public const string Failed = "Failed";
}
