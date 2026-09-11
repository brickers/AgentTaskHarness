using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentTaskHarness.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentComponents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ConfigContent = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentComponents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Boards",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    RepoPath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    ConcurrencyLimit = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Boards", x => x.Id);
                    table.CheckConstraint("CK_Boards_ConcurrencyLimit", "ConcurrencyLimit > 0");
                });

            migrationBuilder.CreateTable(
                name: "AgentDefinitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    BoardId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    FolderPath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentDefinitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentDefinitions_Boards_BoardId",
                        column: x => x.BoardId,
                        principalTable: "Boards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AgentDefinitionComponents",
                columns: table => new
                {
                    AgentDefinitionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ComponentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentDefinitionComponents", x => new { x.AgentDefinitionId, x.ComponentId });
                    table.ForeignKey(
                        name: "FK_AgentDefinitionComponents_AgentComponents_ComponentId",
                        column: x => x.ComponentId,
                        principalTable: "AgentComponents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentDefinitionComponents_AgentDefinitions_AgentDefinitionId",
                        column: x => x.AgentDefinitionId,
                        principalTable: "AgentDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Columns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    BoardId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    IsBacklog = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsTerminal = table.Column<bool>(type: "INTEGER", nullable: false),
                    AgentDefinitionId = table.Column<Guid>(type: "TEXT", nullable: true)
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
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    BranchName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    WorktreePath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
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
                name: "AgentRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TaskId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ColumnId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProcessId = table.Column<int>(type: "INTEGER", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    SessionLink = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    EndedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentRuns_Columns_ColumnId",
                        column: x => x.ColumnId,
                        principalTable: "Columns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentRuns_Tasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
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
                name: "IX_AgentDefinitionComponents_AgentDefinitionId_Order",
                table: "AgentDefinitionComponents",
                columns: new[] { "AgentDefinitionId", "Order" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentDefinitionComponents_ComponentId",
                table: "AgentDefinitionComponents",
                column: "ComponentId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentDefinitions_BoardId_Name",
                table: "AgentDefinitions",
                columns: new[] { "BoardId", "Name" },
                unique: true);

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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentDefinitionComponents");

            migrationBuilder.DropTable(
                name: "AgentRuns");

            migrationBuilder.DropTable(
                name: "ColumnTransitions");

            migrationBuilder.DropTable(
                name: "TaskDependencies");

            migrationBuilder.DropTable(
                name: "AgentComponents");

            migrationBuilder.DropTable(
                name: "Tasks");

            migrationBuilder.DropTable(
                name: "Columns");

            migrationBuilder.DropTable(
                name: "AgentDefinitions");

            migrationBuilder.DropTable(
                name: "Boards");
        }
    }
}
