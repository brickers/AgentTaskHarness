namespace AgentTaskHarness.Domain.Enums;

public enum AgentRunStatus
{
	Queued,
	Working,
	WaitingForInput,
	Completed,
	Failed,
	Blocked,
	Stopped
}