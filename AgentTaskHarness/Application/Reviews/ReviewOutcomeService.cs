using AgentTaskHarness.Domain.Entities;
using AgentTaskHarness.Domain.Enums;
using AgentTaskHarness.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentTaskHarness.Application.Reviews;

public class ReviewOutcomeService(AppDbContext dbContext)
{
	public bool HandleTransition(Feature feature, WorkflowColumn fromColumn, WorkflowColumn toColumn)
	{
		if (fromColumn is WorkflowColumn.AgentReview or WorkflowColumn.HumanReview && toColumn == WorkflowColumn.Build)
		{
			RecordReviewFailure(feature, fromColumn);
			return true;
		}

		return false;
	}

	public bool HandleTransition(Step step, WorkflowColumn fromColumn, WorkflowColumn toColumn)
	{
		if (fromColumn is WorkflowColumn.AgentReview or WorkflowColumn.HumanReview && toColumn == WorkflowColumn.Build)
		{
			RecordReviewFailure(step, fromColumn);
			return true;
		}

		return false;
	}

	public void RecordReviewFailure(Feature feature, WorkflowColumn reviewColumn)
	{
		ArgumentNullException.ThrowIfNull(feature);

		if (reviewColumn == WorkflowColumn.AgentReview)
		{
			feature.AgentReviewFailCount++;
		}
		else if (reviewColumn == WorkflowColumn.HumanReview)
		{
			feature.HumanReviewFailCount++;
		}
		else
		{
			throw new ArgumentException($"Only AgentReview and HumanReview failures can be recorded. Given: '{reviewColumn}'.", nameof(reviewColumn));
		}
	}

	public void RecordReviewFailure(Step step, WorkflowColumn reviewColumn)
	{
		ArgumentNullException.ThrowIfNull(step);

		if (reviewColumn == WorkflowColumn.AgentReview)
		{
			step.AgentReviewFailCount++;
		}
		else if (reviewColumn == WorkflowColumn.HumanReview)
		{
			step.HumanReviewFailCount++;
		}
		else
		{
			throw new ArgumentException($"Only AgentReview and HumanReview failures can be recorded. Given: '{reviewColumn}'.", nameof(reviewColumn));
		}
	}

	public async Task RecordReviewFailureAsync(CardType cardType, Guid cardId, WorkflowColumn reviewColumn, CancellationToken cancellationToken = default)
	{
		if (cardType == CardType.Feature)
		{
			var feature = await dbContext.Features
				.SingleOrDefaultAsync(f => f.Id == cardId, cancellationToken)
				?? throw new KeyNotFoundException($"Feature '{cardId}' was not found.");

			RecordReviewFailure(feature, reviewColumn);
		}
		else if (cardType == CardType.Step)
		{
			var step = await dbContext.Steps
				.SingleOrDefaultAsync(s => s.Id == cardId, cancellationToken)
				?? throw new KeyNotFoundException($"Step '{cardId}' was not found.");

			RecordReviewFailure(step, reviewColumn);
		}
		else
		{
			throw new ArgumentOutOfRangeException(nameof(cardType), cardType, "Unsupported card type.");
		}

		await dbContext.SaveChangesAsync(cancellationToken);
	}

	public bool IsRework(WorkflowColumn column, int agentFailCount, int humanFailCount)
		=> column == WorkflowColumn.Build && (agentFailCount > 0 || humanFailCount > 0);

	public bool IsRework(Feature feature)
	{
		ArgumentNullException.ThrowIfNull(feature);
		return IsRework(feature.WorkflowColumn, feature.AgentReviewFailCount, feature.HumanReviewFailCount);
	}

	public bool IsRework(Step step)
	{
		ArgumentNullException.ThrowIfNull(step);
		return IsRework(step.WorkflowColumn, step.AgentReviewFailCount, step.HumanReviewFailCount);
	}

	public bool HasExceededThreshold(int count, int threshold)
		=> threshold > 0 && count >= threshold;

	public bool HasIssue(int agentFailCount, int humanFailCount, int agentThreshold, int humanThreshold)
		=> HasExceededThreshold(agentFailCount, agentThreshold) || HasExceededThreshold(humanFailCount, humanThreshold);

	public bool HasIssue(Feature feature, Board board)
	{
		ArgumentNullException.ThrowIfNull(feature);
		ArgumentNullException.ThrowIfNull(board);
		return HasIssue(feature.AgentReviewFailCount, feature.HumanReviewFailCount, board.AgentReviewFailThreshold, board.HumanReviewFailThreshold);
	}

	public bool HasIssue(Step step, Board board)
	{
		ArgumentNullException.ThrowIfNull(step);
		ArgumentNullException.ThrowIfNull(board);
		return HasIssue(step.AgentReviewFailCount, step.HumanReviewFailCount, board.AgentReviewFailThreshold, board.HumanReviewFailThreshold);
	}

	public ReviewOutcomeStatus GetStatus(WorkflowColumn column, int agentFailCount, int humanFailCount, int agentThreshold, int humanThreshold)
	{
		var agentThresholdCrossed = HasExceededThreshold(agentFailCount, agentThreshold);
		var humanThresholdCrossed = HasExceededThreshold(humanFailCount, humanThreshold);
		var hasIssue = agentThresholdCrossed || humanThresholdCrossed;
		var isRework = IsRework(column, agentFailCount, humanFailCount);

		return new ReviewOutcomeStatus(
			IsRework: isRework,
			HasIssue: hasIssue,
			AgentReviewFailCount: agentFailCount,
			HumanReviewFailCount: humanFailCount,
			AgentReviewFailThreshold: agentThreshold,
			HumanReviewFailThreshold: humanThreshold,
			AgentReviewThresholdCrossed: agentThresholdCrossed,
			HumanReviewThresholdCrossed: humanThresholdCrossed);
	}

	public ReviewOutcomeStatus GetStatus(Feature feature, Board board)
	{
		ArgumentNullException.ThrowIfNull(feature);
		ArgumentNullException.ThrowIfNull(board);
		return GetStatus(feature.WorkflowColumn, feature.AgentReviewFailCount, feature.HumanReviewFailCount, board.AgentReviewFailThreshold, board.HumanReviewFailThreshold);
	}

	public ReviewOutcomeStatus GetStatus(Step step, Board board)
	{
		ArgumentNullException.ThrowIfNull(step);
		ArgumentNullException.ThrowIfNull(board);
		return GetStatus(step.WorkflowColumn, step.AgentReviewFailCount, step.HumanReviewFailCount, board.AgentReviewFailThreshold, board.HumanReviewFailThreshold);
	}

	public async Task<ReviewOutcomeStatus> GetStatusForFeatureAsync(Guid featureId, CancellationToken cancellationToken = default)
	{
		var feature = await dbContext.Features
			.Include(f => f.Board)
			.AsNoTracking()
			.SingleOrDefaultAsync(f => f.Id == featureId, cancellationToken)
			?? throw new KeyNotFoundException($"Feature '{featureId}' was not found.");

		return GetStatus(feature, feature.Board);
	}

	public async Task<ReviewOutcomeStatus> GetStatusForStepAsync(Guid stepId, CancellationToken cancellationToken = default)
	{
		var step = await dbContext.Steps
			.Include(s => s.Feature)
				.ThenInclude(f => f.Board)
			.AsNoTracking()
			.SingleOrDefaultAsync(s => s.Id == stepId, cancellationToken)
			?? throw new KeyNotFoundException($"Step '{stepId}' was not found.");

		return GetStatus(step, step.Feature.Board);
	}
}
