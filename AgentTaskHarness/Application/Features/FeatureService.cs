using AgentTaskHarness.Domain.Entities;
using AgentTaskHarness.Domain.Enums;
using AgentTaskHarness.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentTaskHarness.Application.Features;

public class FeatureService(AppDbContext dbContext)
{
	public async Task<Feature> CreateAsync(
		Guid boardId,
		string title,
		string requirements = "",
		string acceptanceCriteria = "",
		string suggestedSolution = "",
		bool alwaysRequireHumanReview = false,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(title);

		var boardExists = await dbContext.Boards.AnyAsync(b => b.Id == boardId, cancellationToken);
		if (!boardExists) throw new KeyNotFoundException($"Board '{boardId}' was not found.");

		var feature = new Feature
		{
			BoardId = boardId,
			Title = title.Trim(),
			Requirements = requirements.Trim(),
			AcceptanceCriteria = acceptanceCriteria.Trim(),
			SuggestedSolution = suggestedSolution.Trim(),
			WorkflowColumn = WorkflowColumn.Backlog,
			AlwaysRequireHumanReview = alwaysRequireHumanReview,
			AgentReviewFailCount = 0,
			HumanReviewFailCount = 0
		};

		dbContext.Features.Add(feature);
		await dbContext.SaveChangesAsync(cancellationToken);
		return feature;
	}

	public async Task<Feature?> GetByIdAsync(Guid featureId, CancellationToken cancellationToken = default)
	{
		var feature = await dbContext.Features
			.Include(f => f.Steps)
			.Include(f => f.Dependencies).ThenInclude(d => d.DependsOnFeature)
			.Include(f => f.DependedOnBy).ThenInclude(d => d.Feature)
			.SingleOrDefaultAsync(f => f.Id == featureId, cancellationToken);

		if (feature is not null) feature.Steps = feature.Steps.OrderBy(s => s.CreatedAt).ToList();

		return feature;
	}

	public async Task<List<Feature>> GetForBoardAsync(Guid boardId, CancellationToken cancellationToken = default)
	{
		var features = await dbContext.Features
			.AsNoTracking()
			.Where(f => f.BoardId == boardId)
			.ToListAsync(cancellationToken);

		return features.OrderBy(f => f.CreatedAt).ToList();
	}

	public async Task<Feature> UpdateAsync(
		Guid featureId,
		string title,
		string requirements,
		string acceptanceCriteria,
		string suggestedSolution,
		bool alwaysRequireHumanReview,
		CancellationToken cancellationToken = default)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(title);

		var feature = await FindFeatureAsync(featureId, cancellationToken);
		feature.Title = title.Trim();
		feature.Requirements = requirements.Trim();
		feature.AcceptanceCriteria = acceptanceCriteria.Trim();
		feature.SuggestedSolution = suggestedSolution.Trim();
		feature.AlwaysRequireHumanReview = alwaysRequireHumanReview;

		await dbContext.SaveChangesAsync(cancellationToken);
		return feature;
	}

	public async Task DeleteAsync(Guid featureId, CancellationToken cancellationToken = default)
	{
		var feature = await FindFeatureAsync(featureId, cancellationToken);

		var stepIds = await dbContext.Steps
			.Where(s => s.FeatureId == featureId)
			.Select(s => s.Id)
			.ToListAsync(cancellationToken);

		var hasActiveAgent = await dbContext.AgentRuns.AnyAsync(r =>
				((r.CardType == CardType.Feature && r.CardId == featureId) ||
				 (r.CardType == CardType.Step && stepIds.Contains(r.CardId))) &&
				(r.Status == AgentRunStatus.Working || r.Status == AgentRunStatus.WaitingForInput),
			cancellationToken);

		if (hasActiveAgent) throw new InvalidOperationException("Cannot delete a feature while an agent is running.");

		dbContext.Features.Remove(feature);
		await dbContext.SaveChangesAsync(cancellationToken);
	}

	private async Task<Feature> FindFeatureAsync(Guid featureId, CancellationToken cancellationToken)
	{
		return await dbContext.Features.SingleOrDefaultAsync(f => f.Id == featureId, cancellationToken)
		       ?? throw new KeyNotFoundException($"Feature '{featureId}' was not found.");
	}
}