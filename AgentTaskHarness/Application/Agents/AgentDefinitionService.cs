using AgentTaskHarness.Domain.Entities;
using AgentTaskHarness.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentTaskHarness.Application.Agents;

public class AgentDefinitionService(AppDbContext dbContext)
{
	public async Task<AgentDefinition> CreateAsync(Guid boardId, string name, string folderPath, CancellationToken cancellationToken = default)
	{
		Validate(name, folderPath);
		if (!await dbContext.Boards.AnyAsync(board => board.Id == boardId, cancellationToken))
		{
			throw new KeyNotFoundException($"Board '{boardId}' was not found.");
		}
		var definition = new AgentDefinition { BoardId = boardId, Name = name.Trim(), FolderPath = folderPath.Trim() };
		dbContext.AgentDefinitions.Add(definition);
		await dbContext.SaveChangesAsync(cancellationToken);
		return definition;
	}

	public Task<List<AgentDefinition>> GetForBoardAsync(Guid boardId, CancellationToken cancellationToken = default) =>
		dbContext.AgentDefinitions.AsNoTracking().Where(definition => definition.BoardId == boardId).OrderBy(definition => definition.Name).ToListAsync(cancellationToken);

	public async Task<AgentDefinition> UpdateAsync(Guid definitionId, string name, string folderPath, CancellationToken cancellationToken = default)
	{
		Validate(name, folderPath);
		var definition = await FindDefinitionAsync(definitionId, cancellationToken);
		definition.Name = name.Trim();
		definition.FolderPath = folderPath.Trim();
		await dbContext.SaveChangesAsync(cancellationToken);
		return definition;
	}

	public async Task DeleteAsync(Guid definitionId, CancellationToken cancellationToken = default)
	{
		var definition = await FindDefinitionAsync(definitionId, cancellationToken);
		dbContext.AgentDefinitions.Remove(definition);
		await dbContext.SaveChangesAsync(cancellationToken);
	}

	private async Task<AgentDefinition> FindDefinitionAsync(Guid definitionId, CancellationToken cancellationToken) =>
		await dbContext.AgentDefinitions.SingleOrDefaultAsync(definition => definition.Id == definitionId, cancellationToken)
		?? throw new KeyNotFoundException($"Agent definition '{definitionId}' was not found.");

	private static void Validate(string name, string folderPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
	}
}