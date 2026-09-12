using AgentTaskHarness.Domain.Entities;
using AgentTaskHarness.Domain.Enums;

namespace AgentTaskHarness.Application.Agents;

public interface IAgentScheduler
{
	/// <summary>
	/// Requests execution for a dev plan step in Build or AgentReview column.
	/// If a previous agent is still running, flags as blocked.
	/// If review fail thresholds are exceeded, leaves queued without auto-starting.
	/// Otherwise queues or starts the step according to board concurrency and prioritization.
	/// </summary>
	Task RequestStartAsync(Guid stepId, CancellationToken cancellationToken = default);

	/// <summary>
	/// Manually retries or restarts an agent for a dev plan step in Build or AgentReview.
	/// Clears previous failed or stopped runs and re-evaluates the board queue.
	/// </summary>
	Task RetryAgentAsync(Guid stepId, CancellationToken cancellationToken = default);

	/// <summary>
	/// Notifies the scheduler that an agent on a step has completed, recording token and time usage,
	/// freeing up a concurrency slot and re-evaluating the queue.
	/// </summary>
	Task OnAgentFinishedAsync(
		Guid stepId,
		long tokensUsed = 0,
		TimeSpan? timeSpent = null,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Manually stops any active running agent for the specified step, recording usage and re-checking the queue.
	/// </summary>
	Task StopAgentAsync(
		Guid stepId,
		long tokensUsed = 0,
		TimeSpan? timeSpent = null,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Manually stops any active running agent for the specified card (Step or Feature).
	/// </summary>
	Task StopAgentAsync(
		CardType cardType,
		Guid cardId,
		long tokensUsed = 0,
		TimeSpan? timeSpent = null,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Directly updates the token and time usage on the latest or active agent run for a step, and updates the step totals.
	/// </summary>
	Task RecordRunUsageAsync(
		Guid stepId,
		long tokensUsed,
		TimeSpan? timeSpent = null,
		CancellationToken cancellationToken = default);

	/// <summary>
	/// Processes the prioritized queue for a board, starting eligible queued steps up to the concurrency limit.
	/// </summary>
	Task ProcessQueueAsync(Guid boardId, CancellationToken cancellationToken = default);

	/// <summary>
	/// Checks whether an agent is actively running (Working or WaitingForInput) for the step.
	/// </summary>
	Task<bool> IsAgentRunningAsync(Guid stepId, CancellationToken cancellationToken = default);

	/// <summary>
	/// Checks whether an agent is actively running (Working or WaitingForInput) for the specified card.
	/// </summary>
	Task<bool> IsAgentRunningAsync(CardType cardType, Guid cardId, CancellationToken cancellationToken = default);

	/// <summary>
	/// Returns the status of the current or most recent agent run for the step.
	/// </summary>
	Task<AgentRunStatus?> GetStepAgentStatusAsync(Guid stepId, CancellationToken cancellationToken = default);

	/// <summary>
	/// Returns the current or most recent agent run for the step.
	/// </summary>
	Task<AgentRun?> GetCurrentRunAsync(Guid stepId, CancellationToken cancellationToken = default);

	/// <summary>
	/// Returns the current or most recent agent run for the specified card.
	/// </summary>
	Task<AgentRun?> GetCurrentRunAsync(CardType cardType, Guid cardId, CancellationToken cancellationToken = default);
}
