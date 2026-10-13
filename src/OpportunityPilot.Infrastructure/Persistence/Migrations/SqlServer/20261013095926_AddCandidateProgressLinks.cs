using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpportunityPilot.Infrastructure.Persistence.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AddCandidateProgressLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "candidate_progress_links",
                schema: "app",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CandidateId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Show = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_candidate_progress_links", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_candidate_progress_links_OwnerId_CandidateId",
                schema: "app",
                table: "candidate_progress_links",
                columns: new[] { "OwnerId", "CandidateId" });

            migrationBuilder.CreateIndex(
                name: "IX_candidate_progress_links_TokenHash",
                schema: "app",
                table: "candidate_progress_links",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "candidate_progress_links",
                schema: "app");
        }
    }
}
