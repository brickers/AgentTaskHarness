using AgentTaskHarness.Application.Boards;
using AgentTaskHarness.Application.Agents;
using AgentTaskHarness.Application.Columns;
using AgentTaskHarness.Application.Tasks;
using AgentTaskHarness.Domain.Enums;
using AgentTaskHarness.Infrastructure.Persistence;
using AgentTaskHarness.Infrastructure.Agents;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using DomainTaskStatus = AgentTaskHarness.Domain.Enums.TaskStatus;

namespace AgentTaskHarness.Tests;

public class PersistenceServicesTests : IAsyncLifetime
{
	private readonly SqliteConnection connection = new("Data Source=:memory:");
	private AppDbContext dbContext = null!;
	private BoardService boards = null!;
	private ColumnService columns = null!;
	private TaskService tasks = null!;
	private TaskDependencyService dependencies = null!;
	private AgentDefinitionService agentDefinitions = null!;

	public async Task InitializeAsync()
	{
		await connection.OpenAsync();
		dbContext = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
		await dbContext.Database.MigrateAsync();
		boards = new BoardService(dbContext);
		columns = new ColumnService(dbContext);
		tasks = new TaskService(dbContext);
		dependencies = new TaskDependencyService(dbContext);
		agentDefinitions = new AgentDefinitionService(dbContext, new AgentDefinitionFolderWriter());
	}

	public async Task DisposeAsync()
	{
		await dbContext.DisposeAsync();
		await connection.DisposeAsync();
	}

	[Fact]
	public async Task CreateBoardAsync_CreatesRequiredBacklogAndTerminalColumns()
	{
		var board = await boards.CreateAsync("Harness", "/repos/harness", 2);

		var boardColumns = await columns.GetForBoardAsync(board.Id);

		Assert.Collection(boardColumns,
			column => Assert.True(column.IsBacklog),
			column => Assert.True(column.IsTerminal));
	}

	[Fact]
	public async Task CreateTaskAsync_RejectsNonBacklogAndOtherBoardColumns()
	{
		var board = await boards.CreateAsync("Harness", "/repos/harness", 1);
		var otherBoard = await boards.CreateAsync("Other", "/repos/other", 1);
		var doneColumn = (await columns.GetForBoardAsync(board.Id)).Single(column => column.IsTerminal);
		var otherBacklog = (await columns.GetForBoardAsync(otherBoard.Id)).Single(column => column.IsBacklog);

		await Assert.ThrowsAsync<InvalidOperationException>(() => tasks.CreateAsync(board.Id, doneColumn.Id, "Not allowed", ""));
		await Assert.ThrowsAsync<InvalidOperationException>(() => tasks.CreateAsync(board.Id, otherBacklog.Id, "Not allowed", ""));

		var backlog = (await columns.GetForBoardAsync(board.Id)).Single(column => column.IsBacklog);
		var task = await tasks.CreateAsync(board.Id, backlog.Id, "Allowed", "Description");
		Assert.Equal(DomainTaskStatus.Backlog, task.Status);
	}

	[Fact]
	public async Task GetForBoardAsync_OrdersTasksByCreationTimeWithSqlite()
	{
		var board = await boards.CreateAsync("Harness", "/repos/harness", 1);
		var backlog = (await columns.GetForBoardAsync(board.Id)).Single(column => column.IsBacklog);
		var later = await tasks.CreateAsync(board.Id, backlog.Id, "Later", "");
		var earlier = await tasks.CreateAsync(board.Id, backlog.Id, "Earlier", "");
		later.CreatedAt = new DateTimeOffset(2026, 9, 11, 10, 0, 0, TimeSpan.Zero);
		earlier.CreatedAt = new DateTimeOffset(2026, 9, 11, 9, 0, 0, TimeSpan.Zero);
		await dbContext.SaveChangesAsync();
		dbContext.ChangeTracker.Clear();

		var boardTasks = await tasks.GetForBoardAsync(board.Id);

		Assert.Collection(boardTasks,
			task => Assert.Equal("Earlier", task.Title),
			task => Assert.Equal("Later", task.Title));
	}

