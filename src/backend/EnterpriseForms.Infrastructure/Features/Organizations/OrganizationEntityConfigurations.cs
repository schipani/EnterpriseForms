using EnterpriseForms.Domain.Features.Identity;
using EnterpriseForms.Domain.Features.Organizations;
using EnterpriseForms.Domain.Features.UserPreferences;
using EnterpriseForms.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EnterpriseForms.Infrastructure.Features.Organizations;

public sealed class OrganizationBrandingConfiguration : IEntityTypeConfiguration<OrganizationBranding>
{
    public void Configure(EntityTypeBuilder<OrganizationBranding> builder)
    {
        builder.MapSnakeCase("organization_brandings");
        builder.HasKey(b => b.OrganizationBrandingId);
        builder.Property(b => b.OrganizationDisplayName).HasMaxLength(200);
        builder.Property(b => b.LogoAlternativeText).HasMaxLength(200);
        builder.Property(b => b.PrimaryButtonBackgroundColor).HasMaxLength(7).IsFixedLength();
        builder.Property(b => b.PrimaryButtonForegroundColor).HasMaxLength(7).IsFixedLength();
        builder.Property(b => b.SecondaryButtonBackgroundColor).HasMaxLength(7).IsFixedLength();
        builder.Property(b => b.SecondaryButtonForegroundColor).HasMaxLength(7).IsFixedLength();
        builder.Property(b => b.SecondaryButtonBorderColor).HasMaxLength(7).IsFixedLength();
        builder.Property(b => b.OrganizationDefaultTextScalePercent).HasColumnType("numeric(5,1)");
        builder.Property(b => b.OrganizationColorMode).HasMaxLength(12);
        builder.HasIndex(b => b.TenantId).IsUnique().HasDatabaseName("uq_organization_brandings_tenant");
        builder.HasOne<Tenant>().WithMany().HasForeignKey(b => b.TenantId).HasConstraintName("fk_organization_brandings_tenant");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_organization_brandings_text_scale", "organization_default_text_scale_percent IN (100.0, 112.5, 125.0, 150.0)");
            t.HasCheckConstraint("ck_organization_brandings_color_mode", "organization_color_mode IN ('Light','Dark')");
            t.HasCheckConstraint("ck_organization_brandings_logo_size", "organization_logo_png IS NULL OR octet_length(organization_logo_png) <= 2097152");
        });
    }
}

public sealed class OrganizationSecurityPolicyConfiguration : IEntityTypeConfiguration<OrganizationSecurityPolicy>
{
    public void Configure(EntityTypeBuilder<OrganizationSecurityPolicy> builder)
    {
        builder.MapSnakeCase("organization_security_policies");
        builder.Property(p => p.PlaintextDowngradePermitted).HasDefaultValue(false);
        builder.HasKey(p => p.OrganizationSecurityPolicyId);
        builder.Property(p => p.MinimumFieldClassification).HasMaxLength(20);
        builder.Property(p => p.PolicyStatementText).HasMaxLength(2000);
        builder.HasIndex(p => p.TenantId).IsUnique().HasDatabaseName("uq_organization_security_policies_tenant");
        builder.HasOne<Tenant>().WithMany().HasForeignKey(p => p.TenantId).HasConstraintName("fk_organization_security_policies_tenant");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_organization_security_policies_minimum", "minimum_field_classification IN ('Public','Internal','Confidential','Restricted')");
            t.HasCheckConstraint("ck_organization_security_policies_revision", "security_policy_revision >= 1");
        });
    }
}

public sealed class UserUiPreferenceConfiguration : IEntityTypeConfiguration<UserUiPreference>
{
    public void Configure(EntityTypeBuilder<UserUiPreference> builder)
    {
        builder.MapSnakeCase("user_ui_preferences");
        builder.HasKey(p => p.UserUiPreferenceId);
        builder.Property(p => p.TextScalePreferenceMode).HasMaxLength(20);
        builder.Property(p => p.ExplicitTextScalePercent).HasColumnType("numeric(5,1)");
        builder.HasIndex(p => p.UserId).IsUnique().HasDatabaseName("uq_user_ui_preferences_user");
        builder.HasOne<Tenant>().WithMany().HasForeignKey(p => p.TenantId).HasConstraintName("fk_user_ui_preferences_tenant");
        builder.HasOne<User>().WithMany().HasForeignKey(p => p.UserId).HasConstraintName("fk_user_ui_preferences_user");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_user_ui_preferences_mode", "text_scale_preference_mode IN ('OrganizationDefault','BrowserDefault','Explicit')");
            t.HasCheckConstraint("ck_user_ui_preferences_explicit", "(text_scale_preference_mode = 'Explicit') = (explicit_text_scale_percent IS NOT NULL)");
            t.HasCheckConstraint("ck_user_ui_preferences_scale", "explicit_text_scale_percent IS NULL OR explicit_text_scale_percent IN (100.0, 112.5, 125.0, 150.0)");
        });
    }
}

