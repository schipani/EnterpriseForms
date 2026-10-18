namespace EnterpriseForms.Application.Features.FormRecords.Integrity;

/// <summary>
/// Timestamps that enter a signed canonical row are bound at microsecond precision (RowHmac/v1 rejects finer ticks).
/// Producers truncate before persisting so the stored value, the signed value and the PostgreSQL timestamptz value agree.
/// </summary>
public static class SignedTimestamp
{
    private const long TicksPerMicrosecond = TimeSpan.TicksPerMillisecond / 1000;

    public static DateTimeOffset Truncate(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % TicksPerMicrosecond), TimeSpan.Zero);
    }
}
