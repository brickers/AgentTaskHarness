using AgentTaskHarness.Domain.Entities;
using AgentTaskHarness.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentTaskHarness.Application.Agents;

public class AgentDefinitionService(AppDbContext dbContext, IAgentDefinitionFolderWriter folderWriter)
{
	public async Task<AgentDefinition> CreateAsync(Guid boardId, string name, string folderPath,
		CancellationToken cancellationToken = default)
	{
		Validate(name, folderPath);
		if (!await dbContext.Boards.AnyAsync(board => board.Id == boardId, cancellationToken))
			throw new KeyNotFoundException($"Board '{boardId}' was not found.");
		var definition = new AgentDefinition { BoardId = boardId, Name = name.Trim(), FolderPath = folderPath.Trim() };
		dbContext.AgentDefinitions.Add(definition);
		await dbContext.SaveChangesAsync(cancellationToken);
		return definition;
	}

	public Task<List<AgentDefinition>> GetForBoardAsync(Guid boardId, CancellationToken cancellationToken = default)
	{
		return dbContext.AgentDefinitions.AsNoTracking().Where(definition => definition.BoardId == boardId)
			.OrderBy(definition => definition.Name).ToListAsync(cancellationToken);
	}

	public Task<AgentDefinition?> GetByIdAsync(Guid definitionId, CancellationToken cancellationToken = default)
	{
		return dbContext.AgentDefinitions.AsNoTracking()
			.Include(definition => definition.Components.OrderBy(component => component.Order))
			.ThenInclude(component => component.Component)
			.SingleOrDefaultAsync(definition => definition.Id == definitionId, cancellationToken);
	}

	public async Task<AgentDefinition> UpdateAsync(Guid definitionId, string name, string folderPath,
		CancellationToken cancellationToken = default)
	{
		Validate(name, folderPath);
		var definition = await FindDefinitionAsync(definitionId, cancellationToken);
		definition.Name = name.Trim();
		definition.FolderPath = folderPath.Trim();
		await dbContext.SaveChangesAsync(cancellationToken);
		return definition;
	}

	public async Task<AgentDefinition> SaveAsync(Guid definitionId, string name, string folderPath,
		IReadOnlyList<Guid> componentIds, CancellationToken cancellationToken = default)
	{
		Validate(name, folderPath);
		ArgumentNullException.ThrowIfNull(componentIds);
		var definition = await FindDefinitionWithComponentsAsync(definitionId, cancellationToken);
		await ReplaceComponentsAsync(definition, componentIds, cancellationToken);
		definition.Name = name.Trim();
		definition.FolderPath = folderPath.Trim();
		await dbContext.SaveChangesAsync(cancellationToken);
		await folderWriter.WriteAsync(definition, cancellationToken);
		return definition;
	}

	public Task<List<AgentComponent>> GetComponentsAsync(CancellationToken cancellationToken = default)
	{
		return dbContext.AgentComponents.AsNoTracking().OrderBy(component => component.Name)
			.ToListAsync(cancellationToken);
	}

	public async Task<AgentComponent> CreateComponentAsync(string name, string configContent,
		CancellationToken cancellationToken = default)
	{
		ValidateComponent(name, configContent);
		var component = new AgentComponent { Name = name.Trim(), ConfigContent = configContent };
		dbContext.AgentComponents.Add(component);
		await dbContext.SaveChangesAsync(cancellationToken);
		return component;
	}

	public async Task<AgentComponent> UpdateComponentAsync(Guid componentId, string name, string configContent,
		CancellationToken cancellationToken = default)
	{
		ValidateComponent(name, configContent);
		var component = await FindComponentAsync(componentId, cancellationToken);
		component.Name = name.Trim();
		component.ConfigContent = configContent;
		await dbContext.SaveChangesAsync(cancellationToken);
		return component;
	}

	public async Task DeleteComponentAsync(Guid componentId, CancellationToken cancellationToken = default)
	{
		var component = await FindComponentAsync(componentId, cancellationToken);
		if (await dbContext.AgentDefinitionComponents.AnyAsync(reference => reference.ComponentId == componentId,
			    cancellationToken))
			throw new InvalidOperationException("A component used by an agent definition cannot be deleted.");

		dbContext.AgentComponents.Remove(component);
		await dbContext.SaveChangesAsync(cancellationToken);
	}

	public async Task AddComponentAsync(Guid definitionId, Guid componentId,
		CancellationToken cancellationToken = default)
	{
		var definition = await FindDefinitionWithComponentsAsync(definitionId, cancellationToken);
		var component = await FindComponentAsync(componentId, cancellationToken);
		if (definition.Components.Any(reference => reference.ComponentId == componentId)) return;

		definition.Components.Add(new AgentDefinitionComponent
		{
			AgentDefinitionId = definition.Id,
			ComponentId = component.Id,
			Order = definition.Components.Count,
			Component = component
		});
		await dbContext.SaveChangesAsync(cancellationToken);
	}

