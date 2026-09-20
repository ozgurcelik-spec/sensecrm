using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sense.Crm.Shared.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AuditApiKeyId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "api_key_id",
                schema: "audit",
                table: "audit_log_entries",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_entries_tenant_id_api_key_id_occurred_at",
                schema: "audit",
                table: "audit_log_entries",
                columns: new[] { "tenant_id", "api_key_id", "occurred_at" },
                descending: new[] { false, false, true },
                filter: "api_key_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_audit_log_entries_tenant_id_api_key_id_occurred_at",
                schema: "audit",
                table: "audit_log_entries");

            migrationBuilder.DropColumn(
                name: "api_key_id",
                schema: "audit",
                table: "audit_log_entries");
        }
    }
}
