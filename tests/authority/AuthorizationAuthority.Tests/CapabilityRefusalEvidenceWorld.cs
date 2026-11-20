using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Application.Features.Journal;
using AuthorizationAuthority.Application.Features.Journal.Verification;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Organizations;

namespace AuthorizationAuthority.Tests;

/// <summary>A controllable clock: wall time and the monotonic timestamp move together, only when told to.</summary>
public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private long ticks;

    public DateTimeOffset Now { get; private set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public override long GetTimestamp() => Interlocked.Read(ref ticks);

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public void Advance(TimeSpan by)
    {
        Now = Now.Add(by);
        Interlocked.Add(ref ticks, by.Ticks);
    }
}

/// <summary>How the next commit behaves (one shot).</summary>
public enum EvidenceCommitBehavior
{
    Normal,

    /// <summary>The commit fails and nothing takes effect (an ambiguous outcome that did not commit).</summary>
    FailBeforeEffect,

    /// <summary>The commit takes effect, then the client sees a failure (an ambiguous outcome that did commit).</summary>
    FailAfterEffect,
}

/// <summary>
/// E3-M2f-3b test double of AuthorityDb for the evidence transaction T2: organization rows with a real row lock (one holder at a time,
/// held from <c>LockOrganizationAsync</c> until the scope is disposed), transactional journal appends and organization updates (applied
/// only on commit, discarded otherwise), an emulated <c>clock_timestamp()</c>, the index check, failure injection, and a log of every
/// call. Any method the recorder must not use throws and is recorded in <see cref="Unexpected"/>.
/// </summary>
public sealed class CapabilityRefusalEvidenceWorld
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> rowLocks = new();
    private int scopeSequence;

    public CapabilityRefusalEvidenceWorld(DateTimeOffset? start = null)
    {
        Clock = new ManualClock(start ?? new DateTimeOffset(2026, 11, 17, 10, 15, 0, TimeSpan.Zero));
        var store = DispatchProxy.Create<IAuthorityStore, EvidenceStoreProxy>();
        ((EvidenceStoreProxy)(object)store).World = this;
        Store = store;
    }

    public ManualClock Clock { get; }

    public IAuthorityStore Store { get; }

    /// <summary>When set, the emulated AuthorityDb clock; otherwise the manual clock's wall time.</summary>
    public DateTimeOffset? DatabaseTime { get; set; }

    public bool IndexUsable { get; set; } = true;

    public EvidenceCommitBehavior NextCommit { get; set; }

    /// <summary>Delay of the lookup statement (to exercise the budget).</summary>
    public TimeSpan LookupDelay { get; set; }

    public ConcurrentDictionary<string, Exception> FailOn { get; } = new();

    public ConcurrentDictionary<Guid, OrganizationAuthorizationState> Organizations { get; } = new();

    public List<SecurityJournalEntry> Journal { get; } = [];

    public List<string> Events { get; } = [];

    public ConcurrentQueue<string> Unexpected { get; } = new();

    public int Commits { get; private set; }

    public int Scopes => Volatile.Read(ref scopeSequence);

    /// <summary>
    /// SEC-BL-01 WP-5a: the genuine audit entry at revision 40 of each organization (not part of <see cref="Journal"/>, which holds only what
    /// the tests append). The organization head links to it, so appended entries chain onto a real predecessor that D+ can verify once
    /// <see cref="ProvisionArchiveAsync"/> has archived it.
    /// </summary>
    public ConcurrentDictionary<Guid, SecurityJournalEntry> AuditPredecessors { get; } = new();

    /// <param name="freshAuditStream">WP-5a: an organization whose audit stream starts at revision 1 (genesis), with no history.</param>
    public Guid AddOrganization(bool freshAuditStream = false)
    {
        var id = Guid.CreateVersion7();
        if (freshAuditStream)
        {
            Organizations[id] = new OrganizationAuthorizationState { OrganizationId = id, AuthorizationRevision = 5, ProjectionAppliedRevision = 5 };
            return id;
        }

        var history = new OrganizationAuthorizationState { OrganizationId = id, AuditJournalRevision = 39, LatestAuditJournalDigest = SHA256.HashData(id.ToByteArray()) };
        var predecessor = JournalChain.Append(history, JournalStreamNames.Audit, 40, "WorldHistory", new { note = "genuine audit history before the test" }, null, null, Clock);
        AuditPredecessors[id] = predecessor;
        Organizations[id] = new OrganizationAuthorizationState
        {
            OrganizationId = id, AuthorizationRevision = 5, ProjectionAppliedRevision = 5, AuditJournalRevision = 40, LatestAuditJournalDigest = predecessor.JournalEventDigest,
        };
        return id;
    }

    /// <summary>SEC-BL-01 G14-R2 (A-4 §24): the in-memory D+ durability (record stores, instance pin, recovery keys) of this world's processors.</summary>
    public DPlusTestStores DPlus { get; } = new();

    /// <summary>SEC-BL-01 E-6/E-7: the receipt verifier of this world's scopes (fail closed unless a test composes one).</summary>
    public IJournalReceiptVerifier JournalReceipts { get; set; } = NotComposedJournalReceiptVerifier.Instance;

    /// <summary>SEC-BL-01 WP-5b: the genuine revision-40 history entries of the authorization and content streams (see <see cref="AddStreamHistory"/>).</summary>
    public ConcurrentDictionary<(Guid Organization, string Stream), SecurityJournalEntry> StreamPredecessors { get; } = new();

    private readonly ConcurrentDictionary<(Guid Organization, string Stream), long> streamHeads = new();

    /// <summary>The genuine revision-40 history entry of <paramref name="stream"/> (audit: <see cref="AuditPredecessors"/>).</summary>
    public SecurityJournalEntry Predecessor(Guid organization, string stream) =>
        stream == JournalStreamNames.Audit ? AuditPredecessors[organization] : StreamPredecessors[(organization, stream)];

    /// <summary>
    /// SEC-BL-01 WP-5b: genuine history for the authorization or content stream, as for the audit stream: revision 40 chained to an
    /// unknown older digest, committed (not in <see cref="Journal"/>, like the audit history) and not archived until a test archives it. For
    /// the authorization stream the projection precondition of the gate is satisfied, so only archive delivery decides it.
    /// </summary>
    public SecurityJournalEntry AddStreamHistory(Guid organization, string stream)
    {
        if (stream == JournalStreamNames.Audit)
        {
            return AuditPredecessors[organization];
        }

        lock (Journal)
        {
            var state = Organizations[organization];
            var older = SHA256.HashData([.. organization.ToByteArray(), .. System.Text.Encoding.UTF8.GetBytes(stream)]);
            var history = new OrganizationAuthorizationState { OrganizationId = organization, LatestSecurityJournalDigest = older, LatestContentJournalDigest = older };
            var predecessor = JournalChain.Append(history, stream, 40, "WorldHistory", new { note = "genuine " + stream + " history before the test" }, null, null, Clock);
            predecessor.ArchiveDeliveryState = ArchiveDeliveryStates.Anchored; // delivered before the test (the archive object is the test's choice)
            if (stream == JournalStreamNames.Authorization)
            {
                state.LatestSecurityJournalDigest = predecessor.JournalEventDigest;
                state.AuthorizationRevision = 40;
                state.ProjectionAppliedRevision = 40;
                state.ProjectionZedToken = "world-zed-40";
            }
            else
            {
                state.LatestContentJournalDigest = predecessor.JournalEventDigest;
            }

            streamHeads[(organization, stream)] = 40;
            StreamPredecessors[(organization, stream)] = predecessor;
            return predecessor;
        }
    }

    /// <summary>
    /// SEC-BL-01 WP-5b: appends an entry to any stream as a committed transaction would (audit: <see cref="Seed"/>). Authorization entries also
    /// advance the authorization revision and its applied projection (the gate then depends on archive delivery only).
    /// </summary>
    public SecurityJournalEntry SeedStream(Guid organization, string stream, string eventKind, object body)
    {
        if (stream == JournalStreamNames.Audit)
        {
            return Seed(organization, eventKind, body);
        }

        // The organization row lock, as a committing Authority transaction holds it (a pass in progress commits its state first).
        var rowLock = RowLock(organization);
        rowLock.Wait();
        try
        {
            lock (Journal)
            {
                return SeedLocked(organization, stream, eventKind, body);
            }
        }
        finally
        {
            rowLock.Release();
        }
    }

    private SecurityJournalEntry SeedLocked(Guid organization, string stream, string eventKind, object body)
    {
        {
            var state = Organizations[organization];
            var revision = streamHeads.AddOrUpdate((organization, stream), 1, (_, head) => head + 1);
            if (stream == JournalStreamNames.Authorization)
            {
                state.AuthorizationRevision = revision;
                state.ProjectionAppliedRevision = revision;
                state.ProjectionZedToken = "world-zed-" + revision.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            var entry = JournalChain.Append(state, stream, revision, eventKind, body, null, null, Clock);
            Journal.Add(entry);
            return entry;
        }
    }

    /// <summary>
    /// SEC-BL-01 WP-5a: the sentinel and every organization's archived revision-40 predecessor, as a baselined archive holds them.
    /// G14-R2: the sentinel's instance is also pinned by a provisioning record in <see cref="DPlus"/>.
    /// </summary>
    public async Task ProvisionArchiveAsync(ISecurityEventArchive archive)
    {
        if (archive is ISecurityEventArchiveInventory inventory)
        {
            await DPlus.ProvisionAsync(inventory, Clock.GetUtcNow()).ConfigureAwait(false);
        }

        foreach (var predecessor in AuditPredecessors.Values.Concat(StreamPredecessors.Values))
        {
            await archive.AppendAsync(predecessor.OrganizationId, predecessor.JournalStreamName, 40, System.Text.Encoding.UTF8.GetBytes(predecessor.JournalEventBodyJson), predecessor.JournalEventDigest,
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>Appends an entry directly (test setup), exactly as <see cref="AuditJournal.Append"/> would in a committed transaction.</summary>
    public SecurityJournalEntry Seed(Guid organization, string eventKind, object body)
    {
        lock (Journal)
        {
            var state = Organizations[organization];
            var scope = DispatchProxy.Create<IAuthorityScope, SeedScopeProxy>();
            ((SeedScopeProxy)(object)scope).Journal = Journal;
            var entry = AuditJournal.Append(scope, state, eventKind, body, Clock);
            return entry;
        }
    }

    /// <summary>Seeds a well-formed evidence entry for a key at a database time.</summary>
    public SecurityJournalEntry SeedEvidence(Guid organization, string entryPoint, DateTimeOffset databaseTime) =>
        Seed(organization, CapabilityRefusalEvidence.EventKind, new CapabilityRefusalEvidenceBody(1, CapabilityRefusalEvidence.RefusalCode, organization,
            CapabilityRefusalEvidence.WorkloadOf(entryPoint), entryPoint, 1, databaseTime, CapabilityRefusalEvidence.BucketOf(databaseTime), databaseTime));

    /// <summary>The verified evidence entries of an organization, oldest first (all of them, not just eight).</summary>
    public List<CapabilityRefusalEvidenceEntry> EvidenceOf(Guid organization)
    {
        lock (Journal)
        {
            return Journal.Where(e => e.OrganizationId == organization && e.JournalEventKind == CapabilityRefusalEvidence.EventKind)
                .OrderBy(e => e.JournalStreamRevision)
                .Select(e => CapabilityRefusalEvidenceLookup.Verify(new CapabilityRefusalEvidenceRow(e.SecurityJournalEntryId, e.JournalStreamRevision, e.JournalEventBodyJson, e.JournalEventDigest), organization)
                             ?? throw new InvalidOperationException("unverifiable evidence entry"))
                .ToList();
        }
    }

    public void Log(string text)
    {
        lock (Events)
        {
            Events.Add(text);
        }
    }

    internal SemaphoreSlim RowLock(Guid organization) => rowLocks.GetOrAdd(organization, _ => new SemaphoreSlim(1, 1));

    internal int NextScopeId() => Interlocked.Increment(ref scopeSequence);

    internal void Apply(Guid organization, OrganizationAuthorizationState state, List<SecurityJournalEntry> appended)
    {
        lock (Journal)
        {
            Journal.AddRange(appended);
            Organizations[organization] = state;
            Commits++;
        }
    }

    internal static OrganizationAuthorizationState Clone(OrganizationAuthorizationState state) =>
        (OrganizationAuthorizationState)typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(state, null)!;
}

/// <summary>A scope that only accepts journal appends (direct seeding as a committed transaction would).</summary>
public class SeedScopeProxy : DispatchProxy
{
    public List<SecurityJournalEntry> Journal { get; set; } = null!;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == nameof(IAuthorityScope.AddJournalEntry))
        {
            Journal.Add((SecurityJournalEntry)args![0]!);
            return null;
        }

        throw new InvalidOperationException("seed scope: " + targetMethod?.Name);
    }
}

public class EvidenceStoreProxy : DispatchProxy
{
    public CapabilityRefusalEvidenceWorld World { get; set; } = null!;

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == nameof(IAuthorityStore.BeginScopeAsync))
        {
            var scope = Create<IAuthorityScope, EvidenceScopeProxy>();
            var proxy = (EvidenceScopeProxy)(object)scope;
            proxy.World = World;
            proxy.Id = World.NextScopeId();
            World.Log("BeginScopeAsync#" + proxy.Id);
            return Task.FromResult(scope);
        }

        World.Unexpected.Enqueue("store." + targetMethod?.Name);
        throw new InvalidOperationException("evidence world store: " + targetMethod?.Name + " is not available");
    }
}

