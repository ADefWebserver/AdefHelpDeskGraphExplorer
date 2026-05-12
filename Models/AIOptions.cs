namespace AdefHelpDeskGraphExplorer.Models;

public sealed class AIOptions
{
    public const string SectionName = "AI";

    public string ActiveProvider { get; set; } = "OpenAI";
    public Dictionary<string, ProviderOptions> Providers { get; set; } = new();
    public ChatDefaults Defaults { get; set; } = new();
    public ToolsOptions Tools { get; set; } = new();
}

public sealed class ToolsOptions
{
    /// <summary>Master switch for graph-traversal tool calling on the chat page.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Maximum tool-call rounds per user turn (safety cap).</summary>
    public int MaxCallsPerTurn { get; set; } = 6;
    /// <summary>Hard ceiling for any tool's max-results parameter.</summary>
    public int MaxResultsHardCap { get; set; } = 100;
}

public sealed class ProviderOptions
{
    public bool Enabled { get; set; }
    public string? ApiKey { get; set; }
    public string? Endpoint { get; set; }
    public string? DefaultModel { get; set; }
    public string? DeploymentName { get; set; }   // Azure only
    public string? ApiVersion { get; set; }       // Azure only
    public List<string> Models { get; set; } = new();
}

public sealed class ChatDefaults
{
    public float Temperature { get; set; } = 0.7f;
    public int MaxOutputTokens { get; set; } = 1024;
    public string SystemPrompt { get; set; } = "You are a help-desk analytics assistant.";
}

public sealed class GraphOptions
{
    public const string SectionName = "Graph";
    public string OutputDirectory { get; set; } = "App_Data/graph";
    public bool GenerateEmbeddings { get; set; } = false;
}
