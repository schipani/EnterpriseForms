using System.Reflection;
using System.Text;
using System.Text.Json;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Contracts.V1;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.PlatformOperators;

namespace AuthorizationAuthority.Application.Features.PlatformOperators;

/// <summary>
/// Rows a platform event created or changed, as they are after the event. The journal body carries them in full, so replaying
/// the archived events rebuilds the platform registry state exactly (operators, proposals, approvals, single-use evidence).
/// </summary>
public sealed record PlatformStateDelta(
    IReadOnlyList<PlatformOperator> Operators,
    IReadOnlyList<PlatformOperatorChangeProposal> Proposals,
    IReadOnlyList<PlatformOperatorApproval> Approvals,
    IReadOnlyList<PlatformOperatorEvidenceUse> EvidenceUses)
{
    public static PlatformStateDelta Of(IEnumerable<PlatformOperator>? operators = null, IEnumerable<PlatformOperatorChangeProposal>? proposals = null, IEnumerable<PlatformOperatorApproval>? approvals = null, IEnumerable<PlatformOperatorEvidenceUse>? evidenceUses = null) =>
        new([.. operators ?? []], [.. proposals ?? []], [.. approvals ?? []], [.. evidenceUses ?? []]);
}

/// <summary>
/// D8-44 platform journal: one hash-chained stream of platform events, delivered to the separately administered append-only
/// archive (the same create-only store as the organization journals, key scope <see cref="ArchiveScopeId"/>, stream
/// <see cref="StreamName"/>).
/// <para>
/// <b>Rollback detection.</b> The archive is the separately protected checkpoint: it is create-only and administered apart from
/// AuthorityDb. Before any platform decision (read or write) the Authority asks the archive whether an event exists at
/// <c>head + 1</c>. If it does, AuthorityDb is behind evidence that was already accepted — typically a restore from before an
/// operator was suspended — and every platform operation fails closed (<c>PLATFORM_JOURNAL_BEHIND_ARCHIVE</c>, critical alert)
/// until the operator-plane replay re-applies the archived events. An unreachable archive also fails closed
/// (<c>PLATFORM_JOURNAL_UNVERIFIABLE</c>). Decisions that grant or advance anything additionally require that every earlier
/// platform event is anchored, and each mutation tries to anchor its own event before answering, so an acknowledged
/// suspension is always in the archive. Bodies that no longer match their digest, or an archive that holds different bytes,
/// quarantine the platform journal; nothing is repaired automatically.
/// </para>
/// </summary>
public sealed class PlatformJournal(IAuthorityStore store, ISecurityEventArchive archive, ISecurityAlertSink alerts, TimeProvider clock)
{
    public const string StreamName = "platform-operators";
    public const string EnvelopeFormat = "DynamicForms/PlatformJournal/v1";
    public const string BodyDigestMismatch = "PLATFORM_JOURNAL_BODY_DIGEST_MISMATCH";
    public const string ArchiveBodyMismatch = "PLATFORM_ARCHIVE_BODY_MISMATCH";
    public const string ReplayChainMismatch = "PLATFORM_REPLAY_CHAIN_MISMATCH";
    private const int MaximumReplayEvents = 10_000;

    /// <summary>Archive key scope of the platform stream (no organization).</summary>
    public static readonly Guid ArchiveScopeId = Guid.Empty;

    /// <summary>The locked head; created on first use when the migration seed is absent (the fixed primary key serializes creators).</summary>
    public static async Task<PlatformJournalHead> LockHeadAsync(IAuthorityScope scope, TimeProvider clock, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(clock);
        if (await scope.LockPlatformJournalAsync(cancellationToken).ConfigureAwait(false) is { } head)
        {
            return head;
        }

        var created = new PlatformJournalHead { HeadModifiedOn = clock.GetUtcNow() };
        scope.AddPlatformJournalHead(created);
        return created;
    }

    /// <summary>
    /// Fails closed unless the platform journal is current: not quarantined, nothing archived beyond this database's head, the
    /// archive reachable, and (for <paramref name="requireAnchored"/>) no earlier event still waiting for the archive.
    /// </summary>
    public async Task EnsureCurrentAsync(IAuthorityScope scope, PlatformJournalHead head, bool requireAnchored, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(head);
        if (await RefusalAsync(scope, head, requireAnchored, correlationId, cancellationToken).ConfigureAwait(false) is { } refusal)
        {
            throw refusal;
        }
    }

