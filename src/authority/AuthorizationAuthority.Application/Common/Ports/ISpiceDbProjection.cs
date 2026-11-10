using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Domain.Features.SecurityCommands;

namespace AuthorizationAuthority.Application.Common.Ports;

/// <param name="SubjectRelation">IX-5a: optional subject relation (for example <c>team#participant</c>); empty for a plain object subject.</param>
public sealed record SpiceDbCheck(string ResourceType, string ResourceId, string Permission, string SubjectType, string SubjectId, string SubjectRelation = "");

public enum SpiceDbCheckOutcome
{
    Allowed,
    Denied,
    /// <summary>Conditional or unspecified permissionship: always treated as deny.</summary>
    Unknown,
}

/// <summary>How a projection-marker read selects its SpiceDB snapshot (C8 M4 S1, Design B §2.4).</summary>
public enum MarkerReadConsistency
{
    /// <summary>The pin read (M1): at least as fresh as the Authority-held token; its <c>read_at</c> becomes the decision snapshot.</summary>
    AtLeastAsFresh,

    /// <summary>The decisive verify read (M2), the periodic check and the transition: the head snapshot, bypassing SpiceDB caches.</summary>
    FullyConsistent,
}

/// <summary>
/// The organization's projection-marker relationships as SpiceDB returned them, and the snapshot of that read (null when no
/// relationship matched: SpiceDB then streams no message and so no <c>read_at</c>).
/// </summary>
public sealed record MarkerObservation(IReadOnlyList<string> MarkerSubjectIds, string? ReadAt);

/// <summary>SpiceDB query projection. Only the authority projector writes; the business API has no credential or network path to it.</summary>
public interface ISpiceDbProjection
{
    Task EnsureSchemaAsync(CancellationToken cancellationToken);

    /// <summary>Creates the organization's step-0 marker (<see cref="ProjectionMarker"/> v2) if no marker exists. Returns the ZedToken.</summary>
    Task<string> InitializeCursorAsync(Guid organizationId, CancellationToken cancellationToken);

    /// <summary>
    /// Atomically: precondition marker == <paramref name="expectedMarkerSubjectId"/> and no marker == <paramref name="newMarkerSubjectId"/>,
    /// apply the exact delta, delete the old marker, create the new marker. A stale worker whose expected marker no longer
    /// matches gets <see cref="SpiceDbPreconditionFailedException"/> and changes nothing.
    /// </summary>
    Task<string> ApplyDeltaAsync(Guid organizationId, string expectedMarkerSubjectId, string newMarkerSubjectId, IReadOnlyList<RelationshipDeltaEntry> delta, CancellationToken cancellationToken);

    /// <summary>
    /// Every projection-marker relationship of the organization (none, one, or several when tampered). <paramref name="atLeastAsFreshToken"/>
    /// is required for <see cref="MarkerReadConsistency.AtLeastAsFresh"/>. Throws <see cref="SpiceDbSchemaMissingException"/> when the
    /// datastore has no schema, <see cref="SpiceDbSnapshotException"/> when the returned messages disagree on their snapshot.
    /// </summary>
    Task<MarkerObservation> ReadMarkerAsync(Guid organizationId, MarkerReadConsistency consistency, string? atLeastAsFreshToken, CancellationToken cancellationToken);

    Task<bool> TupleExistsAsync(RelationshipTuple tuple, CancellationToken cancellationToken);

    /// <summary>
    /// at_exact_snapshot(the snapshot's pin) check. Only <see cref="ProjectionIntegrityVerifier"/> calls the decision reads, and
    /// only with an open snapshot; the response's <c>checked_at</c> must equal the pin (<see cref="SpiceDbSnapshotException"/> otherwise).
    /// </summary>
    Task<SpiceDbCheckOutcome> CheckAsync(SpiceDbCheck check, VerifiedProjectionSnapshot snapshot, CancellationToken cancellationToken);

    /// <summary>
    /// Bulk form of <see cref="CheckAsync"/>. IX-2 O-1: results are mapped by the request each result echoes (a mismatch or a
    /// count difference throws <see cref="SpiceDbUnavailableException"/>), and large calls are split at the same snapshot, never
    /// truncated. A per-item error is <see cref="SpiceDbCheckOutcome.Unknown"/> (existing behaviour, never allowed).
    /// </summary>
    Task<IReadOnlyList<SpiceDbCheckOutcome>> CheckBulkAsync(IReadOnlyList<SpiceDbCheck> checks, VerifiedProjectionSnapshot snapshot, CancellationToken cancellationToken);

    /// <summary>IX-2 O-1 strict form for V2 (IX-3): as <see cref="CheckBulkAsync"/>, and a per-item error fails the whole call (unavailable).</summary>
    Task<IReadOnlyList<SpiceDbCheckOutcome>> CheckBulkStrictAsync(IReadOnlyList<SpiceDbCheck> checks, VerifiedProjectionSnapshot snapshot, CancellationToken cancellationToken) =>
        throw new NotSupportedException("this SpiceDB projection does not implement the strict bulk check");

    /// <summary>
    /// Every resource the subject holds the permission on (unconditionally), at exactly the snapshot's pin, enumerated
    /// completely with SpiceDB cursors; every result's <c>looked_up_at</c> must equal the pin. Never truncates: more than
    /// <paramref name="maximumResults"/> distinct results throws <see cref="SpiceDbLookupLimitExceededException"/> (C-28).
    /// </summary>
    Task<IReadOnlyList<string>> LookupResourcesAsync(string resourceType, string permission, string subjectType, string subjectId, VerifiedProjectionSnapshot snapshot, int maximumResults, CancellationToken cancellationToken);

    Task<(bool Ready, string Detail)> ProbeAsync(CancellationToken cancellationToken);
}

public sealed class SpiceDbPreconditionFailedException(string detail) : Exception(detail);

/// <summary>A lookup would return more results than the caller accepts; nothing partial is returned.</summary>
public sealed class SpiceDbLookupLimitExceededException(string detail) : Exception(detail);

public sealed class SpiceDbUnavailableException(string detail, Exception? inner = null) : Exception(detail, inner);

/// <summary>The SpiceDB datastore has no schema (for example a replaced, freshly migrated datastore; Design B E4).</summary>
public sealed class SpiceDbSchemaMissingException(string detail, Exception? inner = null) : Exception(detail, inner);

public enum SpiceDbSnapshotFailure
{
    /// <summary>The pinned snapshot is no longer, or not yet, available (expired, future or malformed revision).</summary>
    Unavailable,

    /// <summary>SpiceDB reports that the pin references a different datastore instance (the datastore changed mid-request; E5).</summary>
    DatastoreChanged,

    /// <summary>A response's snapshot token differs from the pin (or messages of one read disagree): unexpected SpiceDB behaviour.</summary>
    TokenMismatch,
}

/// <summary>A decision read could not be proven to run at the pinned snapshot. Always fails closed.</summary>
public sealed class SpiceDbSnapshotException(SpiceDbSnapshotFailure failure, string detail, Exception? inner = null) : Exception(detail, inner)
{
    public SpiceDbSnapshotFailure Failure { get; } = failure;
}
