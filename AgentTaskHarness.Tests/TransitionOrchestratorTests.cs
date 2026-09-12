using AgentTaskHarness.Application.Boards;
using AgentTaskHarness.Application.Features;
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
		featureOrchestrator = new FeatureTransitionOrchestrator(dbContext, rules, featureDeps);
		stepOrchestrator = new StepTransitionOrchestrator(dbContext, rules, stepDeps);
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
}
