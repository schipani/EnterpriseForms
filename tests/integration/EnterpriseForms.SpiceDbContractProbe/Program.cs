// C8 M4 S1 SpiceDB behavioural contract probe (Design B revision 2 §2.2, §2.11: CT-1..CT-6), plus IX-2 IX2-CT-07 (P-S1-1 §4
// O-1: the CheckBulkPermissions per-request item maximum). CT-7 stays reserved for S1 Design B §2.11 (C-129). Runs in a container on the
// security-test stack's authority network. Writes only relationships of a fresh random probe organization. Prints one line per
// contract ("CT-n PASS|FAIL detail"); exit code 0 only when all pass. Security-test stack only.
using System.Globalization;
using Authzed.Api.V1;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;

var endpoint = Environment.GetEnvironmentVariable("SPICEDB_ENDPOINT") ?? "http://spicedb:50051";
var key = Environment.GetEnvironmentVariable("SPICEDB_KEY") ?? throw new InvalidOperationException("SPICEDB_KEY is required");
using var channel = GrpcChannel.ForAddress(endpoint);
var client = new PermissionsService.PermissionsServiceClient(channel);
var headers = new Metadata { { "authorization", "Bearer " + key } };
CallOptions Options() => new(headers, DateTime.UtcNow.AddSeconds(10));

if (args.Length == 2 && args[0] == "marker")
{
    // Lists an organization's projection markers (fully consistent) for the rollback tests.
    foreach (var subject in await MarkersAsync(args[1], new Consistency { FullyConsistent = true }))
    {
        Console.WriteLine("MARKER " + subject);
    }

    return 0;
}

var org = Guid.NewGuid().ToString("D");
var results = new List<(string Id, bool Pass, string Detail)>();
void Record(string id, bool pass, string detail) => results.Add((id, pass, detail));

Relationship Rel(string relation, string subjectType, string subjectId) => new()
{
    Resource = new ObjectReference { ObjectType = "organization", ObjectId = org },
    Relation = relation,
    Subject = new SubjectReference { Object = new ObjectReference { ObjectType = subjectType, ObjectId = subjectId } },
};
string Step(long n) => "step-" + org + "-" + n.ToString(CultureInfo.InvariantCulture) + "-" + new string('0', 64);
RelationshipFilter MarkerFilter(string? subject = null) => new()
{
    ResourceType = "organization",
    OptionalResourceId = org,
    OptionalRelation = "projection_cursor",
    OptionalSubjectFilter = subject is null ? null : new SubjectFilter { SubjectType = "projection_step", OptionalSubjectId = subject },
};

async Task<List<string>> MarkersAsync(string organization, Consistency consistency)
{
    using var call = client.ReadRelationships(new ReadRelationshipsRequest { Consistency = consistency, RelationshipFilter = new RelationshipFilter { ResourceType = "organization", OptionalResourceId = organization, OptionalRelation = "projection_cursor" } }, Options());
    var subjects = new List<string>();
    await foreach (var response in call.ResponseStream.ReadAllAsync())
    {
        subjects.Add(response.Relationship.Subject.Object.ObjectId);
    }

    return subjects;
}

async Task<(int Messages, string? ReadAt)> ReadMarkerAsync(Consistency consistency, string organization)
{
    using var call = client.ReadRelationships(new ReadRelationshipsRequest { Consistency = consistency, RelationshipFilter = new RelationshipFilter { ResourceType = "organization", OptionalResourceId = organization, OptionalRelation = "projection_cursor" } }, Options());
    var count = 0;
    string? readAt = null;
    await foreach (var response in call.ResponseStream.ReadAllAsync())
    {
        count++;
        readAt ??= response.ReadAt.Token;
    }

    return (count, readAt);
}

