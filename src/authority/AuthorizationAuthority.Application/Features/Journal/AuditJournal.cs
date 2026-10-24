using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Domain.Features.Journal;
using AuthorizationAuthority.Domain.Features.Organizations;

namespace AuthorizationAuthority.Application.Features.Journal;

/// <summary>
/// The non-gating audit journal stream (OD-8): hash-chained, archived and anchored by the unchanged archive processor.
/// The caller must hold the organization row lock (<paramref name="locked"/>) and commit the entry in its own
/// transaction: the event becomes durable together with what it records, or not at all.
/// </summary>
public static class AuditJournal
{
    public static void Append(IAuthorityScope scope, OrganizationAuthorizationState locked, string eventKind, object body, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(locked);
        ArgumentNullException.ThrowIfNull(clock);
        locked.AuditJournalRevision++;
        locked.StateModifiedOn = clock.GetUtcNow();
        scope.AddJournalEntry(JournalChain.Append(locked, JournalStreamNames.Audit, locked.AuditJournalRevision, eventKind, body, null, null, clock));
    }
}
