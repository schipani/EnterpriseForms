using System.Reflection;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Domain.Features.SecurityCommands;
using Authzed.Api.V1;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Options;

namespace AuthorizationAuthority.Infrastructure.SpiceDb;

public sealed class SpiceDbOptions
{
    public const string SectionName = "SpiceDb";

    /// <summary>host:port of the SpiceDB gRPC endpoint on the authority network.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Pre-shared key from the authority's secret configuration. Only the authority holds it.</summary>
    public string? PresharedKey { get; set; }

    public bool UseTls { get; set; }

    public int TimeoutSeconds { get; set; } = 5;

    /// <summary>Results per LookupResources page; enumeration continues with cursors until exhausted (C-28).</summary>
    public int LookupPageSize { get; set; } = 1000;
}

/// <summary>
/// Pure managed gRPC projection client. Every relationship mutation is a single WriteRelationships call carrying the
/// projection-cursor marker CAS (precondition on the expected old marker, delete old marker, touch new marker)
/// so a delayed stale worker cannot apply a write after the gate reopened (CROSS_SYSTEM §5). Markers are history-bound
/// (<see cref="ProjectionMarker"/>, C8 M4 S1). Decision reads run at exactly the snapshot's pin and prove it through the
/// response token; the Authority-held ZedToken is only a freshness selector for the pin read, never integrity evidence.
/// </summary>
public sealed class SpiceDbProjectionClient : ISpiceDbProjection, IDisposable
{
    private const string CursorRelation = "projection_cursor";
    private readonly Lazy<(GrpcChannel Channel, PermissionsService.PermissionsServiceClient Permissions, SchemaService.SchemaServiceClient Schema, Metadata Headers)> connection;
    private readonly TimeSpan timeout;
    private readonly int lookupPageSize;
    private readonly string? configurationError;

    public SpiceDbProjectionClient(IOptions<SpiceDbOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var value = options.Value;
        if (string.IsNullOrWhiteSpace(value.Endpoint) || string.IsNullOrWhiteSpace(value.PresharedKey))
        {
            configurationError = "SpiceDb:Endpoint and SpiceDb:PresharedKey are not configured; every projection operation fails closed.";
        }

        timeout = TimeSpan.FromSeconds(Math.Max(1, value.TimeoutSeconds));
        lookupPageSize = Math.Clamp(value.LookupPageSize, 1, 10_000);
        connection = new Lazy<(GrpcChannel, PermissionsService.PermissionsServiceClient, SchemaService.SchemaServiceClient, Metadata)>(() =>
        {
            if (configurationError is not null)
            {
                throw new SpiceDbUnavailableException(configurationError);
            }

            var channel = GrpcChannel.ForAddress((value.UseTls ? "https://" : "http://") + value.Endpoint);
            return (channel, new PermissionsService.PermissionsServiceClient(channel), new SchemaService.SchemaServiceClient(channel), new Metadata { { "authorization", "Bearer " + value.PresharedKey } });
        });
    }

    private PermissionsService.PermissionsServiceClient Permissions => connection.Value.Permissions;

    private SchemaService.SchemaServiceClient Schema => connection.Value.Schema;

    private Metadata Headers => connection.Value.Headers;