CheckPermissionRequest Check(Consistency consistency, string user) => new()
{
    Consistency = consistency,
    Resource = new ObjectReference { ObjectType = "organization", ObjectId = org },
    Permission = "view_audit",
    Subject = new SubjectReference { Object = new ObjectReference { ObjectType = "user", ObjectId = user } },
};

// Setup: marker step 1 and one auditor, written together (the projector's shape).
var setup = new WriteRelationshipsRequest();
setup.OptionalPreconditions.Add(new Precondition { Operation = Precondition.Types.Operation.MustNotMatch, Filter = MarkerFilter() });
setup.Updates.Add(new RelationshipUpdate { Operation = RelationshipUpdate.Types.Operation.Touch, Relationship = Rel("projection_cursor", "projection_step", Step(1)) });
setup.Updates.Add(new RelationshipUpdate { Operation = RelationshipUpdate.Types.Operation.Touch, Relationship = Rel("security_auditor", "user", "auditor-" + org) });
var written = (await client.WriteRelationshipsAsync(setup, Options())).WrittenAt.Token;

// CT-1: a marker read at least as fresh as a token streams one message with read_at.
var (messages, s) = await ReadMarkerAsync(new Consistency { AtLeastAsFresh = new ZedToken { Token = written } }, org);
Record("CT-1", messages == 1 && !string.IsNullOrEmpty(s), $"messages={messages} read_at_present={!string.IsNullOrEmpty(s)}");
s ??= written;

// CT-2: a read that matches nothing streams no message (so no read_at).
var (none, noneReadAt) = await ReadMarkerAsync(new Consistency { FullyConsistent = true }, Guid.NewGuid().ToString("D"));
Record("CT-2", none == 0 && noneReadAt is null, $"messages={none}");

// CT-3: decision reads at at_exact_snapshot(S) echo S byte for byte.
var exact = new Consistency { AtExactSnapshot = new ZedToken { Token = s } };
var single = await client.CheckPermissionAsync(Check(exact, "auditor-" + org), Options());
var bulkRequest = new CheckBulkPermissionsRequest { Consistency = exact };
bulkRequest.Items.Add(new CheckBulkPermissionsRequestItem { Resource = new ObjectReference { ObjectType = "organization", ObjectId = org }, Permission = "view_audit", Subject = new SubjectReference { Object = new ObjectReference { ObjectType = "user", ObjectId = "auditor-" + org } } });
var bulk = await client.CheckBulkPermissionsAsync(bulkRequest, Options());
var lookedUp = new List<(string Id, string Token)>();
using (var lookup = client.LookupResources(new LookupResourcesRequest { Consistency = exact, ResourceObjectType = "organization", Permission = "view_audit", Subject = new SubjectReference { Object = new ObjectReference { ObjectType = "user", ObjectId = "auditor-" + org } } }, Options()))
{
    await foreach (var response in lookup.ResponseStream.ReadAllAsync())
    {
        lookedUp.Add((response.ResourceObjectId, response.LookedUpAt.Token));
    }
}

var ct3 = single.CheckedAt.Token == s && bulk.CheckedAt.Token == s && lookedUp.Count > 0 && lookedUp.All(l => l.Token == s) && lookedUp.Any(l => l.Id == org)
          && single.Permissionship == CheckPermissionResponse.Types.Permissionship.HasPermission;
Record("CT-3", ct3, $"check_echo={single.CheckedAt.Token == s} bulk_echo={bulk.CheckedAt.Token == s} lookup_results={lookedUp.Count} lookup_echo={lookedUp.All(l => l.Token == s)}");

// Token manipulation relies on SpiceDB's current (undocumented) ZedToken encoding; a format change fails CT-4/CT-5 loudly.
string? foreign = null, future = null, expired = null;
try
{
    foreign = RewriteToken(s, datastoreId: "00000000", revisionXid: null);
    future = RewriteToken(s, datastoreId: null, revisionXid: 9_000_000_000);
    expired = RewriteToken(s, datastoreId: null, revisionXid: 3);
}
catch (Exception ex)
{
    Record("CT-4", false, "ZedToken encoding not recognised: " + ex.Message);
    Record("CT-5", false, "ZedToken encoding not recognised: " + ex.Message);
}

