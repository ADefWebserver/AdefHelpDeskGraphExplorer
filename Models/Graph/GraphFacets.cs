namespace AdefHelpDeskGraphExplorer.Models.Graph;

/// <summary>
/// Distinct values present in the current graph used to populate filter
/// checkboxes. Computed from a loaded <see cref="GraphDocument"/>.
/// </summary>
public sealed record GraphFacets(
    IReadOnlyList<string> NodeTypes,
    IReadOnlyList<string> TaskStatuses,
    IReadOnlyList<string> DetailTypes)
{
    public static GraphFacets Empty { get; } = new(
        Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());

    public static GraphFacets From(GraphDocument doc)
    {
        if (doc is null) return Empty;

        var nodeTypes = doc.Nodes
            .Select(n => n.Type ?? string.Empty)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        var taskStatuses = doc.Nodes
            .Where(n => string.Equals(n.Type, "Task", StringComparison.Ordinal))
            .Select(n => ReadString(n.Data, "status"))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .Select(s => s!)
            .ToList();

        var detailTypes = doc.Nodes
            .Where(n => string.Equals(n.Type, "TaskDetail", StringComparison.Ordinal))
            .Select(n => ReadString(n.Data, "detailType"))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .Select(s => s!)
            .ToList();

        return new GraphFacets(nodeTypes, taskStatuses, detailTypes);
    }

    private static string? ReadString(IDictionary<string, object?>? data, string key)
    {
        if (data is null) return null;
        if (!data.TryGetValue(key, out var v) || v is null) return null;
        if (v is System.Text.Json.JsonElement el)
        {
            return el.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => el.GetString(),
                System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined => null,
                _ => el.ToString()
            };
        }
        return v.ToString();
    }
}
