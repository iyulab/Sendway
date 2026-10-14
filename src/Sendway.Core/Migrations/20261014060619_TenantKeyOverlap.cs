using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sendway.Core.Migrations
{
    /// <inheritdoc />
    public partial class TenantKeyOverlap : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "PreviousApiKeyExpiresAt",
                table: "Tenants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreviousApiKeyHash",
                table: "Tenants",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tenants_PreviousApiKeyHash",
                table: "Tenants",
                column: "PreviousApiKeyHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tenants_PreviousApiKeyHash",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "PreviousApiKeyExpiresAt",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "PreviousApiKeyHash",
                table: "Tenants");
        }
    }
}
