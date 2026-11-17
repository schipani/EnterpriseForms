using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AuthorizationAuthority.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// C-143 (R-52): an access path, not new state. A partial expression index over the append-only security journal, limited to the
    /// two builder event kinds, so the Authority can read a form's latest attestation or population event in one probe under the
    /// organization lock (to decide whether an unchanged blocked state was already journaled). No row is written or changed; the
    /// journal's content, grants and hash chain are untouched. The index can be dropped and rebuilt at any time, so
    /// <see cref="Down"/> simply drops it.
    /// </summary>
    public partial class AuthorityJournalSearchIndexFormEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS ix_journal_search_index_form_events
                ON authority.security_journal_entries (organization_id, journal_event_kind, ((journal_event_body_json::jsonb -> 'body' ->> 'form_definition_id')), journal_stream_revision DESC)
                WHERE journal_event_kind IN ('SearchIndexCoverageAttested', 'SearchIndexPopulationIssued');
                """);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("DROP INDEX IF EXISTS authority.ix_journal_search_index_form_events;");
    }
}
