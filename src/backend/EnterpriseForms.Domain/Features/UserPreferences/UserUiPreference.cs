namespace EnterpriseForms.Domain.Features.UserPreferences;

/// <summary>User-scoped presentation preference (CLAUDE.md §4E subset for Stage B: text scale). Never affects stored data or authorization.</summary>
public sealed class UserUiPreference
{
    public Guid UserUiPreferenceId { get; set; }

    public Guid TenantId { get; set; }

    public Guid UserId { get; set; }

    /// <summary>OrganizationDefault | BrowserDefault | Explicit</summary>
    public string TextScalePreferenceMode { get; set; } = "OrganizationDefault";

    public decimal? ExplicitTextScalePercent { get; set; }

    public long PreferenceRevision { get; set; } = 1;

    public DateTimeOffset PreferenceModifiedOn { get; set; }
}
