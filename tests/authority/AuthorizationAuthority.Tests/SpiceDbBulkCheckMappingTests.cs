using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Infrastructure.SpiceDb;
using Authzed.Api.V1;
using Xunit;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// IX-2, O-1 client hardening (R1 §A.3 O-1(a); P-S1-1 §4): results are mapped by the request each pair echoes; a count or echo
/// mismatch fails the call as unavailable; per-item errors are Unknown for existing callers and fail the call in strict mode;
/// large calls are chunked, never truncated. The server-side item maximum itself is contract test IX2-CT-07 (Docker).
/// </summary>
public sealed class SpiceDbBulkCheckMappingTests
{
    private static readonly SpiceDbCheck[] Checks =
    [
        new("form_record", "r1", "read", "user", "u1"),
        new("form_record", "r2", "read", "user", "u1"),
        new("field_item", "i1", "read", "user", "u1"),
    ];

    private static CheckBulkPermissionsRequestItem Echo(SpiceDbCheck check) => new()
    {
        Resource = new ObjectReference { ObjectType = check.ResourceType, ObjectId = check.ResourceId },
        Permission = check.Permission,
        Subject = new SubjectReference { Object = new ObjectReference { ObjectType = check.SubjectType, ObjectId = check.SubjectId } },
    };

    private static CheckBulkPermissionsPair Pair(SpiceDbCheck check, CheckPermissionResponse.Types.Permissionship permissionship) =>
        new() { Request = Echo(check), Item = new CheckBulkPermissionsResponseItem { Permissionship = permissionship } };

    private static CheckBulkPermissionsPair ErrorPair(SpiceDbCheck check) => new() { Request = Echo(check), Error = new Google.Rpc.Status { Code = 14, Message = "unavailable" } };

    [Fact]
    public void ResultsAreMappedToTheItemsTheyEcho()
    {
        var pairs = new[]
        {
            Pair(Checks[0], CheckPermissionResponse.Types.Permissionship.HasPermission),
            Pair(Checks[1], CheckPermissionResponse.Types.Permissionship.NoPermission),
            Pair(Checks[2], CheckPermissionResponse.Types.Permissionship.ConditionalPermission),
        };
        Assert.Equal([SpiceDbCheckOutcome.Allowed, SpiceDbCheckOutcome.Denied, SpiceDbCheckOutcome.Unknown], SpiceDbBulkCheckMapping.Map(Checks, pairs, BulkItemErrorPolicy.FailCall));
    }

    [Fact]
    public void AReorderedResponseFailsTheCallInsteadOfMisattributingADecision()
    {
        var pairs = new[]
        {
            Pair(Checks[1], CheckPermissionResponse.Types.Permissionship.HasPermission),
            Pair(Checks[0], CheckPermissionResponse.Types.Permissionship.NoPermission),
            Pair(Checks[2], CheckPermissionResponse.Types.Permissionship.NoPermission),
        };
        Assert.Throws<SpiceDbUnavailableException>(() => SpiceDbBulkCheckMapping.Map(Checks, pairs, BulkItemErrorPolicy.ReportUnknown));
    }

    [Theory]
    [InlineData("resource")]
    [InlineData("permission")]
    [InlineData("subject")]
    [InlineData("relation")]
    [InlineData("missing")]
    public void AnyEchoDifferenceFailsTheCall(string difference)
    {
        var pairs = Checks.Select(c => Pair(c, CheckPermissionResponse.Types.Permissionship.HasPermission)).ToArray();
        switch (difference)
        {
            case "resource": pairs[2].Request.Resource.ObjectId = "i2"; break;
            case "permission": pairs[2].Request.Permission = "edit"; break;
            case "subject": pairs[2].Request.Subject.Object.ObjectId = "u2"; break;
            case "relation": pairs[2].Request.Subject.OptionalRelation = "member"; break;
            default: pairs[2].Request = null; break;
        }

        Assert.Throws<SpiceDbUnavailableException>(() => SpiceDbBulkCheckMapping.Map(Checks, pairs, BulkItemErrorPolicy.ReportUnknown));
    }

    [Fact]
    public void ACountDifferenceFailsTheCall()
    {
        var pairs = Checks.Take(2).Select(c => Pair(c, CheckPermissionResponse.Types.Permissionship.HasPermission)).ToArray();
        Assert.Throws<SpiceDbUnavailableException>(() => SpiceDbBulkCheckMapping.Map(Checks, pairs, BulkItemErrorPolicy.ReportUnknown));
    }

    [Fact]
    public void APerItemErrorIsUnknownForExistingCallersAndFailsTheStrictCall()
    {
        var pairs = new[] { Pair(Checks[0], CheckPermissionResponse.Types.Permissionship.HasPermission), ErrorPair(Checks[1]), Pair(Checks[2], CheckPermissionResponse.Types.Permissionship.NoPermission) };
        Assert.Equal([SpiceDbCheckOutcome.Allowed, SpiceDbCheckOutcome.Unknown, SpiceDbCheckOutcome.Denied], SpiceDbBulkCheckMapping.Map(Checks, pairs, BulkItemErrorPolicy.ReportUnknown));
        Assert.Throws<SpiceDbUnavailableException>(() => SpiceDbBulkCheckMapping.Map(Checks, pairs, BulkItemErrorPolicy.FailCall));
    }

    [Fact]
    public void ChunkingSplitsWithoutDroppingOrReorderingItems()
    {
        var many = Enumerable.Range(0, 251).Select(i => new SpiceDbCheck("field_item", "i" + i, "read", "user", "u")).ToList();
        var chunks = SpiceDbBulkCheckMapping.Chunk(many, 100).ToList();
        Assert.Equal([100, 100, 51], chunks.Select(c => c.Count));
        Assert.Equal(many, chunks.SelectMany(c => c));
        Assert.Throws<ArgumentOutOfRangeException>(() => SpiceDbBulkCheckMapping.Chunk(many, 0).ToList());
    }
}
