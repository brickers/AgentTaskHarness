using AgentTaskHarness.Application.Boards;
using AgentTaskHarness.Application.Features;
using AgentTaskHarness.Application.Reviews;
using AgentTaskHarness.Application.Steps;
using AgentTaskHarness.Application.Workflow;
using AgentTaskHarness.Domain.Enums;
using AgentTaskHarness.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentTaskHarness.Tests;

public class TransitionOrchestratorTests : IAsyncLifetime
{
	private readonly SqliteConnection connection = new("Data Source=:memory:");
	private AppDbContext dbContext = null!;
	private BoardService boards = null!;
	private FeatureService features = null!;
	private StepService steps = null!;
	private WorkflowTransitionRules rules = null!;
	private FeatureDependencyService featureDeps = null!;
	private StepDependencyService stepDeps = null!;
	private ReviewOutcomeService reviewOutcomeService = null!;
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
		featureOrchestrator = new FeatureTransitionOrchestrator(dbContext, rules, featureDeps, reviewOutcomeService);
		stepOrchestrator = new StepTransitionOrchestrator(dbContext, rules, stepDeps, featureOrchestrator, reviewOutcomeService);
	}

	public async Task DisposeAsync()
	{
		await dbContext.DisposeAsync();
		await connection.DisposeAsync();
	}

	[Fact]
	public async Task Feature_MoveAsync_RejectsDisallowedTransition()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var feat = await features.CreateAsync(board.Id, "Feat 1");

		var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Done));

		Assert.Equal("Moving from 'Backlog' to 'Done' is not allowed.", ex.Message);
		Assert.Equal(WorkflowColumn.Backlog, (await features.GetByIdAsync(feat.Id))!.WorkflowColumn);
	}

	[Fact]
	public async Task Feature_MoveAsync_RejectsMoveFromDone()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var feat = await features.CreateAsync(board.Id, "Feat 1");
		feat.WorkflowColumn = WorkflowColumn.Done;
		await dbContext.SaveChangesAsync();

		var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Backlog));

		Assert.Equal("Completed features are terminal and cannot be moved.", ex.Message);
	}

	[Fact]
	public async Task Feature_MoveAsync_RejectsMoveWhenMergeConflictPending()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var feat = await features.CreateAsync(board.Id, "Feat 1");
		feat.WorkflowColumn = WorkflowColumn.Build;
		feat.MergeConflictPending = true;
		await dbContext.SaveChangesAsync();

		var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.AgentReview));

		Assert.Equal("Resolve the pending merge conflict before moving this feature.", ex.Message);
	}

	[Fact]
	public async Task Feature_MoveAsync_GatesBacklogToReadyOnFeatureDependencies()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var prereq = await features.CreateAsync(board.Id, "Prereq");
		var blocked = await features.CreateAsync(board.Id, "Blocked");
		await featureDeps.AddAsync(blocked.Id, prereq.Id);

		// Prerequisite is not Done -> move is rejected
		var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			featureOrchestrator.MoveAsync(blocked.Id, WorkflowColumn.Ready));
		Assert.Equal("All feature dependencies must be completed before moving to Ready.", ex.Message);

		// Mark prerequisite as Done
		prereq.WorkflowColumn = WorkflowColumn.Done;
		await dbContext.SaveChangesAsync();

		// Now move succeeds
		var moved = await featureOrchestrator.MoveAsync(blocked.Id, WorkflowColumn.Ready);
		Assert.Equal(WorkflowColumn.Ready, moved.WorkflowColumn);
	}

	[Fact]
	public async Task Feature_MoveAsync_BacklogToReady_BatchAdvancesBacklogStepsToReady()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var feat = await features.CreateAsync(board.Id, "Feat 1");
		var step1 = await steps.CreateAsync(feat.Id, "Step 1");
		var step2 = await steps.CreateAsync(feat.Id, "Step 2");

		Assert.Equal(WorkflowColumn.Backlog, step1.WorkflowColumn);
		Assert.Equal(WorkflowColumn.Backlog, step2.WorkflowColumn);

		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);

		var refreshedStep1 = await steps.GetByIdAsync(step1.Id);
		var refreshedStep2 = await steps.GetByIdAsync(step2.Id);

		Assert.Equal(WorkflowColumn.Ready, refreshedStep1!.WorkflowColumn);
		Assert.Equal(WorkflowColumn.Ready, refreshedStep2!.WorkflowColumn);
	}

	[Fact]
	public async Task Feature_GetAllowedMovesAsync_FiltersOutReadyIfDependenciesUnmet()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var prereq = await features.CreateAsync(board.Id, "Prereq");
		var blocked = await features.CreateAsync(board.Id, "Blocked");
		await featureDeps.AddAsync(blocked.Id, prereq.Id);

		var moves = await featureOrchestrator.GetAllowedMovesAsync(blocked.Id);
		Assert.Empty(moves); // Ready was filtered out because prereq is not Done

		prereq.WorkflowColumn = WorkflowColumn.Done;
		await dbContext.SaveChangesAsync();

		moves = await featureOrchestrator.GetAllowedMovesAsync(blocked.Id);
		Assert.Equal([WorkflowColumn.Ready], moves);
	}

	[Fact]
	public async Task Step_MoveAsync_RejectsDisallowedTransition()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var feat = await features.CreateAsync(board.Id, "Feat 1");
		var step = await steps.CreateAsync(feat.Id, "Step 1");

		var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Build));

		Assert.Equal("Moving from 'Backlog' to 'Build' is not allowed.", ex.Message);
	}

	[Fact]
	public async Task Step_MoveAsync_RejectsMoveFromDone()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var feat = await features.CreateAsync(board.Id, "Feat 1");
		var step = await steps.CreateAsync(feat.Id, "Step 1");
		step.WorkflowColumn = WorkflowColumn.Done;
		await dbContext.SaveChangesAsync();

		var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Backlog));

		Assert.Equal("Completed steps are terminal and cannot be moved.", ex.Message);
	}

	[Fact]
	public async Task Step_MoveAsync_RejectsMoveWhenMergeConflictPending()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var feat = await features.CreateAsync(board.Id, "Feat 1");
		var step = await steps.CreateAsync(feat.Id, "Step 1");
		step.WorkflowColumn = WorkflowColumn.Build;
		step.MergeConflictPending = true;
		await dbContext.SaveChangesAsync();

		var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.AgentReview));

		Assert.Equal("Resolve the pending merge conflict before moving this step.", ex.Message);
	}

	[Fact]
	public async Task Step_MoveAsync_GatesReadyToBuildOnStepDependencies()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var feat = await features.CreateAsync(board.Id, "Feat 1");
		var prereq = await steps.CreateAsync(feat.Id, "Prereq");
		var blocked = await steps.CreateAsync(feat.Id, "Blocked");
		await stepDeps.AddAsync(blocked.Id, prereq.Id);

		// Advance both to Ready
		await stepOrchestrator.MoveAsync(prereq.Id, WorkflowColumn.Ready);
		await stepOrchestrator.MoveAsync(blocked.Id, WorkflowColumn.Ready);

		// Prerequisite is not Done -> move to Build is blocked
		var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			stepOrchestrator.MoveAsync(blocked.Id, WorkflowColumn.Build));
		Assert.Equal("All step dependencies must be completed before moving to Build.", ex.Message);

		// Advance prerequisite to Done
		prereq.WorkflowColumn = WorkflowColumn.Done;
		await dbContext.SaveChangesAsync();

		// Now blocked step can move to Build
		var moved = await stepOrchestrator.MoveAsync(blocked.Id, WorkflowColumn.Build);
		Assert.Equal(WorkflowColumn.Build, moved.WorkflowColumn);
	}

	[Fact]
	public async Task Step_GetAllowedMovesAsync_FiltersOutBuildIfDependenciesUnmet()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var feat = await features.CreateAsync(board.Id, "Feat 1");
		var prereq = await steps.CreateAsync(feat.Id, "Prereq");
		var blocked = await steps.CreateAsync(feat.Id, "Blocked");
		await stepDeps.AddAsync(blocked.Id, prereq.Id);

		await stepOrchestrator.MoveAsync(blocked.Id, WorkflowColumn.Ready);

		var moves = await stepOrchestrator.GetAllowedMovesAsync(blocked.Id);
		// From Ready, candidates are Backlog and Build; since Build is gated and prereq not Done, only Backlog remains
		Assert.Equal([WorkflowColumn.Backlog], moves);

		prereq.WorkflowColumn = WorkflowColumn.Done;
		await dbContext.SaveChangesAsync();

		moves = await stepOrchestrator.GetAllowedMovesAsync(blocked.Id);
		Assert.Equal([WorkflowColumn.Backlog, WorkflowColumn.Build], moves);
	}

	[Fact]
	public async Task Step_MoveAsync_FirstStepEnteringBuild_AdvancesFeatureFromReadyToBuild()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var feat = await features.CreateAsync(board.Id, "Feat 1");
		var step1 = await steps.CreateAsync(feat.Id, "Step 1");
		var step2 = await steps.CreateAsync(feat.Id, "Step 2");

		// Advance feature to Ready (batch advances steps to Ready)
		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);

		var refreshedFeat = await features.GetByIdAsync(feat.Id);
		Assert.Equal(WorkflowColumn.Ready, refreshedFeat!.WorkflowColumn);

		// First step enters Build -> should automatically advance Feature to Build
		var movedStep1 = await stepOrchestrator.MoveAsync(step1.Id, WorkflowColumn.Build);
		Assert.Equal(WorkflowColumn.Build, movedStep1.WorkflowColumn);

		refreshedFeat = await features.GetByIdAsync(feat.Id);
		Assert.Equal(WorkflowColumn.Build, refreshedFeat!.WorkflowColumn);

		// Second step enters Build -> Feature is already in Build, remains in Build
		var movedStep2 = await stepOrchestrator.MoveAsync(step2.Id, WorkflowColumn.Build);
		Assert.Equal(WorkflowColumn.Build, movedStep2.WorkflowColumn);

		refreshedFeat = await features.GetByIdAsync(feat.Id);
		Assert.Equal(WorkflowColumn.Build, refreshedFeat!.WorkflowColumn);
	}

	[Fact]
	public async Task Step_MoveAsync_AllStepsReachingDone_AdvancesFeatureFromBuildToAgentReview()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var feat = await features.CreateAsync(board.Id, "Feat 1");
		var step1 = await steps.CreateAsync(feat.Id, "Step 1");
		var step2 = await steps.CreateAsync(feat.Id, "Step 2");

		// Advance feature to Ready (batch advances steps to Ready)
		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);

		// Step 1 enters Build -> Feature becomes Build
		await stepOrchestrator.MoveAsync(step1.Id, WorkflowColumn.Build);
		// Step 2 enters Build
		await stepOrchestrator.MoveAsync(step2.Id, WorkflowColumn.Build);

		var refreshedFeat = await features.GetByIdAsync(feat.Id);
		Assert.Equal(WorkflowColumn.Build, refreshedFeat!.WorkflowColumn);

		// Step 1 moves Build -> AgentReview -> HumanReview -> Done
		await stepOrchestrator.MoveAsync(step1.Id, WorkflowColumn.AgentReview);
		await stepOrchestrator.MoveAsync(step1.Id, WorkflowColumn.HumanReview);
		await stepOrchestrator.MoveAsync(step1.Id, WorkflowColumn.Done);

		// Step 1 is Done, but Step 2 is still in Build -> Feature remains in Build
		refreshedFeat = await features.GetByIdAsync(feat.Id);
		Assert.Equal(WorkflowColumn.Build, refreshedFeat!.WorkflowColumn);

		// Step 2 moves Build -> AgentReview -> HumanReview -> Done
		await stepOrchestrator.MoveAsync(step2.Id, WorkflowColumn.AgentReview);
		await stepOrchestrator.MoveAsync(step2.Id, WorkflowColumn.HumanReview);
		await stepOrchestrator.MoveAsync(step2.Id, WorkflowColumn.Done);

		// Now all steps are Done -> Feature automatically advances from Build to AgentReview!
		refreshedFeat = await features.GetByIdAsync(feat.Id);
		Assert.Equal(WorkflowColumn.AgentReview, refreshedFeat!.WorkflowColumn);
	}

	[Fact]
	public async Task Step_MoveAsync_SkipHumanReview_AutoAdvancesFromAgentReviewToDone()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1, skipStepHumanReview: true);
		var feat = await features.CreateAsync(board.Id, "Feat 1");
		var step = await steps.CreateAsync(feat.Id, "Step 1", alwaysRequireHumanReview: false);

		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Build);
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.AgentReview);

		// Calling move to HumanReview when skip is active auto-advances straight to Done
		var moved = await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.HumanReview);
		Assert.Equal(WorkflowColumn.Done, moved.WorkflowColumn);

		var refreshed = await steps.GetByIdAsync(step.Id);
		Assert.Equal(WorkflowColumn.Done, refreshed!.WorkflowColumn);
	}

	[Fact]
	public async Task Step_MoveAsync_SkipHumanReview_DirectMoveToDoneSucceeds()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1, skipStepHumanReview: true);
		var feat = await features.CreateAsync(board.Id, "Feat 1");
		var step = await steps.CreateAsync(feat.Id, "Step 1", alwaysRequireHumanReview: false);

		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Build);
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.AgentReview);

		// Calling move directly to Done when skip is active is allowed
		var moved = await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Done);
		Assert.Equal(WorkflowColumn.Done, moved.WorkflowColumn);
	}

	[Fact]
	public async Task Step_MoveAsync_SkipHumanReview_CardOverrideStopsAtHumanReview()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1, skipStepHumanReview: true);
		var feat = await features.CreateAsync(board.Id, "Feat 1");
		var step = await steps.CreateAsync(feat.Id, "Step 1", alwaysRequireHumanReview: true);

		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Build);
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.AgentReview);

		// Direct move to Done is rejected because card requires human review
		var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Done));
		Assert.Equal("Moving from 'AgentReview' to 'Done' is not allowed.", ex.Message);

		// Move to HumanReview stops at HumanReview
		var moved = await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.HumanReview);
		Assert.Equal(WorkflowColumn.HumanReview, moved.WorkflowColumn);
	}

	[Fact]
	public async Task Step_GetAllowedMovesAsync_SkipHumanReview_ReflectsSkipAndOverride()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1, skipStepHumanReview: true);
		var feat = await features.CreateAsync(board.Id, "Feat 1");
		var step = await steps.CreateAsync(feat.Id, "Step 1", alwaysRequireHumanReview: false);

		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Build);
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.AgentReview);

		var moves = await stepOrchestrator.GetAllowedMovesAsync(step.Id);
		// HumanReview is replaced with Done
		Assert.Contains(WorkflowColumn.Done, moves);
		Assert.DoesNotContain(WorkflowColumn.HumanReview, moves);

		// Now enable AlwaysRequireHumanReview override
		step.AlwaysRequireHumanReview = true;
		await dbContext.SaveChangesAsync();

		moves = await stepOrchestrator.GetAllowedMovesAsync(step.Id);
		// Done is removed, HumanReview is restored
		Assert.Contains(WorkflowColumn.HumanReview, moves);
		Assert.DoesNotContain(WorkflowColumn.Done, moves);
	}

	[Fact]
	public async Task Feature_MoveAsync_SkipHumanReview_AutoAdvancesFromAgentReviewToDone()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1, skipFeatureHumanReview: true);
		var feat = await features.CreateAsync(board.Id, "Feat 1", alwaysRequireHumanReview: false);

		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);
		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Build);
		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.AgentReview);

		// Calling move to HumanReview when skip is active auto-advances to Done
		var moved = await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.HumanReview);
		Assert.Equal(WorkflowColumn.Done, moved.WorkflowColumn);

		var refreshed = await features.GetByIdAsync(feat.Id);
		Assert.Equal(WorkflowColumn.Done, refreshed!.WorkflowColumn);
	}

	[Fact]
	public async Task Feature_MoveAsync_SkipHumanReview_DirectMoveToDoneSucceeds()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1, skipFeatureHumanReview: true);
		var feat = await features.CreateAsync(board.Id, "Feat 1", alwaysRequireHumanReview: false);

		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);
		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Build);
		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.AgentReview);

		var moved = await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Done);
		Assert.Equal(WorkflowColumn.Done, moved.WorkflowColumn);
	}

	[Fact]
	public async Task Feature_MoveAsync_SkipHumanReview_CardOverrideStopsAtHumanReview()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1, skipFeatureHumanReview: true);
		var feat = await features.CreateAsync(board.Id, "Feat 1", alwaysRequireHumanReview: true);

		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);
		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Build);
		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.AgentReview);

		// Direct move to Done is rejected
		var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Done));
		Assert.Equal("Moving from 'AgentReview' to 'Done' is not allowed.", ex.Message);

		// Move to HumanReview stops at HumanReview
		var moved = await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.HumanReview);
		Assert.Equal(WorkflowColumn.HumanReview, moved.WorkflowColumn);
	}

	[Fact]
	public async Task Feature_GetAllowedMovesAsync_SkipHumanReview_ReflectsSkipAndOverride()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1, skipFeatureHumanReview: true);
		var feat = await features.CreateAsync(board.Id, "Feat 1", alwaysRequireHumanReview: false);

		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);
		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Build);
		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.AgentReview);

		var moves = await featureOrchestrator.GetAllowedMovesAsync(feat.Id);
		Assert.Contains(WorkflowColumn.Done, moves);
		Assert.DoesNotContain(WorkflowColumn.HumanReview, moves);

		// Override
		feat.AlwaysRequireHumanReview = true;
		await dbContext.SaveChangesAsync();

		moves = await featureOrchestrator.GetAllowedMovesAsync(feat.Id);
		Assert.Contains(WorkflowColumn.HumanReview, moves);
		Assert.DoesNotContain(WorkflowColumn.Done, moves);
	}

	[Fact]
	public async Task Step_AutoAdvanceToDone_TriggersFeatureBuildToAgentReview()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1, skipStepHumanReview: true);
		var feat = await features.CreateAsync(board.Id, "Feat 1");
		var step = await steps.CreateAsync(feat.Id, "Step 1");

		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Build);
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.AgentReview);

		// Step auto-advances from AgentReview to Done via skipStepHumanReview
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.HumanReview);

		var refreshedStep = await steps.GetByIdAsync(step.Id);
		Assert.Equal(WorkflowColumn.Done, refreshedStep!.WorkflowColumn);

		// Since all steps of the feature are Done, parent Feature automatically advances from Build to AgentReview
		var refreshedFeat = await features.GetByIdAsync(feat.Id);
		Assert.Equal(WorkflowColumn.AgentReview, refreshedFeat!.WorkflowColumn);
	}

	[Fact]
	public async Task Feature_MoveAsync_FromAgentReviewToBuild_IncrementsAgentReviewFailCount()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var feat = await features.CreateAsync(board.Id, "Feat 1");

		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);
		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Build);
		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.AgentReview);

		Assert.Equal(0, feat.AgentReviewFailCount);
		Assert.Equal(0, feat.HumanReviewFailCount);

		// Failed review: AgentReview -> Build
		var moved = await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Build);
		Assert.Equal(WorkflowColumn.Build, moved.WorkflowColumn);
		Assert.Equal(1, moved.AgentReviewFailCount);
		Assert.Equal(0, moved.HumanReviewFailCount);

		var refreshed = await features.GetByIdAsync(feat.Id);
		Assert.Equal(1, refreshed!.AgentReviewFailCount);
		Assert.Equal(0, refreshed.HumanReviewFailCount);
	}

	[Fact]
	public async Task Feature_MoveAsync_FromHumanReviewToBuild_IncrementsHumanReviewFailCount()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var feat = await features.CreateAsync(board.Id, "Feat 1", alwaysRequireHumanReview: true);

		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);
		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Build);
		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.AgentReview);
		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.HumanReview);

		Assert.Equal(0, feat.AgentReviewFailCount);
		Assert.Equal(0, feat.HumanReviewFailCount);

		// Failed review: HumanReview -> Build
		var moved = await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Build);
		Assert.Equal(WorkflowColumn.Build, moved.WorkflowColumn);
		Assert.Equal(0, moved.AgentReviewFailCount);
		Assert.Equal(1, moved.HumanReviewFailCount);

		var refreshed = await features.GetByIdAsync(feat.Id);
		Assert.Equal(0, refreshed!.AgentReviewFailCount);
		Assert.Equal(1, refreshed.HumanReviewFailCount);
	}

	[Fact]
	public async Task Step_MoveAsync_FromAgentReviewToBuild_IncrementsAgentReviewFailCount()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var feat = await features.CreateAsync(board.Id, "Feat 1");
		var step = await steps.CreateAsync(feat.Id, "Step 1");

		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Build);
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.AgentReview);

		Assert.Equal(0, step.AgentReviewFailCount);
		Assert.Equal(0, step.HumanReviewFailCount);

		// Failed review: AgentReview -> Build
		var moved = await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Build);
		Assert.Equal(WorkflowColumn.Build, moved.WorkflowColumn);
		Assert.Equal(1, moved.AgentReviewFailCount);
		Assert.Equal(0, moved.HumanReviewFailCount);

		var refreshed = await steps.GetByIdAsync(step.Id);
		Assert.Equal(1, refreshed!.AgentReviewFailCount);
		Assert.Equal(0, refreshed.HumanReviewFailCount);
	}

	[Fact]
	public async Task Step_MoveAsync_FromHumanReviewToBuild_IncrementsHumanReviewFailCount()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var feat = await features.CreateAsync(board.Id, "Feat 1");
		var step = await steps.CreateAsync(feat.Id, "Step 1", alwaysRequireHumanReview: true);

		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Build);
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.AgentReview);
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.HumanReview);

		Assert.Equal(0, step.AgentReviewFailCount);
		Assert.Equal(0, step.HumanReviewFailCount);

		// Failed review: HumanReview -> Build
		var moved = await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Build);
		Assert.Equal(WorkflowColumn.Build, moved.WorkflowColumn);
		Assert.Equal(0, moved.AgentReviewFailCount);
		Assert.Equal(1, moved.HumanReviewFailCount);

		var refreshed = await steps.GetByIdAsync(step.Id);
		Assert.Equal(0, refreshed!.AgentReviewFailCount);
		Assert.Equal(1, refreshed.HumanReviewFailCount);
	}

	[Fact]
	public async Task MoveAsync_ToBacklog_DoesNotIncrementFailCounts()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var feat = await features.CreateAsync(board.Id, "Feat 1");
		var step = await steps.CreateAsync(feat.Id, "Step 1");

		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Build);
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.AgentReview);
		// Fail step review
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Build);
		Assert.Equal(1, step.AgentReviewFailCount);

		// Now move to Backlog
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Backlog);
		var refreshedStep = await steps.GetByIdAsync(step.Id);
		Assert.Equal(WorkflowColumn.Backlog, refreshedStep!.WorkflowColumn);
		// Counters never reset, but moving to Backlog did NOT increment them either
		Assert.Equal(1, refreshedStep.AgentReviewFailCount);
		Assert.Equal(0, refreshedStep.HumanReviewFailCount);
	}
}
