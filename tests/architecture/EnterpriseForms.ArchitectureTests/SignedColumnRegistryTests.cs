using EnterpriseForms.Application.Features.FormRecords.Integrity;
using EnterpriseForms.Infrastructure.Persistence;
using EnterpriseForms.Integrity.Canonical;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace EnterpriseForms.ArchitectureTests;

/// <summary>
/// SEC-42 / acceptance test 82 / G18: every integrity-protected business table has a signed-column registry entry
/// whose columns exist in the compiled EF model, and every security-significant column of that table is signed.
/// Columns that are deliberately unsigned must be listed here with a reason (local untrusted projections / integrity envelope).
/// </summary>
public sealed class SignedColumnRegistryTests
{
    /// <summary>Protected table → registry entry; the authority signs under these entity kinds only (ContentService.AllowedEntityKinds).</summary>
    private static readonly Dictionary<string, (RowSchemaDefinition Schema, string EfTable, string[] UnsignedWithReason)> Protected = new(StringComparer.Ordinal)
    {
        ["form_records"] = (BusinessSignedSchemas.FormRecordsV3, "form_record_revisions", new[]
        {
            "form_record_revision_id:physical surrogate of the staged copy; the canonical pk is form_record_id",
            "form_record_id:bound as the canonical primary key",
            "content_revision:bound as row_revision",
            "row_revision:integrity envelope",
            "row_signature:integrity envelope",
            "row_signature_key_version:integrity envelope (authenticated via key_version)",
            "row_signature_schema_version:integrity envelope (authenticated via schema_version)",
            "candidate_state:local untrusted hint; the authority head decides visibility",
            "staged_on:local timestamp, not content",
        }),
        ["field_string_values"] = (BusinessSignedSchemas.FieldStringValuesV1, "field_string_values", new[]
        {
            "field_string_value_id:bound as the canonical primary key",
            "content_revision:bound as row_revision",
            "row_revision:integrity envelope",
            "row_signature:integrity envelope",
            "row_signature_key_version:integrity envelope",
            "row_signature_schema_version:integrity envelope",
            "created_on:audit timestamp of the physical copy",
            "created_by_user_id:audit actor of the physical copy; the authority journal is the trusted actor record",
        }),
        ["field_integer_values"] = (BusinessSignedSchemas.FieldIntegerValuesV1, "field_integer_values", new[]
        {
            "field_integer_value_id:bound as the canonical primary key",
            "content_revision:bound as row_revision",
            "row_revision:integrity envelope",
            "row_signature:integrity envelope",
            "row_signature_key_version:integrity envelope",
            "row_signature_schema_version:integrity envelope",
            "created_on:audit timestamp of the physical copy",
            "created_by_user_id:audit actor of the physical copy; the authority journal is the trusted actor record",
        }),
        ["field_decimal_values"] = (BusinessSignedSchemas.FieldDecimalValuesV1, "field_decimal_values", new[]
        {
            "field_decimal_value_id:bound as the canonical primary key",
            "content_revision:bound as row_revision",
            "row_revision:integrity envelope",
            "row_signature:integrity envelope",
            "row_signature_key_version:integrity envelope",
            "row_signature_schema_version:integrity envelope",
            "created_on:audit timestamp of the physical copy",
            "created_by_user_id:audit actor of the physical copy; the authority journal is the trusted actor record",
        }),
        ["field_date_values"] = (BusinessSignedSchemas.FieldDateValuesV1, "field_date_values", new[]
        {
            "field_date_value_id:bound as the canonical primary key",
            "content_revision:bound as row_revision",
            "row_revision:integrity envelope",
            "row_signature:integrity envelope",
            "row_signature_key_version:integrity envelope",
            "row_signature_schema_version:integrity envelope",
            "created_on:audit timestamp of the physical copy",
            "created_by_user_id:audit actor of the physical copy; the authority journal is the trusted actor record",
        }),
        ["field_date_time_values"] = (BusinessSignedSchemas.FieldDateTimeValuesV1, "field_date_time_values", new[]
        {
            "field_date_time_value_id:bound as the canonical primary key",
            "content_revision:bound as row_revision",
            "row_revision:integrity envelope",
            "row_signature:integrity envelope",
            "row_signature_key_version:integrity envelope",
            "row_signature_schema_version:integrity envelope",
            "created_on:audit timestamp of the physical copy",
            "created_by_user_id:audit actor of the physical copy; the authority journal is the trusted actor record",
        }),
        ["field_guid_values"] = (BusinessSignedSchemas.FieldGuidValuesV1, "field_guid_values", new[]
        {
            "field_guid_value_id:bound as the canonical primary key",
            "content_revision:bound as row_revision",
            "row_revision:integrity envelope",
            "row_signature:integrity envelope",
            "row_signature_key_version:integrity envelope",
            "row_signature_schema_version:integrity envelope",
            "created_on:audit timestamp of the physical copy",
            "created_by_user_id:audit actor of the physical copy; the authority journal is the trusted actor record",
        }),
        ["field_encrypted_values"] = (BusinessSignedSchemas.FieldEncryptedValuesV1, "field_encrypted_values", new[]
        {
            "field_encrypted_value_id:bound as the canonical primary key",
            "content_revision:bound as row_revision",
            "row_revision:integrity envelope",
            "row_signature:integrity envelope",
            "row_signature_key_version:integrity envelope",
            "row_signature_schema_version:integrity envelope",
            "created_on:audit timestamp of the physical copy",
            "created_by_user_id:audit actor of the physical copy; the authority journal is the trusted actor record",
        }),
        ["field_boolean_values"] = (BusinessSignedSchemas.FieldBooleanValuesV1, "field_boolean_values", new[]
        {
            "field_boolean_value_id:bound as the canonical primary key",
            "content_revision:bound as row_revision",
            "row_revision:integrity envelope",
            "row_signature:integrity envelope",
            "row_signature_key_version:integrity envelope",
            "row_signature_schema_version:integrity envelope",
            "created_on:audit timestamp of the physical copy",
            "created_by_user_id:audit actor of the physical copy; the authority journal is the trusted actor record",
        }),
        ["field_option_values"] = (BusinessSignedSchemas.FieldOptionValuesV1, "field_option_values", new[]
        {
            "field_option_value_id:bound as the canonical primary key",
            "content_revision:bound as row_revision",
            "row_revision:integrity envelope",
            "row_signature:integrity envelope",
            "row_signature_key_version:integrity envelope",
            "row_signature_schema_version:integrity envelope",
            "created_on:audit timestamp of the physical copy",
            "created_by_user_id:audit actor of the physical copy; the authority journal is the trusted actor record",
        }),
        ["file_attachment_versions"] = (BusinessSignedSchemas.FileAttachmentVersionsV1, "file_attachment_versions", new[]
        {
            "file_attachment_version_id:bound as the canonical primary key",
            "content_revision:bound as row_revision",
            "row_revision:integrity envelope",
            "row_signature:integrity envelope",
            "row_signature_key_version:integrity envelope",
            "row_signature_schema_version:integrity envelope",
            "version_state:local untrusted hint; the authority manifest decides which version is current",
        }),
    };

