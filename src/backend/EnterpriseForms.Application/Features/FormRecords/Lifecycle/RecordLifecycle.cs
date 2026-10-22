using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Features.FormRecords.Projection;

namespace EnterpriseForms.Application.Features.FormRecords.Lifecycle;

/// <summary>
/// Record lifecycle (Phase C6 M1, architecture §2): Draft → Submitted → Archived, decided from the status in the
/// Authority-verified signed record header — never from the unsigned <c>form_records.record_status</c> projection.
/// <c>record_status</c> and <c>submitted_on</c> are signed header columns, so a database writer cannot change them
/// without failing verification. Transitions are owner-only; the rules below are enforced by the business API (the
/// Authority does not interpret status for owner writes, R-01; its Draft-only guard for share editors is AC-2, not built).
/// </summary>
public static class RecordLifecycle
{
    public const string Draft = "Draft";
    public const string Submitted = "Submitted";
    public const string Archived = "Archived";

    public const string Submit = "Submit";
    public const string Reopen = "Reopen";
    public const string Archive = "Archive";
    public const string Unarchive = "Unarchive";

    public static readonly IReadOnlyList<string> Transitions = [Submit, Reopen, Archive, Unarchive];

    public const int MaximumReasonLength = 500;

    /// <summary>
    /// The header produced by a transition, or null when the transition is not allowed from the current state.
    /// Archive keeps <c>submitted_on</c> and Reopen clears it, so Unarchive returns a record to exactly the state it was
    /// archived from (Submitted if it was ever submitted and not reopened, otherwise Draft) without any extra signed column.
    /// </summary>
    public static (string Status, DateTimeOffset? SubmittedOn)? Plan(string currentStatus, DateTimeOffset? currentSubmittedOn, string transition, DateTimeOffset now) => (currentStatus, transition) switch
    {
        (Draft, Submit) => (Submitted, Integrity.SignedTimestamp.Truncate(now)),
        (Submitted, Reopen) => (Draft, null),
        (Draft, Archive) => (Archived, currentSubmittedOn),
        (Submitted, Archive) => (Archived, currentSubmittedOn),
        (Archived, Unarchive) => (currentSubmittedOn is null ? Draft : Submitted, currentSubmittedOn),
        _ => null,
    };

    /// <summary>The transitions an owner may start from a status (UI hint; the server re-decides).</summary>
    public static IReadOnlyList<string> AllowedFor(string status) => status switch
    {
        Draft => [Submit, Archive],
        Submitted => [Reopen, Archive],
        Archived => [Unarchive],
        _ => [],
    };

    /// <summary>Content changes (values, entries, documents, child records) are accepted only while the record is a Draft.</summary>
    public static bool IsContentEditable(string status) => string.Equals(status, Draft, StringComparison.Ordinal);

    /// <summary>
    /// Server-side lock of ordinary content changes. Applied to the Authority-verified header of the record, for owners and
    /// shared contributors alike; archive/unarchive/reopen are the only operations that may change a locked record's header.
    /// </summary>
    public static void RequireContentEditable(VerifiedRecord verified)
    {
        ArgumentNullException.ThrowIfNull(verified);
        if (!IsContentEditable(verified.Header.RecordStatus))
        {
            throw new BusinessException("RECORD_NOT_EDITABLE", 409, $"this record is {verified.Header.RecordStatus} and cannot be changed; the owner can reopen a submitted record or unarchive an archived one");
        }
    }

    public static string AuditActionFor(string transition) => transition switch
    {
        Submit => Domain.Features.Audit.AuditActions.RecordSubmitted,
        Reopen => Domain.Features.Audit.AuditActions.RecordReopened,
        Archive => Domain.Features.Audit.AuditActions.RecordArchived,
        Unarchive => Domain.Features.Audit.AuditActions.RecordUnarchived,
        _ => throw new ArgumentOutOfRangeException(nameof(transition)),
    };
}
