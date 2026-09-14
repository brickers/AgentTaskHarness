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
	private int _nextProcessId = 100;
	public List<(Step Step, AgentDefinition Definition)> StartedAgents { get; } = [];
	public List<int> StoppedProcessIds { get; } = [];

	public Task<AgentProcessResult> StartAsync(Step step, AgentDefinition definition,
		CancellationToken cancellationToken = default)
	{
		StartedAgents.Add((step, definition));
		var processId = _nextProcessId++;
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
	private readonly SqliteConnection _connection = new("Data Source=:memory:");
	private AgentMatchingService _agentMatchingService = null!;
	private BoardService _boards = null!;
	private AppDbContext _dbContext = null!;
	private FeatureDependencyService _featureDeps = null!;
	private FeatureTransitionOrchestrator _featureOrchestrator = null!;
	private FeatureService _features = null!;
	private ReviewOutcomeService _reviewOutcomeService = null!;
	private WorkflowTransitionRules _rules = null!;
	private AgentSchedulerService _scheduler = null!;
	private StepDependencyService _stepDeps = null!;
	private StepTransitionOrchestrator _stepOrchestrator = null!;
	private StepService _steps = null!;
	private TestAgentProcessRunner _testProcessRunner = null!;

	public async Task InitializeAsync()
	{
		await _connection.OpenAsync();
		_dbContext = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
		await _dbContext.Database.MigrateAsync();

		_boards = new BoardService(_dbContext);
		_features = new FeatureService(_dbContext);
		_steps = new StepService(_dbContext);
		_rules = new WorkflowTransitionRules();
		_featureDeps = new FeatureDependencyService(_dbContext);
		_stepDeps = new StepDependencyService(_dbContext);
		_reviewOutcomeService = new ReviewOutcomeService(_dbContext);
		_agentMatchingService = new AgentMatchingService(_dbContext);
		_testProcessRunner = new TestAgentProcessRunner();

		_scheduler = new AgentSchedulerService(
			_dbContext,
			_agentMatchingService,
			_reviewOutcomeService,
			_stepDeps,
			_testProcessRunner);

		_featureOrchestrator =
			new FeatureTransitionOrchestrator(_dbContext, _rules, _featureDeps, _reviewOutcomeService);
		_stepOrchestrator = new StepTransitionOrchestrator(
			_dbContext,
			_rules,
			_stepDeps,
			_featureOrchestrator,
			_reviewOutcomeService,
			null,
			_scheduler);
	}

	public async Task DisposeAsync()
	{
		await _dbContext.DisposeAsync();
		await _connection.DisposeAsync();
	}

	private async Task<AgentDefinition> CreateAndAssignAgentAsync(Guid boardId, ColumnScope scope, string? name = null)
	{
		var agentName = name ?? $"Agent_{scope}_{Guid.NewGuid():N}";
		var def = new AgentDefinition
		{
			Name = agentName,
			FolderPath = $"/agents/{agentName}"
		};
		_dbContext.AgentDefinitions.Add(def);
		await _dbContext.SaveChangesAsync();

		await _agentMatchingService.AssignAgentAsync(boardId, scope, def.Id);
		return def;
	}

	[Fact]
	public async Task RequestStart_IgnoresColumnsOtherThanBuildOrAgentReview()
	{
		var board = await _boards.CreateAsync("Board 1", "/repos/b1");
		var feat = await _features.CreateAsync(board.Id, "Feat 1");
		var step = await _steps.CreateAsync(feat.Id, "Step 1");
		await CreateAndAssignAgentAsync(board.Id, ColumnScope.StepBuild);

		// Step is currently in Backlog
		await _scheduler.RequestStartAsync(step.Id);

		var run = await _scheduler.GetCurrentRunAsync(step.Id);
		Assert.Null(run);
		Assert.Empty(_testProcessRunner.StartedAgents);
	}

	[Fact]
	public async Task RequestStart_StartsAgentWhenConcurrencyAllows()
	{
		var board = await _boards.CreateAsync("Board 1", "/repos/b1");
		var feat = await _features.CreateAsync(board.Id, "Feat 1");
		var step = await _steps.CreateAsync(feat.Id, "Step 1");
		var agent = await CreateAndAssignAgentAsync(board.Id, ColumnScope.StepBuild);

		// Advance to Build
		await _featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);
		await _stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Build);

		var run = await _scheduler.GetCurrentRunAsync(step.Id);
		Assert.NotNull(run);
		Assert.Equal(AgentRunStatus.Working, run.Status);
		Assert.NotNull(run.ProcessId);
		Assert.NotNull(run.SessionLink);
		Assert.Single(_testProcessRunner.StartedAgents);
		Assert.Equal(agent.Id, _testProcessRunner.StartedAgents[0].Definition.Id);
	}

	[Fact]
	public void CalculatePriorityScore_ProgressWeight_ScoresAgentReviewHigherThanBuild()
	{
		var stepBuild = new Step
			{ Id = Guid.NewGuid(), WorkflowColumn = WorkflowColumn.Build, CreatedAt = DateTimeOffset.UtcNow };
		var stepReview = new Step
			{ Id = Guid.NewGuid(), WorkflowColumn = WorkflowColumn.AgentReview, CreatedAt = DateTimeOffset.UtcNow };

		var scoreBuild = AgentSchedulerService.CalculatePriorityScore(stepBuild, 3, 1, 0);
		var scoreReview = AgentSchedulerService.CalculatePriorityScore(stepReview, 3, 1, 0);

		Assert.True(scoreReview > scoreBuild, "AgentReview should have higher progress score than Build.");
	}

	[Fact]
	public void CalculatePriorityScore_RemainingWorkWeight_ScoresFewerRemainingStepsHigher()
	{
		var stepNearDone = new Step
			{ Id = Guid.NewGuid(), WorkflowColumn = WorkflowColumn.Build, CreatedAt = DateTimeOffset.UtcNow };
		var stepFarFromDone = new Step
			{ Id = Guid.NewGuid(), WorkflowColumn = WorkflowColumn.Build, CreatedAt = DateTimeOffset.UtcNow };

		// stepNearDone has 1 remaining step (done 4 of 5), stepFarFromDone has 5 remaining (done 0 of 5)
		var scoreNearDone = AgentSchedulerService.CalculatePriorityScore(stepNearDone, 5, 4, 0);
		var scoreFarFromDone = AgentSchedulerService.CalculatePriorityScore(stepFarFromDone, 5, 0, 0);

		Assert.True(scoreNearDone > scoreFarFromDone, "Step closer to finishing feature should score higher.");
	}

	[Fact]
	public void CalculatePriorityScore_RemainingWorkWeight_ScoresUnblockingDependentsHigher()
	{
		var stepWithDependents = new Step
			{ Id = Guid.NewGuid(), WorkflowColumn = WorkflowColumn.Build, CreatedAt = DateTimeOffset.UtcNow };
		var stepWithoutDependents = new Step
			{ Id = Guid.NewGuid(), WorkflowColumn = WorkflowColumn.Build, CreatedAt = DateTimeOffset.UtcNow };

		var scoreWithDeps = AgentSchedulerService.CalculatePriorityScore(stepWithDependents, 3, 0, 3);
		var scoreNoDeps = AgentSchedulerService.CalculatePriorityScore(stepWithoutDependents, 3, 0, 0);

		Assert.True(scoreWithDeps > scoreNoDeps, "Step that unblocks more dependents should score higher.");
	}

	[Fact]
	public void CalculatePriorityScore_AgeWeight_BoostsOlderQueuedSteps()
	{
		var step = new Step
			{ Id = Guid.NewGuid(), WorkflowColumn = WorkflowColumn.Build, CreatedAt = DateTimeOffset.UtcNow };
		var queuedEarlier = DateTimeOffset.UtcNow.AddMinutes(-60);
		var queuedJustNow = DateTimeOffset.UtcNow;

		var scoreEarlier = AgentSchedulerService.CalculatePriorityScore(step, 2, 0, 0, queuedEarlier);
		var scoreJustNow = AgentSchedulerService.CalculatePriorityScore(step, 2, 0, 0, queuedJustNow);

		Assert.True(scoreEarlier > scoreJustNow,
			"Older queued item should receive higher score to prevent starvation.");
	}

	[Fact]
	public async Task ConcurrencyLimit_QueuesWhenFull_AndStartsHighestPriorityWhenSlotFreed()
	{
		// Board with ConcurrencyLimit = 1
		var board = await _boards.CreateAsync("Board 1", "/repos/b1");
		await CreateAndAssignAgentAsync(board.Id, ColumnScope.StepBuild);
		await CreateAndAssignAgentAsync(board.Id, ColumnScope.StepAgentReview);

		var feat1 = await _features.CreateAsync(board.Id, "Feat 1");
		var feat2 = await _features.CreateAsync(board.Id, "Feat 2");

		var step1 = await _steps.CreateAsync(feat1.Id, "Step 1");
		var step2 = await _steps.CreateAsync(feat1.Id, "Step 2");
		var step3 = await _steps.CreateAsync(feat2.Id, "Step 3");

		await _featureOrchestrator.MoveAsync(feat1.Id, WorkflowColumn.Ready);
		await _featureOrchestrator.MoveAsync(feat2.Id, WorkflowColumn.Ready);

		// Step 1 enters Build -> starts running (active runs = 1/1)
		await _stepOrchestrator.MoveAsync(step1.Id, WorkflowColumn.Build);
		Assert.Equal(AgentRunStatus.Working, (await _scheduler.GetCurrentRunAsync(step1.Id))!.Status);

		// Add dependency: step3 has a dependent, making it higher priority than step2
		var stepDependent = await _steps.CreateAsync(feat2.Id, "Step Dep");
		await _stepDeps.AddAsync(stepDependent.Id, step3.Id);

		// Now step2 and step3 move to Build (using MCP move or scheduler directly since board is busy)
		// They should both be queued
		step2.WorkflowColumn = WorkflowColumn.Build;
		step3.WorkflowColumn = WorkflowColumn.Build;
		await _dbContext.SaveChangesAsync();

		await _scheduler.RequestStartAsync(step2.Id);
		await _scheduler.RequestStartAsync(step3.Id);

		Assert.Equal(AgentRunStatus.Queued, (await _scheduler.GetCurrentRunAsync(step2.Id))!.Status);
		Assert.Equal(AgentRunStatus.Queued, (await _scheduler.GetCurrentRunAsync(step3.Id))!.Status);

		// Step 1 finishes!
		await _scheduler.OnAgentFinishedAsync(step1.Id);

		// Step 1 is Completed
		Assert.Equal(AgentRunStatus.Completed, (await _scheduler.GetCurrentRunAsync(step1.Id))!.Status);

		// Step 3 had higher priority score due to unblocking dependent step -> it should have started!
		var run3 = await _scheduler.GetCurrentRunAsync(step3.Id);
		Assert.Equal(AgentRunStatus.Working, run3!.Status);

		// Step 2 should remain queued
		var run2 = await _scheduler.GetCurrentRunAsync(step2.Id);
		Assert.Equal(AgentRunStatus.Queued, run2!.Status);
	}

	[Fact]
	public async Task FailureThresholdSkip_DoesNotAutoStartStepCrossingBoardThreshold()
	{
		// Board with AgentReviewFailThreshold = 2
		var board = await _boards.CreateAsync("Board 1", "/repos/b1", agentReviewFailThreshold: 2);
		await CreateAndAssignAgentAsync(board.Id, ColumnScope.StepBuild);

		var feat = await _features.CreateAsync(board.Id, "Feat 1");
		var step = await _steps.CreateAsync(feat.Id, "Step 1");
		step.WorkflowColumn = WorkflowColumn.Build;
		step.AgentReviewFailCount = 2; // Hit threshold!
		await _dbContext.SaveChangesAsync();

		await _scheduler.RequestStartAsync(step.Id);

		// Should be queued/flagged but NOT started (process runner not invoked)
		var run = await _scheduler.GetCurrentRunAsync(step.Id);
		Assert.NotNull(run);
		Assert.Equal(AgentRunStatus.Queued, run.Status);
		Assert.Empty(_testProcessRunner.StartedAgents);
	}

	[Fact]
	public async Task FailureThresholdSkip_HumanReviewFailThresholdExceeded_SkipsAutoStart()
	{
		var board = await _boards.CreateAsync("Board 1", "/repos/b1", humanReviewFailThreshold: 1);
		await CreateAndAssignAgentAsync(board.Id, ColumnScope.StepBuild);

		var feat = await _features.CreateAsync(board.Id, "Feat 1");
		var step = await _steps.CreateAsync(feat.Id, "Step 1");
		step.WorkflowColumn = WorkflowColumn.Build;
		step.HumanReviewFailCount = 1; // Hit threshold!
		await _dbContext.SaveChangesAsync();

		await _scheduler.RequestStartAsync(step.Id);

		var run = await _scheduler.GetCurrentRunAsync(step.Id);
		Assert.NotNull(run);
		Assert.Equal(AgentRunStatus.Queued, run.Status);
		Assert.Empty(_testProcessRunner.StartedAgents);
	}

	[Fact]
	public async Task UI_HardBlocksMove_WhenAgentIsCurrentlyRunning()
	{
		var board = await _boards.CreateAsync("Board 1", "/repos/b1");
		await CreateAndAssignAgentAsync(board.Id, ColumnScope.StepBuild);
		await CreateAndAssignAgentAsync(board.Id, ColumnScope.StepAgentReview);

		var feat = await _features.CreateAsync(board.Id, "Feat 1");
		var step = await _steps.CreateAsync(feat.Id, "Step 1");

		await _featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);
		await _stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Build);

		// Agent is now Working
		Assert.True(await _scheduler.IsAgentRunningAsync(step.Id));

		// UI move (isMcpMove = false) must throw InvalidOperationException
		var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
			_stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.AgentReview));
		Assert.Equal("Cannot move a step while an agent is running.", ex.Message);

		// GetAllowedMovesAsync must return empty list for UI
		var allowed = await _stepOrchestrator.GetAllowedMovesAsync(step.Id);
		Assert.Empty(allowed);
	}

	[Fact]
	public async Task MCP_AllowsMoveWhileAgentRunning_AndSetsSoftBlockedStatus()
	{
		var board = await _boards.CreateAsync("Board 1", "/repos/b1");
		await CreateAndAssignAgentAsync(board.Id, ColumnScope.StepBuild);
		await CreateAndAssignAgentAsync(board.Id, ColumnScope.StepAgentReview);

		var feat = await _features.CreateAsync(board.Id, "Feat 1");
		var step = await _steps.CreateAsync(feat.Id, "Step 1");

		await _featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);
		await _stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Build);

		var buildRun = await _scheduler.GetCurrentRunAsync(step.Id);
		Assert.NotNull(buildRun);
		Assert.Equal(AgentRunStatus.Working, buildRun.Status);

		// MCP move succeeds even though agent is still running
		var movedStep = await _stepOrchestrator.MoveMcpAsync(step.Id, WorkflowColumn.AgentReview);
		Assert.Equal(WorkflowColumn.AgentReview, movedStep.WorkflowColumn);

		// A new run with Blocked status is created
		var currentRun = await _scheduler.GetCurrentRunAsync(step.Id);
		Assert.NotNull(currentRun);
		Assert.Equal(AgentRunStatus.Blocked, currentRun.Status);

		// When outgoing agent finishes, the blocked run is unblocked to Queued and then started
		await _scheduler.OnAgentFinishedAsync(step.Id);

		// Outgoing build run is Completed
		var refreshedBuildRun = await _dbContext.AgentRuns.FindAsync(buildRun.Id);
		Assert.Equal(AgentRunStatus.Completed, refreshedBuildRun!.Status);

		// New run transitioned from Blocked -> Queued -> Working!
		var newRun = await _scheduler.GetCurrentRunAsync(step.Id);
		Assert.Equal(AgentRunStatus.Working, newRun!.Status);
	}

	[Fact]
	public async Task StopAgentAsync_StopsRunningAgent_AndTriggersQueue()
	{
		var board = await _boards.CreateAsync("Board 1", "/repos/b1");
		await CreateAndAssignAgentAsync(board.Id, ColumnScope.StepBuild);

		var feat = await _features.CreateAsync(board.Id, "Feat 1");
		var step1 = await _steps.CreateAsync(feat.Id, "Step 1");
		var step2 = await _steps.CreateAsync(feat.Id, "Step 2");

		await _featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);
		await _stepOrchestrator.MoveAsync(step1.Id, WorkflowColumn.Build);

		var run1 = await _scheduler.GetCurrentRunAsync(step1.Id);
		Assert.Equal(AgentRunStatus.Working, run1!.Status);
		Assert.NotNull(run1.ProcessId);

		// Queue step2
		step2.WorkflowColumn = WorkflowColumn.Build;
		await _dbContext.SaveChangesAsync();
		await _scheduler.RequestStartAsync(step2.Id);
		Assert.Equal(AgentRunStatus.Queued, (await _scheduler.GetCurrentRunAsync(step2.Id))!.Status);

		// Stop step1 agent
		await _scheduler.StopAgentAsync(step1.Id);

		// Step 1 run is Stopped, process runner was requested to stop
		var stoppedRun1 = await _dbContext.AgentRuns.FindAsync(run1.Id);
		Assert.Equal(AgentRunStatus.Stopped, stoppedRun1!.Status);
		Assert.Contains(run1.ProcessId.Value, _testProcessRunner.StoppedProcessIds);

		// Step 2 automatically started!
		var run2 = await _scheduler.GetCurrentRunAsync(step2.Id);
		Assert.Equal(AgentRunStatus.Working, run2!.Status);
	}

	[Fact]
	public async Task OnAgentFinishedAsync_RecordsUsageOnAgentRun_AndUpdatesStepEntity()
	{
		var board = await _boards.CreateAsync("Board 1", "/repos/b1");
		await CreateAndAssignAgentAsync(board.Id, ColumnScope.StepBuild);

		var feat = await _features.CreateAsync(board.Id, "Feat 1");
		var step = await _steps.CreateAsync(feat.Id, "Step 1");

		await _featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);
		await _stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Build);

		var run = await _scheduler.GetCurrentRunAsync(step.Id);
		Assert.NotNull(run);

		// Finish agent with specific token and time usage
		await _scheduler.OnAgentFinishedAsync(step.Id, 1500, TimeSpan.FromMinutes(3));

		var refreshedRun = await _dbContext.AgentRuns.FindAsync(run.Id);
		Assert.Equal(AgentRunStatus.Completed, refreshedRun!.Status);
		Assert.Equal(1500, refreshedRun.TokensUsed);
		Assert.Equal(TimeSpan.FromMinutes(3), refreshedRun.TimeSpent);

		var refreshedStep = await _dbContext.Steps.FindAsync(step.Id);
		Assert.Equal(1500, refreshedStep!.TokensUsed);
		Assert.Equal(TimeSpan.FromMinutes(3), refreshedStep.TimeSpent);
	}

	[Fact]
	public async Task OnAgentFinishedAsync_AccumulatesTokensAndTimeAcrossMultipleRuns()
	{
		var board = await _boards.CreateAsync("Board 1", "/repos/b1");
		await CreateAndAssignAgentAsync(board.Id, ColumnScope.StepBuild);
		await CreateAndAssignAgentAsync(board.Id, ColumnScope.StepAgentReview);

		var feat = await _features.CreateAsync(board.Id, "Feat 1");
		var step = await _steps.CreateAsync(feat.Id, "Step 1");

		// Run 1: In Build
		await _featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);
		await _stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Build);
		await _scheduler.OnAgentFinishedAsync(step.Id, 1000, TimeSpan.FromMinutes(2));

		var stepAfterRun1 = await _dbContext.Steps.FindAsync(step.Id);
		Assert.Equal(1000, stepAfterRun1!.TokensUsed);
		Assert.Equal(TimeSpan.FromMinutes(2), stepAfterRun1.TimeSpent);

		// Run 2: Advance to AgentReview and run again
		await _stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.AgentReview);
		await _scheduler.OnAgentFinishedAsync(step.Id, 2500, TimeSpan.FromMinutes(5));

		var stepAfterRun2 = await _dbContext.Steps.FindAsync(step.Id);
		Assert.Equal(3500, stepAfterRun2!.TokensUsed);
		Assert.Equal(TimeSpan.FromMinutes(7), stepAfterRun2.TimeSpent);
	}

	[Fact]
	public async Task StopAgentAsync_RecordsUsageOnAgentRun_AndUpdatesStepEntity()
	{
		var board = await _boards.CreateAsync("Board 1", "/repos/b1");
		await CreateAndAssignAgentAsync(board.Id, ColumnScope.StepBuild);

		var feat = await _features.CreateAsync(board.Id, "Feat 1");
		var step = await _steps.CreateAsync(feat.Id, "Step 1");

		await _featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);
		await _stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Build);

		var run = await _scheduler.GetCurrentRunAsync(step.Id);
		Assert.NotNull(run);

		await _scheduler.StopAgentAsync(step.Id, 600, TimeSpan.FromSeconds(45));

		var refreshedRun = await _dbContext.AgentRuns.FindAsync(run.Id);
		Assert.Equal(AgentRunStatus.Stopped, refreshedRun!.Status);
		Assert.Equal(600, refreshedRun.TokensUsed);
		Assert.Equal(TimeSpan.FromSeconds(45), refreshedRun.TimeSpent);

		var refreshedStep = await _dbContext.Steps.FindAsync(step.Id);
		Assert.Equal(600, refreshedStep!.TokensUsed);
		Assert.Equal(TimeSpan.FromSeconds(45), refreshedStep.TimeSpent);
	}

	[Fact]
	public async Task RecordRunUsageAsync_DirectlyUpdatesUsage()
	{
		var board = await _boards.CreateAsync("Board 1", "/repos/b1");
		await CreateAndAssignAgentAsync(board.Id, ColumnScope.StepBuild);

		var feat = await _features.CreateAsync(board.Id, "Feat 1");
		var step = await _steps.CreateAsync(feat.Id, "Step 1");

		await _featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);
		await _stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Build);

		await _scheduler.RecordRunUsageAsync(step.Id, 2000, TimeSpan.FromMinutes(4));

		var refreshedStep = await _dbContext.Steps.FindAsync(step.Id);
		Assert.Equal(2000, refreshedStep!.TokensUsed);
		Assert.Equal(TimeSpan.FromMinutes(4), refreshedStep.TimeSpent);
	}
}