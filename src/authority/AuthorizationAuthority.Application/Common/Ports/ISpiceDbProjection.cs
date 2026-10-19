using AuthorizationAuthority.Domain.Features.SecurityCommands;

namespace AuthorizationAuthority.Application.Common.Ports;

public sealed record SpiceDbCheck(string ResourceType, string ResourceId, string Permission, string SubjectType, string SubjectId);

public enum SpiceDbCheckOutcome
{
    Allowed,
    Denied,
    /// <summary>Conditional or unspecified permissionship: always treated as deny.</summary>
    Unknown,
}

/// <summary>SpiceDB query projection. Only the authority projector writes; the business API has no credential or network path to it.</summary>
public interface ISpiceDbProjection
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken);

    /// <summary>Creates the organization's step-0 cursor marker if absent. Returns the ZedToken.</summary>
    Task<string> InitializeCursorAsync(Guid organizationId, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically: precondition cursor == expectedStep, apply the exact delta, delete the old marker, create the new marker.
    /// A stale worker whose expected step no longer matches gets <see cref="SpiceDbPreconditionFailedException"/> and changes nothing.
    /// </summary>
    Task<string> ApplyDeltaAsync(Guid organizationId, long expectedStep, long newStep, IReadOnlyList<RelationshipDeltaEntry> delta, CancellationToken cancellationToken);

    /// <summary>Current cursor step (null if no marker) and a fresh ZedToken for that read.</summary>
    Task<(long? Step, string ZedToken)> ReadCursorStepAsync(Guid organizationId, CancellationToken cancellationToken);

    Task<bool> TupleExistsAsync(RelationshipTuple tuple, CancellationToken cancellationToken);

    /// <summary>at_least_as_fresh(zedToken) check; minimize_latency is never used for protected decisions.</summary>
    Task<SpiceDbCheckOutcome> CheckAsync(SpiceDbCheck check, string zedToken, CancellationToken cancellationToken);

    Task<IReadOnlyList<SpiceDbCheckOutcome>> CheckBulkAsync(IReadOnlyList<SpiceDbCheck> checks, string zedToken, CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> LookupResourcesAsync(string resourceType, string permission, string subjectType, string subjectId, string zedToken, int limit, CancellationToken cancellationToken);

    Task<(bool Ready, string Detail)> ProbeAsync(CancellationToken cancellationToken);
}

public sealed class SpiceDbPreconditionFailedException(string detail) : Exception(detail);

public sealed class SpiceDbUnavailableException(string detail, Exception? inner = null) : Exception(detail, inner);
