using AgentTaskHarness.Domain.Entities;
using AgentTaskHarness.Domain.Enums;
using AgentTaskHarness.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentTaskHarness.Application.Usage;

/// <summary>
/// Aggregated usage statistics for a Feature, rolling up token usage and time spent across all its Steps,
/// as well as any agent runs executed directly on the Feature itself.
/// </summary>
public record FeatureUsageSummary(
	Guid FeatureId,
	string Title,
	long TotalTokensUsed,
	TimeSpan TotalTimeSpent,
	long StepTokensUsed,
	TimeSpan StepTimeSpent,
	long FeatureTokensUsed,
	TimeSpan FeatureTimeSpent,
	int StepCount,
	IReadOnlyList<StepUsageSummary> StepSummaries
);

/// <summary>
/// Usage metrics for an individual Step within a Feature.
/// </summary>
public record StepUsageSummary(
	Guid StepId,
	Guid FeatureId,
	string Title,
	long TokensUsed,
	TimeSpan TimeSpent,
	WorkflowColumn WorkflowColumn,
	int RunCount
);

/// <summary>
/// Board-level usage rollup across all Features and Steps on the board.
/// </summary>
public record BoardUsageSummary(
	Guid BoardId,
	string BoardName,
	long TotalTokensUsed,
	TimeSpan TotalTimeSpent,
	int FeatureCount,
	int StepCount,
	IReadOnlyList<FeatureUsageSummary> FeatureSummaries
);

/// <summary>
/// Quality-signal metrics beyond cost and time tracking.
///
/// SOLUTION DESIGN NOTE (Cost &amp; Quality Tracking - Open Extension Point):
/// "Additional signals to help judge whether a Feature/Step was well-formed (e.g. whether its
/// original content was sufficient to reach the finished result without much back-and-forth)
/// are wanted; the concrete metrics (e.g. rework/return-to-backlog counts, agent-reported
/// missing-information events) are not yet decided — open question for a future iteration."
///
/// This record and extension point provide hooks for future quality metrics such as:
/// - Agent and Human review failure counts and churn ratios.
/// - Rework indicators (card returned from review to Build).
/// - Return-to-backlog count for dependency or requirement alterations.
/// - Agent clarification / missing-context requests during execution.
/// - First-pass completion rate.
/// </summary>
public record QualitySignals(
	Guid CardId,
	CardType CardType,
	int AgentReviewFailCount,
	int HumanReviewFailCount,
	int TotalReviewFailures,
	bool IsRework,
	double ChurnRatio = 0.0,
	int BacklogReturnCount = 0,
	IReadOnlyDictionary<string, object>? CustomSignals = null
);

