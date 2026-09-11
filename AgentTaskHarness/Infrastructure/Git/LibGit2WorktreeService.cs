using System.Diagnostics;
using AgentTaskHarness.Application.Abstractions;
using AgentTaskHarness.Domain.Entities;
using AgentTaskHarness.Infrastructure.Persistence;
using LibGit2Sharp;
using Microsoft.EntityFrameworkCore;

namespace AgentTaskHarness.Infrastructure.Git;

public class LibGit2WorktreeService(AppDbContext dbContext) : IGitWorktreeService
{
	public async Task HandleTransitionAsync(TaskItem task, Column sourceColumn, Column targetColumn, CancellationToken cancellationToken = default)
	{
		if (sourceColumn.IsBacklog && !targetColumn.IsBacklog)
		{
			await EnsureWorktreeAsync(task, cancellationToken);
		}

		if (!string.IsNullOrWhiteSpace(task.WorktreePath))
		{
			CommitPendingChanges(task.WorktreePath, $"Move task {task.Id:N} to {targetColumn.Name}");
		}

		if (targetColumn.IsTerminal)
		{
			await MergeAndRemoveWorktreeAsync(task, cancellationToken);
		}
	}

	public async Task DiscardWorktreeAsync(TaskItem task, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(task.BranchName) && string.IsNullOrWhiteSpace(task.WorktreePath))
		{
			return;
		}

		var board = await GetBoardAsync(task.BoardId, cancellationToken);
		if (!string.IsNullOrWhiteSpace(task.WorktreePath) && Directory.Exists(task.WorktreePath))
		{
			await RunGitAsync(board.RepoPath, ["worktree", "remove", "--force", task.WorktreePath], cancellationToken);
		}

