using System.Text.Json;
using AdefHelpDeskGraphExplorer.Models.Graph;
using AdefHelpDeskGraphExplorer.Services.Graph;

namespace AdefHelpDeskGraphExplorer.Services.AI.GraphTools;

/// <summary>
/// Read-only implementation of <see cref="IGraphChatTools"/> against the cached graph document.
/// </summary>
public sealed class GraphChatTools : IGraphChatTools
{
    public const int HardMaxCap = 100;

    private readonly GraphQueryService _query;
    private readonly ILogger<GraphChatTools> _log;

    public GraphChatTools(GraphQueryService query, ILogger<GraphChatTools> log)
    {
        _query = query;
        _log = log;
    }

    private GraphDocument? Doc() => _query.Snapshot();

    private static int Clamp(int max, int defaultMax) =>
        max <= 0 ? defaultMax : Math.Min(max, HardMaxCap);

    public NodeSummary[] SearchNodes(string query, string? type, int max)
    {
        var doc = Doc();
        if (doc is null) return Array.Empty<NodeSummary>();
        var cap = Clamp(max, 20);
        var terms = (query ?? string.Empty)
            .Split(new[] { ' ', '\t', '\n', ',', '.', '?', '!' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 1)
            .Select(w => w.ToLowerInvariant())
            .Distinct()
            .ToList();

        IEnumerable<GraphNode> nodes = doc.Nodes;
        if (!string.IsNullOrWhiteSpace(type))
            nodes = nodes.Where(n => string.Equals(n.Type, type, StringComparison.OrdinalIgnoreCase));

        if (terms.Count > 0)
            nodes = nodes.Where(n => MatchesAll(n, terms));

        return nodes.Take(cap).Select(ToSummary).ToArray();
    }

    public NodeDetail? GetNode(string id)
    {
        var doc = Doc();
        if (doc is null || string.IsNullOrWhiteSpace(id)) return null;
        var node = doc.Nodes.FirstOrDefault(n => string.Equals(n.Id, id, StringComparison.OrdinalIgnoreCase));
        if (node is null) return null;

        var byId = doc.Nodes.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
        var edgeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int outCount = 0, inCount = 0;
        var preview = new List<Neighbor>(capacity: 12);

        foreach (var e in doc.Edges)
        {
            var isOut = string.Equals(e.Source, id, StringComparison.OrdinalIgnoreCase);
            var isIn = !isOut && string.Equals(e.Target, id, StringComparison.OrdinalIgnoreCase);
            if (!isOut && !isIn) continue;

            if (isOut) outCount++; else inCount++;
            edgeCounts[e.Type] = edgeCounts.TryGetValue(e.Type, out var c) ? c + 1 : 1;

            if (preview.Count < 12)
            {
                var otherId = isOut ? e.Target : e.Source;
                if (byId.TryGetValue(otherId, out var other))
                    preview.Add(BuildNeighbor(e.Id, e.Type, isOut ? "out" : "in", other));
            }
        }

        return new NodeDetail(node.Id, node.Type, node.Label, node.Data,
            outCount, inCount, edgeCounts, preview.ToArray());
    }

    public Neighbor[] GetNeighbors(string id, string? edgeType, int max)
    {
        var doc = Doc();
        if (doc is null || string.IsNullOrWhiteSpace(id)) return Array.Empty<Neighbor>();
        var cap = Clamp(max, 25);
        var byId = doc.Nodes.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
        var results = new List<Neighbor>();

        foreach (var e in doc.Edges)
        {
            if (!string.IsNullOrWhiteSpace(edgeType)
                && !string.Equals(e.Type, edgeType, StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.Equals(e.Source, id, StringComparison.OrdinalIgnoreCase)
                && byId.TryGetValue(e.Target, out var t))
            {
                results.Add(BuildNeighbor(e.Id, e.Type, "out", t));
            }
            else if (string.Equals(e.Target, id, StringComparison.OrdinalIgnoreCase)
                     && byId.TryGetValue(e.Source, out var s))
            {
                results.Add(BuildNeighbor(e.Id, e.Type, "in", s));
            }

            if (results.Count >= cap) break;
        }
        return results.ToArray();
    }

    private static Neighbor BuildNeighbor(string edgeId, string edgeType, string direction, GraphNode other)
    {
        string? status = null, priority = null, snippet = null;
        switch (other.Type)
        {
            case "Task":
                status = GetString(other, "status");
                priority = GetString(other, "priority");
                snippet = Trim(GetString(other, "description"), 120);
                break;
            case "TaskDetail":
            case "Comment":
                snippet = Trim(GetString(other, "text"), 120);
                break;
            case "User":
                snippet = GetString(other, "email") ?? GetString(other, "username");
                break;
        }
        return new Neighbor(edgeId, edgeType, direction, other.Id, other.Type, other.Label,
            status, priority, snippet);
    }

    public TaskSummary[] ListTasksForUser(int userId, string? role, string? status, int max)
    {
        var doc = Doc();
        if (doc is null) return Array.Empty<TaskSummary>();
        var cap = Clamp(max, 25);

        var taskIds = TaskIdsForUser(doc, userId, role);
        if (taskIds.Count == 0) return Array.Empty<TaskSummary>();

        IEnumerable<GraphNode> tasks = doc.Nodes.Where(n => n.Type == "Task" && taskIds.Contains(n.Id));
        if (!string.IsNullOrWhiteSpace(status))
            tasks = tasks.Where(n => string.Equals(GetString(n, "status"), status, StringComparison.OrdinalIgnoreCase));

        var ctx = TaskEnrichmentContext.Build(doc);
        return tasks.Take(cap).Select(n => ToTaskSummary(n, ctx)).ToArray();
    }

    public int CountTasksForUser(int userId, string? role)
    {
        var doc = Doc();
        if (doc is null) return 0;
        return TaskIdsForUser(doc, userId, role).Count;
    }

    public UserActivity? GetUserActivity(int userId, int maxIdsPerList = 100)
    {
        var doc = Doc();
        if (doc is null) return null;
        var userNode = doc.Nodes.FirstOrDefault(n => n.Type == "User"
            && string.Equals(n.Id, $"user:{userId}", StringComparison.OrdinalIgnoreCase));
        if (userNode is null) return null;

        var requested = TaskIdsForUser(doc, userId, "requested");
        var assigned = TaskIdsForUser(doc, userId, "assigned");
        var commented = TaskIdsForUser(doc, userId, "commented");

        // "Worked on" = union of requester/assignee/commenter. We treat requester
        // and assignee as effectively the same set today (see HelpDeskGraphBuilder
        // — the legacy schema collapses them), but include both for forward-compat.
        var workedOn = new HashSet<string>(requested, StringComparer.OrdinalIgnoreCase);
        foreach (var t in assigned) workedOn.Add(t);
        foreach (var t in commented) workedOn.Add(t);

        // Tasks the user commented on but did NOT request — useful signal.
        var commentedOnly = new HashSet<string>(commented, StringComparer.OrdinalIgnoreCase);
        foreach (var t in requested) commentedOnly.Remove(t);
        foreach (var t in assigned) commentedOnly.Remove(t);

        var cap = Math.Max(1, Math.Min(maxIdsPerList, 500));

        // Count comments authored by this user (via AUTHORED edges).
        var authoredCount = doc.Edges.Count(e => e.Type == "AUTHORED"
            && string.Equals(e.Source, userNode.Id, StringComparison.OrdinalIgnoreCase));

        var taskById = doc.Nodes
            .Where(n => n.Type == "Task")
            .ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);

        TaskRef ToRef(string id)
        {
            if (taskById.TryGetValue(id, out var node))
                return new TaskRef(
                    node.Id,
                    node.Label ?? id,
                    GetString(node, "status"),
                    GetString(node, "priority"),
                    GetDateTime(node, "createdUtc"));
            return new TaskRef(id, id, null);
        }

        bool? isSuper = null;
        if (userNode.Data.TryGetValue("isSuperUser", out var su))
        {
            if (su is bool b) isSuper = b;
            else if (su is JsonElement el && (el.ValueKind == JsonValueKind.True || el.ValueKind == JsonValueKind.False))
                isSuper = el.GetBoolean();
        }

        return new UserActivity(
            userNode.Id,
            userId,
            userNode.Label,
            GetString(userNode, "username"),
            GetString(userNode, "email"),
            isSuper,
            requested.Count,
            assigned.Count,
            commented.Count,
            workedOn.Count,
            authoredCount,
            requested.Take(cap).Select(ToRef).ToArray(),
            assigned.Take(cap).Select(ToRef).ToArray(),
            commentedOnly.Take(cap).Select(ToRef).ToArray(),
            workedOn.Take(cap).Select(ToRef).ToArray());
    }

    /// <summary>
    /// Compute the set of task node-ids the user participates in for the given role.
    /// role: "requested" | "assigned" | "commented" | "any" (default when null/empty).
    /// </summary>
    private static HashSet<string> TaskIdsForUser(GraphDocument doc, int userId, string? role)
    {
        var userNode = $"user:{userId}";
        var r = (role ?? "any").Trim().ToLowerInvariant();

        var requested = new HashSet<string>(
            doc.Edges
                .Where(e => e.Type == "REQUESTED_BY"
                    && string.Equals(e.Target, userNode, StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Source),
            StringComparer.OrdinalIgnoreCase);

        var assigned = new HashSet<string>(
            doc.Edges
                .Where(e => e.Type == "ASSIGNED_TO"
                    && string.Equals(e.Target, userNode, StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Source),
            StringComparer.OrdinalIgnoreCase);

        if (r == "requested") return requested;
        if (r == "assigned") return assigned;

        // Tasks the user commented on: user → comment/detail (AUTHORED) → task (HAS_COMMENT/HAS_DETAIL reversed).
        HashSet<string> commented = ComputeCommentedTaskIds(doc, userNode);

        if (r == "commented") return commented;

        // "any" / unrecognised → union of all three.
        var union = new HashSet<string>(requested, StringComparer.OrdinalIgnoreCase);
        foreach (var t in assigned) union.Add(t);
        foreach (var t in commented) union.Add(t);
        return union;
    }

    private static HashSet<string> ComputeCommentedTaskIds(GraphDocument doc, string userNode)
    {
        var authoredDetailIds = new HashSet<string>(
            doc.Edges
                .Where(e => e.Type == "AUTHORED"
                    && string.Equals(e.Source, userNode, StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Target),
            StringComparer.OrdinalIgnoreCase);

        if (authoredDetailIds.Count == 0)
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return new HashSet<string>(
            doc.Edges
                .Where(e => (e.Type == "HAS_COMMENT" || e.Type == "HAS_DETAIL")
                    && authoredDetailIds.Contains(e.Target))
                .Select(e => e.Source),
            StringComparer.OrdinalIgnoreCase);
    }

    public CommentSummary[] ListCommentsForTask(int taskId, int max)
    {
        var doc = Doc();
        if (doc is null) return Array.Empty<CommentSummary>();
        var cap = Clamp(max, 50);

        var taskNode = $"task:{taskId}";
        var task = doc.Nodes.FirstOrDefault(n => n.Type == "Task"
            && string.Equals(n.Id, taskNode, StringComparison.OrdinalIgnoreCase));
        var taskLabel = task?.Label;

        var detailIds = new HashSet<string>(
            doc.Edges
                .Where(e => (e.Type == "HAS_COMMENT" || e.Type == "HAS_DETAIL")
                    && string.Equals(e.Source, taskNode, StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Target),
            StringComparer.OrdinalIgnoreCase);

        return doc.Nodes
            .Where(n => (n.Type == "Comment" || n.Type == "TaskDetail") && detailIds.Contains(n.Id))
            .OrderBy(n => GetDateTime(n, "insertedUtc") ?? DateTime.MinValue)
            .Take(cap)
            .Select(n => ToCommentSummary(n, taskLabel))
            .ToArray();
    }

    public TaskSummary[] ListTasksInCategory(int categoryId, bool includeDescendants, int max)
    {
        var doc = Doc();
        if (doc is null) return Array.Empty<TaskSummary>();
        var cap = Clamp(max, 25);

        var catIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { $"category:{categoryId}" };

        if (includeDescendants)
        {
            // CHILD_OF goes child → parent; descendants are nodes whose ancestor chain
            // contains the seed.
            bool added;
            do
            {
                added = false;
                foreach (var e in doc.Edges.Where(e => e.Type == "CHILD_OF"))
                {
                    if (catIds.Contains(e.Target) && catIds.Add(e.Source))
                        added = true;
                }
            } while (added);
        }

        var taskIds = new HashSet<string>(
            doc.Edges
                .Where(e => e.Type == "IN_CATEGORY" && catIds.Contains(e.Target))
                .Select(e => e.Source),
            StringComparer.OrdinalIgnoreCase);

        var ctx = TaskEnrichmentContext.Build(doc);
        return doc.Nodes
            .Where(n => n.Type == "Task" && taskIds.Contains(n.Id))
            .Take(cap)
            .Select(n => ToTaskSummary(n, ctx))
            .ToArray();
    }

    public UserSummary[] FindUserByName(string query, int max)
    {
        var doc = Doc();
        if (doc is null || string.IsNullOrWhiteSpace(query)) return Array.Empty<UserSummary>();
        var cap = Clamp(max, 10);
        var q = query.Trim().ToLowerInvariant();

        return doc.Nodes
            .Where(n => n.Type == "User")
            .Where(n =>
                (n.Label ?? "").ToLowerInvariant().Contains(q) ||
                (GetString(n, "username") ?? "").ToLowerInvariant().Contains(q) ||
                (GetString(n, "email") ?? "").ToLowerInvariant().Contains(q))
            .Take(cap)
            .Select(n =>
            {
                var uid = (int)(GetInt(n, "userId") ?? 0);
                var requested = TaskIdsForUser(doc, uid, "requested").Count;
                var assigned = TaskIdsForUser(doc, uid, "assigned").Count;
                var commented = TaskIdsForUser(doc, uid, "commented").Count;
                var any = TaskIdsForUser(doc, uid, "any").Count;
                bool? isSuper = null;
                if (n.Data.TryGetValue("isSuperUser", out var su))
                {
                    if (su is bool b) isSuper = b;
                    else if (su is JsonElement el && (el.ValueKind == JsonValueKind.True || el.ValueKind == JsonValueKind.False))
                        isSuper = el.GetBoolean();
                }
                return new UserSummary(
                    n.Id, uid, n.Label,
                    GetString(n, "username"),
                    GetString(n, "email"),
                    isSuper,
                    requested, assigned, commented, any);
            })
            .ToArray();
    }

    public RequesterSummary[] ListRequesters(string? nameContains, int max)
    {
        var doc = Doc();
        if (doc is null) return Array.Empty<RequesterSummary>();
        var cap = Clamp(max, 50);

        var userById = doc.Nodes
            .Where(n => n.Type == "User")
            .ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);

        // Key = "user:N" for registered requesters, "name:<lower-name>" for unregistered.
        var groups = new Dictionary<string, (string Key, GraphNode? User, string Name, List<GraphNode> Tasks)>(StringComparer.OrdinalIgnoreCase);

        foreach (var t in doc.Nodes.Where(n => n.Type == "Task"))
        {
            var uid = GetInt(t, "requesterUserId");
            var rname = GetString(t, "requesterName");
            string key;
            GraphNode? userNode = null;
            string displayName;

            if (uid is > 0 && userById.TryGetValue($"user:{uid}", out var un))
            {
                key = un.Id;
                userNode = un;
                displayName = un.Label ?? rname ?? $"User {uid}";
            }
            else if (!string.IsNullOrWhiteSpace(rname))
            {
                key = "name:" + rname.Trim().ToLowerInvariant();
                displayName = rname.Trim();
            }
            else
            {
                key = "name:(unknown)";
                displayName = "(unknown)";
            }

            if (!groups.TryGetValue(key, out var g))
                groups[key] = g = (key, userNode, displayName, new List<GraphNode>());
            g.Tasks.Add(t);
            groups[key] = g;
        }

        IEnumerable<(string Key, GraphNode? User, string Name, List<GraphNode> Tasks)> rows = groups.Values;

        if (!string.IsNullOrWhiteSpace(nameContains))
        {
            var q = nameContains.Trim().ToLowerInvariant();
            rows = rows.Where(r =>
                (r.Name ?? "").ToLowerInvariant().Contains(q) ||
                (r.User is not null && (
                    (GetString(r.User, "username") ?? "").ToLowerInvariant().Contains(q) ||
                    (GetString(r.User, "email") ?? "").ToLowerInvariant().Contains(q))));
        }

        return rows
            .OrderByDescending(r => r.Tasks.Count)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Take(cap)
            .Select(r =>
            {
                var byStatus = r.Tasks
                    .GroupBy(n => GetString(n, "status") ?? "(none)")
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
                var sample = r.Tasks
                    .Select(n => (int)(GetInt(n, "taskId") ?? 0))
                    .Where(i => i > 0)
                    .Take(10)
                    .ToArray();
                return new RequesterSummary(
                    r.User?.Id,
                    r.User is not null ? (int?)(GetInt(r.User, "userId") ?? 0) : null,
                    r.Name,
                    r.User is not null ? GetString(r.User, "username") : null,
                    r.User is not null ? GetString(r.User, "email") : null,
                    r.User is not null,
                    r.Tasks.Count,
                    byStatus,
                    sample);
            })
            .ToArray();
    }

    public TaskParticipant[] GetTaskParticipants(int taskId)
    {
        var doc = Doc();
        if (doc is null) return Array.Empty<TaskParticipant>();
        var taskNode = $"task:{taskId}";

        // user-id -> roles
        var roles = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var commentCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void Add(string userId, string role)
        {
            if (!roles.TryGetValue(userId, out var set))
                roles[userId] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            set.Add(role);
        }

        foreach (var e in doc.Edges)
        {
            if (!string.Equals(e.Source, taskNode, StringComparison.OrdinalIgnoreCase)) continue;
            if (e.Type == "REQUESTED_BY" && e.Target.StartsWith("user:", StringComparison.OrdinalIgnoreCase))
                Add(e.Target, "requester");
            else if (e.Type == "ASSIGNED_TO" && e.Target.StartsWith("user:", StringComparison.OrdinalIgnoreCase))
                Add(e.Target, "assignee");
        }

        // Commenters: task → comment/detail → user (AUTHORED_BY)
        var detailIds = new HashSet<string>(
            doc.Edges
                .Where(e => (e.Type == "HAS_COMMENT" || e.Type == "HAS_DETAIL")
                    && string.Equals(e.Source, taskNode, StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Target),
            StringComparer.OrdinalIgnoreCase);

        foreach (var e in doc.Edges)
        {
            if (e.Type != "AUTHORED_BY") continue;
            if (!detailIds.Contains(e.Source)) continue;
            if (!e.Target.StartsWith("user:", StringComparison.OrdinalIgnoreCase)) continue;
            Add(e.Target, "commenter");
            commentCounts[e.Target] = commentCounts.TryGetValue(e.Target, out var c) ? c + 1 : 1;
        }

        if (roles.Count == 0) return Array.Empty<TaskParticipant>();

        var byId = doc.Nodes.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);
        var result = new List<TaskParticipant>(roles.Count);
        foreach (var (uid, set) in roles)
        {
            if (!byId.TryGetValue(uid, out var userNode)) continue;
            result.Add(new TaskParticipant(
                userNode.Id,
                (int)(GetInt(userNode, "userId") ?? 0),
                userNode.Label,
                GetString(userNode, "username"),
                GetString(userNode, "email"),
                set.OrderBy(s => s, StringComparer.Ordinal).ToArray(),
                commentCounts.TryGetValue(uid, out var cc) ? cc : 0));
        }
        return result.ToArray();
    }

    public CategoryRollup? GetCategoryRollup(int categoryId, bool includeDescendants)
    {
        var doc = Doc();
        if (doc is null) return null;
        var rootId = $"category:{categoryId}";
        var rootNode = doc.Nodes.FirstOrDefault(n => n.Type == "Category"
            && string.Equals(n.Id, rootId, StringComparison.OrdinalIgnoreCase));
        if (rootNode is null) return null;

        var catIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { rootId };
        if (includeDescendants)
        {
            bool added;
            do
            {
                added = false;
                foreach (var e in doc.Edges.Where(e => e.Type == "CHILD_OF"))
                {
                    if (catIds.Contains(e.Target) && catIds.Add(e.Source))
                        added = true;
                }
            } while (added);
        }

        var directTaskIds = new HashSet<string>(
            doc.Edges
                .Where(e => e.Type == "IN_CATEGORY"
                    && string.Equals(e.Target, rootId, StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Source),
            StringComparer.OrdinalIgnoreCase);

        var allTaskIds = new HashSet<string>(
            doc.Edges
                .Where(e => e.Type == "IN_CATEGORY" && catIds.Contains(e.Target))
                .Select(e => e.Source),
            StringComparer.OrdinalIgnoreCase);

        var taskNodes = doc.Nodes
            .Where(n => n.Type == "Task" && allTaskIds.Contains(n.Id))
            .ToList();

        var byStatus = taskNodes
            .GroupBy(n => GetString(n, "status") ?? "(none)")
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var byPriority = taskNodes
            .GroupBy(n => GetString(n, "priority") ?? "(none)")
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var descendantIds = catIds
            .Where(c => !string.Equals(c, rootId, StringComparison.OrdinalIgnoreCase))
            .Select(c => int.TryParse(c.AsSpan("category:".Length), out var ci) ? ci : 0)
            .Where(i => i > 0)
            .ToArray();

        // Direct children only (CHILD_OF edges whose target is this root).
        var childCount = doc.Edges.Count(e => e.Type == "CHILD_OF"
            && string.Equals(e.Target, rootId, StringComparison.OrdinalIgnoreCase));

        return new CategoryRollup(
            categoryId,
            rootNode.Label,
            (int?)GetInt(rootNode, "parentCategoryId"),
            (int?)GetInt(rootNode, "level"),
            includeDescendants,
            descendantIds,
            childCount,
            directTaskIds.Count,
            allTaskIds.Count,
            byStatus,
            byPriority);
    }

    public GraphStats Stats()
    {
        var doc = Doc();
        if (doc is null)
        {
            return new GraphStats(DateTime.MinValue, 0, 0, new(), new(), new(), new(), new(),
                Array.Empty<TopUser>(), Array.Empty<TopCategory>());
        }

        var nodesByType = doc.Nodes.GroupBy(n => n.Type).ToDictionary(g => g.Key, g => g.Count());
        var edgesByType = doc.Edges.GroupBy(e => e.Type).ToDictionary(g => g.Key, g => g.Count());

        var taskNodes = doc.Nodes.Where(n => n.Type == "Task").ToList();
        var tasksByStatus = taskNodes
            .GroupBy(n => GetString(n, "status") ?? "(none)")
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var tasksByPriority = taskNodes
            .GroupBy(n => GetString(n, "priority") ?? "(none)")
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        var commentsByType = doc.Nodes
            .Where(n => n.Type == "Comment" || n.Type == "TaskDetail")
            .GroupBy(n => GetString(n, "detailType") ?? "(none)")
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        // Top users by "worked on" union (requested/assigned/commented).
        var topUsers = doc.Nodes
            .Where(n => n.Type == "User")
            .Select(n =>
            {
                var uid = (int)(GetInt(n, "userId") ?? 0);
                var any = uid > 0 ? TaskIdsForUser(doc, uid, "any").Count : 0;
                return new TopUser(n.Id, uid, n.Label, any);
            })
            .Where(u => u.WorkedOnTaskCount > 0)
            .OrderByDescending(u => u.WorkedOnTaskCount)
            .Take(10)
            .ToArray();

        // Top categories by direct task count (cheap and useful).
        var taskByCategoryEdges = doc.Edges
            .Where(e => e.Type == "IN_CATEGORY")
            .GroupBy(e => e.Target)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var topCategories = doc.Nodes
            .Where(n => n.Type == "Category")
            .Select(n => new TopCategory(
                (int)(GetInt(n, "categoryId") ?? 0),
                n.Label,
                taskByCategoryEdges.TryGetValue(n.Id, out var c) ? c : 0))
            .Where(c => c.TaskCount > 0)
            .OrderByDescending(c => c.TaskCount)
            .Take(10)
            .ToArray();

        return new GraphStats(
            doc.GeneratedUtc,
            doc.Nodes.Count,
            doc.Edges.Count,
            nodesByType,
            edgesByType,
            tasksByStatus,
            tasksByPriority,
            commentsByType,
            topUsers,
            topCategories);
    }

    // ---------- helpers ----------

    private static bool MatchesAll(GraphNode n, List<string> terms)
    {
        var label = (n.Label ?? "").ToLowerInvariant();
        // Match if any term hits label OR any data value (OR semantics — keeps recall high).
        foreach (var t in terms)
        {
            if (label.Contains(t)) return true;
        }
        foreach (var v in n.Data.Values)
        {
            if (v is null) continue;
            var s = v.ToString();
            if (string.IsNullOrEmpty(s)) continue;
            var l = s.ToLowerInvariant();
            foreach (var t in terms)
            {
                if (l.Contains(t)) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Pre-computed per-task structural data used to enrich <see cref="TaskSummary"/>
    /// rows (comment/work counts, participant counts, category memberships) without
    /// re-walking the full edge list once per task.
    /// </summary>
    private sealed class TaskEnrichmentContext
    {
        public Dictionary<string, int> CommentCount { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> WorkCount { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, HashSet<string>> Participants { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<(int Id, string Name)>> Categories { get; } = new(StringComparer.OrdinalIgnoreCase);

        public static TaskEnrichmentContext Build(GraphDocument doc)
        {
            var ctx = new TaskEnrichmentContext();
            var byId = doc.Nodes.ToDictionary(n => n.Id, StringComparer.OrdinalIgnoreCase);

            // Detail -> task lookup (so we can attribute author-of-detail to a task).
            var detailToTask = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in doc.Edges)
            {
                if (e.Type == "HAS_COMMENT" || e.Type == "HAS_DETAIL")
                {
                    if (!detailToTask.ContainsKey(e.Target)) detailToTask[e.Target] = e.Source;
                    if (!byId.TryGetValue(e.Target, out var detail)) continue;
                    var dt = GetString(detail, "detailType");
                    if (string.Equals(dt, "Work", StringComparison.OrdinalIgnoreCase))
                        ctx.WorkCount[e.Source] = ctx.WorkCount.TryGetValue(e.Source, out var w) ? w + 1 : 1;
                    else
                        ctx.CommentCount[e.Source] = ctx.CommentCount.TryGetValue(e.Source, out var c) ? c + 1 : 1;
                }
            }

            // Participants: requesters, assignees, and authors-of-details.
            foreach (var e in doc.Edges)
            {
                if (e.Type == "REQUESTED_BY" || e.Type == "ASSIGNED_TO")
                {
                    if (!ctx.Participants.TryGetValue(e.Source, out var set))
                        ctx.Participants[e.Source] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    set.Add(e.Target);
                }
                else if (e.Type == "AUTHORED_BY" && detailToTask.TryGetValue(e.Source, out var taskId))
                {
                    if (!ctx.Participants.TryGetValue(taskId, out var set))
                        ctx.Participants[taskId] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    set.Add(e.Target);
                }
                else if (e.Type == "IN_CATEGORY")
                {
                    if (!ctx.Categories.TryGetValue(e.Source, out var list))
                        ctx.Categories[e.Source] = list = new List<(int, string)>();
                    var cid = (int)(byId.TryGetValue(e.Target, out var cat) ? (GetInt(cat, "categoryId") ?? 0) : 0);
                    var cname = byId.TryGetValue(e.Target, out var cat2) ? (cat2.Label ?? string.Empty) : string.Empty;
                    list.Add((cid, cname));
                }
            }

            return ctx;
        }
    }

    private static NodeSummary ToSummary(GraphNode n)
    {
        var status = GetString(n, "status");
        return n.Type switch
        {
            "Task" => new NodeSummary(
                n.Id, n.Type, n.Label,
                Status: status,
                Priority: GetString(n, "priority"),
                CreatedUtc: GetDateTime(n, "createdUtc"),
                RequesterName: GetString(n, "requesterName"),
                AssignedUserName: GetString(n, "assignedUserName"),
                Snippet: Trim(GetString(n, "description"), 120)),
            "TaskDetail" or "Comment" => new NodeSummary(
                n.Id, n.Type, n.Label,
                CreatedUtc: GetDateTime(n, "insertedUtc"),
                AuthorName: GetString(n, "authorUserName"),
                ParentId: GetInt(n, "taskId") is long tid && tid > 0 ? $"task:{tid}" : null,
                Snippet: Trim(GetString(n, "text"), 120)),
            "User" => new NodeSummary(
                n.Id, n.Type, n.Label,
                Email: GetString(n, "email"),
                Snippet: GetString(n, "username")),
            "Category" => new NodeSummary(
                n.Id, n.Type, n.Label,
                ParentId: GetInt(n, "parentCategoryId") is long pid && pid > 0 ? $"category:{pid}" : null,
                Level: (int?)GetInt(n, "level")),
            _ => new NodeSummary(n.Id, n.Type, n.Label, Status: status),
        };
    }

    private static TaskSummary ToTaskSummary(GraphNode n, TaskEnrichmentContext ctx)
    {
        var created = GetDateTime(n, "createdUtc");
        int? ageDays = created.HasValue
            ? Math.Max(0, (int)Math.Floor((DateTime.UtcNow - created.Value).TotalDays))
            : null;

        int comments = ctx.CommentCount.TryGetValue(n.Id, out var cc) ? cc : 0;
        int works = ctx.WorkCount.TryGetValue(n.Id, out var wc) ? wc : 0;
        int participants = ctx.Participants.TryGetValue(n.Id, out var pset) ? pset.Count : 0;

        int[] catIds; string[] catNames;
        if (ctx.Categories.TryGetValue(n.Id, out var cats))
        {
            catIds = cats.Select(c => c.Id).Where(i => i > 0).ToArray();
            catNames = cats.Select(c => c.Name).Where(s => !string.IsNullOrEmpty(s)).ToArray();
        }
        else
        {
            catIds = Array.Empty<int>();
            catNames = Array.Empty<string>();
        }

        return new TaskSummary(
            n.Id,
            (int)(GetInt(n, "taskId") ?? 0),
            GetString(n, "description"),
            GetString(n, "status"),
            GetString(n, "priority"),
            created,
            GetDateTime(n, "dueUtc"),
            (int?)GetInt(n, "requesterUserId"),
            GetString(n, "requesterName"),
            (int?)GetInt(n, "assignedUserId"),
            GetString(n, "assignedUserName"),
            (int?)GetInt(n, "assignedRoleId"),
            comments,
            works,
            participants,
            ageDays,
            catIds,
            catNames);
    }

    private static CommentSummary ToCommentSummary(GraphNode n, string? taskLabel)
    {
        var start = GetDateTime(n, "startTime");
        var stop = GetDateTime(n, "stopTime");
        double? duration = (start.HasValue && stop.HasValue)
            ? (stop.Value - start.Value).TotalMinutes
            : null;

        return new CommentSummary(
            n.Id,
            (int)(GetInt(n, "detailId") ?? 0),
            (int)(GetInt(n, "taskId") ?? 0),
            taskLabel,
            GetString(n, "detailType"),
            GetDateTime(n, "insertedUtc"),
            (int?)GetInt(n, "authorUserId") ?? (int?)GetInt(n, "userId"),
            GetString(n, "authorUserName"),
            GetString(n, "text"),
            start,
            stop,
            duration);
    }

    private static string? Trim(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        return s.Length <= max ? s : s.Substring(0, max) + "…";
    }

    private static string? GetString(GraphNode n, string key)
    {
        if (!n.Data.TryGetValue(key, out var v) || v is null) return null;
        if (v is string s) return s;
        if (v is JsonElement el)
        {
            return el.ValueKind switch
            {
                JsonValueKind.String => el.GetString(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => el.ToString()
            };
        }
        return v.ToString();
    }

    private static long? GetInt(GraphNode n, string key)
    {
        if (!n.Data.TryGetValue(key, out var v) || v is null) return null;
        if (v is long l) return l;
        if (v is int i) return i;
        if (v is JsonElement el && el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var jl)) return jl;
        if (long.TryParse(v.ToString(), out var p)) return p;
        return null;
    }

    private static DateTime? GetDateTime(GraphNode n, string key)
    {
        if (!n.Data.TryGetValue(key, out var v) || v is null) return null;
        if (v is DateTime dt) return dt;
        if (v is DateTimeOffset dto) return dto.UtcDateTime;
        if (v is JsonElement el && el.ValueKind == JsonValueKind.String && el.TryGetDateTime(out var jd)) return jd;
        if (DateTime.TryParse(v.ToString(), out var p)) return p;
        return null;
    }
}
