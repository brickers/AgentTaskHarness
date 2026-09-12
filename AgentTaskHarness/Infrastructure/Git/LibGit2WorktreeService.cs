using System.Diagnostics;
using AgentTaskHarness.Application.Abstractions;
using AgentTaskHarness.Domain.Entities;
using AgentTaskHarness.Domain.Enums;
using AgentTaskHarness.Infrastructure.Persistence;
using LibGit2Sharp;
using Microsoft.EntityFrameworkCore;

namespace AgentTaskHarness.Infrastructure.Git;

public class LibGit2WorktreeService(AppDbContext dbContext) : IGitWorktreeService
{
	public async Task HandleFeatureTransitionAsync(Feature feature, WorkflowColumn sourceColumn, WorkflowColumn targetColumn, CancellationToken cancellationToken = default)
	{
		var board = feature.Board ?? await GetBoardAsync(feature.BoardId, cancellationToken);

		if (targetColumn == WorkflowColumn.Build && (sourceColumn == WorkflowColumn.Backlog || sourceColumn == WorkflowColumn.Ready))
		{
			await EnsureFeatureWorktreeAsync(feature, board, cancellationToken);
		}

		if (!string.IsNullOrWhiteSpace(feature.WorktreePath) && Directory.Exists(feature.WorktreePath))
		{
			CommitPendingChanges(feature.WorktreePath, $"Move feature {feature.Id:N} to {targetColumn}");
		}

		if (targetColumn == WorkflowColumn.Done)
		{
			await MergeFeatureAndRemoveWorktreeAsync(feature, board, cancellationToken);
		}
	}

	public async Task HandleStepTransitionAsync(Step step, WorkflowColumn sourceColumn, WorkflowColumn targetColumn, CancellationToken cancellationToken = default)
	{
		var feature = step.Feature ?? await dbContext.Features
			.Include(f => f.Board)
			.SingleOrDefaultAsync(f => f.Id == step.FeatureId, cancellationToken)
			?? throw new KeyNotFoundException($"Feature '{step.FeatureId}' was not found.");
		var board = feature.Board ?? await GetBoardAsync(feature.BoardId, cancellationToken);

		if (targetColumn == WorkflowColumn.Build)
		{
			await EnsureStepWorktreeAsync(step, feature, board, cancellationToken);
		}

		if (!string.IsNullOrWhiteSpace(step.WorktreePath) && Directory.Exists(step.WorktreePath))
		{
			CommitPendingChanges(step.WorktreePath, $"Move step {step.Id:N} to {targetColumn}");
		}

		if (targetColumn == WorkflowColumn.Done)
		{
			await MergeStepAndRemoveWorktreeAsync(step, feature, board, cancellationToken);
		}
	}

	public async Task DiscardFeatureWorktreeAsync(Feature feature, CancellationToken cancellationToken = default)
	{
		var steps = await dbContext.Steps.Where(s => s.FeatureId == feature.Id).ToListAsync(cancellationToken);
		foreach (var s in steps)
		{
			if (!string.IsNullOrWhiteSpace(s.WorktreePath) || !string.IsNullOrWhiteSpace(s.BranchName))
			{
				await DiscardStepWorktreeAsync(s, cancellationToken);
			}
		}

		if (string.IsNullOrWhiteSpace(feature.BranchName) && string.IsNullOrWhiteSpace(feature.WorktreePath))
		{
			return;
		}

		var board = feature.Board ?? await GetBoardAsync(feature.BoardId, cancellationToken);
		await RemoveWorktreeAndBranchAsync(board.RepoPath, feature.WorktreePath, feature.BranchName, cancellationToken);
		feature.BranchName = null;
		feature.WorktreePath = null;
		feature.MergeConflictPending = false;
	}

	public async Task DiscardStepWorktreeAsync(Step step, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(step.BranchName) && string.IsNullOrWhiteSpace(step.WorktreePath))
		{
			return;
		}

		var feature = step.Feature ?? await dbContext.Features
			.Include(f => f.Board)
			.SingleOrDefaultAsync(f => f.Id == step.FeatureId, cancellationToken)
			?? throw new KeyNotFoundException($"Feature '{step.FeatureId}' was not found.");
		var board = feature.Board ?? await GetBoardAsync(feature.BoardId, cancellationToken);

