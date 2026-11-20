using System.Text;
using System.Text.Json;
using AuthorizationAuthority.Application.Common.Ports;
using AuthorizationAuthority.Domain.Features.Content;
using AuthorizationAuthority.Domain.Features.Journal;

namespace AuthorizationAuthority.Application.Features.Content;

/// <summary>
/// SEC-BL-01 E-7 (C-158, A-7 §27 CR-1..CR-3): content of a head is released only when its checkpoint journal entry
/// <list type="bullet">
/// <item>CR-1 binds to exactly that head: content stream, kind <c>ContentHeadCommitted</c>, the head's operation (column and body), and the body's
/// organization, record, committed revision and manifest root equal the head's (so a receipt of another record, revision or operation, or a
/// substituted body, never releases this head);</item>
/// <item>CR-2 carries a trusted delivery receipt (<see cref="IJournalReceiptVerifier"/>): the archived object equals the row, the archive is the
/// pinned instance, and the W2-S commitment names the row. The row's delivery state alone is never sufficient;</item>
/// <item>CR-3 anything else, including an unavailable store, is not released (<c>AwaitingArchive</c>).</item>
/// </list>
/// Content integrity (HMAC rows, manifest root) is verified afterwards exactly as before.
/// </summary>
public static class ContentCheckpointReceipts
{
    public const string CheckpointEventKind = "ContentHeadCommitted";

    public static async Task<bool> IsDeliveredAsync(IAuthorityScope scope, RecordContentHead head, SecurityJournalEntry? checkpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(head);
        return checkpoint is not null && Binds(checkpoint, head)
            && (await scope.JournalReceipts.VerifyAsync(checkpoint, cancellationToken).ConfigureAwait(false)).IsVerified;
    }

    /// <summary>CR-1: the checkpoint entry names exactly this head (row columns and the canonical body).</summary>
    public static bool Binds(SecurityJournalEntry checkpoint, RecordContentHead head)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(head);
        if (!string.Equals(checkpoint.JournalStreamName, JournalStreamNames.Content, StringComparison.Ordinal) || !string.Equals(checkpoint.JournalEventKind, CheckpointEventKind, StringComparison.Ordinal)
            || checkpoint.OrganizationId != head.OrganizationId || head.CommittedOperationId is not { } operation || checkpoint.ContentChangeOperationId != operation)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(Encoding.UTF8.GetBytes(checkpoint.JournalEventBodyJson));
            var envelope = document.RootElement;
            var body = envelope.GetProperty("body");
            return envelope.GetProperty("organization_id").GetGuid() == head.OrganizationId
                && string.Equals(envelope.GetProperty("stream_name").GetString(), JournalStreamNames.Content, StringComparison.Ordinal)
                && envelope.GetProperty("stream_revision").GetInt64() == checkpoint.JournalStreamRevision
                && string.Equals(envelope.GetProperty("event_kind").GetString(), CheckpointEventKind, StringComparison.Ordinal)
                && body.GetProperty("form_record_id").GetGuid() == head.FormRecordId
                && body.GetProperty("committed_content_revision").GetInt64() == head.CommittedContentRevision
                && body.GetProperty("committed_operation_id").GetGuid() == operation
                && string.Equals(body.GetProperty("manifest_root_hex").GetString(), Convert.ToHexStringLower(head.CommittedManifestRoot), StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return false; // not a canonical checkpoint body: never released
        }
    }
}
