using System.Text.Json;
using AgentTaskHarness.Domain.Entities;
using AgentTaskHarness.Domain.Enums;
using AgentTaskHarness.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentTaskHarness.Application.Agents;

public class AgentMatchingService(AppDbContext dbContext)
{
	public Task<List<AgentColumnAssignment>> GetAssignmentsAsync(Guid boardId,
		CancellationToken cancellationToken = default)
	{
		return dbContext.AgentColumnAssignments
			.AsNoTracking()
			.Include(assignment => assignment.AgentDefinition)
			.Where(assignment => assignment.BoardId == boardId)
			.OrderBy(assignment => assignment.ColumnScope)
			.ThenBy(assignment => assignment.Id)
			.ToListAsync(cancellationToken);
	}

	public Task<List<AgentColumnAssignment>> GetAssignmentsForScopeAsync(Guid boardId, ColumnScope columnScope,
		CancellationToken cancellationToken = default)
	{
		return dbContext.AgentColumnAssignments
			.AsNoTracking()
			.Include(assignment => assignment.AgentDefinition)
			.Where(assignment => assignment.BoardId == boardId && assignment.ColumnScope == columnScope)
			.OrderBy(assignment => assignment.Id)
			.ToListAsync(cancellationToken);
	}

	public async Task<AgentColumnAssignment> AssignAgentAsync(
		Guid boardId,
		ColumnScope columnScope,
		Guid agentDefinitionId,
		string? matchCriteria = null,
		CancellationToken cancellationToken = default)
	{
		ValidateScope(columnScope);

		if (!await dbContext.Boards.AnyAsync(board => board.Id == boardId, cancellationToken))
			throw new KeyNotFoundException($"Board '{boardId}' was not found.");

		var definition =
			await dbContext.AgentDefinitions.SingleOrDefaultAsync(d => d.Id == agentDefinitionId, cancellationToken)
			?? throw new KeyNotFoundException($"Agent definition '{agentDefinitionId}' was not found.");

		var assignment = new AgentColumnAssignment
		{
			BoardId = boardId,
			ColumnScope = columnScope,
			AgentDefinitionId = agentDefinitionId,
			AgentDefinition = definition,
			MatchCriteria = matchCriteria?.Trim() ?? string.Empty
		};

		dbContext.AgentColumnAssignments.Add(assignment);
		await dbContext.SaveChangesAsync(cancellationToken);

		return assignment;
	}

	public async Task<AgentColumnAssignment> UpdateAssignmentAsync(
		Guid assignmentId,
		Guid agentDefinitionId,
		string? matchCriteria = null,
		CancellationToken cancellationToken = default)
	{
		var assignment = await dbContext.AgentColumnAssignments
			                 .Include(a => a.AgentDefinition)
			                 .SingleOrDefaultAsync(a => a.Id == assignmentId, cancellationToken)
		                 ?? throw new KeyNotFoundException($"Agent column assignment '{assignmentId}' was not found.");

		var definition =
			await dbContext.AgentDefinitions.SingleOrDefaultAsync(d => d.Id == agentDefinitionId, cancellationToken)
			?? throw new KeyNotFoundException($"Agent definition '{agentDefinitionId}' was not found.");

		assignment.AgentDefinitionId = agentDefinitionId;
		assignment.AgentDefinition = definition;
		assignment.MatchCriteria = matchCriteria?.Trim() ?? string.Empty;

		await dbContext.SaveChangesAsync(cancellationToken);
		return assignment;
	}

	public async Task DeleteAssignmentAsync(Guid assignmentId, CancellationToken cancellationToken = default)
	{
		var assignment =
			await dbContext.AgentColumnAssignments.SingleOrDefaultAsync(a => a.Id == assignmentId, cancellationToken)
			?? throw new KeyNotFoundException($"Agent column assignment '{assignmentId}' was not found.");

		dbContext.AgentColumnAssignments.Remove(assignment);
		await dbContext.SaveChangesAsync(cancellationToken);
	}

