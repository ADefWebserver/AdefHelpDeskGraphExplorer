namespace AdefHelpDeskGraphExplorer.Models.Graph;

public sealed class GraphDocument
{
    public int Version { get; set; } = 1;
    public DateTime GeneratedUtc { get; set; } = DateTime.UtcNow;
    public List<GraphNode> Nodes { get; set; } = new();
    public List<GraphEdge> Edges { get; set; } = new();
}
