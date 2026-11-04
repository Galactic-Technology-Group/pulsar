using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nms.Data.Migrations;

/// <summary>
/// Data-only migration: the built-in per-site device created as "Internet line" (agent-side probe of public
/// resolvers) becomes "Gateway" (worker-side probe of the site's public address). The address must be filled in
/// by an operator, so it is cleared, and the few samples the old check produced are dropped.
/// </summary>
[DbContext(typeof(NmsDbContext))]
[Migration("20261104170000_GatewayCheck")]
public partial class GatewayCheck : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DELETE FROM nms.check_results
             WHERE check_id IN (SELECT c.id FROM nms.checks c JOIN nms.devices d ON d.id = c.device_id WHERE d.kind = 1);
            UPDATE nms.checks SET state = 0, consecutive_failures = 0, last_result_utc = NULL, last_latency_ms = NULL, state_changed_utc = NULL
             WHERE device_id IN (SELECT id FROM nms.devices WHERE kind = 1);
            UPDATE nms.incidents SET closed_utc = now() WHERE kind = 2 AND closed_utc IS NULL;
            UPDATE nms.devices SET name = 'Gateway', address = '', tags = ARRAY['gateway']::text[] WHERE kind = 1;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // The previous shape cannot be restored meaningfully; the column itself is unchanged.
    }
}
