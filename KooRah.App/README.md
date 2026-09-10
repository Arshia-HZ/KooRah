# KooRah.App

MAUI shell showing your computed route on an OpenStreetMap/Leaflet map inside
a WebView — no Google Maps dependency, no API key needed for the map itself
(only TomTom, for live traffic, which you already have).

## One-time setup

1. **Install the MAUI workload** if you haven't already:
   ```
   dotnet workload install maui
   ```

2. **Add this project to your existing solution** alongside `KooRah.Core` and
   `KooRah.ConsoleTest`:
   ```
   dotnet sln add KooRah.App/KooRah.App.csproj
   ```

3. **Copy your `tehran.osm.pbf`** into `KooRah.App/Resources/Raw/tehran.osm.pbf`
   (same file your console test already uses — MAUI needs its own copy bundled
   as an asset so the app can ship with it).

4. **Set your TomTom key.** For now it's a literal in `MainPage.xaml.cs`
   (`TomTomApiKey` constant) — move it to `Preferences` or a git-ignored
   config file when you get a chance, per the earlier note.

5. **Build/run** for your platform, e.g.:
   ```
   dotnet build -t:Run -f net10.0-android
   ```

## What this first version does

- On launch: copies the bundled `.pbf` to app storage (once) and builds the
  road graph in the background, same as the console test.
- Tap **Find Route**: runs the same start→end coordinates as your console
  test, refreshes live traffic on the candidate route, races all three
  algorithms, and draws the winning route as a blue polyline on the map with
  start/destination pins.
- The status label shows the winning algorithm, ETA, distance, and each
  algorithm's compute time — the same numbers your console test printed.

## Natural next steps

- **Tap-to-pick destination** instead of hardcoded coordinates (Leaflet
  already supports map click events — you'd wire a JS→C# bridge via
  `WebView.Navigating` interception or `HybridWebView` in newer MAUI).
- **Turn-by-turn list** below the map (needs street names — currently
  discarded in `OsmGraphBuilder`, easy to add back from the way's `name` tag).
- **Auto-refresh while navigating**, re-drawing the polyline as traffic
  updates rather than only on button tap.
- **Persist the built graph** so it doesn't reparse the `.pbf` on every cold
  start (worth doing once the graph load time starts to bug you).
