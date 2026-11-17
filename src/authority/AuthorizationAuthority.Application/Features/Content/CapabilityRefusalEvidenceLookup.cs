using System.Globalization;
using System.Text;
using System.Text.Json;
using AuthorizationAuthority.Application.Common;
using AuthorizationAuthority.Application.Common.Ports;

namespace AuthorizationAuthority.Application.Features.Content;

/// <summary>One evidence key (closure §5.1): seven exist per organization for the one refusal code in scope.</summary>
public sealed record CapabilityRefusalEvidenceKey(Guid OrganizationId, string WorkloadName, string EntryPoint, string RefusalCode)
{
    public static CapabilityRefusalEvidenceKey For(Guid organizationId, string entryPoint) =>
        new(organizationId, CapabilityRefusalEvidence.WorkloadOf(entryPoint), entryPoint, CapabilityRefusalEvidence.RefusalCode);

    /// <summary>LK-9: one of the seven approved keys (known entry point, its own workload, the one refusal code).</summary>
    public bool IsApproved =>
        CapabilityRefusalEvidence.EntryPoints.All.Contains(EntryPoint, StringComparer.Ordinal)
        && string.Equals(WorkloadName, CapabilityRefusalEvidence.WorkloadOf(EntryPoint), StringComparison.Ordinal)
        && string.Equals(RefusalCode, CapabilityRefusalEvidence.RefusalCode, StringComparison.Ordinal);
}

/// <summary>Why an attempt did not record (closure §5.3, §5.7, §10.4 reasons).</summary>
public enum CapabilityRefusalEvidenceReason
{
    None,
    IndexUnavailable,
    BoundInconsistent,
    ClockRegression,
    TimeOrderViolation,
    UnverifiableBody,
    Timeout,
    Error,
    AttemptLimit,
    RetryCapacity,
    Abandoned,
    Ambiguous,
}

public enum CapabilityRefusalEvidenceVerdict
{
    /// <summary>Conclusive, and no entry exists for the key in the bucket: append.</summary>
    Append,

    /// <summary>Conclusive, and exactly one entry exists for the key in the bucket: nothing to write.</summary>
    AlreadyRecorded,

    /// <summary>Absence cannot be proven: never append (D-7).</summary>
    Inconclusive,
}

/// <summary>One verified evidence entry as the lookup sees it.</summary>
public sealed record CapabilityRefusalEvidenceEntry(long Revision, CapabilityRefusalEvidenceKey Key, DateTimeOffset DatabaseTime, DateTimeOffset Bucket);

/// <summary>
/// E3-M2f-3b: the pure lookup decision LK-3..LK-7 (docs/security/E3_M2F3_CONTRACT_CLOSURE.md §5.3) over the rows LK-1 returned
/// (newest first, at most <see cref="CapabilityRefusalEvidence.LookupLimit"/>). Every row must verify (digest, closed schema,
/// organization, kind, stream, bucket); times must be non-increasing; the newest must not be later than <c>t_db</c>; and the read must
/// reach either the organization's oldest evidence entry (fewer than eight rows) or an entry of an earlier bucket. Only then can the
/// absence of the key in the current bucket be proven (closure §5.4, I-1, L-A, L-B).
/// </summary>
public static class CapabilityRefusalEvidenceLookup
{
    public static (CapabilityRefusalEvidenceVerdict Verdict, CapabilityRefusalEvidenceReason Reason) Decide(
        IReadOnlyList<CapabilityRefusalEvidenceRow> rows, CapabilityRefusalEvidenceKey key, DateTimeOffset databaseTime)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(key);
        if (rows.Count > CapabilityRefusalEvidence.LookupLimit)
        {
            return Inconclusive(CapabilityRefusalEvidenceReason.BoundInconsistent);
        }

        var entries = new List<CapabilityRefusalEvidenceEntry>(rows.Count);
        long? previousRevision = null;
        foreach (var row in rows)
        {
            if (previousRevision is { } p && row.JournalStreamRevision >= p)
            {
                return Inconclusive(CapabilityRefusalEvidenceReason.UnverifiableBody); // not newest-first by revision
            }

            previousRevision = row.JournalStreamRevision;
            if (Verify(row, key.OrganizationId) is not { } entry)
            {
                return Inconclusive(CapabilityRefusalEvidenceReason.UnverifiableBody); // LK-3
            }

            entries.Add(entry);
        }

        if (entries.Count > 0 && entries[0].DatabaseTime > databaseTime)
        {
            return Inconclusive(CapabilityRefusalEvidenceReason.ClockRegression); // LK-6
        }

        for (var i = 1; i < entries.Count; i++)
        {
            if (entries[i].DatabaseTime > entries[i - 1].DatabaseTime)
            {
                return Inconclusive(CapabilityRefusalEvidenceReason.TimeOrderViolation); // LK-4
            }
        }

