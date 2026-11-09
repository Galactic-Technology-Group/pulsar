using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nms.Data.Migrations
{
    /// <inheritdoc />
    public partial class AgentRollback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "rollback_from_version",
                schema: "nms",
                table: "agents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "rollback_reason",
                schema: "nms",
                table: "agents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "rollback_utc",
                schema: "nms",
                table: "agents",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "rollback_from_version",
                schema: "nms",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "rollback_reason",
                schema: "nms",
                table: "agents");

            migrationBuilder.DropColumn(
                name: "rollback_utc",
                schema: "nms",
                table: "agents");
        }
    }
}
