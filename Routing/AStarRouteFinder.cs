using System.Diagnostics;
using KooRah.Core.Models;

namespace KooRah.Core.Routing;

public sealed class AStarRouteFinder : IRouteFinder
{
    // Assumed realistic top speed anywhere in the city, used to keep the heuristic admissible
    // (it must never overestimate true remaining travel time).
    private const double AssumedMaxSpeedKph = 90.0;

    public string Name => "A*";

    public RouteResult? FindRoute(RoadGraph graph, long startNodeId, long endNodeId)
    {
        var sw = Stopwatch.StartNew();

        if (!graph.Nodes.TryGetValue(endNodeId, out var endNode))
            return null;

        var gScore = new Dictionary<long, double>();
        var prev = new Dictionary<long, long>();
        var visited = new HashSet<long>();
        var openSet = new PriorityQueue<long, double>();

        gScore[startNodeId] = 0;
        openSet.Enqueue(startNodeId, Heuristic(graph, startNodeId, endNode));

        while (openSet.Count > 0)
        {
            var current = openSet.Dequeue();
            if (!visited.Add(current)) continue;
            if (current == endNodeId) break;

            if (!graph.Nodes.TryGetValue(current, out var currentNode)) continue;

            foreach (var edge in currentNode.OutgoingEdges)
            {
                var tentativeG = gScore[current] + edge.TravelTimeSeconds;
                if (!gScore.TryGetValue(edge.ToNodeId, out var known) || tentativeG < known)
                {
                    gScore[edge.ToNodeId] = tentativeG;
                    prev[edge.ToNodeId] = current;
                    var f = tentativeG + Heuristic(graph, edge.ToNodeId, endNode);
                    openSet.Enqueue(edge.ToNodeId, f);
                }
            }
        }

        sw.Stop();
        return RouteResultBuilder.Build(Name, graph, startNodeId, endNodeId, gScore, prev, sw.Elapsed);
    }

    /// <summary>Straight-line time-to-go at the assumed max speed — always &lt;= real remaining time.</summary>
    private static double Heuristic(RoadGraph graph, long nodeId, GraphNode end)
    {
        var node = graph.Nodes[nodeId];
        var meters = RoadGraph.HaversineMeters(node.Latitude, node.Longitude, end.Latitude, end.Longitude);
        return (meters / 1000.0) / AssumedMaxSpeedKph * 3600.0;
    }
}
