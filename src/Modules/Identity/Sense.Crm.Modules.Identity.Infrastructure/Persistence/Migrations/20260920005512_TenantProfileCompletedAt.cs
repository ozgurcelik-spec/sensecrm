using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sense.Crm.Modules.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenantProfileCompletedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "profile_completed_at",
                schema: "identity",
                table: "tenants",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "profile_completed_at",
                schema: "identity",
                table: "tenants");
        }
    }
}
