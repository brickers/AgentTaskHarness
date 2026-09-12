using AgentTaskHarness.Application.Abstractions;
using AgentTaskHarness.Application.Reviews;
using AgentTaskHarness.Application.Steps;
using AgentTaskHarness.Domain.Entities;
using AgentTaskHarness.Domain.Enums;
using AgentTaskHarness.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentTaskHarness.Application.Agents;

/// <summary>
/// Tunable constants for the agent scheduler prioritization algorithm.
/// Weights are explicitly left open per Solution Design ("exact weighting is to be tuned later").
/// </summary>
public static class SchedulerScoringWeights
{
	/// <summary>
	/// Tunable constant: weight applied to progress (card column progress + feature completion ratio).
	/// </summary>
	public const double DefaultProgressWeight = 1.0;

	/// <summary>
	/// Tunable constant: weight applied to remaining work (fewer remaining sibling steps in feature + unblocking dependents).
	/// </summary>
	public const double DefaultRemainingWorkWeight = 1.0;

	/// <summary>
	/// Tunable constant: weight applied to card age / queue wait time to prevent starvation.
	/// </summary>
	public const double DefaultAgeWeight = 0.5;
}

public class AgentSchedulerService(
	AppDbContext dbContext,
	AgentMatchingService agentMatchingService,
	ReviewOutcomeService reviewOutcomeService,
	StepDependencyService stepDependencyService,
	IAgentProcessRunner processRunner) : IAgentScheduler
{
	public async Task RequestStartAsync(Guid stepId, CancellationToken cancellationToken = default)
	{
		var step = await dbContext.Steps
			.Include(s => s.Feature)
				.ThenInclude(f => f.Board)
			.SingleOrDefaultAsync(s => s.Id == stepId, cancellationToken)
			?? throw new KeyNotFoundException($"Step '{stepId}' was not found.");

		// Only Build and Agent Review invoke agents
		if (step.WorkflowColumn is not (WorkflowColumn.Build or WorkflowColumn.AgentReview))
		{
			return;
		}

		// If a previous agent is still running on this card (e.g. MCP moved column while agent active),
		// set the soft "blocked: previous agent still active" status.
		var hasActiveAgent = await dbContext.AgentRuns
			.AnyAsync(r => r.CardType == CardType.Step && r.CardId == stepId &&
						   (r.Status == AgentRunStatus.Working || r.Status == AgentRunStatus.WaitingForInput),
				cancellationToken);

		if (hasActiveAgent)
		{
			var blockedRun = new AgentRun
			{
				CardType = CardType.Step,
				CardId = stepId,
				Status = AgentRunStatus.Blocked,
				StartedAt = DateTimeOffset.UtcNow
			};
			dbContext.AgentRuns.Add(blockedRun);
			await dbContext.SaveChangesAsync(cancellationToken);
			return;
		}

		var board = step.Feature?.Board;

		// Failure-threshold skip: a Step whose counters have crossed the board's fail threshold
		// is left queued/flagged rather than auto-started.
		var hasThresholdIssue = board != null && reviewOutcomeService.HasIssue(step, board);
		if (hasThresholdIssue)
		{
			var existingQueued = await dbContext.AgentRuns
				.FirstOrDefaultAsync(r => r.CardType == CardType.Step && r.CardId == stepId && r.Status == AgentRunStatus.Queued, cancellationToken);
			if (existingQueued == null)
			{
				dbContext.AgentRuns.Add(new AgentRun
				{
					CardType = CardType.Step,
					CardId = stepId,
					Status = AgentRunStatus.Queued,
					StartedAt = DateTimeOffset.UtcNow
				});
				await dbContext.SaveChangesAsync(cancellationToken);
			}
			return;
		}

		// Ensure queued run exists for this step
		var existingRun = await dbContext.AgentRuns
			.FirstOrDefaultAsync(r => r.CardType == CardType.Step && r.CardId == stepId && r.Status == AgentRunStatus.Queued, cancellationToken);
		if (existingRun == null)
		{
			dbContext.AgentRuns.Add(new AgentRun
			{
				CardType = CardType.Step,
				CardId = stepId,
				Status = AgentRunStatus.Queued,
				StartedAt = DateTimeOffset.UtcNow
			});
			await dbContext.SaveChangesAsync(cancellationToken);
		}

		// Process the queue for the board
		if (board != null)
		{
			await ProcessQueueAsync(board.Id, cancellationToken);
		}
	}

	public async Task ProcessQueueAsync(Guid boardId, CancellationToken cancellationToken = default)
	{
		var board = await dbContext.Boards
			.SingleOrDefaultAsync(b => b.Id == boardId, cancellationToken)
			?? throw new KeyNotFoundException($"Board '{boardId}' was not found.");

		// Count active runs on this board
		var activeStepRunsCount = await dbContext.AgentRuns
			.Where(r => r.CardType == CardType.Step &&
						(r.Status == AgentRunStatus.Working || r.Status == AgentRunStatus.WaitingForInput))
			.Join(dbContext.Steps.Include(s => s.Feature),
				r => r.CardId,
				s => s.Id,
				(r, s) => s.Feature.BoardId)
			.Where(bId => bId == boardId)
			.CountAsync(cancellationToken);

		var activeFeatureRunsCount = await dbContext.AgentRuns
			.Where(r => r.CardType == CardType.Feature &&
						(r.Status == AgentRunStatus.Working || r.Status == AgentRunStatus.WaitingForInput))
			.Join(dbContext.Features,
				r => r.CardId,
				f => f.Id,
				(r, f) => f.BoardId)
			.Where(bId => bId == boardId)
			.CountAsync(cancellationToken);

		var currentActive = activeStepRunsCount + activeFeatureRunsCount;
		var availableSlots = board.ConcurrencyLimit - currentActive;
		if (availableSlots <= 0)
		{
			return;
		}

		// Find queued runs for this board
		var queuedRuns = await dbContext.AgentRuns
			.Where(r => r.CardType == CardType.Step && r.Status == AgentRunStatus.Queued)
			.ToListAsync(cancellationToken);

		if (queuedRuns.Count == 0)
		{
			return;
		}

		var stepIds = queuedRuns.Select(r => r.CardId).Distinct().ToList();
		var steps = await dbContext.Steps
			.Include(s => s.Feature)
				.ThenInclude(f => f.Board)
			.Include(s => s.Feature)
				.ThenInclude(f => f.Steps)
			.Include(s => s.DependedOnBy)
			.Where(s => stepIds.Contains(s.Id) && s.Feature.BoardId == boardId)
			.ToListAsync(cancellationToken);

		var candidates = new List<(Step Step, AgentRun Run, AgentDefinition AgentDef, double Score)>();

		foreach (var step in steps)
		{
			// Verify eligible column
			if (step.WorkflowColumn is not (WorkflowColumn.Build or WorkflowColumn.AgentReview))
			{
				var orphanedRuns = queuedRuns.Where(r => r.CardId == step.Id).ToList();
				foreach (var orphaned in orphanedRuns)
				{
					orphaned.Status = AgentRunStatus.Stopped;
					orphaned.EndedAt = DateTimeOffset.UtcNow;
				}
				continue;
			}

			// Failure-threshold skip
			if (reviewOutcomeService.HasIssue(step, board))
			{
				continue;
			}

			// Cannot start if an agent is already active on this step
			var stepHasActiveAgent = await dbContext.AgentRuns.AnyAsync(r =>
				r.CardType == CardType.Step && r.CardId == step.Id &&
				(r.Status == AgentRunStatus.Working || r.Status == AgentRunStatus.WaitingForInput),
				cancellationToken);

			if (stepHasActiveAgent)
			{
				continue;
			}

			// In Build, verify step dependencies
			if (step.WorkflowColumn == WorkflowColumn.Build &&
			    !await stepDependencyService.AreDependenciesMetAsync(step.Id, cancellationToken))
			{
				continue;
			}

			// Match agent definition
			var scope = step.WorkflowColumn == WorkflowColumn.Build
				? ColumnScope.StepBuild
				: ColumnScope.StepAgentReview;

			var agentDef = await agentMatchingService.ResolveAgentAsync(step, scope, cancellationToken);
			if (agentDef == null)
			{
				continue;
			}

			var totalSteps = step.Feature?.Steps.Count ?? 1;
			var doneSteps = step.Feature?.Steps.Count(s => s.WorkflowColumn == WorkflowColumn.Done) ?? 0;
			var dependentCount = step.DependedOnBy.Count;
			var run = queuedRuns.Where(r => r.CardId == step.Id).OrderByDescending(r => r.StartedAt).First();

			var score = CalculatePriorityScore(step, totalSteps, doneSteps, dependentCount, run.StartedAt);
			candidates.Add((step, run, agentDef, score));
		}

		var toStart = candidates
			.OrderByDescending(c => c.Score)
			.ThenBy(c => c.Run.StartedAt)
			.Take(availableSlots)
			.ToList();

		foreach (var candidate in toStart)
		{
			var result = await processRunner.StartAsync(candidate.Step, candidate.AgentDef, cancellationToken);
			candidate.Run.Status = AgentRunStatus.Working;
			candidate.Run.ProcessId = result.ProcessId;
			candidate.Run.SessionLink = result.SessionLink;
			candidate.Run.StartedAt = DateTimeOffset.UtcNow;
		}

		await dbContext.SaveChangesAsync(cancellationToken);
	}

	public async Task OnAgentFinishedAsync(Guid stepId, CancellationToken cancellationToken = default)
	{
		var activeRuns = await dbContext.AgentRuns
			.Where(r => r.CardType == CardType.Step && r.CardId == stepId &&
						(r.Status == AgentRunStatus.Working || r.Status == AgentRunStatus.WaitingForInput))
			.ToListAsync(cancellationToken);

		var activeRun = activeRuns.OrderByDescending(r => r.StartedAt).FirstOrDefault();

		if (activeRun != null)
		{
			activeRun.Status = AgentRunStatus.Completed;
			activeRun.EndedAt = DateTimeOffset.UtcNow;
		}

		// If this step had a blocked run (due to soft MCP transition while previous agent was running),
		// unblock it to Queued now that outgoing agent has finished
		var blockedRun = await dbContext.AgentRuns
			.Where(r => r.CardType == CardType.Step && r.CardId == stepId && r.Status == AgentRunStatus.Blocked)
			.FirstOrDefaultAsync(cancellationToken);

		if (blockedRun != null)
		{
			blockedRun.Status = AgentRunStatus.Queued;
		}

		await dbContext.SaveChangesAsync(cancellationToken);

		var step = await dbContext.Steps
			.Include(s => s.Feature)
			.SingleOrDefaultAsync(s => s.Id == stepId, cancellationToken);

		if (step?.Feature != null)
		{
			await ProcessQueueAsync(step.Feature.BoardId, cancellationToken);
		}
	}

	public async Task StopAgentAsync(Guid stepId, CancellationToken cancellationToken = default)
	{
		var activeRuns = await dbContext.AgentRuns
			.Where(r => r.CardType == CardType.Step && r.CardId == stepId &&
						(r.Status == AgentRunStatus.Working || r.Status == AgentRunStatus.WaitingForInput))
			.ToListAsync(cancellationToken);

		var activeRun = activeRuns.OrderByDescending(r => r.StartedAt).FirstOrDefault();

		if (activeRun != null)
		{
			if (activeRun.ProcessId.HasValue)
			{
				await processRunner.StopAsync(activeRun.ProcessId.Value, cancellationToken);
			}
			activeRun.Status = AgentRunStatus.Stopped;
			activeRun.EndedAt = DateTimeOffset.UtcNow;
		}

		// Also unblock any blocked run
		var blockedRun = await dbContext.AgentRuns
			.Where(r => r.CardType == CardType.Step && r.CardId == stepId && r.Status == AgentRunStatus.Blocked)
			.FirstOrDefaultAsync(cancellationToken);

		if (blockedRun != null)
		{
			blockedRun.Status = AgentRunStatus.Queued;
		}

		await dbContext.SaveChangesAsync(cancellationToken);

		var step = await dbContext.Steps
			.Include(s => s.Feature)
			.SingleOrDefaultAsync(s => s.Id == stepId, cancellationToken);

		if (step?.Feature != null)
		{
			await ProcessQueueAsync(step.Feature.BoardId, cancellationToken);
		}
	}

	public Task<bool> IsAgentRunningAsync(Guid stepId, CancellationToken cancellationToken = default)
	{
		return dbContext.AgentRuns.AnyAsync(r =>
			r.CardType == CardType.Step && r.CardId == stepId &&
			(r.Status == AgentRunStatus.Working || r.Status == AgentRunStatus.WaitingForInput),
			cancellationToken);
	}

	public async Task<AgentRunStatus?> GetStepAgentStatusAsync(Guid stepId, CancellationToken cancellationToken = default)
	{
		var run = await GetCurrentRunAsync(stepId, cancellationToken);
		return run?.Status;
	}

	public async Task<AgentRun?> GetCurrentRunAsync(Guid stepId, CancellationToken cancellationToken = default)
	{
		var runs = await dbContext.AgentRuns
			.Where(r => r.CardType == CardType.Step && r.CardId == stepId)
			.ToListAsync(cancellationToken);

		return runs.OrderByDescending(r => r.StartedAt).FirstOrDefault();
	}

	/// <summary>
	/// Computes the prioritization score for ordering queued steps on a board.
	/// Factors: progress, remaining work, and age.
	/// Weightings are tunable constants per Solution Design ("exact weighting is to be tuned later").
	/// </summary>
	public static double CalculatePriorityScore(
		Step step,
		int totalStepsInFeature,
		int doneStepsInFeature,
		int dependentStepsCount,
		DateTimeOffset? queuedAt = null,
		double progressWeight = SchedulerScoringWeights.DefaultProgressWeight,
		double remainingWorkWeight = SchedulerScoringWeights.DefaultRemainingWorkWeight,
		double ageWeight = SchedulerScoringWeights.DefaultAgeWeight)
	{
		ArgumentNullException.ThrowIfNull(step);

		// 1. Progress factor:
		// AgentReview is further along than Build (5 vs 2).
		// Plus percentage of sibling steps completed in the parent feature (up to 5 points).
		double columnProgress = step.WorkflowColumn == WorkflowColumn.AgentReview ? 5.0 : 2.0;
		double featureProgress = totalStepsInFeature > 0
			? ((double)doneStepsInFeature / totalStepsInFeature) * 5.0
			: 0.0;
		double progressScore = columnProgress + featureProgress;

		// 2. Remaining work factor:
		// Closing out nearly completed features gets a boost (fewer remaining steps -> higher score).
		int remainingSteps = Math.Max(1, totalStepsInFeature - doneStepsInFeature);
		double remainingStepsScore = 5.0 / remainingSteps;
		// Unblocking other steps: each step depending on this one adds priority (up to 5 points).
		double unblockingScore = Math.Min(5.0, dependentStepsCount * 1.5);
		double remainingWorkScore = remainingStepsScore + unblockingScore;

		// 3. Age factor:
		// Prevents starvation by boosting steps waiting longer in the queue.
		var referenceTime = queuedAt ?? step.CreatedAt;
		var waitMinutes = Math.Max(0, (DateTimeOffset.UtcNow - referenceTime).TotalMinutes);
		double ageScore = Math.Min(10.0, waitMinutes * 0.1);

		return (progressScore * progressWeight) +
		       (remainingWorkScore * remainingWorkWeight) +
		       (ageScore * ageWeight);
	}
}