		using var repository = OpenRepository(board.RepoPath);
		var branch = repository.Branches[task.BranchName];
		if (branch is not null)
		{
			repository.Branches.Remove(branch.FriendlyName, true);
		}
		task.BranchName = null;
		task.WorktreePath = null;
		task.MergeConflictPending = false;
	}

	public async Task DiscardUncommittedChangesAsync(TaskItem task, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(task.WorktreePath) || !Directory.Exists(task.WorktreePath))
		{
			throw new InvalidOperationException("This task does not have an active worktree.");
		}

		using (var repository = OpenRepository(task.WorktreePath))
		{
			repository.Reset(ResetMode.Hard, repository.Head.Tip);
		}
		await RunGitAsync(task.WorktreePath, ["clean", "-fd"], cancellationToken);
	}

	public async Task ResumeMergeAsync(TaskItem task, CancellationToken cancellationToken = default)
	{
		var board = await GetBoardAsync(task.BoardId, cancellationToken);
		using var repository = OpenRepository(board.RepoPath);
		var branch = repository.Branches[task.BranchName]
			?? throw new InvalidOperationException("The task branch no longer exists.");

		if (HasConflicts(repository))
		{
			throw new GitMergeConflictException();
		}
		if (repository.RetrieveStatus().IsDirty)
		{
			Commands.Stage(repository, "*");
			repository.Commit($"Resolve merge for task {task.Id:N}", Signature(repository), Signature(repository));
		}

		if (!IsMergedIntoHead(repository, branch))
		{
			var mergeResult = repository.Merge(branch, Signature(repository));
			if (mergeResult.Status == MergeStatus.Conflicts)
			{
				task.MergeConflictPending = true;
				throw new GitMergeConflictException();
			}
		}

		await RemoveWorktreeAndBranchAsync(repository, board.RepoPath, task, cancellationToken);
	}

	private async Task EnsureWorktreeAsync(TaskItem task, CancellationToken cancellationToken)
	{
		var board = await GetBoardAsync(task.BoardId, cancellationToken);
		using var repository = OpenRepository(board.RepoPath);
		var branchName = task.BranchName ?? $"task/{task.Id:N}";
		var worktreePath = task.WorktreePath ?? Path.Combine($"{Path.GetFullPath(board.RepoPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)}.worktrees", task.Id.ToString("N"));

		if (!Directory.Exists(worktreePath))
		{
			Directory.CreateDirectory(Path.GetDirectoryName(worktreePath)!);
			if (repository.Branches[branchName] is null)
			{
				await RunGitAsync(board.RepoPath, ["worktree", "add", "-b", branchName, worktreePath, "main"], cancellationToken);
			}
			else
			{
				await RunGitAsync(board.RepoPath, ["worktree", "add", worktreePath, branchName], cancellationToken);
			}
		}

		task.BranchName = branchName;
		task.WorktreePath = worktreePath;
	}

	private async Task MergeAndRemoveWorktreeAsync(TaskItem task, CancellationToken cancellationToken)
	{
		var board = await GetBoardAsync(task.BoardId, cancellationToken);
		using var repository = OpenRepository(board.RepoPath);
		var branch = repository.Branches[task.BranchName]
			?? throw new InvalidOperationException("The task branch no longer exists.");
		var mergeResult = repository.Merge(branch, Signature(repository));
		if (mergeResult.Status == MergeStatus.Conflicts)
		{
			task.MergeConflictPending = true;
			throw new GitMergeConflictException();
		}

		await RemoveWorktreeAndBranchAsync(repository, board.RepoPath, task, cancellationToken);
	}

	private static async Task RemoveWorktreeAndBranchAsync(Repository repository, string repoPath, TaskItem task, CancellationToken cancellationToken)
	{
		if (!string.IsNullOrWhiteSpace(task.WorktreePath) && Directory.Exists(task.WorktreePath))
		{
			await RunGitAsync(repoPath, ["worktree", "remove", "--force", task.WorktreePath], cancellationToken);
		}
		var branch = repository.Branches[task.BranchName];
		if (branch is not null)
		{
			repository.Branches.Remove(branch.FriendlyName);
		}
		task.BranchName = null;
		task.WorktreePath = null;
		task.MergeConflictPending = false;
	}

	private async Task<Board> GetBoardAsync(Guid boardId, CancellationToken cancellationToken) =>
		await dbContext.Boards.SingleOrDefaultAsync(board => board.Id == boardId, cancellationToken)
		?? throw new KeyNotFoundException($"Board '{boardId}' was not found.");

	private static Repository OpenRepository(string path)
	{
		var repositoryPath = Repository.Discover(path)
			?? throw new InvalidOperationException($"'{path}' is not a Git repository.");
		return new Repository(repositoryPath);
	}

	private static void CommitPendingChanges(string worktreePath, string message)
	{
		using var repository = OpenRepository(worktreePath);
		if (!repository.RetrieveStatus().IsDirty)
		{
			return;
		}
		Commands.Stage(repository, "*");
		repository.Commit(message, Signature(repository), Signature(repository));
	}

	private static bool HasConflicts(Repository repository) => repository.RetrieveStatus().Any(entry => entry.State.HasFlag(FileStatus.Conflicted));

	private static bool IsMergedIntoHead(Repository repository, Branch branch) =>
		branch.Tip is not null && repository.Commits.QueryBy(new CommitFilter { IncludeReachableFrom = repository.Head.Tip }).Any(commit => commit.Sha == branch.Tip.Sha);

	private static Signature Signature(Repository repository)
	{
		var name = repository.Config.Get<string>("user.name")?.Value ?? "Agent Task Harness";
		var email = repository.Config.Get<string>("user.email")?.Value ?? "agent-task-harness@local";
		return new Signature(name, email, DateTimeOffset.UtcNow);
	}

	private static async Task RunGitAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
	{
		var startInfo = new ProcessStartInfo("git")
		{
			WorkingDirectory = workingDirectory,
			RedirectStandardError = true,
			RedirectStandardOutput = true,
			UseShellExecute = false
		};
		foreach (var argument in arguments)
		{
			startInfo.ArgumentList.Add(argument);
		}

		using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Git could not be started.");
		var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
		var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
		await process.WaitForExitAsync(cancellationToken);
		await Task.WhenAll(standardError, standardOutput);
		if (process.ExitCode != 0)
		{
			throw new InvalidOperationException($"Git {string.Join(' ', arguments)} failed: {await standardError}");
		}
	}
}