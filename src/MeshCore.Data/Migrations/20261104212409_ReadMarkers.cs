using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeshCore.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReadMarkers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReadMarkers",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ConversationKey = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    LastReadMessageId = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReadMarkers", x => new { x.UserId, x.ConversationKey });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReadMarkers");
        }
    }
}
