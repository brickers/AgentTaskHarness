using AgentTaskHarness.Application.Abstractions;
using AgentTaskHarness.Application.Agents;
using AgentTaskHarness.Application.Boards;
using AgentTaskHarness.Application.Features;
using AgentTaskHarness.Application.Reviews;
using AgentTaskHarness.Application.Steps;
using AgentTaskHarness.Application.Workflow;
using AgentTaskHarness.Domain.Entities;
using AgentTaskHarness.Domain.Enums;
using AgentTaskHarness.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentTaskHarness.Tests;

public class TestAgentProcessRunner : IAgentProcessRunner
{
	public List<(Step Step, AgentDefinition Definition)> StartedAgents { get; } = [];
	public List<int> StoppedProcessIds { get; } = [];
	private int nextProcessId = 100;

	public Task<AgentProcessResult> StartAsync(Step step, AgentDefinition definition, CancellationToken cancellationToken = default)
	{
		StartedAgents.Add((step, definition));
		var processId = nextProcessId++;
		var sessionLink = $"https://session.link/{processId}";
		return Task.FromResult(new AgentProcessResult(processId, sessionLink));
	}

	public Task StopAsync(int processId, CancellationToken cancellationToken = default)
	{
		StoppedProcessIds.Add(processId);
		return Task.CompletedTask;
	}
}

