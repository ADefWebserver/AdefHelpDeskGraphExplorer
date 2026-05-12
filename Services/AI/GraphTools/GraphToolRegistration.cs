using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace AdefHelpDeskGraphExplorer.Services.AI.GraphTools;

/// <summary>
/// Builds the <see cref="AITool"/> list exposed to the chat model for read-only
/// traversal of the help-desk knowledge graph.
/// </summary>
public static class GraphToolRegistration
{
    public static IList<AITool> BuildTools(IGraphChatTools t) => new List<AITool>
    {
        AIFunctionFactory.Create(
            ([Description("Free-text query — names, words, IDs.")] string query,
             [Description("Optional node type filter: Task, User, Category, Comment, TaskDetail.")] string? type,
             [Description("Max results, 1-100. Default 20.")] int max)
                => t.SearchNodes(query, type, max),
            new AIFunctionFactoryOptions
            {
                Name = "SearchNodes",
                Description = "Search graph nodes by keyword. Returns enriched node summaries that already include the most relevant per-type fields: Task -> status/priority/createdUtc/requesterName/assignedUserName/snippet; TaskDetail -> insertedUtc/authorName/parentId(task:N)/snippet; User -> email/snippet(username); Category -> parentId/level. Prefer this over follow-up GetNode calls."
            }),

        AIFunctionFactory.Create(
            ([Description("The node id, e.g. 'task:42', 'user:7', 'comment:912'.")] string id)
                => t.GetNode(id),
            new AIFunctionFactoryOptions
            {
                Name = "GetNode",
                Description = "Return the full data payload for a single node plus structural summary: outgoing/incoming edge counts, per-edge-type counts, and a preview of up to 12 1-hop neighbours (with each neighbour's status/priority/snippet when known)."
            }),

        AIFunctionFactory.Create(
            ([Description("The node id, e.g. 'task:42'.")] string id,
             [Description("Optional edge type filter: REQUESTED_BY, ASSIGNED_TO, IN_CATEGORY, CHILD_OF, HAS_DETAIL, HAS_COMMENT, AUTHORED, AUTHORED_BY.")] string? edgeType,
             [Description("Max results, 1-100. Default 25.")] int max)
                => t.GetNeighbors(id, edgeType, max),
            new AIFunctionFactoryOptions
            {
                Name = "GetNeighbors",
                Description = "Get 1-hop neighbours of a node, optionally filtered by edge type. Each neighbour entry includes status/priority/snippet for the neighbour where applicable, so you usually don't need a follow-up GetNode call."
            }),

        AIFunctionFactory.Create(
            ([Description("The user id (integer).")] int userId,
             [Description("Which participation to include: 'requested', 'assigned', 'commented', or 'any' (default).")] string? role,
             [Description("Optional status filter, e.g. Open, Closed.")] string? status,
             [Description("Max results, 1-100. Default 25.")] int max)
                => t.ListTasksForUser(userId, role, status, max),
            new AIFunctionFactoryOptions
            {
                Name = "ListTasksForUser",
                Description = "List tasks the user participated in. 'role' selects which participation: requested/assigned/commented/any. Each returned task includes status, priority, requester+assignee names, comment/work counts, participant count, ageDays, and category ids+names. Result list is capped — for a true count call CountTasksForUser or GetUserActivity."
            }),

        AIFunctionFactory.Create(
            ([Description("The user id (integer).")] int userId,
             [Description("Which participation to count: 'requested', 'assigned', 'commented', or 'any' (default).")] string? role)
                => t.CountTasksForUser(userId, role),
            new AIFunctionFactoryOptions
            {
                Name = "CountTasksForUser",
                Description = "True total count of tasks the user participated in for the given role. Never capped. Use this to answer 'how many tasks did <user> work on?'."
            }),

        AIFunctionFactory.Create(
            ([Description("The user id (integer).")] int userId,
             [Description("Max task IDs to include in each list (1-500). Default 100. Counts are always exact regardless of this cap.")] int maxIdsPerList)
                => t.GetUserActivity(userId, maxIdsPerList <= 0 ? 100 : maxIdsPerList),
            new AIFunctionFactoryOptions
            {
                Name = "GetUserActivity",
                Description = "Complete activity rollup for a user. Returns username, email, isSuperUser, requested/assigned/commented/workedOn counts, authoredCommentCount, and four task arrays (requested, assigned, commentedOnly, workedOn) where each entry has id, label, status, priority and createdUtc. Use this for any aggregate question about one person — you should NOT need follow-up GetNode calls to name the tasks."
            }),

        AIFunctionFactory.Create(
            ([Description("The task id (integer).")] int taskId)
                => t.GetTaskParticipants(taskId),
            new AIFunctionFactoryOptions
            {
                Name = "GetTaskParticipants",
                Description = "All users connected to a task with their roles (requester/assignee/commenter), plus each user's username, email and number of comments they wrote on that task. Use this to answer 'who worked on task N?'."
            }),

        AIFunctionFactory.Create(
            ([Description("The task id (integer).")] int taskId,
             [Description("Max results, 1-100. Default 50.")] int max)
                => t.ListCommentsForTask(taskId, max),
            new AIFunctionFactoryOptions
            {
                Name = "ListCommentsForTask",
                Description = "List all task details and comments for a task, ordered by insertedUtc. Each entry includes the parent task label, detailType, author id/name, text, startTime/stopTime and durationMinutes (for Work entries)."
            }),

        AIFunctionFactory.Create(
            ([Description("The category id (integer).")] int categoryId,
             [Description("Include descendant categories (walks CHILD_OF). Default true.")] bool includeDescendants,
             [Description("Max results, 1-100. Default 25.")] int max)
                => t.ListTasksInCategory(categoryId, includeDescendants, max),
            new AIFunctionFactoryOptions
            {
                Name = "ListTasksInCategory",
                Description = "List tasks in a category (optionally including descendants). Each task already carries status, priority, requester+assignee names, comment/work counts, participant count, ageDays, and category ids+names."
            }),

        AIFunctionFactory.Create(
            ([Description("The category id (integer).")] int categoryId,
             [Description("Include descendant categories (walks CHILD_OF). Default true.")] bool includeDescendants)
                => t.GetCategoryRollup(categoryId, includeDescendants),
            new AIFunctionFactoryOptions
            {
                Name = "GetCategoryRollup",
                Description = "Aggregate task counts for a category: parentCategoryId, level, childCategoryCount, descendant category ids, direct count, total count (with descendants), and breakdowns ByStatus and ByPriority. Use this for 'how many tickets in <category>?'."
            }),

        AIFunctionFactory.Create(
            ([Description("Natural-language person name, username, or partial email.")] string query,
             [Description("Max results, 1-50. Default 10.")] int max)
                => t.FindUserByName(query, max),
            new AIFunctionFactoryOptions
            {
                Name = "FindUserByName",
                Description = "Resolve a natural-language person name (or partial username/email) to one or more User nodes. Each result already includes isSuperUser and the user's requested/assigned/commented/workedOn task counts — you usually don't need a follow-up GetUserActivity call just to get counts."
            }),

        AIFunctionFactory.Create(
            ([Description("Optional case-insensitive substring filter on name / username / email.")] string? nameContains,
             [Description("Max results, 1-100. Default 50.")] int max)
                => t.ListRequesters(nameContains, max),
            new AIFunctionFactoryOptions
            {
                Name = "ListRequesters",
                Description = "Enumerate EVERY distinct requester across all tasks in the graph — including unregistered requesters that only appear as a free-text requesterName on a Task (these have no User node and isRegistered=false). Registered requesters (backed by a User node) are merged onto the same row. Each row returns id (user:N or null), userId, name, username, email, isRegistered, taskCount, tasksByStatus, and up to 10 sample task ids. Results are sorted by taskCount descending. ALWAYS use this tool to answer 'who submitted tasks?', 'list the requesters', or 'who are the requesters?' — do NOT rely on SearchNodes excerpts or FindUserByName, which both miss unregistered requesters."
            }),

        AIFunctionFactory.Create(
            () => t.Stats(),
            new AIFunctionFactoryOptions
            {
                Name = "GraphStats",
                Description = "High-level graph statistics: generatedUtc, total node/edge counts, breakdowns by node type and edge type, tasksByStatus, tasksByPriority, commentsByType, plus top-10 most active users (by workedOnTaskCount) and top-10 categories by task count."
            }),
    };
}
