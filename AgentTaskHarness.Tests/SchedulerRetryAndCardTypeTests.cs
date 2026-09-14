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

public class SchedulerRetryAndCardTypeTests : IAsyncLifetime
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
	public async Task RetryAgentAsync_ResetsFailedRun_AndStartsAgent()
	{
		var board = await _boards.CreateAsync("Board 1", "/repos/b1");
		await CreateAndAssignAgentAsync(board.Id, ColumnScope.StepBuild);

		var feat = await _features.CreateAsync(board.Id, "Feat 1");
		var step = await _steps.CreateAsync(feat.Id, "Step 1");

		await _featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);
		await _stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Build);

		// Mark run as Failed
		var run = await _scheduler.GetCurrentRunAsync(step.Id);
		Assert.NotNull(run);
		run.Status = AgentRunStatus.Failed;
		run.EndedAt = DateTimeOffset.UtcNow;
		await _dbContext.SaveChangesAsync();

		// Retry
		await _scheduler.RetryAgentAsync(step.Id);

		var retriedRun = await _scheduler.GetCurrentRunAsync(step.Id);
		Assert.NotNull(retriedRun);
		Assert.Equal(AgentRunStatus.Working, retriedRun.Status);
	}

	[Fact]
	public async Task CardType_Overloads_WorkForFeatureAndStep()
	{
		var board = await _boards.CreateAsync("Board 1", "/repos/b1");
		var feat = await _features.CreateAsync(board.Id, "Feat 1");

		// Add a mock Feature run
		_dbContext.AgentRuns.Add(new AgentRun
		{
			CardType = CardType.Feature,
			CardId = feat.Id,
			Status = AgentRunStatus.Working,
			StartedAt = DateTimeOffset.UtcNow,
			ProcessId = 999
		});
		await _dbContext.SaveChangesAsync();

		Assert.True(await _scheduler.IsAgentRunningAsync(CardType.Feature, feat.Id));
		var currentRun = await _scheduler.GetCurrentRunAsync(CardType.Feature, feat.Id);
		Assert.NotNull(currentRun);
		Assert.Equal(AgentRunStatus.Working, currentRun.Status);

		await _scheduler.StopAgentAsync(CardType.Feature, feat.Id, 100, TimeSpan.FromSeconds(10));
		Assert.False(await _scheduler.IsAgentRunningAsync(CardType.Feature, feat.Id));

		var stoppedRun = await _scheduler.GetCurrentRunAsync(CardType.Feature, feat.Id);
		Assert.NotNull(stoppedRun);
		Assert.Equal(AgentRunStatus.Stopped, stoppedRun.Status);
	}
}