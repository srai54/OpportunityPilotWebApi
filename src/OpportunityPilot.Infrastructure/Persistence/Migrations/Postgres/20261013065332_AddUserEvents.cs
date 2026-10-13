using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpportunityPilot.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddUserEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "user_events",
                schema: "app",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Detail = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    OpportunityId = table.Column<Guid>(type: "uuid", nullable: true),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_events", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_user_events_OwnerId_OccurredAt",
                schema: "app",
                table: "user_events",
                columns: new[] { "OwnerId", "OccurredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_events",
                schema: "app");
        }
    }
}
