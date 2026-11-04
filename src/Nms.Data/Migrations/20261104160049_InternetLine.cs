using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nms.Data.Migrations
{
    /// <inheritdoc />
    public partial class InternetLine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "kind",
                schema: "nms",
                table: "devices",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "kind",
                schema: "nms",
                table: "devices");
        }
    }
}
