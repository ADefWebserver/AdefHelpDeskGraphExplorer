using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using AdefHelpDeskGraphExplorer.Models;
using MEAIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using MEAIChatRole = Microsoft.Extensions.AI.ChatRole;

namespace AdefHelpDeskGraphExplorer.Services.AI;

/// <summary>
/// Stateless chat orchestrator. Streams tokens from the active <see cref="IChatClient"/>.
/// </summary>
public sealed class ChatService
{
    private readonly ChatClientFactory _factory;
    private readonly IOptionsMonitor<AIOptions> _options;
    private readonly ILogger<ChatService> _logger;

    public ChatService(ChatClientFactory factory, IOptionsMonitor<AIOptions> options, ILogger<ChatService> logger)
    {
        _factory = factory;
        _options = options;
        _logger = logger;
    }

    public async IAsyncEnumerable<string> StreamAsync(
        IEnumerable<ChatTurn> history,
        string? providerKeyOverride = null,
        string? modelOverride = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var defaults = _options.CurrentValue.Defaults;
        var providerKey = providerKeyOverride ?? _options.CurrentValue.ActiveProvider;

        var (client, defaultModel) = _factory.Create(providerKey);
        using var _ = client;

        var messages = new List<MEAIChatMessage>();
        if (!history.Any(m => m.Role == ChatTurnRole.System) && !string.IsNullOrWhiteSpace(defaults.SystemPrompt))
        {
            messages.Add(new MEAIChatMessage(MEAIChatRole.System, defaults.SystemPrompt));
        }

        foreach (var m in history)
        {
            var role = m.Role switch
            {
                ChatTurnRole.User => MEAIChatRole.User,
                ChatTurnRole.Assistant => MEAIChatRole.Assistant,
                _ => MEAIChatRole.System
            };
            messages.Add(new MEAIChatMessage(role, m.Content));
        }

        var effectiveModel = string.IsNullOrWhiteSpace(modelOverride) ? defaultModel : modelOverride;
        var chatOptions = new ChatOptions
        {
            MaxOutputTokens = defaults.MaxOutputTokens,
            ModelId = effectiveModel,
        };

        var supportsTemperature =
            AICapabilities.IsAnthropic(providerKey)
                ? AICapabilities.AnthropicSupportsTemperature(effectiveModel)
                : AICapabilities.SupportsCustomTemperature(effectiveModel);

        if (supportsTemperature)
        {
            chatOptions.Temperature = defaults.Temperature;
        }

        await foreach (var update in client.GetStreamingResponseAsync(messages, chatOptions, ct))
        {
            if (!string.IsNullOrEmpty(update.Text))
            {
                yield return update.Text;
            }
        }
    }

    public async Task<string> TestAccessAsync(string providerKey, string? model = null, CancellationToken ct = default)
    {
        var (client, defaultModel) = _factory.Create(providerKey);
        using var _ = client;
        var resp = await client.GetResponseAsync(
            new[] { new MEAIChatMessage(MEAIChatRole.User, "Say 'ok'.") },
            new ChatOptions { ModelId = string.IsNullOrWhiteSpace(model) ? defaultModel : model, MaxOutputTokens = 16 },
            ct);
        return resp.Text ?? string.Empty;
    }
}
