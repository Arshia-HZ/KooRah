using KooRah.Core.Models;

namespace KooRah.Core.Routing;

public sealed class RouteResult
{
    public required string AlgorithmName { get; init; }
    public required List<long> NodePath { get; init; }
    public required double TotalTravelTimeSeconds { get; init; }
    public required double TotalDistanceMeters { get; init; }
    public required TimeSpan ComputeTime { get; init; }
}

public interface IRouteFinder
{
    string Name { get; }

    /// <summary>Finds the fastest path by current (traffic-aware) travel time between two nodes.</summary>
    RouteResult? FindRoute(RoadGraph graph, long startNodeId, long endNodeId);
}