    [Fact]
    public void EveryRegisteredSchemaIsOrdinalSortedAndAllowListedByTheAuthority()
    {
        foreach (var schema in BusinessSignedSchemas.All)
        {
            Assert.True(schema.Columns.Select(c => c.ColumnName).SequenceEqual(schema.Columns.Select(c => c.ColumnName).OrderBy(c => c, StringComparer.Ordinal)), schema.EntityKind + " registry is not ordinal-sorted");
            Assert.True(Protected.ContainsKey(schema.EntityKind), schema.EntityKind + " must be in the protected-table inventory");
        }

        // Historical schema versions stay registered so Stage A rows keep verifying (row-integrity-format.md §3.1).
        Assert.Contains(BusinessSignedSchemas.All, s => s.EntityKind == "form_records" && s.SchemaVersion == 2);
        Assert.Contains(BusinessSignedSchemas.All, s => s.EntityKind == "form_records" && s.SchemaVersion == 3);
    }

    [Fact]
    public void RegisteredColumnsExistInTheModelAndUnsignedColumnsAreJustified()
    {
        var model = Model();
        foreach (var (entityKind, (schema, efTable, unsigned)) in Protected)
        {
            var entity = model.GetEntityTypes().Single(e => e.GetTableName() == efTable);
            var store = StoreObjectIdentifier.Table(efTable, entity.GetSchema());
            var columns = entity.GetProperties().Select(p => (Name: p.GetColumnName(store) ?? p.Name, Nullable: p.IsNullable)).ToList();
            var columnNames = columns.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);

            foreach (var signed in schema.Columns)
            {
                Assert.True(columnNames.Contains(signed.ColumnName), $"{entityKind}: signed column {signed.ColumnName} is not an EF column of {efTable}");
                var ef = columns.Single(c => c.Name == signed.ColumnName);
                Assert.True(ef.Nullable == signed.Nullable, $"{entityKind}: nullability of {signed.ColumnName} differs between registry ({signed.Nullable}) and model ({ef.Nullable})");
            }

            var justified = unsigned.Select(u => u.Split(':', 2)[0]).ToHashSet(StringComparer.Ordinal);
            var unaccounted = columnNames.Where(c => !schema.Columns.Any(s => s.ColumnName == c) && !justified.Contains(c)).ToList();
            Assert.True(unaccounted.Count == 0, $"{entityKind}: columns neither signed nor justified as unsigned: {string.Join(", ", unaccounted)}");
        }
    }

    [Fact]
    public void OwnerProjectionColumnsAreNeverPartOfASignedSchema()
    {
        foreach (var schema in BusinessSignedSchemas.All)
        {
            Assert.DoesNotContain(schema.Columns, c => c.ColumnName is "owner_user_id" or "owner_team_id");
        }
    }

    private static IModel Model()
    {
        var options = new DbContextOptionsBuilder<BusinessDbContext>().UseNpgsql("Host=model-only.invalid;Database=unused;Username=unused;Password=unused").Options;
        using var context = new BusinessDbContext(options);
        return context.Model;
    }
}
