using AgentTaskHarness.Application.Boards;
using AgentTaskHarness.Application.Features;
using AgentTaskHarness.Application.Steps;
using AgentTaskHarness.Application.Usage;
using AgentTaskHarness.Domain.Entities;
using AgentTaskHarness.Domain.Enums;
using AgentTaskHarness.Infrastructure.Agents;
using AgentTaskHarness.Infrastructure.Git;
using AgentTaskHarness.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentTaskHarness.Tests;

public class UsageTrackingServiceTests : IAsyncLifetime
{
	private readonly SqliteConnection _connection = new("Data Source=:memory:");
	private BoardService _boardService = null!;
	private AppDbContext _dbContext = null!;
	private FeatureService _featureService = null!;
	private StepService _stepService = null!;
	private UsageTrackingService _usageService = null!;

	public async Task InitializeAsync()
	{
		await _connection.OpenAsync();
		_dbContext = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
		await _dbContext.Database.MigrateAsync();

		_boardService = new BoardService(_dbContext);
		_featureService = new FeatureService(_dbContext);
		_stepService = new StepService(_dbContext);
		_usageService = new UsageTrackingService(_dbContext);
	}

	public async Task DisposeAsync()
	{
		await _dbContext.DisposeAsync();
		await _connection.DisposeAsync();
	}

	[Fact]
	public async Task GetFeatureSummaryAsync_AggregatesTokensAndTime_AcrossMultipleSteps()
	{
		var board = await _boardService.CreateAsync("Test Board", "/repos/test", 2);
		var feat = await _featureService.CreateAsync(board.Id, "Feat 1");

		var step1 = await _stepService.CreateAsync(feat.Id, "Step 1");
		step1.TokensUsed = 1200;
		step1.TimeSpent = TimeSpan.FromMinutes(5);

		var step2 = await _stepService.CreateAsync(feat.Id, "Step 2");
		step2.TokensUsed = 2300;
		step2.TimeSpent = TimeSpan.FromMinutes(10);

		await _dbContext.SaveChangesAsync();

		var summary = await _usageService.GetFeatureSummaryAsync(feat.Id);

		Assert.Equal(feat.Id, summary.FeatureId);
		Assert.Equal("Feat 1", summary.Title);
		Assert.Equal(3500, summary.TotalTokensUsed);
		Assert.Equal(TimeSpan.FromMinutes(15), summary.TotalTimeSpent);
		Assert.Equal(3500, summary.StepTokensUsed);
		Assert.Equal(TimeSpan.FromMinutes(15), summary.StepTimeSpent);
		Assert.Equal(0, summary.FeatureTokensUsed);
		Assert.Equal(TimeSpan.Zero, summary.FeatureTimeSpent);
		Assert.Equal(2, summary.StepCount);
		Assert.Equal(2, summary.StepSummaries.Count);

		var step1Summary = summary.StepSummaries.Single(s => s.StepId == step1.Id);
		Assert.Equal(1200, step1Summary.TokensUsed);
		Assert.Equal(TimeSpan.FromMinutes(5), step1Summary.TimeSpent);

		var step2Summary = summary.StepSummaries.Single(s => s.StepId == step2.Id);
		Assert.Equal(2300, step2Summary.TokensUsed);
		Assert.Equal(TimeSpan.FromMinutes(10), step2Summary.TimeSpent);
	}

	[Fact]
	public async Task GetFeatureSummary_Alias_ReturnsSameResultAsAsync()
	{
		var board = await _boardService.CreateAsync("Test Board", "/repos/test", 2);
		var feat = await _featureService.CreateAsync(board.Id, "Feat 1");
		var step = await _stepService.CreateAsync(feat.Id, "Step 1");
		step.TokensUsed = 500;
		step.TimeSpent = TimeSpan.FromSeconds(30);
		await _dbContext.SaveChangesAsync();

		var summary = await _usageService.GetFeatureSummary(feat.Id);

		Assert.Equal(500, summary.TotalTokensUsed);
		Assert.Equal(TimeSpan.FromSeconds(30), summary.TotalTimeSpent);
	}

	[Fact]
	public async Task GetFeatureSummaryAsync_IncludesFeatureLevelAgentRuns()
	{
		var board = await _boardService.CreateAsync("Test Board", "/repos/test", 2);
		var feat = await _featureService.CreateAsync(board.Id, "Feat 1");

		var step = await _stepService.CreateAsync(feat.Id, "Step 1");
		step.TokensUsed = 1000;
		step.TimeSpent = TimeSpan.FromMinutes(2);

		// Feature-level AgentRun (e.g. Feature Agent Review run)
		_dbContext.AgentRuns.Add(new AgentRun
		{
			CardType = CardType.Feature,
			CardId = feat.Id,
			Status = AgentRunStatus.Completed,
			TokensUsed = 800,
			TimeSpent = TimeSpan.FromMinutes(1),
			StartedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
			EndedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
		});

		await _dbContext.SaveChangesAsync();

		var summary = await _usageService.GetFeatureSummaryAsync(feat.Id);

		Assert.Equal(1800, summary.TotalTokensUsed);
		Assert.Equal(TimeSpan.FromMinutes(3), summary.TotalTimeSpent);
		Assert.Equal(1000, summary.StepTokensUsed);
		Assert.Equal(TimeSpan.FromMinutes(2), summary.StepTimeSpent);
		Assert.Equal(800, summary.FeatureTokensUsed);
		Assert.Equal(TimeSpan.FromMinutes(1), summary.FeatureTimeSpent);
	}

