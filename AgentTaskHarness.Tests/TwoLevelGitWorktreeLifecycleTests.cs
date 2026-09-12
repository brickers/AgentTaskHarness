using System.Diagnostics;
using AgentTaskHarness.Application.Abstractions;
using AgentTaskHarness.Application.Boards;
using AgentTaskHarness.Application.Features;
using AgentTaskHarness.Application.Reviews;
using AgentTaskHarness.Application.Steps;
using AgentTaskHarness.Application.Workflow;
using AgentTaskHarness.Domain.Entities;
using AgentTaskHarness.Domain.Enums;
using AgentTaskHarness.Infrastructure.Git;
using AgentTaskHarness.Infrastructure.Persistence;
using LibGit2Sharp;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentTaskHarness.Tests;

public class TwoLevelGitWorktreeLifecycleTests : IAsyncLifetime
{
	private readonly SqliteConnection connection = new("Data Source=:memory:");
	private readonly string workspacePath = Path.Combine(Path.GetTempPath(), $"agent-task-harness-tests-{Guid.NewGuid():N}");
	private AppDbContext dbContext = null!;
	private BoardService boards = null!;
	private FeatureService features = null!;
	private StepService steps = null!;
	private WorkflowTransitionRules rules = null!;
	private FeatureDependencyService featureDeps = null!;
	private StepDependencyService stepDeps = null!;
	private ReviewOutcomeService reviewOutcomeService = null!;
	private LibGit2WorktreeService gitWorktrees = null!;
	private FeatureTransitionOrchestrator featureOrchestrator = null!;
	private StepTransitionOrchestrator stepOrchestrator = null!;

	public async Task InitializeAsync()
	{
		await connection.OpenAsync();
		dbContext = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
		await dbContext.Database.MigrateAsync();

		boards = new BoardService(dbContext);
		features = new FeatureService(dbContext);
		steps = new StepService(dbContext);
		rules = new WorkflowTransitionRules();
		featureDeps = new FeatureDependencyService(dbContext);
		stepDeps = new StepDependencyService(dbContext);
		reviewOutcomeService = new ReviewOutcomeService(dbContext);
		gitWorktrees = new LibGit2WorktreeService(dbContext);
		featureOrchestrator = new FeatureTransitionOrchestrator(dbContext, rules, featureDeps, reviewOutcomeService, gitWorktrees);
		stepOrchestrator = new StepTransitionOrchestrator(dbContext, rules, stepDeps, featureOrchestrator, reviewOutcomeService, gitWorktrees);

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

		var worktreesParent = $"{workspacePath}.worktrees";
		if (Directory.Exists(worktreesParent))
		{
			try { Directory.Delete(worktreesParent, true); } catch { /* ignore */ }
		}

		if (Directory.Exists(workspacePath))
		{
			try { Directory.Delete(workspacePath, true); } catch { /* ignore */ }
		}
	}

	[Fact]
	public async Task TwoLevelWorktree_CreationCommitsAndMergeOnDone()
	{
		var board = await boards.CreateAsync("Harness", workspacePath, 1);
		var feat = await features.CreateAsync(board.Id, "Feature 1");
		var step = await steps.CreateAsync(feat.Id, "Step 1");

		// Move feature to Ready (batch-moves step to Ready)
		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);