		await RemoveWorktreeAndBranchAsync(board.RepoPath, step.WorktreePath, step.BranchName, cancellationToken);
		step.BranchName = null;
		step.WorktreePath = null;
		step.MergeConflictPending = false;
	}

	public async Task DiscardFeatureUncommittedChangesAsync(Feature feature, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(feature.WorktreePath) || !Directory.Exists(feature.WorktreePath))
		{
			throw new InvalidOperationException("This feature does not have an active worktree.");
		}

		using (var repository = OpenRepository(feature.WorktreePath))
		{
			repository.Reset(ResetMode.Hard, repository.Head.Tip);
		}
		await RunGitAsync(feature.WorktreePath, ["clean", "-fd"], cancellationToken);
	}

	public async Task DiscardStepUncommittedChangesAsync(Step step, CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(step.WorktreePath) || !Directory.Exists(step.WorktreePath))
		{
			throw new InvalidOperationException("This step does not have an active worktree.");
		}

		using (var repository = OpenRepository(step.WorktreePath))
		{
			repository.Reset(ResetMode.Hard, repository.Head.Tip);
		}
		await RunGitAsync(step.WorktreePath, ["clean", "-fd"], cancellationToken);
	}

	public async Task ResumeFeatureMergeAsync(Feature feature, CancellationToken cancellationToken = default)
	{
		var board = feature.Board ?? await GetBoardAsync(feature.BoardId, cancellationToken);

		if (string.IsNullOrWhiteSpace(feature.BranchName))
		{
			throw new InvalidOperationException("The feature branch no longer exists.");
		}

		bool hasConflict = false;
		using (var mainRepo = OpenRepository(board.RepoPath))
		{
			var branch = mainRepo.Branches[feature.BranchName]
				?? throw new InvalidOperationException("The feature branch no longer exists.");

			if (HasConflicts(mainRepo))
			{
				throw new GitMergeConflictException();
			}

			if (mainRepo.RetrieveStatus().IsDirty)
			{
				Commands.Stage(mainRepo, "*");
				mainRepo.Commit($"Resolve merge for feature {feature.Id:N}", Signature(mainRepo), Signature(mainRepo));
			}

			if (!IsMergedIntoHead(mainRepo, branch))
			{
				var mergeResult = mainRepo.Merge(branch, Signature(mainRepo));
				if (mergeResult.Status == MergeStatus.Conflicts)
				{
					hasConflict = true;
				}
			}
		}

		if (hasConflict)
		{
			feature.MergeConflictPending = true;
			throw new GitMergeConflictException();
		}

		await RemoveWorktreeAndBranchAsync(board.RepoPath, feature.WorktreePath, feature.BranchName, cancellationToken);
		feature.BranchName = null;
		feature.WorktreePath = null;
		feature.MergeConflictPending = false;

		var steps = await dbContext.Steps.Where(s => s.FeatureId == feature.Id).ToListAsync(cancellationToken);
		foreach (var s in steps)
		{
			if (!string.IsNullOrWhiteSpace(s.WorktreePath) || !string.IsNullOrWhiteSpace(s.BranchName))
			{
				await DiscardStepWorktreeAsync(s, cancellationToken);
			}
		}
	}

	public async Task ResumeStepMergeAsync(Step step, CancellationToken cancellationToken = default)
	{
		var feature = step.Feature ?? await dbContext.Features
			.Include(f => f.Board)
			.SingleOrDefaultAsync(f => f.Id == step.FeatureId, cancellationToken)
			?? throw new KeyNotFoundException($"Feature '{step.FeatureId}' was not found.");
		var board = feature.Board ?? await GetBoardAsync(feature.BoardId, cancellationToken);

		if (string.IsNullOrWhiteSpace(feature.WorktreePath) || !Directory.Exists(feature.WorktreePath))
		{
			throw new InvalidOperationException("The feature worktree does not exist.");
		}
		if (string.IsNullOrWhiteSpace(step.BranchName))
		{
			throw new InvalidOperationException("The step branch no longer exists.");
		}

		bool hasConflict = false;
		using (var featureRepo = OpenRepository(feature.WorktreePath))
		{
			var branch = featureRepo.Branches[step.BranchName]
				?? throw new InvalidOperationException("The step branch no longer exists.");

			if (HasConflicts(featureRepo))
			{
				throw new GitMergeConflictException();
			}

			if (featureRepo.RetrieveStatus().IsDirty)
			{
				Commands.Stage(featureRepo, "*");
				featureRepo.Commit($"Resolve merge for step {step.Id:N}", Signature(featureRepo), Signature(featureRepo));
			}

			if (!IsMergedIntoHead(featureRepo, branch))
			{
				var mergeResult = featureRepo.Merge(branch, Signature(featureRepo));
				if (mergeResult.Status == MergeStatus.Conflicts)
				{
					hasConflict = true;
				}
			}
		}

		if (hasConflict)
		{
			step.MergeConflictPending = true;
			throw new GitMergeConflictException();
		}

		await RemoveWorktreeAndBranchAsync(board.RepoPath, step.WorktreePath, step.BranchName, cancellationToken);
		step.BranchName = null;
		step.WorktreePath = null;
		step.MergeConflictPending = false;
	}

	private async Task EnsureFeatureWorktreeAsync(Feature feature, Board board, CancellationToken cancellationToken)
	{
		var repoRoot = Path.GetFullPath(board.RepoPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		var branchName = feature.BranchName ?? $"feature/{feature.Id:N}";
		var worktreePath = feature.WorktreePath ?? Path.Combine($"{repoRoot}.worktrees", $"feature-{feature.Id:N}");

		if (!Directory.Exists(worktreePath))
		{
			Directory.CreateDirectory(Path.GetDirectoryName(worktreePath)!);
			using var repository = OpenRepository(board.RepoPath);
			if (repository.Branches[branchName] is null)
			{
				var baseBranch = repository.Branches["main"] is not null ? "main" : repository.Head.FriendlyName;
				await RunGitAsync(board.RepoPath, ["worktree", "add", "-b", branchName, worktreePath, baseBranch], cancellationToken);
			}
			else
			{
				await RunGitAsync(board.RepoPath, ["worktree", "add", worktreePath, branchName], cancellationToken);
			}
		}

		feature.BranchName = branchName;
		feature.WorktreePath = worktreePath;
	}

	private async Task EnsureStepWorktreeAsync(Step step, Feature feature, Board board, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(feature.BranchName) || string.IsNullOrWhiteSpace(feature.WorktreePath) || !Directory.Exists(feature.WorktreePath))
		{
			await EnsureFeatureWorktreeAsync(feature, board, cancellationToken);
		}

		var repoRoot = Path.GetFullPath(board.RepoPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		var branchName = step.BranchName ?? $"step/{step.Id:N}";
		var worktreePath = step.WorktreePath ?? Path.Combine($"{repoRoot}.worktrees", $"step-{step.Id:N}");

		if (!Directory.Exists(worktreePath))
		{
			Directory.CreateDirectory(Path.GetDirectoryName(worktreePath)!);
			using var repository = OpenRepository(board.RepoPath);
			if (repository.Branches[branchName] is null)
			{
				await RunGitAsync(board.RepoPath, ["worktree", "add", "-b", branchName, worktreePath, feature.BranchName!], cancellationToken);
			}
			else
			{
				await RunGitAsync(board.RepoPath, ["worktree", "add", worktreePath, branchName], cancellationToken);
			}
		}

		step.BranchName = branchName;
		step.WorktreePath = worktreePath;
	}

	private async Task MergeStepAndRemoveWorktreeAsync(Step step, Feature feature, Board board, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(step.BranchName))
		{
			return;
		}

		if (string.IsNullOrWhiteSpace(feature.WorktreePath) || !Directory.Exists(feature.WorktreePath))
		{
			await EnsureFeatureWorktreeAsync(feature, board, cancellationToken);
		}

		if (!string.IsNullOrWhiteSpace(step.WorktreePath) && Directory.Exists(step.WorktreePath))
		{
			CommitPendingChanges(step.WorktreePath, $"Auto-commit before merging step {step.Id:N}");
		}

		bool hasConflict = false;
		using (var featureRepo = OpenRepository(feature.WorktreePath!))
		{
			var branch = featureRepo.Branches[step.BranchName]
				?? throw new InvalidOperationException("The step branch no longer exists.");

			if (featureRepo.RetrieveStatus().IsDirty)
			{
				Commands.Stage(featureRepo, "*");
				featureRepo.Commit($"Auto-commit in feature worktree before merging step {step.Id:N}", Signature(featureRepo), Signature(featureRepo));
			}

			var mergeResult = featureRepo.Merge(branch, Signature(featureRepo));
			if (mergeResult.Status == MergeStatus.Conflicts)
			{
				hasConflict = true;
			}
		}

		if (hasConflict)
		{
			step.MergeConflictPending = true;
			throw new GitMergeConflictException();
		}

		await RemoveWorktreeAndBranchAsync(board.RepoPath, step.WorktreePath, step.BranchName, cancellationToken);
		step.BranchName = null;
		step.WorktreePath = null;
		step.MergeConflictPending = false;
	}

	private async Task MergeFeatureAndRemoveWorktreeAsync(Feature feature, Board board, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(feature.BranchName))
		{
			return;
		}

		if (!string.IsNullOrWhiteSpace(feature.WorktreePath) && Directory.Exists(feature.WorktreePath))
		{
			CommitPendingChanges(feature.WorktreePath, $"Auto-commit before merging feature {feature.Id:N}");
		}

		bool hasConflict = false;
		using (var mainRepo = OpenRepository(board.RepoPath))
		{
			var branch = mainRepo.Branches[feature.BranchName]
				?? throw new InvalidOperationException("The feature branch no longer exists.");

			if (mainRepo.RetrieveStatus().IsDirty)
			{
				Commands.Stage(mainRepo, "*");
				mainRepo.Commit($"Auto-commit in main repo before merging feature {feature.Id:N}", Signature(mainRepo), Signature(mainRepo));
			}

			var mergeResult = mainRepo.Merge(branch, Signature(mainRepo));
			if (mergeResult.Status == MergeStatus.Conflicts)
			{
				hasConflict = true;
			}
		}

		if (hasConflict)
		{
			feature.MergeConflictPending = true;
			throw new GitMergeConflictException();
		}

		await RemoveWorktreeAndBranchAsync(board.RepoPath, feature.WorktreePath, feature.BranchName, cancellationToken);
		feature.BranchName = null;
		feature.WorktreePath = null;
		feature.MergeConflictPending = false;

		var steps = await dbContext.Steps.Where(s => s.FeatureId == feature.Id).ToListAsync(cancellationToken);
		foreach (var s in steps)
		{
			if (!string.IsNullOrWhiteSpace(s.WorktreePath) || !string.IsNullOrWhiteSpace(s.BranchName))
			{
				await DiscardStepWorktreeAsync(s, cancellationToken);
			}
		}
	}

	private static async Task RemoveWorktreeAndBranchAsync(string mainRepoPath, string? worktreePath, string? branchName, CancellationToken cancellationToken)
	{
		if (!string.IsNullOrWhiteSpace(worktreePath) && Directory.Exists(worktreePath))
		{
			try
			{
				await RunGitAsync(mainRepoPath, ["worktree", "remove", "--force", worktreePath], cancellationToken);
			}
			catch
			{
				// Ignore if git CLI worktree remove fails
			}
			if (Directory.Exists(worktreePath))
			{
				try { Directory.Delete(worktreePath, true); } catch { /* ignore */ }
			}
		}

		if (!string.IsNullOrWhiteSpace(branchName))
		{
			using var repository = OpenRepository(mainRepoPath);
			var branch = repository.Branches[branchName];
			if (branch is not null)
			{
				repository.Branches.Remove(branch.FriendlyName, true);
			}
		}
	}

	private static void CommitPendingChanges(string? worktreePath, string message)
	{
		if (string.IsNullOrWhiteSpace(worktreePath) || !Directory.Exists(worktreePath))
		{
			return;
		}

		using var repository = OpenRepository(worktreePath);
		if (!repository.RetrieveStatus().IsDirty)
		{
			return;
		}

		Commands.Stage(repository, "*");
		repository.Commit(message, Signature(repository), Signature(repository));
	}

	private static Repository OpenRepository(string path)
	{
		var repositoryPath = Repository.Discover(path)
			?? throw new InvalidOperationException($"'{path}' is not a Git repository.");
		return new Repository(repositoryPath);
	}

	private static bool HasConflicts(Repository repository) =>
		repository.Index.Conflicts.Any();

	private static bool IsMergedIntoHead(Repository repository, Branch branch) =>
		branch.Tip is not null && repository.Commits.QueryBy(new CommitFilter { IncludeReachableFrom = repository.Head.Tip }).Any(commit => commit.Sha == branch.Tip.Sha);

	private static Signature Signature(Repository repository)
	{
		var name = repository.Config.Get<string>("user.name")?.Value ?? "Agent Task Harness";
		var email = repository.Config.Get<string>("user.email")?.Value ?? "agent-task-harness@local";
		return new Signature(name, email, DateTimeOffset.UtcNow);
	}

	private async Task<Board> GetBoardAsync(Guid boardId, CancellationToken cancellationToken)
	{
		return await dbContext.Boards.SingleOrDefaultAsync(b => b.Id == boardId, cancellationToken)
			?? throw new KeyNotFoundException($"Board '{boardId}' was not found.");
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
