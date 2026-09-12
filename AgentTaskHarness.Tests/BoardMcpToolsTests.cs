using System.Text.Json;
using AgentTaskHarness.Application.Agents;
using AgentTaskHarness.Application.Boards;
using AgentTaskHarness.Application.Comments;
using AgentTaskHarness.Application.Features;
using AgentTaskHarness.Application.Reviews;
using AgentTaskHarness.Application.Steps;
using AgentTaskHarness.Application.Workflow;
using AgentTaskHarness.Domain.Entities;
using AgentTaskHarness.Domain.Enums;
using AgentTaskHarness.Infrastructure.Mcp;
using AgentTaskHarness.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Xunit;

namespace AgentTaskHarness.Tests;

public class BoardMcpToolsTests : IAsyncLifetime
{
	private readonly SqliteConnection _connection = new("Data Source=:memory:");
	private AgentMatchingService _agentMatchingService = null!;
	private BoardService _boards = null!;
	private CommentService _commentService = null!;
	private AppDbContext _dbContext = null!;
	private FeatureDependencyService _featureDeps = null!;
	private FeatureTransitionOrchestrator _featureOrchestrator = null!;
	private FeatureService _features = null!;
	private BoardMcpTools _mcpTools = null!;
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
		_commentService = new CommentService(_dbContext);
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

