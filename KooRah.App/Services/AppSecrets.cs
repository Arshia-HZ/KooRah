using System.Text.Json;

namespace KooRah.App.Services;

public static class AppSecrets
{
    private const string DefaultPlaceholder = "YOUR_API_KEY";
    private static string? _cachedApiKey;

    public static async Task<string> GetTomTomApiKeyAsync()
    {
        if (!string.IsNullOrEmpty(_cachedApiKey))
            return _cachedApiKey;

        // 1. Check environment variable (e.g. CI or system variable)
        var envKey = Environment.GetEnvironmentVariable("TOMTOM_API_KEY");
        if (!string.IsNullOrWhiteSpace(envKey) && envKey != DefaultPlaceholder)
        {
            _cachedApiKey = envKey.Trim();
            return _cachedApiKey;
        }

        // 2. Check local file paths
        var candidatePaths = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "secrets.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "secrets.json"),
            Path.Combine(FileSystem.AppDataDirectory, "secrets.json"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "secrets.json")
        };

        foreach (var path in candidatePaths)
        {
            try
            {
                if (File.Exists(path))
                {
                    var text = await File.ReadAllTextAsync(path);
                    var key = ExtractKey(text);
                    if (!string.IsNullOrWhiteSpace(key) && key != DefaultPlaceholder)
                    {
                        _cachedApiKey = key;
                        return _cachedApiKey;
                    }
                }
            }
            catch { }
        }

        // 3. Check bundled MauiAsset (if packaged on mobile)
        try
        {
            using var stream = await FileSystem.OpenAppPackageFileAsync("secrets.json");
            using var reader = new StreamReader(stream);
            var text = await reader.ReadToEndAsync();
            var key = ExtractKey(text);
            if (!string.IsNullOrWhiteSpace(key) && key != DefaultPlaceholder)
            {
                _cachedApiKey = key;
                return _cachedApiKey;
            }
        }
        catch { }

        _cachedApiKey = DefaultPlaceholder;
        return _cachedApiKey;
    }

    private static string? ExtractKey(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("TomTomApiKey", out var prop))
                return prop.GetString()?.Trim();
            if (doc.RootElement.TryGetProperty("tomTomApiKey", out var prop2))
                return prop2.GetString()?.Trim();
        }
        catch { }

        return null;
    }
}

