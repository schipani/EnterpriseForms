using EnterpriseForms.Infrastructure.Persistence;
using EnterpriseForms.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace EnterpriseForms.ArchitectureTests;

/// <summary>
/// E3-M2b-2 (contract §9.3 as corrected by D-1): the migration's CHECK operations are exactly the reviewed constants the EF
/// model uses (no drift between the migration and the model), and the envelope-2 disjunct pairs every comparison with an
/// explicit IS NOT NULL (PostgreSQL accepts a CHECK that evaluates to NULL). The behaviour against real PostgreSQL is in the
/// E3-M2b-2 PostgreSQL gate (CHECK matrix).
/// </summary>
public sealed class EnvelopeMetadataMigrationTests
{
    [Fact]
    public void TheMigrationAddsExactlyTheReviewedChecksAndDropsTheOpenOne()
    {
        var migration = new EnvelopeV2SignedColumns();
        var added = migration.UpOperations.OfType<AddCheckConstraintOperation>().ToDictionary(o => o.Name, o => (o.Table, o.Sql));
        Assert.Equal(new Dictionary<string, (string, string)>
        {
            [EnvelopeMetadataConstraints.FieldEnvelopeVersionName] = ("field_encrypted_values", EnvelopeMetadataConstraints.EnvelopeVersionSql),
            [EnvelopeMetadataConstraints.FieldSchemaEnvelopeTieName] = ("field_encrypted_values", EnvelopeMetadataConstraints.SchemaEnvelopeTieSql),
            [EnvelopeMetadataConstraints.FieldMetadataName] = ("field_encrypted_values", EnvelopeMetadataConstraints.MetadataSql),
            [EnvelopeMetadataConstraints.AttachmentEnvelopeVersionName] = ("file_attachment_versions", EnvelopeMetadataConstraints.EnvelopeVersionSql),
            [EnvelopeMetadataConstraints.AttachmentSchemaEnvelopeTieName] = ("file_attachment_versions", EnvelopeMetadataConstraints.SchemaEnvelopeTieSql),
            [EnvelopeMetadataConstraints.AttachmentMetadataName] = ("file_attachment_versions", EnvelopeMetadataConstraints.MetadataSql),
        }, added);
        Assert.Contains(migration.UpOperations.OfType<DropCheckConstraintOperation>(), o => o.Name == EnvelopeMetadataConstraints.LegacyFieldEnvelopePositiveName);
        Assert.Equal(6, migration.UpOperations.OfType<AddColumnOperation>().Count(c => c.IsNullable && c.DefaultValue is null && c.DefaultValueSql is null));
        Assert.Empty(migration.UpOperations.OfType<CreateIndexOperation>());

        // The guarded Down refuses first, then restores the open CHECK.
        var down = migration.DownOperations;
        Assert.IsType<SqlOperation>(down[0]);
        Assert.Contains(EnvelopeV2SignedColumns.DownRefusedMessage, ((SqlOperation)down[0]).Sql, StringComparison.Ordinal);
        Assert.Contains(down.OfType<AddCheckConstraintOperation>(), o => o.Name == EnvelopeMetadataConstraints.LegacyFieldEnvelopePositiveName && o.Sql == EnvelopeMetadataConstraints.LegacyFieldEnvelopePositiveSql);
    }

    [Fact]
    public void TheEnvelopeTwoDisjunctRequiresEveryColumnExplicitly()
    {
        var disjunct = EnvelopeMetadataConstraints.MetadataSql[EnvelopeMetadataConstraints.MetadataSql.IndexOf("encryption_envelope_version = 2", StringComparison.Ordinal)..];
        foreach (var column in new[] { "data_key_identifier", "data_key_commitment_bytes", "key_wrap_algorithm_code" })
        {
            Assert.Contains(column + " IS NOT NULL", disjunct, StringComparison.Ordinal);
        }

        Assert.Contains("octet_length(data_key_commitment_bytes) = 32", disjunct, StringComparison.Ordinal);
        Assert.Contains("key_wrap_algorithm_code IN (1, 2)", disjunct, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEfModelUsesTheSameReviewedChecks()
    {
        var options = new DbContextOptionsBuilder<BusinessDbContext>().UseNpgsql("Host=model-only.invalid;Database=unused;Username=unused;Password=unused").Options;
        using var context = new BusinessDbContext(options);
        var model = context.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>().Model;
        var checks = model.GetEntityTypes().SelectMany(e => e.GetCheckConstraints()).ToDictionary(c => c.Name!, c => c.Sql);
        Assert.Equal(EnvelopeMetadataConstraints.MetadataSql, checks[EnvelopeMetadataConstraints.FieldMetadataName]);
        Assert.Equal(EnvelopeMetadataConstraints.MetadataSql, checks[EnvelopeMetadataConstraints.AttachmentMetadataName]);
        Assert.Equal(EnvelopeMetadataConstraints.SchemaEnvelopeTieSql, checks[EnvelopeMetadataConstraints.FieldSchemaEnvelopeTieName]);
        Assert.Equal(EnvelopeMetadataConstraints.SchemaEnvelopeTieSql, checks[EnvelopeMetadataConstraints.AttachmentSchemaEnvelopeTieName]);
        Assert.False(checks.ContainsKey(EnvelopeMetadataConstraints.LegacyFieldEnvelopePositiveName));
    }
}