public class UsageTrackingService(AppDbContext dbContext)
{
	/// <summary>
	/// Aggregates token and time usage across a Feature's Steps and any Feature-level agent runs.
	/// </summary>
	public async Task<FeatureUsageSummary> GetFeatureSummaryAsync(Guid featureId, CancellationToken cancellationToken = default)
	{
		var feature = await dbContext.Features
			.Include(f => f.Steps)
			.SingleOrDefaultAsync(f => f.Id == featureId, cancellationToken)
			?? throw new KeyNotFoundException($"Feature '{featureId}' was not found.");

		var steps = feature.Steps.OrderBy(s => s.CreatedAt).ToList();
		var stepIds = steps.Select(s => s.Id).ToList();

		var stepRuns = await dbContext.AgentRuns
			.AsNoTracking()
			.Where(r => r.CardType == CardType.Step && stepIds.Contains(r.CardId))
			.ToListAsync(cancellationToken);

		var stepRunCounts = stepRuns
			.GroupBy(r => r.CardId)
			.ToDictionary(g => g.Key, g => g.Count());

		var stepSummaries = steps.Select(s => new StepUsageSummary(
			s.Id,
			s.FeatureId,
			s.Title,
			s.TokensUsed,
			s.TimeSpent,
			s.WorkflowColumn,
			stepRunCounts.GetValueOrDefault(s.Id, 0)
		)).ToList();

		var stepTokens = stepSummaries.Sum(s => s.TokensUsed);
		var stepTime = stepSummaries.Aggregate(TimeSpan.Zero, (acc, s) => acc + s.TimeSpent);

		var featureRuns = await dbContext.AgentRuns
			.AsNoTracking()
			.Where(r => r.CardType == CardType.Feature && r.CardId == featureId)
			.ToListAsync(cancellationToken);

		var featureTokens = featureRuns.Sum(r => r.TokensUsed);
		var featureTime = featureRuns.Aggregate(TimeSpan.Zero, (acc, r) => acc + r.TimeSpent);

		return new FeatureUsageSummary(
			FeatureId: feature.Id,
			Title: feature.Title,
			TotalTokensUsed: stepTokens + featureTokens,
			TotalTimeSpent: stepTime + featureTime,
			StepTokensUsed: stepTokens,
			StepTimeSpent: stepTime,
			FeatureTokensUsed: featureTokens,
			FeatureTimeSpent: featureTime,
			StepCount: stepSummaries.Count,
			StepSummaries: stepSummaries
		);
	}

	/// <summary>
	/// Alias for <see cref="GetFeatureSummaryAsync"/> matching the implementation plan specification:
	/// UsageTrackingService.GetFeatureSummary(featureId)
	/// </summary>
	public Task<FeatureUsageSummary> GetFeatureSummary(Guid featureId) =>
		GetFeatureSummaryAsync(featureId);

	/// <summary>
	/// Returns usage summary for a single Step.
	/// </summary>
	public async Task<StepUsageSummary> GetStepSummaryAsync(Guid stepId, CancellationToken cancellationToken = default)
	{
		var step = await dbContext.Steps
			.SingleOrDefaultAsync(s => s.Id == stepId, cancellationToken)
			?? throw new KeyNotFoundException($"Step '{stepId}' was not found.");

		var runCount = await dbContext.AgentRuns
			.AsNoTracking()
			.CountAsync(r => r.CardType == CardType.Step && r.CardId == stepId, cancellationToken);

		return new StepUsageSummary(
			step.Id,
			step.FeatureId,
			step.Title,
			step.TokensUsed,
			step.TimeSpent,
			step.WorkflowColumn,
			runCount
		);
	}

	/// <summary>
	/// Returns a dictionary of FeatureUsageSummary for all features on a board, optimized for board-level UI rendering.
	/// </summary>
	public async Task<Dictionary<Guid, FeatureUsageSummary>> GetFeatureSummariesForBoardAsync(Guid boardId, CancellationToken cancellationToken = default)
	{
		var features = await dbContext.Features
			.Include(f => f.Steps)
			.Where(f => f.BoardId == boardId)
			.ToListAsync(cancellationToken);

		var result = new Dictionary<Guid, FeatureUsageSummary>();
		foreach (var feature in features)
		{
			var summary = await GetFeatureSummaryAsync(feature.Id, cancellationToken);
			result[feature.Id] = summary;
		}

		return result;
	}

	/// <summary>
	/// Returns aggregated board-level usage across all features and steps.
	/// </summary>
	public async Task<BoardUsageSummary> GetBoardSummaryAsync(Guid boardId, CancellationToken cancellationToken = default)
	{
		var board = await dbContext.Boards
			.SingleOrDefaultAsync(b => b.Id == boardId, cancellationToken)
			?? throw new KeyNotFoundException($"Board '{boardId}' was not found.");

		var featureSummariesMap = await GetFeatureSummariesForBoardAsync(boardId, cancellationToken);
		var summariesList = featureSummariesMap.Values.ToList();

		var totalTokens = summariesList.Sum(s => s.TotalTokensUsed);
		var totalTime = summariesList.Aggregate(TimeSpan.Zero, (acc, s) => acc + s.TotalTimeSpent);
		var totalSteps = summariesList.Sum(s => s.StepCount);

		return new BoardUsageSummary(
			BoardId: board.Id,
			BoardName: board.Name,
			TotalTokensUsed: totalTokens,
			TotalTimeSpent: totalTime,
			FeatureCount: summariesList.Count,
			StepCount: totalSteps,
			FeatureSummaries: summariesList
		);
	}

