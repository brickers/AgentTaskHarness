using AgentTaskHarness.Domain.Entities;
using AgentTaskHarness.Domain.Enums;
using AgentTaskHarness.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentTaskHarness.Application.Comments;

public class CommentService(AppDbContext dbContext)
{
	public async Task<Comment> AddCommentAsync(
		CardType cardType,
		Guid cardId,
		string author,
		string body,
		CancellationToken cancellationToken = default)
	{
		if (string.IsNullOrWhiteSpace(author)) throw new ArgumentException("Author is required.", nameof(author));

		if (author.Trim().Length > 200)
			throw new ArgumentException("Author must not exceed 200 characters.", nameof(author));

		if (string.IsNullOrWhiteSpace(body)) throw new ArgumentException("Comment body is required.", nameof(body));

		if (cardType == CardType.Feature)
		{
			var exists = await dbContext.Features.AnyAsync(f => f.Id == cardId, cancellationToken);
			if (!exists) throw new KeyNotFoundException($"Feature '{cardId}' was not found.");
		}
		else if (cardType == CardType.Step)
		{
			var exists = await dbContext.Steps.AnyAsync(s => s.Id == cardId, cancellationToken);
			if (!exists) throw new KeyNotFoundException($"Step '{cardId}' was not found.");
		}
		else
		{
			throw new ArgumentOutOfRangeException(nameof(cardType), cardType, "Unsupported card type.");
		}

		var comment = new Comment
		{
			Id = Guid.NewGuid(),
			CardType = cardType,
			CardId = cardId,
			Author = author.Trim(),
			Body = body.Trim(),
			CreatedAt = DateTimeOffset.UtcNow
		};

		dbContext.Comments.Add(comment);
		await dbContext.SaveChangesAsync(cancellationToken);
		return comment;
	}

	public async Task<IReadOnlyList<Comment>> GetCommentsAsync(
		CardType cardType,
		Guid cardId,
		CancellationToken cancellationToken = default)
	{
		var comments = await dbContext.Comments
			.AsNoTracking()
			.Where(c => c.CardType == cardType && c.CardId == cardId)
			.ToListAsync(cancellationToken);

		return comments.OrderBy(c => c.CreatedAt).ToList();
	}

	public async Task<int> GetCommentCountAsync(
		CardType cardType,
		Guid cardId,
		CancellationToken cancellationToken = default)
	{
		return await dbContext.Comments
			.Where(c => c.CardType == cardType && c.CardId == cardId)
			.CountAsync(cancellationToken);
	}

	public async Task<Dictionary<(CardType CardType, Guid CardId), int>> GetCommentCountsForBoardAsync(
		Guid boardId,
		CancellationToken cancellationToken = default)
	{
		var featureIds = await dbContext.Features
			.Where(f => f.BoardId == boardId)
			.Select(f => f.Id)
			.ToListAsync(cancellationToken);

		var stepIds = await dbContext.Steps
			.Where(s => featureIds.Contains(s.FeatureId))
			.Select(s => s.Id)
			.ToListAsync(cancellationToken);

		var featureCounts = await dbContext.Comments
			.Where(c => c.CardType == CardType.Feature && featureIds.Contains(c.CardId))
			.GroupBy(c => c.CardId)
			.Select(g => new { g.Key, Count = g.Count() })
			.ToListAsync(cancellationToken);

		var stepCounts = await dbContext.Comments
			.Where(c => c.CardType == CardType.Step && stepIds.Contains(c.CardId))
			.GroupBy(c => c.CardId)
			.Select(g => new { g.Key, Count = g.Count() })
			.ToListAsync(cancellationToken);

		var result = new Dictionary<(CardType CardType, Guid CardId), int>();

		foreach (var fc in featureCounts) result[(CardType.Feature, fc.Key)] = fc.Count;

		foreach (var sc in stepCounts) result[(CardType.Step, sc.Key)] = sc.Count;

		return result;
	}

	public async Task DeleteCommentAsync(Guid commentId, CancellationToken cancellationToken = default)
	{
		var comment = await dbContext.Comments
			              .SingleOrDefaultAsync(c => c.Id == commentId, cancellationToken)
		              ?? throw new KeyNotFoundException($"Comment '{commentId}' was not found.");

		dbContext.Comments.Remove(comment);
		await dbContext.SaveChangesAsync(cancellationToken);
	}
}