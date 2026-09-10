# KooRah.Core

A minimal, on-device routing engine: OSM road graph + live traffic + three
routing algorithms (Dijkstra, A*, Bidirectional A*) that race each other.
No backend — everything here runs directly on your device.

## 1. Get the Tehran road data

1. Go to https://www.openstreetmap.org/export or https://extract.bbbike.org/
   and draw a bounding box around Tehran (or the neighborhoods you actually drive).
   Smaller box = smaller/faster graph. Export as `.osm.pbf`.
2. Drop the file somewhere your app can read it, e.g. `tehran.osm.pbf`.

(A full-country extract works too but is unnecessarily large for a one-city app —
stick to a Tehran bounding box.)

## 2. Build the graph once

```csharp
using KooRah.Core.Osm;

var graph = OsmGraphBuilder.BuildFromPbf("tehran.osm.pbf");
```

This takes a few seconds for a city-sized extract. You can serialize `graph`
to disk (e.g. with System.Text.Json, or a simple binary format) afterward so
you don't have to re-parse the .pbf every app launch — only rebuild it when
you re-download a fresher OSM extract (say, every few months).

## 3. Get a TomTom API key

Sign up free at https://developer.tomtom.com — no credit card needed for the
free tier (2,500 non-tile requests/day, comfortably enough for one person's
route checks in one city).

## 4. Update live traffic for the edges on your candidate routes

Don't refresh the whole city — just the edges near your start/end and along
routes you're about to evaluate:

```csharp
using KooRah.Core.Traffic;

var traffic = new TomTomTrafficProvider("YOUR_API_KEY");
using var http = new HttpClient();

// e.g. after finding a candidate path, refresh traffic on its edges before trusting the ETA:
foreach (var edge in candidateRouteEdges)
{
    await traffic.UpdateEdgeSpeedAsync(edge, http);
}
```

## 5. Find the best route

```csharp
using KooRah.Core.Routing;

var start = graph.FindNearestNode(35.7219, 51.3347); // your current lat/lon
var end   = graph.FindNearestNode(35.6997, 51.3380);  // destination lat/lon

var comparer = new RouteComparer();
var (best, all) = comparer.FindBestRoute(graph, start.Id, end.Id);

Console.WriteLine($"Winner: {best?.AlgorithmName}, {best?.TotalTravelTimeSeconds:F0}s, " +
                   $"{best?.TotalDistanceMeters / 1000:F1} km");

foreach (var r in all)
    Console.WriteLine($"  {r.AlgorithmName}: {r.ComputeTime.TotalMilliseconds:F1}ms compute");
```

On a single-city graph, all three algorithms should agree on total travel
time (they're finding the same shortest path); what differs is compute
time — bidirectional A* should usually win there once the graph gets
reasonably large.

## What's not built yet (next steps)

- **UI layer** (MAUI page: map view, tap-to-set destination, turn list).
- **Graph persistence** (save the built graph so you skip re-parsing the
  .pbf on every launch).
- **Background refresh** so traffic updates while you're actively navigating,
  not just at route-request time.
- **Turn-by-turn instructions** (currently you get a node path + total time,
  not street-by-street directions — that needs street names pulled from the
  OSM way tags, which the builder currently discards after computing speed).

Happy to build any of these next — the UI is probably the natural next step
once you've test-run this against a real Tehran extract.
