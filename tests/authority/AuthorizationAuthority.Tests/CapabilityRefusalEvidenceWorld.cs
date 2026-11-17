using System.Collections.Concurrent;
using System.Reflection;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Content;
using AuthorizationAuthority.Application.Features.Journal;
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

    public Guid AddOrganization()
    {
        var id = Guid.CreateVersion7();
        Organizations[id] = new OrganizationAuthorizationState { OrganizationId = id, AuthorizationRevision = 5, ProjectionAppliedRevision = 5, AuditJournalRevision = 40 };
        return id;
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
