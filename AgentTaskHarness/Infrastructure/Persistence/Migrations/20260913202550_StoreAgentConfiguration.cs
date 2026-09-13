using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentTaskHarness.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StoreAgentConfiguration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FolderPath",
                table: "AgentDefinitions");

            migrationBuilder.AddColumn<string>(
                name: "Instructions",
                table: "AgentDefinitions",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Prompt",
                table: "AgentDefinitions",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "ToolConfiguration",
                table: "AgentDefinitions",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Instructions",
                table: "AgentDefinitions");

            migrationBuilder.DropColumn(
                name: "Prompt",
                table: "AgentDefinitions");

            migrationBuilder.DropColumn(
                name: "ToolConfiguration",
                table: "AgentDefinitions");

            migrationBuilder.AddColumn<string>(
                name: "FolderPath",
                table: "AgentDefinitions",
                type: "TEXT",
                maxLength: 1024,
                nullable: false,
                defaultValue: "");
        }
    }
}