	[Fact]
	public async Task GetFeatureSummaryAsync_ThrowsKeyNotFound_WhenFeatureDoesNotExist()
	{
		var missingId = Guid.NewGuid();
		await Assert.ThrowsAsync<KeyNotFoundException>(() =>
			_usageService.GetFeatureSummaryAsync(missingId));
	}

	[Fact]
	public async Task GetStepSummaryAsync_ReturnsStepUsageAndRunCount()
	{
		var board = await _boardService.CreateAsync("Test Board", "/repos/test", 2);
		var feat = await _featureService.CreateAsync(board.Id, "Feat 1");
		var step = await _stepService.CreateAsync(feat.Id, "Step 1");
		step.TokensUsed = 750;
		step.TimeSpent = TimeSpan.FromSeconds(45);

		_dbContext.AgentRuns.Add(new AgentRun
		{
			CardType = CardType.Step,
			CardId = step.Id,
			Status = AgentRunStatus.Completed,
			TokensUsed = 400,
			TimeSpent = TimeSpan.FromSeconds(25),
			StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
			EndedAt = DateTimeOffset.UtcNow.AddMinutes(-4)
		});

		_dbContext.AgentRuns.Add(new AgentRun
		{
			CardType = CardType.Step,
			CardId = step.Id,
			Status = AgentRunStatus.Completed,
			TokensUsed = 350,
			TimeSpent = TimeSpan.FromSeconds(20),
			StartedAt = DateTimeOffset.UtcNow.AddMinutes(-3),
			EndedAt = DateTimeOffset.UtcNow.AddMinutes(-2)
		});

		await _dbContext.SaveChangesAsync();

		var stepSummary = await _usageService.GetStepSummaryAsync(step.Id);

		Assert.Equal(step.Id, stepSummary.StepId);
		Assert.Equal(feat.Id, stepSummary.FeatureId);
		Assert.Equal("Step 1", stepSummary.Title);
		Assert.Equal(750, stepSummary.TokensUsed);
		Assert.Equal(TimeSpan.FromSeconds(45), stepSummary.TimeSpent);
		Assert.Equal(2, stepSummary.RunCount);
	}

	[Fact]
	public async Task GetBoardSummaryAsync_AggregatesAcrossAllFeaturesAndSteps()
	{
		var board = await _boardService.CreateAsync("Main Board", "/repos/main", 2);
		var feat1 = await _featureService.CreateAsync(board.Id, "Feat 1");
		var feat2 = await _featureService.CreateAsync(board.Id, "Feat 2");

		var step1 = await _stepService.CreateAsync(feat1.Id, "Step 1");
		step1.TokensUsed = 1000;
		step1.TimeSpent = TimeSpan.FromMinutes(2);

		var step2 = await _stepService.CreateAsync(feat2.Id, "Step 2");
		step2.TokensUsed = 2500;
		step2.TimeSpent = TimeSpan.FromMinutes(4);

		await _dbContext.SaveChangesAsync();

		var boardSummary = await _usageService.GetBoardSummaryAsync(board.Id);

		Assert.Equal(board.Id, boardSummary.BoardId);
		Assert.Equal("Main Board", boardSummary.BoardName);
		Assert.Equal(3500, boardSummary.TotalTokensUsed);
		Assert.Equal(TimeSpan.FromMinutes(6), boardSummary.TotalTimeSpent);
		Assert.Equal(2, boardSummary.FeatureCount);
		Assert.Equal(2, boardSummary.StepCount);
		Assert.Equal(2, boardSummary.FeatureSummaries.Count);
	}

	[Fact]
	public async Task GetFeatureSummariesForBoardAsync_ReturnsMapForBoard()
	{
		var board = await _boardService.CreateAsync("Test Board", "/repos/test", 2);
		var feat1 = await _featureService.CreateAsync(board.Id, "Feat 1");
		var feat2 = await _featureService.CreateAsync(board.Id, "Feat 2");

		var map = await _usageService.GetFeatureSummariesForBoardAsync(board.Id);

		Assert.Equal(2, map.Count);
		Assert.True(map.ContainsKey(feat1.Id));
		Assert.True(map.ContainsKey(feat2.Id));
	}