		_mcpTools = new BoardMcpTools(
			_featureOrchestrator,
			_stepOrchestrator,
			_featureDeps,
			_stepDeps,
			_reviewOutcomeService,
			_commentService,
			_dbContext);
	}

	public async Task DisposeAsync()
	{
		await _dbContext.DisposeAsync();
		await _connection.DisposeAsync();
	}

	[Fact]
	public async Task StartBuild_StepInReady_TransitionsToBuild()
	{
		var board = await _boards.CreateAsync("Board 1", "/repos/b1");
		var feat = await _features.CreateAsync(board.Id, "Feat 1");
		var step = await _steps.CreateAsync(feat.Id, "Step 1");

		await _featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);

		var result = await _mcpTools.StartBuildAsync(step.Id);

		Assert.Equal("Step", result.CardType);
		Assert.Equal(step.Id, result.CardId);
		Assert.Equal("Build", result.WorkflowColumn);
		Assert.Equal("start_build", result.Action);
		Assert.False(result.IsBlocked);

		var reloaded = await _steps.GetByIdAsync(step.Id);
		Assert.Equal(WorkflowColumn.Build, reloaded!.WorkflowColumn);
	}

	[Fact]
	public async Task StartBuild_StepNotInReady_ThrowsInvalidOperationException()
	{
		var board = await _boards.CreateAsync("Board 1", "/repos/b1");
		var feat = await _features.CreateAsync(board.Id, "Feat 1");
		var step = await _steps.CreateAsync(feat.Id, "Step 1");

		// Step is in Backlog
		await Assert.ThrowsAsync<InvalidOperationException>(() => _mcpTools.StartBuildAsync(step.Id));
	}

	[Fact]
	public async Task StartBuild_StepDependenciesUnmet_ThrowsInvalidOperationException()
	{
		var board = await _boards.CreateAsync("Board 1", "/repos/b1");
		var feat = await _features.CreateAsync(board.Id, "Feat 1");
		var step1 = await _steps.CreateAsync(feat.Id, "Step 1");
		var step2 = await _steps.CreateAsync(feat.Id, "Step 2");

		await _stepDeps.AddAsync(step2.Id, step1.Id);
		await _featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);

		await Assert.ThrowsAsync<InvalidOperationException>(() => _mcpTools.StartBuildAsync(step2.Id));
	}

	[Fact]
	public async Task StartBuild_FeatureInBacklog_MovesToReadyAndStepsToReady()
	{
		var board = await _boards.CreateAsync("Board 1", "/repos/b1");
		var feat = await _features.CreateAsync(board.Id, "Feat 1");
		var step = await _steps.CreateAsync(feat.Id, "Step 1");

		var result = await _mcpTools.StartBuildAsync(feat.Id, "Feature");

		Assert.Equal("Feature", result.CardType);
		Assert.Equal("Ready", result.WorkflowColumn);

		var reloadedFeat = await _features.GetByIdAsync(feat.Id);
		Assert.Equal(WorkflowColumn.Ready, reloadedFeat!.WorkflowColumn);

		var reloadedStep = await _steps.GetByIdAsync(step.Id);
		Assert.Equal(WorkflowColumn.Ready, reloadedStep!.WorkflowColumn);
	}

	[Fact]
	public async Task StartBuild_FeatureInReady_TransitionsToBuild()
	{
		var board = await _boards.CreateAsync("Board 1", "/repos/b1");
		var feat = await _features.CreateAsync(board.Id, "Feat 1");
		await _featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);

		var result = await _mcpTools.StartBuildAsync(feat.Id);

		Assert.Equal("Feature", result.CardType);
		Assert.Equal("Build", result.WorkflowColumn);
	}

	[Fact]
	public async Task StartBuild_WithActiveAgent_FlagsSoftBlocked()
	{
		var board = await _boards.CreateAsync("Board 1", "/repos/b1");
		var feat = await _features.CreateAsync(board.Id, "Feat 1");
		var step = await _steps.CreateAsync(feat.Id, "Step 1");
		await _featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);

		// Simulate an active agent running on the step
		_dbContext.AgentRuns.Add(new AgentRun
		{
			CardType = CardType.Step,
			CardId = step.Id,
			Status = AgentRunStatus.Working,
			StartedAt = DateTimeOffset.UtcNow
		});
		await _dbContext.SaveChangesAsync();

		var result = await _mcpTools.StartBuildAsync(step.Id);

		Assert.Equal("Build", result.WorkflowColumn);
		Assert.True(result.IsBlocked);

		var hasBlockedRun = await _dbContext.AgentRuns.AnyAsync(r =>
			r.CardType == CardType.Step && r.CardId == step.Id && r.Status == AgentRunStatus.Blocked);
		Assert.True(hasBlockedRun);
	}

	[Fact]
	public async Task SubmitForReview_StepInBuild_TransitionsToAgentReviewAndRecordsNotes()
	{
		var board = await _boards.CreateAsync("Board 1", "/repos/b1");
		var feat = await _features.CreateAsync(board.Id, "Feat 1");
		var step = await _steps.CreateAsync(feat.Id, "Step 1");
		await _featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);
		await _stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Build);

		var result = await _mcpTools.SubmitForReviewAsync(step.Id, notes: "Finished implementing auth module.");

		Assert.Equal("Step", result.CardType);
		Assert.Equal("AgentReview", result.WorkflowColumn);
		Assert.Equal("submit_for_review", result.Action);

		var reloaded = await _steps.GetByIdAsync(step.Id);
		Assert.Equal(WorkflowColumn.AgentReview, reloaded!.WorkflowColumn);

		var comments = await _commentService.GetCommentsAsync(CardType.Step, step.Id);
		Assert.Single(comments);
		Assert.Equal("Finished implementing auth module.", comments[0].Body);
		Assert.Equal("Agent", comments[0].Author);
	}

	[Fact]
	public async Task SubmitForReview_FeatureInBuild_TransitionsToAgentReview()
	{
		var board = await _boards.CreateAsync("Board 1", "/repos/b1");
		var feat = await _features.CreateAsync(board.Id, "Feat 1");
		await _featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);
		await _featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Build);

		var result = await _mcpTools.SubmitForReviewAsync(feat.Id);

		Assert.Equal("Feature", result.CardType);
		Assert.Equal("AgentReview", result.WorkflowColumn);
	}

	[Fact]
	public async Task SubmitForReview_NotInBuild_ThrowsInvalidOperationException()
	{
		var board = await _boards.CreateAsync("Board 1", "/repos/b1");
		var feat = await _features.CreateAsync(board.Id, "Feat 1");
		var step = await _steps.CreateAsync(feat.Id, "Step 1");

		await Assert.ThrowsAsync<InvalidOperationException>(() => _mcpTools.SubmitForReviewAsync(step.Id));
	}

	[Fact]
	public async Task ApproveReview_FromAgentReview_AdvancesToHumanReviewOrDone()
	{
		// Board without skipStepHumanReview
		var board = await _boards.CreateAsync("Board 1", "/repos/b1");
		var feat = await _features.CreateAsync(board.Id, "Feat 1");
		var step = await _steps.CreateAsync(feat.Id, "Step 1");
		await _featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);
		await _stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Build);
		await _stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.AgentReview);

		var result = await _mcpTools.ApproveReviewAsync(step.Id, notes: "Looks good to me.");

		Assert.Equal("HumanReview", result.WorkflowColumn);
		Assert.Equal("approve_review", result.Action);

		var comments = await _commentService.GetCommentsAsync(CardType.Step, step.Id);
		Assert.Single(comments);
		Assert.Equal("Review Agent", comments[0].Author);

		// Now approve from HumanReview -> moves to Done
		var result2 = await _mcpTools.ApproveReviewAsync(step.Id);
		Assert.Equal("Done", result2.WorkflowColumn);
	}

	[Fact]
	public async Task ApproveReview_WithSkipHumanReview_AutoAdvancesStraightToDone()
	{
		var board = await _boards.CreateAsync("Board 1", "/repos/b1", skipStepHumanReview: true);
		var feat = await _features.CreateAsync(board.Id, "Feat 1");
		var step = await _steps.CreateAsync(feat.Id, "Step 1");
		await _featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);
		await _stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Build);
		await _stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.AgentReview);

		var result = await _mcpTools.ApproveReviewAsync(step.Id);

		Assert.Equal("Done", result.WorkflowColumn);
	}

	[Fact]
	public async Task ApproveReview_NotInReviewColumn_ThrowsInvalidOperationException()
	{
		var board = await _boards.CreateAsync("Board 1", "/repos/b1");
		var feat = await _features.CreateAsync(board.Id, "Feat 1");
		var step = await _steps.CreateAsync(feat.Id, "Step 1");

		await Assert.ThrowsAsync<InvalidOperationException>(() => _mcpTools.ApproveReviewAsync(step.Id));
	}

	[Fact]
	public async Task FailReview_FromAgentReview_ReturnsToBuildAndIncrementsAgentFailCount()
	{
		var board = await _boards.CreateAsync("Board 1", "/repos/b1");
		var feat = await _features.CreateAsync(board.Id, "Feat 1");
		var step = await _steps.CreateAsync(feat.Id, "Step 1");
		await _featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);
		await _stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Build);
		await _stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.AgentReview);

		Assert.Equal(0, step.AgentReviewFailCount);

		var result = await _mcpTools.FailReviewAsync(step.Id, reason: "Missing unit tests for edge cases.");

		Assert.Equal("Build", result.WorkflowColumn);
		Assert.Equal("fail_review", result.Action);

		var reloaded = await _steps.GetByIdAsync(step.Id);
		Assert.Equal(WorkflowColumn.Build, reloaded!.WorkflowColumn);
		Assert.Equal(1, reloaded.AgentReviewFailCount);
		Assert.Equal(0, reloaded.HumanReviewFailCount);

		var comments = await _commentService.GetCommentsAsync(CardType.Step, step.Id);
		Assert.Single(comments);
		Assert.Equal("Review Agent", comments[0].Author);
		Assert.Equal("Missing unit tests for edge cases.", comments[0].Body);
	}

	[Fact]
	public async Task FailReview_FromHumanReview_ReturnsToBuildAndIncrementsHumanFailCount()
	{
		var board = await _boards.CreateAsync("Board 1", "/repos/b1");
		var feat = await _features.CreateAsync(board.Id, "Feat 1");
		var step = await _steps.CreateAsync(feat.Id, "Step 1");
		await _featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);
		await _stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Build);
		await _stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.AgentReview);
		await _stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.HumanReview);

		var result = await _mcpTools.FailReviewAsync(step.Id, reason: "Manual testing found regression.");

		Assert.Equal("Build", result.WorkflowColumn);

		var reloaded = await _steps.GetByIdAsync(step.Id);
		Assert.Equal(WorkflowColumn.Build, reloaded!.WorkflowColumn);
		Assert.Equal(0, reloaded.AgentReviewFailCount);
		Assert.Equal(1, reloaded.HumanReviewFailCount);
	}

	[Fact]
	public async Task FailReview_NotInReview_ThrowsInvalidOperationException()
	{
		var board = await _boards.CreateAsync("Board 1", "/repos/b1");
		var feat = await _features.CreateAsync(board.Id, "Feat 1");
		var step = await _steps.CreateAsync(feat.Id, "Step 1");

		await Assert.ThrowsAsync<InvalidOperationException>(() => _mcpTools.FailReviewAsync(step.Id));
	}

	[Fact]
	public async Task SendToBacklog_FromBuild_MovesToBacklog()
	{
		var board = await _boards.CreateAsync("Board 1", "/repos/b1");
		var feat = await _features.CreateAsync(board.Id, "Feat 1");
		var step = await _steps.CreateAsync(feat.Id, "Step 1");
		await _featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);
		await _stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Build);

		var result = await _mcpTools.SendToBacklogAsync(step.Id, reason: "Deprioritized for now.");

		Assert.Equal("Backlog", result.WorkflowColumn);
		Assert.Equal("send_to_backlog", result.Action);

		var reloaded = await _steps.GetByIdAsync(step.Id);
		Assert.Equal(WorkflowColumn.Backlog, reloaded!.WorkflowColumn);

		var comments = await _commentService.GetCommentsAsync(CardType.Step, step.Id);
		Assert.Single(comments);
		Assert.Equal("Deprioritized for now.", comments[0].Body);
	}

	[Fact]
	public async Task SendToBacklog_CompletedCard_ThrowsInvalidOperationException()
	{
		var board = await _boards.CreateAsync("Board 1", "/repos/b1", skipStepHumanReview: true);
		var feat = await _features.CreateAsync(board.Id, "Feat 1");
		var step = await _steps.CreateAsync(feat.Id, "Step 1");
		await _featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);
		await _stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Build);
		await _stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.AgentReview);
		await _stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Done);

		await Assert.ThrowsAsync<InvalidOperationException>(() => _mcpTools.SendToBacklogAsync(step.Id));
	}

	[Fact]
	public async Task GetCardDetails_Step_ReturnsCompleteDetails()
	{
		var board = await _boards.CreateAsync("Board 1", "/repos/b1");
		var feat = await _features.CreateAsync(board.Id, "Feat 1", "Reqs", "AC");
		var step = await _steps.CreateAsync(feat.Id, "Step 1", "Step desc", "Notes");

		await _commentService.AddCommentAsync(CardType.Step, step.Id, "Dev", "First comment");

		var details = await _mcpTools.GetCardDetailsAsync(step.Id);

		Assert.Equal(step.Id, details.CardId);
		Assert.Equal("Step", details.CardType);
		Assert.Equal("Step 1", details.Title);
		Assert.Equal("Backlog", details.WorkflowColumn);
		Assert.Equal(board.Id, details.BoardId);
		Assert.Equal(feat.Id, details.FeatureId);
		Assert.Equal("Step desc", details.Description);
		Assert.Equal("Reqs", details.Requirements);
		Assert.Equal("AC", details.AcceptanceCriteria);
		Assert.Equal("Notes", details.GuidanceNotes);
		Assert.Equal(1, details.CommentsCount);
		Assert.False(details.IsRework);
		Assert.False(details.HasIssue);
	}

	[Fact]
	public async Task GetCardDetails_Feature_ReturnsCompleteDetails()
	{
		var board = await _boards.CreateAsync("Board 1", "/repos/b1");
		var feat = await _features.CreateAsync(board.Id, "Feat 1", "Feature Reqs");
		var step1 = await _steps.CreateAsync(feat.Id, "Step 1");
		var step2 = await _steps.CreateAsync(feat.Id, "Step 2");

		var details = await _mcpTools.GetCardDetailsAsync(feat.Id, "Feature");

		Assert.Equal(feat.Id, details.CardId);
		Assert.Equal("Feature", details.CardType);
		Assert.Equal("Feat 1", details.Title);
		Assert.Equal("Feature Reqs", details.Requirements);
		Assert.Equal(2, details.TotalSteps);
		Assert.Equal(0, details.DoneSteps);
		Assert.NotNull(details.StepIds);
		Assert.Equal(2, details.StepIds.Count);
	}

	[Fact]
	public async Task GetAvailableActions_Step_ReturnsExpectedActionsPerColumn()
	{
		var board = await _boards.CreateAsync("Board 1", "/repos/b1");
		var feat = await _features.CreateAsync(board.Id, "Feat 1");
		var step1 = await _steps.CreateAsync(feat.Id, "Step 1");
		var step2 = await _steps.CreateAsync(feat.Id, "Step 2");

		// Step in Backlog: no direct move actions available
		var actionsBacklog = await _mcpTools.GetAvailableActionsAsync(step1.Id);
		Assert.Empty(actionsBacklog.AvailableActions);

		// Move feature to Ready (moves steps to Ready)
		await _featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);

		// Add dependency: step2 depends on step1
		await _stepDeps.AddAsync(step2.Id, step1.Id);

		// Step 1 in Ready (dependencies met): can start_build or send_to_backlog
		var actionsReadyMet = await _mcpTools.GetAvailableActionsAsync(step1.Id);
		Assert.Contains("start_build", actionsReadyMet.AvailableActions);
		Assert.Contains("send_to_backlog", actionsReadyMet.AvailableActions);

		// Step 2 in Ready (dependencies unmet): cannot start_build, can send_to_backlog
		var actionsReadyUnmet = await _mcpTools.GetAvailableActionsAsync(step2.Id);
		Assert.DoesNotContain("start_build", actionsReadyUnmet.AvailableActions);
		Assert.Contains("send_to_backlog", actionsReadyUnmet.AvailableActions);

		// Move step1 to Build
		await _stepOrchestrator.MoveAsync(step1.Id, WorkflowColumn.Build);
		var actionsBuild = await _mcpTools.GetAvailableActionsAsync(step1.Id);
		Assert.Contains("submit_for_review", actionsBuild.AvailableActions);
		Assert.Contains("send_to_backlog", actionsBuild.AvailableActions);

		// Move step1 to AgentReview
		await _stepOrchestrator.MoveAsync(step1.Id, WorkflowColumn.AgentReview);
		var actionsReview = await _mcpTools.GetAvailableActionsAsync(step1.Id);
		Assert.Contains("approve_review", actionsReview.AvailableActions);
		Assert.Contains("fail_review", actionsReview.AvailableActions);
		Assert.Contains("send_to_backlog", actionsReview.AvailableActions);

		// Move step1 to Done
		step1.WorkflowColumn = WorkflowColumn.Done;
		await _dbContext.SaveChangesAsync();
		var actionsDone = await _mcpTools.GetAvailableActionsAsync(step1.Id);
		Assert.Empty(actionsDone.AvailableActions);
	}

	[Fact]
	public async Task GetAvailableActions_Feature_ReturnsExpectedActionsPerColumn()
	{
		var board = await _boards.CreateAsync("Board 1", "/repos/b1");
		var feat = await _features.CreateAsync(board.Id, "Feat 1");

		// Feature in Backlog with dependencies met: start_build available
		var actionsBacklog = await _mcpTools.GetAvailableActionsAsync(feat.Id);
		Assert.Contains("start_build", actionsBacklog.AvailableActions);

		// In Ready: start_build and send_to_backlog
		await _featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);
		var actionsReady = await _mcpTools.GetAvailableActionsAsync(feat.Id);
		Assert.Contains("start_build", actionsReady.AvailableActions);
		Assert.Contains("send_to_backlog", actionsReady.AvailableActions);

		// In Build: submit_for_review and send_to_backlog
		await _featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Build);
		var actionsBuild = await _mcpTools.GetAvailableActionsAsync(feat.Id);
		Assert.Contains("submit_for_review", actionsBuild.AvailableActions);
		Assert.Contains("send_to_backlog", actionsBuild.AvailableActions);
	}

	[Fact]
	public async Task GetAvailableActions_WithActiveAgent_ActionsStillAvailableAndIsBlockedTrue()
	{
		var board = await _boards.CreateAsync("Board 1", "/repos/b1");
		var feat = await _features.CreateAsync(board.Id, "Feat 1");
		var step = await _steps.CreateAsync(feat.Id, "Step 1");
		await _featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);
		await _stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Build);

		// Agent is working on this step
		_dbContext.AgentRuns.Add(new AgentRun
		{
			CardType = CardType.Step,
			CardId = step.Id,
			Status = AgentRunStatus.Working,
			StartedAt = DateTimeOffset.UtcNow
		});
		await _dbContext.SaveChangesAsync();

		// In UI, GetAllowedMovesAsync returns empty:
		var uiAllowed = await _stepOrchestrator.GetAllowedMovesAsync(step.Id);
		Assert.Empty(uiAllowed);

		// In MCP, actions are NOT blocked! Instead IsBlocked is true:
		var mcpActions = await _mcpTools.GetAvailableActionsAsync(step.Id);
		Assert.Contains("submit_for_review", mcpActions.AvailableActions);
		Assert.True(mcpActions.IsBlocked);
	}

	[Fact]
	public async Task AddComment_And_GetComments_RoundTrip()
	{
		var board = await _boards.CreateAsync("Board 1", "/repos/b1");
		var feat = await _features.CreateAsync(board.Id, "Feat 1");
		var step = await _steps.CreateAsync(feat.Id, "Step 1");

		var added = await _mcpTools.AddCommentAsync(step.Id, "Alice", "Investigating bug...");
		Assert.Equal("Step", added.CardType);
		Assert.Equal(step.Id, added.CardId);
		Assert.Equal("Alice", added.Author);
		Assert.Equal("Investigating bug...", added.Body);

		var list = await _mcpTools.GetCommentsAsync(step.Id);
		Assert.Single(list);
		Assert.Equal("Alice", list[0].Author);
		Assert.Equal("Investigating bug...", list[0].Body);
	}

	[Fact]
	public void McpServerRegistration_WithToolsFromAssembly_DiscoversAllNamedActionsAndTools()
	{
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddScoped<BoardMcpTools>(_ => _mcpTools);
		services.AddMcpServer()
			.WithTools<BoardMcpTools>();

		var sp = services.BuildServiceProvider();
		var options = sp.GetRequiredService<IOptions<McpServerOptions>>().Value;
		var toolCollection = options.ToolCollection;

		Assert.NotNull(toolCollection);

		var expectedTools = new[]
		{
			"start_build",
			"submit_for_review",
			"approve_review",
			"fail_review",
			"send_to_backlog",
			"get_card_details",
			"get_available_actions",
			"add_comment",
			"get_comments"
		};

		foreach (var name in expectedTools)
		{
			Assert.Contains(toolCollection, t => t.ProtocolTool.Name == name);
			var tool = toolCollection[name];
			Assert.False(string.IsNullOrWhiteSpace(tool.ProtocolTool.Description),
				$"Tool '{name}' should have a description.");
			Assert.True(tool.ProtocolTool.InputSchema.ValueKind != JsonValueKind.Undefined);
		}
	}
}