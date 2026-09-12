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
	private readonly SqliteConnection connection = new("Data Source=:memory:");
	private AppDbContext dbContext = null!;
	private BoardService boards = null!;
	private FeatureService features = null!;
	private StepService steps = null!;
	private ReviewOutcomeService reviewOutcomeService = null!;

	public async Task InitializeAsync()
	{
		await connection.OpenAsync();
		dbContext = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
		await dbContext.Database.MigrateAsync();
		boards = new BoardService(dbContext);
		features = new FeatureService(dbContext);
		steps = new StepService(dbContext);
		reviewOutcomeService = new ReviewOutcomeService(dbContext);
	}

	public async Task DisposeAsync()
	{
		await dbContext.DisposeAsync();
		await connection.DisposeAsync();
	}

	[Fact]
	public void RecordReviewFailure_IncrementsAgentAndHumanCounters()
	{
		var feature = new Feature();
		var step = new Step();

		reviewOutcomeService.RecordReviewFailure(feature, WorkflowColumn.AgentReview);
		Assert.Equal(1, feature.AgentReviewFailCount);
		Assert.Equal(0, feature.HumanReviewFailCount);

		reviewOutcomeService.RecordReviewFailure(feature, WorkflowColumn.HumanReview);
		Assert.Equal(1, feature.AgentReviewFailCount);
		Assert.Equal(1, feature.HumanReviewFailCount);

		reviewOutcomeService.RecordReviewFailure(step, WorkflowColumn.AgentReview);
		Assert.Equal(1, step.AgentReviewFailCount);
		Assert.Equal(0, step.HumanReviewFailCount);

		reviewOutcomeService.RecordReviewFailure(step, WorkflowColumn.HumanReview);
		Assert.Equal(1, step.AgentReviewFailCount);
		Assert.Equal(1, step.HumanReviewFailCount);
	}

	[Fact]
	public void RecordReviewFailure_ThrowsOnInvalidColumn()
	{
		var feature = new Feature();
		Assert.Throws<ArgumentException>(() =>
			reviewOutcomeService.RecordReviewFailure(feature, WorkflowColumn.Build));
	}

	[Fact]
	public void HandleTransition_OnlyIncrementsWhenReviewColumnTransitionsToBuild()
	{
		var feature = new Feature { WorkflowColumn = WorkflowColumn.AgentReview };

		// AgentReview -> Build increments
		var handled = reviewOutcomeService.HandleTransition(feature, WorkflowColumn.AgentReview, WorkflowColumn.Build);
		Assert.True(handled);
		Assert.Equal(1, feature.AgentReviewFailCount);

		// HumanReview -> Build increments
		handled = reviewOutcomeService.HandleTransition(feature, WorkflowColumn.HumanReview, WorkflowColumn.Build);
		Assert.True(handled);
		Assert.Equal(1, feature.HumanReviewFailCount);

		// AgentReview -> Backlog does not increment
		handled = reviewOutcomeService.HandleTransition(feature, WorkflowColumn.AgentReview, WorkflowColumn.Backlog);
		Assert.False(handled);
		Assert.Equal(1, feature.AgentReviewFailCount);

		// Build -> AgentReview does not increment
		handled = reviewOutcomeService.HandleTransition(feature, WorkflowColumn.Build, WorkflowColumn.AgentReview);
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
		Assert.True(reviewOutcomeService.IsRework(feature));

		// Moving to AgentReview -> no longer rework
		feature.WorkflowColumn = WorkflowColumn.AgentReview;
		Assert.False(reviewOutcomeService.IsRework(feature));

		// In Backlog with failures -> not rework
		feature.WorkflowColumn = WorkflowColumn.Backlog;
		Assert.False(reviewOutcomeService.IsRework(feature));

		// In Build with zero failures -> not rework
		feature.WorkflowColumn = WorkflowColumn.Build;
		feature.AgentReviewFailCount = 0;
		feature.HumanReviewFailCount = 0;
		Assert.False(reviewOutcomeService.IsRework(feature));

		// Human review failure in Build -> rework
		feature.HumanReviewFailCount = 1;
		Assert.True(reviewOutcomeService.IsRework(feature));
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
		Assert.False(reviewOutcomeService.HasIssue(feature, board));

		// Reaches agent threshold
		feature.AgentReviewFailCount = 3;
		Assert.True(reviewOutcomeService.HasIssue(feature, board));

		// Reset agent below, but human reaches threshold
		feature.AgentReviewFailCount = 1;
		feature.HumanReviewFailCount = 2;
		Assert.True(reviewOutcomeService.HasIssue(feature, board));
	}

	[Fact]
	public async Task RecordReviewFailureAsync_PersistsToDatabase()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var feat = await features.CreateAsync(board.Id, "Feat 1");
		var step = await steps.CreateAsync(feat.Id, "Step 1");

		await reviewOutcomeService.RecordReviewFailureAsync(CardType.Feature, feat.Id, WorkflowColumn.AgentReview);
		await reviewOutcomeService.RecordReviewFailureAsync(CardType.Step, step.Id, WorkflowColumn.HumanReview);

		var refreshedFeat = await features.GetByIdAsync(feat.Id);
		var refreshedStep = await steps.GetByIdAsync(step.Id);

		Assert.Equal(1, refreshedFeat!.AgentReviewFailCount);
		Assert.Equal(0, refreshedFeat.HumanReviewFailCount);
		Assert.Equal(0, refreshedStep!.AgentReviewFailCount);
		Assert.Equal(1, refreshedStep.HumanReviewFailCount);
	}

	[Fact]
	public async Task GetStatusForFeatureAsync_And_StepAsync_ComputesCorrectStatus()
	{
		var board = await boards.CreateAsync(
			"Board 1",
			"/repos/b1",
			concurrencyLimit: 1,
			agentReviewFailThreshold: 2,
			humanReviewFailThreshold: 3);

		var feat = await features.CreateAsync(board.Id, "Feat 1");
		feat.WorkflowColumn = WorkflowColumn.Build;
		feat.AgentReviewFailCount = 2; // threshold met!
		await dbContext.SaveChangesAsync();

		var status = await reviewOutcomeService.GetStatusForFeatureAsync(feat.Id);

		Assert.True(status.IsRework);
		Assert.True(status.HasIssue);
		Assert.True(status.AgentReviewThresholdCrossed);
		Assert.False(status.HumanReviewThresholdCrossed);
		Assert.Equal(2, status.AgentReviewFailCount);
		Assert.Equal(0, status.HumanReviewFailCount);
		Assert.Equal(2, status.AgentReviewFailThreshold);
		Assert.Equal(3, status.HumanReviewFailThreshold);

		var step = await steps.CreateAsync(feat.Id, "Step 1");
		step.WorkflowColumn = WorkflowColumn.Build;
		step.HumanReviewFailCount = 1;
		await dbContext.SaveChangesAsync();

		var stepStatus = await reviewOutcomeService.GetStatusForStepAsync(step.Id);
		Assert.True(stepStatus.IsRework);
		Assert.False(stepStatus.HasIssue);
		Assert.Equal(1, stepStatus.HumanReviewFailCount);
	}
}
