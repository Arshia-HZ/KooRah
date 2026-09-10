using System.Globalization;
using System.Text;
using System.Text.Json;
using KooRah.App.Services;
using KooRah.Core.Models;
using KooRah.Core.Osm;
using KooRah.Core.Routing;
using KooRah.Core.Traffic;

namespace KooRah.App;

public partial class MainPage : ContentPage
{
    private enum NavState
    {
        Idle,           // Waiting for inputs / route calculation
        RouteCalculated,// 2D route overview displayed, ready to start
        Navigating      // 3D driver follow navigation active
    }

    private static readonly Color ColorTurquoise = Color.FromArgb("#23A6A0");
    private static readonly Color ColorAmber = Color.FromArgb("#E0A458");
    private static readonly Color ColorMuted = Color.FromArgb("#8B96A3");
    private static readonly Color ColorActiveNavBtn = Color.FromArgb("#364453");

    private static readonly (double Lat, double Lon) FallbackAzadi = (35.6997, 51.3375);
    private const string FallbackOriginTitle = "Azadi Square";

    private readonly GeocodingService _geocoding = new();
    private readonly HttpClient _http = new();

    private RoadGraph? _graph;

    // Origin defaults to Azadi if location access is not granted
    private (double Lat, double Lon) _startCoord = FallbackAzadi;
    private string _startTitle = FallbackOriginTitle;

    // Destination is empty by default until user selects one
    private (double Lat, double Lon)? _endCoord = null;
    private string _endTitle = string.Empty;

    private NavState _navState = NavState.Idle;
    private bool _isSearchingOrigin = false;
    private CancellationTokenSource? _searchCts;
    private bool _isProgrammaticTextChange = false;

    public MainPage()
    {
        InitializeComponent();

        OriginEntry.Text = _startTitle;
        DestinationEntry.Text = string.Empty;
        FindRouteButton.IsEnabled = false;
        FindRouteButton.Text = "Find Route";
        AlgorithmDetailsLabel.Text = "Choose a destination to route";

        _ = LoadGraphAndLocationAsync();
    }

    private void SetStatus(string text, Color dotColor)
    {
        StatusLabel.Text = text;
        StatusDot.Fill = new SolidColorBrush(dotColor);
    }

