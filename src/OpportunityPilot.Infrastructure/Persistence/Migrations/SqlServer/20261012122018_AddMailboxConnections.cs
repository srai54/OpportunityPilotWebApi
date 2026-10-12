using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpportunityPilot.Infrastructure.Persistence.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AddMailboxConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mailbox_connections",
                schema: "app",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    EmailAddress = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    EncryptedRefreshToken = table.Column<string>(type: "nvarchar(max)", maxLength: 8000, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ConnectedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_mailbox_connections", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_mailbox_connections_OwnerId_Provider",
                schema: "app",
                table: "mailbox_connections",
                columns: new[] { "OwnerId", "Provider" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mailbox_connections",
                schema: "app");
        }
    }
}
