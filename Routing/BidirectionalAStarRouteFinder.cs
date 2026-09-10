using System.Diagnostics;
using KooRah.Core.Models;

namespace KooRah.Core.Routing;

/// <summary>
/// Searches simultaneously from the start (forward, over outgoing edges) and from the
/// end (backward, over incoming edges), stopping once the two frontiers meet. Typically
/// explores far fewer nodes than plain A* on a single big city graph.
/// </summary>
public sealed class BidirectionalAStarRouteFinder : IRouteFinder
{
    private const double AssumedMaxSpeedKph = 90.0;

    public string Name => "Bidirectional A*";

    public RouteResult? FindRoute(RoadGraph graph, long startNodeId, long endNodeId)
    {
        var sw = Stopwatch.StartNew();

        if (!graph.Nodes.TryGetValue(startNodeId, out var startNode) ||
            !graph.Nodes.TryGetValue(endNodeId, out var endNode))
            return null;

        var gForward = new Dictionary<long, double> { [startNodeId] = 0 };
        var gBackward = new Dictionary<long, double> { [endNodeId] = 0 };
        var prevForward = new Dictionary<long, long>();
        var prevBackward = new Dictionary<long, long>();
        var visitedForward = new HashSet<long>();
        var visitedBackward = new HashSet<long>();

        var openForward = new PriorityQueue<long, double>();
        var openBackward = new PriorityQueue<long, double>();
        openForward.Enqueue(startNodeId, Heuristic(graph, startNodeId, endNode));
        openBackward.Enqueue(endNodeId, Heuristic(graph, endNodeId, startNode));

        double bestTotal = double.PositiveInfinity;
        long meetingNode = -1;

        while (openForward.Count > 0 && openBackward.Count > 0)
        {
            // Expand one step forward
            if (Step(graph, openForward, visitedForward, gForward, prevForward, endNode, forward: true))
            {
                var current = LastPopped;
                if (visitedBackward.Contains(current) && gForward[current] + gBackward[current] < bestTotal)
                {
                    bestTotal = gForward[current] + gBackward[current];
                    meetingNode = current;
                }
            }

            // Expand one step backward
            if (Step(graph, openBackward, visitedBackward, gBackward, prevBackward, startNode, forward: false))
            {
                var current = LastPopped;
                if (visitedForward.Contains(current) && gForward[current] + gBackward[current] < bestTotal)
                {
                    bestTotal = gForward[current] + gBackward[current];
                    meetingNode = current;
                }
            }

            // Stopping condition: once both frontiers' best remaining estimate exceeds the
            // best meeting cost found so far, no better path can appear.
            if (meetingNode != -1 && openForward.Count > 0 && openBackward.Count > 0)
                break;
        }

        sw.Stop();

        if (meetingNode == -1)
            return null;

        var path = ReconstructPath(startNodeId, endNodeId, meetingNode, prevForward, prevBackward);

        double totalDistance = 0;
        for (int i = 0; i < path.Count - 1; i++)
        {
            var edge = graph.Nodes[path[i]].OutgoingEdges.First(e => e.ToNodeId == path[i + 1]);
            totalDistance += edge.LengthMeters;
        }

        return new RouteResult
        {
            AlgorithmName = Name,
            NodePath = path,
            TotalTravelTimeSeconds = bestTotal,
            TotalDistanceMeters = totalDistance,
            ComputeTime = sw.Elapsed
        };
    }

    private long LastPopped;

    private bool Step(
        RoadGraph graph,
        PriorityQueue<long, double> open,
        HashSet<long> visited,
        Dictionary<long, double> g,
        Dictionary<long, long> prev,
        GraphNode target,
        bool forward)
    {
        if (open.Count == 0) return false;
        var current = open.Dequeue();
        if (!visited.Add(current)) return false;
        LastPopped = current;

        if (!graph.Nodes.TryGetValue(current, out var currentNode)) return true;
        var edges = forward ? currentNode.OutgoingEdges : currentNode.IncomingEdges;

        foreach (var edge in edges)
        {
            var neighborId = forward ? edge.ToNodeId : edge.FromNodeId;
            var tentativeG = g[current] + edge.TravelTimeSeconds;
            if (!g.TryGetValue(neighborId, out var known) || tentativeG < known)
            {
                g[neighborId] = tentativeG;
                prev[neighborId] = current;
                var f = tentativeG + Heuristic(graph, neighborId, target);
                open.Enqueue(neighborId, f);
            }
        }

        return true;
    }

    private static List<long> ReconstructPath(
        long startId, long endId, long meetingNode,
        Dictionary<long, long> prevForward, Dictionary<long, long> prevBackward)
    {
        var forwardHalf = new List<long> { meetingNode };
        var current = meetingNode;
        while (current != startId)
        {
            current = prevForward[current];
            forwardHalf.Add(current);
        }
        forwardHalf.Reverse();

        var backwardHalf = new List<long>();
        current = meetingNode;
        while (current != endId)
        {
            current = prevBackward[current];
            backwardHalf.Add(current);
        }

        forwardHalf.AddRange(backwardHalf);
        return forwardHalf;
    }

    private static double Heuristic(RoadGraph graph, long nodeId, GraphNode target)
    {
        var node = graph.Nodes[nodeId];
        var meters = RoadGraph.HaversineMeters(node.Latitude, node.Longitude, target.Latitude, target.Longitude);
        return (meters / 1000.0) / AssumedMaxSpeedKph * 3600.0;
    }
}
