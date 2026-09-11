using AgentTaskHarness.Application.Abstractions;
using AgentTaskHarness.Application.Agents;
using AgentTaskHarness.Application.Boards;
using AgentTaskHarness.Application.Columns;
using AgentTaskHarness.Application.Tasks;
using AgentTaskHarness.Domain.Entities;
using AgentTaskHarness.Domain.Enums;
using AgentTaskHarness.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using DomainTaskStatus = AgentTaskHarness.Domain.Enums.TaskStatus;

namespace AgentTaskHarness.Tests;

public class TransitionLifecycleTests : IAsyncLifetime
{
	private readonly SqliteConnection connection = new("Data Source=:memory:");
	private AppDbContext dbContext = null!;
	private BoardService boards = null!;
	private ColumnService columns = null!;
	private TaskService tasks = null!;
	private TaskDependencyService dependencies = null!;
	private TrackingGitWorktreeService gitWorktrees = null!;
	private TrackingAgentScheduler scheduler = null!;
	private TaskTransitionOrchestrator transitions = null!;

	public async Task InitializeAsync()
	{
		await connection.OpenAsync();
		dbContext = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
		await dbContext.Database.MigrateAsync();
		boards = new BoardService(dbContext);
		columns = new ColumnService(dbContext);
		tasks = new TaskService(dbContext);
		dependencies = new TaskDependencyService(dbContext);
		gitWorktrees = new TrackingGitWorktreeService();
		scheduler = new TrackingAgentScheduler();
		transitions = new TaskTransitionOrchestrator(dbContext, gitWorktrees, scheduler);
	}

	public async Task DisposeAsync()
	{
		await dbContext.DisposeAsync();
		await connection.DisposeAsync();
	}

	[Fact]
	public async Task MoveTaskAsync_RejectsDisallowedTransition()
	{
		var setup = await CreateWorkflowAsync();
		var task = await tasks.CreateAsync(setup.Board.Id, setup.Backlog.Id, "Task", "");

		await Assert.ThrowsAsync<InvalidOperationException>(() => transitions.MoveTaskAsync(task.Id, setup.Done.Id));
		Assert.Equal(setup.Backlog.Id, (await tasks.GetByIdAsync(task.Id))!.ColumnId);
		Assert.Empty(gitWorktrees.Transitions);
	}

	[Fact]
	public async Task MoveTaskAsync_RequiresDependenciesToBeCompletedBeforeLeavingBacklog()
	{
		var setup = await CreateWorkflowAsync();
		var prerequisite = await tasks.CreateAsync(setup.Board.Id, setup.Backlog.Id, "Prerequisite", "");
		var blocked = await tasks.CreateAsync(setup.Board.Id, setup.Backlog.Id, "Blocked", "");
		await dependencies.AddAsync(blocked.Id, prerequisite.Id);

		await Assert.ThrowsAsync<InvalidOperationException>(() => transitions.MoveTaskAsync(blocked.Id, setup.Working.Id));

		await transitions.MoveTaskAsync(prerequisite.Id, setup.Working.Id);
		await transitions.MoveTaskAsync(prerequisite.Id, setup.Done.Id);
		await transitions.MoveTaskAsync(blocked.Id, setup.Working.Id);

		Assert.Equal(setup.Working.Id, (await tasks.GetByIdAsync(blocked.Id))!.ColumnId);
		Assert.Equal(DomainTaskStatus.InProgress, (await tasks.GetByIdAsync(blocked.Id))!.Status);
	}

	[Fact]
	public async Task DependencyChanges_AreRejectedAfterTaskLeavesBacklog()
	{
		var setup = await CreateWorkflowAsync();
		var task = await tasks.CreateAsync(setup.Board.Id, setup.Backlog.Id, "Task", "");
		var dependency = await tasks.CreateAsync(setup.Board.Id, setup.Backlog.Id, "Dependency", "");
		await dependencies.AddAsync(task.Id, dependency.Id);

		await transitions.MoveTaskAsync(dependency.Id, setup.Working.Id);
		await transitions.MoveTaskAsync(dependency.Id, setup.Done.Id);
		await transitions.MoveTaskAsync(task.Id, setup.Working.Id);

		await Assert.ThrowsAsync<InvalidOperationException>(() => dependencies.RemoveAsync(task.Id, dependency.Id));
		await Assert.ThrowsAsync<InvalidOperationException>(() => dependencies.AddAsync(task.Id, Guid.NewGuid()));
	}

	[Fact]
	public async Task DeleteAsync_RejectsTaskWithRunningAgent()
	{
		var setup = await CreateWorkflowAsync();
		var task = await tasks.CreateAsync(setup.Board.Id, setup.Backlog.Id, "Task", "");
		dbContext.AgentRuns.Add(new AgentRun { TaskId = task.Id, ColumnId = setup.Backlog.Id, Status = AgentRunStatus.Working });
		await dbContext.SaveChangesAsync();

		await Assert.ThrowsAsync<InvalidOperationException>(() => tasks.DeleteAsync(task.Id));
	}

	private async Task<Workflow> CreateWorkflowAsync()
	{
		var board = await boards.CreateAsync("Harness", "/repos/harness", 1);
		var boardColumns = await columns.GetForBoardAsync(board.Id);
		var backlog = boardColumns.Single(column => column.IsBacklog);
		var done = boardColumns.Single(column => column.IsTerminal);
		await columns.UpdateAsync(done.Id, "Done", 2, false, true, null);
		var working = await columns.CreateAsync(board.Id, "Working", 1);
		await columns.AddAllowedTransitionAsync(backlog.Id, working.Id);
		await columns.AddAllowedTransitionAsync(working.Id, done.Id);
		return new Workflow(board, backlog, working, done);
	}

	private sealed record Workflow(Board Board, Column Backlog, Column Working, Column Done);

	private sealed class TrackingGitWorktreeService : IGitWorktreeService
	{
		public List<Guid> Transitions { get; } = [];

		public Task HandleTransitionAsync(TaskItem task, Column sourceColumn, Column targetColumn, CancellationToken cancellationToken = default)
		{
			Transitions.Add(task.Id);
			return Task.CompletedTask;
		}
	}

	private sealed class TrackingAgentScheduler : IAgentScheduler
	{
		public Task RequestStartAsync(TaskItem task, Column column, CancellationToken cancellationToken = default) => Task.CompletedTask;
	}
}