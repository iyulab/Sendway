using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sendway.Core.Migrations
{
    /// <inheritdoc />
    public partial class DestinationLimits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DestinationCooldownSeconds",
                table: "Tenants",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DestinationPerHour",
                table: "Tenants",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Destination",
                table: "MessageRecords",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MessageRecords_TenantId_Channel_Destination_AcceptedAt",
                table: "MessageRecords",
                columns: new[] { "TenantId", "Channel", "Destination", "AcceptedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MessageRecords_TenantId_Channel_Destination_AcceptedAt",
                table: "MessageRecords");

            migrationBuilder.DropColumn(
                name: "DestinationCooldownSeconds",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "DestinationPerHour",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "Destination",
                table: "MessageRecords");
        }
    }
}