	public async Task<AgentDefinition?> ResolveAgentAsync(Step step, ColumnScope columnScope,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(step);
		if (columnScope == ColumnScope.FeatureAgentReview)
			throw new ArgumentException("FeatureAgentReview scope is not applicable to a Step card.",
				nameof(columnScope));
		ValidateScope(columnScope);

		var boardId = step.Feature?.BoardId ?? await dbContext.Features
			.Where(feature => feature.Id == step.FeatureId)
			.Select(feature => feature.BoardId)
			.SingleOrDefaultAsync(cancellationToken);

		if (boardId == Guid.Empty) throw new KeyNotFoundException($"Board for step '{step.Id}' was not found.");

		var assignments = await dbContext.AgentColumnAssignments
			.AsNoTracking()
			.Include(assignment => assignment.AgentDefinition)
			.Where(assignment => assignment.BoardId == boardId && assignment.ColumnScope == columnScope)
			.ToListAsync(cancellationToken);

		return MatchAssignment(assignments, step);
	}

	public async Task<AgentDefinition?> ResolveAgentAsync(Feature feature, ColumnScope columnScope,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(feature);
		if (columnScope != ColumnScope.FeatureAgentReview)
			throw new ArgumentException("Only FeatureAgentReview scope is applicable to a Feature card.",
				nameof(columnScope));
		ValidateScope(columnScope);

		var assignments = await dbContext.AgentColumnAssignments
			.AsNoTracking()
			.Include(assignment => assignment.AgentDefinition)
			.Where(assignment => assignment.BoardId == feature.BoardId && assignment.ColumnScope == columnScope)
			.ToListAsync(cancellationToken);

		return MatchAssignment(assignments, feature);
	}

	public async Task<AgentDefinition?> ResolveAgentAsync(CardType cardType, Guid cardId, ColumnScope columnScope,
		CancellationToken cancellationToken = default)
	{
		return cardType switch
		{
			CardType.Step => await ResolveAgentAsync(
				await dbContext.Steps.Include(s => s.Feature)
					.SingleOrDefaultAsync(s => s.Id == cardId, cancellationToken)
				?? throw new KeyNotFoundException($"Step '{cardId}' was not found."),
				columnScope,
				cancellationToken),
			CardType.Feature => await ResolveAgentAsync(
				await dbContext.Features.SingleOrDefaultAsync(f => f.Id == cardId, cancellationToken)
				?? throw new KeyNotFoundException($"Feature '{cardId}' was not found."),
				columnScope,
				cancellationToken),
			_ => throw new ArgumentOutOfRangeException(nameof(cardType), $"Unsupported card type '{cardType}'.")
		};
	}

	public AgentDefinition? MatchAssignment(IEnumerable<AgentColumnAssignment> assignments, Step step)
	{
		ArgumentNullException.ThrowIfNull(assignments);
		ArgumentNullException.ThrowIfNull(step);

		var matched = new List<(AgentColumnAssignment Assignment, int Specificity)>();

		foreach (var assignment in assignments.Where(a =>
			         a.ColumnScope == ColumnScope.StepBuild || a.ColumnScope == ColumnScope.StepAgentReview))
			if (EvaluateCriteria(assignment.MatchCriteria, step, out var specificity))
				matched.Add((assignment, specificity));

		return matched
			.OrderByDescending(candidate => candidate.Specificity)
			.ThenBy(candidate => candidate.Assignment.Id)
			.Select(candidate => candidate.Assignment.AgentDefinition)
			.FirstOrDefault();
	}

	public AgentDefinition? MatchAssignment(IEnumerable<AgentColumnAssignment> assignments, Feature feature)
	{
		ArgumentNullException.ThrowIfNull(assignments);
		ArgumentNullException.ThrowIfNull(feature);

		var matched = new List<(AgentColumnAssignment Assignment, int Specificity)>();

		foreach (var assignment in assignments.Where(a => a.ColumnScope == ColumnScope.FeatureAgentReview))
			if (EvaluateCriteria(assignment.MatchCriteria, feature, out var specificity))
				matched.Add((assignment, specificity));

		return matched
			.OrderByDescending(candidate => candidate.Specificity)
			.ThenBy(candidate => candidate.Assignment.Id)
			.Select(candidate => candidate.Assignment.AgentDefinition)
			.FirstOrDefault();
	}

	public static bool EvaluateCriteria(string? criteria, Step step, out int specificity)
	{
		ArgumentNullException.ThrowIfNull(step);
		var isRework = step.AgentReviewFailCount > 0 || step.HumanReviewFailCount > 0;
		return EvaluateCriteriaInternal(
			criteria,
			isRework,
			step.AgentReviewFailCount,
			step.HumanReviewFailCount,
			step.AlwaysRequireHumanReview,
			step.Title,
			step.Description,
			step.GuidanceNotes,
			out specificity);
	}

