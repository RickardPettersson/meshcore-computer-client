using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeshCore.Data.Migrations
{
    /// <inheritdoc />
    public partial class BatteryHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsMine",
                table: "Contacts",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "BatteryReadings",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Node = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    At = table.Column<long>(type: "INTEGER", nullable: false),
                    Millivolts = table.Column<int>(type: "INTEGER", nullable: true),
                    Note = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BatteryReadings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BatteryReadings_At",
                table: "BatteryReadings",
                column: "At");

            migrationBuilder.CreateIndex(
                name: "IX_BatteryReadings_Node_At",
                table: "BatteryReadings",
                columns: new[] { "Node", "At" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BatteryReadings");

            migrationBuilder.DropColumn(
                name: "IsMine",
                table: "Contacts");
        }
    }
}