    public static string EmbeddedSchema()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("authority.schema.zed") ?? throw new InvalidOperationException("embedded schema missing");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Schema.WriteSchemaAsync(new WriteSchemaRequest { Schema = EmbeddedSchema() }, Options(cancellationToken)).ConfigureAwait(false);
        }
        catch (RpcException ex)
        {
            throw new SpiceDbUnavailableException("WriteSchema failed: " + ex.Status.StatusCode, ex);
        }
    }

    public async Task<string> InitializeCursorAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        var existing = await ReadMarkerAsync(organizationId, MarkerReadConsistency.FullyConsistent, null, cancellationToken).ConfigureAwait(false);
        if (existing.MarkerSubjectIds.Count > 0)
        {
            return existing.ReadAt ?? string.Empty;
        }

        var request = new WriteRelationshipsRequest();
        request.OptionalPreconditions.Add(new Precondition
        {
            Operation = Precondition.Types.Operation.MustNotMatch,
            Filter = new RelationshipFilter { ResourceType = ResourceTypeNames.Organization, OptionalResourceId = organizationId.ToString("D"), OptionalRelation = CursorRelation },
        });
        request.Updates.Add(new RelationshipUpdate { Operation = RelationshipUpdate.Types.Operation.Touch, Relationship = Cursor(organizationId, ProjectionMarker.SubjectId(organizationId, 0, null)) });
        return (await WriteAsync(request, cancellationToken).ConfigureAwait(false)).WrittenAt.Token;
    }

    public async Task<string> ApplyDeltaAsync(Guid organizationId, string expectedMarkerSubjectId, string newMarkerSubjectId, IReadOnlyList<RelationshipDeltaEntry> delta, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delta);
        var request = new WriteRelationshipsRequest();
        var oldMarker = Cursor(organizationId, expectedMarkerSubjectId);
        request.OptionalPreconditions.Add(new Precondition
        {
            Operation = Precondition.Types.Operation.MustMatch,
            Filter = new RelationshipFilter
            {
                ResourceType = ResourceTypeNames.Organization,
                OptionalResourceId = organizationId.ToString("D"),
                OptionalRelation = CursorRelation,
                OptionalSubjectFilter = new SubjectFilter { SubjectType = ResourceTypeNames.ProjectionStep, OptionalSubjectId = oldMarker.Subject.Object.ObjectId },
            },
        });
        request.OptionalPreconditions.Add(new Precondition
        {
            Operation = Precondition.Types.Operation.MustNotMatch,
            Filter = new RelationshipFilter
            {
                ResourceType = ResourceTypeNames.Organization,
                OptionalResourceId = organizationId.ToString("D"),
                OptionalRelation = CursorRelation,
                OptionalSubjectFilter = new SubjectFilter { SubjectType = ResourceTypeNames.ProjectionStep, OptionalSubjectId = newMarkerSubjectId },
            },
        });

        foreach (var entry in delta)
        {
            request.Updates.Add(new RelationshipUpdate
            {
                Operation = entry.Operation == RelationshipDeltaOperations.Touch ? RelationshipUpdate.Types.Operation.Touch : RelationshipUpdate.Types.Operation.Delete,
                Relationship = ToRelationship(entry.Tuple),
            });
        }

        request.Updates.Add(new RelationshipUpdate { Operation = RelationshipUpdate.Types.Operation.Delete, Relationship = oldMarker });
        request.Updates.Add(new RelationshipUpdate { Operation = RelationshipUpdate.Types.Operation.Touch, Relationship = Cursor(organizationId, newMarkerSubjectId) });
        return (await WriteAsync(request, cancellationToken).ConfigureAwait(false)).WrittenAt.Token;
    }

    public async Task<MarkerObservation> ReadMarkerAsync(Guid organizationId, MarkerReadConsistency consistency, string? atLeastAsFreshToken, CancellationToken cancellationToken)
    {
        if (consistency == MarkerReadConsistency.AtLeastAsFresh && string.IsNullOrEmpty(atLeastAsFreshToken))
        {
            throw new SpiceDbUnavailableException("no authority-held ZedToken; projection not confirmed");
        }

        try
        {
            using var call = Permissions.ReadRelationships(new ReadRelationshipsRequest
            {
                Consistency = consistency == MarkerReadConsistency.FullyConsistent ? new Consistency { FullyConsistent = true } : new Consistency { AtLeastAsFresh = new ZedToken { Token = atLeastAsFreshToken } },
                RelationshipFilter = new RelationshipFilter { ResourceType = ResourceTypeNames.Organization, OptionalResourceId = organizationId.ToString("D"), OptionalRelation = CursorRelation },
            }, Options(cancellationToken));
            var subjects = new List<string>();
            string? readAt = null;
            await foreach (var response in call.ResponseStream.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (readAt is not null && !string.Equals(readAt, response.ReadAt.Token, StringComparison.Ordinal))
                {
                    throw new SpiceDbSnapshotException(SpiceDbSnapshotFailure.TokenMismatch, "marker read messages disagree on their snapshot");
                }

                readAt = response.ReadAt.Token;
                var subject = response.Relationship.Subject.Object;
                subjects.Add(subject.ObjectType == ResourceTypeNames.ProjectionStep ? subject.ObjectId : "?" + subject.ObjectType);
            }

            return new MarkerObservation(subjects, readAt);
        }
        catch (RpcException ex)
        {
            throw Classify(ex, "ReadRelationships");
        }
    }

    public async Task<bool> TupleExistsAsync(RelationshipTuple tuple, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tuple);
        try
        {
            using var call = Permissions.ReadRelationships(new ReadRelationshipsRequest
            {
                Consistency = new Consistency { FullyConsistent = true },
                RelationshipFilter = new RelationshipFilter
                {
                    ResourceType = tuple.ResourceType,
                    OptionalResourceId = tuple.ResourceId,
                    OptionalRelation = tuple.Relation,
                    OptionalSubjectFilter = new SubjectFilter
                    {
                        SubjectType = tuple.SubjectType,
                        OptionalSubjectId = tuple.SubjectId,
                        OptionalRelation = new SubjectFilter.Types.RelationFilter { Relation = tuple.SubjectRelation },
                    },
                },
            }, Options(cancellationToken));
            await foreach (var _ in call.ResponseStream.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            return false;
        }
        catch (RpcException ex)
        {
            throw new SpiceDbUnavailableException("ReadRelationships failed: " + ex.Status.StatusCode, ex);
        }
    }

    public async Task<SpiceDbCheckOutcome> CheckAsync(SpiceDbCheck check, VerifiedProjectionSnapshot snapshot, CancellationToken cancellationToken)
    {
        var outcomes = await CheckBulkAsync([check], snapshot, cancellationToken).ConfigureAwait(false);
        return outcomes[0];
    }

    public async Task<IReadOnlyList<SpiceDbCheckOutcome>> CheckBulkAsync(IReadOnlyList<SpiceDbCheck> checks, VerifiedProjectionSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(checks);
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.RequireOpen();
        var request = new CheckBulkPermissionsRequest { Consistency = new Consistency { AtExactSnapshot = new ZedToken { Token = snapshot.Pin } } };
        foreach (var check in checks)
        {
            request.Items.Add(new CheckBulkPermissionsRequestItem
            {
                Resource = new ObjectReference { ObjectType = check.ResourceType, ObjectId = check.ResourceId },
                Permission = check.Permission,
                Subject = new SubjectReference { Object = new ObjectReference { ObjectType = check.SubjectType, ObjectId = check.SubjectId } },
            });
        }

        try
        {
            var response = await Permissions.CheckBulkPermissionsAsync(request, Options(cancellationToken)).ConfigureAwait(false);
            if (!string.Equals(response.CheckedAt?.Token, snapshot.Pin, StringComparison.Ordinal))
            {
                throw new SpiceDbSnapshotException(SpiceDbSnapshotFailure.TokenMismatch, "checked_at differs from the pinned snapshot");
            }

            var outcomes = new List<SpiceDbCheckOutcome>(checks.Count);
            foreach (var pair in response.Pairs)
            {
                if (pair.ResponseCase != CheckBulkPermissionsPair.ResponseOneofCase.Item)
                {
                    outcomes.Add(SpiceDbCheckOutcome.Unknown);
                    continue;
                }

                outcomes.Add(pair.Item.Permissionship switch
                {
                    CheckPermissionResponse.Types.Permissionship.HasPermission => SpiceDbCheckOutcome.Allowed,
                    CheckPermissionResponse.Types.Permissionship.NoPermission => SpiceDbCheckOutcome.Denied,
                    _ => SpiceDbCheckOutcome.Unknown,
                });
            }

            if (outcomes.Count != checks.Count)
            {
                throw new SpiceDbUnavailableException("bulk check returned a different number of results");
            }

            return outcomes;
        }
        catch (RpcException ex)
        {
            throw Classify(ex, "CheckBulkPermissions");
        }
    }

    public async Task<IReadOnlyList<string>> LookupResourcesAsync(string resourceType, string permission, string subjectType, string subjectId, VerifiedProjectionSnapshot snapshot, int maximumResults, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.RequireOpen();

        // Cursor-paged (C-28). Every page repeats the same request (SpiceDB binds a cursor to the request, consistency
        // included); a cursor also carries the snapshot revision of the first page, so the pages form one consistent view.
        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Cursor? cursor = null;
        try
        {
            while (true)
            {
                var request = new LookupResourcesRequest
                {
                    Consistency = new Consistency { AtExactSnapshot = new ZedToken { Token = snapshot.Pin } },
                    ResourceObjectType = resourceType,
                    Permission = permission,
                    Subject = new SubjectReference { Object = new ObjectReference { ObjectType = subjectType, ObjectId = subjectId } },
                    OptionalLimit = (uint)lookupPageSize,
                };
                if (cursor is not null)
                {
                    request.OptionalCursor = cursor;
                }

                var received = 0;
                using (var call = Permissions.LookupResources(request, Options(cancellationToken)))
                {
                    await foreach (var response in call.ResponseStream.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                    {
                        received++;
                        if (!string.Equals(response.LookedUpAt?.Token, snapshot.Pin, StringComparison.Ordinal))
                        {
                            throw new SpiceDbSnapshotException(SpiceDbSnapshotFailure.TokenMismatch, "looked_up_at differs from the pinned snapshot");
                        }

                        cursor = response.AfterResultCursor;
                        // Conditional (caveated) permission is not permission: it is never listed (fail closed).
                        if (response.Permissionship == LookupPermissionship.HasPermission && seen.Add(response.ResourceObjectId))
                        {
                            ids.Add(response.ResourceObjectId);
                            if (ids.Count > maximumResults)
                            {
                                throw new SpiceDbLookupLimitExceededException($"more than {maximumResults} {resourceType} results");
                            }
                        }
                    }
                }

                if (received < lookupPageSize)
                {
                    return ids;
                }

                if (cursor is null)
                {
                    throw new SpiceDbUnavailableException("LookupResources returned a full page without a cursor");
                }
            }
        }
        catch (RpcException ex)
        {
            throw Classify(ex, "LookupResources");
        }
    }

    public async Task<(bool Ready, string Detail)> ProbeAsync(CancellationToken cancellationToken)
    {
        if (configurationError is not null)
        {
            return (false, "SpiceDbUnconfigured");
        }

        try
        {
            var response = await Schema.ReadSchemaAsync(new ReadSchemaRequest(), Options(cancellationToken)).ConfigureAwait(false);
            return response.SchemaText.Contains("definition projection_step", StringComparison.Ordinal) ? (true, "SpiceDB schema present") : (false, "SpiceDbSchemaMissing");
        }
        catch (RpcException ex)
        {
            return (false, "SpiceDbUnavailable:" + ex.Status.StatusCode);
        }
    }

    public void Dispose()
    {
        if (connection.IsValueCreated)
        {
            connection.Value.Channel.Dispose();
        }
    }

    private async Task<WriteRelationshipsResponse> WriteAsync(WriteRelationshipsRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return await Permissions.WriteRelationshipsAsync(request, Options(cancellationToken)).ConfigureAwait(false);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.FailedPrecondition)
        {
            throw new SpiceDbPreconditionFailedException(ex.Status.Detail);
        }
        catch (RpcException ex)
        {
            throw new SpiceDbUnavailableException("WriteRelationships failed: " + ex.Status.StatusCode, ex);
        }
    }

    private CallOptions Options(CancellationToken cancellationToken) => new(Headers, DateTime.UtcNow.Add(timeout), cancellationToken);

    /// <summary>
    /// Failure classes characterised on SpiceDB v1.56.2 (Design B §2.2 P6, E4, E5; contract tests CT-5/CT-7): a pin from a
    /// different datastore instance (<c>Unknown</c>), an expired, future or malformed revision (<c>OutOfRange</c>,
    /// <c>InvalidArgument</c>), a datastore without schema (<c>FailedPrecondition</c> "... not found"). Anything else is an outage.
    /// </summary>
    private static Exception Classify(RpcException ex, string operation)
    {
        var detail = ex.Status.Detail ?? string.Empty;
        if (detail.Contains("different datastore instance", StringComparison.OrdinalIgnoreCase))
        {
            return new SpiceDbSnapshotException(SpiceDbSnapshotFailure.DatastoreChanged, operation + ": the pinned snapshot belongs to a different datastore instance", ex);
        }

        if (ex.StatusCode is StatusCode.OutOfRange || (ex.StatusCode is StatusCode.InvalidArgument && detail.Contains("revision", StringComparison.OrdinalIgnoreCase)))
        {
            return new SpiceDbSnapshotException(SpiceDbSnapshotFailure.Unavailable, operation + ": pinned snapshot unavailable (" + ex.Status.StatusCode + ")", ex);
        }

        if (ex.StatusCode is StatusCode.FailedPrecondition && detail.Contains("not found", StringComparison.OrdinalIgnoreCase) && detail.Contains("definition", StringComparison.OrdinalIgnoreCase))
        {
            return new SpiceDbSchemaMissingException(operation + ": the datastore has no schema", ex);
        }

        return new SpiceDbUnavailableException(operation + " failed: " + ex.Status.StatusCode, ex);
    }

    private static Relationship Cursor(Guid organizationId, string markerSubjectId) => new()
    {
        Resource = new ObjectReference { ObjectType = ResourceTypeNames.Organization, ObjectId = organizationId.ToString("D") },
        Relation = CursorRelation,
        Subject = new SubjectReference { Object = new ObjectReference { ObjectType = ResourceTypeNames.ProjectionStep, ObjectId = markerSubjectId } },
    };

    private static Relationship ToRelationship(RelationshipTuple tuple) => new()
    {
        Resource = new ObjectReference { ObjectType = tuple.ResourceType, ObjectId = tuple.ResourceId },
        Relation = tuple.Relation,
        Subject = new SubjectReference
        {
            Object = new ObjectReference { ObjectType = tuple.SubjectType, ObjectId = tuple.SubjectId },
            OptionalRelation = tuple.SubjectRelation,
        },
    };
}