	public static bool EvaluateCriteria(string? criteria, Feature feature, out int specificity)
	{
		ArgumentNullException.ThrowIfNull(feature);
		var isRework = feature.AgentReviewFailCount > 0 || feature.HumanReviewFailCount > 0;
		return EvaluateCriteriaInternal(
			criteria,
			isRework,
			feature.AgentReviewFailCount,
			feature.HumanReviewFailCount,
			feature.AlwaysRequireHumanReview,
			feature.Title,
			feature.Requirements,
			$"{feature.AcceptanceCriteria} {feature.SuggestedSolution}",
			out specificity);
	}

	private static bool EvaluateCriteriaInternal(
		string? criteria,
		bool isRework,
		int agentReviewFailCount,
		int humanReviewFailCount,
		bool alwaysRequireHumanReview,
		string title,
		string description,
		string extraNotes,
		out int specificity)
	{
		specificity = 0;

		if (string.IsNullOrWhiteSpace(criteria) || criteria.Trim() == "*" ||
		    criteria.Trim().Equals("default", StringComparison.OrdinalIgnoreCase))
		{
			specificity = 0;
			return true;
		}

		var trimmed = criteria.Trim();

		if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
			try
			{
				using var document = JsonDocument.Parse(trimmed);
				return EvaluateJsonObject(
					document.RootElement,
					isRework,
					agentReviewFailCount,
					humanReviewFailCount,
					alwaysRequireHumanReview,
					title,
					description,
					extraNotes,
					out specificity);
			}
			catch (JsonException)
			{
				// Fall through to text-based evaluation
			}

		return EvaluateDelimitedCriteria(
			trimmed,
			isRework,
			agentReviewFailCount,
			humanReviewFailCount,
			alwaysRequireHumanReview,
			title,
			description,
			extraNotes,
			out specificity);
	}

