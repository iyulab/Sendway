using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sendway.Core.Migrations
{
    /// <inheritdoc />
    public partial class MessageServeBy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ServeBy",
                table: "MessageRecords",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.CreateIndex(
                name: "IX_MessageRecords_Status_ServeBy_NextAttemptAt",
                table: "MessageRecords",
                columns: new[] { "Status", "ServeBy", "NextAttemptAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MessageRecords_Status_ServeBy_NextAttemptAt",
                table: "MessageRecords");

            migrationBuilder.DropColumn(
                name: "ServeBy",
                table: "MessageRecords");
        }
    }
}
