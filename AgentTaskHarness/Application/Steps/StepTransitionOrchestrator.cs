using AgentTaskHarness.Application.Abstractions;
using AgentTaskHarness.Application.Agents;
using AgentTaskHarness.Application.Features;
using AgentTaskHarness.Application.Reviews;
using AgentTaskHarness.Application.Workflow;
using AgentTaskHarness.Domain.Entities;
using AgentTaskHarness.Domain.Enums;
using AgentTaskHarness.Infrastructure.Git;
using AgentTaskHarness.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentTaskHarness.Application.Steps;

public class StepTransitionOrchestrator(
	AppDbContext dbContext,
	WorkflowTransitionRules rules,
	StepDependencyService dependencyService,
	FeatureTransitionOrchestrator featureTransitionOrchestrator,
	ReviewOutcomeService reviewOutcomeService,
	IGitWorktreeService? gitWorktrees = null,
	IAgentScheduler? agentScheduler = null)
{
	private readonly IGitWorktreeService _gitWorktrees = gitWorktrees ?? new NoOpGitWorktreeService();

	public Task<Step> MoveAsync(Guid stepId, WorkflowColumn targetColumn, CancellationToken cancellationToken = default)
	{
		return MoveAsync(stepId, targetColumn, false, false, cancellationToken);
	}

	public Task<Step> MoveAndDiscardWorktreeAsync(Guid stepId, WorkflowColumn targetColumn,
		CancellationToken cancellationToken = default)
	{
		return MoveAsync(stepId, targetColumn, true, false, cancellationToken);
	}

	public Task<Step> MoveAsync(Guid stepId, WorkflowColumn targetColumn, bool discardWorktreeOnBacklogReturn,
		CancellationToken cancellationToken = default)
	{
		return MoveAsync(stepId, targetColumn, discardWorktreeOnBacklogReturn, false, cancellationToken);
	}

	public Task<Step> MoveMcpAsync(Guid stepId, WorkflowColumn targetColumn,
		CancellationToken cancellationToken = default)
	{
		return MoveAsync(stepId, targetColumn, false, true, cancellationToken);
	}

	public async Task<Step> MoveAsync(Guid stepId, WorkflowColumn targetColumn, bool discardWorktreeOnBacklogReturn,
		bool isMcpMove, CancellationToken cancellationToken = default)
	{
		var step = await dbContext.Steps
			           .Include(s => s.Feature)
			           .ThenInclude(f => f.Board)
			           .SingleOrDefaultAsync(s => s.Id == stepId, cancellationToken)
		           ?? throw new KeyNotFoundException($"Step '{stepId}' was not found.");

		if (step.WorkflowColumn == WorkflowColumn.Done)
			throw new InvalidOperationException("Completed steps are terminal and cannot be moved.");

		// Hard-block UI moves if an agent is currently running. MCP moves allow the transition and apply the soft blocked flag.
		var hasActiveAgent = await dbContext.AgentRuns.AnyAsync(r =>
				r.CardType == CardType.Step && r.CardId == stepId &&
				(r.Status == AgentRunStatus.Working || r.Status == AgentRunStatus.WaitingForInput),
			cancellationToken);

		if (!isMcpMove && hasActiveAgent)
			throw new InvalidOperationException("Cannot move a step while an agent is running.");

		var canSkipHumanReview = step.Feature?.Board is not null && step.Feature.Board.SkipStepHumanReview &&
		                         !step.AlwaysRequireHumanReview;
		var effectiveTarget = targetColumn;

		// Skip human review auto-advance: when skip is active and moving from AgentReview, advance straight to Done instead of stopping at HumanReview
		if (step.WorkflowColumn == WorkflowColumn.AgentReview && targetColumn == WorkflowColumn.HumanReview &&
		    canSkipHumanReview) effectiveTarget = WorkflowColumn.Done;

		var isAllowed = rules.IsAllowedTransition(step.WorkflowColumn, effectiveTarget)
		                || (step.WorkflowColumn == WorkflowColumn.AgentReview &&
		                    effectiveTarget == WorkflowColumn.Done && canSkipHumanReview);

		if (!isAllowed)
			throw new InvalidOperationException(
				$"Moving from '{step.WorkflowColumn}' to '{targetColumn}' is not allowed.");

		if (step.MergeConflictPending)
			throw new InvalidOperationException("Resolve the pending merge conflict before moving this step.");

		// A Step can't move from Ready to Build until all its Step dependencies are complete
		if (effectiveTarget == WorkflowColumn.Build)
		{
			var dependenciesMet = await dependencyService.AreDependenciesMetAsync(stepId, cancellationToken);
			if (!dependenciesMet)
				throw new InvalidOperationException("All step dependencies must be completed before moving to Build.");

			// Trigger 1: first Step entering Build (Feature Ready -> Build)
			var feature = step.Feature ?? await dbContext.Features
				.SingleOrDefaultAsync(f => f.Id == step.FeatureId, cancellationToken);

			if (feature is not null && feature.WorkflowColumn == WorkflowColumn.Ready)
				await featureTransitionOrchestrator.MoveAsync(feature.Id, WorkflowColumn.Build, cancellationToken);
		}

		var previousColumn = step.WorkflowColumn;

		try
		{
			if (effectiveTarget == WorkflowColumn.Backlog && discardWorktreeOnBacklogReturn)
				await _gitWorktrees.DiscardStepWorktreeAsync(step, cancellationToken);
			else
				await _gitWorktrees.HandleStepTransitionAsync(step, previousColumn, effectiveTarget, cancellationToken);
		}
		catch (GitMergeConflictException)
		{
			step.MergeConflictPending = true;
			await dbContext.SaveChangesAsync(cancellationToken);
			throw;
		}

		step.WorkflowColumn = effectiveTarget;

		reviewOutcomeService.HandleTransition(step, previousColumn, effectiveTarget);

		await dbContext.SaveChangesAsync(cancellationToken);

		// Trigger 2: all Steps reaching Done (Feature Build -> AgentReview)
		if (step.WorkflowColumn == WorkflowColumn.Done)
		{
			var featureSteps = await dbContext.Steps
				.Where(s => s.FeatureId == step.FeatureId)
				.ToListAsync(cancellationToken);

			if (featureSteps.Count > 0 && featureSteps.All(s => s.WorkflowColumn == WorkflowColumn.Done))
			{
				var feature = step.Feature ?? await dbContext.Features
					.SingleOrDefaultAsync(f => f.Id == step.FeatureId, cancellationToken);

				if (feature is not null && feature.WorkflowColumn == WorkflowColumn.Build)
					await featureTransitionOrchestrator.MoveAsync(feature.Id, WorkflowColumn.AgentReview,
						cancellationToken);
			}
		}

		// Wire agent scheduler on entering Build or AgentReview
		if (step.WorkflowColumn is WorkflowColumn.Build or WorkflowColumn.AgentReview)
		{
			if (agentScheduler != null) await agentScheduler.RequestStartAsync(step.Id, cancellationToken);
		}
		else
		{
			// Leaving Build / AgentReview: clean up any queued or blocked runs
			var pendingRuns = await dbContext.AgentRuns
				.Where(r => r.CardType == CardType.Step && r.CardId == stepId &&
				            (r.Status == AgentRunStatus.Queued || r.Status == AgentRunStatus.Blocked))
				.ToListAsync(cancellationToken);

			foreach (var run in pendingRuns)
			{
				run.Status = AgentRunStatus.Stopped;
				run.EndedAt = DateTimeOffset.UtcNow;
			}

			if (isMcpMove && hasActiveAgent)
				dbContext.AgentRuns.Add(new AgentRun
				{
					CardType = CardType.Step,
					CardId = stepId,
					Status = AgentRunStatus.Blocked,
					StartedAt = DateTimeOffset.UtcNow
				});

			await dbContext.SaveChangesAsync(cancellationToken);
		}

		return step;
	}

	public async Task<Step> ResumeMergeAsync(Guid stepId, CancellationToken cancellationToken = default)
	{
		var step = await dbContext.Steps
			           .Include(s => s.Feature)
			           .ThenInclude(f => f.Board)
			           .SingleOrDefaultAsync(s => s.Id == stepId, cancellationToken)
		           ?? throw new KeyNotFoundException($"Step '{stepId}' was not found.");

		if (!step.MergeConflictPending)
			throw new InvalidOperationException("This step does not have a merge conflict pending.");

		await _gitWorktrees.ResumeStepMergeAsync(step, cancellationToken);
		step.WorkflowColumn = WorkflowColumn.Done;
		step.MergeConflictPending = false;
		await dbContext.SaveChangesAsync(cancellationToken);

		// Trigger 2: all Steps reaching Done (Feature Build -> AgentReview)
		var featureSteps = await dbContext.Steps
			.Where(s => s.FeatureId == step.FeatureId)
			.ToListAsync(cancellationToken);

		if (featureSteps.Count > 0 && featureSteps.All(s => s.WorkflowColumn == WorkflowColumn.Done))
		{
			var feature = step.Feature ?? await dbContext.Features
				.SingleOrDefaultAsync(f => f.Id == step.FeatureId, cancellationToken);

			if (feature is not null && feature.WorkflowColumn == WorkflowColumn.Build)
				await featureTransitionOrchestrator.MoveAsync(feature.Id, WorkflowColumn.AgentReview,
					cancellationToken);
		}

		return step;
	}

	public async Task DiscardUncommittedChangesAsync(Guid stepId, CancellationToken cancellationToken = default)
	{
		var step = await dbContext.Steps
			           .Include(s => s.Feature)
			           .ThenInclude(f => f.Board)
			           .SingleOrDefaultAsync(s => s.Id == stepId, cancellationToken)
		           ?? throw new KeyNotFoundException($"Step '{stepId}' was not found.");

		await _gitWorktrees.DiscardStepUncommittedChangesAsync(step, cancellationToken);
	}

	public async Task DiscardWorktreeAsync(Guid stepId, CancellationToken cancellationToken = default)
	{
		var step = await dbContext.Steps
			           .Include(s => s.Feature)
			           .ThenInclude(f => f.Board)
			           .SingleOrDefaultAsync(s => s.Id == stepId, cancellationToken)
		           ?? throw new KeyNotFoundException($"Step '{stepId}' was not found.");

		await _gitWorktrees.DiscardStepWorktreeAsync(step, cancellationToken);
		await dbContext.SaveChangesAsync(cancellationToken);
	}

	public async Task<IReadOnlyList<WorkflowColumn>> GetAllowedMovesAsync(Guid stepId,
		CancellationToken cancellationToken = default)
	{
		var step = await dbContext.Steps
			           .Include(s => s.Feature)
			           .ThenInclude(f => f.Board)
			           .SingleOrDefaultAsync(s => s.Id == stepId, cancellationToken)
		           ?? throw new KeyNotFoundException($"Step '{stepId}' was not found.");

		// UI hard-blocks moving a card while an agent is currently running
		var hasActiveAgent = await dbContext.AgentRuns.AnyAsync(r =>
				r.CardType == CardType.Step && r.CardId == stepId &&
				(r.Status == AgentRunStatus.Working || r.Status == AgentRunStatus.WaitingForInput),
			cancellationToken);

		if (step.WorkflowColumn == WorkflowColumn.Done || step.MergeConflictPending || hasActiveAgent) return [];

		var canSkipHumanReview = step.Feature?.Board is not null && step.Feature.Board.SkipStepHumanReview &&
		                         !step.AlwaysRequireHumanReview;
		var candidates = rules.GetAllowedTransitions(step.WorkflowColumn);
		var allowed = new List<WorkflowColumn>();

		foreach (var target in candidates)
		{
			if (target == WorkflowColumn.Build)
				if (!await dependencyService.AreDependenciesMetAsync(stepId, cancellationToken))
					continue;

			if (step.WorkflowColumn == WorkflowColumn.AgentReview && target == WorkflowColumn.HumanReview &&
			    canSkipHumanReview)
			{
				allowed.Add(WorkflowColumn.Done);
				continue;
			}

			allowed.Add(target);
		}

		return allowed;
	}
}