if (foreign is not null && future is not null && expired is not null)
{
    // CT-4: at_least_as_fresh silently accepts a foreign-datastore and a future token (why S1 never trusts the token).
    var foreignFresh = await TryAsync(() => ReadMarkerAsync(new Consistency { AtLeastAsFresh = new ZedToken { Token = foreign } }, org));
    var futureFresh = await TryAsync(() => ReadMarkerAsync(new Consistency { AtLeastAsFresh = new ZedToken { Token = future } }, org));
    Record("CT-4", foreignFresh.Error is null && foreignFresh.Value.Messages == 1 && futureFresh.Error is null && futureFresh.Value.Messages == 1,
        $"foreign={foreignFresh.Error ?? "accepted"} future={futureFresh.Error ?? "accepted"}");

    // CT-5: at_exact_snapshot error classes (Design B §2.2 P6).
    var foreignExact = await TryAsync(() => ReadMarkerAsync(new Consistency { AtExactSnapshot = new ZedToken { Token = foreign } }, org));
    var futureExact = await TryAsync(() => ReadMarkerAsync(new Consistency { AtExactSnapshot = new ZedToken { Token = future } }, org));
    var expiredExact = await TryAsync(() => ReadMarkerAsync(new Consistency { AtExactSnapshot = new ZedToken { Token = expired } }, org));
    var malformedExact = await TryAsync(() => ReadMarkerAsync(new Consistency { AtExactSnapshot = new ZedToken { Token = "bm90LWEtdG9rZW4=" } }, org));
    var ct5 = (foreignExact.Error ?? string.Empty).Contains("different datastore instance", StringComparison.Ordinal)
              && (futureExact.Error ?? string.Empty).StartsWith("OutOfRange", StringComparison.Ordinal)
              && (expiredExact.Error ?? string.Empty).StartsWith("OutOfRange", StringComparison.Ordinal)
              && (malformedExact.Error ?? string.Empty).StartsWith("InvalidArgument", StringComparison.Ordinal);
    Record("CT-5", ct5, $"foreign=[{foreignExact.Error}] future=[{futureExact.Error}] expired=[{expiredExact.Error}] malformed=[{malformedExact.Error}]");
}

// CT-6: a marker CAS written together with a relationship change is atomic under concurrent snapshot reads.
var violations = 0;
var observations = 0;
var writerDone = false;
var step = 1L;
var writer = Task.Run(async () =>
{
    for (var i = 0; i < 60; i++)
    {
        var request = new WriteRelationshipsRequest();
        request.OptionalPreconditions.Add(new Precondition { Operation = Precondition.Types.Operation.MustMatch, Filter = MarkerFilter(Step(step)) });
        request.Updates.Add(new RelationshipUpdate { Operation = RelationshipUpdate.Types.Operation.Delete, Relationship = Rel("projection_cursor", "projection_step", Step(step)) });
        request.Updates.Add(new RelationshipUpdate { Operation = RelationshipUpdate.Types.Operation.Touch, Relationship = Rel("projection_cursor", "projection_step", Step(step + 1)) });
        // Odd steps carry the toggled grant, even steps do not.
        request.Updates.Add(new RelationshipUpdate { Operation = (step + 1) % 2 == 1 ? RelationshipUpdate.Types.Operation.Touch : RelationshipUpdate.Types.Operation.Delete, Relationship = Rel("security_auditor", "user", "toggled-" + org) });
        await client.WriteRelationshipsAsync(request, Options());
        step++;
    }

    writerDone = true;
});
var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
{
    while (!writerDone)
    {
        using var call = client.ReadRelationships(new ReadRelationshipsRequest { Consistency = new Consistency { FullyConsistent = true }, RelationshipFilter = MarkerFilter() }, Options());
        var markers = new List<(string Subject, string Token)>();
        await foreach (var response in call.ResponseStream.ReadAllAsync())
        {
            markers.Add((response.Relationship.Subject.Object.ObjectId, response.ReadAt.Token));
        }

        if (markers.Count != 1)
        {
            Interlocked.Increment(ref violations);
            continue;
        }

        var parts = markers[0].Subject.Split('-');
        var n = long.Parse(parts[^2], CultureInfo.InvariantCulture);
        var granted = await client.CheckPermissionAsync(Check(new Consistency { AtExactSnapshot = new ZedToken { Token = markers[0].Token } }, "toggled-" + org), Options());
        var has = granted.Permissionship == CheckPermissionResponse.Types.Permissionship.HasPermission;
        Interlocked.Increment(ref observations);
        if (has != (n % 2 == 1 && n > 1))
        {
            Interlocked.Increment(ref violations);
        }
    }
})).ToArray();
await Task.WhenAll(readers.Append(writer));
Record("CT-6", violations == 0 && observations > 20, $"observations={observations} violations={violations} final_step={step}");