public class EvidenceScopeProxy : DispatchProxy
{
    private readonly List<SecurityJournalEntry> appended = [];
    private Guid? held;
    private OrganizationAuthorizationState? working;

    public CapabilityRefusalEvidenceWorld World { get; set; } = null!;

    public int Id { get; set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var name = targetMethod!.Name;
        World.Log(name + "#" + Id);
        if (World.FailOn.TryGetValue(name, out var failure))
        {
            throw failure;
        }

        switch (name)
        {
            case nameof(IAsyncDisposable.DisposeAsync):
                if (held is { } organization)
                {
                    held = null;
                    World.RowLock(organization).Release();
                }

                return ValueTask.CompletedTask;
            case nameof(IAuthorityScope.SetCapabilityEvidenceLockTimeoutAsync):
                return Task.CompletedTask;
            case nameof(IAuthorityScope.LockOrganizationAsync):
                return LockAsync((Guid)args![0]!, (CancellationToken)args[1]!);
            case nameof(IAuthorityScope.StartCapabilityEvidenceLookupAsync):
                return Task.FromResult(World.DatabaseTime ?? World.Clock.GetUtcNow());
            case nameof(IAuthorityScope.IsCapabilityRefusalEvidenceIndexUsableAsync):
                return Task.FromResult(World.IndexUsable);
            case nameof(IAuthorityScope.GetNewestCapabilityRefusalEvidenceAsync):
                return LookupAsync((Guid)args![0]!, (CancellationToken)args[1]!);
            case nameof(IAuthorityScope.GetPendingJournalEntriesAsync):
                lock (World.Journal)
                {
                    return Task.FromResult(World.Journal.Where(e => e.OrganizationId == (Guid)args![0]! && e.ArchiveDeliveryState == ArchiveDeliveryStates.Pending).Take((int)args![1]!).ToList());
                }

            case nameof(IAuthorityScope.GetPendingJournalEntriesByStreamAsync):
                lock (World.Journal)
                {
                    // SEC-BL-01 WP-5a: the per-stream pending query of the archive processor.
                    var stream = (string)args![1]!;
                    var include = (bool)args[2]!;
                    return Task.FromResult(World.Journal.Where(e => e.OrganizationId == (Guid)args[0]! && e.ArchiveDeliveryState == ArchiveDeliveryStates.Pending
                            && string.Equals(e.JournalStreamName, stream, StringComparison.Ordinal) == include)
                        .OrderBy(e => e.JournalStreamName, StringComparer.Ordinal).ThenBy(e => e.JournalStreamRevision).Take((int)args[3]!).ToList());
                }

            case nameof(IAuthorityScope.CountPendingArchiveAsync):
                lock (World.Journal)
                {
                    // SEC-BL-01 WP-5b: the organization gate's count (rows of the stream not Anchored), as AuthorityStore computes it.
                    return Task.FromResult(World.Journal.Count(e => e.OrganizationId == (Guid)args![0]! && e.JournalStreamName == (string)args[1]! && e.ArchiveDeliveryState != ArchiveDeliveryStates.Anchored));
                }

            case nameof(IAuthorityScope.FindLatestJournalEntryAsync):
                lock (World.Journal)
                {
                    return Task.FromResult(World.Journal.Where(e => e.OrganizationId == (Guid)args![0]! && e.JournalStreamName == (string)args[1]!).OrderByDescending(e => e.JournalStreamRevision).FirstOrDefault());
                }

            case "get_" + nameof(IAuthorityScope.JournalReceipts):
                return World.JournalReceipts;

            case nameof(IAuthorityScope.FindJournalEntryAsync):
                lock (World.Journal)
                {
                    return Task.FromResult(World.Journal.FirstOrDefault(e => e.OrganizationId == (Guid)args![0]! && e.JournalStreamName == (string)args[1]! && e.JournalStreamRevision == (long)args[2]!));
                }

            case nameof(IAuthorityScope.AddJournalEntry):
                appended.Add((SecurityJournalEntry)args![0]!);
                return null;
            case nameof(IAuthorityScope.CommitAsync):
                return Commit();
            default:
                World.Unexpected.Enqueue(name);
                throw new InvalidOperationException("evidence world scope: " + name + " is not available");
        }
    }