    /// <summary>The refusal <see cref="EnsureCurrentAsync"/> would throw, or null.</summary>
    public async Task<AuthorityException?> RefusalAsync(IAuthorityScope scope, PlatformJournalHead head, bool requireAnchored, Guid correlationId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(head);
        if (head.PlatformQuarantineEnabled)
        {
            return new AuthorityException(AuthorityErrorCodes.PlatformJournalQuarantined, 423, false, "the platform journal is quarantined (" + head.PlatformQuarantineReasonCode + "); privileged reconciliation is required");
        }

        byte[]? ahead;
        try
        {
            ahead = await archive.ReadAsync(ArchiveScopeId, StreamName, head.PlatformJournalRevision + 1, cancellationToken).ConfigureAwait(false);
        }
        catch (ArchiveUnavailableException)
        {
            return new AuthorityException(AuthorityErrorCodes.PlatformJournalUnverifiable, 503, true, "the independent archive cannot confirm that the platform journal is current");
        }

        if (ahead is not null)
        {
            alerts.Raise(new SecurityAlert("PlatformJournalBehindArchive", "Critical", ArchiveScopeId, Guid.Empty, null,
                $"AuthorityDb platform journal is at revision {head.PlatformJournalRevision} but the archive holds revision {head.PlatformJournalRevision + 1}: a restore or rollback; platform operations are closed until replay", correlationId));
            return new AuthorityException(AuthorityErrorCodes.PlatformJournalBehindArchive, 423, false, "the platform journal is behind the independent archive (database restore or rollback); platform operations stay closed until the archived events are replayed");
        }

        if (requireAnchored && await scope.CountPendingPlatformArchiveAsync(cancellationToken).ConfigureAwait(false) > 0)
        {
            return new AuthorityException(AuthorityErrorCodes.PlatformJournalArchivePending, 503, true, "an earlier platform event is not yet in the independent archive; retry shortly");
        }

        return null;
    }

    /// <summary>Appends one event to the locked head inside the caller's transaction. Call after every row in the delta has its final state.</summary>
    public static PlatformJournalEntry Append(IAuthorityScope scope, PlatformJournalHead head, string eventKind, object detail, PlatformStateDelta delta, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(head);
        ArgumentNullException.ThrowIfNull(delta);
        var revision = head.PlatformJournalRevision + 1;
        foreach (var platformOperator in delta.Operators)
        {
            platformOperator.LastPlatformJournalRevision = revision;
        }

        var envelope = new PlatformJournalEnvelope<object>(EnvelopeFormat, StreamName, revision, eventKind, head.LatestPlatformJournalDigest is { } previous ? Convert.ToHexStringLower(previous) : null, now, detail, delta);
        var bytes = CanonicalJson.SerializeBytes(envelope);
        var digest = CanonicalJson.Digest(bytes);
        var entry = new PlatformJournalEntry
        {
            PlatformJournalEntryId = Guid.CreateVersion7(),
            PlatformJournalRevision = revision,
            JournalEventKind = eventKind,
            JournalEventBodyJson = Encoding.UTF8.GetString(bytes),
            JournalEventDigest = digest,
            JournalPreviousDigest = head.LatestPlatformJournalDigest,
            ArchiveDeliveryState = ArchiveDeliveryStates.Pending,
            JournalCreatedOn = now,
        };
        head.PlatformJournalRevision = revision;
        head.LatestPlatformJournalDigest = digest;
        head.HeadModifiedOn = now;
        scope.AddPlatformJournalEntry(entry);
        return entry;
    }

    public static PlatformJournalStatus Status(PlatformJournalHead head, int pending) =>
        new(head.PlatformJournalRevision, pending, head.PlatformQuarantineEnabled, head.PlatformQuarantineReasonCode);