public sealed class OrganizationSettingConfiguration : IEntityTypeConfiguration<OrganizationSetting>
{
    public void Configure(EntityTypeBuilder<OrganizationSetting> builder)
    {
        builder.MapSnakeCase("organization_settings");
        builder.HasKey(s => s.OrganizationSettingId);
        builder.Property(s => s.DefaultCountryCode).HasMaxLength(2).IsFixedLength();
        builder.Property(s => s.DefaultIanaTimeZoneId).HasMaxLength(64);
        builder.Property(s => s.DefaultLanguageCode).HasMaxLength(12);
        builder.Property(s => s.DefaultDateFormatPresetKey).HasMaxLength(32);
        builder.Property(s => s.DefaultNumberFormatPresetKey).HasMaxLength(32);
        builder.HasIndex(s => s.TenantId).IsUnique().HasDatabaseName("uq_organization_settings_tenant");
        builder.HasOne<Tenant>().WithMany().HasForeignKey(s => s.TenantId).HasConstraintName("fk_organization_settings_tenant");
        builder.ToTable(t => t.HasCheckConstraint("ck_organization_settings_revision", "organization_setting_revision >= 1"));
    }
}

public sealed class UserPreferenceConfiguration : IEntityTypeConfiguration<UserPreference>
{
    private const string Modes = "('OrganizationDefault','BrowserDefault','Explicit')";

    public void Configure(EntityTypeBuilder<UserPreference> builder)
    {
        builder.MapSnakeCase("user_preferences");
        builder.HasKey(p => p.UserPreferenceId);
        builder.Property(p => p.TimeZonePreferenceMode).HasMaxLength(20);
        builder.Property(p => p.IanaTimeZoneId).HasMaxLength(64);
        builder.Property(p => p.DateFormatPreferenceMode).HasMaxLength(20);
        builder.Property(p => p.DateFormatPresetKey).HasMaxLength(32);
        builder.Property(p => p.NumberFormatPreferenceMode).HasMaxLength(20);
        builder.Property(p => p.NumberFormatPresetKey).HasMaxLength(32);
        builder.Property(p => p.LanguagePreferenceMode).HasMaxLength(20);
        builder.Property(p => p.LanguageCode).HasMaxLength(12);
        builder.HasIndex(p => new { p.TenantId, p.UserId }).IsUnique().HasDatabaseName("uq_user_preferences_tenant_user");
        builder.HasOne<Tenant>().WithMany().HasForeignKey(p => p.TenantId).HasConstraintName("fk_user_preferences_tenant");
        builder.HasOne<User>().WithMany().HasForeignKey(p => p.UserId).HasConstraintName("fk_user_preferences_user");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_user_preferences_time_zone", $"time_zone_preference_mode IN {Modes} AND (time_zone_preference_mode = 'Explicit') = (iana_time_zone_id IS NOT NULL)");
            t.HasCheckConstraint("ck_user_preferences_date_format", $"date_format_preference_mode IN {Modes} AND (date_format_preference_mode = 'Explicit') = (date_format_preset_key IS NOT NULL)");
            t.HasCheckConstraint("ck_user_preferences_number_format", $"number_format_preference_mode IN {Modes} AND (number_format_preference_mode = 'Explicit') = (number_format_preset_key IS NOT NULL)");
            t.HasCheckConstraint("ck_user_preferences_language", $"language_preference_mode IN {Modes} AND (language_preference_mode = 'Explicit') = (language_code IS NOT NULL)");
            t.HasCheckConstraint("ck_user_preferences_revision", "preference_revision >= 1");
        });
    }
}
