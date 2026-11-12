using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthorizationAuthority.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// C-141 (UF-25, U-1b): the accepted per-field item counts of each Tier 1 entry, and a record probe index that ends with the
    /// item id.
    /// <list type="bullet">
    /// <item><c>search_record_entries.derived_field_item_counts</c> (integer[], nullable): one count per derived approval, written
    /// with the entry from the same postings (derivation version 3) and never updated (no UPDATE grant on it). Existing rows are not
    /// touched: version 1 and 2 entries keep NULL, as <c>ck_search_entry_field_item_counts</c> requires. They are re-derived from
    /// verified bytes by the builder after the S-A derivation upgrade returns their Complete coverages to Building; meanwhile a
    /// share-only search refuses them as not ready (503, retryable). No backfill writes a count that was not derived from
    /// Authority-verified bytes.</item>
    /// <item>The seven <c>ix_search_*_postings_record</c> indexes become (organization_id, form_record_id, field_definition_id,
    /// manifest_item_id): still record-keyed with no value column (PD-9); the candidate statement's probe is their prefix.</item>
    /// </list>
    /// Forward-only: <see cref="Down"/> refuses. Rolling the application back is safe without it: an image before C-141 never
    /// reads the column, writes version 2 entries with NULL counts (allowed by the constraint) and treats version 3 entries as
    /// current; a later return to this image re-derives those version 2 entries through the same upgrade.
    /// </summary>
    public partial class AuthoritySearchIndexUf25FieldItemCounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_search_string_postings_record",
                schema: "authority_search",
                table: "search_string_postings");

            migrationBuilder.DropCheckConstraint(
                name: "ck_search_entry_derivation",
                schema: "authority_search",
                table: "search_record_entries");

            migrationBuilder.DropIndex(
                name: "ix_search_option_postings_record",
                schema: "authority_search",
                table: "search_option_postings");

            migrationBuilder.DropIndex(
                name: "ix_search_integer_postings_record",
                schema: "authority_search",
                table: "search_integer_postings");

            migrationBuilder.DropIndex(
                name: "ix_search_decimal_postings_record",
                schema: "authority_search",
                table: "search_decimal_postings");

            migrationBuilder.DropIndex(
                name: "ix_search_date_time_postings_record",
                schema: "authority_search",
                table: "search_date_time_postings");

            migrationBuilder.DropIndex(
                name: "ix_search_date_postings_record",
                schema: "authority_search",
                table: "search_date_postings");

            migrationBuilder.DropIndex(
                name: "ix_search_boolean_postings_record",
                schema: "authority_search",
                table: "search_boolean_postings");

            migrationBuilder.AddColumn<int[]>(
                name: "derived_field_item_counts",
                schema: "authority_search",
                table: "search_record_entries",
                type: "integer[]",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_search_string_postings_record",
                schema: "authority_search",
                table: "search_string_postings",
                columns: new[] { "organization_id", "form_record_id", "field_definition_id", "manifest_item_id" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_search_entry_derivation",
                schema: "authority_search",
                table: "search_record_entries",
                sql: "entry_derivation_version IN (1, 2, 3) AND (entry_derivation_version = 1 OR (octet_length(header_source_canonical_bytes) > 0 AND created_by_business_user_id IS NOT NULL))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_search_entry_field_item_counts",
                schema: "authority_search",
                table: "search_record_entries",
                sql: "(entry_derivation_version < 3 AND derived_field_item_counts IS NULL) OR (entry_derivation_version >= 3 AND derived_field_item_counts IS NOT NULL AND cardinality(derived_field_item_counts) = cardinality(derived_field_search_approval_ids) AND array_position(derived_field_item_counts, NULL) IS NULL AND 0 <= ALL (derived_field_item_counts))");

            migrationBuilder.CreateIndex(
                name: "ix_search_option_postings_record",
                schema: "authority_search",
                table: "search_option_postings",
                columns: new[] { "organization_id", "form_record_id", "field_definition_id", "manifest_item_id" });

            migrationBuilder.CreateIndex(
                name: "ix_search_integer_postings_record",
                schema: "authority_search",
                table: "search_integer_postings",
                columns: new[] { "organization_id", "form_record_id", "field_definition_id", "manifest_item_id" });

            migrationBuilder.CreateIndex(
                name: "ix_search_decimal_postings_record",
                schema: "authority_search",
                table: "search_decimal_postings",
                columns: new[] { "organization_id", "form_record_id", "field_definition_id", "manifest_item_id" });

            migrationBuilder.CreateIndex(
                name: "ix_search_date_time_postings_record",
                schema: "authority_search",
                table: "search_date_time_postings",
                columns: new[] { "organization_id", "form_record_id", "field_definition_id", "manifest_item_id" });

            migrationBuilder.CreateIndex(
                name: "ix_search_date_postings_record",
                schema: "authority_search",
                table: "search_date_postings",
                columns: new[] { "organization_id", "form_record_id", "field_definition_id", "manifest_item_id" });

            migrationBuilder.CreateIndex(
                name: "ix_search_boolean_postings_record",
                schema: "authority_search",
                table: "search_boolean_postings",
                columns: new[] { "organization_id", "form_record_id", "field_definition_id", "manifest_item_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            throw new NotSupportedException("AuthoritySearchIndexUf25FieldItemCounts is forward-only (C-141): dropping the item counts would leave version 3 entries without the metadata the share-only readability rule and the sweeps verify; roll the application back instead (older images ignore the column).");
    }
}