	/// <summary>
	/// Directly records incremental token and time usage on a step.
	/// </summary>
	public async Task RecordStepUsageAsync(
		Guid stepId,
		long tokensUsed,
		TimeSpan timeSpent,
		CancellationToken cancellationToken = default)
	{
		var step = await dbContext.Steps
			.SingleOrDefaultAsync(s => s.Id == stepId, cancellationToken)
			?? throw new KeyNotFoundException($"Step '{stepId}' was not found.");

		step.TokensUsed += tokensUsed;
		step.TimeSpent += timeSpent;

		await dbContext.SaveChangesAsync(cancellationToken);
	}

	/// <summary>
	/// Directly records usage onto an AgentRun and rolls it up to the associated step if applicable.
	/// </summary>
	public async Task RecordRunUsageAsync(
		Guid runId,
		long tokensUsed,
		TimeSpan timeSpent,
		CancellationToken cancellationToken = default)
	{
		var run = await dbContext.AgentRuns
			.SingleOrDefaultAsync(r => r.Id == runId, cancellationToken)
			?? throw new KeyNotFoundException($"Agent run '{runId}' was not found.");

		var tokenDelta = tokensUsed - run.TokensUsed;
		var timeDelta = timeSpent - run.TimeSpent;

		run.TokensUsed = tokensUsed;
		run.TimeSpent = timeSpent;

		if (run.CardType == CardType.Step)
		{
			var step = await dbContext.Steps
				.SingleOrDefaultAsync(s => s.Id == run.CardId, cancellationToken);

			if (step != null)
			{
				step.TokensUsed += Math.Max(0, tokenDelta);
				step.TimeSpent += (timeDelta > TimeSpan.Zero ? timeDelta : TimeSpan.Zero);
			}
		}

		await dbContext.SaveChangesAsync(cancellationToken);
	}

	/// <summary>
	/// Computes quality signals for a feature as an open extension point for future quality metrics.
	/// </summary>
	public async Task<QualitySignals> GetQualitySignalsAsync(Guid featureId, CancellationToken cancellationToken = default)
	{
		var feature = await dbContext.Features
			.Include(f => f.Steps)
			.SingleOrDefaultAsync(f => f.Id == featureId, cancellationToken)
			?? throw new KeyNotFoundException($"Feature '{featureId}' was not found.");

		var stepAgentFails = feature.Steps.Sum(s => s.AgentReviewFailCount);
		var stepHumanFails = feature.Steps.Sum(s => s.HumanReviewFailCount);

		var totalAgentFails = feature.AgentReviewFailCount + stepAgentFails;
		var totalHumanFails = feature.HumanReviewFailCount + stepHumanFails;
		var totalFails = totalAgentFails + totalHumanFails;

		var isRework = feature.AgentReviewFailCount > 0 || feature.HumanReviewFailCount > 0 ||
		               feature.Steps.Any(s => s.AgentReviewFailCount > 0 || s.HumanReviewFailCount > 0);

		var totalSteps = feature.Steps.Count;
		var churnRatio = totalSteps > 0 ? (double)totalFails / totalSteps : totalFails;

		return new QualitySignals(
			CardId: feature.Id,
			CardType: CardType.Feature,
			AgentReviewFailCount: totalAgentFails,
			HumanReviewFailCount: totalHumanFails,
			TotalReviewFailures: totalFails,
			IsRework: isRework,
			ChurnRatio: churnRatio
		);
	}
}
