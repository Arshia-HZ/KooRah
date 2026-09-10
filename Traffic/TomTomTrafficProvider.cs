using System.Globalization;
using System.Text.Json;
using KooRah.Core.Models;

namespace KooRah.Core.Traffic;

/// <summary>
/// Calls TomTom's Flow Segment Data endpoint for the coordinate nearest an edge's midpoint
/// and updates that edge's live speed. Free tier: 2,500 non-tile requests/day — plenty for
/// routing checks on one person's candidate edges in one city.
/// Docs: https://developer.tomtom.com/traffic-api/documentation/traffic-flow/flow-segment-data
/// </summary>
public sealed class TomTomTrafficProvider : ITrafficProvider
{
    private readonly string _apiKey;

    public TomTomTrafficProvider(string apiKey) => _apiKey = apiKey;

    public async Task UpdateEdgeSpeedAsync(GraphEdge edge, HttpClient httpClient, CancellationToken ct = default)
    {
        var speed = await GetCurrentSpeedKphAsync(edge.MidLatitude, edge.MidLongitude, httpClient, ct);
        if (speed.HasValue && speed.Value > 0)
            edge.CurrentSpeedKph = speed.Value;
        // If the call fails, we simply leave CurrentSpeedKph at whatever it was
        // (initially FreeFlowSpeedKph) rather than routing on a bad/zero value.
    }

    /// <summary>Actual TomTom call, factored out so you can call it once you have a lat/lon.</summary>
    public async Task<double?> GetCurrentSpeedKphAsync(double lat, double lon, HttpClient httpClient, CancellationToken ct = default)
    {
        var url = $"https://api.tomtom.com/traffic/services/4/flowSegmentData/absolute/10/json" +
                  $"?key={_apiKey}&point={lat.ToString(CultureInfo.InvariantCulture)},{lon.ToString(CultureInfo.InvariantCulture)}";

        using var response = await httpClient.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
            return null;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        // Response shape: { "flowSegmentData": { "currentSpeed": <kph>, "freeFlowSpeed": <kph>, ... } }
        if (doc.RootElement.TryGetProperty("flowSegmentData", out var flow) &&
            flow.TryGetProperty("currentSpeed", out var speedEl))
        {
            return speedEl.GetDouble();
        }

        return null;
    }
}
