using KooRah.Core.Models;

namespace KooRah.Core.Routing;

internal static class RouteResultBuilder
{
    public static RouteResult? Build(
        string algorithmName,
        RoadGraph graph,
        long startNodeId,
        long endNodeId,
        Dictionary<long, double> dist,
        Dictionary<long, long> prev,
        TimeSpan computeTime)
    {
        if (!dist.ContainsKey(endNodeId))
            return null; // unreachable

        var path = new List<long> { endNodeId };
        var current = endNodeId;
        while (current != startNodeId)
        {
            if (!prev.TryGetValue(current, out var p))
                return null; // broken chain, shouldn't happen if dist has the end node
            path.Add(p);
            current = p;
        }
        path.Reverse();

        double totalDistance = 0;
        for (int i = 0; i < path.Count - 1; i++)
        {
            var edge = graph.Nodes[path[i]].OutgoingEdges.First(e => e.ToNodeId == path[i + 1]);
            totalDistance += edge.LengthMeters;
        }

        return new RouteResult
        {
            AlgorithmName = algorithmName,
            NodePath = path,
            TotalTravelTimeSeconds = dist[endNodeId],
            TotalDistanceMeters = totalDistance,
            ComputeTime = computeTime
        };
    }
}
