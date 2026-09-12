using AgentTaskHarness.Domain.Enums;

namespace AgentTaskHarness.Domain.Entities;

public class Comment
{
	public Guid Id { get; set; } = Guid.NewGuid();
	public CardType CardType { get; set; }
	public Guid CardId { get; set; }
	public string Author { get; set; } = string.Empty;
	public string Body { get; set; } = string.Empty;
	public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}