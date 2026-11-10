using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Nms.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase1DataModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "address",
                schema: "nms",
                table: "sites",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "latitude",
                schema: "nms",
                table: "sites",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "longitude",
                schema: "nms",
                table: "sites",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "mac_address",
                schema: "nms",
                table: "devices",
                type: "character varying(17)",
                maxLength: 17,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "audit_events",
                schema: "nms",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    time_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    actor = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    actor_id = table.Column<string>(type: "text", nullable: true),
                    action = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    target_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    target_id = table.Column<Guid>(type: "uuid", nullable: true),
                    site_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    detail = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_events", x => x.id);
                });

            // Discovery used to store the MAC in the notes as exactly "MAC aa:bb:cc:dd:ee:ff". Move it into the new
            // column (and clear those notes) where it is unambiguous within the site, so the unique index below holds.
            migrationBuilder.Sql("""
                UPDATE nms.devices d
                SET mac_address = lower(substring(d.notes from '^MAC ([0-9A-Fa-f:]{17})$')), notes = NULL
                WHERE d.notes ~ '^MAC [0-9A-Fa-f]{2}(:[0-9A-Fa-f]{2}){5}$'
                  AND (SELECT count(*) FROM nms.devices o WHERE o.site_id = d.site_id AND lower(o.notes) = lower(d.notes)) = 1;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_devices_site_id_mac_address",
                schema: "nms",
                table: "devices",
                columns: new[] { "site_id", "mac_address" },
                unique: true,
                filter: "mac_address IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_site_id_time_utc",
                schema: "nms",
                table: "audit_events",
                columns: new[] { "site_id", "time_utc" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_time_utc",
                schema: "nms",
                table: "audit_events",
                column: "time_utc",
                descending: new bool[0]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_events",
                schema: "nms");

            migrationBuilder.DropIndex(
                name: "IX_devices_site_id_mac_address",
                schema: "nms",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "address",
                schema: "nms",
                table: "sites");

            migrationBuilder.DropColumn(
                name: "latitude",
                schema: "nms",
                table: "sites");

            migrationBuilder.DropColumn(
                name: "longitude",
                schema: "nms",
                table: "sites");

            migrationBuilder.DropColumn(
                name: "mac_address",
                schema: "nms",
                table: "devices");
        }
    }
}
