using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentTaskHarness.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentColumnAssignments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentColumnAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    BoardId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ColumnScope = table.Column<int>(type: "INTEGER", nullable: false),
                    AgentDefinitionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MatchCriteria = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentColumnAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentColumnAssignments_AgentDefinitions_AgentDefinitionId",
                        column: x => x.AgentDefinitionId,
                        principalTable: "AgentDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AgentColumnAssignments_Boards_BoardId",
                        column: x => x.BoardId,
                        principalTable: "Boards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentColumnAssignments_AgentDefinitionId",
                table: "AgentColumnAssignments",
                column: "AgentDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentColumnAssignments_BoardId_ColumnScope",
                table: "AgentColumnAssignments",
                columns: new[] { "BoardId", "ColumnScope" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentColumnAssignments");
        }
    }
}
