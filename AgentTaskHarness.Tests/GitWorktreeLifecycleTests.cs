using AgentTaskHarness.Application.Abstractions;
using AgentTaskHarness.Application.Agents;
using AgentTaskHarness.Application.Boards;
using AgentTaskHarness.Application.Columns;
using AgentTaskHarness.Application.Tasks;
using AgentTaskHarness.Domain.Entities;
using AgentTaskHarness.Infrastructure.Agents;
using AgentTaskHarness.Infrastructure.Git;
using AgentTaskHarness.Infrastructure.Persistence;
using LibGit2Sharp;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using Xunit;

namespace AgentTaskHarness.Tests;

public class GitWorktreeLifecycleTests : IAsyncLifetime
{
	private readonly SqliteConnection connection = new("Data Source=:memory:");
	private readonly string workspacePath = Path.Combine(Path.GetTempPath(), $"agent-task-harness-tests-{Guid.NewGuid():N}");
	private AppDbContext dbContext = null!;
	private BoardService boards = null!;
	private ColumnService columns = null!;
	private TaskService tasks = null!;
	private IGitWorktreeService gitWorktrees = null!;

	public async Task InitializeAsync()
	{
		await connection.OpenAsync();
		dbContext = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
		await dbContext.Database.MigrateAsync();
		boards = new BoardService(dbContext);
		columns = new ColumnService(dbContext);
		tasks = new TaskService(dbContext);
		gitWorktrees = new LibGit2WorktreeService(dbContext);

		Directory.CreateDirectory(workspacePath);
		await InitializeGitRepositoryAsync(workspacePath);
		File.WriteAllText(Path.Combine(workspacePath, "README.md"), "initial");
		File.WriteAllText(Path.Combine(workspacePath, "shared.txt"), "initial");
		using var repository = new Repository(workspacePath);
		Commands.Stage(repository, "*");
		repository.Commit("Initial commit", Signature(), Signature());
	}

	public async Task DisposeAsync()
	{
		await dbContext.DisposeAsync();
		await connection.DisposeAsync();
		if (Directory.Exists(workspacePath))
		{
			Directory.Delete(workspacePath, true);
		}
	}

	[Fact]
	public async Task HandleTransitionAsync_CreatesWorktreeCommitsChangesAndMergesOnDone()
	{
		var board = await boards.CreateAsync("Harness", workspacePath, 1);
		var boardColumns = await columns.GetForBoardAsync(board.Id);
		var backlog = boardColumns.Single(column => column.IsBacklog);
		var done = boardColumns.Single(column => column.IsTerminal);
		await columns.UpdateAsync(done.Id, "Done", 2, false, true, null);
		var working = await columns.CreateAsync(board.Id, "Working", 1);
		var task = await tasks.CreateAsync(board.Id, backlog.Id, "Task", "");

		await gitWorktrees.HandleTransitionAsync(task, backlog, working);

		Assert.Equal($"task/{task.Id:N}", task.BranchName);
		Assert.NotNull(task.WorktreePath);
		Assert.True(Directory.Exists(task.WorktreePath));
		File.WriteAllText(Path.Combine(task.WorktreePath!, "task.txt"), "task work");

		await gitWorktrees.HandleTransitionAsync(task, working, done);

		Assert.Null(task.BranchName);
		Assert.Null(task.WorktreePath);
		Assert.True(File.Exists(Path.Combine(workspacePath, "task.txt")));
		Assert.False(Directory.Exists(Path.Combine($"{workspacePath}.worktrees", task.Id.ToString("N"))));
	}

	[Fact]
	public async Task Transitions_PauseOrDiscardWorktreeWhenReturningToBacklog()
	{
		var workflow = await CreateWorkflowAsync();
		var transitionOrchestrator = new TaskTransitionOrchestrator(dbContext, gitWorktrees, new NoOpAgentScheduler());
		var task = await tasks.CreateAsync(workflow.Board.Id, workflow.Backlog.Id, "Task", "");

		await transitionOrchestrator.MoveTaskAsync(task.Id, workflow.Working.Id);
		var startedTask = (await tasks.GetByIdAsync(task.Id))!;
		var originalBranch = startedTask.BranchName;
		var originalWorktree = startedTask.WorktreePath;

		await transitionOrchestrator.MoveTaskAsync(task.Id, workflow.Backlog.Id);
		var pausedTask = (await tasks.GetByIdAsync(task.Id))!;
		Assert.Equal(originalBranch, pausedTask.BranchName);
		Assert.Equal(originalWorktree, pausedTask.WorktreePath);

		await transitionOrchestrator.MoveTaskAsync(task.Id, workflow.Working.Id);
		var resumedTask = (await tasks.GetByIdAsync(task.Id))!;
		Assert.Equal(originalWorktree, resumedTask.WorktreePath);

		await transitionOrchestrator.MoveTaskAndDiscardWorktreeAsync(task.Id, workflow.Backlog.Id);
		var discardedTask = (await tasks.GetByIdAsync(task.Id))!;
		Assert.Null(discardedTask.BranchName);
		Assert.Null(discardedTask.WorktreePath);
		Assert.False(Directory.Exists(originalWorktree));
	}

