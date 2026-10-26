using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpportunityPilot.Infrastructure.Persistence.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class AddResumeBuilder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "JobDescription",
                schema: "app",
                table: "staffing_deals",
                type: "character varying(8000)",
                maxLength: 8000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "candidate_resumes",
                schema: "app",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    CandidateId = table.Column<Guid>(type: "uuid", nullable: false),
                    DealId = table.Column<Guid>(type: "uuid", nullable: false),
                    Data = table.Column<string>(type: "text", nullable: false),
                    MatchScore = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SalesScore = table.Column<int>(type: "integer", nullable: true),
                    SalesFeedback = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SubmittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReviewedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_candidate_resumes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_candidate_resumes_OwnerId_CandidateId_DealId",
                schema: "app",
                table: "candidate_resumes",
                columns: new[] { "OwnerId", "CandidateId", "DealId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "candidate_resumes",
                schema: "app");

            migrationBuilder.DropColumn(
                name: "JobDescription",
                schema: "app",
                table: "staffing_deals");
        }
    }
}
