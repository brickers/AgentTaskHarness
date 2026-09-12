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

public class SchedulerRetryAndCardTypeTests : IAsyncLifetime
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
	public async Task RetryAgentAsync_ResetsFailedRun_AndStartsAgent()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		await CreateAndAssignAgentAsync(board.Id, ColumnScope.StepBuild);

		var feat = await features.CreateAsync(board.Id, "Feat 1");
		var step = await steps.CreateAsync(feat.Id, "Step 1");

		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Build);

		// Mark run as Failed
		var run = await scheduler.GetCurrentRunAsync(step.Id);
		Assert.NotNull(run);
		run.Status = AgentRunStatus.Failed;
		run.EndedAt = DateTimeOffset.UtcNow;
		await dbContext.SaveChangesAsync();

		// Retry
		await scheduler.RetryAgentAsync(step.Id);

		var retriedRun = await scheduler.GetCurrentRunAsync(step.Id);
		Assert.NotNull(retriedRun);
		Assert.Equal(AgentRunStatus.Working, retriedRun.Status);
	}

	[Fact]
	public async Task CardType_Overloads_WorkForFeatureAndStep()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var feat = await features.CreateAsync(board.Id, "Feat 1");

		// Add a mock Feature run
		dbContext.AgentRuns.Add(new AgentRun
		{
			CardType = CardType.Feature,
			CardId = feat.Id,
			Status = AgentRunStatus.Working,
			StartedAt = DateTimeOffset.UtcNow,
			ProcessId = 999
		});
		await dbContext.SaveChangesAsync();

		Assert.True(await scheduler.IsAgentRunningAsync(CardType.Feature, feat.Id));
		var currentRun = await scheduler.GetCurrentRunAsync(CardType.Feature, feat.Id);
		Assert.NotNull(currentRun);
		Assert.Equal(AgentRunStatus.Working, currentRun.Status);

		await scheduler.StopAgentAsync(CardType.Feature, feat.Id, tokensUsed: 100, timeSpent: TimeSpan.FromSeconds(10));
		Assert.False(await scheduler.IsAgentRunningAsync(CardType.Feature, feat.Id));

		var stoppedRun = await scheduler.GetCurrentRunAsync(CardType.Feature, feat.Id);
		Assert.NotNull(stoppedRun);
		Assert.Equal(AgentRunStatus.Stopped, stoppedRun.Status);
	}
}