public class AgentSchedulerServiceTests : IAsyncLifetime
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
	private AgentMatchingService agentMatchingService = null!;
	private TestAgentProcessRunner testProcessRunner = null!;
	private AgentSchedulerService scheduler = null!;
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
		agentMatchingService = new AgentMatchingService(dbContext);
		testProcessRunner = new TestAgentProcessRunner();

		scheduler = new AgentSchedulerService(
			dbContext,
			agentMatchingService,
			reviewOutcomeService,
			stepDeps,
			testProcessRunner);

		featureOrchestrator = new FeatureTransitionOrchestrator(dbContext, rules, featureDeps, reviewOutcomeService);
		stepOrchestrator = new StepTransitionOrchestrator(
			dbContext,
			rules,
			stepDeps,
			featureOrchestrator,
			reviewOutcomeService,
			gitWorktrees: null,
			agentScheduler: scheduler);
	}

	public async Task DisposeAsync()
	{
		await dbContext.DisposeAsync();
		await connection.DisposeAsync();
	}

	private async Task<AgentDefinition> CreateAndAssignAgentAsync(Guid boardId, ColumnScope scope, string? name = null)
	{
		var agentName = name ?? $"Agent_{scope}_{Guid.NewGuid():N}";
		var def = new AgentDefinition
		{
			BoardId = boardId,
			Name = agentName,
			FolderPath = $"/agents/{agentName}"
		};
		dbContext.AgentDefinitions.Add(def);
		await dbContext.SaveChangesAsync();

		await agentMatchingService.AssignAgentAsync(boardId, scope, def.Id, matchCriteria: null);
		return def;
	}

	[Fact]
	public async Task RequestStart_IgnoresColumnsOtherThanBuildOrAgentReview()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var feat = await features.CreateAsync(board.Id, "Feat 1");
		var step = await steps.CreateAsync(feat.Id, "Step 1");
		await CreateAndAssignAgentAsync(board.Id, ColumnScope.StepBuild);

		// Step is currently in Backlog
		await scheduler.RequestStartAsync(step.Id);

		var run = await scheduler.GetCurrentRunAsync(step.Id);
		Assert.Null(run);
		Assert.Empty(testProcessRunner.StartedAgents);
	}

	[Fact]
	public async Task RequestStart_StartsAgentWhenConcurrencyAllows()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var feat = await features.CreateAsync(board.Id, "Feat 1");
		var step = await steps.CreateAsync(feat.Id, "Step 1");
		var agent = await CreateAndAssignAgentAsync(board.Id, ColumnScope.StepBuild);

		// Advance to Build
		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Build);

		var run = await scheduler.GetCurrentRunAsync(step.Id);
		Assert.NotNull(run);
		Assert.Equal(AgentRunStatus.Working, run.Status);
		Assert.NotNull(run.ProcessId);
		Assert.NotNull(run.SessionLink);
		Assert.Single(testProcessRunner.StartedAgents);
		Assert.Equal(agent.Id, testProcessRunner.StartedAgents[0].Definition.Id);
	}

	[Fact]
	public void CalculatePriorityScore_ProgressWeight_ScoresAgentReviewHigherThanBuild()
	{
		var stepBuild = new Step { Id = Guid.NewGuid(), WorkflowColumn = WorkflowColumn.Build, CreatedAt = DateTimeOffset.UtcNow };
		var stepReview = new Step { Id = Guid.NewGuid(), WorkflowColumn = WorkflowColumn.AgentReview, CreatedAt = DateTimeOffset.UtcNow };

		var scoreBuild = AgentSchedulerService.CalculatePriorityScore(stepBuild, totalStepsInFeature: 3, doneStepsInFeature: 1, dependentStepsCount: 0);
		var scoreReview = AgentSchedulerService.CalculatePriorityScore(stepReview, totalStepsInFeature: 3, doneStepsInFeature: 1, dependentStepsCount: 0);

		Assert.True(scoreReview > scoreBuild, "AgentReview should have higher progress score than Build.");
	}

	[Fact]
	public void CalculatePriorityScore_RemainingWorkWeight_ScoresFewerRemainingStepsHigher()
	{
		var stepNearDone = new Step { Id = Guid.NewGuid(), WorkflowColumn = WorkflowColumn.Build, CreatedAt = DateTimeOffset.UtcNow };
		var stepFarFromDone = new Step { Id = Guid.NewGuid(), WorkflowColumn = WorkflowColumn.Build, CreatedAt = DateTimeOffset.UtcNow };

		// stepNearDone has 1 remaining step (done 4 of 5), stepFarFromDone has 5 remaining (done 0 of 5)
		var scoreNearDone = AgentSchedulerService.CalculatePriorityScore(stepNearDone, totalStepsInFeature: 5, doneStepsInFeature: 4, dependentStepsCount: 0);
		var scoreFarFromDone = AgentSchedulerService.CalculatePriorityScore(stepFarFromDone, totalStepsInFeature: 5, doneStepsInFeature: 0, dependentStepsCount: 0);

		Assert.True(scoreNearDone > scoreFarFromDone, "Step closer to finishing feature should score higher.");
	}

	[Fact]
	public void CalculatePriorityScore_RemainingWorkWeight_ScoresUnblockingDependentsHigher()
	{
		var stepWithDependents = new Step { Id = Guid.NewGuid(), WorkflowColumn = WorkflowColumn.Build, CreatedAt = DateTimeOffset.UtcNow };
		var stepWithoutDependents = new Step { Id = Guid.NewGuid(), WorkflowColumn = WorkflowColumn.Build, CreatedAt = DateTimeOffset.UtcNow };

		var scoreWithDeps = AgentSchedulerService.CalculatePriorityScore(stepWithDependents, totalStepsInFeature: 3, doneStepsInFeature: 0, dependentStepsCount: 3);
		var scoreNoDeps = AgentSchedulerService.CalculatePriorityScore(stepWithoutDependents, totalStepsInFeature: 3, doneStepsInFeature: 0, dependentStepsCount: 0);

		Assert.True(scoreWithDeps > scoreNoDeps, "Step that unblocks more dependents should score higher.");
	}

	[Fact]
	public void CalculatePriorityScore_AgeWeight_BoostsOlderQueuedSteps()
	{
		var step = new Step { Id = Guid.NewGuid(), WorkflowColumn = WorkflowColumn.Build, CreatedAt = DateTimeOffset.UtcNow };
		var queuedEarlier = DateTimeOffset.UtcNow.AddMinutes(-60);
		var queuedJustNow = DateTimeOffset.UtcNow;

		var scoreEarlier = AgentSchedulerService.CalculatePriorityScore(step, totalStepsInFeature: 2, doneStepsInFeature: 0, dependentStepsCount: 0, queuedAt: queuedEarlier);
		var scoreJustNow = AgentSchedulerService.CalculatePriorityScore(step, totalStepsInFeature: 2, doneStepsInFeature: 0, dependentStepsCount: 0, queuedAt: queuedJustNow);

		Assert.True(scoreEarlier > scoreJustNow, "Older queued item should receive higher score to prevent starvation.");
	}

	[Fact]
	public async Task ConcurrencyLimit_QueuesWhenFull_AndStartsHighestPriorityWhenSlotFreed()
	{
		// Board with ConcurrencyLimit = 1
		var board = await boards.CreateAsync("Board 1", "/repos/b1", concurrencyLimit: 1);
		await CreateAndAssignAgentAsync(board.Id, ColumnScope.StepBuild);
		await CreateAndAssignAgentAsync(board.Id, ColumnScope.StepAgentReview);

		var feat1 = await features.CreateAsync(board.Id, "Feat 1");
		var feat2 = await features.CreateAsync(board.Id, "Feat 2");

		var step1 = await steps.CreateAsync(feat1.Id, "Step 1");
		var step2 = await steps.CreateAsync(feat1.Id, "Step 2");
		var step3 = await steps.CreateAsync(feat2.Id, "Step 3");

		await featureOrchestrator.MoveAsync(feat1.Id, WorkflowColumn.Ready);
		await featureOrchestrator.MoveAsync(feat2.Id, WorkflowColumn.Ready);

		// Step 1 enters Build -> starts running (active runs = 1/1)
		await stepOrchestrator.MoveAsync(step1.Id, WorkflowColumn.Build);
		Assert.Equal(AgentRunStatus.Working, (await scheduler.GetCurrentRunAsync(step1.Id))!.Status);

		// Add dependency: step3 has a dependent, making it higher priority than step2
		var stepDependent = await steps.CreateAsync(feat2.Id, "Step Dep");
		await stepDeps.AddAsync(stepDependent.Id, step3.Id);

		// Now step2 and step3 move to Build (using MCP move or scheduler directly since board is busy)
		// They should both be queued
		step2.WorkflowColumn = WorkflowColumn.Build;
		step3.WorkflowColumn = WorkflowColumn.Build;
		await dbContext.SaveChangesAsync();

		await scheduler.RequestStartAsync(step2.Id);
		await scheduler.RequestStartAsync(step3.Id);

		Assert.Equal(AgentRunStatus.Queued, (await scheduler.GetCurrentRunAsync(step2.Id))!.Status);
		Assert.Equal(AgentRunStatus.Queued, (await scheduler.GetCurrentRunAsync(step3.Id))!.Status);

		// Step 1 finishes!
		await scheduler.OnAgentFinishedAsync(step1.Id);

		// Step 1 is Completed
		Assert.Equal(AgentRunStatus.Completed, (await scheduler.GetCurrentRunAsync(step1.Id))!.Status);

		// Step 3 had higher priority score due to unblocking dependent step -> it should have started!
		var run3 = await scheduler.GetCurrentRunAsync(step3.Id);
		Assert.Equal(AgentRunStatus.Working, run3!.Status);

		// Step 2 should remain queued
		var run2 = await scheduler.GetCurrentRunAsync(step2.Id);
		Assert.Equal(AgentRunStatus.Queued, run2!.Status);
	}

	[Fact]
	public async Task FailureThresholdSkip_DoesNotAutoStartStepCrossingBoardThreshold()
	{
		// Board with AgentReviewFailThreshold = 2
		var board = await boards.CreateAsync("Board 1", "/repos/b1", concurrencyLimit: 1, agentReviewFailThreshold: 2);
		await CreateAndAssignAgentAsync(board.Id, ColumnScope.StepBuild);

		var feat = await features.CreateAsync(board.Id, "Feat 1");
		var step = await steps.CreateAsync(feat.Id, "Step 1");
		step.WorkflowColumn = WorkflowColumn.Build;
		step.AgentReviewFailCount = 2; // Hit threshold!
		await dbContext.SaveChangesAsync();

		await scheduler.RequestStartAsync(step.Id);

		// Should be queued/flagged but NOT started (process runner not invoked)
		var run = await scheduler.GetCurrentRunAsync(step.Id);
		Assert.NotNull(run);
		Assert.Equal(AgentRunStatus.Queued, run.Status);
		Assert.Empty(testProcessRunner.StartedAgents);
	}

	[Fact]
	public async Task FailureThresholdSkip_HumanReviewFailThresholdExceeded_SkipsAutoStart()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", concurrencyLimit: 1, humanReviewFailThreshold: 1);
		await CreateAndAssignAgentAsync(board.Id, ColumnScope.StepBuild);

		var feat = await features.CreateAsync(board.Id, "Feat 1");
		var step = await steps.CreateAsync(feat.Id, "Step 1");
		step.WorkflowColumn = WorkflowColumn.Build;
		step.HumanReviewFailCount = 1; // Hit threshold!
		await dbContext.SaveChangesAsync();

		await scheduler.RequestStartAsync(step.Id);

		var run = await scheduler.GetCurrentRunAsync(step.Id);
		Assert.NotNull(run);
		Assert.Equal(AgentRunStatus.Queued, run.Status);
		Assert.Empty(testProcessRunner.StartedAgents);
	}

	[Fact]
	public async Task UI_HardBlocksMove_WhenAgentIsCurrentlyRunning()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		await CreateAndAssignAgentAsync(board.Id, ColumnScope.StepBuild);
		await CreateAndAssignAgentAsync(board.Id, ColumnScope.StepAgentReview);

		var feat = await features.CreateAsync(board.Id, "Feat 1");
		var step = await steps.CreateAsync(feat.Id, "Step 1");

		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Build);

		// Agent is now Working
		Assert.True(await scheduler.IsAgentRunningAsync(step.Id));

		// UI move (isMcpMove = false) must throw InvalidOperationException
		var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.AgentReview));
		Assert.Equal("Cannot move a step while an agent is running.", ex.Message);

		// GetAllowedMovesAsync must return empty list for UI
		var allowed = await stepOrchestrator.GetAllowedMovesAsync(step.Id);
		Assert.Empty(allowed);
	}

	[Fact]
	public async Task MCP_AllowsMoveWhileAgentRunning_AndSetsSoftBlockedStatus()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		await CreateAndAssignAgentAsync(board.Id, ColumnScope.StepBuild);
		await CreateAndAssignAgentAsync(board.Id, ColumnScope.StepAgentReview);

		var feat = await features.CreateAsync(board.Id, "Feat 1");
		var step = await steps.CreateAsync(feat.Id, "Step 1");

		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Build);

		var buildRun = await scheduler.GetCurrentRunAsync(step.Id);
		Assert.NotNull(buildRun);
		Assert.Equal(AgentRunStatus.Working, buildRun.Status);

		// MCP move succeeds even though agent is still running
		var movedStep = await stepOrchestrator.MoveMcpAsync(step.Id, WorkflowColumn.AgentReview);
		Assert.Equal(WorkflowColumn.AgentReview, movedStep.WorkflowColumn);

		// A new run with Blocked status is created
		var currentRun = await scheduler.GetCurrentRunAsync(step.Id);
		Assert.NotNull(currentRun);
		Assert.Equal(AgentRunStatus.Blocked, currentRun.Status);

		// When outgoing agent finishes, the blocked run is unblocked to Queued and then started
		await scheduler.OnAgentFinishedAsync(step.Id);

		// Outgoing build run is Completed
		var refreshedBuildRun = await dbContext.AgentRuns.FindAsync(buildRun.Id);
		Assert.Equal(AgentRunStatus.Completed, refreshedBuildRun!.Status);

		// New run transitioned from Blocked -> Queued -> Working!
		var newRun = await scheduler.GetCurrentRunAsync(step.Id);
		Assert.Equal(AgentRunStatus.Working, newRun!.Status);
	}

	[Fact]
	public async Task StopAgentAsync_StopsRunningAgent_AndTriggersQueue()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", concurrencyLimit: 1);
		await CreateAndAssignAgentAsync(board.Id, ColumnScope.StepBuild);

		var feat = await features.CreateAsync(board.Id, "Feat 1");
		var step1 = await steps.CreateAsync(feat.Id, "Step 1");
		var step2 = await steps.CreateAsync(feat.Id, "Step 2");

		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);
		await stepOrchestrator.MoveAsync(step1.Id, WorkflowColumn.Build);

		var run1 = await scheduler.GetCurrentRunAsync(step1.Id);
		Assert.Equal(AgentRunStatus.Working, run1!.Status);
		Assert.NotNull(run1.ProcessId);

		// Queue step2
		step2.WorkflowColumn = WorkflowColumn.Build;
		await dbContext.SaveChangesAsync();
		await scheduler.RequestStartAsync(step2.Id);
		Assert.Equal(AgentRunStatus.Queued, (await scheduler.GetCurrentRunAsync(step2.Id))!.Status);

		// Stop step1 agent
		await scheduler.StopAgentAsync(step1.Id);

		// Step 1 run is Stopped, process runner was requested to stop
		var stoppedRun1 = await dbContext.AgentRuns.FindAsync(run1.Id);
		Assert.Equal(AgentRunStatus.Stopped, stoppedRun1!.Status);
		Assert.Contains(run1.ProcessId.Value, testProcessRunner.StoppedProcessIds);

		// Step 2 automatically started!
		var run2 = await scheduler.GetCurrentRunAsync(step2.Id);
		Assert.Equal(AgentRunStatus.Working, run2!.Status);
	}

	[Fact]
	public async Task OnAgentFinishedAsync_RecordsUsageOnAgentRun_AndUpdatesStepEntity()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		await CreateAndAssignAgentAsync(board.Id, ColumnScope.StepBuild);

		var feat = await features.CreateAsync(board.Id, "Feat 1");
		var step = await steps.CreateAsync(feat.Id, "Step 1");

		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Build);

		var run = await scheduler.GetCurrentRunAsync(step.Id);
		Assert.NotNull(run);

		// Finish agent with specific token and time usage
		await scheduler.OnAgentFinishedAsync(step.Id, tokensUsed: 1500, timeSpent: TimeSpan.FromMinutes(3));

		var refreshedRun = await dbContext.AgentRuns.FindAsync(run.Id);
		Assert.Equal(AgentRunStatus.Completed, refreshedRun!.Status);
		Assert.Equal(1500, refreshedRun.TokensUsed);
		Assert.Equal(TimeSpan.FromMinutes(3), refreshedRun.TimeSpent);

		var refreshedStep = await dbContext.Steps.FindAsync(step.Id);
		Assert.Equal(1500, refreshedStep!.TokensUsed);
		Assert.Equal(TimeSpan.FromMinutes(3), refreshedStep.TimeSpent);
	}

	[Fact]
	public async Task OnAgentFinishedAsync_AccumulatesTokensAndTimeAcrossMultipleRuns()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		await CreateAndAssignAgentAsync(board.Id, ColumnScope.StepBuild);
		await CreateAndAssignAgentAsync(board.Id, ColumnScope.StepAgentReview);

		var feat = await features.CreateAsync(board.Id, "Feat 1");
		var step = await steps.CreateAsync(feat.Id, "Step 1");

		// Run 1: In Build
		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Build);
		await scheduler.OnAgentFinishedAsync(step.Id, tokensUsed: 1000, timeSpent: TimeSpan.FromMinutes(2));

		var stepAfterRun1 = await dbContext.Steps.FindAsync(step.Id);
		Assert.Equal(1000, stepAfterRun1!.TokensUsed);
		Assert.Equal(TimeSpan.FromMinutes(2), stepAfterRun1.TimeSpent);

		// Run 2: Advance to AgentReview and run again
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.AgentReview);
		await scheduler.OnAgentFinishedAsync(step.Id, tokensUsed: 2500, timeSpent: TimeSpan.FromMinutes(5));

		var stepAfterRun2 = await dbContext.Steps.FindAsync(step.Id);
		Assert.Equal(3500, stepAfterRun2!.TokensUsed);
		Assert.Equal(TimeSpan.FromMinutes(7), stepAfterRun2.TimeSpent);
	}

	[Fact]
	public async Task StopAgentAsync_RecordsUsageOnAgentRun_AndUpdatesStepEntity()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		await CreateAndAssignAgentAsync(board.Id, ColumnScope.StepBuild);

		var feat = await features.CreateAsync(board.Id, "Feat 1");
		var step = await steps.CreateAsync(feat.Id, "Step 1");

		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Build);

		var run = await scheduler.GetCurrentRunAsync(step.Id);
		Assert.NotNull(run);

		await scheduler.StopAgentAsync(step.Id, tokensUsed: 600, timeSpent: TimeSpan.FromSeconds(45));

		var refreshedRun = await dbContext.AgentRuns.FindAsync(run.Id);
		Assert.Equal(AgentRunStatus.Stopped, refreshedRun!.Status);
		Assert.Equal(600, refreshedRun.TokensUsed);
		Assert.Equal(TimeSpan.FromSeconds(45), refreshedRun.TimeSpent);

		var refreshedStep = await dbContext.Steps.FindAsync(step.Id);
		Assert.Equal(600, refreshedStep!.TokensUsed);
		Assert.Equal(TimeSpan.FromSeconds(45), refreshedStep.TimeSpent);
	}

	[Fact]
	public async Task RecordRunUsageAsync_DirectlyUpdatesUsage()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		await CreateAndAssignAgentAsync(board.Id, ColumnScope.StepBuild);

		var feat = await features.CreateAsync(board.Id, "Feat 1");
		var step = await steps.CreateAsync(feat.Id, "Step 1");

		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Build);

		await scheduler.RecordRunUsageAsync(step.Id, tokensUsed: 2000, timeSpent: TimeSpan.FromMinutes(4));

		var refreshedStep = await dbContext.Steps.FindAsync(step.Id);
		Assert.Equal(2000, refreshedStep!.TokensUsed);
		Assert.Equal(TimeSpan.FromMinutes(4), refreshedStep.TimeSpent);
	}
}
