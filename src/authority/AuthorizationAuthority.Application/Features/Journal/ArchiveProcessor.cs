using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Domain.Features.Journal;

namespace AuthorizationAuthority.Application.Features.Journal;

/// <summary>
/// Delivers pending journal bodies to the independent archive and records receipts. While an authorization-stream
/// entry is unarchived the organization gate stays closed; a content checkpoint that is unarchived keeps its
/// revision unreadable (SEC-P12). Retries are idempotent (same key, same bytes).
/// </summary>
public sealed class ArchiveProcessor(IAuthorityStore store, ISecurityEventArchive archive, TimeProvider clock)
{
    public async Task<int> ProcessOrganizationAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var state = await scope.LockOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            return 0;
        }

        var pending = await scope.GetPendingJournalEntriesAsync(organizationId, 32, cancellationToken).ConfigureAwait(false);
        var archived = 0;
        foreach (var entry in pending.OrderBy(e => e.JournalStreamName, StringComparer.Ordinal).ThenBy(e => e.JournalStreamRevision))
        {
            entry.ArchiveAttemptCount++;
            try
            {
                var body = System.Text.Encoding.UTF8.GetBytes(entry.JournalEventBodyJson);
                var receipt = await archive.AppendAsync(organizationId, entry.JournalStreamName, entry.JournalStreamRevision, body, entry.JournalEventDigest, cancellationToken).ConfigureAwait(false);
                entry.ArchiveDeliveryState = ArchiveDeliveryStates.Anchored;
                entry.ArchiveReceiptReference = receipt.ReceiptReference;
                entry.ArchiveReceiptDigest = receipt.ReceiptDigest;
                entry.JournalArchivedOn = clock.GetUtcNow();
                entry.LastArchiveErrorCode = null;
                archived++;
            }
            catch (ArchiveConflictException)
            {
                entry.ArchiveDeliveryState = ArchiveDeliveryStates.VerificationFailed;
                entry.LastArchiveErrorCode = "ARCHIVE_BODY_MISMATCH";
                state.IntegrityQuarantineEnabled = true;
                state.IntegrityQuarantineReasonCode = "ARCHIVE_BODY_MISMATCH";
                break;
            }
            catch (ArchiveUnavailableException ex)
            {
                entry.LastArchiveErrorCode = Truncate(ex.Message);
                break; // keep ordering; retry on the next pass
            }
        }

        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return archived;
    }

    private static string Truncate(string text) => text.Length <= 100 ? text : text[..100];
}
