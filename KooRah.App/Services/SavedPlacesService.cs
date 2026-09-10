using System.Text.Json;

namespace KooRah.App.Services;

public sealed class SavedPlacesService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _storagePath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private List<SavedPlace>? _cache;

    public event EventHandler? SavedPlacesChanged;

    public SavedPlacesService()
    {
        _storagePath = Path.Combine(FileSystem.AppDataDirectory, "saved_places.json");
    }

    public async Task<List<SavedPlace>> GetSavedPlacesAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (_cache != null)
                return new List<SavedPlace>(_cache);

            if (!File.Exists(_storagePath))
            {
                _cache = GetDefaultStarterPlaces();
                await PersistInternalAsync(_cache);
                return new List<SavedPlace>(_cache);
            }

            var json = await File.ReadAllTextAsync(_storagePath);
            var loaded = JsonSerializer.Deserialize<List<SavedPlace>>(json, JsonOptions);
            _cache = loaded ?? GetDefaultStarterPlaces();
            return new List<SavedPlace>(_cache);
        }
        catch
        {
            _cache = GetDefaultStarterPlaces();
            return new List<SavedPlace>(_cache);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<SavedPlace> SavePlaceAsync(string label, string address, double lat, double lon, string icon = "📍")
    {
        await _lock.WaitAsync();
        try
        {
            _cache ??= await LoadInternalAsync();

            // Check if place with exact same coordinates or label already exists; update it if so
            var existing = _cache.FirstOrDefault(p => 
                p.Label.Equals(label, StringComparison.OrdinalIgnoreCase) ||
                (Math.Abs(p.Latitude - lat) < 0.0003 && Math.Abs(p.Longitude - lon) < 0.0003));

            SavedPlace saved;
            if (existing != null)
            {
                _cache.Remove(existing);
                saved = existing with
                {
                    Label = label,
                    Address = address,
                    Latitude = lat,
                    Longitude = lon,
                    Icon = icon
                };
            }
            else
            {
                saved = new SavedPlace(
                    Id: Guid.NewGuid().ToString("N"),
                    Label: label,
                    Address: address,
                    Latitude: lat,
                    Longitude: lon,
                    Icon: string.IsNullOrWhiteSpace(icon) ? "📍" : icon,
                    CreatedAt: DateTime.UtcNow
                );
            }

            _cache.Insert(0, saved);
            await PersistInternalAsync(_cache);

            SavedPlacesChanged?.Invoke(this, EventArgs.Empty);
            return saved;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> DeletePlaceAsync(string id)
    {
        await _lock.WaitAsync();
        try
        {
            _cache ??= await LoadInternalAsync();
            var item = _cache.FirstOrDefault(p => p.Id == id);
            if (item != null)
            {
                _cache.Remove(item);
                await PersistInternalAsync(_cache);
                SavedPlacesChanged?.Invoke(this, EventArgs.Empty);
                return true;
            }
            return false;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> IsSavedAsync(double lat, double lon, double toleranceDegrees = 0.0005)
    {
        var places = await GetSavedPlacesAsync();
        return places.Any(p => Math.Abs(p.Latitude - lat) < toleranceDegrees && Math.Abs(p.Longitude - lon) < toleranceDegrees);
    }

    private async Task<List<SavedPlace>> LoadInternalAsync()
    {
        if (!File.Exists(_storagePath))
            return GetDefaultStarterPlaces();

        try
        {
            var json = await File.ReadAllTextAsync(_storagePath);
            return JsonSerializer.Deserialize<List<SavedPlace>>(json, JsonOptions) ?? GetDefaultStarterPlaces();
        }
        catch
        {
            return GetDefaultStarterPlaces();
        }
    }

    private async Task PersistInternalAsync(List<SavedPlace> places)
    {
        try
        {
            var json = JsonSerializer.Serialize(places, JsonOptions);
            await File.WriteAllTextAsync(_storagePath, json);
        }
        catch { /* Best effort local file write */ }
    }

    private static List<SavedPlace> GetDefaultStarterPlaces() => new()
    {
        new SavedPlace(
            Id: "starter-azadi",
            Label: "میدان آزادی",
            Address: "میدان آزادی، تهران",
            Latitude: 35.6997,
            Longitude: 51.3375,
            Icon: "⭐",
            CreatedAt: DateTime.UtcNow
        ),
        new SavedPlace(
            Id: "starter-milad",
            Label: "برج میلاد",
            Address: "بزرگراه همت، برج میلاد",
            Latitude: 35.7448,
            Longitude: 51.3753,
            Icon: "🗼",
            CreatedAt: DateTime.UtcNow
        ),
        new SavedPlace(
            Id: "starter-tajrish",
            Label: "میدان تجریش",
            Address: "میدان تجریش، شمیرانات",
            Latitude: 35.8055,
            Longitude: 51.4285,
            Icon: "🛍️",
            CreatedAt: DateTime.UtcNow
        )
    };
}
