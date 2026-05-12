using System.Text.Json;
using AdefHelpDeskGraphExplorer.Models.Graph;

namespace AdefHelpDeskGraphExplorer.Services.Graph;

/// <summary>Loads graph.json and produces small, focused subgraphs for chat grounding.</summary>
public class GraphQueryService
{
    private readonly GraphCache _cache;
    private GraphDocument? _doc;
    private DateTime _loadedUtc;

    public GraphQueryService(GraphCache cache) => _cache = cache;

    private GraphDocument? Load()
    {
        if (!_cache.Exists) return null;
        var info = new FileInfo(_cache.GraphJsonPath);
        if (_doc != null && _loadedUtc >= info.LastWriteTimeUtc) return _doc;
        using var fs = File.OpenRead(_cache.GraphJsonPath);
        _doc = JsonSerializer.Deserialize<GraphDocument>(fs, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        _loadedUtc = info.LastWriteTimeUtc;
        return _doc;
    }

    public GraphDocument? Snapshot() => Load();

    /// <summary>
    /// Build a small JSON excerpt suitable for use as chat grounding context.
    /// Scope can be: null/empty => keyword search, "node:&lt;id&gt;" => 1-hop around a specific node.
    /// </summary>
    public string BuildContext(string? scope, string userPrompt, int maxNodes = 30)
    {
        var doc = Load();
        if (doc is null) return "{\"nodes\":[],\"edges\":[],\"note\":\"graph.json not built yet\"}";

        IEnumerable<GraphNode> seeds;

        if (!string.IsNullOrWhiteSpace(scope) && scope.StartsWith("node:", StringComparison.OrdinalIgnoreCase))
        {
            var id = scope[5..];
            seeds = doc.Nodes.Where(n => n.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            // Simple keyword filter against label + data values.
            var terms = (userPrompt ?? "")
                .Split(new[] { ' ', '\t', '\n', ',', '.', '?', '!' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length > 2)
                .Select(w => w.ToLowerInvariant())
                .Distinct()
                .ToList();
            seeds = terms.Count == 0
                ? doc.Nodes.Where(n => n.Type == "Task").Take(maxNodes / 2)
                : doc.Nodes.Where(n => Matches(n, terms));
        }

        var selected = new Dictionary<string, GraphNode>(StringComparer.Ordinal);
        foreach (var n in seeds.Take(maxNodes / 2)) selected[n.Id] = n;

        // Expand 1 hop
        var byId = doc.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var edges = new List<GraphEdge>();
        foreach (var e in doc.Edges)
        {
            var srcIn = selected.ContainsKey(e.Source);
            var dstIn = selected.ContainsKey(e.Target);
            if (!srcIn && !dstIn) continue;
            edges.Add(e);
            if (selected.Count < maxNodes)
            {
                if (!srcIn && byId.TryGetValue(e.Source, out var s)) selected[s.Id] = s;
                if (!dstIn && byId.TryGetValue(e.Target, out var d)) selected[d.Id] = d;
            }
        }

        var excerpt = new
        {
            nodes = selected.Values.Take(maxNodes),
            edges = edges.Where(e => selected.ContainsKey(e.Source) && selected.ContainsKey(e.Target))
        };

        return JsonSerializer.Serialize(excerpt, new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }

    private static bool Matches(GraphNode n, List<string> terms)
    {
        var label = (n.Label ?? "").ToLowerInvariant();
        if (terms.Any(t => label.Contains(t))) return true;
        foreach (var v in n.Data.Values)
        {
            if (v is null) continue;
            var s = v.ToString()?.ToLowerInvariant();
            if (string.IsNullOrEmpty(s)) continue;
            if (terms.Any(t => s!.Contains(t))) return true;
        }
        return false;
    }
}
