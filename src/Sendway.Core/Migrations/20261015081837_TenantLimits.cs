using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sendway.Core.Migrations
{
    /// <inheritdoc />
    public partial class TenantLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EmailPerDay",
                table: "Tenants",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PushPerDay",
                table: "Tenants",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SmsCountries",
                table: "Tenants",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SmsPerDay",
                table: "Tenants",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MessageRecords_TenantId_Channel_AcceptedAt",
                table: "MessageRecords",
                columns: new[] { "TenantId", "Channel", "AcceptedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MessageRecords_TenantId_Channel_AcceptedAt",
                table: "MessageRecords");

            migrationBuilder.DropColumn(
                name: "EmailPerDay",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "PushPerDay",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "SmsCountries",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "SmsPerDay",
                table: "Tenants");
        }
    }
}
