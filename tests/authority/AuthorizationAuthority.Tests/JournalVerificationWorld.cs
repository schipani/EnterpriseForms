using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.Journal;
using AuthorizationAuthority.Application.Features.Journal.Reconciliation;
using AuthorizationAuthority.Application.Features.Journal.Verification;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Organizations;
using AuthorizationAuthority.Infrastructure.Archive;

namespace AuthorizationAuthority.Tests;

/// <summary>
/// SEC-BL-01 WP-3/WP-4 test world: organizations and journal rows written by the real <see cref="JournalChain"/>, the in-memory archive,
/// checkpoint and finding stores, a recording alert sink and a read-only snapshot reader. Tampering is emulated by editing rows, organization
/// heads or archive objects directly, as J1/J2/J3/J4 actors would.
/// </summary>
internal sealed class JournalVerificationWorld
{
    public static readonly DateTimeOffset Start = new(2026, 11, 17, 16, 0, 0, TimeSpan.Zero);

    public ManualClock Clock { get; } = new(Start);

    public InMemorySecurityEventArchive Archive { get; } = new();

    public InMemoryVerificationCheckpointStore Checkpoints { get; } = new();

    public InMemoryJournalVerificationFindingStore Findings { get; } = new();

    public InMemoryJournalAcceptedExceptionStore Exceptions { get; } = new();

    public RecordingAlerts Alerts { get; } = new();

    public Dictionary<Guid, OrganizationAuthorizationState> Organizations { get; } = [];

    public List<SecurityJournalEntry> Rows { get; } = [];

    /// <summary>WP-5a.1: platform-journal rows counted as provisioning evidence (the platform journal itself is not modelled here).</summary>
    public int PlatformJournalRows { get; set; }

    public InMemoryJournalVerificationReader Reader { get; }

    public JournalVerificationWorld() => Reader = new InMemoryJournalVerificationReader(this);

    private sealed record WorldBody(string NoteText, long CounterValue);

    public Guid AddOrganization()
    {
        var id = Guid.CreateVersion7(Clock.GetUtcNow());
        Organizations[id] = new OrganizationAuthorizationState
        {
            OrganizationAuthorizationStateId = Guid.CreateVersion7(), OrganizationId = id, OrganizationDisplayName = "verification test", StateModifiedOn = Clock.GetUtcNow(),
            CheckpointEnforcementModeName = CheckpointEnforcementModes.RequiredBeforeEffective,
        };
        return id;
    }

    public long Head(Guid org, string stream)
    {
        var s = Organizations[org];
        return stream switch { JournalStreamNames.Authorization => s.AuthorizationRevision, JournalStreamNames.Content => s.ContentIntegrityRevision, _ => s.AuditJournalRevision };
    }

    public void SetHead(Guid org, string stream, long revision, byte[]? digest)
    {
        var s = Organizations[org];
        switch (stream)
        {
            case JournalStreamNames.Authorization:
                s.AuthorizationRevision = revision;
                s.LatestSecurityJournalDigest = digest;
                break;
            case JournalStreamNames.Content:
                // Init-only in the domain (advanced only through AdvanceContentIntegrityRevision); a test world sets it directly, as a J2 actor could.
                typeof(OrganizationAuthorizationState).GetProperty(nameof(OrganizationAuthorizationState.ContentIntegrityRevision))!.SetValue(s, revision);
                s.LatestContentJournalDigest = digest;
                break;
            default:
                s.AuditJournalRevision = revision;
                s.LatestAuditJournalDigest = digest;
                break;
        }
    }

    /// <summary>Appends <paramref name="count"/> genuine entries, advancing the stream head like the Authority does.</summary>
    public List<SecurityJournalEntry> Append(Guid org, string stream, int count, string note = "entry")
    {
        var state = Organizations[org];
        var added = new List<SecurityJournalEntry>();
        for (var i = 0; i < count; i++)
        {
            Clock.Advance(TimeSpan.FromSeconds(1));
            var revision = Head(org, stream) + 1;
            var entry = JournalChain.Append(state, stream, revision, "TestEvent", new WorldBody(note + " " + revision, revision), null, null, Clock);
            SetHead(org, stream, revision, entry.JournalEventDigest);
            Rows.Add(entry);
            added.Add(entry);
        }

        return added;
    }

