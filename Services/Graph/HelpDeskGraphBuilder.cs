using System.Text.Json;
using AdefHelpDeskGraphExplorer.Data;
using AdefHelpDeskGraphExplorer.Models.Graph;
using AdefHelpDeskGraphExplorer.Models.HelpDesk;

namespace AdefHelpDeskGraphExplorer.Services.Graph;

public class HelpDeskGraphBuilder : IHelpDeskGraphBuilder
{
    private readonly HelpDeskRepository _repo;
    private readonly ILogger<HelpDeskGraphBuilder> _log;

    public HelpDeskGraphBuilder(HelpDeskRepository repo, ILogger<HelpDeskGraphBuilder> log)
    {
        _repo = repo;
        _log = log;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<GraphDocument> BuildAsync(IProgress<int>? progress, CancellationToken ct)
    {
        _log.LogInformation("Building help-desk graph");

        var tasks = await _repo.GetTasksAsync(since: null, ct);
        progress?.Report(20);
        var taskIds = tasks.Select(t => t.TaskID).ToList();

        var details = await _repo.GetTaskDetailsAsync(taskIds, ct);
        progress?.Report(40);

        var taskCats = await _repo.GetTaskCategoriesAsync(taskIds, ct);
        progress?.Report(55);

        var categories = await _repo.GetCategoriesAsync(ct);
        progress?.Report(70);

        var userIds = tasks.Where(t => t.RequesterUserID.HasValue).Select(t => t.RequesterUserID!.Value)
            .Concat(details.Where(d => d.UserID.HasValue).Select(d => d.UserID!.Value))
            .Distinct().ToList();
        var users = await _repo.GetUsersAsync(userIds, ct);
        progress?.Report(85);

        var doc = new GraphDocument
        {
            GeneratedUtc = DateTime.UtcNow
        };
        var nodeIds = new HashSet<string>(StringComparer.Ordinal);

        // Users
        foreach (var u in users)
        {
            var id = $"user:{u.UserID}";
            if (!nodeIds.Add(id)) continue;
            doc.Nodes.Add(new GraphNode
            {
                Id = id,
                Type = "User",
                Label = string.IsNullOrWhiteSpace($"{u.FirstName} {u.LastName}".Trim())
                    ? (u.Username ?? $"User {u.UserID}")
                    : $"{u.FirstName} {u.LastName}".Trim(),
                Data = new()
                {
                    ["userId"] = u.UserID,
                    ["username"] = u.Username,
                    ["email"] = u.Email,
                    ["isSuperUser"] = u.IsSuperUser
                }
            });
        }

        // Categories
        foreach (var c in categories)
        {
            var id = $"category:{c.CategoryID}";
            if (!nodeIds.Add(id)) continue;
            doc.Nodes.Add(new GraphNode
            {
                Id = id,
                Type = "Category",
                Label = c.CategoryName ?? $"Category {c.CategoryID}",
                Data = new()
                {
                    ["categoryId"] = c.CategoryID,
                    ["parentCategoryId"] = c.ParentCategoryID,
                    ["level"] = c.Level
                }
            });
        }

        // Tasks
        foreach (var t in tasks)
        {
            var id = $"task:{t.TaskID}";
            if (!nodeIds.Add(id)) continue;
            doc.Nodes.Add(new GraphNode
            {
                Id = id,
                Type = "Task",
                Label = Truncate(t.Description, 80) ?? $"Task {t.TaskID}",
                Data = new()
                {
                    ["taskId"] = t.TaskID,
                    ["status"] = t.Status,
                    ["priority"] = t.Priority,
                    ["createdUtc"] = t.CreatedDate,
                    ["dueUtc"] = t.DueDate,
                    ["requesterUserId"] = t.RequesterUserID,
                    ["requesterName"] = t.RequesterName,
                    ["assignedRoleId"] = t.AssignedRoleID,
                    ["description"] = t.Description
                }
            });
        }

        // TaskDetails / Comments
        foreach (var d in details)
        {
            var isComment = string.Equals(d.DetailType, "Comment", StringComparison.OrdinalIgnoreCase);
            var prefix = isComment ? "comment" : "detail";
            var id = $"{prefix}:{d.DetailID}";
            if (!nodeIds.Add(id)) continue;
            doc.Nodes.Add(new GraphNode
            {
                Id = id,
                Type = isComment ? "Comment" : "TaskDetail",
                Label = isComment ? "Comment" : (d.DetailType ?? "Detail"),
                Data = new()
                {
                    ["detailId"] = d.DetailID,
                    ["taskId"] = d.TaskID,
                    ["detailType"] = d.DetailType,
                    ["userId"] = d.UserID,
                    ["insertedUtc"] = d.InsertDate,
                    ["text"] = d.Description,
                    ["startTime"] = d.StartTime,
                    ["stopTime"] = d.StopTime
                }
            });
        }

        // Edges
        var edgeIdx = 0;
        string NextEdgeId() => $"e{++edgeIdx}";

        foreach (var t in tasks)
        {
            var taskNode = $"task:{t.TaskID}";
            if (t.RequesterUserID.HasValue)
            {
                var u = $"user:{t.RequesterUserID.Value}";
                if (nodeIds.Contains(u))
                    doc.Edges.Add(new GraphEdge { Id = NextEdgeId(), Source = taskNode, Target = u, Type = "REQUESTED_BY" });
            }
        }

        foreach (var tc in taskCats)
        {
            var taskNode = $"task:{tc.TaskID}";
            var catNode = $"category:{tc.CategoryID}";
            if (nodeIds.Contains(taskNode) && nodeIds.Contains(catNode))
                doc.Edges.Add(new GraphEdge { Id = NextEdgeId(), Source = taskNode, Target = catNode, Type = "IN_CATEGORY" });
        }

        foreach (var c in categories.Where(c => c.ParentCategoryID.HasValue))
        {
            var src = $"category:{c.CategoryID}";
            var dst = $"category:{c.ParentCategoryID!.Value}";
            if (nodeIds.Contains(src) && nodeIds.Contains(dst))
                doc.Edges.Add(new GraphEdge { Id = NextEdgeId(), Source = src, Target = dst, Type = "CHILD_OF" });
        }

        foreach (var d in details)
        {
            var isComment = string.Equals(d.DetailType, "Comment", StringComparison.OrdinalIgnoreCase);
            var prefix = isComment ? "comment" : "detail";
            var src = $"task:{d.TaskID}";
            var dst = $"{prefix}:{d.DetailID}";
            if (nodeIds.Contains(src) && nodeIds.Contains(dst))
                doc.Edges.Add(new GraphEdge
                {
                    Id = NextEdgeId(),
                    Source = src,
                    Target = dst,
                    Type = isComment ? "HAS_COMMENT" : "HAS_DETAIL"
                });

            if (d.UserID.HasValue)
            {
                var u = $"user:{d.UserID.Value}";
                if (nodeIds.Contains(u) && nodeIds.Contains(dst))
                    doc.Edges.Add(new GraphEdge { Id = NextEdgeId(), Source = u, Target = dst, Type = "AUTHORED" });
            }
        }

        progress?.Report(100);
        _log.LogInformation("Graph built: {NodeCount} nodes / {EdgeCount} edges", doc.Nodes.Count, doc.Edges.Count);
        return doc;
    }

    public async Task SaveAsync(GraphDocument doc, string outputDir, CancellationToken ct)
    {
        Directory.CreateDirectory(outputDir);
        var path = Path.Combine(outputDir, "graph.json");
        await using var fs = File.Create(path);
        await JsonSerializer.SerializeAsync(fs, doc, JsonOpts, ct);
        _log.LogInformation("Wrote {Path}", path);
    }

    private static string? Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s[..max] + "…");
}
