using OsmSharp;
using OsmSharp.Streams;
using KooRah.Core.Models;

namespace KooRah.Core.Osm;

/// <summary>
/// Turns a .osm.pbf extract (e.g. a Tehran bounding-box export) into a RoadGraph.
/// Only keeps ways tagged as drivable roads and treats each way as a chain of directed
/// edges between consecutive nodes (both directions unless oneway=yes).
/// </summary>
public static class OsmGraphBuilder
{
    // Rough free-flow speed defaults by OSM highway tag, km/h. Tune as you like.
    private static readonly Dictionary<string, double> DefaultSpeedByHighwayType = new()
    {
        ["motorway"] = 100,
        ["trunk"] = 80,
        ["primary"] = 60,
        ["secondary"] = 50,
        ["tertiary"] = 40,
        ["residential"] = 30,
        ["living_street"] = 15,
        ["unclassified"] = 30,
    };

    public static RoadGraph BuildFromPbf(string pbfFilePath)
    {
        var graph = new RoadGraph();

        using var fileStream = File.OpenRead(pbfFilePath);
        var source = new PBFOsmStreamSource(fileStream);

        // Pass 1: nodes (need lat/lon before we can build edges)
        var nodeCoords = new Dictionary<long, (double Lat, double Lon)>();
        foreach (var element in source)
        {
            if (element.Type == OsmGeoType.Node && element is Node n && n.Id.HasValue
                && n.Latitude.HasValue && n.Longitude.HasValue)
            {
                nodeCoords[n.Id.Value] = (n.Latitude.Value, n.Longitude.Value);
            }
        }

        // Pass 2: ways -> edges (re-open the stream; OsmSharp streams are forward-only)
        fileStream.Position = 0;
        var source2 = new PBFOsmStreamSource(fileStream);

        foreach (var element in source2)
        {
            if (element.Type != OsmGeoType.Way || element is not Way way || way.Nodes is null)
                continue;

            if (way.Tags is null || !way.Tags.TryGetValue("highway", out var highwayType))
                continue; // not a road

            if (!DefaultSpeedByHighwayType.TryGetValue(highwayType, out var freeFlowSpeed))
                continue; // unsupported/non-drivable type (footway, cycleway, etc.)

            bool isOneWay = way.Tags.TryGetValue("oneway", out var oneway) && oneway == "yes";

            for (int i = 0; i < way.Nodes.Length - 1; i++)
            {
                var fromId = way.Nodes[i];
                var toId = way.Nodes[i + 1];

                if (!nodeCoords.TryGetValue(fromId, out var fromCoord) ||
                    !nodeCoords.TryGetValue(toId, out var toCoord))
                    continue;

                EnsureNode(graph, fromId, fromCoord);
                EnsureNode(graph, toId, toCoord);

                var lengthMeters = RoadGraph.HaversineMeters(fromCoord.Lat, fromCoord.Lon, toCoord.Lat, toCoord.Lon);
                var midLat = (fromCoord.Lat + toCoord.Lat) / 2.0;
                var midLon = (fromCoord.Lon + toCoord.Lon) / 2.0;

                graph.AddEdge(new GraphEdge
                {
                    FromNodeId = fromId,
                    ToNodeId = toId,
                    LengthMeters = lengthMeters,
                    FreeFlowSpeedKph = freeFlowSpeed,
                    CurrentSpeedKph = freeFlowSpeed, // until traffic data arrives
                    OsmWayId = way.Id ?? 0,
                    MidLatitude = midLat,
                    MidLongitude = midLon
                });

                if (!isOneWay)
                {
                    graph.AddEdge(new GraphEdge
                    {
                        FromNodeId = toId,
                        ToNodeId = fromId,
                        LengthMeters = lengthMeters,
                        FreeFlowSpeedKph = freeFlowSpeed,
                        CurrentSpeedKph = freeFlowSpeed,
                        OsmWayId = way.Id ?? 0,
                        MidLatitude = midLat,
                        MidLongitude = midLon
                    });
                }
            }
        }

        return graph;
    }

    private static void EnsureNode(RoadGraph graph, long id, (double Lat, double Lon) coord)
    {
        if (!graph.Nodes.ContainsKey(id))
            graph.AddNode(new GraphNode { Id = id, Latitude = coord.Lat, Longitude = coord.Lon });
    }
}
