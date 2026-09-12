namespace AgentTaskHarness.Domain.Entities;

public class StepDependency
{
	public Guid StepId { get; set; }
	public Guid DependsOnStepId { get; set; }

	public Step Step { get; set; } = null!;
	public Step DependsOnStep { get; set; } = null!;
}
