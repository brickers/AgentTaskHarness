using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentTaskHarness.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HierarchicalModelRework : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AgentRuns_Columns_ColumnId",
                table: "AgentRuns");

            migrationBuilder.DropForeignKey(
                name: "FK_AgentRuns_Tasks_TaskId",
                table: "AgentRuns");

            migrationBuilder.DropTable(
                name: "ColumnTransitions");

            migrationBuilder.DropTable(
                name: "TaskDependencies");

            migrationBuilder.DropTable(
                name: "Tasks");

            migrationBuilder.DropTable(
                name: "Columns");

            migrationBuilder.DropIndex(
                name: "IX_AgentRuns_ColumnId",
                table: "AgentRuns");

            migrationBuilder.DropIndex(
                name: "IX_AgentRuns_TaskId",
                table: "AgentRuns");

            migrationBuilder.RenameColumn(
                name: "TaskId",
                table: "AgentRuns",
                newName: "TimeSpent");

            migrationBuilder.RenameColumn(
                name: "ColumnId",
                table: "AgentRuns",
                newName: "CardId");

            migrationBuilder.AddColumn<int>(
                name: "AgentReviewFailThreshold",
                table: "Boards",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "HumanReviewFailThreshold",
                table: "Boards",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "SkipFeatureHumanReview",
                table: "Boards",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "SkipStepHumanReview",
                table: "Boards",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "CardType",
                table: "AgentRuns",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "TokensUsed",
                table: "AgentRuns",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "Comments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CardType = table.Column<int>(type: "INTEGER", nullable: false),
                    CardId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Author = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Body = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Comments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Features",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    BoardId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Requirements = table.Column<string>(type: "TEXT", nullable: false),
                    AcceptanceCriteria = table.Column<string>(type: "TEXT", nullable: false),
                    SuggestedSolution = table.Column<string>(type: "TEXT", nullable: false),
                    WorkflowColumn = table.Column<int>(type: "INTEGER", nullable: false),
                    AlwaysRequireHumanReview = table.Column<bool>(type: "INTEGER", nullable: false),
                    AgentReviewFailCount = table.Column<int>(type: "INTEGER", nullable: false),
                    HumanReviewFailCount = table.Column<int>(type: "INTEGER", nullable: false),
                    BranchName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    WorktreePath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    MergeConflictPending = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Features", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Features_Boards_BoardId",
                        column: x => x.BoardId,
                        principalTable: "Boards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FeatureDependencies",
                columns: table => new
                {
                    FeatureId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DependsOnFeatureId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeatureDependencies", x => new { x.FeatureId, x.DependsOnFeatureId });
                    table.ForeignKey(
                        name: "FK_FeatureDependencies_Features_DependsOnFeatureId",
                        column: x => x.DependsOnFeatureId,
                        principalTable: "Features",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FeatureDependencies_Features_FeatureId",
                        column: x => x.FeatureId,
                        principalTable: "Features",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Steps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    FeatureId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    GuidanceNotes = table.Column<string>(type: "TEXT", nullable: false),
                    WorkflowColumn = table.Column<int>(type: "INTEGER", nullable: false),
                    AlwaysRequireHumanReview = table.Column<bool>(type: "INTEGER", nullable: false),
                    AgentReviewFailCount = table.Column<int>(type: "INTEGER", nullable: false),
                    HumanReviewFailCount = table.Column<int>(type: "INTEGER", nullable: false),
                    BranchName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    WorktreePath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    TokensUsed = table.Column<long>(type: "INTEGER", nullable: false),
                    TimeSpent = table.Column<TimeSpan>(type: "TEXT", nullable: false),
                    MergeConflictPending = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Steps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Steps_Features_FeatureId",
                        column: x => x.FeatureId,
                        principalTable: "Features",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StepDependencies",
                columns: table => new
                {
                    StepId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DependsOnStepId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StepDependencies", x => new { x.StepId, x.DependsOnStepId });
                    table.ForeignKey(
                        name: "FK_StepDependencies_Steps_DependsOnStepId",
                        column: x => x.DependsOnStepId,
                        principalTable: "Steps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StepDependencies_Steps_StepId",
                        column: x => x.StepId,
                        principalTable: "Steps",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Boards_AgentReviewFailThreshold",
                table: "Boards",
                sql: "AgentReviewFailThreshold >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Boards_HumanReviewFailThreshold",
                table: "Boards",
                sql: "HumanReviewFailThreshold >= 0");

            migrationBuilder.CreateIndex(
                name: "IX_AgentRuns_CardType_CardId",
                table: "AgentRuns",
                columns: new[] { "CardType", "CardId" });

            migrationBuilder.CreateIndex(
                name: "IX_Comments_CardType_CardId_CreatedAt",
                table: "Comments",
                columns: new[] { "CardType", "CardId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_FeatureDependencies_DependsOnFeatureId",
                table: "FeatureDependencies",
                column: "DependsOnFeatureId");

            migrationBuilder.CreateIndex(
                name: "IX_Features_BoardId_WorkflowColumn",
                table: "Features",
                columns: new[] { "BoardId", "WorkflowColumn" });

            migrationBuilder.CreateIndex(
                name: "IX_StepDependencies_DependsOnStepId",
                table: "StepDependencies",
                column: "DependsOnStepId");

            migrationBuilder.CreateIndex(
                name: "IX_Steps_FeatureId_WorkflowColumn",
                table: "Steps",
                columns: new[] { "FeatureId", "WorkflowColumn" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Comments");

            migrationBuilder.DropTable(
                name: "FeatureDependencies");

            migrationBuilder.DropTable(
                name: "StepDependencies");

            migrationBuilder.DropTable(
                name: "Steps");

            migrationBuilder.DropTable(
                name: "Features");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Boards_AgentReviewFailThreshold",
                table: "Boards");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Boards_HumanReviewFailThreshold",
                table: "Boards");

            migrationBuilder.DropIndex(
                name: "IX_AgentRuns_CardType_CardId",
                table: "AgentRuns");

            migrationBuilder.DropColumn(
                name: "AgentReviewFailThreshold",
                table: "Boards");

            migrationBuilder.DropColumn(
                name: "HumanReviewFailThreshold",
                table: "Boards");

            migrationBuilder.DropColumn(
                name: "SkipFeatureHumanReview",
                table: "Boards");

            migrationBuilder.DropColumn(
                name: "SkipStepHumanReview",
                table: "Boards");

            migrationBuilder.DropColumn(
                name: "CardType",
                table: "AgentRuns");

            migrationBuilder.DropColumn(
                name: "TokensUsed",
                table: "AgentRuns");

            migrationBuilder.RenameColumn(
                name: "TimeSpent",
                table: "AgentRuns",
                newName: "TaskId");

            migrationBuilder.RenameColumn(
                name: "CardId",
                table: "AgentRuns",
                newName: "ColumnId");

            migrationBuilder.CreateTable(
                name: "Columns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentDefinitionId = table.Column<Guid>(type: "TEXT", nullable: true),
                    BoardId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IsBacklog = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsTerminal = table.Column<bool>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Columns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Columns_AgentDefinitions_AgentDefinitionId",
                        column: x => x.AgentDefinitionId,
                        principalTable: "AgentDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Columns_Boards_BoardId",
                        column: x => x.BoardId,
                        principalTable: "Boards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ColumnTransitions",
                columns: table => new
                {
                    FromColumnId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ToColumnId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ColumnTransitions", x => new { x.FromColumnId, x.ToColumnId });
                    table.ForeignKey(
                        name: "FK_ColumnTransitions_Columns_FromColumnId",
                        column: x => x.FromColumnId,
                        principalTable: "Columns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ColumnTransitions_Columns_ToColumnId",
                        column: x => x.ToColumnId,
                        principalTable: "Columns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Tasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    BoardId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ColumnId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BranchName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    MergeConflictPending = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    WorktreePath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Tasks_Boards_BoardId",
                        column: x => x.BoardId,
                        principalTable: "Boards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Tasks_Columns_ColumnId",
                        column: x => x.ColumnId,
                        principalTable: "Columns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TaskDependencies",
                columns: table => new
                {
                    TaskId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DependsOnTaskId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskDependencies", x => new { x.TaskId, x.DependsOnTaskId });
                    table.ForeignKey(
                        name: "FK_TaskDependencies_Tasks_DependsOnTaskId",
                        column: x => x.DependsOnTaskId,
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TaskDependencies_Tasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentRuns_ColumnId",
                table: "AgentRuns",
                column: "ColumnId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentRuns_TaskId",
                table: "AgentRuns",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_Columns_AgentDefinitionId",
                table: "Columns",
                column: "AgentDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_Columns_BoardId_IsBacklog",
                table: "Columns",
                columns: new[] { "BoardId", "IsBacklog" },
                unique: true,
                filter: "IsBacklog = 1");

            migrationBuilder.CreateIndex(
                name: "IX_Columns_BoardId_IsTerminal",
                table: "Columns",
                columns: new[] { "BoardId", "IsTerminal" },
                unique: true,
                filter: "IsTerminal = 1");

            migrationBuilder.CreateIndex(
                name: "IX_Columns_BoardId_Order",
                table: "Columns",
                columns: new[] { "BoardId", "Order" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ColumnTransitions_ToColumnId",
                table: "ColumnTransitions",
                column: "ToColumnId");

            migrationBuilder.CreateIndex(
                name: "IX_TaskDependencies_DependsOnTaskId",
                table: "TaskDependencies",
                column: "DependsOnTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_BoardId_ColumnId",
                table: "Tasks",
                columns: new[] { "BoardId", "ColumnId" });

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_ColumnId",
                table: "Tasks",
                column: "ColumnId");

            migrationBuilder.AddForeignKey(
                name: "FK_AgentRuns_Columns_ColumnId",
                table: "AgentRuns",
                column: "ColumnId",
                principalTable: "Columns",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AgentRuns_Tasks_TaskId",
                table: "AgentRuns",
                column: "TaskId",
                principalTable: "Tasks",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
