using System.Globalization;
using EnterpriseForms.Application.Common;
using EnterpriseForms.Application.Common.Authority;
using EnterpriseForms.Application.Common.Persistence;
using EnterpriseForms.Contracts.Features;
using EnterpriseForms.Domain.Features.Organizations;
using EnterpriseForms.Domain.Features.UserPreferences;

namespace EnterpriseForms.Application.Features.Organizations;

/// <summary>
/// Organization branding (PG-042, ADR-PB-014) and user text-size preference (PG-007 subset). Colours are validated
/// pairwise against WCAG contrast before they become effective; a failing theme is rejected and the previous safe
/// theme stays in force. Only the OrganizationOwner (authority manage_configuration) may change branding.
/// </summary>
public sealed class BrandingHandlers(IBusinessStore store, IAuthorizationAuthorityClient authority, TimeProvider clock)
{
    private static readonly decimal[] AllowedScales = [100.0m, 112.5m, 125.0m, 150.0m];

    public async Task<BrandingResponse> GetAsync(RequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var branding = await scope.FindBrandingAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false) ?? Default(context);
        return ToResponse(branding);
    }

    public async Task<byte[]?> GetLogoAsync(RequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        return (await scope.FindBrandingAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false))?.OrganizationLogoPng;
    }

    public async Task<BrandingResponse> UpdateAsync(RequestContext context, UpdateBrandingRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        await RequireOrganizationOwnerAsync(context, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(request.OrganizationDisplayName) || request.OrganizationDisplayName.Length > 200 || (request.LogoAlternativeText?.Length ?? 0) > 200)
        {
            throw new BusinessException("INVALID_REQUEST", 422, "organization display name (max 200) is required");
        }

        if (!AllowedScales.Contains(request.OrganizationDefaultTextScalePercent) || request.OrganizationColorMode is not ("Light" or "Dark"))
        {
            throw new BusinessException("INVALID_REQUEST", 422, "text scale must be one of 100, 112.5, 125, 150 and color mode Light or Dark");
        }

        var failures = ThemeContrast.Validate(request.PrimaryButtonBackgroundColor, request.PrimaryButtonForegroundColor, request.SecondaryButtonBackgroundColor, request.SecondaryButtonForegroundColor, request.SecondaryButtonBorderColor, request.OrganizationColorMode);
        if (failures.Count > 0)
        {
            throw new BusinessException("THEME_CONTRAST_FAILED", 422, "theme rejected: " + string.Join("; ", failures));
        }

        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var branding = await scope.FindBrandingAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        if (branding is null)
        {
            branding = Default(context);
            branding.BrandingRevision = 0;
            scope.AddBranding(branding);
        }

        if (branding.BrandingRevision != request.ExpectedBrandingRevision && !(branding.BrandingRevision == 0 && request.ExpectedBrandingRevision == 1))
        {
            throw new BusinessException("BRANDING_REVISION_CONFLICT", 409, "branding was changed by someone else");
        }

        branding.OrganizationDisplayName = request.OrganizationDisplayName.Trim();
        branding.LogoAlternativeText = request.LogoAlternativeText?.Trim();
        branding.PrimaryButtonBackgroundColor = ThemeContrast.Normalize(request.PrimaryButtonBackgroundColor);
        branding.PrimaryButtonForegroundColor = ThemeContrast.Normalize(request.PrimaryButtonForegroundColor);
        branding.SecondaryButtonBackgroundColor = ThemeContrast.Normalize(request.SecondaryButtonBackgroundColor);
        branding.SecondaryButtonForegroundColor = ThemeContrast.Normalize(request.SecondaryButtonForegroundColor);
        branding.SecondaryButtonBorderColor = ThemeContrast.Normalize(request.SecondaryButtonBorderColor);
        branding.OrganizationDefaultTextScalePercent = request.OrganizationDefaultTextScalePercent;
        branding.OrganizationColorMode = request.OrganizationColorMode;
        branding.BrandingRevision++;
        branding.BrandingModifiedOn = now;
        branding.BrandingModifiedByUserId = context.LocalUserId;
        Audit.AuditRecorder.Record(scope, context, clock, "Organization", "ORGANIZATION_SETTINGS_UPDATED", "organization branding updated", targetEntityType: "organization_branding", targetEntityId: branding.OrganizationBrandingId, metadata: new { revision = branding.BrandingRevision });
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToResponse(branding);
    }

    /// <summary>PNG only, decoded header validated, size-capped; stored bytes are the uploaded PNG (re-encoding is a PB-14 follow-up, noted in STAGE_B_STATUS).</summary>
    public async Task<BrandingResponse> UploadLogoAsync(RequestContext context, byte[] png, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(png);
        await RequireOrganizationOwnerAsync(context, cancellationToken).ConfigureAwait(false);
        if (png.Length is < 8 or > 2 * 1024 * 1024 || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            throw new BusinessException("INVALID_REQUEST", 422, "logo must be a PNG of at most 2 MiB");
        }

        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var branding = await scope.FindBrandingAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false);
        if (branding is null)
        {
            branding = Default(context);
            scope.AddBranding(branding);
        }

        branding.OrganizationLogoPng = png;
        branding.BrandingRevision++;
        branding.BrandingModifiedOn = clock.GetUtcNow();
        branding.BrandingModifiedByUserId = context.LocalUserId;
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToResponse(branding);
    }

    public async Task<UiPreferenceResponse> GetPreferenceAsync(RequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        var branding = await scope.FindBrandingAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false);
        var preference = await scope.FindUiPreferenceAsync(context.LocalUserId, cancellationToken).ConfigureAwait(false);
        return ToPreference(preference, branding?.OrganizationDefaultTextScalePercent ?? 100.0m);
    }

    public async Task<UiPreferenceResponse> UpdatePreferenceAsync(RequestContext context, UpdateUiPreferenceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        if (request.TextScalePreferenceMode is not ("OrganizationDefault" or "BrowserDefault" or "Explicit") || (request.TextScalePreferenceMode == "Explicit") != request.ExplicitTextScalePercent.HasValue
            || (request.ExplicitTextScalePercent is { } s && !AllowedScales.Contains(s)))
        {
            throw new BusinessException("INVALID_REQUEST", 422, "mode must be OrganizationDefault, BrowserDefault or Explicit (with an allowed scale)");
        }

        await using var scope = await store.BeginScopeAsync(cancellationToken).ConfigureAwait(false);
        await scope.EnsureTenantAndUserProjectionAsync(context.OrganizationId, context.LocalUserId, context.IssuerUri, context.SubjectIdentifier, context.DisplayName, cancellationToken).ConfigureAwait(false);
        var preference = await scope.FindUiPreferenceAsync(context.LocalUserId, cancellationToken).ConfigureAwait(false);
        if (preference is null)
        {
            preference = new UserUiPreference { UserUiPreferenceId = Guid.CreateVersion7(), TenantId = context.OrganizationId, UserId = context.LocalUserId, PreferenceRevision = 0 };
            scope.AddUiPreference(preference);
        }

        preference.TextScalePreferenceMode = request.TextScalePreferenceMode;
        preference.ExplicitTextScalePercent = request.ExplicitTextScalePercent;
        preference.PreferenceRevision++;
        preference.PreferenceModifiedOn = clock.GetUtcNow();
        var branding = await scope.FindBrandingAsync(context.OrganizationId, cancellationToken).ConfigureAwait(false);
        await scope.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToPreference(preference, branding?.OrganizationDefaultTextScalePercent ?? 100.0m);
    }

    private async Task RequireOrganizationOwnerAsync(RequestContext context, CancellationToken cancellationToken)
    {
        var decision = await authority.CheckPermissionAsync(context.ToDelegatedUser(), new PermissionCheckRequest(context.OrganizationId, new ResourceReference(ProtectedResourceKind.Organization, context.OrganizationId), ProtectedAction.ManageOrganization), cancellationToken).ConfigureAwait(false);
        if (!decision.Allowed)
        {
            throw new BusinessException("FORBIDDEN", 403, "organization owner role required");
        }
    }

    private static OrganizationBranding Default(RequestContext context) => new()
    {
        OrganizationBrandingId = Guid.CreateVersion7(),
        TenantId = context.OrganizationId,
        OrganizationDisplayName = "Organization " + context.OrganizationId.ToString("N")[..8],
        BrandingRevision = 1,
        BrandingModifiedOn = DateTimeOffset.UnixEpoch,
        BrandingModifiedByUserId = Guid.Empty,
    };

    private static BrandingResponse ToResponse(OrganizationBranding b) =>
        new(b.OrganizationDisplayName, b.OrganizationLogoPng is not null, b.LogoAlternativeText, b.PrimaryButtonBackgroundColor, b.PrimaryButtonForegroundColor, b.SecondaryButtonBackgroundColor, b.SecondaryButtonForegroundColor, b.SecondaryButtonBorderColor, b.OrganizationDefaultTextScalePercent, b.OrganizationColorMode, b.BrandingRevision);

    private static UiPreferenceResponse ToPreference(UserUiPreference? p, decimal organizationDefault)
    {
        var mode = p?.TextScalePreferenceMode ?? "OrganizationDefault";
        var effective = mode == "Explicit" ? p!.ExplicitTextScalePercent!.Value : mode == "BrowserDefault" ? 100.0m : organizationDefault;
        return new UiPreferenceResponse(mode, p?.ExplicitTextScalePercent, effective, p?.PreferenceRevision ?? 0);
    }
}

