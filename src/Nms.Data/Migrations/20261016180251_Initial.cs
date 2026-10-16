using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nms.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "nms");

            migrationBuilder.CreateTable(
                name: "agent_releases",
                schema: "nms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    version = table.Column<string>(type: "text", nullable: false),
                    download_url_x64 = table.Column<string>(type: "text", nullable: false),
                    download_url_arm64 = table.Column<string>(type: "text", nullable: false),
                    sha256x64 = table.Column<string>(type: "text", nullable: false),
                    sha256arm64 = table.Column<string>(type: "text", nullable: false),
                    is_current = table.Column<bool>(type: "boolean", nullable: false),
                    published_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_releases", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "check_results",
                schema: "nms",
                columns: table => new
                {
                    time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    check_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ok = table.Column<bool>(type: "boolean", nullable: false),
                    latency_ms = table.Column<int>(type: "integer", nullable: true),
                    detail = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_check_results", x => new { x.time, x.check_id });
                });

            migrationBuilder.CreateTable(
                name: "incidents",
                schema: "nms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    check_id = table.Column<Guid>(type: "uuid", nullable: true),
                    agent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    opened_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    closed_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ack_by = table.Column<string>(type: "text", nullable: true),
                    ack_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    summary = table.Column<string>(type: "text", nullable: false),
                    notified = table.Column<bool>(type: "boolean", nullable: false),
                    recovery_notified = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_incidents", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "maintenance_windows",
                schema: "nms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: true),
                    device_id = table.Column<Guid>(type: "uuid", nullable: true),
                    start_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    end_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_maintenance_windows", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "notification_channels",
                schema: "nms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    type = table.Column<int>(type: "integer", nullable: false),
                    config_json = table.Column<string>(type: "text", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_channels", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sites",
                schema: "nms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    time_zone = table.Column<string>(type: "text", nullable: false),
                    vlan_mode = table.Column<int>(type: "integer", nullable: false),
                    created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sites", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "alert_rules",
                schema: "nms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    scope = table.Column<int>(type: "integer", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: true),
                    device_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tag = table.Column<string>(type: "text", nullable: true),
                    channel_id = table.Column<Guid>(type: "uuid", nullable: false),
                    critical_only = table.Column<bool>(type: "boolean", nullable: false),
                    cooldown_sec = table.Column<int>(type: "integer", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alert_rules", x => x.id);
                    table.ForeignKey(
                        name: "FK_alert_rules_notification_channels_channel_id",
                        column: x => x.channel_id,
                        principalSchema: "nms",
                        principalTable: "notification_channels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "agents",
                schema: "nms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    hostname = table.Column<string>(type: "text", nullable: true),
                    enrollment_token_hash = table.Column<string>(type: "text", nullable: true),
                    enrollment_token_expires_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    secret_hash = table.Column<string>(type: "text", nullable: true),
                    version = table.Column<string>(type: "text", nullable: true),
                    last_seen_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    status = table.Column<int>(type: "integer", nullable: false),
                    heartbeat_interval_sec = table.Column<int>(type: "integer", nullable: false),
                    config_version = table.Column<long>(type: "bigint", nullable: false),
                    created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agents", x => x.id);
                    table.ForeignKey(
                        name: "FK_agents_sites_site_id",
                        column: x => x.site_id,
                        principalSchema: "nms",
                        principalTable: "sites",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "devices",
                schema: "nms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    address = table.Column<string>(type: "text", nullable: false),
                    vlan_id = table.Column<int>(type: "integer", nullable: true),
                    is_critical = table.Column<bool>(type: "boolean", nullable: false),
                    tags = table.Column<string[]>(type: "text[]", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: true),
                    created_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_devices", x => x.id);
                    table.ForeignKey(
                        name: "FK_devices_sites_site_id",
                        column: x => x.site_id,
                        principalSchema: "nms",
                        principalTable: "sites",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "agent_interfaces",
                schema: "nms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    vlan_id = table.Column<int>(type: "integer", nullable: true),
                    address = table.Column<string>(type: "text", nullable: false),
                    prefix_length = table.Column<int>(type: "integer", nullable: false),
                    is_enabled = table.Column<bool>(type: "boolean", nullable: false),
                    last_reported_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_agent_interfaces", x => x.id);
                    table.ForeignKey(
                        name: "FK_agent_interfaces_agents_agent_id",
                        column: x => x.agent_id,
                        principalSchema: "nms",
                        principalTable: "agents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "checks",
                schema: "nms",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<int>(type: "integer", nullable: false),
                    port = table.Column<int>(type: "integer", nullable: true),
                    http_path = table.Column<string>(type: "text", nullable: true),
                    expected_http_status = table.Column<int>(type: "integer", nullable: true),
                    body_regex = table.Column<string>(type: "text", nullable: true),
                    interval_sec = table.Column<int>(type: "integer", nullable: false),
                    timeout_ms = table.Column<int>(type: "integer", nullable: false),
                    failure_threshold = table.Column<int>(type: "integer", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    state = table.Column<int>(type: "integer", nullable: false),
                    consecutive_failures = table.Column<int>(type: "integer", nullable: false),
                    last_result_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_latency_ms = table.Column<int>(type: "integer", nullable: true),
                    state_changed_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_checks", x => x.id);
                    table.ForeignKey(
                        name: "FK_checks_devices_device_id",
                        column: x => x.device_id,
                        principalSchema: "nms",
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_agent_interfaces_agent_id_name",
                schema: "nms",
                table: "agent_interfaces",
                columns: new[] { "agent_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_agent_releases_version",
                schema: "nms",
                table: "agent_releases",
                column: "version",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_agents_enrollment_token_hash",
                schema: "nms",
                table: "agents",
                column: "enrollment_token_hash");

            migrationBuilder.CreateIndex(
                name: "IX_agents_site_id_name",
                schema: "nms",
                table: "agents",
                columns: new[] { "site_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_alert_rules_channel_id",
                schema: "nms",
                table: "alert_rules",
                column: "channel_id");

            migrationBuilder.CreateIndex(
                name: "IX_check_results_check_id_time",
                schema: "nms",
                table: "check_results",
                columns: new[] { "check_id", "time" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_checks_device_id",
                schema: "nms",
                table: "checks",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "IX_checks_state",
                schema: "nms",
                table: "checks",
                column: "state");

            migrationBuilder.CreateIndex(
                name: "IX_devices_site_id_address_vlan_id",
                schema: "nms",
                table: "devices",
                columns: new[] { "site_id", "address", "vlan_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_devices_tags",
                schema: "nms",
                table: "devices",
                column: "tags")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "IX_incidents_agent_id_closed_utc",
                schema: "nms",
                table: "incidents",
                columns: new[] { "agent_id", "closed_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_incidents_check_id_closed_utc",
                schema: "nms",
                table: "incidents",
                columns: new[] { "check_id", "closed_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_incidents_site_id_closed_utc",
                schema: "nms",
                table: "incidents",
                columns: new[] { "site_id", "closed_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_maintenance_windows_start_utc_end_utc",
                schema: "nms",
                table: "maintenance_windows",
                columns: new[] { "start_utc", "end_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_sites_name",
                schema: "nms",
                table: "sites",
                column: "name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "agent_interfaces",
                schema: "nms");

            migrationBuilder.DropTable(
                name: "agent_releases",
                schema: "nms");

            migrationBuilder.DropTable(
                name: "alert_rules",
                schema: "nms");

            migrationBuilder.DropTable(
                name: "check_results",
                schema: "nms");

            migrationBuilder.DropTable(
                name: "checks",
                schema: "nms");

            migrationBuilder.DropTable(
                name: "incidents",
                schema: "nms");

            migrationBuilder.DropTable(
                name: "maintenance_windows",
                schema: "nms");

            migrationBuilder.DropTable(
                name: "agents",
                schema: "nms");

            migrationBuilder.DropTable(
                name: "notification_channels",
                schema: "nms");

            migrationBuilder.DropTable(
                name: "devices",
                schema: "nms");

            migrationBuilder.DropTable(
                name: "sites",
                schema: "nms");
        }
    }
}
