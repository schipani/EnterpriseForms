using System.Globalization;

namespace EnterpriseForms.Application.Common.Paging;

/// <summary>
/// Calendar buckets for dashboard charts: the last 7, 30 or 90 days ending today in the viewer's IANA time zone (UTC when
/// the zone is missing or unknown; the response names the zone actually used). 7 and 30 days use daily buckets; 90 days
/// uses 13 weekly buckets (91 days). Instants are compared as instants; only the bucket boundaries are local dates.
/// </summary>
public sealed class TimeBucketPlan
{
    public static readonly IReadOnlyList<int> SupportedPeriods = [7, 30, 90];

    private readonly TimeZoneInfo zone;

    private TimeBucketPlan(TimeZoneInfo zone, string zoneId, int periodDays, DateOnly firstDate, int bucketDayCount, int bucketCount)
    {
        this.zone = zone;
        TimeZoneId = zoneId;
        PeriodDays = periodDays;
        FirstDate = firstDate;
        BucketDayCount = bucketDayCount;
        BucketCount = bucketCount;
        StartInstant = AtLocalMidnight(firstDate);
    }

    public string TimeZoneId { get; }

    public int PeriodDays { get; }

    public DateOnly FirstDate { get; }

    public int BucketDayCount { get; }

    public int BucketCount { get; }

    /// <summary>The first instant counted (local midnight of <see cref="FirstDate"/>).</summary>
    public DateTimeOffset StartInstant { get; }

    public static TimeBucketPlan Create(DateTimeOffset now, int? periodDays, string? timeZoneId)
    {
        var period = periodDays is { } requested && SupportedPeriods.Contains(requested) ? requested : 30;
        var zone = TimeZoneInfo.Utc;
        var zoneId = "Etc/UTC";
        if (!string.IsNullOrWhiteSpace(timeZoneId) && timeZoneId.Length <= 64 && TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out var found))
        {
            zone = found;
            zoneId = timeZoneId;
        }

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var size = period <= 30 ? 1 : 7;
        var count = (period + size - 1) / size;
        return new TimeBucketPlan(zone, zoneId, period, today.AddDays(-((count * size) - 1)), size, count);
    }

    /// <summary>The bucket of an instant, or null when it falls outside the period.</summary>
    public int? IndexOf(DateTimeOffset instant)
    {
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);
        var days = date.DayNumber - FirstDate.DayNumber;
        if (days < 0)
        {
            return null;
        }

        var index = days / BucketDayCount;
        return index < BucketCount ? index : null;
    }

    public string BucketStartDate(int index) => FirstDate.AddDays(index * BucketDayCount).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private DateTimeOffset AtLocalMidnight(DateOnly date)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }
}
