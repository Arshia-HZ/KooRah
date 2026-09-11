using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using KooRah.Core.Models;

namespace KooRah.Core.Traffic;

/// <summary>
/// Calls TomTom's Flow Segment Data endpoint for the coordinate nearest an edge's midpoint
/// and updates that edge's live speed. Free tier: 2,500 non-tile requests/day.
/// Note: TomTom Traffic Flow coverage does not currently include Iran/Tehran.
/// Docs: https://developer.tomtom.com/traffic-api/documentation/traffic-flow/flow-segment-data
/// </summary>
public sealed class TomTomTrafficProvider : ITrafficProvider
{
    private readonly string _apiKey;

    /// <summary>Whether TomTom service is active and available for the current route region.</summary>
    public bool IsAvailable { get; private set; } = true;

    /// <summary>Stores the last error message or coverage notice.</summary>
    public string? LastError { get; private set; }

    /// <summary>Number of successful traffic speed updates made.</summary>
    public int SuccessfulUpdatesCount { get; private set; }

    public TomTomTrafficProvider(string apiKey)
    {
        _apiKey = apiKey;
        if (string.IsNullOrWhiteSpace(_apiKey) || _apiKey == "YOUR_API_KEY")
        {
            IsAvailable = false;
            LastError = "API key not configured";
        }
    }

    public async Task UpdateEdgeSpeedAsync(GraphEdge edge, HttpClient httpClient, CancellationToken ct = default)
    {
        if (!IsAvailable) return;

        // Fast-fail if coordinates fall in Iran bounding box (Lat 24.0-40.0, Lon 44.0-64.0)
        // TomTom officially lacks traffic flow coverage in Iran and returns HTTP 400 "Point too far from nearest existing segment"
        if (IsInIran(edge.MidLatitude, edge.MidLongitude))
        {
            IsAvailable = false;
            LastError = "TomTom Traffic API does not provide coverage for Iran (returns 400 'Point too far from nearest existing segment').";
            Debug.WriteLine($"[TomTomTraffic] {LastError} Skipping API calls to avoid 400 errors.");
            return;
        }

        var speed = await GetCurrentSpeedKphAsync(edge.MidLatitude, edge.MidLongitude, httpClient, ct);
        if (speed.HasValue && speed.Value > 0)
        {
            edge.CurrentSpeedKph = speed.Value;
            SuccessfulUpdatesCount++;
        }
    }

    /// <summary>Actual TomTom call, factored out so you can call it once you have a lat/lon.</summary>
    public async Task<double?> GetCurrentSpeedKphAsync(double lat, double lon, HttpClient httpClient, CancellationToken ct = default)
    {
        if (!IsAvailable) return null;

        if (IsInIran(lat, lon))
        {
            IsAvailable = false;
            LastError = "TomTom Traffic API does not provide coverage for Iran.";
            return null;
        }

        var url = $"https://api.tomtom.com/traffic/services/4/flowSegmentData/absolute/10/json" +
                  $"?key={_apiKey}&point={lat.ToString(CultureInfo.InvariantCulture)},{lon.ToString(CultureInfo.InvariantCulture)}";

        try
        {
            using var response = await httpClient.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                var statusCode = (int)response.StatusCode;
                var errContent = await response.Content.ReadAsStringAsync(ct);
                LastError = $"HTTP {statusCode}: {errContent}";
                Debug.WriteLine($"[TomTomTraffic] Request failed ({statusCode}): {errContent}");

                // Circuit breaker: If 4xx client error (e.g. 400 point out of coverage, 403 unauthorized, 429 quota),
                // stop firing subsequent calls for the rest of the route to protect quota and dashboard stats.
                if (statusCode is >= 400 and < 500)
                {
                    IsAvailable = false;
                }

                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            // Response shape: { "flowSegmentData": { "currentSpeed": <kph>, "freeFlowSpeed": <kph>, ... } }
            if (doc.RootElement.TryGetProperty("flowSegmentData", out var flow) &&
                flow.TryGetProperty("currentSpeed", out var speedEl))
            {
                return speedEl.GetDouble();
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Debug.WriteLine($"[TomTomTraffic] Exception: {ex.Message}");
        }

        return null;
    }

    private static bool IsInIran(double lat, double lon) =>
        lat >= 24.0 && lat <= 40.0 && lon >= 44.0 && lon <= 64.0;
}
