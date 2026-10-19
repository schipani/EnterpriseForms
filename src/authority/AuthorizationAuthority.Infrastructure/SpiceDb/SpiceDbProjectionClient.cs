using System.Globalization;
using System.Reflection;
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
}

/// <summary>
/// Pure managed gRPC projection client. Every relationship mutation is a single WriteRelationships call carrying the
/// projection-cursor marker CAS (precondition on the expected old marker, delete old marker, touch new marker)
/// so a delayed stale worker cannot apply a write after the gate reopened (CROSS_SYSTEM §5).
/// </summary>
public sealed class SpiceDbProjectionClient : ISpiceDbProjection, IDisposable
{
    private const string CursorRelation = "projection_cursor";
    private readonly Lazy<(GrpcChannel Channel, PermissionsService.PermissionsServiceClient Permissions, SchemaService.SchemaServiceClient Schema, Metadata Headers)> connection;
    private readonly TimeSpan timeout;
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
        var (step, token) = await ReadCursorStepAsync(organizationId, cancellationToken).ConfigureAwait(false);
        if (step is not null)
        {
            return token;
        }

        var request = new WriteRelationshipsRequest();
        request.OptionalPreconditions.Add(new Precondition
        {
            Operation = Precondition.Types.Operation.MustNotMatch,
            Filter = new RelationshipFilter { ResourceType = ResourceTypeNames.Organization, OptionalResourceId = organizationId.ToString("D"), OptionalRelation = CursorRelation },
        });
        request.Updates.Add(new RelationshipUpdate { Operation = RelationshipUpdate.Types.Operation.Touch, Relationship = Cursor(organizationId, 0) });
        return (await WriteAsync(request, cancellationToken).ConfigureAwait(false)).WrittenAt.Token;
    }

    public async Task<string> ApplyDeltaAsync(Guid organizationId, long expectedStep, long newStep, IReadOnlyList<RelationshipDeltaEntry> delta, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(delta);
        var request = new WriteRelationshipsRequest();
        var oldMarker = Cursor(organizationId, expectedStep);
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
                OptionalSubjectFilter = new SubjectFilter { SubjectType = ResourceTypeNames.ProjectionStep, OptionalSubjectId = StepId(organizationId, newStep) },
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
        request.Updates.Add(new RelationshipUpdate { Operation = RelationshipUpdate.Types.Operation.Touch, Relationship = Cursor(organizationId, newStep) });
        return (await WriteAsync(request, cancellationToken).ConfigureAwait(false)).WrittenAt.Token;
    }

    public async Task<(long? Step, string ZedToken)> ReadCursorStepAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        try
        {
            using var call = Permissions.ReadRelationships(new ReadRelationshipsRequest
            {
                Consistency = new Consistency { FullyConsistent = true },
                RelationshipFilter = new RelationshipFilter { ResourceType = ResourceTypeNames.Organization, OptionalResourceId = organizationId.ToString("D"), OptionalRelation = CursorRelation },
            }, Options(cancellationToken));
            long? step = null;
            var token = string.Empty;
            await foreach (var response in call.ResponseStream.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                token = response.ReadAt.Token;
                var subjectId = response.Relationship.Subject.Object.ObjectId;
                var prefix = "step-" + organizationId.ToString("D") + "-";
                if (subjectId.StartsWith(prefix, StringComparison.Ordinal) && long.TryParse(subjectId[prefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                {
                    if (step is not null)
                    {
                        throw new SpiceDbPreconditionFailedException("multiple projection cursor markers present");
                    }

                    step = parsed;
                }
            }

            return (step, token);
        }
        catch (RpcException ex)
        {
            throw new SpiceDbUnavailableException("ReadRelationships failed: " + ex.Status.StatusCode, ex);
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

    public async Task<SpiceDbCheckOutcome> CheckAsync(SpiceDbCheck check, string zedToken, CancellationToken cancellationToken)
    {
        var outcomes = await CheckBulkAsync([check], zedToken, cancellationToken).ConfigureAwait(false);
        return outcomes[0];
    }

    public async Task<IReadOnlyList<SpiceDbCheckOutcome>> CheckBulkAsync(IReadOnlyList<SpiceDbCheck> checks, string zedToken, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(checks);
        if (string.IsNullOrEmpty(zedToken))
        {
            throw new SpiceDbUnavailableException("no authority-held ZedToken; projection not confirmed");
        }

        var request = new CheckBulkPermissionsRequest { Consistency = new Consistency { AtLeastAsFresh = new ZedToken { Token = zedToken } } };
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
            throw new SpiceDbUnavailableException("CheckBulkPermissions failed: " + ex.Status.StatusCode, ex);
        }
    }

    public async Task<IReadOnlyList<string>> LookupResourcesAsync(string resourceType, string permission, string subjectType, string subjectId, string zedToken, int limit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(zedToken))
        {
            throw new SpiceDbUnavailableException("no authority-held ZedToken; projection not confirmed");
        }

        try
        {
            using var call = Permissions.LookupResources(new LookupResourcesRequest
            {
                Consistency = new Consistency { AtLeastAsFresh = new ZedToken { Token = zedToken } },
                ResourceObjectType = resourceType,
                Permission = permission,
                Subject = new SubjectReference { Object = new ObjectReference { ObjectType = subjectType, ObjectId = subjectId } },
                OptionalLimit = (uint)limit,
            }, Options(cancellationToken));
            var ids = new List<string>();
            await foreach (var response in call.ResponseStream.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (response.Permissionship == LookupPermissionship.HasPermission)
                {
                    ids.Add(response.ResourceObjectId);
                }
            }

            return ids;
        }
        catch (RpcException ex)
        {
            throw new SpiceDbUnavailableException("LookupResources failed: " + ex.Status.StatusCode, ex);
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

    private static string StepId(Guid organizationId, long step) => "step-" + organizationId.ToString("D") + "-" + step.ToString(CultureInfo.InvariantCulture);

    private static Relationship Cursor(Guid organizationId, long step) => new()
    {
        Resource = new ObjectReference { ObjectType = ResourceTypeNames.Organization, ObjectId = organizationId.ToString("D") },
        Relation = CursorRelation,
        Subject = new SubjectReference { Object = new ObjectReference { ObjectType = ResourceTypeNames.ProjectionStep, ObjectId = StepId(organizationId, step) } },
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
