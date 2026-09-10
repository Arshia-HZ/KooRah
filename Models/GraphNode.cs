namespace KooRah.Core.Models;

/// <summary>
/// An intersection or shape point in the road network.
/// </summary>
public sealed class GraphNode
{
    public long Id { get; init; }
    public double Latitude { get; init; }
    public double Longitude { get; init; }

    /// <summary>Outgoing edges from this node. Populated after graph build.</summary>
    public List<GraphEdge> OutgoingEdges { get; } = new();

    /// <summary>Incoming edges to this node — needed for the backward search in bidirectional A*.</summary>
    public List<GraphEdge> IncomingEdges { get; } = new();
}
