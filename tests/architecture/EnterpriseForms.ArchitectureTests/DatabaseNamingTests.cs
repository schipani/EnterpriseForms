using EnterpriseForms.Infrastructure.Persistence;
using EnterpriseForms.Infrastructure.Persistence.Conventions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EnterpriseForms.ArchitectureTests;

/// <summary>Acceptance test 28: schema-level rejection of single-word and misnamed columns.</summary>
public sealed class DatabaseNamingTests
{
    [Theory]
    [InlineData("name")]
    [InlineData("email")]
    [InlineData("value")]
    [InlineData("status")]
    [InlineData("type")]
    [InlineData("label")]
    [InlineData("description")]
    [InlineData("revision")]
    [InlineData("action")]
    [InlineData("reason")]
    [InlineData("title")]
    [InlineData("key")]
    public void SingleWordColumnsAreRejected(string column)
    {
        var violation = DatabaseColumnNamingRules.EvaluateColumn("form_records", column, isPrimaryOrForeignKey: false);
        Assert.NotNull(violation);
    }

    [Theory]
    [InlineData("email_address")]
    [InlineData("team_name")]
    [InlineData("string_value")]
    [InlineData("record_status")]
    [InlineData("draft_revision")]
    [InlineData("audit_action")]
    [InlineData("row_signature_key_version")]
    public void DescriptiveMultiwordColumnsAreAccepted(string column)
    {
        Assert.Null(DatabaseColumnNamingRules.EvaluateColumn("form_records", column, isPrimaryOrForeignKey: false));
    }

    [Theory]
    [InlineData("FormRecordId")]
    [InlineData("form-record-id")]
    [InlineData("Form_Record")]
    [InlineData("\"name\"")]
    public void NonSnakeCaseIsRejected(string column)
    {
        var violation = DatabaseColumnNamingRules.EvaluateColumn("form_records", column, isPrimaryOrForeignKey: false);
        Assert.Equal("NAME-SNAKE", violation?.RuleCode);
    }

    [Theory]
    [InlineData("form_definitions", "form_definition_id")]
    [InlineData("field_string_values", "field_string_value_id")]
    [InlineData("record_access_challenges", "record_access_challenge_id")]
    [InlineData("countries", "country_id")]
    [InlineData("audit_event_outbox", "audit_event_outbox_id")]
    [InlineData("organization_catalog_option_policies", "organization_catalog_option_policy_id")]
    public void PrimaryKeyFollowsSingularEntityNamePlusId(string table, string pk)
    {
        Assert.Null(DatabaseColumnNamingRules.EvaluatePrimaryKey(table, pk));
    }

    [Theory]
    [InlineData("form_records", "id")]
    [InlineData("form_records", "record_id")]
    [InlineData("field_definitions", "field_id")]
    public void ShortenedPrimaryKeysAreRejected(string table, string pk)
    {
        Assert.NotNull(DatabaseColumnNamingRules.EvaluatePrimaryKey(table, pk));
    }

    [Fact]
    public void KeyColumnsRequireIdSuffix()
    {
        Assert.Null(DatabaseColumnNamingRules.EvaluateColumn("form_records", "owner_user_id", isPrimaryOrForeignKey: true));
        Assert.NotNull(DatabaseColumnNamingRules.EvaluateColumn("form_records", "owner_user", isPrimaryOrForeignKey: true));
        Assert.NotNull(DatabaseColumnNamingRules.EvaluateColumn("form_records", "id", isPrimaryOrForeignKey: true));
    }

    [Fact]
    public void CompiledBusinessModelHasNoNamingViolations()
    {
        var options = new DbContextOptionsBuilder<BusinessDbContext>()
            .UseNpgsql("Host=model-only.invalid;Database=unused;Username=unused;Password=unused")
            .Options;
        using var context = new BusinessDbContext(options);
        var violations = ModelNamingValidator.Validate(context.Model);
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations.Select(v => $"{v.ObjectName}: {v.RuleCode} ({v.Detail})")));
    }
}