    /// <summary>Delivers pending rows up to <paramref name="upTo"/> exactly as the unchanged delivery path would (archive append, then Anchored + receipt).</summary>
    public async Task DeliverAsync(Guid org, string stream, long upTo = long.MaxValue)
    {
        foreach (var row in Rows.Where(r => r.OrganizationId == org && r.JournalStreamName == stream && r.JournalStreamRevision <= upTo && r.ArchiveDeliveryState == ArchiveDeliveryStates.Pending)
                     .OrderBy(r => r.JournalStreamRevision).ToList())
        {
            var receipt = await Archive.AppendAsync(org, stream, row.JournalStreamRevision, Encoding.UTF8.GetBytes(row.JournalEventBodyJson), row.JournalEventDigest, CancellationToken.None);
            row.ArchiveDeliveryState = ArchiveDeliveryStates.Anchored;
            row.ArchiveReceiptReference = receipt.ReceiptReference;
            row.ArchiveReceiptDigest = receipt.ReceiptDigest;
        }
    }

    public Task CreateSentinelAsync() => Archive.CreateIdentityAsync(ArchiveIdentityCodec.NewIdentity(Start), CancellationToken.None);

    public SecurityJournalEntry Row(Guid org, string stream, long revision) => Rows.Single(r => r.OrganizationId == org && r.JournalStreamName == stream && r.JournalStreamRevision == revision);

    public static string ArchiveKey(Guid org, string stream, long revision) => $"{org:D}/{stream}/{revision}";

    public JournalBaselineService Baseline(IVerificationCheckpointStore? checkpoints = null, ISecurityEventArchiveInventory? archive = null) =>
        new(Reader, archive ?? Archive, checkpoints ?? Checkpoints, Exceptions, Clock);

    public JournalVerifier Verifier(JournalVerifierOptions? options = null, IVerificationCheckpointStore? checkpoints = null, ISecurityEventArchiveInventory? archive = null) =>
        new(Reader, archive ?? Archive, checkpoints ?? Checkpoints, Findings, Exceptions, Alerts, Clock, options ?? new JournalVerifierOptions());

    public static JournalBaselineRequest BaselineRequest(Guid org, string stream, bool createCheckpoint = true, bool createSentinel = true, IReadOnlyList<JournalBaselineExceptionDisposition>? dispositions = null,
        bool rebaseline = false) =>
        new(org, stream, Guid.Parse("019a5b2c-5ec1-7b01-8000-00000000b001"), "operator-test", "verifier-test", "test-tool", "test-commit", createSentinel, createCheckpoint, dispositions ?? [], TimeSpan.FromMinutes(15),
            rebaseline);

    /// <summary>An owner disposition recorded one hour before the world start (A-1: the disposition time is mandatory).</summary>
    public static JournalBaselineExceptionDisposition Disposition(string id, Guid org, string stream, string findingCode, string? subtype, long first, long last, string reference = "C-TEST-0001",
        string exceptionClass = "LEGACY_UNVERIFIABLE") =>
        new(id, exceptionClass, org, stream, findingCode, subtype, first, last, reference, Start.AddHours(-1));

