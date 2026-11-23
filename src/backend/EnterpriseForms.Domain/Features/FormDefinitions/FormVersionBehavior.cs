namespace EnterpriseForms.Domain.Features.FormDefinitions;

/// <summary>
/// Per-form version behaviour (CLAUDE.md §3, Phase C9): which published layout a record is viewed and edited with.
/// Stored in <c>form_definitions.version_behavior</c> (CHECK-constrained); Latest is the default for new and existing forms.
/// </summary>
public static class VersionBehaviors
{
    /// <summary>View and edit with the version the record was created with.</summary>
    public const string Historical = "Historical";

    /// <summary>View and edit with the form's current published version.</summary>
    public const string Latest = "Latest";

    /// <summary>View with the version the record was created with; edit with the current published version.</summary>
    public const string Hybrid = "Hybrid";

    public static IReadOnlyList<string> All { get; } = [Historical, Latest, Hybrid];

    public static bool IsKnown(string? value) => value is Historical or Latest or Hybrid;
}

/// <summary>Why a record's layout is resolved: to display it, or to validate and store an edit (update, submit, restore, documents, child records).</summary>
public enum RecordLayoutPurpose
{
    View,
    Edit,
}

/// <summary>
/// The version-selection rule (CLAUDE.md §3), shared by every record read and write:
/// <list type="table">
/// <item><term>Historical</term><description>view and edit with the created version</description></item>
/// <item><term>Latest</term><description>view and edit with the current version</description></item>
/// <item><term>Hybrid</term><description>view with the created version, edit with the current version</description></item>
/// </list>
/// Editing fails closed: when the edit version cannot be determined (no current version, an unknown behaviour value) the
/// result is null and the caller refuses the write; another version is never substituted. Viewing keeps the earlier
/// tolerant behaviour (current, else created).
/// </summary>
public static class FormVersionResolution
{
    public static Guid? Resolve(string? versionBehavior, RecordLayoutPurpose purpose, Guid createdFormVersionId, Guid? currentFormVersionId) =>
        purpose switch
        {
            RecordLayoutPurpose.Edit => versionBehavior switch
            {
                VersionBehaviors.Historical => createdFormVersionId,
                VersionBehaviors.Latest or VersionBehaviors.Hybrid => currentFormVersionId,
                _ => null,
            },
            _ => versionBehavior switch
            {
                VersionBehaviors.Historical or VersionBehaviors.Hybrid => createdFormVersionId,
                _ => currentFormVersionId ?? createdFormVersionId,
            },
        };
}
