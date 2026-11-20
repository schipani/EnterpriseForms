using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnterpriseForms.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PhaseC8M4AuditExplorerIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_audit_events_tenant_actor_time",
                table: "audit_events",
                columns: new[] { "tenant_id", "actor_user_id", "event_occurred_on", "audit_sequence" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_tenant_correlation",
                table: "audit_events",
                columns: new[] { "tenant_id", "correlation_identifier" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_audit_events_tenant_actor_time",
                table: "audit_events");

            migrationBuilder.DropIndex(
                name: "ix_audit_events_tenant_correlation",
                table: "audit_events");
        }
    }
}