    /// <summary>Digest of every row and organization head: a verifier or baseline run must leave it unchanged (no AuthorityDb writes).</summary>
    public string Fingerprint()
    {
        var text = new StringBuilder();
        foreach (var r in Rows.OrderBy(r => r.OrganizationId).ThenBy(r => r.JournalStreamName, StringComparer.Ordinal).ThenBy(r => r.JournalStreamRevision))
        {
            text.Append(r.SecurityJournalEntryId).Append('|').Append(r.JournalStreamRevision).Append('|').Append(r.JournalEventBodyJson).Append('|').Append(Convert.ToHexString(r.JournalEventDigest))
                .Append('|').Append(r.JournalPreviousDigest is null ? "-" : Convert.ToHexString(r.JournalPreviousDigest)).Append('|').Append(r.ArchiveDeliveryState).Append('|')
                .Append(r.ArchiveReceiptReference).Append('\n');
        }

        foreach (var s in Organizations.Values.OrderBy(s => s.OrganizationId))
        {
            text.Append(s.OrganizationId).Append('|').Append(s.AuthorizationRevision).Append('|').Append(s.ContentIntegrityRevision).Append('|').Append(s.AuditJournalRevision).Append('|')
                .Append(s.IntegrityQuarantineEnabled).Append('|').Append(s.IntegrityQuarantineReasonCode).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}

internal sealed class RecordingAlerts : ISecurityAlertSink
{
    public ConcurrentQueue<SecurityAlert> Raised { get; } = new();

    public void Raise(SecurityAlert alert) => Raised.Enqueue(alert);
}

/// <summary>
/// Read-only in-memory reader. A snapshot copies rows and heads when opened (consistent view); the port has no write method. The sweep
/// lock is shared by every reader over the same world (emulates the PostgreSQL advisory lock across processes).
/// </summary>
internal sealed class InMemoryJournalVerificationReader(
    Func<IEnumerable<SecurityJournalEntry>> journalRows, Func<IEnumerable<OrganizationAuthorizationState>> organizationStates, Func<int> platformJournalRows) : IJournalVerificationReader
{
    private readonly ConcurrentDictionary<(Guid, string), object> locks = new();

    public int SnapshotsOpened;

    public InMemoryJournalVerificationReader(JournalVerificationWorld world)
        : this(() => world.Rows, () => world.Organizations.Values, () => world.PlatformJournalRows)
    {
    }

    internal int PlatformRows => platformJournalRows();

    /// <summary>Test hook: awaited before every row batch (blocking and cancellation tests).</summary>
    public Func<CancellationToken, Task>? BeforeRowBatch { get; set; }

    public Task<IJournalVerificationSnapshot> OpenSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref SnapshotsOpened);
        var rows = journalRows().Select(r => new JournalRowSnapshot(r.SecurityJournalEntryId, r.OrganizationId, r.JournalStreamName, r.JournalStreamRevision, r.JournalEventKind, r.JournalEventBodyJson,
            r.JournalEventDigest.ToArray(), r.JournalPreviousDigest?.ToArray(), r.ArchiveDeliveryState, r.ArchiveReceiptReference is not null || r.ArchiveReceiptDigest is not null, r.JournalCreatedOn)).ToList();
        var heads = organizationStates().ToDictionary(s => s.OrganizationId, s => new Dictionary<string, (long, byte[]?)>(StringComparer.Ordinal)
        {
            [JournalStreamNames.Authorization] = (s.AuthorizationRevision, s.LatestSecurityJournalDigest?.ToArray()),
            [JournalStreamNames.Content] = (s.ContentIntegrityRevision, s.LatestContentJournalDigest?.ToArray()),
            [JournalStreamNames.Audit] = (s.AuditJournalRevision, s.LatestAuditJournalDigest?.ToArray()),
        });
        return Task.FromResult<IJournalVerificationSnapshot>(new Snapshot(this, rows, heads));
    }

    private sealed class Snapshot(InMemoryJournalVerificationReader owner, List<JournalRowSnapshot> rows, Dictionary<Guid, Dictionary<string, (long Revision, byte[]? Digest)>> heads) : IJournalVerificationSnapshot
    {
        private readonly List<(Guid, string)> held = [];

        public Task<IReadOnlyList<Guid>> ListOrganizationsAsync(Guid? after, int limit, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<Guid>>([.. heads.Keys.Order().Where(g => after is null || g.CompareTo(after.Value) > 0).Take(limit)]);
        }

        public Task<long> CountJournalRowsAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult((long)rows.Count + owner.PlatformRows);
        }

        public Task<JournalStreamHead?> ReadStreamHeadAsync(Guid organizationId, string streamName, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!heads.TryGetValue(organizationId, out var streams))
            {
                return Task.FromResult<JournalStreamHead?>(null);
            }

            var max = rows.Where(r => r.OrganizationId == organizationId && r.StreamName == streamName).Select(r => r.StreamRevision).DefaultIfEmpty(0).Max();
            return Task.FromResult<JournalStreamHead?>(new JournalStreamHead(organizationId, streamName, streams[streamName].Revision, streams[streamName].Digest, max));
        }

        public async Task<IReadOnlyList<JournalRowSnapshot>> ReadRowsAsync(Guid organizationId, string streamName, long fromRevision, long toRevision, int limit, CancellationToken cancellationToken)
        {
            if (owner.BeforeRowBatch is { } hook)
            {
                await hook(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return [.. rows.Where(r => r.OrganizationId == organizationId && r.StreamName == streamName && r.StreamRevision >= fromRevision && r.StreamRevision <= toRevision)
                .OrderBy(r => r.StreamRevision).Take(limit)];
        }

        public Task<bool> TryAcquireSweepLockAsync(Guid organizationId, string streamName, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var acquired = owner.locks.TryAdd((organizationId, streamName), this);
            if (acquired)
            {
                held.Add((organizationId, streamName));
            }

            return Task.FromResult(acquired);
        }

        public ValueTask DisposeAsync()
        {
            foreach (var key in held)
            {
                owner.locks.TryRemove(key, out _);
            }

            return ValueTask.CompletedTask;
        }
    }
}
