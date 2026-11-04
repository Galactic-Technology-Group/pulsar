using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nms.Data.Migrations;

/// <summary>
/// Azure's outbound path (Standard Load Balancer / NAT Gateway) does not carry ICMP, so gateway checks probe a TCP
/// port by default. Existing gateway checks without a port get 443.
/// </summary>
[DbContext(typeof(NmsDbContext))]
[Migration("20261104180000_GatewayTcpDefault")]
public partial class GatewayTcpDefault : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("UPDATE nms.checks SET port = 443 WHERE type = 3 AND port IS NULL;");

    protected override void Down(MigrationBuilder migrationBuilder) { }
}
