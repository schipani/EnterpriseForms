namespace EnterpriseForms.Domain.Features.Organizations;

/// <summary>
/// Validated organization branding (CLAUDE-DESIGN §2.3, ADR-PB-014). Values are the OUTPUT of server-side theme validation;
/// no tenant-authored CSS is stored. One row per tenant; optimistic concurrency by <see cref="BrandingRevision"/>.
/// </summary>
public sealed class OrganizationBranding
{
    public Guid OrganizationBrandingId { get; set; }

    public Guid TenantId { get; set; }

    public string OrganizationDisplayName { get; set; } = string.Empty;

    /// <summary>Re-encoded PNG bytes (server-side decode/re-encode, max 2 MiB, no SVG); null = text fallback.</summary>
    public byte[]? OrganizationLogoPng { get; set; }

    public string? LogoAlternativeText { get; set; }

    public string PrimaryButtonBackgroundColor { get; set; } = "#111111";

    public string PrimaryButtonForegroundColor { get; set; } = "#FFFFFF";

    public string SecondaryButtonBackgroundColor { get; set; } = "#FFFFFF";

    public string SecondaryButtonForegroundColor { get; set; } = "#111111";

    public string SecondaryButtonBorderColor { get; set; } = "#111111";

    public decimal OrganizationDefaultTextScalePercent { get; set; } = 100.0m;

    public string OrganizationColorMode { get; set; } = "Light";

    public long BrandingRevision { get; set; } = 1;

    public DateTimeOffset BrandingModifiedOn { get; set; }

    public Guid BrandingModifiedByUserId { get; set; }
}
