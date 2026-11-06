using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nms.Data.Migrations
{
    /// <inheritdoc />
    public partial class AgentClaims : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_claims",
                schema: "nms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    claim_key_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    hostname = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    agent_version = table.Column<string>(type: "text", nullable: true),
                    requested_name = table.Column<string>(type: "text", nullable: true),
                    interfaces_json = table.Column<string>(type: "text", nullable: false),
                    remote_address = table.Column<string>(type: "text", nullable: true),
                    state = table.Column<int>(type: "integer", nullable: false),
                    created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    agent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    approved_by = table.Column<string>(type: "text", nullable: true),
                    approved_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_claims", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_agent_claims_code",
                schema: "nms",
                table: "agent_claims",
                column: "code");

            migrationBuilder.CreateIndex(
                name: "IX_agent_claims_state_expires_utc",
                schema: "nms",
                table: "agent_claims",
                columns: new[] { "state", "expires_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_claims",
                schema: "nms");
        }
    }
}
