using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpportunityPilot.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddCandidateShareExtras : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CandidatePrep",
                schema: "app",
                table: "staffing_interviews",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Note",
                schema: "app",
                table: "candidate_progress_links",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "progress_role_suggestions",
                schema: "app",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    CandidateId = table.Column<Guid>(type: "uuid", nullable: false),
                    DealId = table.Column<Guid>(type: "uuid", nullable: false),
                    Answer = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SuggestedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AnsweredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_progress_role_suggestions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_progress_role_suggestions_OwnerId_CandidateId_DealId",
                schema: "app",
                table: "progress_role_suggestions",
                columns: new[] { "OwnerId", "CandidateId", "DealId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "progress_role_suggestions",
                schema: "app");

            migrationBuilder.DropColumn(
                name: "CandidatePrep",
                schema: "app",
                table: "staffing_interviews");

            migrationBuilder.DropColumn(
                name: "Note",
                schema: "app",
                table: "candidate_progress_links");
        }
    }
}
