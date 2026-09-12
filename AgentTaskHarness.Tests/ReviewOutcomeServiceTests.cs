using AgentTaskHarness.Application.Boards;
using AgentTaskHarness.Application.Features;
using AgentTaskHarness.Application.Reviews;
using AgentTaskHarness.Application.Steps;
using AgentTaskHarness.Domain.Entities;
using AgentTaskHarness.Domain.Enums;
using AgentTaskHarness.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AgentTaskHarness.Tests;

public class ReviewOutcomeServiceTests : IAsyncLifetime
{
	private readonly SqliteConnection _connection = new("Data Source=:memory:");
	private BoardService _boards = null!;
	private AppDbContext _dbContext = null!;
	private FeatureService _features = null!;
	private ReviewOutcomeService _reviewOutcomeService = null!;
	private StepService _steps = null!;

	public async Task InitializeAsync()
	{
		await _connection.OpenAsync();
		_dbContext = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
		await _dbContext.Database.MigrateAsync();
		_boards = new BoardService(_dbContext);
		_features = new FeatureService(_dbContext);
		_steps = new StepService(_dbContext);
		_reviewOutcomeService = new ReviewOutcomeService(_dbContext);
	}

	public async Task DisposeAsync()
	{
		await _dbContext.DisposeAsync();
		await _connection.DisposeAsync();
	}

	[Fact]
	public void RecordReviewFailure_IncrementsAgentAndHumanCounters()
	{
		var feature = new Feature();
		var step = new Step();

		_reviewOutcomeService.RecordReviewFailure(feature, WorkflowColumn.AgentReview);
		Assert.Equal(1, feature.AgentReviewFailCount);
		Assert.Equal(0, feature.HumanReviewFailCount);

		_reviewOutcomeService.RecordReviewFailure(feature, WorkflowColumn.HumanReview);
		Assert.Equal(1, feature.AgentReviewFailCount);
		Assert.Equal(1, feature.HumanReviewFailCount);

		_reviewOutcomeService.RecordReviewFailure(step, WorkflowColumn.AgentReview);
		Assert.Equal(1, step.AgentReviewFailCount);
		Assert.Equal(0, step.HumanReviewFailCount);

		_reviewOutcomeService.RecordReviewFailure(step, WorkflowColumn.HumanReview);
		Assert.Equal(1, step.AgentReviewFailCount);
		Assert.Equal(1, step.HumanReviewFailCount);
	}

	[Fact]
	public void RecordReviewFailure_ThrowsOnInvalidColumn()
	{
		var feature = new Feature();
		Assert.Throws<ArgumentException>(() =>
			_reviewOutcomeService.RecordReviewFailure(feature, WorkflowColumn.Build));
	}

	[Fact]
	public void HandleTransition_OnlyIncrementsWhenReviewColumnTransitionsToBuild()
	{
		var feature = new Feature { WorkflowColumn = WorkflowColumn.AgentReview };

		// AgentReview -> Build increments
		var handled = _reviewOutcomeService.HandleTransition(feature, WorkflowColumn.AgentReview, WorkflowColumn.Build);
		Assert.True(handled);
		Assert.Equal(1, feature.AgentReviewFailCount);

		// HumanReview -> Build increments
		handled = _reviewOutcomeService.HandleTransition(feature, WorkflowColumn.HumanReview, WorkflowColumn.Build);
		Assert.True(handled);
		Assert.Equal(1, feature.HumanReviewFailCount);

		// AgentReview -> Backlog does not increment
		handled = _reviewOutcomeService.HandleTransition(feature, WorkflowColumn.AgentReview, WorkflowColumn.Backlog);
		Assert.False(handled);
		Assert.Equal(1, feature.AgentReviewFailCount);

		// Build -> AgentReview does not increment
		handled = _reviewOutcomeService.HandleTransition(feature, WorkflowColumn.Build, WorkflowColumn.AgentReview);
		Assert.False(handled);
	}

