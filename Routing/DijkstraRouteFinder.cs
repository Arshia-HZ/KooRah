using System.Diagnostics;
using KooRah.Core.Models;

namespace KooRah.Core.Routing;

public sealed class DijkstraRouteFinder : IRouteFinder
{
    public string Name => "Dijkstra";

    public RouteResult? FindRoute(RoadGraph graph, long startNodeId, long endNodeId)
    {
        var sw = Stopwatch.StartNew();

        var dist = new Dictionary<long, double>();
        var prev = new Dictionary<long, long>();
        var visited = new HashSet<long>();
        var queue = new PriorityQueue<long, double>();

        dist[startNodeId] = 0;
        queue.Enqueue(startNodeId, 0);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!visited.Add(current)) continue;
            if (current == endNodeId) break;

            if (!graph.Nodes.TryGetValue(current, out var currentNode)) continue;

            foreach (var edge in currentNode.OutgoingEdges)
            {
                var candidateDist = dist[current] + edge.TravelTimeSeconds;
                if (!dist.TryGetValue(edge.ToNodeId, out var known) || candidateDist < known)
                {
                    dist[edge.ToNodeId] = candidateDist;
                    prev[edge.ToNodeId] = current;
                    queue.Enqueue(edge.ToNodeId, candidateDist);
                }
            }
        }

        sw.Stop();
        return RouteResultBuilder.Build(Name, graph, startNodeId, endNodeId, dist, prev, sw.Elapsed);
    }
}
