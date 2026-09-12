using System.ComponentModel;
using AgentTaskHarness.Application.Comments;
using AgentTaskHarness.Application.Features;
using AgentTaskHarness.Application.Reviews;
using AgentTaskHarness.Application.Steps;
using AgentTaskHarness.Domain.Entities;
using AgentTaskHarness.Domain.Enums;
using AgentTaskHarness.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace AgentTaskHarness.Infrastructure.Mcp;

public record CardActionResult(
	Guid CardId,
	string CardType,
	string WorkflowColumn,
	string Action,
	string Message,
	bool IsBlocked);

public record CardDetailsResult(
	Guid CardId,
	string CardType,
	string Title,
	string WorkflowColumn,
	Guid BoardId,
	Guid? FeatureId,
	string? Description,
	string? Requirements,
	string? AcceptanceCriteria,
	string? SuggestedSolution,
	string? GuidanceNotes,
	bool AlwaysRequireHumanReview,
	int AgentReviewFailCount,
	int HumanReviewFailCount,
	bool IsRework,
	bool HasIssue,
	bool MergeConflictPending,
	string? BranchName,
	string? WorktreePath,
	string? ActiveAgentStatus,
	int CommentsCount,
	IReadOnlyList<string> Dependencies,
	IReadOnlyList<Guid>? StepIds,
	int? TotalSteps,
	int? DoneSteps);

public record AvailableActionsResult(
	Guid CardId,
	string CardType,
	string CurrentColumn,
	IReadOnlyList<string> AvailableActions,
	bool IsBlocked);

public record CommentResult(
	Guid Id,
	string CardType,
	Guid CardId,
	string Author,
	string Body,
	DateTimeOffset CreatedAt);

