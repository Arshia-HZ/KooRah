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

    private static readonly Color ColorTurquoise = Color.FromArgb("#0D9488");
    private static readonly Color ColorAmber = Color.FromArgb("#D97706");
    private static readonly Color ColorMuted = Color.FromArgb("#64748B");

    private static readonly (double Lat, double Lon) FallbackAzadi = (35.6997, 51.3375);
    private const string FallbackOriginTitle = "میدان آزادی";

    private readonly GeocodingService _geocoding = new();
    private readonly SavedPlacesService _savedPlaces = new();
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

    // Active Route Information for Real-Time Navigation
    private List<long>? _lastNodePath = null;
    private RouteResult? _lastBestRoute = null;

    // Real-Time GPS Tracking State
    private CancellationTokenSource? _trackingCts = null;
    private double _lastLat = 0;
    private double _lastLon = 0;
    private double _currentHeading = 0;
    private bool _isListeningLocation = false;

    // Saved Places Modal State
    private string _selectedCategoryIcon = "🏠";
    private bool _saveFromGps = false;

    public MainPage()
    {
        InitializeComponent();

        OriginEntry.Text = _startTitle;
        DestinationEntry.Text = string.Empty;
        ClearOriginButton.IsVisible = !string.IsNullOrWhiteSpace(_startTitle);
        ClearDestinationButton.IsVisible = false;
        SaveDestinationButton.IsEnabled = false;

        FindRouteButton.IsEnabled = false;
        FindRouteButton.Text = "مسیریابی";
        AlgorithmDetailsLabel.Text = "مقصد را انتخاب کنید";

        SetBottomCardVisible(false);

        _savedPlaces.SavedPlacesChanged += async (s, e) =>
        {
            await MainThread.InvokeOnMainThreadAsync(LoadSavedPlacesAsync);
        };

        _ = LoadGraphAndLocationAsync();
        _ = LoadSavedPlacesAsync();
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
            SetStatus("بارگذاری شبکه معابر...", ColorMuted);
            var localPbfPath = await EnsureLocalCopyAsync("tehran.osm.pbf");

            SetStatus("ساخت گراف ناوبری...", ColorMuted);
            _graph = await Task.Run(() => OsmGraphBuilder.BuildFromPbf(localPbfPath));

            SetStatus($"آماده — {_graph.Nodes.Count:N0} تقاطع", ColorTurquoise);

            if (_endCoord.HasValue)
            {
                AlgorithmDetailsLabel.Text = "گزینه مسیریابی را انتخاب کنید";
                FindRouteButton.IsEnabled = true;
            }
            else
            {
                AlgorithmDetailsLabel.Text = "مقصد را انتخاب کنید";
                FindRouteButton.IsEnabled = false;
            }
        }
        catch (Exception ex)
        {
            SetStatus("خطا در بارگذاری نقشه", ColorAmber);
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
                    _lastLat = location.Latitude;
                    _lastLon = location.Longitude;

                    var placeName = await _geocoding.ReverseGeocodeAsync(location.Latitude, location.Longitude);
                    _startTitle = !string.IsNullOrWhiteSpace(placeName) ? placeName : "موقعیت فعلی من";
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
            _lastLat = FallbackAzadi.Lat;
            _lastLon = FallbackAzadi.Lon;
        }

        _isProgrammaticTextChange = true;
        OriginEntry.Text = _startTitle;
        ClearOriginButton.IsVisible = !string.IsNullOrWhiteSpace(_startTitle);
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

        SetStatus("دریافت موقعیت با GPS...", ColorAmber);
        LocateMeButton.IsEnabled = false;

        await DetectUserLocationAsync();

        LocateMeButton.IsEnabled = true;
        if (_graph != null)
        {
            SetStatus($"آماده — {_graph.Nodes.Count:N0} تقاطع", ColorTurquoise);
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
        ClearOriginButton.IsVisible = !string.IsNullOrWhiteSpace(e.NewTextValue);

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
        ClearDestinationButton.IsVisible = !string.IsNullOrWhiteSpace(e.NewTextValue);
        SaveDestinationButton.IsEnabled = !string.IsNullOrWhiteSpace(e.NewTextValue) && _endCoord.HasValue;

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
            SaveDestinationButton.IsEnabled = false;
            ResetToIdleState();
            _ = ShowEmptyMapAsync();
        }

        TriggerSearchDebounced(e.NewTextValue);
    }

    private void OnClearOriginClicked(object? sender, EventArgs e)
    {
        if (_navState == NavState.Navigating)
        {
            _ = StopNavigationAsync();
        }
        _isProgrammaticTextChange = true;
        OriginEntry.Text = string.Empty;
        ClearOriginButton.IsVisible = false;
        _isProgrammaticTextChange = false;

        _startCoord = FallbackAzadi;
        _startTitle = FallbackOriginTitle;
        ResetToIdleState();
        _ = ShowEmptyMapAsync();
    }

    private async void OnClearDestinationClicked(object? sender, EventArgs e)
    {
        if (_navState == NavState.Navigating)
        {
            await StopNavigationAsync();
        }
        _isProgrammaticTextChange = true;
        DestinationEntry.Text = string.Empty;
        ClearDestinationButton.IsVisible = false;
        SaveDestinationButton.IsEnabled = false;
        _isProgrammaticTextChange = false;

        _endCoord = null;
        _endTitle = string.Empty;
        SetBottomCardVisible(false);
        try
        {
            await MapView.EvaluateJavaScriptAsync("clearDestinationMarker();");
        }
        catch { }
        ResetToIdleState();
        await ShowEmptyMapAsync();
    }

    private void OnDestinationCompleted(object? sender, EventArgs e)
    {
        if (_endCoord.HasValue && _graph != null)
        {
            SetBottomCardVisible(true);
            OnFindRouteClicked(sender, e);
        }
    }

    private async void OnSwapEndpointsClicked(object? sender, EventArgs e)
    {
        if (_navState == NavState.Navigating)
        {
            await StopNavigationAsync();
        }

        if (!_endCoord.HasValue)
        {
            // If destination is empty, make current origin the destination and detect GPS as origin
            _endCoord = _startCoord;
            _endTitle = _startTitle;
            _startCoord = FallbackAzadi;
            _startTitle = FallbackOriginTitle;
        }
        else
        {
            var tempCoord = _startCoord;
            var tempTitle = _startTitle;

            _startCoord = _endCoord.Value;
            _startTitle = _endTitle;

            _endCoord = tempCoord;
            _endTitle = tempTitle;
        }

        _isProgrammaticTextChange = true;
        OriginEntry.Text = _startTitle;
        DestinationEntry.Text = _endTitle;
        ClearOriginButton.IsVisible = !string.IsNullOrWhiteSpace(_startTitle);
        ClearDestinationButton.IsVisible = !string.IsNullOrWhiteSpace(_endTitle);
        SaveDestinationButton.IsEnabled = _endCoord.HasValue;
        _isProgrammaticTextChange = false;

        ResetToIdleState();
        await ShowEmptyMapAsync();

        if (_graph != null && _endCoord.HasValue)
        {
            OnFindRouteClicked(sender, e);
        }
    }

    private void ResetToIdleState()
    {
        _navState = NavState.Idle;
        _lastNodePath = null;
        _lastBestRoute = null;

        FindRouteButton.Text = "مسیریابی";
        FindRouteButton.BackgroundColor = ColorTurquoise;
        FindRouteButton.TextColor = Color.FromArgb("#10151B");
        FindRouteButton.IsEnabled = _endCoord.HasValue && _graph != null;

        EtaLabel.Text = "-- دقیقه";
        DistanceLabel.Text = string.Empty;
        AlgorithmDetailsLabel.Text = _endCoord.HasValue
            ? "گزینه مسیریابی را انتخاب کنید"
            : "مقصد را انتخاب کنید";

        RouteBadgesLayout.IsVisible = false;
        OverviewPanel.IsVisible = true;
        ActiveNavPanel.IsVisible = false;
        TopSearchCard.IsVisible = true;
        RecenterButton.IsVisible = false;
        SetBottomCardVisible(false);
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
            ClearOriginButton.IsVisible = true;
        }
        else
        {
            _endCoord = (selected.Latitude, selected.Longitude);
            _endTitle = selected.Name;
            DestinationEntry.Text = selected.Name;
            ClearDestinationButton.IsVisible = true;
            SaveDestinationButton.IsEnabled = true;
        }
        _isProgrammaticTextChange = false;

        SuggestionsBorder.IsVisible = false;
        SuggestionsCollection.SelectedItem = null;

        // Reset to idle routing state
        ResetToIdleState();

        // Re-center map with updated endpoints
        _ = ShowEmptyMapAsync();
    }

    private async void OnBookmarkSuggestionClicked(object? sender, EventArgs e)
    {
        if (sender is Button { BindingContext: PlaceSearchResult res })
        {
            await _savedPlaces.SavePlaceAsync(res.Name, res.DisplayAddress, res.Latitude, res.Longitude, "⭐");
            SetStatus($"مکان ذخیره شد: {res.Name}", ColorTurquoise);
            await LoadSavedPlacesAsync();
        }
    }

    #endregion

    #region Saved Places Management & Quick Chips

    private async Task LoadSavedPlacesAsync()
    {
        var places = await _savedPlaces.GetSavedPlacesAsync();

        SavedPlacesCollectionView.ItemsSource = places;
        SavedPlacesChipsLayout.Children.Clear();

        // 1. Add "+ جدید" shortcut chip button
        var addChip = new Button
        {
            Text = "➕ جدید",
            FontFamily = "Manrope",
            FontSize = 11,
            FontAttributes = FontAttributes.Bold,
            TextColor = Color.FromArgb("#0F766E"),
            BackgroundColor = Color.FromArgb("#F0FDFA"),
            BorderColor = Color.FromArgb("#99F6E4"),
            BorderWidth = 1,
            CornerRadius = 12,
            HeightRequest = 32,
            Padding = new Thickness(10, 0)
        };
        addChip.Clicked += (s, e) => OnOpenSavedPlacesModalClicked(s, e);
        SavedPlacesChipsLayout.Children.Add(addChip);

        // 2. Add dynamic chips for each saved place
        foreach (var place in places)
        {
            var chip = new Button
            {
                Text = place.FormattedChipText,
                BindingContext = place,
                FontFamily = "Manrope",
                FontSize = 11,
                FontAttributes = FontAttributes.Bold,
                TextColor = Color.FromArgb("#1E293B"),
                BackgroundColor = Color.FromArgb("#F8FAFC"),
                BorderColor = Color.FromArgb("#E2E8F0"),
                BorderWidth = 1,
                CornerRadius = 12,
                HeightRequest = 32,
                Padding = new Thickness(10, 0)
            };
            chip.Clicked += OnSavedPlaceChipClicked;
            SavedPlacesChipsLayout.Children.Add(chip);
        }
    }

    private void OnSavedPlaceChipClicked(object? sender, EventArgs e)
    {
        if (sender is Button { BindingContext: SavedPlace place })
        {
            if (_navState == NavState.Navigating)
            {
                _ = StopNavigationAsync();
            }

            _endCoord = (place.Latitude, place.Longitude);
            _endTitle = place.Label;

            _isProgrammaticTextChange = true;
            DestinationEntry.Text = place.Label;
            ClearDestinationButton.IsVisible = true;
            SaveDestinationButton.IsEnabled = true;
            _isProgrammaticTextChange = false;

            ResetToIdleState();
            _ = ShowEmptyMapAsync();

            if (_graph != null)
            {
                OnFindRouteClicked(sender, e);
            }
        }
    }

    private void OnOpenSavedPlacesModalClicked(object? sender, EventArgs e)
    {
        NewPlaceLabelEntry.Text = !string.IsNullOrWhiteSpace(_endTitle) ? _endTitle : "";
        SavedPlacesModal.IsVisible = true;
        _ = LoadSavedPlacesAsync();
    }

    private void OnCloseSavedPlacesClicked(object? sender, EventArgs e)
    {
        SavedPlacesModal.IsVisible = false;
    }

    private void OnIconOptionClicked(object? sender, EventArgs e)
    {
        if (sender is Button btn)
        {
            _selectedCategoryIcon = btn.Text;

            foreach (var child in IconPickerLayout.Children)
            {
                if (child is Button b)
                {
                    if (b == btn)
                    {
                        b.BorderColor = ColorTurquoise;
                        b.BorderWidth = 1.5;
                        b.BackgroundColor = Color.FromArgb("#F0FDFA");
                    }
                    else
                    {
                        b.BorderColor = Color.FromArgb("#E2E8F0");
                        b.BorderWidth = 1;
                        b.BackgroundColor = Color.FromArgb("#F8FAFC");
                    }
                }
            }
        }
    }

    private void OnSourceDestinationClicked(object? sender, EventArgs e)
    {
        _saveFromGps = false;
        SourceDestinationButton.BorderColor = ColorTurquoise;
        SourceDestinationButton.BorderWidth = 1.5;
        SourceDestinationButton.TextColor = Color.FromArgb("#0F766E");
        SourceDestinationButton.BackgroundColor = Color.FromArgb("#F0FDFA");

        SourceGpsButton.BorderColor = Color.FromArgb("#E2E8F0");
        SourceGpsButton.BorderWidth = 1;
        SourceGpsButton.TextColor = ColorMuted;
        SourceGpsButton.BackgroundColor = Color.FromArgb("#F8FAFC");
    }

    private void OnSourceGpsClicked(object? sender, EventArgs e)
    {
        _saveFromGps = true;
        SourceGpsButton.BorderColor = ColorTurquoise;
        SourceGpsButton.BorderWidth = 1.5;
        SourceGpsButton.TextColor = Color.FromArgb("#0F766E");
        SourceGpsButton.BackgroundColor = Color.FromArgb("#F0FDFA");

        SourceDestinationButton.BorderColor = Color.FromArgb("#E2E8F0");
        SourceDestinationButton.BorderWidth = 1;
        SourceDestinationButton.TextColor = ColorMuted;
        SourceDestinationButton.BackgroundColor = Color.FromArgb("#F8FAFC");
    }

    private async void OnConfirmSaveNewPlaceClicked(object? sender, EventArgs e)
    {
        var label = NewPlaceLabelEntry.Text?.Trim();
        if (string.IsNullOrWhiteSpace(label))
        {
            label = "مکان ذخیره‌شده";
        }

        double lat;
        double lon;
        string address;

        if (_saveFromGps)
        {
            lat = _startCoord.Lat;
            lon = _startCoord.Lon;
            address = _startTitle;
        }
        else if (_endCoord.HasValue)
        {
            lat = _endCoord.Value.Lat;
            lon = _endCoord.Value.Lon;
            address = _endTitle;
        }
        else
        {
            lat = _startCoord.Lat;
            lon = _startCoord.Lon;
            address = _startTitle;
        }

        await _savedPlaces.SavePlaceAsync(label, address, lat, lon, _selectedCategoryIcon);
        NewPlaceLabelEntry.Text = string.Empty;
        SavedPlacesModal.IsVisible = false;

        SetStatus($"مکان با موفقیت ذخیره شد: {label}", ColorTurquoise);
        await LoadSavedPlacesAsync();
    }

    private void OnSaveCurrentDestinationClicked(object? sender, EventArgs e)
    {
        if (_endCoord.HasValue)
        {
            NewPlaceLabelEntry.Text = _endTitle;
            OnSourceDestinationClicked(sender, e);
            SavedPlacesModal.IsVisible = true;
        }
    }

    private void OnNavigateToSavedPlaceClicked(object? sender, EventArgs e)
    {
        if (sender is Button { BindingContext: SavedPlace place })
        {
            SavedPlacesModal.IsVisible = false;

            if (_navState == NavState.Navigating)
            {
                _ = StopNavigationAsync();
            }

            _endCoord = (place.Latitude, place.Longitude);
            _endTitle = place.Label;

            _isProgrammaticTextChange = true;
            DestinationEntry.Text = place.Label;
            ClearDestinationButton.IsVisible = true;
            SaveDestinationButton.IsEnabled = true;
            _isProgrammaticTextChange = false;

            ResetToIdleState();
            _ = ShowEmptyMapAsync();

            if (_graph != null)
            {
                OnFindRouteClicked(sender, e);
            }
        }
    }

    private async void OnDeleteSavedPlaceClicked(object? sender, EventArgs e)
    {
        if (sender is Button { BindingContext: SavedPlace place })
        {
            await _savedPlaces.DeletePlaceAsync(place.Id);
            SetStatus($"مکان حذف شد: {place.Label}", ColorAmber);
            await LoadSavedPlacesAsync();
        }
    }

    #endregion

    #region Route Calculation & Navigation State Machine

    private async void OnFindRouteClicked(object? sender, EventArgs e)
    {
        if (_navState == NavState.Navigating)
        {
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
        SetStatus("جستجوی کاندیدای مسیر...", ColorMuted);
        SetBottomCardVisible(true);

        var graph = _graph;
        var start = graph.FindNearestNode(_startCoord.Lat, _startCoord.Lon);
        var end = graph.FindNearestNode(_endCoord.Value.Lat, _endCoord.Value.Lon);

        var comparer = new RouteComparer();

        // Initial pass (free-flow speeds) to determine candidate route edges
        var (initial, _) = await Task.Run(() => comparer.FindBestRoute(graph, start.Id, end.Id));
        if (initial is null)
        {
            SetStatus("مسیری یافت نشد", ColorAmber);
            WarningLabel.Text = "هیچ مسیر معتبری بین این دو نقطه پیدا نشد.";
            WarningLabel.IsVisible = true;
            FindRouteButton.IsEnabled = true;
            return;
        }

        SetStatus("محاسبه سریع‌ترین مسیر با ترافیک هوشمند تهران...", ColorMuted);

        // Apply dynamic time-of-day traffic model across Tehran road network (100% free, local, < 3ms)
        var timeTraffic = new TehranTimeTrafficProvider();
        timeTraffic.ApplyTrafficToGraph(graph);

        var (best, all) = await Task.Run(() => comparer.FindBestRoute(graph, start.Id, end.Id));

        if (best is null)
        {
            SetStatus("مسیریابی ناموفق", ColorAmber);
            WarningLabel.Text = "محاسبه مسیر در شرایط فعلی ترافیک میسر نشد.";
            WarningLabel.IsVisible = true;
            FindRouteButton.IsEnabled = true;
            return;
        }

        _lastNodePath = best.NodePath;
        _lastBestRoute = best;

        // Display results according to traffic-aware specs:
        var etaMins = Math.Max(1, (int)Math.Round(best.TotalTravelTimeSeconds / 60.0));
        EtaLabel.Text = $"{etaMins} دقیقه";
        DistanceLabel.Text = $"{best.TotalDistanceMeters / 1000.0:F1} کیلومتر";

        var trafficStatus = TehranTimeTrafficProvider.GetTrafficStatusDescription();
        AlgorithmDetailsLabel.Text = $"{best.AlgorithmName} • {trafficStatus} ({best.ComputeTime.TotalMilliseconds:F1} میلی‌ثانیه)";
        SetStatus($"آماده — {_graph.Nodes.Count:N0} تقاطع", ColorTurquoise);

        RouteBadgesLayout.IsVisible = true;
        LiveTrafficBadgeLabel.Text = "🟢 ترافیک هوشمند تهران";
        await DrawRouteAsync(graph, best.NodePath);

        // Transition to RouteCalculated state
        _navState = NavState.RouteCalculated;
        FindRouteButton.Text = "▶ شروع ناوبری";
        FindRouteButton.BackgroundColor = ColorTurquoise;
        FindRouteButton.TextColor = Color.FromArgb("#10151B");
        FindRouteButton.IsEnabled = true;
        SaveDestinationButton.IsEnabled = true;
        SetBottomCardVisible(true);
    }

    private async Task StartNavigationAsync()
    {
        _navState = NavState.Navigating;

        // Adapt UI to Google Maps Driving Mode
        TopSearchCard.IsVisible = false;
        OverviewPanel.IsVisible = false;
        ActiveNavPanel.IsVisible = true;
        RecenterButton.IsVisible = false;
        SetBottomCardVisible(true);

        if (_lastBestRoute != null)
        {
            var etaMinutes = Math.Max(1, (int)Math.Round(_lastBestRoute.TotalTravelTimeSeconds / 60.0));
            var arrival = DateTime.Now.AddSeconds(_lastBestRoute.TotalTravelTimeSeconds).ToString("h:mm tt");
            NavEtaLabel.Text = $"{etaMinutes} دقیقه";
            NavDetailsLabel.Text = $"{_lastBestRoute.TotalDistanceMeters / 1000.0:F1} کیلومتر • {arrival}";
        }

        SetStatus("ناوبری سه‌بعدی فعال", ColorTurquoise);

        // Tell map to enter 3D driver mode
        try
        {
            await MapView.EvaluateJavaScriptAsync("startDriving();");
        }
        catch { /* WebView guard */ }

        // Start continuous real-time GPS tracking on device
        StartRealTimeGpsTracking();
    }

    private async Task StopNavigationAsync()
    {
        _navState = NavState.RouteCalculated;

        // Stop GPS listener
        StopRealTimeGpsTracking();

        // Restore UI
        TopSearchCard.IsVisible = true;
        ActiveNavPanel.IsVisible = false;
        OverviewPanel.IsVisible = true;
        RecenterButton.IsVisible = false;
        SetBottomCardVisible(true);

        FindRouteButton.Text = "▶ شروع ناوبری";
        FindRouteButton.BackgroundColor = ColorTurquoise;
        FindRouteButton.TextColor = Color.FromArgb("#10151B");

        SetStatus($"آماده — {_graph?.Nodes.Count:N0} تقاطع", ColorTurquoise);

        try
        {
            await MapView.EvaluateJavaScriptAsync("stopDriving();");
        }
        catch { /* WebView guard */ }
    }

    private async void OnExitNavClicked(object? sender, EventArgs e)
    {
        await StopNavigationAsync();
    }

    private async void OnVehicleSwitchClicked(object? sender, EventArgs e)
    {
        try
        {
            await MapView.EvaluateJavaScriptAsync("cycleVehicleFromApp();");
        }
        catch { /* WebView guard */ }
    }

    private void OnMapViewNavigating(object? sender, WebNavigatingEventArgs e)
    {
        if (e.Url != null && e.Url.StartsWith("koorah://nav/", StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            if (e.Url.Contains("following=0"))
            {
                if (_navState == NavState.Navigating)
                {
                    RecenterButton.IsVisible = true;
                }
            }
            else if (e.Url.Contains("following=1"))
            {
                RecenterButton.IsVisible = false;
            }
        }
        else if (e.Url != null && e.Url.StartsWith("koorah://map/click", StringComparison.OrdinalIgnoreCase))
        {
            e.Cancel = true;
            _ = HandleMapClickedLocationAsync(e.Url);
        }
    }

    private async Task HandleMapClickedLocationAsync(string url)
    {
        try
        {
            var queryIndex = url.IndexOf('?');
            if (queryIndex < 0) return;

            var queryString = url.Substring(queryIndex + 1);
            var pairs = queryString.Split('&');
            double? lat = null;
            double? lon = null;

            foreach (var pair in pairs)
            {
                var kv = pair.Split('=');
                if (kv.Length == 2)
                {
                    if (kv[0].Equals("lat", StringComparison.OrdinalIgnoreCase) &&
                        double.TryParse(kv[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsedLat))
                    {
                        lat = parsedLat;
                    }
                    else if (kv[0].Equals("lon", StringComparison.OrdinalIgnoreCase) &&
                        double.TryParse(kv[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsedLon))
                    {
                        lon = parsedLon;
                    }
                }
            }

            if (!lat.HasValue || !lon.HasValue) return;

            if (_navState == NavState.Navigating)
            {
                await StopNavigationAsync();
            }

            _endCoord = (lat.Value, lon.Value);
            SetStatus("دریافت نام موقعیت...", ColorMuted);

            // Reverse geocode to get human-friendly Persian street or place name
            var placeName = await _geocoding.ReverseGeocodeAsync(lat.Value, lon.Value);
            _endTitle = !string.IsNullOrWhiteSpace(placeName) ? placeName : $"موقعیت انتخابی ({lat.Value:F4}, {lon.Value:F4})";

            _isProgrammaticTextChange = true;
            DestinationEntry.Text = _endTitle;
            ClearDestinationButton.IsVisible = true;
            SaveDestinationButton.IsEnabled = true;
            _isProgrammaticTextChange = false;

            try
            {
                await MapView.EvaluateJavaScriptAsync($"setDestinationTitle('{EscapeForJs(_endTitle)}');");
            }
            catch { }

            // Show bottom route card and find route
            SetBottomCardVisible(true);

            if (_graph != null)
            {
                OnFindRouteClicked(this, EventArgs.Empty);
            }
            else
            {
                AlgorithmDetailsLabel.Text = "برای مسیریابی کلیک کنید";
                FindRouteButton.IsEnabled = true;
                FindRouteButton.Text = "مسیریابی";
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error handling map click: {ex.Message}");
        }
    }

    private void SetBottomCardVisible(bool visible)
    {
        BottomRouteCard.IsVisible = visible;

        bool hasRouteOrDest = _endCoord.HasValue || _navState == NavState.Navigating || _navState == NavState.RouteCalculated;
        ExpandRouteCardButton.IsVisible = !visible && hasRouteOrDest;

        if (!visible)
        {
            if (_navState == NavState.Navigating)
            {
                ExpandRouteCardButton.Text = "🧭 ادامه ناوبری";
            }
            else if (_navState == NavState.RouteCalculated)
            {
                ExpandRouteCardButton.Text = "🗺️ جزئیات مسیر";
            }
            else if (_endCoord.HasValue)
            {
                ExpandRouteCardButton.Text = "🚗 مسیریابی";
            }
        }

        RecenterButton.Margin = new Thickness(0, 0, 0, visible ? 245 : 30);

        try
        {
            _ = MapView.EvaluateJavaScriptAsync($"setBottomCardVisible({(visible ? "true" : "false")});");
        }
        catch { }
    }

    private void OnMinimizeRouteCardClicked(object? sender, EventArgs e)
    {
        SetBottomCardVisible(false);
    }

    private void OnExpandRouteCardClicked(object? sender, EventArgs e)
    {
        SetBottomCardVisible(true);
        if (_navState == NavState.Idle && _endCoord.HasValue && _graph != null)
        {
            OnFindRouteClicked(sender, e);
        }
    }

    private async void OnRecenterClicked(object? sender, EventArgs e)
    {
        RecenterButton.IsVisible = false;
        try
        {
            await MapView.EvaluateJavaScriptAsync("recenterMap();");
        }
        catch { /* WebView guard */ }
    }

    #endregion

    #region Real-Time GPS Tracking & Snap-to-Route Engine

    private void StartRealTimeGpsTracking()
    {
        StopRealTimeGpsTracking();

        _trackingCts = new CancellationTokenSource();
        var ct = _trackingCts.Token;

        // 1. Subscribe to foreground location listener
        try
        {
            if (!_isListeningLocation)
            {
                Geolocation.LocationChanged += OnDeviceLocationChanged;
                _ = Geolocation.StartListeningForegroundAsync(
                    new GeolocationListeningRequest(GeolocationAccuracy.Best, TimeSpan.FromSeconds(1)));
                _isListeningLocation = true;
            }
        }
        catch { /* Platform without foreground listener */ }

        // 2. High-frequency polling loop as reliable fallback across all Android/iOS device vendors
        Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var request = new GeolocationRequest(GeolocationAccuracy.Best, TimeSpan.FromSeconds(2));
                    var loc = await Geolocation.Default.GetLocationAsync(request, ct);
                    if (loc != null && !ct.IsCancellationRequested)
                    {
                        await ProcessLiveLocationUpdateAsync(loc);
                    }
                }
                catch { /* Ignore single polling errors while driving */ }

                try { await Task.Delay(1000, ct); }
                catch { break; }
            }
        }, ct);
    }

    private void StopRealTimeGpsTracking()
    {
        _trackingCts?.Cancel();
        _trackingCts = null;

        if (_isListeningLocation)
        {
            try
            {
                Geolocation.LocationChanged -= OnDeviceLocationChanged;
                Geolocation.StopListeningForeground();
            }
            catch { }
            _isListeningLocation = false;
        }
    }

    private void OnDeviceLocationChanged(object? sender, GeolocationLocationChangedEventArgs e)
    {
        if (e.Location != null && _navState == NavState.Navigating)
        {
            _ = ProcessLiveLocationUpdateAsync(e.Location);
        }
    }

    private async Task ProcessLiveLocationUpdateAsync(Location loc)
    {
        double lat = loc.Latitude;
        double lon = loc.Longitude;

        // 1. Calculate heading from GPS hardware Course or displacement delta
        double heading = _currentHeading;
        if (loc.Course.HasValue && loc.Course.Value > 0)
        {
            heading = loc.Course.Value;
        }
        else if (_lastLat != 0 && _lastLon != 0)
        {
            double distMoved = ComputeDistanceMeters(_lastLat, _lastLon, lat, lon);
            if (distMoved > 2.0)
            {
                heading = ComputeBearing(_lastLat, _lastLon, lat, lon);
            }
        }

        // 2. Snap to route & calculate remaining metrics
        double snappedLat = lat;
        double snappedLon = lon;
        double remainingDistMeters = 0;
        double remainingSeconds = 0;
        string maneuver = "straight";
        string nextStreet = "مقصد";

        if (_graph != null && _lastNodePath != null && _lastNodePath.Count > 1)
        {
            var (projLat, projLon, distToRoute, segmentIdx) = FindClosestRouteSegment(lat, lon, _graph, _lastNodePath);

            // If heading is not available or device is stationary, use the current road direction
            if ((heading == 0 || double.IsNaN(heading)) && segmentIdx < _lastNodePath.Count - 1)
            {
                var nA = _graph.Nodes[_lastNodePath[segmentIdx]];
                var nB = _graph.Nodes[_lastNodePath[segmentIdx + 1]];
                heading = ComputeBearing(nA.Latitude, nA.Longitude, nB.Latitude, nB.Longitude);
            }

            _currentHeading = heading;
            _lastLat = lat;
            _lastLon = lon;

            // Snap if within 45 meters of route
            if (distToRoute < 45.0)
            {
                snappedLat = projLat;
                snappedLon = projLon;
            }

            // Calculate remaining distance from current segment to end
            remainingDistMeters = ComputeRemainingDistance(_graph, _lastNodePath, segmentIdx, snappedLat, snappedLon);
            remainingSeconds = remainingDistMeters / Math.Max(7.0, (loc.Speed ?? 10.0)); // min 25 km/h for ETA

            // Determine next maneuver at upcoming node
            var (maneuverType, streetName) = ExtractUpcomingManeuver(_graph, _lastNodePath, segmentIdx);
            maneuver = maneuverType;
            nextStreet = !string.IsNullOrWhiteSpace(streetName) ? streetName : "مسیر پیش‌رو";
        }
        else if (_endCoord.HasValue)
        {
            remainingDistMeters = ComputeDistanceMeters(lat, lon, _endCoord.Value.Lat, _endCoord.Value.Lon);
            remainingSeconds = remainingDistMeters / 10.0;
        }

        var etaMinutes = Math.Max(1, (int)Math.Round(remainingSeconds / 60.0));
        var arrivalFormatted = DateTime.Now.AddSeconds(remainingSeconds).ToString("h:mm tt");

        // 3. Push to WebView
        await MainThread.InvokeOnMainThreadAsync(async () =>
        {
            NavEtaLabel.Text = $"{etaMinutes} دقیقه";
            NavDetailsLabel.Text = $"{remainingDistMeters / 1000.0:F1} کیلومتر • {arrivalFormatted}";

            try
            {
                var script = string.Format(CultureInfo.InvariantCulture,
                    "updateUserNavigation({0:F6}, {1:F6}, {2:F1}, {3:F1}, '{4}', '{5}', {6:F0}, '{7}');",
                    snappedLat, snappedLon, heading, loc.Speed ?? 0,
                    maneuver, EscapeForJs(nextStreet), remainingDistMeters, arrivalFormatted);

                await MapView.EvaluateJavaScriptAsync(script);
            }
            catch { }
        });
    }

    private static (double Lat, double Lon, double DistMeters, int SegmentIdx) FindClosestRouteSegment(
        double lat, double lon, RoadGraph graph, List<long> nodePath)
    {
        double bestDist = double.MaxValue;
        double bestLat = lat;
        double bestLon = lon;
        int bestIdx = 0;

        for (int i = 0; i < nodePath.Count - 1; i++)
        {
            var n1 = graph.Nodes[nodePath[i]];
            var n2 = graph.Nodes[nodePath[i + 1]];

            var (pLat, pLon, dist) = ProjectOnSegment(lat, lon, n1.Latitude, n1.Longitude, n2.Latitude, n2.Longitude);
            if (dist < bestDist)
            {
                bestDist = dist;
                bestLat = pLat;
                bestLon = pLon;
                bestIdx = i;
            }
        }

        return (bestLat, bestLon, bestDist, bestIdx);
    }

    private static (double Lat, double Lon, double DistMeters) ProjectOnSegment(
        double pLat, double pLon, double aLat, double aLon, double bLat, double bLon)
    {
        const double R = 6371000.0;
        double midLatRad = ((aLat + bLat) / 2.0) * Math.PI / 180.0;
        double cosLat = Math.Cos(midLatRad);

        double ax = aLon * (Math.PI / 180.0) * R * cosLat;
        double ay = aLat * (Math.PI / 180.0) * R;
        double bx = bLon * (Math.PI / 180.0) * R * cosLat;
        double by = bLat * (Math.PI / 180.0) * R;
        double px = pLon * (Math.PI / 180.0) * R * cosLat;
        double py = pLat * (Math.PI / 180.0) * R;

        double dx = bx - ax;
        double dy = by - ay;
        double segLenSq = dx * dx + dy * dy;

        if (segLenSq < 1e-6)
            return (aLat, aLon, Math.Sqrt((px - ax) * (px - ax) + (py - ay) * (py - ay)));

        double t = Math.Clamp(((px - ax) * dx + (py - ay) * dy) / segLenSq, 0.0, 1.0);
        double projX = ax + t * dx;
        double projY = ay + t * dy;

        double dist = Math.Sqrt((px - projX) * (px - projX) + (py - projY) * (py - projY));
        double projLat = projY / (R * (Math.PI / 180.0));
        double projLon = projX / (R * cosLat * (Math.PI / 180.0));

        return (projLat, projLon, dist);
    }

    private static double ComputeRemainingDistance(RoadGraph graph, List<long> nodePath, int segmentIdx, double curLat, double curLon)
    {
        if (segmentIdx >= nodePath.Count - 1) return 0;

        var nextNode = graph.Nodes[nodePath[segmentIdx + 1]];
        double dist = ComputeDistanceMeters(curLat, curLon, nextNode.Latitude, nextNode.Longitude);

        for (int i = segmentIdx + 1; i < nodePath.Count - 1; i++)
        {
            var edge = graph.Nodes[nodePath[i]].OutgoingEdges.FirstOrDefault(e => e.ToNodeId == nodePath[i + 1]);
            if (edge != null)
                dist += edge.LengthMeters;
            else
            {
                var nA = graph.Nodes[nodePath[i]];
                var nB = graph.Nodes[nodePath[i + 1]];
                dist += ComputeDistanceMeters(nA.Latitude, nA.Longitude, nB.Latitude, nB.Longitude);
            }
        }
        return dist;
    }

    private static (string Maneuver, string NextStreet) ExtractUpcomingManeuver(RoadGraph graph, List<long> nodePath, int segmentIdx)
    {
        if (segmentIdx >= nodePath.Count - 2)
        {
            return ("straight", "مقصد");
        }

        var n0 = graph.Nodes[nodePath[segmentIdx]];
        var n1 = graph.Nodes[nodePath[segmentIdx + 1]];
        var n2 = graph.Nodes[nodePath[segmentIdx + 2]];

        double b1 = ComputeBearing(n0.Latitude, n0.Longitude, n1.Latitude, n1.Longitude);
        double b2 = ComputeBearing(n1.Latitude, n1.Longitude, n2.Latitude, n2.Longitude);

        double delta = (b2 - b1 + 540) % 360 - 180;

        string maneuver = "straight";
        string instruction = "ادامه در مسیر";
        if (delta > 35)
        {
            maneuver = "right";
            instruction = "گردش به راست در تقاطع بعد";
        }
        else if (delta < -35)
        {
            maneuver = "left";
            instruction = "گردش به چپ در تقاطع بعد";
        }
        else if (delta > 130 || delta < -130)
        {
            maneuver = "uturn";
            instruction = "دور زدن در دوربرگردان";
        }

        return (maneuver, instruction);
    }

    private static double ComputeDistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371000.0;
        double dLat = (lat2 - lat1) * Math.PI / 180.0;
        double dLon = (lon2 - lon1) * Math.PI / 180.0;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                   Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0) *
                   Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return 2 * R * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double ComputeBearing(double lat1, double lon1, double lat2, double lon2)
    {
        double phi1 = lat1 * Math.PI / 180.0;
        double phi2 = lat2 * Math.PI / 180.0;
        double dLambda = (lon2 - lon1) * Math.PI / 180.0;

        double y = Math.Sin(dLambda) * Math.Cos(phi2);
        double x = Math.Cos(phi1) * Math.Sin(phi2) - Math.Sin(phi1) * Math.Cos(phi2) * Math.Cos(dLambda);

        double theta = Math.Atan2(y, x);
        return (theta * 180.0 / Math.PI + 360.0) % 360.0;
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