// IX2-CT-07 (IX-2, O-1): the per-request item maximum of CheckBulkPermissions. Every accepted request returns exactly one result per
// item, each echoing the issued item at its position (no truncation, no reordering); a request above the maximum is refused with
// an error status. The Authority's configured chunk size (BULK_CHECK_CAP, default 100) must be accepted.
var cap = int.Parse(Environment.GetEnvironmentVariable("BULK_CHECK_CAP") ?? "100", CultureInfo.InvariantCulture);
using var bulkChannel = GrpcChannel.ForAddress(endpoint, new GrpcChannelOptions { MaxReceiveMessageSize = 256 * 1024 * 1024, MaxSendMessageSize = 256 * 1024 * 1024 });
var bulkClient = new PermissionsService.PermissionsServiceClient(bulkChannel);
var bulkExact = new Consistency { AtExactSnapshot = new ZedToken { Token = written } };
async Task<(bool Accepted, bool Complete, string Detail)> BulkAsync(int size)
{
    var request = new CheckBulkPermissionsRequest { Consistency = bulkExact };
    for (var i = 0; i < size; i++)
    {
        request.Items.Add(new CheckBulkPermissionsRequestItem { Resource = new ObjectReference { ObjectType = "organization", ObjectId = org }, Permission = "view_audit", Subject = new SubjectReference { Object = new ObjectReference { ObjectType = "user", ObjectId = "ct7-" + i.ToString(CultureInfo.InvariantCulture) } } });
    }

    try
    {
        var response = await bulkClient.CheckBulkPermissionsAsync(request, new CallOptions(headers, DateTime.UtcNow.AddSeconds(60)));
        var complete = response.Pairs.Count == size && response.Pairs.Select((p, i) => p.Request.Subject.Object.ObjectId == "ct7-" + i.ToString(CultureInfo.InvariantCulture) && p.Request.Permission == "view_audit" && p.ResponseCase == CheckBulkPermissionsPair.ResponseOneofCase.Item).All(ok => ok);
        return (true, complete, $"pairs={response.Pairs.Count}");
    }
    catch (RpcException ex)
    {
        return (false, false, ex.StatusCode + ": " + ex.Status.Detail);
    }
}

var capResult = await BulkAsync(cap);
int lastAccepted = 0, firstRefused = 0;
var allComplete = capResult.Accepted && capResult.Complete;
string refusal = "none";
for (var size = 100; size <= 51_200; size *= 2)
{
    var outcome = await BulkAsync(size);
    if (!outcome.Accepted)
    {
        firstRefused = size;
        refusal = outcome.Detail;
        break;
    }

    allComplete &= outcome.Complete;
    lastAccepted = size;
}