    private async Task<OrganizationAuthorizationState?> LockAsync(Guid organization, CancellationToken cancellationToken)
    {
        if (!World.Organizations.TryGetValue(organization, out var state))
        {
            return null;
        }

        await World.RowLock(organization).WaitAsync(cancellationToken).ConfigureAwait(false);
        held = organization;
        working = CapabilityRefusalEvidenceWorld.Clone(World.Organizations[organization]);
        return working;
    }

    private async Task<IReadOnlyList<CapabilityRefusalEvidenceRow>> LookupAsync(Guid organization, CancellationToken cancellationToken)
    {
        if (World.LookupDelay > TimeSpan.Zero)
        {
            await Task.Delay(World.LookupDelay, cancellationToken).ConfigureAwait(false);
        }

        lock (World.Journal)
        {
            return World.Journal
                .Where(e => e.OrganizationId == organization && e.JournalStreamName == "audit" && e.JournalEventKind == "EnvelopeCapabilityRefused")
                .OrderByDescending(e => e.JournalStreamRevision).Take(8)
                .Select(e => new CapabilityRefusalEvidenceRow(e.SecurityJournalEntryId, e.JournalStreamRevision, e.JournalEventBodyJson, e.JournalEventDigest)).ToList();
        }
    }

    private Task Commit()
    {
        var behavior = World.NextCommit;
        World.NextCommit = EvidenceCommitBehavior.Normal;
        if (behavior == EvidenceCommitBehavior.FailBeforeEffect)
        {
            return Task.FromException(new InvalidOperationException("connection lost before the commit took effect"));
        }

        if (held is { } organization && working is not null)
        {
            World.Apply(organization, working, [.. appended]);
        }

        return behavior == EvidenceCommitBehavior.FailAfterEffect
            ? Task.FromException(new InvalidOperationException("connection lost after the commit took effect"))
            : Task.CompletedTask;
    }
}
