using System.Text;
using System.Text.Json;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Application.Features.PlatformOperators;
using AuthorizationAuthority.Domain.Features.PlatformOperators;

namespace AuthorizationAuthority.Application.Features.Content;

/// <summary>
/// E3-M2e (docs/security/E3_M2E_CONTRACT_CLOSURE.md OD-M2e-13; owner decision on UQ-3: no new table): durably records the effective
/// Envelope v2 acceptance policy of this Authority instance as an <see cref="EventKind"/> event on the existing hash-chained, archived
/// platform journal, and only then lets the switch treat that policy as effective (<see cref="EnvelopeV2AcceptanceSwitch.MarkObserved"/>).
/// <para>
/// <b>Bounded retrieval.</b> Under the platform journal head lock, after the journal is confirmed current (not behind the archive, not
/// quarantined, archive reachable), the newest <see cref="SearchWindow"/> entries are read through the unique revision index. An event is
/// skipped only when the most recent <see cref="EventKind"/> entry inside that window has a body that still matches its digest and
/// records exactly this policy digest. Otherwise (a different policy, no such event within the window, or an entry that does not
/// verify) a new event is appended: the bound can only cause an extra record, never a missing one. Durable history is the platform
/// journal and its archive; the switch keeps only the digest of what was recorded, to gate acceptance (fail closed).
/// </para>
/// </summary>
public sealed class EnvelopeAcceptancePolicyObserver(IAuthorityStore store, PlatformJournal journal, EnvelopeV2AcceptanceSwitch acceptance, TimeProvider clock)
{
    public const string EventKind = "EnvelopeAcceptancePolicyObserved";

    /// <summary>The bounded number of newest platform journal entries examined for the previous policy event.</summary>
    public const int SearchWindow = 64;

    public const string Recorded = "Recorded";
    public const string AlreadyRecorded = "AlreadyRecorded";

    /// <summary>Records the current effective policy (or confirms it is already the latest record) and marks it observed. Throws on any failure.</summary>
    public async Task<string> RecordAsync(Guid correlationId, CancellationToken cancellationToken)
    {
        var descriptor = acceptance.Describe();
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var head = await PlatformJournal.LockHeadAsync(scope, clock, cancellationToken).ConfigureAwait(false);
        await journal.EnsureCurrentAsync(scope, head, requireAnchored: false, correlationId, cancellationToken).ConfigureAwait(false);
        var latest = await scope.GetLatestPlatformJournalEntriesAsync(SearchWindow, cancellationToken).ConfigureAwait(false);
        var previous = latest.Where(e => e.JournalEventKind == EventKind).OrderByDescending(e => e.PlatformJournalRevision).FirstOrDefault();
        if (previous is not null && string.Equals(RecordedPolicyDigest(previous), descriptor.PolicyDigestHex, StringComparison.Ordinal))
        {
            acceptance.MarkObserved(descriptor.PolicyDigestHex);
            return AlreadyRecorded; // nothing to write; the scope is discarded
        }

        PlatformJournal.Append(scope, head, EventKind, descriptor, PlatformStateDelta.Of(), clock.GetUtcNow());
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        acceptance.MarkObserved(descriptor.PolicyDigestHex);
        return Recorded;
    }

    /// <summary>The policy digest a platform journal entry records, or null when its body does not verify or is not a policy event.</summary>
    public static string? RecordedPolicyDigest(PlatformJournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var bytes = Encoding.UTF8.GetBytes(entry.JournalEventBodyJson);
        if (entry.JournalEventDigest is not { Length: 32 } || !CanonicalJson.Digest(bytes).AsSpan().SequenceEqual(entry.JournalEventDigest))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            return root.GetProperty("event_kind").GetString() == EventKind && root.GetProperty("detail").GetProperty("policy_digest_hex") is { ValueKind: JsonValueKind.String } digest
                ? digest.GetString()
                : null;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }
}
