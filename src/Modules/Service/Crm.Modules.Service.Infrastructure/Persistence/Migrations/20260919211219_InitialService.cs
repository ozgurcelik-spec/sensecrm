using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Crm.Modules.Service.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialService : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "service");

            migrationBuilder.CreateTable(
                name: "case_comments",
                schema: "service",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    case_id = table.Column<Guid>(type: "uuid", nullable: false),
                    visibility = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    body = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    author_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    modified_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    modified_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_case_comments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "case_counters",
                schema: "service",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    year = table.Column<int>(type: "integer", nullable: false),
                    last_value = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_case_counters", x => new { x.tenant_id, x.year });
                });

            migrationBuilder.CreateTable(
                name: "case_events",
                schema: "service",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    case_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    from_value = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    to_value = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    note = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    modified_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    modified_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_case_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "cases",
                schema: "service",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    description = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    account_id = table.Column<Guid>(type: "uuid", nullable: true),
                    contact_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    priority = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    assigned_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    resolution_note = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    first_response_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    resolved_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    closed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    reopen_count = table.Column<int>(type: "integer", nullable: false),
                    first_response_due_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    due_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    sla_anchor_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    first_response_warn_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    resolution_warn_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    is_deleted = table.Column<bool>(type: "boolean", nullable: false),
                    deleted_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    deleted_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    modified_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    modified_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cases", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "inbox_messages",
                schema: "service",
                columns: table => new
                {
                    message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    handler = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    processed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    modified_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    modified_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inbox_messages", x => new { x.message_id, x.handler });
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                schema: "service",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    payload = table.Column<string>(type: "jsonb", maxLength: 500, nullable: false),
                    occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    processed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    error = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    correlation_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    is_dead = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    modified_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    modified_user_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sla_policies",
                schema: "service",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    priority = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    first_response_minutes = table.Column<int>(type: "integer", nullable: false),
                    resolution_minutes = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    modified_date = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    modified_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sla_policies", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_case_comments_tenant_id",
                schema: "service",
                table: "case_comments",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_case_comments_tenant_id_case_id_created_at",
                schema: "service",
                table: "case_comments",
                columns: new[] { "tenant_id", "case_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_case_counters_tenant_id",
                schema: "service",
                table: "case_counters",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_case_events_tenant_id",
                schema: "service",
                table: "case_events",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_case_events_tenant_id_case_id_created_at",
                schema: "service",
                table: "case_events",
                columns: new[] { "tenant_id", "case_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_cases_tenant_id",
                schema: "service",
                table: "cases",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_cases_tenant_id_account_id",
                schema: "service",
                table: "cases",
                columns: new[] { "tenant_id", "account_id" });

            migrationBuilder.CreateIndex(
                name: "ix_cases_tenant_id_assigned_user_id_status",
                schema: "service",
                table: "cases",
                columns: new[] { "tenant_id", "assigned_user_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_cases_tenant_id_contact_id",
                schema: "service",
                table: "cases",
                columns: new[] { "tenant_id", "contact_id" });

            migrationBuilder.CreateIndex(
                name: "ix_cases_tenant_id_due_at",
                schema: "service",
                table: "cases",
                columns: new[] { "tenant_id", "due_at" });

            migrationBuilder.CreateIndex(
                name: "ix_cases_tenant_id_number",
                schema: "service",
                table: "cases",
                columns: new[] { "tenant_id", "number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_cases_tenant_id_status_created_at",
                schema: "service",
                table: "cases",
                columns: new[] { "tenant_id", "status", "created_at" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_processed_at_next_attempt_at",
                schema: "service",
                table: "outbox_messages",
                columns: new[] { "processed_at", "next_attempt_at" },
                filter: "processed_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_messages_tenant_id",
                schema: "service",
                table: "outbox_messages",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_sla_policies_tenant_id",
                schema: "service",
                table: "sla_policies",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_sla_policies_tenant_id_priority",
                schema: "service",
                table: "sla_policies",
                columns: new[] { "tenant_id", "priority" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "case_comments",
                schema: "service");

            migrationBuilder.DropTable(
                name: "case_counters",
                schema: "service");

            migrationBuilder.DropTable(
                name: "case_events",
                schema: "service");

            migrationBuilder.DropTable(
                name: "cases",
                schema: "service");

            migrationBuilder.DropTable(
                name: "inbox_messages",
                schema: "service");

            migrationBuilder.DropTable(
                name: "outbox_messages",
                schema: "service");

            migrationBuilder.DropTable(
                name: "sla_policies",
                schema: "service");
        }
    }
}
