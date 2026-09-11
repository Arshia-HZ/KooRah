# KooRah (کوراه) 🧭🚗

[![Download APK](https://img.shields.io/github/v/release/Arshia-HZ/KooRah?label=Download%20APK&color=0D9488&logo=android)](https://github.com/Arshia-HZ/KooRah/releases)
[![Build & Release](https://github.com/Arshia-HZ/KooRah/actions/workflows/release-apk.yml/badge.svg)](https://github.com/Arshia-HZ/KooRah/actions/workflows/release-apk.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

**KooRah** is a high-performance, completely on-device navigation and routing application tailored for **Tehran, Iran**. Built with **.NET 10** and **.NET MAUI**, it features an offline OpenStreetMap road graph parser, three racing pathfinding algorithms, an empirical rush-hour traffic engine, and an interactive 3D vector map.

Everything runs **directly on your device** — no paid APIs, no subscriptions, no backend server, and zero external tracking.

> **📱 Install on Android:** Download the latest ready-to-install `KooRah-*.apk` directly from [GitHub Releases](https://github.com/Arshia-HZ/KooRah/releases).

---

## 🌟 Key Features

- **⚡ 100% On-Device Routing**:
  - Parses OpenStreetMap PBF files (`tehran.osm.pbf`) into an in-memory directed graph of **~200,000 intersections** and **~340,000 road segments**.
  - Fast nearest-node spatial lookups using Haversine distance.
- **🏁 3 Racing Pathfinding Algorithms**:
  - Simultaneously runs **Dijkstra**, **A\***, and **Bidirectional A\*** on the road graph.
  - Automatically selects the winner with sub-millisecond to few-millisecond compute times.
- **🟢 100% Free Tehran Dynamic Traffic Engine (`TehranTimeTrafficProvider`)**:
  - Calibrated rush-hour traffic model for Tehran's major expressways (Hemmat, Hakim, Modarres, Chamran, Niayesh, Yadegar-e-Emam, Imam Ali, Resalat, Valiasr, etc.).
  - Evaluates morning rush (07:00–09:45), midday commercial traffic (12:30–14:30), and evening peak (16:30–20:45), with tailored schedules for Thursdays and Friday weekends.
  - Dynamically weights edge travel times (`TravelTimeSeconds`), steering routes away from congested bottlenecks.
  - **Zero Cost Guarantee**: Completely free forever, zero API keys needed, works 100% offline, and causes zero network errors.
- **🗺️ Interactive 3D Vector Map (MapLibre GL)**:
  - Hardware-accelerated 3D vector map rendered via local WebView.
  - Day / Light clean aesthetic optimized for daylight navigation.
  - Dynamic camera transitions: pitch and bearing tilt for real 3D driving view.
  - **Tap-to-Pick Destination**: Click anywhere on the map to set origin or destination with instant Persian reverse geocoding.
- **📱 Clean Modern Persian UI (RTL)**:
  - Native Right-to-Left (RTL) layout with unmirrored navigation cards and proper Persian typography.
  - Search destination with debounced autocomplete suggestions.
  - **Saved Places**: Bookmark favorite locations (Home, Work, Gym, Cafe, etc.) with custom category icons.
  - Expandable bottom card with routing metrics (ETA, distance, algorithm details).
  - Real-time GPS location detection and turn-follow driving camera.

---

## 🏗️ Architecture & Project Structure

The repository is organized into a clean multi-project solution:

```text
├── KooRah.Core/                  # Core routing & navigation engine (.NET 10.0)
│   ├── Models/                   # GraphNode, GraphEdge, RoadGraph, RouteResult
│   ├── Osm/                      # OsmGraphBuilder (OsmSharp PBF reader & parser)
│   ├── Routing/                  # Dijkstra, AStar, BidirectionalAStar, RouteComparer
│   └── Traffic/                  # TehranTimeTrafficProvider, TomTomTrafficProvider, ITrafficProvider
│
├── KooRah.App/                   # Cross-platform .NET MAUI Client
│   ├── Resources/Raw/            # Local MapLibre GL template, styling & icons
│   ├── Services/                 # GeocodingService, SavedPlacesService, AppSecrets
│   ├── MainPage.xaml             # Modern Persian RTL navigation view
│   └── MainPage.xaml.cs          # UI state machine & driving navigation controller
│
├── KooRah.ConsoleTest/           # Benchmarking & algorithm testing CLI
│   └── Program.cs                # Graph loading & algorithm race harness
│
├── tehran.osm.pbf                # Local OpenStreetMap extract of Tehran
├── secrets.example.json          # Template for optional API credentials
└── README.md
```

---

## 🚀 Quick Start

### 1. Prerequisites
- [.NET 10 SDK](https://dotnet.microsoft.com/download) (or .NET 9/10 SDK with MAUI workload).
- .NET MAUI Workload:
  ```bash
  dotnet workload install maui
  ```
- Android SDK (for Android deployment) or Windows 10/11 developer mode enabled (for Windows desktop app).

### 2. Clone the Repository
```bash
git clone https://github.com/YOUR_USERNAME/KooRah.git
cd KooRah
```

### 3. Run the Console Benchmark Test
To verify OSM graph parsing and algorithm performance in your terminal:
```bash
dotnet run --project KooRah.ConsoleTest
```
Example output:
```text
=== KooRah Console Test ===
Building graph from 'tehran.osm.pbf'...
Graph built: 199,002 nodes, 338,794 edges.

Finding candidate route (Azadi to destination)...
Found initial route with 145 edges.

Evaluating 100% Free Tehran Dynamic Traffic Model...
Current Tehran Traffic Status: پیک عصرگاهی بزرگراه‌ها

Evaluating routes across racing algorithms (Dijkstra, A*, Bidirectional A*)...
Winner: Bidirectional A*, 322s, 4.7 km
  Dijkstra: 18.3ms compute
  A*: 7.4ms compute
  Bidirectional A*: 1.8ms compute

Done!
```

### 4. Run the Mobile / Desktop App
- **Windows Desktop:**
  ```bash
  dotnet build -t:Run -f net10.0-windows10.0.19041.0 KooRah.App/KooRah.App.csproj
  ```
- **Android Device / Emulator:**
  ```bash
  dotnet build -t:Run -f net10.0-android KooRah.App/KooRah.App.csproj
  ```
- Or simply open the solution (`KooRah.slnx`) in **Visual Studio 2022+** or **VS Code** (with C# Dev Kit & .NET MAUI extension) and press **F5**.

---

## 🗺️ Using Your Own City / OSM Map Data

While pre-configured for Tehran, you can use KooRah for **any city in the world**:

1. Download an `.osm.pbf` extract of your target region from [Geofabrik](https://download.geofabrik.de/) or export a custom bounding box via [BBBike](https://extract.bbbike.org/).
2. Place the `.osm.pbf` file in the project root and in `KooRah.App/Resources/Raw/`.
3. Build and launch — KooRah will automatically construct the road graph and enable navigation!

---

## ⚙️ Optional API Configuration

KooRah is **100% functional without any external keys**.

If you wish to test international traffic providers (e.g. TomTom in European or North American cities):
1. Copy `secrets.example.json` to `secrets.json`:
   ```bash
   cp secrets.example.json secrets.json
   ```
2. Insert your TomTom API key:
   ```json
   {
     "TomTomApiKey": "YOUR_TOMTOM_KEY_HERE"
   }
   ```
*(Note: `secrets.json` is ignored by git to protect private credentials).*

---

## 📄 License

This project is licensed under the **MIT License** - see the [LICENSE](LICENSE) file for details.

---

## 🤝 Contributing

Contributions, issues, and feature requests are welcome!
Feel free to check the [issues page](https://github.com/YOUR_USERNAME/KooRah/issues).