	private static bool EvaluateJsonObject(
		JsonElement root,
		bool isRework,
		int agentReviewFailCount,
		int humanReviewFailCount,
		bool alwaysRequireHumanReview,
		string title,
		string description,
		string extraNotes,
		out int specificity)
	{
		specificity = 0;
		if (root.ValueKind != JsonValueKind.Object) return false;

		foreach (var property in root.EnumerateObject())
		{
			var name = property.Name.Trim().ToLowerInvariant();
			switch (name)
			{
				case "rework" or "is_rework":
					var targetRework = property.Value.ValueKind switch
					{
						JsonValueKind.True => true,
						JsonValueKind.False => false,
						JsonValueKind.String => ParseBoolean(property.Value.GetString()),
						_ => null
					};
					if (targetRework is null || targetRework.Value != isRework) return false;
					specificity += 10;
					break;

				case "new" or "new_build":
					var isNew = property.Value.ValueKind switch
					{
						JsonValueKind.True => true,
						JsonValueKind.False => false,
						JsonValueKind.String => ParseBoolean(property.Value.GetString()),
						_ => null
					};
					if (isNew is null || isNew.Value == isRework) return false;
					specificity += 10;
					break;

				case "title":
					var titlePattern = property.Value.GetString();
					if (string.IsNullOrWhiteSpace(titlePattern) ||
					    !title.Contains(titlePattern.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
					specificity += 5;
					break;

				case "keyword" or "tag" or "contains":
					var keyword = property.Value.GetString();
					if (string.IsNullOrWhiteSpace(keyword) ||
					    (!title.Contains(keyword.Trim(), StringComparison.OrdinalIgnoreCase) &&
					     !description.Contains(keyword.Trim(), StringComparison.OrdinalIgnoreCase) &&
					     !extraNotes.Contains(keyword.Trim(), StringComparison.OrdinalIgnoreCase)))
						return false;
					specificity += 3;
					break;

				case "always_human_review":
					var expectedReview = property.Value.ValueKind switch
					{
						JsonValueKind.True => true,
						JsonValueKind.False => false,
						JsonValueKind.String => ParseBoolean(property.Value.GetString()),
						_ => null
					};
					if (expectedReview is null || expectedReview.Value != alwaysRequireHumanReview) return false;
					specificity += 5;
					break;

				case "agent_fails" or "agent_review_fails":
					if (!property.Value.TryGetInt32(out var expectedAgentFails) ||
					    expectedAgentFails != agentReviewFailCount) return false;
					specificity += 5;
					break;

				case "human_fails" or "human_review_fails":
					if (!property.Value.TryGetInt32(out var expectedHumanFails) ||
					    expectedHumanFails != humanReviewFailCount) return false;
					specificity += 5;
					break;

				default:
					var textVal = property.Value.ToString();
					if (!string.IsNullOrWhiteSpace(textVal) && (
						    title.Contains(textVal, StringComparison.OrdinalIgnoreCase) ||
						    description.Contains(textVal, StringComparison.OrdinalIgnoreCase) ||
						    extraNotes.Contains(textVal, StringComparison.OrdinalIgnoreCase)))
						specificity += 2;
					else
						return false;
					break;
			}
		}

		return true;
	}

	private static bool EvaluateDelimitedCriteria(
		string criteria,
		bool isRework,
		int agentReviewFailCount,
		int humanReviewFailCount,
		bool alwaysRequireHumanReview,
		string title,
		string description,
		string extraNotes,
		out int specificity)
	{
		specificity = 0;
		var clauses = criteria.Split([',', ';', '\n', '&'],
			StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

		if (clauses.Length == 0) return true;

		foreach (var clause in clauses)
		{
			var separatorIndex = clause.IndexOfAny(['=', ':']);
			if (separatorIndex >= 0)
			{
				var key = clause[..separatorIndex].Trim().ToLowerInvariant();
				var value = clause[(separatorIndex + 1)..].Trim().Trim('"', '\'');

				switch (key)
				{
					case "rework" or "is_rework":
						var boolRework = ParseBoolean(value);
						if (boolRework is null || boolRework.Value != isRework) return false;
						specificity += 10;
						break;

					case "new" or "new_build":
						var boolNew = ParseBoolean(value);
						if (boolNew is null || boolNew.Value == isRework) return false;
						specificity += 10;
						break;

					case "title":
						if (string.IsNullOrWhiteSpace(value) ||
						    !title.Contains(value, StringComparison.OrdinalIgnoreCase)) return false;
						specificity += 5;
						break;

					case "keyword" or "tag" or "contains":
						if (string.IsNullOrWhiteSpace(value) ||
						    (!title.Contains(value, StringComparison.OrdinalIgnoreCase) &&
						     !description.Contains(value, StringComparison.OrdinalIgnoreCase) &&
						     !extraNotes.Contains(value, StringComparison.OrdinalIgnoreCase)))
							return false;
						specificity += 3;
						break;

					case "always_human_review":
						var boolRev = ParseBoolean(value);
						if (boolRev is null || boolRev.Value != alwaysRequireHumanReview) return false;
						specificity += 5;
						break;

					case "agent_fails" or "agent_review_fails":
						if (!int.TryParse(value, out var expectedAgentFails) ||
						    expectedAgentFails != agentReviewFailCount) return false;
						specificity += 5;
						break;

					case "human_fails" or "human_review_fails":
						if (!int.TryParse(value, out var expectedHumanFails) ||
						    expectedHumanFails != humanReviewFailCount) return false;
						specificity += 5;
						break;

					default:
						if (!string.IsNullOrWhiteSpace(value) && (
							    title.Contains(value, StringComparison.OrdinalIgnoreCase) ||
							    description.Contains(value, StringComparison.OrdinalIgnoreCase) ||
							    extraNotes.Contains(value, StringComparison.OrdinalIgnoreCase)))
							specificity += 2;
						else
							return false;
						break;
				}
			}
			else
			{
				var token = clause.ToLowerInvariant();
				if (token is "rework" or "is_rework")
				{
					if (!isRework) return false;
					specificity += 10;
				}
				else if (token is "new" or "new_build")
				{
					if (isRework) return false;
					specificity += 10;
				}
				else
				{
					if (title.Contains(clause, StringComparison.OrdinalIgnoreCase) ||
					    description.Contains(clause, StringComparison.OrdinalIgnoreCase) ||
					    extraNotes.Contains(clause, StringComparison.OrdinalIgnoreCase))
						specificity += 2;
					else
						return false;
				}
			}
		}

		return true;
	}

	private static bool? ParseBoolean(string? value)
	{
		if (string.IsNullOrWhiteSpace(value)) return null;
		var clean = value.Trim().ToLowerInvariant();
		return clean switch
		{
			"true" or "1" or "yes" or "y" => true,
			"false" or "0" or "no" or "n" => false,
			_ => null
		};
	}

	private static void ValidateScope(ColumnScope columnScope)
	{
		if (!Enum.IsDefined(columnScope))
			throw new ArgumentOutOfRangeException(nameof(columnScope), $"Invalid column scope '{columnScope}'.");
	}
}