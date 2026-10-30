using System.Text.RegularExpressions;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Domain.Features.SecurityOperations;

namespace EnterpriseForms.Application.Features.SecurityOperations.Incidents;

/// <summary>
/// A verification problem the business API just observed. Carries identifiers and codes only — never record content,
/// field values, file names, keys or signatures.
/// </summary>
public sealed record SecurityIncidentObservation(
    Guid TenantId,
    Guid? ObservedByUserId,
    Guid CorrelationId,
    string IncidentTypeName,
    Guid? FormRecordId,
    string DetectionSourceName,
    string VerificationErrorCode,
    long? CommittedContentRevision = null)
{
    public static SecurityIncidentObservation For(RequestContext context, string incidentTypeName, Guid? formRecordId, string detectionSource, string errorCode, long? committedContentRevision = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new(context.OrganizationId, context.LocalUserId, context.CorrelationId, incidentTypeName, formRecordId, detectionSource, SecurityIncidentClassifier.SanitizeCode(errorCode), committedContentRevision);
    }

    /// <summary>
    /// One problem, one key. Every integrity observation of a record shares the record key, so a confirmed mismatch and the
    /// quarantine refusals that follow it are one incident; service outages share one key per error code.
    /// </summary>
    public string DeduplicationKey => FormRecordId is { } record && IncidentTypeName is SecurityIncidentTypes.ConfirmedIntegrityMismatch or SecurityIncidentTypes.IntegrityQuarantine or SecurityIncidentTypes.OperationalVerificationFailure
        ? (IncidentTypeName == SecurityIncidentTypes.OperationalVerificationFailure ? "operational:" : "integrity:") + record.ToString("N")
        : "service:" + VerificationErrorCode;
}

/// <summary>
/// Records observations durably (business-side triage store). Implementations MUST NOT throw: a failure to record is
/// logged and swallowed, and the caller always continues to fail closed exactly as before.
/// </summary>
public interface ISecurityIncidentRecorder
{
    Task RecordAsync(SecurityIncidentObservation observation, CancellationToken cancellationToken);
}

/// <summary>Maps verification outcomes to incident types. Unclassified outcomes (not found, permission, transient readiness) are not incidents.</summary>
public static partial class SecurityIncidentClassifier
{
    [GeneratedRegex("[^A-Za-z0-9_.:-]")]
    private static partial Regex UnsafeCodeCharacters();

    /// <summary>
    /// A false <c>Verified</c> from VerifyRecordProjection. NO_COMMITTED_CONTENT and HEAD_CHANGED are races, not integrity
    /// failures; anything else means the Authority compared the rows with its accepted manifest and they differed.
    /// </summary>
    public static string? FromVerificationFailure(string? failureReasonCode) => failureReasonCode switch
    {
        "NO_COMMITTED_CONTENT" or "HEAD_CHANGED" => null,
        _ => SecurityIncidentTypes.ConfirmedIntegrityMismatch,
    };

    /// <summary>An Authority client error. AUTHORITY_NOT_READY (for example awaiting the archive receipt after an edit) is transient and not an incident.</summary>
    public static string? FromAuthorityError(AuthorityClientException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.ErrorCode switch
        {
            "INTEGRITY_QUARANTINE" => SecurityIncidentTypes.IntegrityQuarantine,
            "AUTHORITY_UNAVAILABLE" or "KEY_UNAVAILABLE" or "INTEGRITY_SERVICE_UNAVAILABLE" => SecurityIncidentTypes.VerificationServiceUnavailable,
            _ => null,
        };
    }

    public static string SanitizeCode(string? code)
    {
        var cleaned = UnsafeCodeCharacters().Replace(code ?? string.Empty, string.Empty);
        return cleaned.Length > 80 ? cleaned[..80] : cleaned;
    }
}

/// <summary>What one observation does to the store. Pure, so the rule is unit-tested without a database.</summary>
public enum SecurityIncidentDecisionKind
{
    /// <summary>No incident with this key, or the latest is closed and this is a fresh detection: open a new incident.</summary>
    CreateIncident,

    /// <summary>Count the observation on the latest incident (active, or closed but this only re-observes the same quarantine).</summary>
    CountOnExisting,
}

public sealed record SecurityIncidentDecision(SecurityIncidentDecisionKind Kind, bool AppendHistoryRow);

public static class SecurityIncidentDeduplication
{
    /// <summary>Repeated detections closer together than this are counted but share one history row (bounded growth).</summary>
    public static readonly TimeSpan HistorySamplingInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Rules: (1) an active (Open/Investigating) incident with the key absorbs every observation; (2) a closed incident absorbs
    /// re-observations of the same quarantine (a refusal of an already-quarantined record is not a new problem, and closing an
    /// incident never lifts the quarantine), without being reopened; (3) otherwise a new incident is opened. Status is never
    /// changed by detection.
    /// </summary>
    public static SecurityIncidentDecision Decide(SecurityIncident? latest, SecurityIncidentObservation observation, DateTimeOffset? lastHistoryRowOn, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (latest is null)
        {
            return new(SecurityIncidentDecisionKind.CreateIncident, true);
        }

        var absorb = SecurityIncidentStatuses.IsActive(latest.IncidentStatusName)
                     || (observation.IncidentTypeName == SecurityIncidentTypes.IntegrityQuarantine && SecurityIncidentStatuses.IsClosed(latest.IncidentStatusName));
        if (!absorb)
        {
            return new(SecurityIncidentDecisionKind.CreateIncident, true);
        }

        var appendHistory = lastHistoryRowOn is null || now - lastHistoryRowOn.Value >= HistorySamplingInterval || observation.IncidentTypeName != latest.IncidentTypeName;
        return new(SecurityIncidentDecisionKind.CountOnExisting, appendHistory);
    }
}
