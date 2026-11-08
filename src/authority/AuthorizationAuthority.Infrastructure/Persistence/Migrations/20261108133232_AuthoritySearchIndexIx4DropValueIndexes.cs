using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthorizationAuthority.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// C-135 (PD-9, design §22.4): drops every value-keyed index of the Tier 1 posting tables — the seven B-tree value indexes
    /// (organization_id, field_definition_id, &lt;value&gt;, form_record_id) of IX-1 and the trigram GIN index on
    /// normalized_string_value. No approved Tier 1 query or maintenance path uses them (every posting read and delete is keyed by
    /// organization and record, organization and form, or organization); PostgreSQL 18 could serve the per-record candidate probe
    /// by a skip scan of a value index, whose cost depends on the organization-wide value distribution (§8.2). Indexes only: no
    /// posting, entry, seal or other row is changed. Forward-only: <see cref="Down"/> refuses, so a rollback cannot reintroduce the
    /// dependency; reintroducing a value-keyed index needs a separate approval.
    /// </summary>
    public partial class AuthoritySearchIndexIx4DropValueIndexes : Migration
    {
        private static readonly string[] ValueIndexes =
        [
            "ix_search_string_postings_value",
            "ix_search_integer_postings_value",
            "ix_search_decimal_postings_value",
            "ix_search_date_postings_value",
            "ix_search_date_time_postings_value",
            "ix_search_boolean_postings_value",
            "ix_search_option_postings_value",
            "ix_search_string_postings_trigram",
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var index in ValueIndexes)
            {
                migrationBuilder.Sql($"DROP INDEX IF EXISTS authority_search.{index};");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            throw new NotSupportedException("AuthoritySearchIndexIx4DropValueIndexes is forward-only (C-135, PD-9): value-keyed posting indexes reintroduce a planner dependency on organization-wide value distributions (design §8.2, §22.4) and need a separate approval.");
    }
}
