using System.Globalization;
using System.Text;
using System.Text.Json;
using KooRah.Core.Models;
using KooRah.Core.Osm;
using KooRah.Core.Routing;
using KooRah.Core.Traffic;

namespace KooRah.App;

public partial class MainPage : ContentPage
{
    // TODO: move to a config/secrets file instead of a literal - see chat note.
    private const string TomTomApiKey = "YOUR_API_KEY";

    // Hardcoded for the first pass - swap for map-tap or an address search later.
    private static readonly (double Lat, double Lon) StartCoord = (35.7219, 51.3347); // Azadi
    private static readonly (double Lat, double Lon) EndCoord = (35.6997, 51.3380);

    private RoadGraph? _graph;
    private readonly HttpClient _http = new();

    public MainPage()
    {
        InitializeComponent();
        _ = LoadGraphAsync();
    }

    private async Task LoadGraphAsync()
    {
        try
        {
            var localPbfPath = await EnsureLocalCopyAsync("tehran.osm.pbf");

            StatusLabel.Text = "Building road graph (first run only takes longer)...";
            _graph = await Task.Run(() => OsmGraphBuilder.BuildFromPbf(localPbfPath));

            StatusLabel.Text = $"Ready — {_graph.Nodes.Count:N0} nodes, {_graph.Edges.Count:N0} edges.";
            FindRouteButton.IsEnabled = true;

            await ShowEmptyMapAsync();
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Failed to load graph: {ex.Message}";
        }
    }

    private async void OnFindRouteClicked(object? sender, EventArgs e)
    {
        if (_graph is null) return;

        FindRouteButton.IsEnabled = false;
        StatusLabel.Text = "Finding candidate route...";

        var graph = _graph;
        var start = graph.FindNearestNode(StartCoord.Lat, StartCoord.Lon);
        var end = graph.FindNearestNode(EndCoord.Lat, EndCoord.Lon);

        var comparer = new RouteComparer();

        // Initial pass (free-flow speeds) just to know which edges to refresh traffic on.
        var (initial, _) = await Task.Run(() => comparer.FindBestRoute(graph, start.Id, end.Id));
        if (initial is null)
        {
            StatusLabel.Text = "No route found between those points.";
            FindRouteButton.IsEnabled = true;
            return;
        }

        StatusLabel.Text = "Updating live traffic for candidate route...";
        var traffic = new TomTomTrafficProvider(TomTomApiKey);
        var candidateEdges = ResolveEdges(graph, initial.NodePath);

        foreach (var edge in candidateEdges)
        {
            try { await traffic.UpdateEdgeSpeedAsync(edge, _http); }
            catch { /* keep free-flow speed for this edge if the call fails */ }
        }

        StatusLabel.Text = "Racing algorithms with live traffic...";
        var (best, all) = await Task.Run(() => comparer.FindBestRoute(graph, start.Id, end.Id));

        if (best is null)
        {
            StatusLabel.Text = "No route found after traffic update.";
            FindRouteButton.IsEnabled = true;
            return;
        }

        var timings = string.Join(", ", all.Select(r => $"{r.AlgorithmName} {r.ComputeTime.TotalMilliseconds:F1}ms"));
        StatusLabel.Text =
            $"{best.AlgorithmName} won — {best.TotalTravelTimeSeconds / 60:F0} min, " +
            $"{best.TotalDistanceMeters / 1000:F1} km ({timings})";

        await DrawRouteAsync(graph, best.NodePath);
        FindRouteButton.IsEnabled = true;
    }

    private static List<GraphEdge> ResolveEdges(RoadGraph graph, List<long> nodePath)
    {
        var edges = new List<GraphEdge>();
        for (int i = 0; i < nodePath.Count - 1; i++)
        {
            var edge = graph.Nodes[nodePath[i]].OutgoingEdges
                .FirstOrDefault(e => e.ToNodeId == nodePath[i + 1]);
            if (edge != null)
                edges.Add(edge);
        }
        return edges;
    }

    private async Task ShowEmptyMapAsync() => await RenderMapAsync(Array.Empty<(double, double)>());

    private async Task DrawRouteAsync(RoadGraph graph, List<long> nodePath)
    {
        var coords = nodePath
            .Select(id => (graph.Nodes[id].Latitude, graph.Nodes[id].Longitude))
            .ToArray();
        await RenderMapAsync(coords);
    }

    private async Task RenderMapAsync((double Lat, double Lon)[] routeCoords)
    {
        var templateBytes = await ReadAssetAsync("map_template.html");
        var template = Encoding.UTF8.GetString(templateBytes);

        var routeJson = JsonSerializer.Serialize(routeCoords.Select(c => new[] { c.Lat, c.Lon }));
        var startJson = JsonSerializer.Serialize(new[] { StartCoord.Lat, StartCoord.Lon });
        var endJson = JsonSerializer.Serialize(new[] { EndCoord.Lat, EndCoord.Lon });

        var html = template
            .Replace("__ROUTE_COORDS__", routeJson)
            .Replace("__START_COORD__", startJson)
            .Replace("__END_COORD__", endJson);

        MapView.Source = new HtmlWebViewSource { Html = html };
    }

    /// <summary>Copies a bundled MauiAsset out to app-writable storage, once, and returns the real path.</summary>
    private static async Task<string> EnsureLocalCopyAsync(string logicalName)
    {
        var localPath = Path.Combine(FileSystem.CacheDirectory, logicalName);
        if (!File.Exists(localPath))
        {
            using var assetStream = await FileSystem.OpenAppPackageFileAsync(logicalName);
            using var fileStream = File.Create(localPath);
            await assetStream.CopyToAsync(fileStream);
        }
        return localPath;
    }

    private static async Task<byte[]> ReadAssetAsync(string logicalName)
    {
        using var stream = await FileSystem.OpenAppPackageFileAsync(logicalName);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        return ms.ToArray();
    }
}
