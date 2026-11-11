using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sendway.Core.Migrations
{
    /// <inheritdoc />
    public partial class Verifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Verifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Channel = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Destination = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    Purpose = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    ProtectedCode = table.Column<string>(type: "text", nullable: true),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    VerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Verifications", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Verifications_TenantId_Channel_Destination_CreatedAt",
                table: "Verifications",
                columns: new[] { "TenantId", "Channel", "Destination", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Verifications_TenantId_Channel_Destination_Purpose_Status",
                table: "Verifications",
                columns: new[] { "TenantId", "Channel", "Destination", "Purpose", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Verifications_TenantId_IdempotencyKey",
                table: "Verifications",
                columns: new[] { "TenantId", "IdempotencyKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Verifications");
        }
    }
}
