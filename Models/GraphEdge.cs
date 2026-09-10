namespace KooRah.Core.Models;

/// <summary>
/// A directed road segment between two nodes. Distance is fixed at build time;
/// CurrentSpeedKph is mutated live as traffic data comes in, which is what your
/// routing algorithms should actually weigh on.
/// </summary>
public sealed class GraphEdge
{
    public required long FromNodeId { get; init; }
    public required long ToNodeId { get; init; }

    /// <summary>Physical length of the segment, in meters. Fixed.</summary>
    public required double LengthMeters { get; init; }

    /// <summary>Posted/free-flow speed with no traffic, km/h. Fixed. Used as a fallback.</summary>
    public required double FreeFlowSpeedKph { get; init; }

    /// <summary>Current observed speed, km/h. Updated by ITrafficProvider. Defaults to free-flow.</summary>
    public double CurrentSpeedKph { get; set; }

    /// <summary>OSM way id this edge came from — useful for matching traffic API segments back to edges.</summary>
    public long OsmWayId { get; init; }

    /// <summary>Midpoint coordinates, set at build time — used to query the traffic API for this segment.</summary>
    public required double MidLatitude { get; init; }
    public required double MidLongitude { get; init; }

    /// <summary>Travel time in seconds at current (traffic-aware) speed. This is the edge weight algorithms use.</summary>
    public double TravelTimeSeconds =>
        CurrentSpeedKph > 0 ? (LengthMeters / 1000.0) / CurrentSpeedKph * 3600.0 : double.PositiveInfinity;
}
