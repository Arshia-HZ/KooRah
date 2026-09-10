using KooRah.Core.Models;

namespace KooRah.Core.Routing;

public sealed class RouteComparer
{
    private readonly List<IRouteFinder> _finders;

    public RouteComparer(IEnumerable<IRouteFinder>? finders = null)
    {
        _finders = finders?.ToList() ?? new List<IRouteFinder>
        {
            new DijkstraRouteFinder(),
            new AStarRouteFinder(),
            new BidirectionalAStarRouteFinder()
        };
    }

    /// <summary>
    /// Runs every algorithm against the current graph state and returns all results plus
    /// the fastest one. Since they all compute over the same traffic-aware weights, they
    /// should agree on travel time (barring bugs) — differences you'll actually see are in
    /// ComputeTime, which is the interesting number here for a single small-city graph.
    /// </summary>
    public (RouteResult? Best, List<RouteResult> All) FindBestRoute(RoadGraph graph, long startNodeId, long endNodeId)
    {
        var results = new List<RouteResult>();

        foreach (var finder in _finders)
        {
            var result = finder.FindRoute(graph, startNodeId, endNodeId);
            if (result is not null)
                results.Add(result);
        }

        var best = results
            .OrderBy(r => r.TotalTravelTimeSeconds)
            .ThenBy(r => r.ComputeTime)
            .FirstOrDefault();

        return (best, results);
    }
}
