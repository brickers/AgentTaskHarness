using AgentTaskHarness.Domain.Enums;

namespace AgentTaskHarness.Domain.Entities;

public class AgentRun
{
	public Guid Id { get; set; } = Guid.NewGuid();
	public CardType CardType { get; set; }
	public Guid CardId { get; set; }
	public int? ProcessId { get; set; }
	public AgentRunStatus Status { get; set; } = AgentRunStatus.Queued;
	public string? SessionLink { get; set; }
	public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
	public DateTimeOffset? EndedAt { get; set; }
	public long TokensUsed { get; set; }
	public TimeSpan TimeSpent { get; set; } = TimeSpan.Zero;
}