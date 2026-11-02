using System.Text.Json;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Application.Features.SecurityOperations.Incidents;
using EnterpriseForms.Domain.Features.Audit;
using EnterpriseForms.Domain.Features.SecurityOperations;
using Microsoft.Extensions.Logging;

namespace EnterpriseForms.Infrastructure.Features.SecurityOperations;

/// <summary>
/// Writes observations to the incident triage store in its own transaction, serialized per (tenant, problem) by an
/// advisory lock so retries and concurrent failing reads deduplicate (<see cref="SecurityIncidentDeduplication"/>). A new
/// incident also emits SECURITY_INCIDENT_DETECTED in the same transaction. Never throws: the caller has already decided
/// to fail closed, and a recording failure (database down, constraint, timeout) must not change that or mask it.
/// </summary>
public sealed partial class SecurityIncidentRecorder(IBusinessStore store, TimeProvider clock, ILogger<SecurityIncidentRecorder> logger) : ISecurityIncidentRecorder
{
    private static readonly TimeSpan RecordingTimeout = TimeSpan.FromSeconds(5);

    public async Task RecordAsync(SecurityIncidentObservation observation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(observation);
        // Independent of the request's cancellation: a client that disconnects after a tamper detection must not prevent the record.
        using var timeout = new CancellationTokenSource(RecordingTimeout);
        try
        {
            await RecordCoreAsync(observation, timeout.Token).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // deliberately broad: recording is best effort and must never alter the fail-closed outcome
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogRecordingFailed(logger, ex, observation.IncidentTypeName, observation.TenantId, observation.CorrelationId);
        }
    }

    private async Task RecordCoreAsync(SecurityIncidentObservation observation, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var key = observation.DeduplicationKey;
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var latest = await scope.LockLatestSecurityIncidentAsync(observation.TenantId, key, cancellationToken).ConfigureAwait(false);
        var lastHistory = latest is null ? null : await scope.GetLatestSecurityIncidentOccurrenceOnAsync(latest.SecurityIncidentId, cancellationToken).ConfigureAwait(false);
        var decision = SecurityIncidentDeduplication.Decide(latest, observation, lastHistory, now);

        SecurityIncident incident;
        if (decision.Kind == SecurityIncidentDecisionKind.CreateIncident)
        {
            incident = new SecurityIncident
            {
                SecurityIncidentId = Guid.CreateVersion7(),
                TenantId = observation.TenantId,
                FormRecordId = observation.FormRecordId,
                IncidentTypeName = observation.IncidentTypeName,
                IncidentSeverityName = SecurityIncidentSeverities.For(observation.IncidentTypeName),
                IncidentStatusName = SecurityIncidentStatuses.Open,
                IncidentDeduplicationKey = key,
                FirstDetectedOn = now,
                LastDetectedOn = now,
                OccurrenceCount = 1,
                DetectionSourceName = observation.DetectionSourceName,
                VerificationErrorCode = observation.VerificationErrorCode,
                FirstCorrelationIdentifier = observation.CorrelationId,
                IncidentRevision = 1,
                IncidentModifiedOn = now,
            };
            scope.AddSecurityIncident(incident);
            scope.AddAuditEvent(new AuditEvent
            {
                AuditEventId = Guid.CreateVersion7(),
                TenantId = observation.TenantId,
                EventOccurredOn = now,
                EventCategory = "Security",
                EventAction = AuditActions.SecurityIncidentDetected,
                EventOutcome = "Success",
                ActorUserId = observation.ObservedByUserId,
                ActorType = "System",
                FormRecordId = observation.FormRecordId,
                TargetEntityType = "security_incident",
                TargetEntityId = incident.SecurityIncidentId,
                CorrelationIdentifier = observation.CorrelationId,
                EventDescription = "security incident detected: " + observation.IncidentTypeName,
                EventMetadataJson = JsonSerializer.Serialize(new { incidentType = observation.IncidentTypeName, detectionSource = observation.DetectionSourceName, verificationErrorCode = observation.VerificationErrorCode }),
                EventSchemaVersion = 1,
            });
        }
        else
        {
            incident = latest!;
            incident.OccurrenceCount = incident.OccurrenceCount == int.MaxValue ? int.MaxValue : incident.OccurrenceCount + 1;
            if (now > incident.LastDetectedOn)
            {
                incident.LastDetectedOn = now;
            }
        }

        if (decision.AppendHistoryRow)
        {
            scope.AddSecurityIncidentOccurrence(new SecurityIncidentOccurrence
            {
                SecurityIncidentOccurrenceId = Guid.CreateVersion7(),
                SecurityIncidentId = incident.SecurityIncidentId,
                TenantId = observation.TenantId,
                OccurrenceDetectedOn = now,
                ObservedIncidentTypeName = observation.IncidentTypeName,
                DetectionSourceName = observation.DetectionSourceName,
                VerificationErrorCode = observation.VerificationErrorCode,
                ObservedByUserId = observation.ObservedByUserId,
                RequestCorrelationIdentifier = observation.CorrelationId,
                CommittedContentRevision = observation.CommittedContentRevision,
            });
        }

        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 7301, Level = LogLevel.Error, Message = "Security incident could not be recorded (type {IncidentType}, tenant {TenantId}, correlation {CorrelationId}); the request still failed closed")]
    private static partial void LogRecordingFailed(ILogger logger, Exception exception, string incidentType, Guid tenantId, Guid correlationId);
}
