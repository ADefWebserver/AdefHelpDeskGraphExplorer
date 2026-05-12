namespace AdefHelpDeskGraphExplorer.Models.Graph;

public sealed class GraphNode
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public Dictionary<string, object?> Data { get; set; } = new();
}
