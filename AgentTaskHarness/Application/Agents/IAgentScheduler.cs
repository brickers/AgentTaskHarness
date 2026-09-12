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
	/// Notifies the scheduler that an agent on a step has completed, freeing up a concurrency slot and re-evaluating the queue.
	/// </summary>
	Task OnAgentFinishedAsync(Guid stepId, CancellationToken cancellationToken = default);

	/// <summary>
	/// Manually stops any active running agent for the specified step and re-checks the queue.
	/// </summary>
	Task StopAgentAsync(Guid stepId, CancellationToken cancellationToken = default);

	/// <summary>
	/// Processes the prioritized queue for a board, starting eligible queued steps up to the concurrency limit.
	/// </summary>
	Task ProcessQueueAsync(Guid boardId, CancellationToken cancellationToken = default);

	/// <summary>
	/// Checks whether an agent is actively running (Working or WaitingForInput) for the step.
	/// </summary>
	Task<bool> IsAgentRunningAsync(Guid stepId, CancellationToken cancellationToken = default);

	/// <summary>
	/// Returns the status of the current or most recent agent run for the step.
	/// </summary>
	Task<AgentRunStatus?> GetStepAgentStatusAsync(Guid stepId, CancellationToken cancellationToken = default);

	/// <summary>
	/// Returns the current or most recent agent run for the step.
	/// </summary>
	Task<AgentRun?> GetCurrentRunAsync(Guid stepId, CancellationToken cancellationToken = default);
}