	[Fact]
	public async Task AddDependencyAsync_RejectsCrossBoardDependency()
	{
		var board = await boards.CreateAsync("Harness", "/repos/harness", 1);
		var otherBoard = await boards.CreateAsync("Other", "/repos/other", 1);
		var backlog = (await columns.GetForBoardAsync(board.Id)).Single(column => column.IsBacklog);
		var otherBacklog = (await columns.GetForBoardAsync(otherBoard.Id)).Single(column => column.IsBacklog);
		var task = await tasks.CreateAsync(board.Id, backlog.Id, "Task", "");
		var otherTask = await tasks.CreateAsync(otherBoard.Id, otherBacklog.Id, "Other task", "");

		await Assert.ThrowsAsync<InvalidOperationException>(() => dependencies.AddAsync(task.Id, otherTask.Id));
		Assert.Empty(await dependencies.GetForTaskAsync(task.Id));
	}

	[Fact]
	public async Task AddAllowedTransitionAsync_RejectsCrossBoardTransition()
	{
		var board = await boards.CreateAsync("Harness", "/repos/harness", 1);
		var otherBoard = await boards.CreateAsync("Other", "/repos/other", 1);
		var backlog = (await columns.GetForBoardAsync(board.Id)).Single(column => column.IsBacklog);
		var otherBacklog = (await columns.GetForBoardAsync(otherBoard.Id)).Single(column => column.IsBacklog);

		await Assert.ThrowsAsync<InvalidOperationException>(() => columns.AddAllowedTransitionAsync(backlog.Id, otherBacklog.Id));
	}

	[Fact]
	public async Task ReorderAsync_KeepsBacklogFirstAndDoneLast()
	{
		var board = await boards.CreateAsync("Harness", "/repos/harness", 1);
		var boardColumns = await columns.GetForBoardAsync(board.Id);

		await Assert.ThrowsAsync<InvalidOperationException>(() => columns.ReorderAsync(board.Id, [boardColumns[1].Id, boardColumns[0].Id]));
	}

	[Fact]
	public async Task ColumnOrderingAsync_ProtectsBacklogAndDonePositions()
	{
		var board = await boards.CreateAsync("Harness", "/repos/harness", 1);
		var initialColumns = await columns.GetForBoardAsync(board.Id);
		var working = await columns.CreateAsync(board.Id, "Working", initialColumns.Count);
		var boardColumns = await columns.GetForBoardAsync(board.Id);

		Assert.Collection(boardColumns,
			column => Assert.True(column.IsBacklog),
			column => Assert.Equal(working.Id, column.Id),
			column => Assert.True(column.IsTerminal));
		await Assert.ThrowsAsync<InvalidOperationException>(() => columns.ReorderAsync(board.Id, [working.Id, boardColumns[0].Id, boardColumns[2].Id]));
		await Assert.ThrowsAsync<InvalidOperationException>(() => columns.UpdateAsync(boardColumns[0].Id, "Backlog", 1, true, false, null));
	}

	[Fact]
	public async Task AgentDefinitionAsync_SupportsCreateUpdateAndDelete()
	{
		var board = await boards.CreateAsync("Harness", "/repos/harness", 1);
		var definition = await agentDefinitions.CreateAsync(board.Id, "Implementer", "/agents/implementer");

		await agentDefinitions.UpdateAsync(definition.Id, "Reviewer", "/agents/reviewer");
		var boardDefinitions = await agentDefinitions.GetForBoardAsync(board.Id);
		Assert.Single(boardDefinitions);
		Assert.Equal("Reviewer", boardDefinitions[0].Name);

		await agentDefinitions.DeleteAsync(definition.Id);
		Assert.Empty(await agentDefinitions.GetForBoardAsync(board.Id));
	}
}