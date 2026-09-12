using AgentTaskHarness.Application.Abstractions;
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
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Xunit;

namespace AgentTaskHarness.Tests;

public class BoardMcpToolsTests : IAsyncLifetime
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
	private CommentService commentService = null!;
	private AgentMatchingService agentMatchingService = null!;
	private TestAgentProcessRunner testProcessRunner = null!;
	private AgentSchedulerService scheduler = null!;
	private FeatureTransitionOrchestrator featureOrchestrator = null!;
	private StepTransitionOrchestrator stepOrchestrator = null!;
	private BoardMcpTools mcpTools = null!;

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
		commentService = new CommentService(dbContext);
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

		mcpTools = new BoardMcpTools(
			featureOrchestrator,
			stepOrchestrator,
			featureDeps,
			stepDeps,
			reviewOutcomeService,
			commentService,
			dbContext);
	}

	public async Task DisposeAsync()
	{
		await dbContext.DisposeAsync();
		await connection.DisposeAsync();
	}

	[Fact]
	public async Task StartBuild_StepInReady_TransitionsToBuild()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var feat = await features.CreateAsync(board.Id, "Feat 1");
		var step = await steps.CreateAsync(feat.Id, "Step 1");

		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);

		var result = await mcpTools.StartBuildAsync(step.Id);

		Assert.Equal("Step", result.CardType);
		Assert.Equal(step.Id, result.CardId);
		Assert.Equal("Build", result.WorkflowColumn);
		Assert.Equal("start_build", result.Action);
		Assert.False(result.IsBlocked);

		var reloaded = await steps.GetByIdAsync(step.Id);
		Assert.Equal(WorkflowColumn.Build, reloaded!.WorkflowColumn);
	}

	[Fact]
	public async Task StartBuild_StepNotInReady_ThrowsInvalidOperationException()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var feat = await features.CreateAsync(board.Id, "Feat 1");
		var step = await steps.CreateAsync(feat.Id, "Step 1");

		// Step is in Backlog
		await Assert.ThrowsAsync<InvalidOperationException>(() => mcpTools.StartBuildAsync(step.Id));
	}

	[Fact]
	public async Task StartBuild_StepDependenciesUnmet_ThrowsInvalidOperationException()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var feat = await features.CreateAsync(board.Id, "Feat 1");
		var step1 = await steps.CreateAsync(feat.Id, "Step 1");
		var step2 = await steps.CreateAsync(feat.Id, "Step 2");

		await stepDeps.AddAsync(step2.Id, step1.Id);
		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);

		await Assert.ThrowsAsync<InvalidOperationException>(() => mcpTools.StartBuildAsync(step2.Id));
	}

	[Fact]
	public async Task StartBuild_FeatureInBacklog_MovesToReadyAndStepsToReady()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var feat = await features.CreateAsync(board.Id, "Feat 1");
		var step = await steps.CreateAsync(feat.Id, "Step 1");

		var result = await mcpTools.StartBuildAsync(feat.Id, cardType: "Feature");

		Assert.Equal("Feature", result.CardType);
		Assert.Equal("Ready", result.WorkflowColumn);

		var reloadedFeat = await features.GetByIdAsync(feat.Id);
		Assert.Equal(WorkflowColumn.Ready, reloadedFeat!.WorkflowColumn);

		var reloadedStep = await steps.GetByIdAsync(step.Id);
		Assert.Equal(WorkflowColumn.Ready, reloadedStep!.WorkflowColumn);
	}

	[Fact]
	public async Task StartBuild_FeatureInReady_TransitionsToBuild()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var feat = await features.CreateAsync(board.Id, "Feat 1");
		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);

		var result = await mcpTools.StartBuildAsync(feat.Id);

		Assert.Equal("Feature", result.CardType);
		Assert.Equal("Build", result.WorkflowColumn);
	}

	[Fact]
	public async Task StartBuild_WithActiveAgent_FlagsSoftBlocked()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var feat = await features.CreateAsync(board.Id, "Feat 1");
		var step = await steps.CreateAsync(feat.Id, "Step 1");
		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);

		// Simulate an active agent running on the step
		dbContext.AgentRuns.Add(new AgentRun
		{
			CardType = CardType.Step,
			CardId = step.Id,
			Status = AgentRunStatus.Working,
			StartedAt = DateTimeOffset.UtcNow
		});
		await dbContext.SaveChangesAsync();

		var result = await mcpTools.StartBuildAsync(step.Id);

		Assert.Equal("Build", result.WorkflowColumn);
		Assert.True(result.IsBlocked);

		var hasBlockedRun = await dbContext.AgentRuns.AnyAsync(r =>
			r.CardType == CardType.Step && r.CardId == step.Id && r.Status == AgentRunStatus.Blocked);
		Assert.True(hasBlockedRun);
	}

	[Fact]
	public async Task SubmitForReview_StepInBuild_TransitionsToAgentReviewAndRecordsNotes()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var feat = await features.CreateAsync(board.Id, "Feat 1");
		var step = await steps.CreateAsync(feat.Id, "Step 1");
		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Build);

		var result = await mcpTools.SubmitForReviewAsync(step.Id, notes: "Finished implementing auth module.");

		Assert.Equal("Step", result.CardType);
		Assert.Equal("AgentReview", result.WorkflowColumn);
		Assert.Equal("submit_for_review", result.Action);

		var reloaded = await steps.GetByIdAsync(step.Id);
		Assert.Equal(WorkflowColumn.AgentReview, reloaded!.WorkflowColumn);

		var comments = await commentService.GetCommentsAsync(CardType.Step, step.Id);
		Assert.Single(comments);
		Assert.Equal("Finished implementing auth module.", comments[0].Body);
		Assert.Equal("Agent", comments[0].Author);
	}

	[Fact]
	public async Task SubmitForReview_FeatureInBuild_TransitionsToAgentReview()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var feat = await features.CreateAsync(board.Id, "Feat 1");
		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);
		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Build);

		var result = await mcpTools.SubmitForReviewAsync(feat.Id);

		Assert.Equal("Feature", result.CardType);
		Assert.Equal("AgentReview", result.WorkflowColumn);
	}

	[Fact]
	public async Task SubmitForReview_NotInBuild_ThrowsInvalidOperationException()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var feat = await features.CreateAsync(board.Id, "Feat 1");
		var step = await steps.CreateAsync(feat.Id, "Step 1");

		await Assert.ThrowsAsync<InvalidOperationException>(() => mcpTools.SubmitForReviewAsync(step.Id));
	}

	[Fact]
	public async Task ApproveReview_FromAgentReview_AdvancesToHumanReviewOrDone()
	{
		// Board without skipStepHumanReview
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1, skipStepHumanReview: false);
		var feat = await features.CreateAsync(board.Id, "Feat 1");
		var step = await steps.CreateAsync(feat.Id, "Step 1");
		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Build);
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.AgentReview);

		var result = await mcpTools.ApproveReviewAsync(step.Id, notes: "Looks good to me.");

		Assert.Equal("HumanReview", result.WorkflowColumn);
		Assert.Equal("approve_review", result.Action);

		var comments = await commentService.GetCommentsAsync(CardType.Step, step.Id);
		Assert.Single(comments);
		Assert.Equal("Review Agent", comments[0].Author);

		// Now approve from HumanReview -> moves to Done
		var result2 = await mcpTools.ApproveReviewAsync(step.Id);
		Assert.Equal("Done", result2.WorkflowColumn);
	}

	[Fact]
	public async Task ApproveReview_WithSkipHumanReview_AutoAdvancesStraightToDone()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1, skipStepHumanReview: true);
		var feat = await features.CreateAsync(board.Id, "Feat 1");
		var step = await steps.CreateAsync(feat.Id, "Step 1");
		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Build);
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.AgentReview);

		var result = await mcpTools.ApproveReviewAsync(step.Id);

		Assert.Equal("Done", result.WorkflowColumn);
	}

	[Fact]
	public async Task ApproveReview_NotInReviewColumn_ThrowsInvalidOperationException()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var feat = await features.CreateAsync(board.Id, "Feat 1");
		var step = await steps.CreateAsync(feat.Id, "Step 1");

		await Assert.ThrowsAsync<InvalidOperationException>(() => mcpTools.ApproveReviewAsync(step.Id));
	}

	[Fact]
	public async Task FailReview_FromAgentReview_ReturnsToBuildAndIncrementsAgentFailCount()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var feat = await features.CreateAsync(board.Id, "Feat 1");
		var step = await steps.CreateAsync(feat.Id, "Step 1");
		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Build);
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.AgentReview);

		Assert.Equal(0, step.AgentReviewFailCount);

		var result = await mcpTools.FailReviewAsync(step.Id, reason: "Missing unit tests for edge cases.");

		Assert.Equal("Build", result.WorkflowColumn);
		Assert.Equal("fail_review", result.Action);

		var reloaded = await steps.GetByIdAsync(step.Id);
		Assert.Equal(WorkflowColumn.Build, reloaded!.WorkflowColumn);
		Assert.Equal(1, reloaded.AgentReviewFailCount);
		Assert.Equal(0, reloaded.HumanReviewFailCount);

		var comments = await commentService.GetCommentsAsync(CardType.Step, step.Id);
		Assert.Single(comments);
		Assert.Equal("Review Agent", comments[0].Author);
		Assert.Equal("Missing unit tests for edge cases.", comments[0].Body);
	}

	[Fact]
	public async Task FailReview_FromHumanReview_ReturnsToBuildAndIncrementsHumanFailCount()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1, skipStepHumanReview: false);
		var feat = await features.CreateAsync(board.Id, "Feat 1");
		var step = await steps.CreateAsync(feat.Id, "Step 1");
		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Build);
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.AgentReview);
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.HumanReview);

		var result = await mcpTools.FailReviewAsync(step.Id, reason: "Manual testing found regression.");

		Assert.Equal("Build", result.WorkflowColumn);

		var reloaded = await steps.GetByIdAsync(step.Id);
		Assert.Equal(WorkflowColumn.Build, reloaded!.WorkflowColumn);
		Assert.Equal(0, reloaded.AgentReviewFailCount);
		Assert.Equal(1, reloaded.HumanReviewFailCount);
	}

	[Fact]
	public async Task FailReview_NotInReview_ThrowsInvalidOperationException()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var feat = await features.CreateAsync(board.Id, "Feat 1");
		var step = await steps.CreateAsync(feat.Id, "Step 1");

		await Assert.ThrowsAsync<InvalidOperationException>(() => mcpTools.FailReviewAsync(step.Id));
	}

	[Fact]
	public async Task SendToBacklog_FromBuild_MovesToBacklog()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var feat = await features.CreateAsync(board.Id, "Feat 1");
		var step = await steps.CreateAsync(feat.Id, "Step 1");
		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Build);

		var result = await mcpTools.SendToBacklogAsync(step.Id, reason: "Deprioritized for now.");

		Assert.Equal("Backlog", result.WorkflowColumn);
		Assert.Equal("send_to_backlog", result.Action);

		var reloaded = await steps.GetByIdAsync(step.Id);
		Assert.Equal(WorkflowColumn.Backlog, reloaded!.WorkflowColumn);

		var comments = await commentService.GetCommentsAsync(CardType.Step, step.Id);
		Assert.Single(comments);
		Assert.Equal("Deprioritized for now.", comments[0].Body);
	}

	[Fact]
	public async Task SendToBacklog_CompletedCard_ThrowsInvalidOperationException()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1, skipStepHumanReview: true);
		var feat = await features.CreateAsync(board.Id, "Feat 1");
		var step = await steps.CreateAsync(feat.Id, "Step 1");
		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Build);
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.AgentReview);
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Done);

		await Assert.ThrowsAsync<InvalidOperationException>(() => mcpTools.SendToBacklogAsync(step.Id));
	}

	[Fact]
	public async Task GetCardDetails_Step_ReturnsCompleteDetails()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var feat = await features.CreateAsync(board.Id, "Feat 1", requirements: "Reqs", acceptanceCriteria: "AC");
		var step = await steps.CreateAsync(feat.Id, "Step 1", description: "Step desc", guidanceNotes: "Notes");

		await commentService.AddCommentAsync(CardType.Step, step.Id, "Dev", "First comment");

		var details = await mcpTools.GetCardDetailsAsync(step.Id);

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
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var feat = await features.CreateAsync(board.Id, "Feat 1", requirements: "Feature Reqs");
		var step1 = await steps.CreateAsync(feat.Id, "Step 1");
		var step2 = await steps.CreateAsync(feat.Id, "Step 2");

		var details = await mcpTools.GetCardDetailsAsync(feat.Id, cardType: "Feature");

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
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var feat = await features.CreateAsync(board.Id, "Feat 1");
		var step1 = await steps.CreateAsync(feat.Id, "Step 1");
		var step2 = await steps.CreateAsync(feat.Id, "Step 2");

		// Step in Backlog: no direct move actions available
		var actionsBacklog = await mcpTools.GetAvailableActionsAsync(step1.Id);
		Assert.Empty(actionsBacklog.AvailableActions);

		// Move feature to Ready (moves steps to Ready)
		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);

		// Add dependency: step2 depends on step1
		await stepDeps.AddAsync(step2.Id, step1.Id);

		// Step 1 in Ready (dependencies met): can start_build or send_to_backlog
		var actionsReadyMet = await mcpTools.GetAvailableActionsAsync(step1.Id);
		Assert.Contains("start_build", actionsReadyMet.AvailableActions);
		Assert.Contains("send_to_backlog", actionsReadyMet.AvailableActions);

		// Step 2 in Ready (dependencies unmet): cannot start_build, can send_to_backlog
		var actionsReadyUnmet = await mcpTools.GetAvailableActionsAsync(step2.Id);
		Assert.DoesNotContain("start_build", actionsReadyUnmet.AvailableActions);
		Assert.Contains("send_to_backlog", actionsReadyUnmet.AvailableActions);

		// Move step1 to Build
		await stepOrchestrator.MoveAsync(step1.Id, WorkflowColumn.Build);
		var actionsBuild = await mcpTools.GetAvailableActionsAsync(step1.Id);
		Assert.Contains("submit_for_review", actionsBuild.AvailableActions);
		Assert.Contains("send_to_backlog", actionsBuild.AvailableActions);

		// Move step1 to AgentReview
		await stepOrchestrator.MoveAsync(step1.Id, WorkflowColumn.AgentReview);
		var actionsReview = await mcpTools.GetAvailableActionsAsync(step1.Id);
		Assert.Contains("approve_review", actionsReview.AvailableActions);
		Assert.Contains("fail_review", actionsReview.AvailableActions);
		Assert.Contains("send_to_backlog", actionsReview.AvailableActions);

		// Move step1 to Done
		step1.WorkflowColumn = WorkflowColumn.Done;
		await dbContext.SaveChangesAsync();
		var actionsDone = await mcpTools.GetAvailableActionsAsync(step1.Id);
		Assert.Empty(actionsDone.AvailableActions);
	}

	[Fact]
	public async Task GetAvailableActions_Feature_ReturnsExpectedActionsPerColumn()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var feat = await features.CreateAsync(board.Id, "Feat 1");

		// Feature in Backlog with dependencies met: start_build available
		var actionsBacklog = await mcpTools.GetAvailableActionsAsync(feat.Id);
		Assert.Contains("start_build", actionsBacklog.AvailableActions);

		// In Ready: start_build and send_to_backlog
		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);
		var actionsReady = await mcpTools.GetAvailableActionsAsync(feat.Id);
		Assert.Contains("start_build", actionsReady.AvailableActions);
		Assert.Contains("send_to_backlog", actionsReady.AvailableActions);

		// In Build: submit_for_review and send_to_backlog
		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Build);
		var actionsBuild = await mcpTools.GetAvailableActionsAsync(feat.Id);
		Assert.Contains("submit_for_review", actionsBuild.AvailableActions);
		Assert.Contains("send_to_backlog", actionsBuild.AvailableActions);
	}

	[Fact]
	public async Task GetAvailableActions_WithActiveAgent_ActionsStillAvailableAndIsBlockedTrue()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var feat = await features.CreateAsync(board.Id, "Feat 1");
		var step = await steps.CreateAsync(feat.Id, "Step 1");
		await featureOrchestrator.MoveAsync(feat.Id, WorkflowColumn.Ready);
		await stepOrchestrator.MoveAsync(step.Id, WorkflowColumn.Build);

		// Agent is working on this step
		dbContext.AgentRuns.Add(new AgentRun
		{
			CardType = CardType.Step,
			CardId = step.Id,
			Status = AgentRunStatus.Working,
			StartedAt = DateTimeOffset.UtcNow
		});
		await dbContext.SaveChangesAsync();

		// In UI, GetAllowedMovesAsync returns empty:
		var uiAllowed = await stepOrchestrator.GetAllowedMovesAsync(step.Id);
		Assert.Empty(uiAllowed);

		// In MCP, actions are NOT blocked! Instead IsBlocked is true:
		var mcpActions = await mcpTools.GetAvailableActionsAsync(step.Id);
		Assert.Contains("submit_for_review", mcpActions.AvailableActions);
		Assert.True(mcpActions.IsBlocked);
	}

	[Fact]
	public async Task AddComment_And_GetComments_RoundTrip()
	{
		var board = await boards.CreateAsync("Board 1", "/repos/b1", 1);
		var feat = await features.CreateAsync(board.Id, "Feat 1");
		var step = await steps.CreateAsync(feat.Id, "Step 1");

		var added = await mcpTools.AddCommentAsync(step.Id, "Alice", "Investigating bug...");
		Assert.Equal("Step", added.CardType);
		Assert.Equal(step.Id, added.CardId);
		Assert.Equal("Alice", added.Author);
		Assert.Equal("Investigating bug...", added.Body);

		var list = await mcpTools.GetCommentsAsync(step.Id);
		Assert.Single(list);
		Assert.Equal("Alice", list[0].Author);
		Assert.Equal("Investigating bug...", list[0].Body);
	}

	[Fact]
	public void McpServerRegistration_WithToolsFromAssembly_DiscoversAllNamedActionsAndTools()
	{
		var services = new ServiceCollection();
		services.AddLogging();
		services.AddScoped<BoardMcpTools>(_ => mcpTools);
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
			Assert.False(string.IsNullOrWhiteSpace(tool.ProtocolTool.Description), $"Tool '{name}' should have a description.");
			Assert.True(tool.ProtocolTool.InputSchema.ValueKind != System.Text.Json.JsonValueKind.Undefined);
		}
	}
}
