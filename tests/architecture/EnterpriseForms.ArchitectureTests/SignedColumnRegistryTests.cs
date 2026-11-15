using EnterpriseForms.Application.Features.FormRecords.Integrity;
using EnterpriseForms.Infrastructure.Persistence;
using EnterpriseForms.Integrity.Canonical;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
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
    /// <summary>
    /// Protected table → its checked registry entries (E3-M2b-2, D-4: by kind and version; the envelope tables check schema 1 and
    /// schema 2); the authority signs under these entity kinds only (ContentService.AllowedEntityKinds).
    /// </summary>
    private static readonly Dictionary<string, (RowSchemaDefinition[] Schemas, string EfTable, string[] UnsignedWithReason)> Protected = new(StringComparer.Ordinal)
    {
        ["form_records"] = ([BusinessSignedSchemas.FormRecordsV3], "form_record_revisions", new[]
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
        ["field_string_values"] = ([BusinessSignedSchemas.FieldStringValuesV1], "field_string_values", new[]
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
        ["field_integer_values"] = ([BusinessSignedSchemas.FieldIntegerValuesV1], "field_integer_values", new[]
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
        ["field_decimal_values"] = ([BusinessSignedSchemas.FieldDecimalValuesV1], "field_decimal_values", new[]
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
        ["field_date_values"] = ([BusinessSignedSchemas.FieldDateValuesV1], "field_date_values", new[]
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
        ["field_date_time_values"] = ([BusinessSignedSchemas.FieldDateTimeValuesV1], "field_date_time_values", new[]
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
        ["field_guid_values"] = ([BusinessSignedSchemas.FieldGuidValuesV1], "field_guid_values", new[]
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
        ["field_encrypted_values"] = ([BusinessSignedSchemas.FieldEncryptedValuesV1, BusinessSignedSchemas.FieldEncryptedValuesV2], "field_encrypted_values", new[]
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
        ["field_boolean_values"] = ([BusinessSignedSchemas.FieldBooleanValuesV1], "field_boolean_values", new[]
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
        ["field_option_values"] = ([BusinessSignedSchemas.FieldOptionValuesV1], "field_option_values", new[]
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
        ["form_record_groups"] = ([BusinessSignedSchemas.FormRecordGroupsV1], "form_record_group_revisions", new[]
        {
            "form_record_group_revision_id:physical surrogate of the staged copy; the canonical pk is form_record_group_id",
            "form_record_group_id:bound as the canonical primary key (stable occurrence id)",
            "content_revision:bound as row_revision",
            "row_revision:integrity envelope",
            "row_signature:integrity envelope",
            "row_signature_key_version:integrity envelope",
            "row_signature_schema_version:integrity envelope",
            "created_on:audit timestamp of the physical copy",
            "created_by_user_id:audit actor of the physical copy; the authority journal is the trusted actor record",
        }),
        ["file_attachment_versions"] = ([BusinessSignedSchemas.FileAttachmentVersionsV1, BusinessSignedSchemas.FileAttachmentVersionsV2], "file_attachment_versions", new[]
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

    /// <summary>
    /// E3-M2b-2 (D-4): columns signed only from a later schema version of their table. For the earlier schemas they are unsigned
    /// because they are NULL by construction (the named CHECK); every other column keeps its per-table classification.
    /// </summary>
    private static readonly Dictionary<(string Table, string Column), int> SignedFromSchemaVersion = new()
    {
        [("field_encrypted_values", "data_key_identifier")] = 2,
        [("field_encrypted_values", "data_key_commitment_bytes")] = 2,
        [("field_encrypted_values", "key_wrap_algorithm_code")] = 2,
        [("file_attachment_versions", "data_key_identifier")] = 2,
        [("file_attachment_versions", "data_key_commitment_bytes")] = 2,
        [("file_attachment_versions", "key_wrap_algorithm_code")] = 2,
    };

    /// <summary>
    /// E3-M2b-2 (D-4), the only nullable-storage exception: a column non-nullable in the signed registry may be nullable in the
    /// database only if earlier schemas of the table store NULL there, and the named CHECK requires it (an explicit
    /// <c>column IS NOT NULL</c>) whenever the row is signed under the schema that signs it.
    /// </summary>
    private static readonly Dictionary<(string Table, string Column), string> NullableStorageGuardedBy = new()
    {
        [("field_encrypted_values", "data_key_identifier")] = EnvelopeMetadataConstraints.FieldMetadataName,
        [("field_encrypted_values", "data_key_commitment_bytes")] = EnvelopeMetadataConstraints.FieldMetadataName,
        [("field_encrypted_values", "key_wrap_algorithm_code")] = EnvelopeMetadataConstraints.FieldMetadataName,
        [("file_attachment_versions", "data_key_identifier")] = EnvelopeMetadataConstraints.AttachmentMetadataName,
        [("file_attachment_versions", "data_key_commitment_bytes")] = EnvelopeMetadataConstraints.AttachmentMetadataName,
        [("file_attachment_versions", "key_wrap_algorithm_code")] = EnvelopeMetadataConstraints.AttachmentMetadataName,
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

    /// <summary>
    /// Phase C5 (D14, C-34 condition 3): the Authority's column-class table and the business signed-column registry
    /// describe exactly the same (entity kind, schema version) set with exactly the same columns, so every signed column
    /// of every supported item type has one class and an unregistered schema or column fails closed in the Authority.
    /// </summary>
    [Fact]
    public void TheAuthorityColumnClassTableCoversEveryBusinessSignedSchemaExactly()
    {
        var business = BusinessSignedSchemas.All.ToDictionary(s => (s.EntityKind, s.SchemaVersion));
        var authority = AuthorizationAuthority.Application.Features.Content.ContentDeltaRules.Rules.ToDictionary(r => (r.EntityKind, r.SchemaVersion));
        Assert.Equal(business.Keys.OrderBy(k => k.EntityKind, StringComparer.Ordinal).ThenBy(k => k.SchemaVersion), authority.Keys.OrderBy(k => k.EntityKind, StringComparer.Ordinal).ThenBy(k => k.SchemaVersion));
        foreach (var (key, schema) in business)
        {
            var rule = authority[key];
            Assert.True(schema.Columns.Select(c => c.ColumnName).OrderBy(c => c, StringComparer.Ordinal).SequenceEqual(rule.Columns.Keys.OrderBy(c => c, StringComparer.Ordinal)),
                $"{key.EntityKind} v{key.SchemaVersion}: the Authority classifies a different column set than the business registry signs");
        }
    }

    /// <summary>
    /// E3-M2b-2 (S-9, OD-M2-22): where the Authority pins column types and nullability (the two envelope kinds, both schema
    /// versions), its pins equal the business registry exactly, so a business encoding change cannot drift from the Authority's
    /// refusal rule unnoticed. No other kind is pinned.
    /// </summary>
    [Fact]
    public void TheAuthorityPinnedShapesEqualTheBusinessRegistryForTheEnvelopeKinds()
    {
        var business = BusinessSignedSchemas.All.ToDictionary(s => (s.EntityKind, s.SchemaVersion));
        var pinned = AuthorizationAuthority.Application.Features.Content.ContentDeltaRules.Rules.Where(r => r.PinnedShapes is not null).ToList();
        Assert.Equal(
            new[] { ("field_encrypted_values", 1), ("field_encrypted_values", 2), ("file_attachment_versions", 1), ("file_attachment_versions", 2) },
            pinned.Select(r => (r.EntityKind, r.SchemaVersion)).Order().ToArray());
        foreach (var rule in pinned)
        {
            var schema = business[(rule.EntityKind, rule.SchemaVersion)];
            foreach (var column in schema.Columns)
            {
                var shape = rule.PinnedShapes![column.ColumnName];
                Assert.True(shape.Type == column.Type && shape.Nullable == column.Nullable, $"{rule.EntityKind} v{rule.SchemaVersion}.{column.ColumnName}: Authority pin differs from the business registry");
            }

            Assert.Equal(schema.Columns.Count, rule.PinnedShapes!.Count);
        }
    }

    [Fact]
    public void RegisteredColumnsExistInTheModelAndUnsignedColumnsAreJustified()
    {
        var model = Model();
        var designEntities = DesignModelEntities();
        foreach (var (entityKind, (schemas, efTable, unsigned)) in Protected)
        {
            var entity = model.GetEntityTypes().Single(e => e.GetTableName() == efTable);
            var store = StoreObjectIdentifier.Table(efTable, entity.GetSchema());
            var columns = entity.GetProperties().Select(p => (Name: p.GetColumnName(store) ?? p.Name, Nullable: p.IsNullable)).ToList();
            var columnNames = columns.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
            var justified = unsigned.Select(u => u.Split(':', 2)[0]).ToHashSet(StringComparer.Ordinal);
            Assert.True(schemas.Length > 0 && schemas.All(s => s.EntityKind == entityKind), entityKind + ": registry entries of another kind");

            foreach (var schema in schemas)
            {
                foreach (var signed in schema.Columns)
                {
                    Assert.True(columnNames.Contains(signed.ColumnName), $"{entityKind} v{schema.SchemaVersion}: signed column {signed.ColumnName} is not an EF column of {efTable}");
                    var ef = columns.Single(c => c.Name == signed.ColumnName);
                    if (ef.Nullable == signed.Nullable)
                    {
                        continue;
                    }

                    // D-4: the single narrowly scoped exception (non-nullable signed, nullable stored, required by a named CHECK).
                    Assert.True(!signed.Nullable && ef.Nullable && NullableStorageGuardedBy.TryGetValue((efTable, signed.ColumnName), out var constraint)
                                && designEntities[efTable].GetCheckConstraints().SingleOrDefault(c => c.Name == constraint) is { } check
                                && check.Sql.Contains(signed.ColumnName + " IS NOT NULL", StringComparison.Ordinal),
                        $"{entityKind} v{schema.SchemaVersion}: nullability of {signed.ColumnName} differs between registry ({signed.Nullable}) and model ({ef.Nullable})");
                }

                var unaccounted = columnNames.Where(c => !schema.Columns.Any(s => s.ColumnName == c) && !justified.Contains(c)
                    && !(SignedFromSchemaVersion.TryGetValue((efTable, c), out var from) && schema.SchemaVersion < from)).ToList();
                Assert.True(unaccounted.Count == 0, $"{entityKind} v{schema.SchemaVersion}: columns neither signed nor justified as unsigned: {string.Join(", ", unaccounted)}");
            }

            // The exceptions are exact: every listed column exists, is signed by some checked schema, and is not also in the per-table unsigned list.
            foreach (var ((table, column), from) in SignedFromSchemaVersion.Where(e => e.Key.Table == efTable))
            {
                Assert.Contains(schemas, s => s.SchemaVersion == from && s.Columns.Any(c => c.ColumnName == column));
                Assert.DoesNotContain(column, justified);
                Assert.True(NullableStorageGuardedBy.ContainsKey((table, column)), column + " needs its guarding CHECK");
            }
        }

        Assert.All(SignedFromSchemaVersion.Keys, k => Assert.Contains(Protected.Values, p => p.EfTable == k.Table));
        Assert.Equal(SignedFromSchemaVersion.Keys.Order(), NullableStorageGuardedBy.Keys.Order());
    }

    [Fact]
    public void OwnerProjectionColumnsAreNeverPartOfASignedSchema()
    {
        foreach (var schema in BusinessSignedSchemas.All)
        {
            Assert.DoesNotContain(schema.Columns, c => c.ColumnName is "owner_user_id" or "owner_team_id");
        }
    }

    /// <summary>The design-time model (check constraints are not kept in the read-optimized runtime model), by table name.</summary>
    private static Dictionary<string, IEntityType> DesignModelEntities()
    {
        var options = new DbContextOptionsBuilder<BusinessDbContext>().UseNpgsql("Host=model-only.invalid;Database=unused;Username=unused;Password=unused").Options;
        using var context = new BusinessDbContext(options);
        return context.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>().Model.GetEntityTypes()
            .Where(e => e.GetTableName() is not null && e.BaseType is null)
            .GroupBy(e => e.GetTableName()!)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
    }

    private static IModel Model()
    {
        var options = new DbContextOptionsBuilder<BusinessDbContext>().UseNpgsql("Host=model-only.invalid;Database=unused;Username=unused;Password=unused").Options;
        using var context = new BusinessDbContext(options);
        return context.Model;
    }
}