/// <summary>WCAG 2.x contrast rules shared with the frontend design-tokens package (same algorithm, same thresholds).</summary>
public static class ThemeContrast
{
    public static string Normalize(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex) || !System.Text.RegularExpressions.Regex.IsMatch(hex, "^#[0-9a-fA-F]{6}$"))
        {
            throw new BusinessException("INVALID_REQUEST", 422, "colours must be #RRGGBB");
        }

        return hex.ToUpperInvariant();
    }

    public static double Ratio(string foreground, string background)
    {
        var f = Luminance(foreground);
        var b = Luminance(background);
        return (Math.Max(f, b) + 0.05) / (Math.Min(f, b) + 0.05);
    }

    public static List<string> Validate(string primaryBg, string primaryFg, string secondaryBg, string secondaryFg, string secondaryBorder, string colorMode)
    {
        var page = colorMode == "Dark" ? "#0F172A" : "#F8FAFC";
        var surface = colorMode == "Dark" ? "#1E293B" : "#FFFFFF";
        var failures = new List<string>();
        void Check(string name, string fg, string bg, double required)
        {
            var ratio = Ratio(Normalize(fg), Normalize(bg));
            if (ratio + 1e-9 < required)
            {
                failures.Add($"{name} {ratio.ToString("0.00", CultureInfo.InvariantCulture)}:1 < {required.ToString("0.0", CultureInfo.InvariantCulture)}:1");
            }
        }

        Check("primary button text", primaryFg, primaryBg, 4.5);
        Check("primary button boundary on surface", primaryBg, surface, 3.0);
        Check("primary button boundary on page", primaryBg, page, 3.0);
        Check("secondary button text", secondaryFg, secondaryBg, 4.5);
        Check("secondary button border on surface", secondaryBorder, surface, 3.0);
        Check("secondary button border on its background", secondaryBorder, secondaryBg, 3.0);
        return failures;
    }

    private static double Luminance(string hex)
    {
        double Channel(int offset)
        {
            var value = int.Parse(hex.AsSpan(offset, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(1) + 0.7152 * Channel(3) + 0.0722 * Channel(5);
    }
}
