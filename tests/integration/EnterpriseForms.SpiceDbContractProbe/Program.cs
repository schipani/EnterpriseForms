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

if (args.Length >= 1 && args[0] is "ix5-lookup" or "ix5-perf")
{
    // IX-5 evidence plans P-1 (E-6) and P-2 (PERF-01), C-146: LookupSubjects on the deployed SpiceDB version. Separate mode, so
    // the CT-1..IX2-CT-07 contract run and its exit code are unchanged.
    return await Ix5LookupSubjectsAsync(args[0] == "ix5-perf");
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

// IX-5 P-1/P-2 (C-146). Fixture in a fresh random namespace: for N in 1, 10, 100, 500 a team of N members, a record owned by one
// user, a record_share whose readers are the team (via participant) and the team's first member directly, and a field_item of
// the record shared through it. Plus one field_item with no relationship. Checks the annex E §1 assumptions.
async Task<int> Ix5LookupSubjectsAsync(bool perf)
{
    var ns = Guid.NewGuid().ToString("N")[..12];
    string Id(string kind, int n, int i = -1) => i < 0 ? $"{kind}-{ns}-{n}" : $"{kind}-{ns}-{n}-{i}";
    var sizes = new[] { 1, 10, 100, 500 };
    RelationshipUpdate Upd(RelationshipUpdate.Types.Operation op, string type, string id, string relation, string subjectType, string subjectId, string subjectRelation = "") => new()
    {
        Operation = op,
        Relationship = new Relationship
        {
            Resource = new ObjectReference { ObjectType = type, ObjectId = id },
            Relation = relation,
            Subject = new SubjectReference { Object = new ObjectReference { ObjectType = subjectType, ObjectId = subjectId }, OptionalRelation = subjectRelation },
        },
    };
    var touch = RelationshipUpdate.Types.Operation.Touch;
    var write = new WriteRelationshipsRequest();
    foreach (var n in sizes)
    {
        for (var i = 0; i < n; i++)
        {
            write.Updates.Add(Upd(touch, "team", Id("t", n), "member", "user", Id("m", n, i)));
        }

        write.Updates.Add(Upd(touch, "form_record", Id("r", n), "owner", "user", Id("owner", n)));
        write.Updates.Add(Upd(touch, "record_share", Id("s", n), "reader", "team", Id("t", n), "participant"));
        write.Updates.Add(Upd(touch, "record_share", Id("s", n), "reader", "user", Id("m", n, 0)));
        write.Updates.Add(Upd(touch, "field_item", Id("f", n), "parent_record", "form_record", Id("r", n)));
        write.Updates.Add(Upd(touch, "field_item", Id("f", n), "shared_via", "record_share", Id("s", n)));
    }

    var pin = (await client.WriteRelationshipsAsync(write, new CallOptions(headers, DateTime.UtcNow.AddSeconds(60)))).WrittenAt.Token;
    var atPin = new Consistency { AtExactSnapshot = new ZedToken { Token = pin } };

    LookupSubjectsRequest Lookup(string fieldItem, Consistency consistency, string subjectType = "user", string subjectRelation = "") => new()
    {
        Consistency = consistency,
        Resource = new ObjectReference { ObjectType = "field_item", ObjectId = fieldItem },
        Permission = "read",
        SubjectObjectType = subjectType,
        OptionalSubjectRelation = subjectRelation,
        WildcardOption = LookupSubjectsRequest.Types.WildcardOption.ExcludeWildcards,
    };

    async Task<(List<LookupSubjectsResponse> Messages, string? Error)> RunAsync(LookupSubjectsRequest request, DateTime? deadline = null)
    {
        var messages = new List<LookupSubjectsResponse>();
        try
        {
            using var call = client.LookupSubjects(request, new CallOptions(headers, deadline ?? DateTime.UtcNow.AddSeconds(60)));
            await foreach (var message in call.ResponseStream.ReadAllAsync())
            {
                messages.Add(message);
            }

            return (messages, null);
        }
        catch (RpcException ex)
        {
            return (messages, ex.StatusCode + ": " + ex.Status.Detail);
        }
    }

    if (perf)
    {
        return await Ix5PerfAsync(sizes, n => Lookup(Id("f", n), atPin), RunAsync);
    }

    var lines = new List<(string Id, bool Pass, string Detail)>();

    // IX5-CT-08: optional_concrete_limit is refused (annex E: "currently unimplemented ... will return an error"); a cursor-less
    // request returns the full set. Pass = the server neither honours nor silently applies a limit.
    var limited = Lookup(Id("f", 100), atPin);
    limited.OptionalConcreteLimit = 5;
    var (limitedMessages, limitedError) = await RunAsync(limited);
    lines.Add(("IX5-CT-08", limitedError is not null && limitedMessages.Count == 0,
        $"limit=5 error=[{limitedError ?? "none"}] messages={limitedMessages.Count} cursor_present={limitedMessages.Any(m => m.AfterResultCursor is not null)}"));

    // IX5-CT-09: duplicates and permissionship. The first member is reachable directly and through the team. Records how often it
    // appears; pass = every message is HAS_PERMISSION and no subject appears with two different permissionships.
    var (dupMessages, dupError) = await RunAsync(Lookup(Id("f", 10), atPin));
    var dupCount = dupMessages.Count(m => m.Subject.SubjectObjectId == Id("m", 10, 0));
    var conflicting = dupMessages.GroupBy(m => m.Subject.SubjectObjectId).Any(g => g.Select(m => m.Subject.Permissionship).Distinct().Count() > 1);
    lines.Add(("IX5-CT-09", dupError is null && !conflicting && dupMessages.All(m => m.Subject.Permissionship == LookupPermissionship.HasPermission),
        $"dual_path_subject_occurrences={dupCount} messages={dupMessages.Count} distinct={dupMessages.Select(m => m.Subject.SubjectObjectId).Distinct().Count()} conflicting_permissionship={conflicting} error=[{dupError ?? "none"}]"));

    // IX5-CT-10: team expansion is complete for user lookups (N members + owner, distinct), every message's looked_up_at equals
    // the pin, no wildcard, and two calls return the same set.
    var expansion = new List<string>();
    var expansionPass = true;
    foreach (var n in sizes)
    {
        var (first, e1) = await RunAsync(Lookup(Id("f", n), atPin));
        var (second, e2) = await RunAsync(Lookup(Id("f", n), atPin));
        var distinct = first.Select(m => m.Subject.SubjectObjectId).ToHashSet(StringComparer.Ordinal);
        var expected = Enumerable.Range(0, n).Select(i => Id("m", n, i)).Append(Id("owner", n)).ToHashSet(StringComparer.Ordinal);
        var pinned = first.All(m => m.LookedUpAt?.Token == pin);
        var sameSet = distinct.SetEquals(second.Select(m => m.Subject.SubjectObjectId));
        var sameOrder = first.Select(m => m.Subject.SubjectObjectId).SequenceEqual(second.Select(m => m.Subject.SubjectObjectId));
        var ok = e1 is null && e2 is null && distinct.SetEquals(expected) && pinned && sameSet && !distinct.Contains("*");
        expansionPass &= ok;
        expansion.Add($"N={n}:messages={first.Count},distinct={distinct.Count},expected={expected.Count},pinned={pinned},same_set={sameSet},same_order={sameOrder}");
    }

    lines.Add(("IX5-CT-10", expansionPass, string.Join(' ', expansion)));

    // IX5-CT-11: a team-scoped lookup (subject type team, relation participant) returns the team itself, not its members, and
    // a user lookup never returns a team. Needed by B2's "no expansion of teams into individual members".
    var (teams, teamError) = await RunAsync(Lookup(Id("f", 500), atPin, "team", "participant"));
    var teamIds = teams.Select(m => m.Subject.SubjectObjectId).ToList();
    var (users500, _) = await RunAsync(Lookup(Id("f", 500), atPin));
    lines.Add(("IX5-CT-11", teamError is null && teamIds.Count == 1 && teamIds[0] == Id("t", 500) && users500.All(m => !m.Subject.SubjectObjectId.StartsWith("t-", StringComparison.Ordinal)),
        $"team_lookup_messages={teams.Count} returned=[{string.Join(',', teamIds.Select(t => t == Id("t", 500) ? "the-team" : "other"))}] error=[{teamError ?? "none"}]"));

    // IX5-CT-12: snapshot isolation across a revocation. Remove the team from the N=100 share; the pin still shows 101 users,
    // the new revision 2 (owner + direct reader), and fully consistent equals the new revision.
    var revoke = new WriteRelationshipsRequest();
    revoke.Updates.Add(Upd(RelationshipUpdate.Types.Operation.Delete, "record_share", Id("s", 100), "reader", "team", Id("t", 100), "participant"));
    var after = (await client.WriteRelationshipsAsync(revoke, Options())).WrittenAt.Token;
    var (beforeMessages, b) = await RunAsync(Lookup(Id("f", 100), atPin));
    var (afterMessages, a) = await RunAsync(Lookup(Id("f", 100), new Consistency { AtExactSnapshot = new ZedToken { Token = after } }));
    var (fullMessages, fc) = await RunAsync(Lookup(Id("f", 100), new Consistency { FullyConsistent = true }));
    int Distinct(List<LookupSubjectsResponse> m) => m.Select(x => x.Subject.SubjectObjectId).Distinct().Count();
    lines.Add(("IX5-CT-12", b is null && a is null && fc is null && Distinct(beforeMessages) == 101 && Distinct(afterMessages) == 2 && Distinct(fullMessages) == 2 && afterMessages.All(m => m.LookedUpAt?.Token == after),
        $"at_pin={Distinct(beforeMessages)} at_revocation={Distinct(afterMessages)} fully_consistent={Distinct(fullMessages)}"));

    // IX5-CT-13: an expired deadline ends the stream with an error status, never a silently truncated OK stream.
    var (deadlineMessages, deadlineError) = await RunAsync(Lookup(Id("f", 500), atPin), DateTime.UtcNow.AddMilliseconds(1));
    lines.Add(("IX5-CT-13", deadlineError is not null, $"error=[{deadlineError ?? "none"}] messages_before_error={deadlineMessages.Count}"));

    // IX5-CT-14: a field item with no relationship streams no message and ends OK.
    var (emptyMessages, emptyError) = await RunAsync(Lookup("f-" + ns + "-none", atPin));
    lines.Add(("IX5-CT-14", emptyError is null && emptyMessages.Count == 0, $"messages={emptyMessages.Count} error=[{emptyError ?? "none"}]"));

    foreach (var (id, pass, detail) in lines)
    {
        Console.WriteLine($"{id} {(pass ? "PASS" : "FAIL")} {detail}");
    }

    return lines.All(l => l.Pass) ? 0 : 1;
}

// P-2 (PERF-01, local only): 200 lookups per subject count, then 60 s sustained at concurrency 1, 4 and 16 on the N=100 item.
static async Task<int> Ix5PerfAsync(int[] sizes, Func<int, LookupSubjectsRequest> request, Func<LookupSubjectsRequest, DateTime?, Task<(List<LookupSubjectsResponse> Messages, string? Error)>> run)
{
    static double Q(List<double> s, double p) => s.Count == 0 ? double.NaN : s[Math.Min(s.Count - 1, (int)(p * s.Count))];
    foreach (var n in sizes)
    {
        var timings = new List<double>();
        var errors = 0;
        for (var i = 0; i < 200; i++)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var (_, error) = await run(request(n), null);
            timings.Add(watch.Elapsed.TotalMilliseconds);
            errors += error is null ? 0 : 1;
        }

        timings.Sort();
        Console.WriteLine(FormattableString.Invariant($"IX5-PERF latency subjects={n + 1} calls=200 errors={errors} p50_ms={Q(timings, 0.50):F2} p95_ms={Q(timings, 0.95):F2} p99_ms={Q(timings, 0.99):F2}"));
    }

    foreach (var concurrency in new[] { 1, 4, 16 })
    {
        var timings = new System.Collections.Concurrent.ConcurrentBag<double>();
        var errors = 0;
        var end = DateTime.UtcNow.AddSeconds(60);
        await Task.WhenAll(Enumerable.Range(0, concurrency).Select(async _ =>
        {
            while (DateTime.UtcNow < end)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var (_, error) = await run(request(100), null);
                timings.Add(watch.Elapsed.TotalMilliseconds);
                if (error is not null)
                {
                    Interlocked.Increment(ref errors);
                }
            }
        }));
        var sorted = timings.Order().ToList();
        Console.WriteLine(FormattableString.Invariant($"IX5-PERF sustained subjects=101 concurrency={concurrency} seconds=60 calls={sorted.Count} rate_per_s={sorted.Count / 60.0:F1} errors={errors} p50_ms={Q(sorted, 0.50):F2} p95_ms={Q(sorted, 0.95):F2}"));
    }

    return 0;
}

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
