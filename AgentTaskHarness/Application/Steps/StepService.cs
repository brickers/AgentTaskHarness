using AgentTaskHarness.Domain.Entities;
using AgentTaskHarness.Domain.Enums;
using AgentTaskHarness.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentTaskHarness.Application.Steps;

public class StepService(AppDbContext dbContext)
{
	public async Task<Step> CreateAsync(
		Guid featureId,
		string title,
		string description = "",
		string guidanceNotes = "",
		bool alwaysRequireHumanReview = false,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(title);

		var featureExists = await dbContext.Features.AnyAsync(f => f.Id == featureId, cancellationToken);
		if (!featureExists)
		{
			throw new KeyNotFoundException($"Feature '{featureId}' was not found.");
		}

		var step = new Step
		{
			FeatureId = featureId,
			Title = title.Trim(),
			Description = description.Trim(),
			GuidanceNotes = guidanceNotes.Trim(),
			WorkflowColumn = WorkflowColumn.Backlog,
			AlwaysRequireHumanReview = alwaysRequireHumanReview,
			AgentReviewFailCount = 0,
			HumanReviewFailCount = 0,
			TokensUsed = 0,
			TimeSpent = TimeSpan.Zero
		};

		dbContext.Steps.Add(step);
		await dbContext.SaveChangesAsync(cancellationToken);
		return step;
	}

	public Task<Step?> GetByIdAsync(Guid stepId, CancellationToken cancellationToken = default) =>
		dbContext.Steps
			.Include(s => s.Feature)
			.Include(s => s.Dependencies).ThenInclude(d => d.DependsOnStep)
			.Include(s => s.DependedOnBy).ThenInclude(d => d.Step)
			.SingleOrDefaultAsync(s => s.Id == stepId, cancellationToken);

	public async Task<List<Step>> GetForFeatureAsync(Guid featureId, CancellationToken cancellationToken = default)
	{
		var steps = await dbContext.Steps
			.AsNoTracking()
			.Where(s => s.FeatureId == featureId)
			.ToListAsync(cancellationToken);

		return steps.OrderBy(s => s.CreatedAt).ToList();
	}

	public async Task<Step> UpdateAsync(
		Guid stepId,
		string title,
		string description,
		string guidanceNotes,
		bool alwaysRequireHumanReview,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(title);

		var step = await FindStepAsync(stepId, cancellationToken);
		step.Title = title.Trim();
		step.Description = description.Trim();
		step.GuidanceNotes = guidanceNotes.Trim();
		step.AlwaysRequireHumanReview = alwaysRequireHumanReview;

		await dbContext.SaveChangesAsync(cancellationToken);
		return step;
	}

	public async Task DeleteAsync(Guid stepId, CancellationToken cancellationToken = default)
	{
		var step = await FindStepAsync(stepId, cancellationToken);

		var hasActiveAgent = await dbContext.AgentRuns.AnyAsync(r =>
			r.CardType == CardType.Step && r.CardId == stepId &&
			(r.Status == AgentRunStatus.Working || r.Status == AgentRunStatus.WaitingForInput),
			cancellationToken);

		if (hasActiveAgent)
		{
			throw new InvalidOperationException("Cannot delete a step while an agent is running.");
		}

		dbContext.Steps.Remove(step);
		await dbContext.SaveChangesAsync(cancellationToken);
	}

	private async Task<Step> FindStepAsync(Guid stepId, CancellationToken cancellationToken) =>
		await dbContext.Steps.SingleOrDefaultAsync(s => s.Id == stepId, cancellationToken)
		?? throw new KeyNotFoundException($"Step '{stepId}' was not found.");
}
