using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MeshCore.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Channels",
                columns: table => new
                {
                    Index = table.Column<byte>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Channels", x => x.Index);
                });

            migrationBuilder.CreateTable(
                name: "Contacts",
                columns: table => new
                {
                    PublicKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    KeyPrefix = table.Column<string>(type: "TEXT", maxLength: 12, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Type = table.Column<byte>(type: "INTEGER", nullable: false),
                    Flags = table.Column<byte>(type: "INTEGER", nullable: false),
                    OutPathLength = table.Column<byte>(type: "INTEGER", nullable: false),
                    OutPath = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Latitude = table.Column<double>(type: "REAL", nullable: false),
                    Longitude = table.Column<double>(type: "REAL", nullable: false),
                    LastAdvert = table.Column<long>(type: "INTEGER", nullable: false),
                    LastModified = table.Column<long>(type: "INTEGER", nullable: false),
                    LastHeard = table.Column<long>(type: "INTEGER", nullable: true),
                    IsOnRadio = table.Column<bool>(type: "INTEGER", nullable: false),
                    FirstSeen = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Contacts", x => x.PublicKey);
                });

            migrationBuilder.CreateTable(
                name: "Messages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    Direction = table.Column<int>(type: "INTEGER", nullable: false),
                    ContactKeyPrefix = table.Column<string>(type: "TEXT", maxLength: 12, nullable: true),
                    ChannelIndex = table.Column<byte>(type: "INTEGER", nullable: true),
                    SenderName = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Text = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    TextType = table.Column<byte>(type: "INTEGER", nullable: false),
                    SignerPrefix = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    SenderTimestamp = table.Column<long>(type: "INTEGER", nullable: false),
                    ReceivedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Snr = table.Column<double>(type: "REAL", nullable: true),
                    Hops = table.Column<int>(type: "INTEGER", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ExpectedAck = table.Column<uint>(type: "INTEGER", nullable: true),
                    AckDeadline = table.Column<long>(type: "INTEGER", nullable: true),
                    RoundTripMs = table.Column<int>(type: "INTEGER", nullable: true),
                    Flooded = table.Column<bool>(type: "INTEGER", nullable: true),
                    SentByUserId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Error = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Messages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_KeyPrefix",
                table: "Contacts",
                column: "KeyPrefix");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ExpectedAck",
                table: "Messages",
                column: "ExpectedAck");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_Kind_ChannelIndex_ReceivedAt",
                table: "Messages",
                columns: new[] { "Kind", "ChannelIndex", "ReceivedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_Kind_ContactKeyPrefix_ReceivedAt",
                table: "Messages",
                columns: new[] { "Kind", "ContactKeyPrefix", "ReceivedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Channels");

            migrationBuilder.DropTable(
                name: "Contacts");

            migrationBuilder.DropTable(
                name: "Messages");
        }
    }
}
