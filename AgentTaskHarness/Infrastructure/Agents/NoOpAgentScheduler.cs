using AgentTaskHarness.Application.Agents;
using AgentTaskHarness.Domain.Entities;
using AgentTaskHarness.Domain.Enums;

namespace AgentTaskHarness.Infrastructure.Agents;

public class NoOpAgentScheduler : IAgentScheduler
{
	public Task RequestStartAsync(Guid stepId, CancellationToken cancellationToken = default)
	{
		return Task.CompletedTask;
	}

	public Task RetryAgentAsync(Guid stepId, CancellationToken cancellationToken = default)
	{
		return Task.CompletedTask;
	}

	public Task OnAgentFinishedAsync(
		Guid stepId,
		long tokensUsed = 0,
		TimeSpan? timeSpent = null,
		CancellationToken cancellationToken = default)
	{
		return Task.CompletedTask;
	}

	public Task StopAgentAsync(
		Guid stepId,
		long tokensUsed = 0,
		TimeSpan? timeSpent = null,
		CancellationToken cancellationToken = default)
	{
		return Task.CompletedTask;
	}

	public Task StopAgentAsync(
		CardType cardType,
		Guid cardId,
		long tokensUsed = 0,
		TimeSpan? timeSpent = null,
		CancellationToken cancellationToken = default)
	{
		return Task.CompletedTask;
	}

	public Task RecordRunUsageAsync(
		Guid stepId,
		long tokensUsed,
		TimeSpan? timeSpent = null,
		CancellationToken cancellationToken = default)
	{
		return Task.CompletedTask;
	}

	public Task ProcessQueueAsync(Guid boardId, CancellationToken cancellationToken = default)
	{
		return Task.CompletedTask;
	}

	public Task<bool> IsAgentRunningAsync(Guid stepId, CancellationToken cancellationToken = default)
	{
		return Task.FromResult(false);
	}

	public Task<bool> IsAgentRunningAsync(CardType cardType, Guid cardId, CancellationToken cancellationToken = default)
	{
		return Task.FromResult(false);
	}

	public Task<AgentRunStatus?> GetStepAgentStatusAsync(Guid stepId, CancellationToken cancellationToken = default)
	{
		return Task.FromResult<AgentRunStatus?>(null);
	}

	public Task<AgentRun?> GetCurrentRunAsync(Guid stepId, CancellationToken cancellationToken = default)
	{
		return Task.FromResult<AgentRun?>(null);
	}

	public Task<AgentRun?> GetCurrentRunAsync(CardType cardType, Guid cardId,
		CancellationToken cancellationToken = default)
	{
		return Task.FromResult<AgentRun?>(null);
	}
}