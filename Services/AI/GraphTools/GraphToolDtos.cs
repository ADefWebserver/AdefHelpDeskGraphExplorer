namespace AdefHelpDeskGraphExplorer.Services.AI.GraphTools;

/// <summary>
/// Compact node listing entry used in search/list results. Per-type optional
/// fields are populated when known so the model rarely needs a follow-up call:
///   • Task          → Status, Priority, CreatedUtc, RequesterName, AssignedUserName, Snippet (description)
///   • TaskDetail    → CreatedUtc (insertedUtc), AuthorName, ParentId (task:N), Snippet (text)
///   • User          → Email, Snippet (username)
///   • Category      → ParentId (category:N), Level
/// </summary>
public sealed record NodeSummary(
    string Id,
    string Type,
    string Label,
    string? Status = null,
    string? Priority = null,
    DateTime? CreatedUtc = null,
    string? RequesterName = null,
    string? AssignedUserName = null,
    string? AuthorName = null,
    string? ParentId = null,
    int? Level = null,
    string? Email = null,
    string? Snippet = null);

/// <summary>
/// Full node payload including its data dictionary, plus edge counts and a
/// small preview of 1-hop neighbours so a single GetNode call already shows
/// the most important relationships.
/// </summary>
public sealed record NodeDetail(
    string Id,
    string Type,
    string Label,
    Dictionary<string, object?> Data,
    int OutgoingEdgeCount,
    int IncomingEdgeCount,
    Dictionary<string, int> EdgeTypeCounts,
    Neighbor[] NeighborsPreview);

/// <summary>
/// 1-hop neighbour with the edge type connecting it to the source node, plus
/// optional neighbour discriminators (status/priority/snippet) when known.
/// </summary>
public sealed record Neighbor(
    string EdgeId,
    string EdgeType,
    string Direction, // "out" (source→neighbor) or "in" (neighbor→source)
    string NeighborId,
    string NeighborType,
    string NeighborLabel,
    string? NeighborStatus = null,
    string? NeighborPriority = null,
    string? NeighborSnippet = null);

/// <summary>Summary row for a Task. Counts and category references are best-effort.</summary>
public sealed record TaskSummary(
    string Id,
    int TaskId,
    string? Description,
    string? Status,
    string? Priority,
    DateTime? CreatedUtc,
    DateTime? DueUtc,
    int? RequesterUserId,
    string? RequesterName,
    int? AssignedUserId,
    string? AssignedUserName,
    int? AssignedRoleId,
    int CommentCount,
    int WorkCount,
    int ParticipantCount,
    int? AgeDays,
    int[] CategoryIds,
    string[] CategoryNames);

/// <summary>Summary row for a Comment / TaskDetail.</summary>
public sealed record CommentSummary(
    string Id,
    int DetailId,
    int TaskId,
    string? TaskLabel,
    string? DetailType,
    DateTime? InsertedUtc,
    int? AuthorUserId,
    string? AuthorUserName,
    string? Text,
    DateTime? StartTime,
    DateTime? StopTime,
    double? DurationMinutes);

/// <summary>Summary row for a User.</summary>
public sealed record UserSummary(
    string Id,
    int UserId,
    string DisplayName,
    string? Username,
    string? Email,
    bool? IsSuperUser,
    int RequestedTaskCount,
    int AssignedTaskCount,
    int CommentedTaskCount,
    int WorkedOnTaskCount);

/// <summary>
/// High-level graph statistics. Includes overall status/priority breakdowns
/// and small "top-N" lists so the model can summarise the graph in one call.
/// </summary>
public sealed record GraphStats(
    DateTime GeneratedUtc,
    int NodeCount,
    int EdgeCount,
    Dictionary<string, int> NodesByType,
    Dictionary<string, int> EdgesByType,
    Dictionary<string, int> TasksByStatus,
    Dictionary<string, int> TasksByPriority,
    Dictionary<string, int> CommentsByType,
    TopUser[] TopUsersByActivity,
    TopCategory[] TopCategoriesByTaskCount);

/// <summary>Top-N entry for "most active users" in <see cref="GraphStats"/>.</summary>
public sealed record TopUser(
    string Id,
    int UserId,
    string DisplayName,
    int WorkedOnTaskCount);

/// <summary>Top-N entry for "categories with the most tasks" in <see cref="GraphStats"/>.</summary>
public sealed record TopCategory(
    int CategoryId,
    string CategoryName,
    int TaskCount);

/// <summary>
/// Lightweight task reference (id + label + key descriptors) used in activity
/// rollups and other aggregate responses so the model never has to follow up
/// with a GetNode call just to print a task name.
/// </summary>
public sealed record TaskRef(
    string Id,
    string Label,
    string? Status,
    string? Priority = null,
    DateTime? CreatedUtc = null);

/// <summary>
/// Per-user activity rollup. Counts are always true totals (not capped); the
/// task reference arrays may be truncated. Includes a split of requested vs.
/// assigned vs. commented for richer downstream reasoning.
/// </summary>
public sealed record UserActivity(
    string Id,
    int UserId,
    string DisplayName,
    string? Username,
    string? Email,
    bool? IsSuperUser,
    int RequestedTaskCount,
    int AssignedTaskCount,
    int CommentedTaskCount,
    int WorkedOnTaskCount,
    int AuthoredCommentCount,
    TaskRef[] RequestedTasks,
    TaskRef[] AssignedTasks,
    TaskRef[] CommentedOnlyTasks,
    TaskRef[] WorkedOnTasks);

/// <summary>
/// One user's participation in a task (any combination of roles). Includes
/// username/email plus a per-user comment count on that task.
/// </summary>
public sealed record TaskParticipant(
    string Id,
    int UserId,
    string DisplayName,
    string? Username,
    string? Email,
    string[] Roles, // any of: "requester", "assignee", "commenter"
    int CommentCount);

/// <summary>
/// Aggregate task counts inside a category (optionally including descendants),
/// plus parent/child structural info and a per-priority breakdown.
/// </summary>
public sealed record CategoryRollup(
    int CategoryId,
    string CategoryName,
    int? ParentCategoryId,
    int? Level,
    bool IncludesDescendants,
    int[] DescendantCategoryIds,
    int ChildCategoryCount,
    int DirectTaskCount,
    int TotalTaskCount,
    Dictionary<string, int> ByStatus,
    Dictionary<string, int> ByPriority);
