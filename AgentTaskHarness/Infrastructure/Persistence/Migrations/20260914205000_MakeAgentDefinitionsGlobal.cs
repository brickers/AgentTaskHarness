using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentTaskHarness.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260914205000_MakeAgentDefinitionsGlobal")]
public partial class MakeAgentDefinitionsGlobal : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE "ef_temp_AgentDefinitions" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_AgentDefinitions" PRIMARY KEY,
                "Instructions" TEXT NOT NULL,
                "Name" TEXT NOT NULL,
                "Prompt" TEXT NOT NULL,
                "ToolConfiguration" TEXT NOT NULL
            );
            INSERT INTO "ef_temp_AgentDefinitions" ("Id", "Instructions", "Name", "Prompt", "ToolConfiguration")
            SELECT "Id", "Instructions", "Name", "Prompt", "ToolConfiguration"
            FROM "AgentDefinitions";
            DROP TABLE "AgentDefinitions";
            ALTER TABLE "ef_temp_AgentDefinitions" RENAME TO "AgentDefinitions";
            CREATE INDEX "IX_AgentDefinitions_Name" ON "AgentDefinitions" ("Name");
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_AgentDefinitions_Name",
            table: "AgentDefinitions");

        migrationBuilder.AddColumn<Guid>(
            name: "BoardId",
            table: "AgentDefinitions",
            type: "TEXT",
            nullable: false,
            defaultValue: Guid.Empty);

        migrationBuilder.CreateIndex(
            name: "IX_AgentDefinitions_BoardId_Name",
            table: "AgentDefinitions",
            columns: new[] { "BoardId", "Name" },
            unique: true);

        migrationBuilder.AddForeignKey(
            name: "FK_AgentDefinitions_Boards_BoardId",
            table: "AgentDefinitions",
            column: "BoardId",
            principalTable: "Boards",
            principalColumn: "Id",
            onDelete: ReferentialAction.Cascade);
    }
}
