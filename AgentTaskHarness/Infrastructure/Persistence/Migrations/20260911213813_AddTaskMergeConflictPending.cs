using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentTaskHarness.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskMergeConflictPending : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "MergeConflictPending",
                table: "Tasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MergeConflictPending",
                table: "Tasks");
        }
    }
}
