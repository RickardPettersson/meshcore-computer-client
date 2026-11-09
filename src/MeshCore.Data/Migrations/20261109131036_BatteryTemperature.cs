using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeshCore.Data.Migrations
{
    /// <inheritdoc />
    public partial class BatteryTemperature : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "TemperatureC",
                table: "BatteryReadings",
                type: "REAL",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TemperatureC",
                table: "BatteryReadings");
        }
    }
}
