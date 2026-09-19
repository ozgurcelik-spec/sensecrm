using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Crm.Modules.Sales.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LeadOwnerAssignedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "owner_assigned_at",
                schema: "sales",
                table: "leads",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "owner_assigned_at",
                schema: "sales",
                table: "leads");
        }
    }
}
