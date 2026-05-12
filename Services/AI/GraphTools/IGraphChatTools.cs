namespace AdefHelpDeskGraphExplorer.Services.AI.GraphTools;

/// <summary>
/// Read-only graph query surface exposed to the chat AI as tool / function calls.
/// All methods are pure reads against the cached <c>graph.json</c> document.
/// </summary>
public interface IGraphChatTools
{
    NodeSummary[] SearchNodes(string query, string? type, int max);
    NodeDetail? GetNode(string id);
    Neighbor[] GetNeighbors(string id, string? edgeType, int max);

    /// <summary>
    /// List tasks where the user participates. <paramref name="role"/> selects which
    /// participation to count: "requested", "assigned", "commented", or "any" (default).
    /// </summary>
    TaskSummary[] ListTasksForUser(int userId, string? role, string? status, int max);

    /// <summary>True total of tasks the user participated in for the given role.</summary>
    int CountTasksForUser(int userId, string? role);

    /// <summary>Comprehensive activity for one user — counts plus (truncated) id arrays.</summary>
    UserActivity? GetUserActivity(int userId, int maxIdsPerList = 100);

    CommentSummary[] ListCommentsForTask(int taskId, int max);
    TaskSummary[] ListTasksInCategory(int categoryId, bool includeDescendants, int max);

    /// <summary>Everyone connected to a task with their roles (requester/assignee/commenter).</summary>
    TaskParticipant[] GetTaskParticipants(int taskId);

    /// <summary>Aggregate task counts (overall + per-status) for a category.</summary>
    CategoryRollup? GetCategoryRollup(int categoryId, bool includeDescendants);

    UserSummary[] FindUserByName(string query, int max);

    /// <summary>
    /// Enumerate every distinct requester across all Task nodes — including
    /// unregistered requesters that only appear as a free-text
    /// <c>requesterName</c> on a task. Registered requesters (those with a
    /// User node and <c>requesterUserId &gt; 0</c>) are merged onto the same
    /// row as the linked user. Results are sorted by task count, descending.
    /// </summary>
    RequesterSummary[] ListRequesters(string? nameContains, int max);

    GraphStats Stats();
}