[McpServerToolType]
public class BoardMcpTools(
	FeatureTransitionOrchestrator featureOrchestrator,
	StepTransitionOrchestrator stepOrchestrator,
	FeatureDependencyService featureDependencyService,
	StepDependencyService stepDependencyService,
	ReviewOutcomeService reviewOutcomeService,
	CommentService commentService,
	AppDbContext dbContext)
{
	[McpServerTool(Name = "start_build"), Description("Starts building a card (Feature or Step) by transitioning it to Build (or Ready for Backlog Features).")]
	public async Task<CardActionResult> StartBuildAsync(
		[Description("The unique identifier of the card (Step or Feature).")] Guid cardId,
		[Description("Optional card type ('Step' or 'Feature'). Auto-detected if omitted.")] string? cardType = null,
		CancellationToken cancellationToken = default)
	{
		var (resolvedType, id) = await ResolveCardAsync(cardId, cardType, cancellationToken);

		if (resolvedType == CardType.Step)
		{
			var step = await dbContext.Steps.SingleAsync(s => s.Id == id, cancellationToken);
			if (step.WorkflowColumn != WorkflowColumn.Ready)
			{
				throw new InvalidOperationException($"Cannot start build for Step '{id}' currently in column '{step.WorkflowColumn}'. Step must be in 'Ready'.");
			}

			var moved = await stepOrchestrator.MoveMcpAsync(id, WorkflowColumn.Build, cancellationToken);
			var isBlocked = await IsCardBlockedAsync(CardType.Step, id, cancellationToken);

			return new CardActionResult(
				CardId: id,
				CardType: "Step",
				WorkflowColumn: moved.WorkflowColumn.ToString(),
				Action: "start_build",
				Message: $"Step '{moved.Title}' transitioned to Build.",
				IsBlocked: isBlocked);
		}
		else
		{
			var feature = await dbContext.Features.SingleAsync(f => f.Id == id, cancellationToken);
			var target = feature.WorkflowColumn switch
			{
				WorkflowColumn.Backlog => WorkflowColumn.Ready,
				WorkflowColumn.Ready => WorkflowColumn.Build,
				_ => throw new InvalidOperationException($"Cannot start build for Feature '{id}' currently in column '{feature.WorkflowColumn}'.")
			};

			var moved = await featureOrchestrator.MoveMcpAsync(id, target, cancellationToken);
			var isBlocked = await IsCardBlockedAsync(CardType.Feature, id, cancellationToken);

			return new CardActionResult(
				CardId: id,
				CardType: "Feature",
				WorkflowColumn: moved.WorkflowColumn.ToString(),
				Action: "start_build",
				Message: $"Feature '{moved.Title}' transitioned to {moved.WorkflowColumn}.",
				IsBlocked: isBlocked);
		}
	}

	[McpServerTool(Name = "submit_for_review"), Description("Submits a card in Build for review, transitioning it to AgentReview.")]
	public async Task<CardActionResult> SubmitForReviewAsync(
		[Description("The unique identifier of the card (Step or Feature).")] Guid cardId,
		[Description("Optional card type ('Step' or 'Feature'). Auto-detected if omitted.")] string? cardType = null,
		[Description("Optional summary notes of completed work to record as a comment.")] string? notes = null,
		CancellationToken cancellationToken = default)
	{
		var (resolvedType, id) = await ResolveCardAsync(cardId, cardType, cancellationToken);

		if (!string.IsNullOrWhiteSpace(notes))
		{
			await commentService.AddCommentAsync(resolvedType, id, "Agent", notes.Trim(), cancellationToken);
		}

		if (resolvedType == CardType.Step)
		{
			var step = await dbContext.Steps.SingleAsync(s => s.Id == id, cancellationToken);
			if (step.WorkflowColumn != WorkflowColumn.Build)
			{
				throw new InvalidOperationException($"Cannot submit Step '{id}' for review from column '{step.WorkflowColumn}'. Step must be in 'Build'.");
			}

			var moved = await stepOrchestrator.MoveMcpAsync(id, WorkflowColumn.AgentReview, cancellationToken);
			var isBlocked = await IsCardBlockedAsync(CardType.Step, id, cancellationToken);

			return new CardActionResult(
				CardId: id,
				CardType: "Step",
				WorkflowColumn: moved.WorkflowColumn.ToString(),
				Action: "submit_for_review",
				Message: $"Step '{moved.Title}' submitted for AgentReview.",
				IsBlocked: isBlocked);
		}
		else
		{
			var feature = await dbContext.Features.SingleAsync(f => f.Id == id, cancellationToken);
			if (feature.WorkflowColumn != WorkflowColumn.Build)
			{
				throw new InvalidOperationException($"Cannot submit Feature '{id}' for review from column '{feature.WorkflowColumn}'. Feature must be in 'Build'.");
			}

			var moved = await featureOrchestrator.MoveMcpAsync(id, WorkflowColumn.AgentReview, cancellationToken);
			var isBlocked = await IsCardBlockedAsync(CardType.Feature, id, cancellationToken);

			return new CardActionResult(
				CardId: id,
				CardType: "Feature",
				WorkflowColumn: moved.WorkflowColumn.ToString(),
				Action: "submit_for_review",
				Message: $"Feature '{moved.Title}' submitted for AgentReview.",
				IsBlocked: isBlocked);
		}
	}

	[McpServerTool(Name = "approve_review"), Description("Approves a review for a card in AgentReview or HumanReview, advancing it forward.")]
	public async Task<CardActionResult> ApproveReviewAsync(
		[Description("The unique identifier of the card (Step or Feature).")] Guid cardId,
		[Description("Optional card type ('Step' or 'Feature'). Auto-detected if omitted.")] string? cardType = null,
		[Description("Optional approval notes or feedback to record as a comment.")] string? notes = null,
		CancellationToken cancellationToken = default)
	{
		var (resolvedType, id) = await ResolveCardAsync(cardId, cardType, cancellationToken);

		if (!string.IsNullOrWhiteSpace(notes))
		{
			await commentService.AddCommentAsync(resolvedType, id, "Review Agent", notes.Trim(), cancellationToken);
		}

		if (resolvedType == CardType.Step)
		{
			var step = await dbContext.Steps.SingleAsync(s => s.Id == id, cancellationToken);
			var target = step.WorkflowColumn switch
			{
				WorkflowColumn.AgentReview => WorkflowColumn.HumanReview,
				WorkflowColumn.HumanReview => WorkflowColumn.Done,
				_ => throw new InvalidOperationException($"Cannot approve review for Step '{id}' in column '{step.WorkflowColumn}'. Must be in AgentReview or HumanReview.")
			};

			var moved = await stepOrchestrator.MoveMcpAsync(id, target, cancellationToken);
			var isBlocked = await IsCardBlockedAsync(CardType.Step, id, cancellationToken);

			return new CardActionResult(
				CardId: id,
				CardType: "Step",
				WorkflowColumn: moved.WorkflowColumn.ToString(),
				Action: "approve_review",
				Message: $"Step '{moved.Title}' approved and moved to {moved.WorkflowColumn}.",
				IsBlocked: isBlocked);
		}
		else
		{
			var feature = await dbContext.Features.SingleAsync(f => f.Id == id, cancellationToken);
			var target = feature.WorkflowColumn switch
			{
				WorkflowColumn.AgentReview => WorkflowColumn.HumanReview,
				WorkflowColumn.HumanReview => WorkflowColumn.Done,
				_ => throw new InvalidOperationException($"Cannot approve review for Feature '{id}' in column '{feature.WorkflowColumn}'. Must be in AgentReview or HumanReview.")
			};

			var moved = await featureOrchestrator.MoveMcpAsync(id, target, cancellationToken);
			var isBlocked = await IsCardBlockedAsync(CardType.Feature, id, cancellationToken);

			return new CardActionResult(
				CardId: id,
				CardType: "Feature",
				WorkflowColumn: moved.WorkflowColumn.ToString(),
				Action: "approve_review",
				Message: $"Feature '{moved.Title}' approved and moved to {moved.WorkflowColumn}.",
				IsBlocked: isBlocked);
		}
	}

	[McpServerTool(Name = "fail_review"), Description("Fails a review and returns the card to Build for rework, incrementing the review failure counter.")]
	public async Task<CardActionResult> FailReviewAsync(
		[Description("The unique identifier of the card (Step or Feature).")] Guid cardId,
		[Description("Optional card type ('Step' or 'Feature'). Auto-detected if omitted.")] string? cardType = null,
		[Description("Optional failure reason or rework instructions to record as a comment.")] string? reason = null,
		CancellationToken cancellationToken = default)
	{
		var (resolvedType, id) = await ResolveCardAsync(cardId, cardType, cancellationToken);

		if (!string.IsNullOrWhiteSpace(reason))
		{
			await commentService.AddCommentAsync(resolvedType, id, "Review Agent", reason.Trim(), cancellationToken);
		}

		if (resolvedType == CardType.Step)
		{
			var step = await dbContext.Steps.SingleAsync(s => s.Id == id, cancellationToken);
			if (step.WorkflowColumn is not (WorkflowColumn.AgentReview or WorkflowColumn.HumanReview))
			{
				throw new InvalidOperationException($"Cannot fail review for Step '{id}' in column '{step.WorkflowColumn}'. Must be in AgentReview or HumanReview.");
			}

			var moved = await stepOrchestrator.MoveMcpAsync(id, WorkflowColumn.Build, cancellationToken);
			var isBlocked = await IsCardBlockedAsync(CardType.Step, id, cancellationToken);

			return new CardActionResult(
				CardId: id,
				CardType: "Step",
				WorkflowColumn: moved.WorkflowColumn.ToString(),
				Action: "fail_review",
				Message: $"Step '{moved.Title}' review failed. Returned to Build for rework.",
				IsBlocked: isBlocked);
		}
		else
		{
			var feature = await dbContext.Features.SingleAsync(f => f.Id == id, cancellationToken);
			if (feature.WorkflowColumn is not (WorkflowColumn.AgentReview or WorkflowColumn.HumanReview))
			{
				throw new InvalidOperationException($"Cannot fail review for Feature '{id}' in column '{feature.WorkflowColumn}'. Must be in AgentReview or HumanReview.");
			}

			var moved = await featureOrchestrator.MoveMcpAsync(id, WorkflowColumn.Build, cancellationToken);
			var isBlocked = await IsCardBlockedAsync(CardType.Feature, id, cancellationToken);

			return new CardActionResult(
				CardId: id,
				CardType: "Feature",
				WorkflowColumn: moved.WorkflowColumn.ToString(),
				Action: "fail_review",
				Message: $"Feature '{moved.Title}' review failed. Returned to Build for rework.",
				IsBlocked: isBlocked);
		}
	}

	[McpServerTool(Name = "send_to_backlog"), Description("Sends a card back to Backlog from any non-Done column.")]
	public async Task<CardActionResult> SendToBacklogAsync(
		[Description("The unique identifier of the card (Step or Feature).")] Guid cardId,
		[Description("Optional card type ('Step' or 'Feature'). Auto-detected if omitted.")] string? cardType = null,
		[Description("Optional reason for returning to backlog to record as a comment.")] string? reason = null,
		[Description("Whether to discard the worktree on return to backlog (defaults to false, pausing it).")] bool discardWorktree = false,
		CancellationToken cancellationToken = default)
	{
		var (resolvedType, id) = await ResolveCardAsync(cardId, cardType, cancellationToken);

		if (!string.IsNullOrWhiteSpace(reason))
		{
			await commentService.AddCommentAsync(resolvedType, id, "Agent", reason.Trim(), cancellationToken);
		}

		if (resolvedType == CardType.Step)
		{
			var step = await dbContext.Steps.SingleAsync(s => s.Id == id, cancellationToken);
			if (step.WorkflowColumn == WorkflowColumn.Done)
			{
				throw new InvalidOperationException($"Completed step '{id}' is terminal and cannot be moved to Backlog.");
			}

			var moved = await stepOrchestrator.MoveAsync(id, WorkflowColumn.Backlog, discardWorktreeOnBacklogReturn: discardWorktree, isMcpMove: true, cancellationToken);
			var isBlocked = await IsCardBlockedAsync(CardType.Step, id, cancellationToken);

			return new CardActionResult(
				CardId: id,
				CardType: "Step",
				WorkflowColumn: moved.WorkflowColumn.ToString(),
				Action: "send_to_backlog",
				Message: $"Step '{moved.Title}' sent to Backlog.",
				IsBlocked: isBlocked);
		}
		else
		{
			var feature = await dbContext.Features.SingleAsync(f => f.Id == id, cancellationToken);
			if (feature.WorkflowColumn == WorkflowColumn.Done)
			{
				throw new InvalidOperationException($"Completed feature '{id}' is terminal and cannot be moved to Backlog.");
			}

			var moved = await featureOrchestrator.MoveAsync(id, WorkflowColumn.Backlog, discardWorktreeOnBacklogReturn: discardWorktree, isMcpMove: true, cancellationToken);
			var isBlocked = await IsCardBlockedAsync(CardType.Feature, id, cancellationToken);

			return new CardActionResult(
				CardId: id,
				CardType: "Feature",
				WorkflowColumn: moved.WorkflowColumn.ToString(),
				Action: "send_to_backlog",
				Message: $"Feature '{moved.Title}' sent to Backlog.",
				IsBlocked: isBlocked);
		}
	}

	[McpServerTool(Name = "get_card_details"), Description("Retrieves full details of a card (Feature or Step), including workflow state, review counters, review outcome status, comments count, and dependencies.")]
	public async Task<CardDetailsResult> GetCardDetailsAsync(
		[Description("The unique identifier of the card (Step or Feature).")] Guid cardId,
		[Description("Optional card type ('Step' or 'Feature'). Auto-detected if omitted.")] string? cardType = null,
		CancellationToken cancellationToken = default)
	{
		var (resolvedType, id) = await ResolveCardAsync(cardId, cardType, cancellationToken);

		if (resolvedType == CardType.Step)
		{
			var step = await dbContext.Steps
				.Include(s => s.Feature).ThenInclude(f => f.Board)
				.Include(s => s.Dependencies).ThenInclude(d => d.DependsOnStep)
				.SingleAsync(s => s.Id == id, cancellationToken);

			var commentsCount = await commentService.GetCommentCountAsync(CardType.Step, id, cancellationToken);
			var status = reviewOutcomeService.GetStatus(step, step.Feature.Board);

			var activeRun = (await dbContext.AgentRuns
				.Where(r => r.CardType == CardType.Step && r.CardId == id)
				.ToListAsync(cancellationToken))
				.OrderByDescending(r => r.StartedAt)
				.FirstOrDefault();

			return new CardDetailsResult(
				CardId: step.Id,
				CardType: "Step",
				Title: step.Title,
				WorkflowColumn: step.WorkflowColumn.ToString(),
				BoardId: step.Feature.BoardId,
				FeatureId: step.FeatureId,
				Description: step.Description,
				Requirements: step.Feature?.Requirements,
				AcceptanceCriteria: step.Feature?.AcceptanceCriteria,
				SuggestedSolution: step.Feature?.SuggestedSolution,
				GuidanceNotes: step.GuidanceNotes,
				AlwaysRequireHumanReview: step.AlwaysRequireHumanReview,
				AgentReviewFailCount: step.AgentReviewFailCount,
				HumanReviewFailCount: step.HumanReviewFailCount,
				IsRework: status.IsRework,
				HasIssue: status.HasIssue,
				MergeConflictPending: step.MergeConflictPending,
				BranchName: step.BranchName,
				WorktreePath: step.WorktreePath,
				ActiveAgentStatus: activeRun?.Status.ToString(),
				CommentsCount: commentsCount,
				Dependencies: step.Dependencies.Select(d => d.DependsOnStep?.Title ?? d.DependsOnStepId.ToString()).ToList(),
				StepIds: null,
				TotalSteps: null,
				DoneSteps: null);
		}
		else
		{
			var feature = await dbContext.Features
				.Include(f => f.Board)
				.Include(f => f.Steps)
				.Include(f => f.Dependencies).ThenInclude(d => d.DependsOnFeature)
				.SingleAsync(f => f.Id == id, cancellationToken);

			var commentsCount = await commentService.GetCommentCountAsync(CardType.Feature, id, cancellationToken);
			var status = reviewOutcomeService.GetStatus(feature, feature.Board);

			var activeRun = (await dbContext.AgentRuns
				.Where(r => r.CardType == CardType.Feature && r.CardId == id)
				.ToListAsync(cancellationToken))
				.OrderByDescending(r => r.StartedAt)
				.FirstOrDefault();

			return new CardDetailsResult(
				CardId: feature.Id,
				CardType: "Feature",
				Title: feature.Title,
				WorkflowColumn: feature.WorkflowColumn.ToString(),
				BoardId: feature.BoardId,
				FeatureId: null,
				Description: null,
				Requirements: feature.Requirements,
				AcceptanceCriteria: feature.AcceptanceCriteria,
				SuggestedSolution: feature.SuggestedSolution,
				GuidanceNotes: null,
				AlwaysRequireHumanReview: feature.AlwaysRequireHumanReview,
				AgentReviewFailCount: feature.AgentReviewFailCount,
				HumanReviewFailCount: feature.HumanReviewFailCount,
				IsRework: status.IsRework,
				HasIssue: status.HasIssue,
				MergeConflictPending: feature.MergeConflictPending,
				BranchName: feature.BranchName,
				WorktreePath: feature.WorktreePath,
				ActiveAgentStatus: activeRun?.Status.ToString(),
				CommentsCount: commentsCount,
				Dependencies: feature.Dependencies.Select(d => d.DependsOnFeature?.Title ?? d.DependsOnFeatureId.ToString()).ToList(),
				StepIds: feature.Steps.Select(s => s.Id).ToList(),
				TotalSteps: feature.Steps.Count,
				DoneSteps: feature.Steps.Count(s => s.WorkflowColumn == WorkflowColumn.Done));
		}
	}

	[McpServerTool(Name = "get_available_actions"), Description("Queries the list of curated named actions currently available for a card based on its workflow state and dependencies.")]
	public async Task<AvailableActionsResult> GetAvailableActionsAsync(
		[Description("The unique identifier of the card (Step or Feature).")] Guid cardId,
		[Description("Optional card type ('Step' or 'Feature'). Auto-detected if omitted.")] string? cardType = null,
		CancellationToken cancellationToken = default)
	{
		var (resolvedType, id) = await ResolveCardAsync(cardId, cardType, cancellationToken);
		var actions = new List<string>();

		if (resolvedType == CardType.Step)
		{
			var step = await dbContext.Steps.SingleAsync(s => s.Id == id, cancellationToken);

			if (!step.MergeConflictPending && step.WorkflowColumn != WorkflowColumn.Done)
			{
				switch (step.WorkflowColumn)
				{
					case WorkflowColumn.Backlog:
						break;
					case WorkflowColumn.Ready:
						var depsMet = await stepDependencyService.AreDependenciesMetAsync(id, cancellationToken);
						if (depsMet)
						{
							actions.Add("start_build");
						}
						actions.Add("send_to_backlog");
						break;
					case WorkflowColumn.Build:
						actions.Add("submit_for_review");
						actions.Add("send_to_backlog");
						break;
					case WorkflowColumn.AgentReview:
					case WorkflowColumn.HumanReview:
						actions.Add("approve_review");
						actions.Add("fail_review");
						actions.Add("send_to_backlog");
						break;
				}
			}

			var isBlocked = await IsCardBlockedAsync(CardType.Step, id, cancellationToken);

			return new AvailableActionsResult(
				CardId: id,
				CardType: "Step",
				CurrentColumn: step.WorkflowColumn.ToString(),
				AvailableActions: actions,
				IsBlocked: isBlocked);
		}
		else
		{
			var feature = await dbContext.Features.SingleAsync(f => f.Id == id, cancellationToken);

			if (!feature.MergeConflictPending && feature.WorkflowColumn != WorkflowColumn.Done)
			{
				switch (feature.WorkflowColumn)
				{
					case WorkflowColumn.Backlog:
						var depsMet = await featureDependencyService.AreDependenciesMetAsync(id, cancellationToken);
						if (depsMet)
						{
							actions.Add("start_build");
						}
						break;
					case WorkflowColumn.Ready:
						actions.Add("start_build");
						actions.Add("send_to_backlog");
						break;
					case WorkflowColumn.Build:
						actions.Add("submit_for_review");
						actions.Add("send_to_backlog");
						break;
					case WorkflowColumn.AgentReview:
					case WorkflowColumn.HumanReview:
						actions.Add("approve_review");
						actions.Add("fail_review");
						actions.Add("send_to_backlog");
						break;
				}
			}

			var isBlocked = await IsCardBlockedAsync(CardType.Feature, id, cancellationToken);

			return new AvailableActionsResult(
				CardId: id,
				CardType: "Feature",
				CurrentColumn: feature.WorkflowColumn.ToString(),
				AvailableActions: actions,
				IsBlocked: isBlocked);
		}
	}

	[McpServerTool(Name = "add_comment"), Description("Adds a new comment to a card (Feature or Step).")]
	public async Task<CommentResult> AddCommentAsync(
		[Description("The unique identifier of the card (Step or Feature).")] Guid cardId,
		[Description("The author name or agent identifier posting the comment.")] string author,
		[Description("The comment text.")] string body,
		[Description("Optional card type ('Step' or 'Feature'). Auto-detected if omitted.")] string? cardType = null,
		CancellationToken cancellationToken = default)
	{
		var (resolvedType, id) = await ResolveCardAsync(cardId, cardType, cancellationToken);
		var comment = await commentService.AddCommentAsync(resolvedType, id, author, body, cancellationToken);

		return new CommentResult(
			Id: comment.Id,
			CardType: comment.CardType.ToString(),
			CardId: comment.CardId,
			Author: comment.Author,
			Body: comment.Body,
			CreatedAt: comment.CreatedAt);
	}

	[McpServerTool(Name = "get_comments"), Description("Retrieves all comments for a card (Feature or Step) ordered chronologically.")]
	public async Task<IReadOnlyList<CommentResult>> GetCommentsAsync(
		[Description("The unique identifier of the card (Step or Feature).")] Guid cardId,
		[Description("Optional card type ('Step' or 'Feature'). Auto-detected if omitted.")] string? cardType = null,
		CancellationToken cancellationToken = default)
	{
		var (resolvedType, id) = await ResolveCardAsync(cardId, cardType, cancellationToken);
		var comments = await commentService.GetCommentsAsync(resolvedType, id, cancellationToken);

		return comments.Select(c => new CommentResult(
			Id: c.Id,
			CardType: c.CardType.ToString(),
			CardId: c.CardId,
			Author: c.Author,
			Body: c.Body,
			CreatedAt: c.CreatedAt)).ToList();
	}

	private async Task<(CardType CardType, Guid CardId)> ResolveCardAsync(
		Guid cardId,
		string? cardType,
		CancellationToken cancellationToken)
	{
		if (!string.IsNullOrWhiteSpace(cardType))
		{
			if (string.Equals(cardType, "Feature", StringComparison.OrdinalIgnoreCase) ||
				string.Equals(cardType, nameof(CardType.Feature), StringComparison.OrdinalIgnoreCase))
			{
				var exists = await dbContext.Features.AnyAsync(f => f.Id == cardId, cancellationToken);
				if (!exists)
				{
					throw new KeyNotFoundException($"Feature '{cardId}' was not found.");
				}
				return (CardType.Feature, cardId);
			}

			if (string.Equals(cardType, "Step", StringComparison.OrdinalIgnoreCase) ||
				string.Equals(cardType, nameof(CardType.Step), StringComparison.OrdinalIgnoreCase))
			{
				var exists = await dbContext.Steps.AnyAsync(s => s.Id == cardId, cancellationToken);
				if (!exists)
				{
					throw new KeyNotFoundException($"Step '{cardId}' was not found.");
				}
				return (CardType.Step, cardId);
			}

			throw new ArgumentException($"Invalid card type '{cardType}'. Must be 'Step' or 'Feature'.", nameof(cardType));
		}

		// Auto-detect: check Steps first, then Features
		var isStep = await dbContext.Steps.AnyAsync(s => s.Id == cardId, cancellationToken);
		if (isStep)
		{
			return (CardType.Step, cardId);
		}

		var isFeature = await dbContext.Features.AnyAsync(f => f.Id == cardId, cancellationToken);
		if (isFeature)
		{
			return (CardType.Feature, cardId);
		}

		throw new KeyNotFoundException($"Card '{cardId}' was not found as a Step or Feature.");
	}

	private async Task<bool> IsCardBlockedAsync(CardType cardType, Guid cardId, CancellationToken cancellationToken)
	{
		return await dbContext.AgentRuns.AnyAsync(r =>
			r.CardType == cardType && r.CardId == cardId &&
			(r.Status == AgentRunStatus.Blocked || r.Status == AgentRunStatus.Working || r.Status == AgentRunStatus.WaitingForInput),
			cancellationToken);
	}
}
