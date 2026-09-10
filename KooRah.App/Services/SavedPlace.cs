namespace KooRah.App.Services;

public sealed record SavedPlace(
    string Id,
    string Label,
    string Address,
    double Latitude,
    double Longitude,
    string Icon = "📍",
    DateTime CreatedAt = default
)
{
    public string DisplaySubtitle => !string.IsNullOrWhiteSpace(Address) 
        ? Address 
        : $"{Latitude:F4}, {Longitude:F4}";

    public string FormattedChipText => $"{Icon} {Label}";
}