    /// <summary>Delivers pending platform events to the archive in revision order. Returns <c>Anchored</c> when none remain pending, else <c>Pending</c>.</summary>
    public async Task<string> DeliverAsync(CancellationToken cancellationToken)
    {
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var head = await scope.LockPlatformJournalAsync(cancellationToken).ConfigureAwait(false);
        if (head is null)
        {
            return ArchiveDeliveryStates.Anchored;
        }

        var pending = await scope.GetPendingPlatformJournalEntriesAsync(64, cancellationToken).ConfigureAwait(false);
        var remaining = pending.Count;
        foreach (var entry in pending.OrderBy(e => e.PlatformJournalRevision))
        {
            entry.ArchiveAttemptCount++;
            var body = Encoding.UTF8.GetBytes(entry.JournalEventBodyJson);
            if (!CanonicalJson.Digest(body).AsSpan().SequenceEqual(entry.JournalEventDigest))
            {
                Quarantine(head, entry, BodyDigestMismatch);
                break;
            }

            try
            {
                var receipt = await archive.AppendAsync(ArchiveScopeId, StreamName, entry.PlatformJournalRevision, body, entry.JournalEventDigest, cancellationToken).ConfigureAwait(false);
                entry.ArchiveDeliveryState = ArchiveDeliveryStates.Anchored;
                entry.ArchiveReceiptReference = receipt.ReceiptReference;
                entry.ArchiveReceiptDigest = receipt.ReceiptDigest;
                entry.JournalArchivedOn = clock.GetUtcNow();
                entry.LastArchiveErrorCode = null;
                remaining--;
            }
            catch (ArchiveConflictException)
            {
                Quarantine(head, entry, ArchiveBodyMismatch);
                break;
            }
            catch (ArchiveUnavailableException ex)
            {
                entry.LastArchiveErrorCode = ex.Message.Length <= 100 ? ex.Message : ex.Message[..100];
                break; // keep ordering; retry on the next pass
            }
        }

        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return remaining == 0 && !head.PlatformQuarantineEnabled ? ArchiveDeliveryStates.Anchored : ArchiveDeliveryStates.Pending;
    }

    /// <summary>
    /// Operator plane only: re-applies archived platform events beyond this database's head, verifying each event's digest,
    /// revision and chain link before applying its state delta. Moves state forward to what was already accepted; it never
    /// chooses or edits anything. A chain mismatch quarantines the platform journal.
    /// </summary>
    public async Task<PlatformJournalReplayResponse> ReplayAsync(string operatorName, Guid correlationId, CancellationToken cancellationToken)
    {
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var head = await LockHeadAsync(scope, clock, cancellationToken).ConfigureAwait(false);
        if (head.PlatformQuarantineEnabled)
        {
            throw new AuthorityException(AuthorityErrorCodes.PlatformJournalQuarantined, 423, false, "the platform journal is quarantined; replay cannot repair it");
        }

        var before = head.PlatformJournalRevision;
        var replayed = 0;
        while (replayed < MaximumReplayEvents)
        {
            byte[]? bytes;
            try
            {
                bytes = await archive.ReadAsync(ArchiveScopeId, StreamName, head.PlatformJournalRevision + 1, cancellationToken).ConfigureAwait(false);
            }
            catch (ArchiveUnavailableException ex)
            {
                throw new AuthorityException(AuthorityErrorCodes.PlatformJournalUnverifiable, 503, true, "archive unavailable during replay: " + ex.Message);
            }

            if (bytes is null)
            {
                break;
            }

            var digest = CanonicalJson.Digest(bytes);
            PlatformJournalEnvelope<JsonElement>? envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<PlatformJournalEnvelope<JsonElement>>(bytes, CanonicalJson.Options);
            }
            catch (JsonException)
            {
                envelope = null;
            }

            var expectedPrevious = head.LatestPlatformJournalDigest is { } previous ? Convert.ToHexStringLower(previous) : null;
            if (envelope is null || envelope.EnvelopeFormat != EnvelopeFormat || envelope.StreamName != StreamName || envelope.StreamRevision != head.PlatformJournalRevision + 1
                || !string.Equals(envelope.PreviousDigestHex, expectedPrevious, StringComparison.Ordinal) || envelope.Delta is null)
            {
                head.PlatformQuarantineEnabled = true;
                head.PlatformQuarantineReasonCode = ReplayChainMismatch;
                await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
                alerts.Raise(new SecurityAlert("PlatformJournalReplayMismatch", "Critical", ArchiveScopeId, Guid.Empty, null, $"archived platform event {head.PlatformJournalRevision + 1} does not chain to the database head; platform journal quarantined", correlationId));
                throw new AuthorityException(AuthorityErrorCodes.PlatformJournalQuarantined, 423, false, "the archived platform event does not chain to this database's head; the platform journal is quarantined");
            }

            await ApplyDeltaAsync(scope, envelope.Delta, cancellationToken).ConfigureAwait(false);
            var now = clock.GetUtcNow();
            scope.AddPlatformJournalEntry(new PlatformJournalEntry
            {
                PlatformJournalEntryId = Guid.CreateVersion7(),
                PlatformJournalRevision = envelope.StreamRevision,
                JournalEventKind = envelope.EventKind,
                JournalEventBodyJson = Encoding.UTF8.GetString(bytes),
                JournalEventDigest = digest,
                JournalPreviousDigest = head.LatestPlatformJournalDigest,
                ArchiveDeliveryState = ArchiveDeliveryStates.Anchored,
                ArchiveReceiptReference = "replayed-from-archive",
                JournalCreatedOn = envelope.CreatedOnUtc,
                JournalArchivedOn = now,
            });
            head.PlatformJournalRevision = envelope.StreamRevision;
            head.LatestPlatformJournalDigest = digest;
            head.HeadModifiedOn = now;
            replayed++;
        }

        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (replayed > 0)
        {
            alerts.Raise(new SecurityAlert("PlatformJournalReplayed", "High", ArchiveScopeId, Guid.Empty, null, $"{replayed} archived platform events re-applied by {operatorName} (revision {before} -> {head.PlatformJournalRevision})", correlationId));
        }