		// Step moves to Build -> Trigger 1 automatically moves Feature to Build first!
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Build);

		var refreshedFeat = (await features.GetByIdAsync(feat.Id))!;
		var refreshedStep = (await steps.GetByIdAsync(step.Id))!;

		Assert.Equal(WorkflowColumn.Build, refreshedFeat.WorkflowColumn);
		Assert.Equal(WorkflowColumn.Build, refreshedStep.WorkflowColumn);

		// Feature worktree & branch created off main
		Assert.Equal($"feature/{feat.Id:N}", refreshedFeat.BranchName);
		Assert.NotNull(refreshedFeat.WorktreePath);
		Assert.True(Directory.Exists(refreshedFeat.WorktreePath));

		// Step worktree & branch created off Feature branch
		Assert.Equal($"step/{step.Id:N}", refreshedStep.BranchName);
		Assert.NotNull(refreshedStep.WorktreePath);
		Assert.True(Directory.Exists(refreshedStep.WorktreePath));

		// Write a file in step worktree
		File.WriteAllText(Path.Combine(refreshedStep.WorktreePath!, "step_output.txt"), "step content");

		// Step advances Build -> AgentReview -> HumanReview -> Done
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.AgentReview);
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.HumanReview);
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Done);

		// Step is Done: merged into Feature branch, Step worktree and branch deleted
		var doneStep = (await steps.GetByIdAsync(step.Id))!;
		Assert.Equal(WorkflowColumn.Done, doneStep.WorkflowColumn);
		Assert.Null(doneStep.BranchName);
		Assert.Null(doneStep.WorktreePath);
		Assert.False(Directory.Exists(refreshedStep.WorktreePath!));

		// Check that step_output.txt now exists in the Feature worktree!
		Assert.True(File.Exists(Path.Combine(refreshedFeat.WorktreePath!, "step_output.txt")));

		// Trigger 2: all steps Done -> Feature automatically advanced to AgentReview
		refreshedFeat = (await features.GetByIdAsync(feat.Id))!;
		Assert.Equal(WorkflowColumn.AgentReview, refreshedFeat.WorkflowColumn);

		// In Feature worktree, add another file
		File.WriteAllText(Path.Combine(refreshedFeat.WorktreePath!, "feature_output.txt"), "feature content");

		// Feature advances AgentReview -> HumanReview -> Done
		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.HumanReview);
		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Done);

		// Feature is Done: merged into main, Feature worktree and branch deleted
		var doneFeat = (await features.GetByIdAsync(feat.Id))!;
		Assert.Equal(WorkflowColumn.Done, doneFeat.WorkflowColumn);
		Assert.Null(doneFeat.BranchName);
		Assert.Null(doneFeat.WorktreePath);
		Assert.False(Directory.Exists(refreshedFeat.WorktreePath!));

		// Check that both files now exist in the main repository!
		Assert.True(File.Exists(Path.Combine(workspacePath, "step_output.txt")));
		Assert.True(File.Exists(Path.Combine(workspacePath, "feature_output.txt")));
	}

	[Fact]
	public async Task Transitions_PauseOrDiscardWorktreeWhenReturningToBacklog()
	{
		var board = await boards.CreateAsync("Harness", workspacePath, 1);
		var feat = await features.CreateAsync(board.Id, "Feature 1");
		var step = await steps.CreateAsync(feat.Id, "Step 1");

		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Build);

		var initialStep = (await steps.GetByIdAsync(step.Id))!;
		var initialFeature = (await features.GetByIdAsync(feat.Id))!;
		var stepWorktree = initialStep.WorktreePath!;
		var featWorktree = initialFeature.WorktreePath!;

		Assert.True(Directory.Exists(stepWorktree));
		Assert.True(Directory.Exists(featWorktree));

		// Move Step to Backlog (pause): worktree & branch preserved
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Backlog);
		var pausedStep = (await steps.GetByIdAsync(step.Id))!;
		Assert.Equal(initialStep.BranchName, pausedStep.BranchName);
		Assert.Equal(stepWorktree, pausedStep.WorktreePath);
		Assert.True(Directory.Exists(stepWorktree));

		// Move Step Backlog -> Ready -> Build: worktree reused
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Ready);
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Build);
		var resumedStep = (await steps.GetByIdAsync(step.Id))!;
		Assert.Equal(stepWorktree, resumedStep.WorktreePath);

		// Move Step to Backlog (discard): worktree & branch removed
		await stepOrchestrator.MoveAndDiscardWorktreeAsync(step.Id, WorkflowColumn.Backlog);
		var discardedStep = (await steps.GetByIdAsync(step.Id))!;
		Assert.Null(discardedStep.BranchName);
		Assert.Null(discardedStep.WorktreePath);
		Assert.False(Directory.Exists(stepWorktree));

		// Move Feature to Backlog (discard): worktree & branch removed
		await featureOrchestrator.MoveAndDiscardWorktreeAsync(feat.Id, WorkflowColumn.Backlog);
		var discardedFeature = (await features.GetByIdAsync(feat.Id))!;
		Assert.Null(discardedFeature.BranchName);
		Assert.Null(discardedFeature.WorktreePath);
		Assert.False(Directory.Exists(featWorktree));
	}

	[Fact]
	public async Task DiscardUncommittedChanges_ResetsTrackedAndCleansUntracked()
	{
		var board = await boards.CreateAsync("Harness", workspacePath, 1);
		var feat = await features.CreateAsync(board.Id, "Feature 1");
		var step = await steps.CreateAsync(feat.Id, "Step 1");

		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Build);

		var currentStep = (await steps.GetByIdAsync(step.Id))!;
		var currentFeat = (await features.GetByIdAsync(feat.Id))!;

		// Dirty changes in step worktree
		File.WriteAllText(Path.Combine(currentStep.WorktreePath!, "shared.txt"), "dirty step");
		File.WriteAllText(Path.Combine(currentStep.WorktreePath!, "untracked.txt"), "untracked step");

		// Discard uncommitted changes in step
		await stepOrchestrator.DiscardUncommittedChangesAsync(step.Id);
		Assert.Equal("initial", File.ReadAllText(Path.Combine(currentStep.WorktreePath!, "shared.txt")));
		Assert.False(File.Exists(Path.Combine(currentStep.WorktreePath!, "untracked.txt")));

		// Dirty changes in feature worktree
		File.WriteAllText(Path.Combine(currentFeat.WorktreePath!, "shared.txt"), "dirty feat");
		File.WriteAllText(Path.Combine(currentFeat.WorktreePath!, "untracked_feat.txt"), "untracked feat");

		// Discard uncommitted changes in feature
		await featureOrchestrator.DiscardUncommittedChangesAsync(feat.Id);
		Assert.Equal("initial", File.ReadAllText(Path.Combine(currentFeat.WorktreePath!, "shared.txt")));
		Assert.False(File.Exists(Path.Combine(currentFeat.WorktreePath!, "untracked_feat.txt")));
	}

	[Fact]
	public async Task StepMergeConflict_SetsConflictPendingAndResumesIndependently()
	{
		var board = await boards.CreateAsync("Harness", workspacePath, 1);
		var feat = await features.CreateAsync(board.Id, "Feature 1");
		var step = await steps.CreateAsync(feat.Id, "Step 1");

		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Build);

		var currentStep = (await steps.GetByIdAsync(step.Id))!;
		var currentFeat = (await features.GetByIdAsync(feat.Id))!;

		// Edit shared.txt in step worktree
		File.WriteAllText(Path.Combine(currentStep.WorktreePath!, "shared.txt"), "step conflicting line");

		// Edit shared.txt in feature worktree and commit
		using (var featRepo = new Repository(currentFeat.WorktreePath!))
		{
			File.WriteAllText(Path.Combine(currentFeat.WorktreePath!, "shared.txt"), "feature conflicting line");
			Commands.Stage(featRepo, "shared.txt");
			featRepo.Commit("Feature conflict line commit", Signature(), Signature());
		}

		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.AgentReview);
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.HumanReview);

		// Moving step to Done triggers merge into Feature branch -> throws GitMergeConflictException
		await Assert.ThrowsAsync<GitMergeConflictException>(() => stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Done));

		var conflictedStep = (await steps.GetByIdAsync(step.Id))!;
		var unconflictedFeat = (await features.GetByIdAsync(feat.Id))!;

		Assert.True(conflictedStep.MergeConflictPending);
		Assert.False(unconflictedFeat.MergeConflictPending);
		Assert.Equal(WorkflowColumn.HumanReview, conflictedStep.WorkflowColumn);

		// Resolve the conflict in Feature worktree
		using (var featRepo = new Repository(currentFeat.WorktreePath!))
		{
			File.WriteAllText(Path.Combine(currentFeat.WorktreePath!, "shared.txt"), "resolved step-feature line");
			Commands.Stage(featRepo, "shared.txt");
			featRepo.Commit("Resolved merge conflict between step and feature", Signature(), Signature());
		}

		// Resume merge
		await stepOrchestrator.ResumeMergeAsync(step.Id);

		var completedStep = (await steps.GetByIdAsync(step.Id))!;
		Assert.Equal(WorkflowColumn.Done, completedStep.WorkflowColumn);
		Assert.False(completedStep.MergeConflictPending);
		Assert.Null(completedStep.BranchName);
		Assert.Null(completedStep.WorktreePath);

		// Trigger 2 also advanced Feature to AgentReview
		var updatedFeat = (await features.GetByIdAsync(feat.Id))!;
		Assert.Equal(WorkflowColumn.AgentReview, updatedFeat.WorkflowColumn);
	}

	[Fact]
	public async Task FeatureMergeConflict_SetsConflictPendingAndResumesIndependently()
	{
		var board = await boards.CreateAsync("Harness", workspacePath, 1);
		var feat = await features.CreateAsync(board.Id, "Feature 1");
		var step = await steps.CreateAsync(feat.Id, "Step 1");

		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Build);
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.AgentReview);
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.HumanReview);
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Done);

		var currentFeat = (await features.GetByIdAsync(feat.Id))!;
		Assert.Equal(WorkflowColumn.AgentReview, currentFeat.WorkflowColumn);

		// In Feature worktree: edit shared.txt
		File.WriteAllText(Path.Combine(currentFeat.WorktreePath!, "shared.txt"), "feature conflicting commit");

		// In Main repo: edit shared.txt and commit
		using (var mainRepo = new Repository(workspacePath))
		{
			File.WriteAllText(Path.Combine(workspacePath, "shared.txt"), "main conflicting commit");
			Commands.Stage(mainRepo, "shared.txt");
			mainRepo.Commit("Main conflicting commit", Signature(), Signature());
		}

		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.HumanReview);

		// Moving feature to Done triggers merge into main -> throws GitMergeConflictException
		await Assert.ThrowsAsync<GitMergeConflictException>(() => featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Done));

		var conflictedFeat = (await features.GetByIdAsync(feat.Id))!;
		Assert.True(conflictedFeat.MergeConflictPending);
		Assert.Equal(WorkflowColumn.HumanReview, conflictedFeat.WorkflowColumn);

		// Resolve conflict in main repo
		using (var mainRepo = new Repository(workspacePath))
		{
			File.WriteAllText(Path.Combine(workspacePath, "shared.txt"), "resolved feature-main line");
			Commands.Stage(mainRepo, "shared.txt");
			mainRepo.Commit("Resolved merge conflict between feature and main", Signature(), Signature());
		}

		// Resume merge
		await featureOrchestrator.ResumeMergeAsync(feat.Id);

		var completedFeat = (await features.GetByIdAsync(feat.Id))!;
		Assert.Equal(WorkflowColumn.Done, completedFeat.WorkflowColumn);
		Assert.False(completedFeat.MergeConflictPending);
		Assert.Null(completedFeat.BranchName);
		Assert.Null(completedFeat.WorktreePath);
		Assert.False(Directory.Exists(currentFeat.WorktreePath!));
	}

	private static Signature Signature() => new("Test User", "test@example.com", DateTimeOffset.UtcNow);

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