	[Fact]
	public void IsRework_DerivedIndicator_TrueOnlyWhenInBuildAndHasFailures()
	{
		var feature = new Feature
		{
			WorkflowColumn = WorkflowColumn.Build,
			AgentReviewFailCount = 1
		};
		Assert.True(_reviewOutcomeService.IsRework(feature));

		// Moving to AgentReview -> no longer rework
		feature.WorkflowColumn = WorkflowColumn.AgentReview;
		Assert.False(_reviewOutcomeService.IsRework(feature));

		// In Backlog with failures -> not rework
		feature.WorkflowColumn = WorkflowColumn.Backlog;
		Assert.False(_reviewOutcomeService.IsRework(feature));

		// In Build with zero failures -> not rework
		feature.WorkflowColumn = WorkflowColumn.Build;
		feature.AgentReviewFailCount = 0;
		feature.HumanReviewFailCount = 0;
		Assert.False(_reviewOutcomeService.IsRework(feature));

		// Human review failure in Build -> rework
		feature.HumanReviewFailCount = 1;
		Assert.True(_reviewOutcomeService.IsRework(feature));
	}

	[Fact]
	public void HasIssue_TrueWhenEitherCounterReachesThreshold()
	{
		var board = new Board
		{
			AgentReviewFailThreshold = 3,
			HumanReviewFailThreshold = 2
		};

		var feature = new Feature
		{
			AgentReviewFailCount = 2,
			HumanReviewFailCount = 1
		};
		Assert.False(_reviewOutcomeService.HasIssue(feature, board));

		// Reaches agent threshold
		feature.AgentReviewFailCount = 3;
		Assert.True(_reviewOutcomeService.HasIssue(feature, board));

		// Reset agent below, but human reaches threshold
		feature.AgentReviewFailCount = 1;
		feature.HumanReviewFailCount = 2;
		Assert.True(_reviewOutcomeService.HasIssue(feature, board));
	}

	[Fact]
	public async Task RecordReviewFailureAsync_PersistsToDatabase()
	{
		var board = await _boards.CreateAsync("Board 1", "/repos/b1");
		var feat = await _features.CreateAsync(board.Id, "Feat 1");
		var step = await _steps.CreateAsync(feat.Id, "Step 1");

		await _reviewOutcomeService.RecordReviewFailureAsync(CardType.Feature, feat.Id, WorkflowColumn.AgentReview);
		await _reviewOutcomeService.RecordReviewFailureAsync(CardType.Step, step.Id, WorkflowColumn.HumanReview);

		var refreshedFeat = await _features.GetByIdAsync(feat.Id);
		var refreshedStep = await _steps.GetByIdAsync(step.Id);

		Assert.Equal(1, refreshedFeat!.AgentReviewFailCount);
		Assert.Equal(0, refreshedFeat.HumanReviewFailCount);
		Assert.Equal(0, refreshedStep!.AgentReviewFailCount);
		Assert.Equal(1, refreshedStep.HumanReviewFailCount);
	}

	[Fact]
	public async Task GetStatusForFeatureAsync_And_StepAsync_ComputesCorrectStatus()
	{
		var board = await _boards.CreateAsync(
			"Board 1",
			"/repos/b1",
			agentReviewFailThreshold: 2,
			humanReviewFailThreshold: 3);

		var feat = await _features.CreateAsync(board.Id, "Feat 1");
		feat.WorkflowColumn = WorkflowColumn.Build;
		feat.AgentReviewFailCount = 2; // threshold met!
		await _dbContext.SaveChangesAsync();

		var status = await _reviewOutcomeService.GetStatusForFeatureAsync(feat.Id);

		Assert.True(status.IsRework);
		Assert.True(status.HasIssue);
		Assert.True(status.AgentReviewThresholdCrossed);
		Assert.False(status.HumanReviewThresholdCrossed);
		Assert.Equal(2, status.AgentReviewFailCount);
		Assert.Equal(0, status.HumanReviewFailCount);
		Assert.Equal(2, status.AgentReviewFailThreshold);
		Assert.Equal(3, status.HumanReviewFailThreshold);

		var step = await _steps.CreateAsync(feat.Id, "Step 1");
		step.WorkflowColumn = WorkflowColumn.Build;
		step.HumanReviewFailCount = 1;
		await _dbContext.SaveChangesAsync();

		var stepStatus = await _reviewOutcomeService.GetStatusForStepAsync(step.Id);
		Assert.True(stepStatus.IsRework);
		Assert.False(stepStatus.HasIssue);
		Assert.Equal(1, stepStatus.HumanReviewFailCount);
	}
}