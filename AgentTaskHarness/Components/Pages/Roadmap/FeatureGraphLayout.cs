using AgentTaskHarness.Domain.Entities;

namespace AgentTaskHarness.Components.Pages.Roadmap;

public sealed record FeatureGraphNode(Feature Feature, int X, int Y);
public sealed record FeatureGraphEdge(Guid PrerequisiteId, Guid DependentId, string Path);

public sealed record FeatureGraphLayout(
	IReadOnlyList<FeatureGraphNode> Nodes, IReadOnlyList<FeatureGraphEdge> Edges, int Width, int Height)
{
	public const int NodeWidth = 220;
	public const int NodeHeight = 84;

	public static FeatureGraphLayout Create(IEnumerable<Feature> features, IEnumerable<FeatureDependency> dependencies)
	{
		var ordered = features.OrderBy(f => f.CreatedAt).ThenBy(f => f.Id).ToList();
		var byId = ordered.ToDictionary(f => f.Id);
		var links = dependencies
			.Where(d => byId.ContainsKey(d.FeatureId) && byId.ContainsKey(d.DependsOnFeatureId))
			.Select(d => (Prerequisite: d.DependsOnFeatureId, Dependent: d.FeatureId)).Distinct().ToList();
		var incoming = ordered.ToDictionary(f => f.Id, _ => 0);
		var outgoing = ordered.ToDictionary(f => f.Id, _ => new List<Guid>());
		foreach (var link in links)
		{
			incoming[link.Dependent]++;
			outgoing[link.Prerequisite].Add(link.Dependent);
		}

		var ranks = ordered.ToDictionary(f => f.Id, _ => 0);
		var queue = new Queue<Guid>(ordered.Where(f => incoming[f.Id] == 0).Select(f => f.Id));
		while (queue.TryDequeue(out var id))
		{
			foreach (var dependent in outgoing[id])
			{
				ranks[dependent] = Math.Max(ranks[dependent], ranks[id] + 1);
				if (--incoming[dependent] == 0) queue.Enqueue(dependent);
			}
		}

		// Keep even malformed legacy cyclic data visible without recursing indefinitely.
		var rows = new Dictionary<int, int>();
		var nodes = ordered.Select(feature =>
		{
			var rank = ranks[feature.Id];
			var row = rows.GetValueOrDefault(rank);
			rows[rank] = row + 1;
			return new FeatureGraphNode(feature, 24 + rank * 320, 24 + row * 132);
		}).ToList();
		var positions = nodes.ToDictionary(n => n.Feature.Id);
		var edges = links.Select(link =>
		{
			var from = positions[link.Prerequisite];
			var to = positions[link.Dependent];
			var x1 = from.X + NodeWidth;
			var y1 = from.Y + NodeHeight / 2;
			var x2 = to.X - 6;
			var y2 = to.Y + NodeHeight / 2;
			// Long edges run in the gutter above nodes, rather than through intermediate features.
			var path = to.X - from.X > 320
				? $"M {x1} {y1} H {x1 + 24} V 10 H {to.X - 24} V {y2} H {x2}"
				: $"M {x1} {y1} C {x1 + 46} {y1}, {x2 - 46} {y2}, {x2} {y2}";
			return new FeatureGraphEdge(link.Prerequisite, link.Dependent, path);
		}).ToList();
		return new FeatureGraphLayout(nodes, edges,
			nodes.Count == 0 ? 320 : nodes.Max(n => n.X) + NodeWidth + 24,
			nodes.Count == 0 ? 132 : nodes.Max(n => n.Y) + NodeHeight + 24);
	}
}
