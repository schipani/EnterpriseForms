using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Organizations;

namespace AuthorizationAuthority.Application.Features.Journal;

/// <summary>Builds hash-chained journal entries. The body is the full semantic event (replayable); the digest chains to the previous entry of the same stream.</summary>
public static class JournalChain
{
    public static SecurityJournalEntry Append<TBody>(OrganizationAuthorizationState state, string streamName, long streamRevision, string eventKind, TBody body, Guid? commandId, Guid? operationId, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(clock);
        var previous = streamName == JournalStreamNames.Authorization ? state.LatestSecurityJournalDigest : state.LatestContentJournalDigest;
        var envelope = new JournalEnvelope<TBody>(
            "DynamicForms/SecurityJournal/v1",
            state.OrganizationId,
            streamName,
            streamRevision,
            eventKind,
            previous is null ? null : Convert.ToHexStringLower(previous),
            clock.GetUtcNow(),
            body);
        var bytes = CanonicalJson.SerializeBytes(envelope);
        var digest = CanonicalJson.Digest(bytes);
        var entry = new SecurityJournalEntry
        {
            SecurityJournalEntryId = Guid.CreateVersion7(),
            OrganizationId = state.OrganizationId,
            JournalStreamName = streamName,
            JournalStreamRevision = streamRevision,
            JournalEventKind = eventKind,
            SecurityCommandId = commandId,
            ContentChangeOperationId = operationId,
            JournalEventBodyJson = System.Text.Encoding.UTF8.GetString(bytes),
            JournalEventDigest = digest,
            JournalPreviousDigest = previous,
            ArchiveDeliveryState = ArchiveDeliveryStates.Pending,
            JournalCreatedOn = clock.GetUtcNow(),
        };

        if (streamName == JournalStreamNames.Authorization)
        {
            state.LatestSecurityJournalDigest = digest;
        }
        else
        {
            state.LatestContentJournalDigest = digest;
        }

        return entry;
    }

    private sealed record JournalEnvelope<TBody>(string EnvelopeFormat, Guid OrganizationId, string StreamName, long StreamRevision, string EventKind, string? PreviousDigestHex, DateTimeOffset CreatedOnUtc, TBody Body);
}