        var bucket = CapabilityRefusalEvidence.BucketOf(databaseTime);
        if (entries.Count >= CapabilityRefusalEvidence.LookupLimit && !entries.Any(e => e.Bucket < bucket))
        {
            return Inconclusive(CapabilityRefusalEvidenceReason.BoundInconsistent); // LK-5: eight entries, none older than the bucket
        }

        var current = entries.Where(e => e.Bucket == bucket).ToList();
        if (current.GroupBy(e => e.Key).Any(g => g.Count() > 1))
        {
            return Inconclusive(CapabilityRefusalEvidenceReason.BoundInconsistent); // LK-7: a duplicate would contradict L-B
        }

        return current.Any(e => e.Key == key)
            ? (CapabilityRefusalEvidenceVerdict.AlreadyRecorded, CapabilityRefusalEvidenceReason.None)
            : (CapabilityRefusalEvidenceVerdict.Append, CapabilityRefusalEvidenceReason.None);
    }

    /// <summary>
    /// LK-3: the row's body matches its stored digest and is an <c>EnvelopeCapabilityRefused</c> envelope of the audit stream of this
    /// organization whose body has exactly the closed schema of closure §9.2 with an approved key; otherwise null.
    /// </summary>
    public static CapabilityRefusalEvidenceEntry? Verify(CapabilityRefusalEvidenceRow row, Guid organizationId)
    {
        ArgumentNullException.ThrowIfNull(row);
        var bytes = Encoding.UTF8.GetBytes(row.JournalEventBodyJson);
        if (row.JournalEventDigest is not { Length: 32 } || !CanonicalJson.Digest(bytes).AsSpan().SequenceEqual(row.JournalEventDigest))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || root.GetProperty("event_kind").GetString() != CapabilityRefusalEvidence.EventKind
                || root.GetProperty("stream_name").GetString() != CapabilityRefusalEvidence.StreamName
                || root.GetProperty("stream_revision").GetInt64() != row.JournalStreamRevision
                || root.GetProperty("organization_id").GetGuid() != organizationId
                || root.GetProperty("body") is not { ValueKind: JsonValueKind.Object } body)
            {
                return null;
            }

            var names = body.EnumerateObject().Select(p => p.Name).ToList();
            if (names.Count != CapabilityRefusalEvidenceBody.MemberNames.Count || !CapabilityRefusalEvidenceBody.MemberNames.All(names.Contains))
            {
                return null; // closed schema: no missing and no additional member
            }

            var key = new CapabilityRefusalEvidenceKey(body.GetProperty("organization_id").GetGuid(), body.GetProperty("workload_name").GetString()!,
                body.GetProperty("entry_point").GetString()!, body.GetProperty("refusal_code").GetString()!);
            var databaseTime = body.GetProperty("evidence_database_time_utc").GetDateTimeOffset();
            var bucket = body.GetProperty("evidence_bucket_start_utc").GetDateTimeOffset();
            _ = body.GetProperty("refusal_observed_on_utc").GetDateTimeOffset();
            if (body.GetProperty("evidence_schema_version").GetInt32() != CapabilityRefusalEvidence.SchemaVersion
                || body.GetProperty("client_capability_level").GetInt32() != ClientEnvelopeCapability.Legacy.Level
                || key.OrganizationId != organizationId || !key.IsApproved
                || bucket != CapabilityRefusalEvidence.BucketOf(databaseTime))
            {
                return null;
            }

            return new CapabilityRefusalEvidenceEntry(row.JournalStreamRevision, key, databaseTime, bucket);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static (CapabilityRefusalEvidenceVerdict, CapabilityRefusalEvidenceReason) Inconclusive(CapabilityRefusalEvidenceReason reason) =>
        (CapabilityRefusalEvidenceVerdict.Inconclusive, reason);

    /// <summary>Invariant rendering for logs (no identifiers beyond the reason).</summary>
    public static string Name(CapabilityRefusalEvidenceReason reason) => reason switch
    {
        CapabilityRefusalEvidenceReason.IndexUnavailable => "INDEX_UNAVAILABLE",
        CapabilityRefusalEvidenceReason.BoundInconsistent => "BOUND_INCONSISTENT",
        CapabilityRefusalEvidenceReason.ClockRegression => "CLOCK_REGRESSION",
        CapabilityRefusalEvidenceReason.TimeOrderViolation => "TIME_ORDER_VIOLATION",
        CapabilityRefusalEvidenceReason.UnverifiableBody => "UNVERIFIABLE_BODY",
        CapabilityRefusalEvidenceReason.Timeout => "TIMEOUT",
        CapabilityRefusalEvidenceReason.Error => "ERROR",
        CapabilityRefusalEvidenceReason.AttemptLimit => "ATTEMPT_LIMIT",
        CapabilityRefusalEvidenceReason.RetryCapacity => "RETRY_CAPACITY",
        CapabilityRefusalEvidenceReason.Abandoned => "ABANDONED",
        CapabilityRefusalEvidenceReason.Ambiguous => "AMBIGUOUS_COMMIT",
        _ => reason.ToString().ToUpper(CultureInfo.InvariantCulture),
    };
}
