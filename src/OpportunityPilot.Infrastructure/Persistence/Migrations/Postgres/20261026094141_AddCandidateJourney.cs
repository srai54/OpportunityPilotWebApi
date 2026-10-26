using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpportunityPilot.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddCandidateJourney : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AlignedSalesperson",
                schema: "app",
                table: "staffing_candidates",
                type: "character varying(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "JoinedAt",
                schema: "app",
                table: "staffing_candidates",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Priority",
                schema: "app",
                table: "staffing_candidates",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "candidate_journey_entries",
                schema: "app",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    CandidateId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Detail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    OccurredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Hours = table.Column<int>(type: "integer", nullable: true),
                    AddedBy = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_candidate_journey_entries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_candidate_journey_entries_OwnerId_CandidateId_OccurredAt",
                schema: "app",
                table: "candidate_journey_entries",
                columns: new[] { "OwnerId", "CandidateId", "OccurredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "candidate_journey_entries",
                schema: "app");

            migrationBuilder.DropColumn(
                name: "AlignedSalesperson",
                schema: "app",
                table: "staffing_candidates");

            migrationBuilder.DropColumn(
                name: "JoinedAt",
                schema: "app",
                table: "staffing_candidates");

            migrationBuilder.DropColumn(
                name: "Priority",
                schema: "app",
                table: "staffing_candidates");
        }
    }
}