        await using var check = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var current = await check.ReadPlatformJournalAsync(cancellationToken).ConfigureAwait(false) ?? head;
        var refusal = await RefusalAsync(check, current, requireAnchored: false, correlationId, cancellationToken).ConfigureAwait(false);
        return new PlatformJournalReplayResponse(before, current.PlatformJournalRevision, replayed, refusal is null, refusal?.ErrorCode, correlationId);
    }

    /// <summary>Upserts every row of a replayed delta (operators first, so evidence and approvals can reference them). Approvals of requests this database no longer has are skipped.</summary>
    private static async Task ApplyDeltaAsync(IAuthorityScope scope, PlatformStateDelta delta, CancellationToken cancellationToken)
    {
        foreach (var row in delta.Operators)
        {
            if (await scope.FindPlatformOperatorAsync(row.PlatformOperatorId, cancellationToken).ConfigureAwait(false) is { } existing)
            {
                CopyInto(row, existing);
            }
            else
            {
                scope.AddPlatformOperator(row);
            }
        }

        foreach (var row in delta.Proposals)
        {
            if (await scope.FindPlatformOperatorProposalAsync(row.PlatformOperatorChangeProposalId, cancellationToken).ConfigureAwait(false) is { } existing)
            {
                CopyInto(row, existing);
            }
            else
            {
                scope.AddPlatformOperatorProposal(row);
            }
        }

        foreach (var row in delta.EvidenceUses)
        {
            if (await scope.FindPlatformEvidenceUseAsync(row.PlatformOperatorEvidenceUseId, cancellationToken).ConfigureAwait(false) is null)
            {
                scope.AddPlatformEvidenceUse(row);
            }
        }

        foreach (var row in delta.Approvals)
        {
            if (await scope.FindPlatformApprovalAsync(row.PlatformOperatorApprovalId, cancellationToken).ConfigureAwait(false) is { } existing)
            {
                CopyInto(row, existing);
            }
            else if (await scope.FindMembershipRequestAsync(row.OrganizationId, row.OrganizationMembershipRequestId, cancellationToken).ConfigureAwait(false) is not null)
            {
                scope.AddPlatformApproval(row);
            }
        }
    }

    private static void CopyInto<T>(T source, T target)
        where T : class
    {
        foreach (var property in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead && p.CanWrite))
        {
            property.SetValue(target, property.GetValue(source));
        }
    }

    private void Quarantine(PlatformJournalHead head, PlatformJournalEntry entry, string reason)
    {
        entry.ArchiveDeliveryState = ArchiveDeliveryStates.VerificationFailed;
        entry.LastArchiveErrorCode = reason;
        head.PlatformQuarantineEnabled = true;
        head.PlatformQuarantineReasonCode = reason;
        alerts.Raise(new SecurityAlert("PlatformJournalQuarantined", "Critical", ArchiveScopeId, Guid.Empty, null, $"platform journal event {entry.PlatformJournalRevision} could not be archived ({reason}); platform operations closed", Guid.Empty));
    }

    private sealed record PlatformJournalEnvelope<TDetail>(string EnvelopeFormat, string StreamName, long StreamRevision, string EventKind, string? PreviousDigestHex, DateTimeOffset CreatedOnUtc, TDetail Detail, PlatformStateDelta Delta);
}
