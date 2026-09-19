using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sense.Crm.Modules.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SecurityHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "must_change_password",
                schema: "identity",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "family_expires_at",
                schema: "identity",
                table: "refresh_tokens",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            // Mevcut oturumlar: aile mutlak ömrü = token'ın kendi son kullanma anı (aile ömrü onlar için yeniden başlamaz).
            migrationBuilder.Sql("UPDATE identity.refresh_tokens SET family_expires_at = expires_at;");

            // Mevcut üyelikler etkin (Active) kalır; yalnız bundan sonra açılan davetler Pending olur.
            migrationBuilder.AddColumn<string>(
                name: "status",
                schema: "identity",
                table: "memberships",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Active");

            migrationBuilder.CreateIndex(
                name: "ix_memberships_user_id_status",
                schema: "identity",
                table: "memberships",
                columns: new[] { "user_id", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_memberships_user_id_status",
                schema: "identity",
                table: "memberships");

            migrationBuilder.DropColumn(
                name: "must_change_password",
                schema: "identity",
                table: "users");

            migrationBuilder.DropColumn(
                name: "family_expires_at",
                schema: "identity",
                table: "refresh_tokens");

            migrationBuilder.DropColumn(
                name: "status",
                schema: "identity",
                table: "memberships");
        }
    }
}
