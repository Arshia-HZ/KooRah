namespace KooRah.Core.Models;

public sealed class RoadGraph
{
    private readonly Dictionary<long, GraphNode> _nodes = new();
    private readonly List<GraphEdge> _edges = new();

    public IReadOnlyDictionary<long, GraphNode> Nodes => _nodes;
    public IReadOnlyList<GraphEdge> Edges => _edges;

    public void AddNode(GraphNode node) => _nodes[node.Id] = node;

    public void AddEdge(GraphEdge edge)
    {
        _edges.Add(edge);
        if (_nodes.TryGetValue(edge.FromNodeId, out var fromNode))
            fromNode.OutgoingEdges.Add(edge);
        if (_nodes.TryGetValue(edge.ToNodeId, out var toNode))
            toNode.IncomingEdges.Add(edge);
    }

    /// <summary>
    /// Brute-force nearest node by straight-line distance. Fine for a single-city graph
    /// (tens of thousands of nodes); swap for a k-d tree / R-tree if it ever feels slow.
    /// </summary>
    public GraphNode FindNearestNode(double latitude, double longitude)
    {
        GraphNode? best = null;
        double bestDist = double.MaxValue;

        foreach (var node in _nodes.Values)
        {
            var d = HaversineMeters(latitude, longitude, node.Latitude, node.Longitude);
            if (d < bestDist)
            {
                bestDist = d;
                best = node;
            }
        }

        return best ?? throw new InvalidOperationException("Graph has no nodes.");
    }

    public static double HaversineMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthRadiusM = 6371000;
        double dLat = DegToRad(lat2 - lat1);
        double dLon = DegToRad(lon2 - lon1);
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                 + Math.Cos(DegToRad(lat1)) * Math.Cos(DegToRad(lat2))
                 * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return earthRadiusM * c;
    }

    private static double DegToRad(double deg) => deg * Math.PI / 180.0;
}