	[Fact]
	public async Task RecordStepUsageAsync_IncrementsStepTotals()
	{
		var board = await _boardService.CreateAsync("Test Board", "/repos/test", 2);
		var feat = await _featureService.CreateAsync(board.Id, "Feat 1");
		var step = await _stepService.CreateAsync(feat.Id, "Step 1");

		await _usageService.RecordStepUsageAsync(step.Id, 500, TimeSpan.FromSeconds(30));

		var refreshed = await _dbContext.Steps.FindAsync(step.Id);
		Assert.Equal(500, refreshed!.TokensUsed);
		Assert.Equal(TimeSpan.FromSeconds(30), refreshed.TimeSpent);

		await _usageService.RecordStepUsageAsync(step.Id, 250, TimeSpan.FromSeconds(15));
		refreshed = await _dbContext.Steps.FindAsync(step.Id);
		Assert.Equal(750, refreshed!.TokensUsed);
		Assert.Equal(TimeSpan.FromSeconds(45), refreshed.TimeSpent);
	}

	[Fact]
	public async Task RecordRunUsageAsync_UpdatesRunAndRollsUpToStep()
	{
		var board = await _boardService.CreateAsync("Test Board", "/repos/test", 2);
		var feat = await _featureService.CreateAsync(board.Id, "Feat 1");
		var step = await _stepService.CreateAsync(feat.Id, "Step 1");

		var run = new AgentRun
		{
			CardType = CardType.Step,
			CardId = step.Id,
			Status = AgentRunStatus.Working,
			StartedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
			TokensUsed = 0,
			TimeSpent = TimeSpan.Zero
		};
		_dbContext.AgentRuns.Add(run);
		await _dbContext.SaveChangesAsync();

		await _usageService.RecordRunUsageAsync(run.Id, 1200, TimeSpan.FromMinutes(2));

		var refreshedRun = await _dbContext.AgentRuns.FindAsync(run.Id);
		Assert.Equal(1200, refreshedRun!.TokensUsed);
		Assert.Equal(TimeSpan.FromMinutes(2), refreshedRun.TimeSpent);

		var refreshedStep = await _dbContext.Steps.FindAsync(step.Id);
		Assert.Equal(1200, refreshedStep!.TokensUsed);
		Assert.Equal(TimeSpan.FromMinutes(2), refreshedStep.TimeSpent);
	}

	[Fact]
	public async Task GetQualitySignalsAsync_ComputesReviewFailuresAndReworkIndicators()
	{
		var board = await _boardService.CreateAsync("Quality Board", "/repos/quality", 2);
		var feat = await _featureService.CreateAsync(board.Id, "Feat 1");
		feat.AgentReviewFailCount = 1;

		var step1 = await _stepService.CreateAsync(feat.Id, "Step 1");
		step1.AgentReviewFailCount = 2;
		step1.HumanReviewFailCount = 1;

		var step2 = await _stepService.CreateAsync(feat.Id, "Step 2");
		step2.HumanReviewFailCount = 1;

		await _dbContext.SaveChangesAsync();

		var signals = await _usageService.GetQualitySignalsAsync(feat.Id);

		Assert.Equal(feat.Id, signals.CardId);
		Assert.Equal(CardType.Feature, signals.CardType);
		Assert.Equal(3, signals.AgentReviewFailCount); // 1 on feat + 2 on step1
		Assert.Equal(2, signals.HumanReviewFailCount); // 1 on step1 + 1 on step2
		Assert.Equal(5, signals.TotalReviewFailures);
		Assert.True(signals.IsRework);
		Assert.Equal(2.5, signals.ChurnRatio); // 5 fails / 2 steps = 2.5
	}

	[Theory]
	[InlineData("tokens_used: 1500, time_spent: 45s", 1500, 45)]
	[InlineData("Total tokens: 3200, duration: 2m", 3200, 120)]
	[InlineData("{\"total_tokens\": 4500, \"duration_seconds\": 60}", 4500, 60)]
	public void CopilotCliProcessRunner_TryParseUsage_ParsesExpectedValues(string text, long expectedTokens,
		int expectedSeconds)
	{
		var parsed = CopilotCliProcessRunner.TryParseUsage(text, out var tokens, out var duration);
		Assert.True(parsed);
		Assert.Equal(expectedTokens, tokens);
		Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), duration);
	}

	[Fact]
	public void GitGuardShimWriter_GeneratesDirectory_AllowListingReadOnlyCommands()
	{
		var writer = new GitGuardShimWriter();
		var dir = writer.CreateGitGuardShimDirectory();

		try
		{
			Assert.True(Directory.Exists(dir));
			Assert.Contains("diff", GitGuardShimWriter.AllowedReadOnlyCommands);
			Assert.Contains("status", GitGuardShimWriter.AllowedReadOnlyCommands);
			Assert.Contains("log", GitGuardShimWriter.AllowedReadOnlyCommands);
			Assert.DoesNotContain("commit", GitGuardShimWriter.AllowedReadOnlyCommands);
			Assert.DoesNotContain("push", GitGuardShimWriter.AllowedReadOnlyCommands);
		}
		finally
		{
			writer.CleanupShimDirectory(dir);
			Assert.False(Directory.Exists(dir));
		}
	}
}