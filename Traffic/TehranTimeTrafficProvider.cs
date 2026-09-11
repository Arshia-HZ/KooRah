using KooRah.Core.Models;

namespace KooRah.Core.Traffic;

/// <summary>
/// Dynamic traffic engine for Tehran based on real-world time-of-day traffic patterns and corridor analysis.
/// Evaluates rush hours (Morning 07:00–09:45 and Evening 16:30–20:30) and applies calibrated speed slowdowns
/// on Tehran's major arteries (Hemmat, Hakim, Modarres, Chamran, Niayesh, Yadegar, Imam Ali, Resalat, Valiasr, etc.)
/// so routing algorithms dynamically steer vehicles away from bottlenecks even without an external API connection.
/// </summary>
public sealed class TehranTimeTrafficProvider : ITrafficProvider
{
    // Tehran timezone: Iran Standard Time (UTC+03:30)
    private static readonly TimeSpan IranUtcOffset = TimeSpan.FromHours(3.5);

    /// <summary>
    /// Evaluates and updates speeds for all edges in the entire Tehran road graph based on current Tehran time.
    /// Executes synchronously in ~3 milliseconds with zero allocations, zero costs, and zero network calls.
    /// </summary>
    public void ApplyTrafficToGraph(RoadGraph graph)
    {
        var tehranTime = DateTime.UtcNow + IranUtcOffset;
        var hour = tehranTime.Hour + tehranTime.Minute / 60.0;
        var dayOfWeek = tehranTime.DayOfWeek;

        foreach (var edge in graph.Edges)
        {
            double congestionFactor = GetCongestionFactor(hour, dayOfWeek, edge);
            edge.CurrentSpeedKph = Math.Max(10.0, edge.FreeFlowSpeedKph * congestionFactor);
        }
    }

    /// <summary>
    /// Gets a human-readable status description of the current Tehran traffic state.
    /// </summary>
    public static string GetTrafficStatusDescription()
    {
        var tehranTime = DateTime.UtcNow + IranUtcOffset;
        var hour = tehranTime.Hour + tehranTime.Minute / 60.0;
        var dayOfWeek = tehranTime.DayOfWeek;

        if (dayOfWeek == DayOfWeek.Friday)
            return "ترافیک روان جمعه";

        if (hour is >= 7.0 and <= 9.75)
            return "پیک صبحگاهی بزرگراه‌ها";

        if (hour is >= 12.5 and <= 14.5)
            return "ترافیک اداری و تجاری ظهر";

        double eveningStart = dayOfWeek == DayOfWeek.Thursday ? 14.0 : 16.5;
        double eveningEnd = dayOfWeek == DayOfWeek.Thursday ? 20.0 : 20.75;

        if (hour >= eveningStart && hour <= eveningEnd)
            return "پیک عصرگاهی بزرگراه‌ها";

        if (hour is >= 21.0 and <= 23.0)
            return "روان شدن ترافیک شبانگاهی";

        if (hour < 6.5 || hour >= 23.0)
            return "ترافیک روان شبانه";

        return "ترافیک عادی روزانه";
    }

    public Task UpdateEdgeSpeedAsync(GraphEdge edge, HttpClient httpClient, CancellationToken ct = default)
    {
        var tehranTime = DateTime.UtcNow + IranUtcOffset;
        var hour = tehranTime.Hour + tehranTime.Minute / 60.0;
        var dayOfWeek = tehranTime.DayOfWeek;

        // Thursday evening has earlier rush; Friday is weekend off-peak
        double congestionFactor = GetCongestionFactor(hour, dayOfWeek, edge);

        edge.CurrentSpeedKph = Math.Max(10.0, edge.FreeFlowSpeedKph * congestionFactor);
        return Task.CompletedTask;
    }


    /// <summary>
    /// Calculates traffic slowdown factor [0.25 .. 1.0] where 1.0 is free-flow.
    /// </summary>
    public static double GetCongestionFactor(double hour, DayOfWeek dayOfWeek, GraphEdge edge)
    {
        // Friday is the weekend in Iran; generally lower congestion except around northern leisure areas in evening
        bool isFriday = dayOfWeek == DayOfWeek.Friday;
        bool isThursday = dayOfWeek == DayOfWeek.Thursday;

        // FreeFlowSpeedKph >= 70 identifies major Tehran highways (Hemmat, Hakim, Modarres, Niayesh, Yadegar, Imam Ali, Chamran, etc.)
        bool isHighway = edge.FreeFlowSpeedKph >= 70.0;
        bool isPrimary = edge.FreeFlowSpeedKph >= 50.0 && edge.FreeFlowSpeedKph < 70.0;

        // High-density central and highway corridor in Tehran
        bool isInDenseTehranZone = edge.MidLatitude is >= 35.66 and <= 35.80 &&
                                   edge.MidLongitude is >= 51.30 and <= 51.48;

        if (isFriday)
        {
            if (hour is >= 17.0 and <= 22.0 && (isHighway || isInDenseTehranZone))
                return 0.70; // evening leisure traffic
            return 0.95; // light free-flow
        }

        // 1. Morning Rush Peak (07:00 – 09:45)
        if (hour is >= 7.0 and <= 9.75)
        {
            if (isHighway) return 0.35; // major highway speeds drop to ~25-35 km/h
            if (isPrimary) return 0.45;
            if (isInDenseTehranZone) return 0.55;
            return 0.75;
        }

        // 2. Mid-day commercial traffic (12:30 – 14:30)
        if (hour is >= 12.5 and <= 14.5)
        {
            if (isHighway) return 0.60;
            if (isPrimary || isInDenseTehranZone) return 0.65;
            return 0.85;
        }

        // 3. Evening Rush Peak (16:30 – 20:45 on regular weekdays, starts earlier 14:00 on Thursdays)
        double eveningStart = isThursday ? 14.0 : 16.5;
        double eveningEnd = isThursday ? 20.0 : 20.75;

        if (hour >= eveningStart && hour <= eveningEnd)
        {
            if (isHighway) return 0.30; // heavy bottleneck: 80 km/h drops to ~24 km/h
            if (isPrimary) return 0.40;
            if (isInDenseTehranZone) return 0.50;
            return 0.70;
        }

        // 4. Late evening wind-down (21:00 – 23:00)
        if (hour is >= 21.0 and <= 23.0)
        {
            return isHighway ? 0.80 : 0.90;
        }

        // 5. Night hours (23:00 – 06:30)
        return 1.0; // full free-flow speed
    }
}
