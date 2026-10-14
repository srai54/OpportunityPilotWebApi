using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OpportunityPilot.Infrastructure.Persistence.Migrations.SqlServer
{
    /// <inheritdoc />
    public partial class AddJdCircle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CircleMappedAt",
                schema: "app",
                table: "opportunities",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CircleRequestedAt",
                schema: "app",
                table: "opportunities",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "Scopes",
                schema: "app",
                table: "agent_keys",
                type: "int",
                nullable: false,
                defaultValue: 15,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 7);

            // Keys that had every scope before People existed (7) keep "every scope" (15), so existing agents keep working.
            migrationBuilder.Sql("UPDATE [app].[agent_keys] SET [Scopes] = 15 WHERE [Scopes] = 7;");

            migrationBuilder.CreateTable(
                name: "circle_people",
                schema: "app",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OpportunityId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Headline = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    ProfileUrl = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Relation = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Degree = table.Column<int>(type: "int", nullable: true),
                    Source = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    FoundAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_circle_people", x => x.Id);
                    table.ForeignKey(
                        name: "FK_circle_people_opportunities_OpportunityId",
                        column: x => x.OpportunityId,
                        principalSchema: "app",
                        principalTable: "opportunities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_circle_people_OpportunityId",
                schema: "app",
                table: "circle_people",
                column: "OpportunityId");

            migrationBuilder.CreateIndex(
                name: "IX_circle_people_OwnerId_OpportunityId_ProfileUrl",
                schema: "app",
                table: "circle_people",
                columns: new[] { "OwnerId", "OpportunityId", "ProfileUrl" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "circle_people",
                schema: "app");

            migrationBuilder.DropColumn(
                name: "CircleMappedAt",
                schema: "app",
                table: "opportunities");

            migrationBuilder.DropColumn(
                name: "CircleRequestedAt",
                schema: "app",
                table: "opportunities");

            // Back to three scopes: drop the People bit; a key that had only People can do nothing, so it is revoked.
            migrationBuilder.Sql("UPDATE [app].[agent_keys] SET [RevokedAt] = SYSUTCDATETIME() WHERE [Scopes] = 8 AND [RevokedAt] IS NULL;");
            migrationBuilder.Sql("UPDATE [app].[agent_keys] SET [Scopes] = CASE WHEN [Scopes] = 8 THEN 7 ELSE [Scopes] - 8 END WHERE [Scopes] >= 8;");

            migrationBuilder.AlterColumn<int>(
                name: "Scopes",
                schema: "app",
                table: "agent_keys",
                type: "int",
                nullable: false,
                defaultValue: 7,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 15);
        }
    }
}
