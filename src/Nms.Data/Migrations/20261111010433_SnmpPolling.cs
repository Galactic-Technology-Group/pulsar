using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nms.Data.Migrations
{
    /// <inheritdoc />
    public partial class SnmpPolling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "metric_instance",
                schema: "nms",
                table: "incidents",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "threshold_id",
                schema: "nms",
                table: "incidents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_metrics_json",
                schema: "nms",
                table: "checks",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "snmp_credential_id",
                schema: "nms",
                table: "checks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "snmp_template",
                schema: "nms",
                table: "checks",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "metric_samples",
                schema: "nms",
                columns: table => new
                {
                    time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    check_id = table.Column<Guid>(type: "uuid", nullable: false),
                    metric = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    instance = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    value = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_metric_samples", x => new { x.time, x.check_id, x.metric, x.instance });
                });

            migrationBuilder.CreateTable(
                name: "metric_thresholds",
                schema: "nms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    check_id = table.Column<Guid>(type: "uuid", nullable: false),
                    metric_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    instance = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    op = table.Column<int>(type: "integer", nullable: false),
                    value = table.Column<double>(type: "double precision", nullable: false),
                    label = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_metric_thresholds", x => x.id);
                    table.ForeignKey(
                        name: "FK_metric_thresholds_checks_check_id",
                        column: x => x.check_id,
                        principalSchema: "nms",
                        principalTable: "checks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "snmp_credentials",
                schema: "nms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: true),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    community = table.Column<string>(type: "text", nullable: true),
                    user = table.Column<string>(type: "text", nullable: true),
                    auth = table.Column<int>(type: "integer", nullable: false),
                    auth_password = table.Column<string>(type: "text", nullable: true),
                    priv = table.Column<int>(type: "integer", nullable: false),
                    priv_password = table.Column<string>(type: "text", nullable: true),
                    context = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_snmp_credentials", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_incidents_threshold_id_closed_utc",
                schema: "nms",
                table: "incidents",
                columns: new[] { "threshold_id", "closed_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_checks_snmp_credential_id",
                schema: "nms",
                table: "checks",
                column: "snmp_credential_id");

            migrationBuilder.CreateIndex(
                name: "IX_metric_samples_check_id_metric_time",
                schema: "nms",
                table: "metric_samples",
                columns: new[] { "check_id", "metric", "time" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_metric_thresholds_check_id",
                schema: "nms",
                table: "metric_thresholds",
                column: "check_id");

            migrationBuilder.CreateIndex(
                name: "IX_snmp_credentials_site_id_name",
                schema: "nms",
                table: "snmp_credentials",
                columns: new[] { "site_id", "name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_checks_snmp_credentials_snmp_credential_id",
                schema: "nms",
                table: "checks",
                column: "snmp_credential_id",
                principalSchema: "nms",
                principalTable: "snmp_credentials",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_checks_snmp_credentials_snmp_credential_id",
                schema: "nms",
                table: "checks");

            migrationBuilder.DropTable(
                name: "metric_samples",
                schema: "nms");

            migrationBuilder.DropTable(
                name: "metric_thresholds",
                schema: "nms");

            migrationBuilder.DropTable(
                name: "snmp_credentials",
                schema: "nms");

            migrationBuilder.DropIndex(
                name: "IX_incidents_threshold_id_closed_utc",
                schema: "nms",
                table: "incidents");

            migrationBuilder.DropIndex(
                name: "IX_checks_snmp_credential_id",
                schema: "nms",
                table: "checks");

            migrationBuilder.DropColumn(
                name: "metric_instance",
                schema: "nms",
                table: "incidents");

            migrationBuilder.DropColumn(
                name: "threshold_id",
                schema: "nms",
                table: "incidents");

            migrationBuilder.DropColumn(
                name: "last_metrics_json",
                schema: "nms",
                table: "checks");

            migrationBuilder.DropColumn(
                name: "snmp_credential_id",
                schema: "nms",
                table: "checks");

            migrationBuilder.DropColumn(
                name: "snmp_template",
                schema: "nms",
                table: "checks");
        }
    }
}