	public async Task RemoveComponentAsync(Guid definitionId, Guid componentId,
		CancellationToken cancellationToken = default)
	{
		var definition = await FindDefinitionWithComponentsAsync(definitionId, cancellationToken);
		var reference = definition.Components.SingleOrDefault(reference => reference.ComponentId == componentId)
		                ?? throw new KeyNotFoundException(
			                $"Component '{componentId}' is not part of agent definition '{definitionId}'.");
		dbContext.AgentDefinitionComponents.Remove(reference);
		definition.Components.Remove(reference);
		await NormalizeComponentOrderAsync(definition.Components.OrderBy(component => component.Order).ToList(),
			cancellationToken);
	}

	public async Task ReorderComponentsAsync(Guid definitionId, IReadOnlyList<Guid> componentIds,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(componentIds);
		var definition = await FindDefinitionWithComponentsAsync(definitionId, cancellationToken);
		if (definition.Components.Count != componentIds.Count ||
		    componentIds.Distinct().Count() != componentIds.Count ||
		    definition.Components.Select(component => component.ComponentId).Except(componentIds).Any())
			throw new InvalidOperationException(
				"The component order must exactly match the agent definition's components.");

		var references = definition.Components.ToDictionary(component => component.ComponentId);
		await NormalizeComponentOrderAsync(componentIds.Select(componentId => references[componentId]).ToList(),
			cancellationToken);
	}

	public async Task DeleteAsync(Guid definitionId, CancellationToken cancellationToken = default)
	{
		var definition = await FindDefinitionAsync(definitionId, cancellationToken);
		dbContext.AgentDefinitions.Remove(definition);
		await dbContext.SaveChangesAsync(cancellationToken);
	}

	private async Task<AgentDefinition> FindDefinitionAsync(Guid definitionId, CancellationToken cancellationToken)
	{
		return await dbContext.AgentDefinitions.SingleOrDefaultAsync(definition => definition.Id == definitionId,
			       cancellationToken)
		       ?? throw new KeyNotFoundException($"Agent definition '{definitionId}' was not found.");
	}

	private async Task<AgentDefinition> FindDefinitionWithComponentsAsync(Guid definitionId,
		CancellationToken cancellationToken)
	{
		return await dbContext.AgentDefinitions
			       .Include(definition => definition.Components)
			       .ThenInclude(component => component.Component)
			       .SingleOrDefaultAsync(definition => definition.Id == definitionId, cancellationToken)
		       ?? throw new KeyNotFoundException($"Agent definition '{definitionId}' was not found.");
	}

	private async Task<AgentComponent> FindComponentAsync(Guid componentId, CancellationToken cancellationToken)
	{
		return await dbContext.AgentComponents.SingleOrDefaultAsync(component => component.Id == componentId,
			       cancellationToken)
		       ?? throw new KeyNotFoundException($"Agent component '{componentId}' was not found.");
	}

	private async Task ReplaceComponentsAsync(AgentDefinition definition, IReadOnlyList<Guid> componentIds,
		CancellationToken cancellationToken)
	{
		if (componentIds.Distinct().Count() != componentIds.Count)
			throw new InvalidOperationException(
				"An agent definition cannot contain the same component more than once.");

		var components = await dbContext.AgentComponents.Where(component => componentIds.Contains(component.Id))
			.ToDictionaryAsync(component => component.Id, cancellationToken);
		if (components.Count != componentIds.Count)
			throw new KeyNotFoundException("One or more agent components were not found.");

		var existingReferences = definition.Components.ToDictionary(component => component.ComponentId);
		foreach (var reference in definition.Components
			         .Where(reference => !componentIds.Contains(reference.ComponentId)).ToList())
		{
			dbContext.AgentDefinitionComponents.Remove(reference);
			definition.Components.Remove(reference);
		}

		foreach (var componentId in componentIds.Where(componentId => !existingReferences.ContainsKey(componentId)))
			definition.Components.Add(new AgentDefinitionComponent
			{
				AgentDefinitionId = definition.Id,
				ComponentId = componentId,
				Component = components[componentId]
			});

		var references = definition.Components.ToDictionary(component => component.ComponentId);
		await NormalizeComponentOrderAsync(componentIds.Select(componentId => references[componentId]).ToList(),
			cancellationToken);
	}

	private async Task NormalizeComponentOrderAsync(IReadOnlyList<AgentDefinitionComponent> orderedComponents,
		CancellationToken cancellationToken)
	{
		for (var index = 0; index < orderedComponents.Count; index++) orderedComponents[index].Order = -index - 1;
		await dbContext.SaveChangesAsync(cancellationToken);

		for (var index = 0; index < orderedComponents.Count; index++) orderedComponents[index].Order = index;
		await dbContext.SaveChangesAsync(cancellationToken);
	}

	private static void Validate(string name, string folderPath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
	}

	private static void ValidateComponent(string name, string configContent)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		ArgumentNullException.ThrowIfNull(configContent);
	}
}