	[Fact]
	public async Task ResumeMergeAsync_CompletesTaskAfterManualConflictResolution()
	{
		var workflow = await CreateWorkflowAsync();
		var transitionOrchestrator = new TaskTransitionOrchestrator(dbContext, gitWorktrees, new NoOpAgentScheduler());
		var task = await tasks.CreateAsync(workflow.Board.Id, workflow.Backlog.Id, "Task", "");
		await transitionOrchestrator.MoveTaskAsync(task.Id, workflow.Working.Id);
		var startedTask = (await tasks.GetByIdAsync(task.Id))!;

		File.WriteAllText(Path.Combine(startedTask.WorktreePath!, "shared.txt"), "task change");
		using (var repository = new Repository(workspacePath))
		{
			File.WriteAllText(Path.Combine(workspacePath, "shared.txt"), "main change");
			Commands.Stage(repository, "shared.txt");
			repository.Commit("Main change", Signature(), Signature());
		}

		await Assert.ThrowsAsync<GitMergeConflictException>(() => transitionOrchestrator.MoveTaskAsync(task.Id, workflow.Done.Id));
		dbContext.ChangeTracker.Clear();
		var conflictedTask = (await tasks.GetByIdAsync(task.Id))!;
		Assert.True(conflictedTask.MergeConflictPending);
		Assert.Equal(workflow.Working.Id, conflictedTask.ColumnId);

		using (var repository = new Repository(workspacePath))
		{
			File.WriteAllText(Path.Combine(workspacePath, "shared.txt"), "resolved");
			Commands.Stage(repository, "shared.txt");
			repository.Commit("Resolve conflict", Signature(), Signature());
		}

		await transitionOrchestrator.ResumeMergeAsync(task.Id);
		var completedTask = (await tasks.GetByIdAsync(task.Id))!;
		Assert.Equal(workflow.Done.Id, completedTask.ColumnId);
		Assert.False(completedTask.MergeConflictPending);
		Assert.Equal(AgentTaskHarness.Domain.Enums.TaskStatus.Completed, completedTask.Status);
	}

	private async Task<Workflow> CreateWorkflowAsync()
	{
		var board = await boards.CreateAsync("Harness", workspacePath, 1);
		var boardColumns = await columns.GetForBoardAsync(board.Id);
		var backlog = boardColumns.Single(column => column.IsBacklog);
		var done = boardColumns.Single(column => column.IsTerminal);
		await columns.UpdateAsync(done.Id, "Done", 2, false, true, null);
		var working = await columns.CreateAsync(board.Id, "Working", 1);
		await columns.AddAllowedTransitionAsync(backlog.Id, working.Id);
		await columns.AddAllowedTransitionAsync(working.Id, backlog.Id);
		await columns.AddAllowedTransitionAsync(working.Id, done.Id);
		return new Workflow(board, backlog, working, done);
	}

	private static Signature Signature() => new("Test User", "test@example.com", DateTimeOffset.UtcNow);

	private sealed record Workflow(Board Board, Column Backlog, Column Working, Column Done);

	private static async Task InitializeGitRepositoryAsync(string path)
	{
		var startInfo = new ProcessStartInfo("git") { RedirectStandardError = true, UseShellExecute = false };
		startInfo.ArgumentList.Add("init");
		startInfo.ArgumentList.Add("--initial-branch=main");
		startInfo.ArgumentList.Add(path);
		using var process = Process.Start(startInfo)!;
		await process.WaitForExitAsync();
		Assert.True(process.ExitCode == 0, await process.StandardError.ReadToEndAsync());
	}
}