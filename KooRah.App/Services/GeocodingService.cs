using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace KooRah.App.Services;

public sealed record PlaceSearchResult(
    string Name,
    string DisplayAddress,
    double Latitude,
    double Longitude
);

public sealed class GeocodingService
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public GeocodingService()
    {
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("User-Agent", "KooRah-App/1.0 (Tehran Navigation Engine)");
        _http.Timeout = TimeSpan.FromSeconds(6);
    }

    /// <summary>
    /// Searches for places in Greater Tehran by name (Persian or English) or direct coordinates.
    /// </summary>
    public async Task<List<PlaceSearchResult>> SearchPlacesAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new List<PlaceSearchResult>();

        query = query.Trim();

        // 1. Check if user entered direct lat, lon coordinates (e.g. "35.6997, 51.3375")
        var coordMatch = Regex.Match(query, @"^([+-]?\d+(?:\.\d+)?)[,\s]+([+-]?\d+(?:\.\d+)?)$");
        if (coordMatch.Success &&
            double.TryParse(coordMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) &&
            double.TryParse(coordMatch.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
        {
            return new List<PlaceSearchResult>
            {
                new("Coordinates", $"{lat:F4}, {lon:F4}", lat, lon)
            };
        }

        try
        {
            // 2. Query Nominatim with viewbox restricted to Greater Tehran
            var url = $"https://nominatim.openstreetmap.org/search" +
                      $"?q={Uri.EscapeDataString(query)}" +
                      $"&format=json&limit=5&addressdetails=1&bounded=1" +
                      $"&viewbox=51.05,35.50,51.65,35.90";

            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
                return await FallbackSearchAsync(query, ct);

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var results = await JsonSerializer.DeserializeAsync<List<NominatimItem>>(stream, JsonOptions, ct);

            if (results == null || results.Count == 0)
                return await FallbackSearchAsync(query, ct);

            return results
                .Where(r => double.TryParse(r.Lat, NumberStyles.Float, CultureInfo.InvariantCulture, out _) &&
                            double.TryParse(r.Lon, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                .Select(FormatResult)
                .ToList();
        }
        catch
        {
            return new List<PlaceSearchResult>();
        }
    }

    private async Task<List<PlaceSearchResult>> FallbackSearchAsync(string query, CancellationToken ct)
    {
        try
        {
            var fallbackUrl = $"https://nominatim.openstreetmap.org/search" +
                              $"?q={Uri.EscapeDataString(query + " Tehran")}" +
                              $"&format=json&limit=4&addressdetails=1";

            using var response = await _http.GetAsync(fallbackUrl, ct);
            if (!response.IsSuccessStatusCode)
                return new List<PlaceSearchResult>();

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var results = await JsonSerializer.DeserializeAsync<List<NominatimItem>>(stream, JsonOptions, ct);

            return results?
                .Where(r => double.TryParse(r.Lat, NumberStyles.Float, CultureInfo.InvariantCulture, out _) &&
                            double.TryParse(r.Lon, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                .Select(FormatResult)
                .ToList() ?? new List<PlaceSearchResult>();
        }
        catch
        {
            return new List<PlaceSearchResult>();
        }
    }

    /// <summary>
    /// Performs reverse geocoding to find the street/landmark name for GPS coordinates.
    /// </summary>
    public async Task<string?> ReverseGeocodeAsync(double lat, double lon, CancellationToken ct = default)
    {
        try
        {
            var url = $"https://nominatim.openstreetmap.org/reverse" +
                      $"?lat={lat.ToString(CultureInfo.InvariantCulture)}" +
                      $"&lon={lon.ToString(CultureInfo.InvariantCulture)}" +
                      $"&format=json";

            using var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (doc.RootElement.TryGetProperty("name", out var nameProp) && !string.IsNullOrWhiteSpace(nameProp.GetString()))
            {
                return nameProp.GetString();
            }

            if (doc.RootElement.TryGetProperty("display_name", out var dispProp))
            {
                var full = dispProp.GetString();
                if (!string.IsNullOrWhiteSpace(full))
                {
                    var parts = full.Split(',');
                    return string.Join(", ", parts.Take(2)).Trim();
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static PlaceSearchResult FormatResult(NominatimItem item)
    {
        var lat = double.Parse(item.Lat, CultureInfo.InvariantCulture);
        var lon = double.Parse(item.Lon, CultureInfo.InvariantCulture);

        var primaryName = !string.IsNullOrWhiteSpace(item.Name)
            ? item.Name
            : item.DisplayName?.Split(',').FirstOrDefault()?.Trim() ?? "Location";

        string secondaryAddress = "";
        if (!string.IsNullOrWhiteSpace(item.DisplayName))
        {
            var parts = item.DisplayName.Split(',').Select(p => p.Trim()).ToList();
            if (parts.Count > 1)
            {
                // Take next 2 elements (e.g. neighborhood, district)
                secondaryAddress = string.Join(", ", parts.Skip(1).Take(2));
            }
        }

        return new PlaceSearchResult(primaryName, secondaryAddress, lat, lon);
    }

    private sealed class NominatimItem
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("display_name")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("lat")]
        public string Lat { get; set; } = "";

        [JsonPropertyName("lon")]
        public string Lon { get; set; } = "";
    }
}
