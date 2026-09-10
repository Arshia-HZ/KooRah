using KooRah.Core.Models;
using KooRah.Core.Osm;
using KooRah.Core.Routing;
using KooRah.Core.Traffic;

Console.WriteLine("=== KooRah Console Test ===");

// 1. Build the graph once from the PBF file
Console.WriteLine("Building graph from 'tehran.osm.pbf'...");
var graph = OsmGraphBuilder.BuildFromPbf("tehran.osm.pbf");
Console.WriteLine($"Graph built: {graph.Nodes.Count:N0} nodes, {graph.Edges.Count:N0} edges.\n");

// 2. Select start & end locations and find candidate route
Console.WriteLine("Finding candidate route (Azadi to destination)...");
var start = graph.FindNearestNode(35.7219, 51.3347); // start lat/lon
var end   = graph.FindNearestNode(35.6997, 51.3380);  // destination lat/lon

var comparer = new RouteComparer();
var (initialBest, initialAll) = comparer.FindBestRoute(graph, start.Id, end.Id);

// Resolve candidate route edges from the initial path
var candidateRouteEdges = new List<GraphEdge>();
if (initialBest != null)
{
    for (int i = 0; i < initialBest.NodePath.Count - 1; i++)
    {
        var fromId = initialBest.NodePath[i];
        var toId = initialBest.NodePath[i + 1];
        var edge = graph.Nodes[fromId].OutgoingEdges.FirstOrDefault(e => e.ToNodeId == toId);
        if (edge != null)
            candidateRouteEdges.Add(edge);
    }
}
Console.WriteLine($"Found initial route with {candidateRouteEdges.Count} edges.");

// 3. Update live traffic for candidate route edges
Console.WriteLine("\nUpdating live traffic for candidate route edges...");
var traffic = new TomTomTrafficProvider("7cbcWRLsLafqAjJAfHT9XRYtDjkDbtXs");
using var http = new HttpClient();

// e.g. after finding a candidate path, refresh traffic on its edges before trusting the ETA:
foreach (var edge in candidateRouteEdges)
{
    await traffic.UpdateEdgeSpeedAsync(edge, http);
}

// 4. Find the best route after traffic updates
Console.WriteLine("\nEvaluating routes across racing algorithms (Dijkstra, A*, Bidirectional A*)...");
var (best, all) = comparer.FindBestRoute(graph, start.Id, end.Id);

Console.WriteLine($"Winner: {best?.AlgorithmName}, {best?.TotalTravelTimeSeconds:F0}s, " +
                   $"{best?.TotalDistanceMeters / 1000:F1} km");

foreach (var r in all)
    Console.WriteLine($"  {r.AlgorithmName}: {r.ComputeTime.TotalMilliseconds:F1}ms compute");

Console.WriteLine("\nDone!");