    private async Task LoadGraphAndLocationAsync()
    {
        // 1. Concurrently start building road network
        _ = LoadGraphAsync();

        // 2. Query user GPS location as default origin (loads single local tile at zoom 15)
        await DetectUserLocationAsync();
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

            if (_endCoord.HasValue)
            {
                AlgorithmDetailsLabel.Text = "Tap Find Route to calculate";
                FindRouteButton.IsEnabled = true;
            }
            else
            {
                AlgorithmDetailsLabel.Text = "Choose a destination to route";
                FindRouteButton.IsEnabled = false;
            }
        }
        catch (Exception ex)
        {
            SetStatus("Failed to load graph", ColorAmber);
            WarningLabel.Text = ex.Message;
            WarningLabel.IsVisible = true;
        }
    }

    private async Task DetectUserLocationAsync()
    {
        bool detected = false;
        try
        {
            var status = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
            if (status != PermissionStatus.Granted)
            {
                status = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();
            }

            if (status == PermissionStatus.Granted)
            {
                var request = new GeolocationRequest(GeolocationAccuracy.Medium, TimeSpan.FromSeconds(5));
                var location = await Geolocation.Default.GetLocationAsync(request);

                if (location != null)
                {
                    _startCoord = (location.Latitude, location.Longitude);

                    var placeName = await _geocoding.ReverseGeocodeAsync(location.Latitude, location.Longitude);
                    _startTitle = !string.IsNullOrWhiteSpace(placeName) ? placeName : "My Current Location";
                    detected = true;
                }
            }
        }
        catch
        {
            // Geolocation unavailable or unsupported on desktop platform; keep fallback
        }

        if (!detected)
        {
            _startCoord = FallbackAzadi;
            _startTitle = FallbackOriginTitle;
        }

        _isProgrammaticTextChange = true;
        OriginEntry.Text = _startTitle;
        _isProgrammaticTextChange = false;

        // Render the single local tile around the detected origin
        await ShowEmptyMapAsync();
    }

    private async void OnLocateMeClicked(object? sender, EventArgs e)
    {
        if (_navState == NavState.Navigating)
        {
            await StopNavigationAsync();
        }

        SetStatus("Locating via GPS...", ColorAmber);
        LocateMeButton.IsEnabled = false;

        await DetectUserLocationAsync();

        LocateMeButton.IsEnabled = true;
        if (_graph != null)
        {
            SetStatus($"Ready — {_graph.Nodes.Count:N0} nodes", ColorTurquoise);
        }
    }

    #region Search & Autocomplete Handlers

    private void OnOriginFocused(object? sender, FocusEventArgs e)
    {
        _isSearchingOrigin = true;
    }

    private void OnDestinationFocused(object? sender, FocusEventArgs e)
    {
        _isSearchingOrigin = false;
    }

    private void OnOriginTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_isProgrammaticTextChange) return;
        _isSearchingOrigin = true;

        if (string.IsNullOrWhiteSpace(e.NewTextValue))
        {
            if (_navState == NavState.Navigating)
            {
                _ = StopNavigationAsync();
            }
            _startCoord = FallbackAzadi;
            _startTitle = FallbackOriginTitle;
            ResetToIdleState();
        }

        TriggerSearchDebounced(e.NewTextValue);
    }

    private void OnDestinationTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_isProgrammaticTextChange) return;
        _isSearchingOrigin = false;

        if (string.IsNullOrWhiteSpace(e.NewTextValue))
        {
            if (_navState == NavState.Navigating)
            {
                _ = StopNavigationAsync();
            }
            _endCoord = null;
            _endTitle = string.Empty;
            ResetToIdleState();
            _ = ShowEmptyMapAsync();
        }

        TriggerSearchDebounced(e.NewTextValue);
    }

    private void ResetToIdleState()
    {
        _navState = NavState.Idle;
        FindRouteButton.Text = "Find Route";
        FindRouteButton.BackgroundColor = ColorTurquoise;
        FindRouteButton.TextColor = Color.FromArgb("#10151B");
        FindRouteButton.IsEnabled = _endCoord.HasValue && _graph != null;
        EtaLabel.Text = "-- min";
        DistanceLabel.Text = string.Empty;
        AlgorithmDetailsLabel.Text = _endCoord.HasValue
            ? "Tap Find Route to calculate"
            : "Choose a destination to route";
    }

    private void TriggerSearchDebounced(string query)
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2)
        {
            SuggestionsBorder.IsVisible = false;
            return;
        }

        Task.Delay(350, ct).ContinueWith(async _ =>
        {
            if (ct.IsCancellationRequested) return;

            var results = await _geocoding.SearchPlacesAsync(query, ct);
            if (ct.IsCancellationRequested) return;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (results.Count > 0)
                {
                    SuggestionsCollection.ItemsSource = results;
                    SuggestionsBorder.IsVisible = true;
                }
                else
                {
                    SuggestionsBorder.IsVisible = false;
                }
            });
        }, TaskScheduler.Default);
    }

    private void OnSuggestionSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not PlaceSearchResult selected)
            return;

        _isProgrammaticTextChange = true;
        if (_isSearchingOrigin)
        {
            _startCoord = (selected.Latitude, selected.Longitude);
            _startTitle = selected.Name;
            OriginEntry.Text = selected.Name;
        }
        else
        {
            _endCoord = (selected.Latitude, selected.Longitude);
            _endTitle = selected.Name;
            DestinationEntry.Text = selected.Name;
        }
        _isProgrammaticTextChange = false;

        SuggestionsBorder.IsVisible = false;
        SuggestionsCollection.SelectedItem = null;

        // Reset to idle routing state
        ResetToIdleState();

        // Re-center map with updated endpoints
        _ = ShowEmptyMapAsync();
    }

    #endregion

    #region Route Calculation & Navigation State Machine

    private async void OnFindRouteClicked(object? sender, EventArgs e)
    {
        if (_navState == NavState.Navigating)
        {
            // Stop 3D driver follow navigation and return to 2D route overview
            await StopNavigationAsync();
            return;
        }

        if (_navState == NavState.RouteCalculated)
        {
            // Transition into 3D driver follow mode
            await StartNavigationAsync();
            return;
        }

        // Idle state: compute optimal route
        if (_graph is null || !_endCoord.HasValue) return;

        SuggestionsBorder.IsVisible = false;
        FindRouteButton.IsEnabled = false;
        WarningLabel.IsVisible = false;
        SetStatus("Finding candidate route...", ColorMuted);

        var graph = _graph;
        var start = graph.FindNearestNode(_startCoord.Lat, _startCoord.Lon);
        var end = graph.FindNearestNode(_endCoord.Value.Lat, _endCoord.Value.Lon);

        var comparer = new RouteComparer();

        // Initial pass (free-flow speeds) to determine candidate route edges
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
        var apiKey = await AppSecrets.GetTomTomApiKeyAsync();
        var traffic = new TomTomTrafficProvider(apiKey);
        var candidateEdges = ResolveEdges(graph, initial.NodePath);

        foreach (var edge in candidateEdges)
        {
            try { await traffic.UpdateEdgeSpeedAsync(edge, _http); }
            catch { /* keep free-flow speed on edge if call fails */ }
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

        // Transition to RouteCalculated state
        _navState = NavState.RouteCalculated;
        FindRouteButton.Text = "▶ Start Navigation";
        FindRouteButton.BackgroundColor = ColorTurquoise;
        FindRouteButton.TextColor = Color.FromArgb("#10151B");
        FindRouteButton.IsEnabled = true;
    }

    private async Task StartNavigationAsync()
    {
        _navState = NavState.Navigating;
        FindRouteButton.Text = "✕ Exit Navigation";
        FindRouteButton.BackgroundColor = ColorActiveNavBtn;
        FindRouteButton.TextColor = Color.FromArgb("#EDEFF2");

        SetStatus("3D Follow Navigation Active", ColorTurquoise);
        try
        {
            await MapView.EvaluateJavaScriptAsync("startDriving();");
        }
        catch { /* WebView guard */ }
    }

    private async Task StopNavigationAsync()
    {
        _navState = NavState.RouteCalculated;
        FindRouteButton.Text = "▶ Start Navigation";
        FindRouteButton.BackgroundColor = ColorTurquoise;
        FindRouteButton.TextColor = Color.FromArgb("#10151B");

        SetStatus($"Ready — {_graph?.Nodes.Count:N0} nodes", ColorTurquoise);
        try
        {
            await MapView.EvaluateJavaScriptAsync("stopDriving();");
        }
        catch { /* WebView guard */ }
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

    #endregion

    #region Map Rendering

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
        var startJson = JsonSerializer.Serialize(new[] { _startCoord.Lat, _startCoord.Lon });
        var endJson = _endCoord.HasValue
            ? JsonSerializer.Serialize(new[] { _endCoord.Value.Lat, _endCoord.Value.Lon })
            : "null";

        var html = template
            .Replace("__ROUTE_COORDS__", routeJson)
            .Replace("__START_COORD__", startJson)
            .Replace("__END_COORD__", endJson)
            .Replace("__START_TITLE__", EscapeForJs(_startTitle))
            .Replace("__END_TITLE__", EscapeForJs(_endTitle));

        MapView.Source = new HtmlWebViewSource { Html = html };
    }

    private static string EscapeForJs(string text) =>
        text.Replace("'", "\\'").Replace("\"", "\\\"");

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

    #endregion
}
