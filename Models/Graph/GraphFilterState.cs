using System.Text.Json.Serialization;

namespace AdefHelpDeskGraphExplorer.Models.Graph;

/// <summary>
/// Client-side filter applied to the rendered graph. Empty sets for
/// <see cref="TaskStatuses"/> and <see cref="DetailTypes"/> mean "show all".
/// </summary>
public sealed class GraphFilterState
{
    [JsonPropertyName("nodeTypes")]
    public HashSet<string> NodeTypes { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("taskStatuses")]
    public HashSet<string> TaskStatuses { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("detailTypes")]
    public HashSet<string> DetailTypes { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("users")]
    public HashSet<string> Users { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("tasks")]
    public HashSet<string> Tasks { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("requesters")]
    public HashSet<string> Requesters { get; set; } = new(StringComparer.Ordinal);

    // Adapters so RadzenCheckBoxList (IEnumerable<T>) can two-way bind.
    [JsonIgnore]
    public IEnumerable<string> NodeTypesList
    {
        get => NodeTypes;
        set => NodeTypes = value is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(value, StringComparer.Ordinal);
    }

    [JsonIgnore]
    public IEnumerable<string> TaskStatusesList
    {
        get => TaskStatuses;
        set => TaskStatuses = value is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(value, StringComparer.Ordinal);
    }

    [JsonIgnore]
    public IEnumerable<string> DetailTypesList
    {
        get => DetailTypes;
        set => DetailTypes = value is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(value, StringComparer.Ordinal);
    }

    [JsonIgnore]
    public IEnumerable<string> UsersList
    {
        get => Users;
        set => Users = value is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(value, StringComparer.Ordinal);
    }

    [JsonIgnore]
    public IEnumerable<string> TasksList
    {
        get => Tasks;
        set => Tasks = value is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(value, StringComparer.Ordinal);
    }

    [JsonIgnore]
    public IEnumerable<string> RequestersList
    {
        get => Requesters;
        set => Requesters = value is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(value, StringComparer.Ordinal);
    }
}
