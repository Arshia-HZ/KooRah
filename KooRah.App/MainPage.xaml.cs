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
    private const string TomTomApiKey = "YOUR_API_KEY";

    private static readonly (double Lat, double Lon) StartCoord = (35.7219, 51.3347); // Azadi
    private static readonly (double Lat, double Lon) EndCoord = (35.6997, 51.3380);

    private static readonly Color ColorTurquoise = Color.FromArgb("#23A6A0");
    private static readonly Color ColorAmber = Color.FromArgb("#E0A458");
    private static readonly Color ColorMuted = Color.FromArgb("#8B96A3");

    private RoadGraph? _graph;
    private readonly HttpClient _http = new();

    public MainPage()
    {
        InitializeComponent();
        _ = LoadGraphAsync();
    }

    private void SetStatus(string text, Color dotColor)
    {
        StatusLabel.Text = text;
        StatusDot.Fill = new SolidColorBrush(dotColor);
    }

    private async Task LoadGraphAsync()
    {
        try
        {
            SetStatus("Loading road network...", ColorMuted);
            var localPbfPath = await EnsureLocalCopyAsync("tehran.osm.pbf");

            SetStatus("Building road graph...", ColorMuted);
            _graph = await Task.Run(() => OsmGraphBuilder.BuildFromPbf(localPbfPath));

            SetStatus($"Ready — {_graph.Nodes.Count:N0} nodes", ColorTurquoise);
            AlgorithmDetailsLabel.Text = "Tap Find Route to calculate";
            FindRouteButton.IsEnabled = true;

            await ShowEmptyMapAsync();
        }
        catch (Exception ex)
        {
            SetStatus("Failed to load graph", ColorAmber);
            WarningLabel.Text = ex.Message;
            WarningLabel.IsVisible = true;
        }
    }

    private async void OnFindRouteClicked(object? sender, EventArgs e)
    {
        if (_graph is null) return;

        FindRouteButton.IsEnabled = false;
        WarningLabel.IsVisible = false;
        SetStatus("Finding candidate route...", ColorMuted);

        var graph = _graph;
        var start = graph.FindNearestNode(StartCoord.Lat, StartCoord.Lon);
        var end = graph.FindNearestNode(EndCoord.Lat, EndCoord.Lon);

        var comparer = new RouteComparer();

        // Initial pass (free-flow speeds) just to know which edges to refresh traffic on.
        var (initial, _) = await Task.Run(() => comparer.FindBestRoute(graph, start.Id, end.Id));
        if (initial is null)
        {
            SetStatus("No route found", ColorAmber);
            WarningLabel.Text = "No navigable path found between coordinates.";
            WarningLabel.IsVisible = true;
            FindRouteButton.IsEnabled = true;
            return;
        }

        SetStatus("Updating live traffic...", ColorAmber);
        var traffic = new TomTomTrafficProvider(TomTomApiKey);
        var candidateEdges = ResolveEdges(graph, initial.NodePath);

        foreach (var edge in candidateEdges)
        {
            try { await traffic.UpdateEdgeSpeedAsync(edge, _http); }
            catch { /* keep free-flow speed for this edge if the call fails */ }
        }

        SetStatus("Racing algorithms...", ColorMuted);
        var (best, all) = await Task.Run(() => comparer.FindBestRoute(graph, start.Id, end.Id));

        if (best is null)
        {
            SetStatus("Routing failed", ColorAmber);
            WarningLabel.Text = "Could not compute route with current traffic conditions.";
            WarningLabel.IsVisible = true;
            FindRouteButton.IsEnabled = true;
            return;
        }

        // Display results according to night-driving design specs:
        var etaMinutes = Math.Max(1, (int)Math.Round(best.TotalTravelTimeSeconds / 60.0));
        EtaLabel.Text = $"{etaMinutes} min";
        DistanceLabel.Text = $"{best.TotalDistanceMeters / 1000.0:F1} km";

        AlgorithmDetailsLabel.Text = $"{best.AlgorithmName} · {best.ComputeTime.TotalMilliseconds:F1}ms";
        SetStatus($"Ready — {_graph.Nodes.Count:N0} nodes", ColorTurquoise);

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
