using AgentTaskHarness.Domain.Entities;

namespace AgentTaskHarness.Application.Agents;

public interface IAgentDefinitionFolderWriter
{
	Task WriteAsync(AgentDefinition definition, CancellationToken cancellationToken = default);
}