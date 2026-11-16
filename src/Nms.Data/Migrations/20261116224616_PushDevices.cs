using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nms.Data.Migrations
{
    /// <inheritdoc />
    public partial class PushDevices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "push_devices",
                schema: "nms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    token = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    environment = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    user_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    user_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    device_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_push_devices", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_push_devices_token",
                schema: "nms",
                table: "push_devices",
                column: "token",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "push_devices",
                schema: "nms");
        }
    }
}
