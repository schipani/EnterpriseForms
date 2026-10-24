namespace EnterpriseForms.Application.Features.Search.FieldSearch;

/// <summary>
/// Phase C7 D4: provisional engineering limits (configuration section <c>FieldSearch</c>), enforced server-side. They are
/// starting values to be measured (PB-09), not production limits. M0 enforces the request-shape limits; the record, byte
/// and time limits are enforced by the trusted executor once AC-5/AC-6 exist and are validated here so a bad configuration
/// fails at startup rather than at the first search.
/// </summary>
public sealed class FieldSearchLimits
{
    public const string SectionName = "FieldSearch";

    /// <summary>V1 hard ceiling for AND-combined conditions (D4/D6). Configuration may lower it, never raise it.</summary>
    public const int ApprovedMaximumConditions = 5;

    public int MaximumConditions { get; set; } = ApprovedMaximumConditions;

    public int MaximumEligibleRecords { get; set; } = 500;

    public long MaximumVerifiedBytes { get; set; } = 64L * 1024 * 1024;

    public int MaximumExecutionSeconds { get; set; } = 15;

    public int MaximumTextValueLength { get; set; } = 200;

    public int MinimumPartialTextLength { get; set; } = 3;

    public static readonly IReadOnlySet<int> PageSizes = new HashSet<int> { 10, 25, 50, 100 };

    public void Validate()
    {
        if (MaximumConditions is < 1 or > ApprovedMaximumConditions)
        {
            throw new InvalidOperationException($"FieldSearch:MaximumConditions must be 1..{ApprovedMaximumConditions} (D4/D6)");
        }

        if (MaximumEligibleRecords < 1 || MaximumVerifiedBytes < 1 || MaximumExecutionSeconds < 1)
        {
            throw new InvalidOperationException("FieldSearch record, byte and time limits must be positive");
        }

        if (MaximumTextValueLength < 1 || MinimumPartialTextLength < 1 || MinimumPartialTextLength > MaximumTextValueLength)
        {
            throw new InvalidOperationException("FieldSearch text limits are inconsistent");
        }
    }
}
