using KooRah.Core.Models;

namespace KooRah.Core.Traffic;

public interface ITrafficProvider
{
    /// <summary>
    /// Fetches current speed for the road segment nearest the given edge's midpoint and
    /// writes it into edge.CurrentSpeedKph. Call this only for edges you're about to route
    /// through — not your whole city graph — to stay well within free API quotas.
    /// </summary>
    Task UpdateEdgeSpeedAsync(GraphEdge edge, HttpClient httpClient, CancellationToken ct = default);
}
