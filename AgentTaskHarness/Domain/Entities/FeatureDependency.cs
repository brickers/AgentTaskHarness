namespace AgentTaskHarness.Domain.Entities;

public class FeatureDependency
{
	public Guid FeatureId { get; set; }
	public Guid DependsOnFeatureId { get; set; }

	public Feature Feature { get; set; } = null!;
	public Feature DependsOnFeature { get; set; } = null!;
}
