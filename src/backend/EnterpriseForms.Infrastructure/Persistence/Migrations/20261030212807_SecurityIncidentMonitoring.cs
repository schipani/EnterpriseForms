using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EnterpriseForms.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SecurityIncidentMonitoring : Migration
    {
        // Schema-owned guard (owned by the migration role, not the runtime role): what was detected, when it was first seen and
        // which problem it belongs to never change; counts, last detection and revision only move forward. A business-database
        // administrator can still drop the trigger or the rows (untrusted projection class); the Authority journal is the evidence.
        private const string GuardFunctionSql = """
            CREATE FUNCTION public.security_incidents_guard() RETURNS trigger
            LANGUAGE plpgsql SET search_path = pg_catalog, pg_temp AS $guard$
            BEGIN
                IF NEW.security_incident_id IS DISTINCT FROM OLD.security_incident_id
                   OR NEW.tenant_id IS DISTINCT FROM OLD.tenant_id
                   OR NEW.form_record_id IS DISTINCT FROM OLD.form_record_id
                   OR NEW.incident_type_name IS DISTINCT FROM OLD.incident_type_name
                   OR NEW.incident_severity_name IS DISTINCT FROM OLD.incident_severity_name
                   OR NEW.incident_deduplication_key IS DISTINCT FROM OLD.incident_deduplication_key
                   OR NEW.first_detected_on IS DISTINCT FROM OLD.first_detected_on
                   OR NEW.detection_source_name IS DISTINCT FROM OLD.detection_source_name
                   OR NEW.verification_error_code IS DISTINCT FROM OLD.verification_error_code
                   OR NEW.first_correlation_identifier IS DISTINCT FROM OLD.first_correlation_identifier THEN
                    RAISE EXCEPTION 'SECURITY_INCIDENT_IDENTITY_IMMUTABLE' USING ERRCODE = 'check_violation';
                END IF;
                IF NEW.occurrence_count < OLD.occurrence_count OR NEW.last_detected_on < OLD.last_detected_on OR NEW.incident_revision < OLD.incident_revision THEN
                    RAISE EXCEPTION 'SECURITY_INCIDENT_HISTORY_REWOUND' USING ERRCODE = 'check_violation';
                END IF;
                RETURN NEW;
            END
            $guard$;

            CREATE TRIGGER trg_security_incidents_guard BEFORE UPDATE ON security_incidents
                FOR EACH ROW EXECUTE FUNCTION public.security_incidents_guard();

            REVOKE ALL ON FUNCTION public.security_incidents_guard() FROM PUBLIC;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "security_incidents",
                columns: table => new
                {
                    security_incident_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    form_record_id = table.Column<Guid>(type: "uuid", nullable: true),
                    incident_type_name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    incident_severity_name = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    incident_status_name = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    incident_deduplication_key = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    first_detected_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_detected_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    occurrence_count = table.Column<int>(type: "integer", nullable: false),
                    detection_source_name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    verification_error_code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    first_correlation_identifier = table.Column<Guid>(type: "uuid", nullable: false),
                    assigned_to_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resolution_explanation_text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    resolved_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resolved_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    incident_revision = table.Column<long>(type: "bigint", nullable: false),
                    incident_modified_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    incident_modified_by_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_security_incidents", x => x.security_incident_id);
                    table.CheckConstraint("ck_security_incidents_counts", "occurrence_count >= 1 AND incident_revision >= 1 AND last_detected_on >= first_detected_on");
                    table.CheckConstraint("ck_security_incidents_error_code", "verification_error_code ~ '^[A-Za-z0-9_.:-]{0,80}$'");
                    table.CheckConstraint("ck_security_incidents_resolution", "(incident_status_name IN ('Resolved','FalsePositive')) = (resolved_on IS NOT NULL AND resolved_by_user_id IS NOT NULL AND resolution_explanation_text IS NOT NULL)");
                    table.CheckConstraint("ck_security_incidents_severity", "incident_severity_name IN ('Critical','High','Medium','Low')");
                    table.CheckConstraint("ck_security_incidents_status", "incident_status_name IN ('Open','Investigating','Resolved','FalsePositive')");
                    table.CheckConstraint("ck_security_incidents_type", "incident_type_name IN ('ConfirmedIntegrityMismatch','IntegrityQuarantine','VerificationServiceUnavailable','OperationalVerificationFailure')");
                    table.ForeignKey(
                        name: "fk_security_incidents_assignee",
                        column: x => x.assigned_to_user_id,
                        principalTable: "users",
                        principalColumn: "user_id");
                    table.ForeignKey(
                        name: "fk_security_incidents_modifier",
                        column: x => x.incident_modified_by_user_id,
                        principalTable: "users",
                        principalColumn: "user_id");
                    table.ForeignKey(
                        name: "fk_security_incidents_resolver",
                        column: x => x.resolved_by_user_id,
                        principalTable: "users",
                        principalColumn: "user_id");
                    table.ForeignKey(
                        name: "fk_security_incidents_tenant",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "tenant_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "security_incident_notes",
                columns: table => new
                {
                    security_incident_note_id = table.Column<Guid>(type: "uuid", nullable: false),
                    security_incident_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    note_author_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    note_created_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    investigation_note_text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_security_incident_notes", x => x.security_incident_note_id);
                    table.CheckConstraint("ck_security_incident_notes_text", "char_length(investigation_note_text) BETWEEN 1 AND 2000");
                    table.ForeignKey(
                        name: "fk_security_incident_notes_author",
                        column: x => x.note_author_user_id,
                        principalTable: "users",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_security_incident_notes_incident",
                        column: x => x.security_incident_id,
                        principalTable: "security_incidents",
                        principalColumn: "security_incident_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_security_incident_notes_tenant",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "tenant_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "security_incident_occurrences",
                columns: table => new
                {
                    security_incident_occurrence_id = table.Column<Guid>(type: "uuid", nullable: false),
                    security_incident_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    occurrence_detected_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    observed_incident_type_name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    detection_source_name = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    verification_error_code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    observed_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    request_correlation_identifier = table.Column<Guid>(type: "uuid", nullable: false),
                    committed_content_revision = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_security_incident_occurrences", x => x.security_incident_occurrence_id);
                    table.CheckConstraint("ck_security_incident_occurrences_type", "observed_incident_type_name IN ('ConfirmedIntegrityMismatch','IntegrityQuarantine','VerificationServiceUnavailable','OperationalVerificationFailure')");
                    table.ForeignKey(
                        name: "fk_security_incident_occurrences_incident",
                        column: x => x.security_incident_id,
                        principalTable: "security_incidents",
                        principalColumn: "security_incident_id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_security_incident_occurrences_observer",
                        column: x => x.observed_by_user_id,
                        principalTable: "users",
                        principalColumn: "user_id");
                    table.ForeignKey(
                        name: "fk_security_incident_occurrences_tenant",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "tenant_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_security_incident_notes_incident",
                table: "security_incident_notes",
                columns: new[] { "security_incident_id", "note_created_on" });

            migrationBuilder.CreateIndex(
                name: "IX_security_incident_notes_note_author_user_id",
                table: "security_incident_notes",
                column: "note_author_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_security_incident_notes_tenant_id",
                table: "security_incident_notes",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_security_incident_occurrences_incident",
                table: "security_incident_occurrences",
                columns: new[] { "security_incident_id", "occurrence_detected_on" });

            migrationBuilder.CreateIndex(
                name: "IX_security_incident_occurrences_observed_by_user_id",
                table: "security_incident_occurrences",
                column: "observed_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_security_incident_occurrences_tenant_id",
                table: "security_incident_occurrences",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "IX_security_incidents_assigned_to_user_id",
                table: "security_incidents",
                column: "assigned_to_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_security_incidents_deduplication",
                table: "security_incidents",
                columns: new[] { "tenant_id", "incident_deduplication_key", "first_detected_on" });

            migrationBuilder.CreateIndex(
                name: "IX_security_incidents_incident_modified_by_user_id",
                table: "security_incidents",
                column: "incident_modified_by_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_security_incidents_recent",
                table: "security_incidents",
                columns: new[] { "tenant_id", "last_detected_on", "security_incident_id" });

            migrationBuilder.CreateIndex(
                name: "ix_security_incidents_record",
                table: "security_incidents",
                columns: new[] { "tenant_id", "form_record_id" });

            migrationBuilder.CreateIndex(
                name: "IX_security_incidents_resolved_by_user_id",
                table: "security_incidents",
                column: "resolved_by_user_id");

            migrationBuilder.CreateIndex(
                name: "uq_security_incidents_active_key",
                table: "security_incidents",
                columns: new[] { "tenant_id", "incident_deduplication_key" },
                unique: true,
                filter: "incident_status_name IN ('Open','Investigating')");

            migrationBuilder.Sql(GuardFunctionSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS trg_security_incidents_guard ON security_incidents; DROP FUNCTION IF EXISTS public.security_incidents_guard();");

            migrationBuilder.DropTable(
                name: "security_incident_notes");

            migrationBuilder.DropTable(
                name: "security_incident_occurrences");

            migrationBuilder.DropTable(
                name: "security_incidents");
        }
    }
}
