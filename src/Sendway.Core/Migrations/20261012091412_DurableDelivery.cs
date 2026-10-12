using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sendway.Core.Migrations
{
    /// <inheritdoc />
    public partial class DurableDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "SentAt",
                table: "MessageRecords",
                newName: "AcceptedAt");

            migrationBuilder.AddColumn<int>(
                name: "AttemptCount",
                table: "MessageRecords",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CompletedAt",
                table: "MessageRecords",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKey",
                table: "MessageRecords",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextAttemptAt",
                table: "MessageRecords",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Payload",
                table: "MessageRecords",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestHash",
                table: "MessageRecords",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            // Every row that predates this migration was recorded after its single attempt had
            // already finished, so it is complete as of the time it was recorded.
            migrationBuilder.Sql(
                """UPDATE "MessageRecords" SET "CompletedAt" = "AcceptedAt", "AttemptCount" = 1;""");

            migrationBuilder.CreateIndex(
                name: "IX_MessageRecords_Status_NextAttemptAt",
                table: "MessageRecords",
                columns: new[] { "Status", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_MessageRecords_TenantId_IdempotencyKey",
                table: "MessageRecords",
                columns: new[] { "TenantId", "IdempotencyKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MessageRecords_Status_NextAttemptAt",
                table: "MessageRecords");

            migrationBuilder.DropIndex(
                name: "IX_MessageRecords_TenantId_IdempotencyKey",
                table: "MessageRecords");

            migrationBuilder.DropColumn(
                name: "AttemptCount",
                table: "MessageRecords");

            migrationBuilder.DropColumn(
                name: "CompletedAt",
                table: "MessageRecords");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                table: "MessageRecords");

            migrationBuilder.DropColumn(
                name: "NextAttemptAt",
                table: "MessageRecords");

            migrationBuilder.DropColumn(
                name: "Payload",
                table: "MessageRecords");

            migrationBuilder.DropColumn(
                name: "RequestHash",
                table: "MessageRecords");

            migrationBuilder.RenameColumn(
                name: "AcceptedAt",
                table: "MessageRecords",
                newName: "SentAt");
        }
    }
}
