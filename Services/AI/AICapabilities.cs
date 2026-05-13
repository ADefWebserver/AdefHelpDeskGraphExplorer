namespace AdefHelpDeskGraphExplorer.Services.AI;

/// <summary>
/// Centralised "does this provider/model support X" rules.
/// This is the single place that names specific model-id prefixes.
/// Ported from AIStoryBuildersOnline/AI/AICapabilities.cs.
/// </summary>
internal static class AICapabilities
{
    public static bool IsGemini(string? aiType) =>
        string.Equals(aiType, "Google AI", StringComparison.OrdinalIgnoreCase)
        || string.Equals(aiType, "GoogleAI", StringComparison.OrdinalIgnoreCase)
        || string.Equals(aiType, "Google", StringComparison.OrdinalIgnoreCase);

    public static bool IsAnthropic(string? aiType) =>
        string.Equals(aiType, "Anthropic", StringComparison.OrdinalIgnoreCase);

    public static bool IsOpenAI(string? aiType) =>
        string.Equals(aiType, "OpenAI", StringComparison.OrdinalIgnoreCase)
        || string.Equals(aiType, "Azure OpenAI", StringComparison.OrdinalIgnoreCase)
        || string.Equals(aiType, "AzureOpenAI", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// OpenAI GPT-5 and o-series reasoning models reject any explicit
    /// temperature (must be the provider default of 1.0).
    /// </summary>
    public static bool SupportsCustomTemperature(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return true;
        var id = modelId.Trim().ToLowerInvariant();
        if (id.StartsWith("gpt-5")) return false;
        if (id.StartsWith("o1") || id.StartsWith("o3") || id.StartsWith("o4")) return false;
        return true;
    }

    /// <summary>
    /// Newer Claude models (Opus 4.x and Sonnet 4.x and later) have deprecated
    /// the <c>temperature</c> request parameter and reject any explicit value.
    /// </summary>
    public static bool AnthropicSupportsTemperature(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return true;
        var id = modelId.Trim().ToLowerInvariant();
        if (id.StartsWith("claude-opus-4")) return false;
        if (id.StartsWith("claude-sonnet-4")) return false;
        return true;
    }

    /// <summary>
    /// Whether the active provider+model pair can drive a tool / function-calling loop
    /// through Microsoft.Extensions.AI's <c>FunctionCallContent</c> round-trip.
    /// OpenAI / Azure OpenAI use the native SDK that supports this out of the box.
    /// <see cref="AnthropicChatClient"/> implements Anthropic's native tool_use /
    /// tool_result content blocks. The custom <see cref="GoogleAIChatClient"/>
    /// wrapper does not yet forward tool definitions to the wire protocol.
    /// </summary>
    public static bool SupportsToolCalling(string? providerKey, string? modelId)
    {
        if (string.IsNullOrWhiteSpace(providerKey)) return false;
        var key = providerKey.Trim();
        if (string.Equals(key, "OpenAI", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(key, "AzureOpenAI", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(key, "Azure OpenAI", StringComparison.OrdinalIgnoreCase)) return true;
        if (IsAnthropic(key)) return true;
        if (IsGemini(key)) return true;
        return false;
    }
}