if (firstRefused > 0 && lastAccepted > 0)
{
    // Binary search between the last accepted and the first refused size.
    while (firstRefused - lastAccepted > 1)
    {
        var middle = lastAccepted + ((firstRefused - lastAccepted) / 2);
        var outcome = await BulkAsync(middle);
        if (outcome.Accepted)
        {
            allComplete &= outcome.Complete;
            lastAccepted = middle;
        }
        else
        {
            firstRefused = middle;
            refusal = outcome.Detail;
        }
    }
}

Record("IX2-CT-07", allComplete && cap <= (lastAccepted == 0 ? 0 : lastAccepted) && (firstRefused == 0 || firstRefused > cap),
    $"configured_cap={cap} cap_accepted={capResult.Accepted} every_accepted_complete={allComplete} maximum_accepted={lastAccepted} first_refused={(firstRefused == 0 ? "none up to 51200" : firstRefused.ToString(CultureInfo.InvariantCulture))} refusal=[{refusal}]");

foreach (var (id, pass, detail) in results)
{
    Console.WriteLine($"{id} {(pass ? "PASS" : "FAIL")} {detail}");
}

return results.All(r => r.Pass) && results.Count == 7 ? 0 : 1;

static async Task<(T Value, string? Error)> TryAsync<T>(Func<Task<T>> action)
{
    try
    {
        return (await action().ConfigureAwait(false), null);
    }
    catch (RpcException ex)
    {
        return (default!, ex.StatusCode + ": " + ex.Status.Detail);
    }
}

// ZedToken (v1.56 encoding): base64(proto{ field 3: { field 1: revision string, field 2: datastore id string } }), the
// Postgres revision string being base64(proto{ field 1: xid varint, ... }).
static string RewriteToken(string token, string? datastoreId, long? revisionXid)
{
    var outer = CodedInputRead(Convert.FromBase64String(token));
    var inner = CodedInputRead(outer[3]);
    var revision = inner[1];
    var datastore = inner[2];
    if (revisionXid is { } xid)
    {
        var bytes = new MemoryStream();
        var output = new CodedOutputStream(bytes);
        output.WriteTag(1, WireFormat.WireType.Varint);
        output.WriteUInt64((ulong)xid);
        output.Flush();
        revision = System.Text.Encoding.ASCII.GetBytes(Convert.ToBase64String(bytes.ToArray()));
    }

    if (datastoreId is not null)
    {
        datastore = System.Text.Encoding.ASCII.GetBytes(datastoreId);
    }

    var innerBytes = new MemoryStream();
    var innerOut = new CodedOutputStream(innerBytes);
    innerOut.WriteTag(1, WireFormat.WireType.LengthDelimited);
    innerOut.WriteBytes(ByteString.CopyFrom(revision));
    innerOut.WriteTag(2, WireFormat.WireType.LengthDelimited);
    innerOut.WriteBytes(ByteString.CopyFrom(datastore));
    innerOut.Flush();
    var outerBytes = new MemoryStream();
    var outerOut = new CodedOutputStream(outerBytes);
    outerOut.WriteTag(3, WireFormat.WireType.LengthDelimited);
    outerOut.WriteBytes(ByteString.CopyFrom(innerBytes.ToArray()));
    outerOut.Flush();
    return Convert.ToBase64String(outerBytes.ToArray());
}

static Dictionary<int, byte[]> CodedInputRead(byte[] data)
{
    var fields = new Dictionary<int, byte[]>();
    var input = new CodedInputStream(data);
    uint tag;
    while ((tag = input.ReadTag()) != 0)
    {
        var number = WireFormat.GetTagFieldNumber(tag);
        if (WireFormat.GetTagWireType(tag) != WireFormat.WireType.LengthDelimited)
        {
            throw new FormatException("unexpected wire type in ZedToken field " + number);
        }

        fields[number] = input.ReadBytes().ToByteArray();
    }

    return fields;
}
