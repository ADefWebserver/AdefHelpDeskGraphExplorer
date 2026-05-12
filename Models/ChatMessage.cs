namespace AdefHelpDeskGraphExplorer.Models;

public enum ChatTurnRole
{
    System,
    User,
    Assistant
}

/// <summary>
/// One entry in a SimpleChat conversation. Named "ChatTurn" to avoid clashing with
/// Microsoft.Extensions.AI.ChatMessage and Radzen.Blazor.ChatMessage.
/// </summary>
public sealed class ChatTurn
{
    public ChatTurnRole Role { get; set; }
    public string Content { get; set; } = string.Empty;
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
    public bool IsStreaming { get; set; }
    public bool IsError { get; set; }

    public ChatTurn() { }

    public ChatTurn(ChatTurnRole role, string content)
    {
        Role = role;
        Content = content;
    }
}
