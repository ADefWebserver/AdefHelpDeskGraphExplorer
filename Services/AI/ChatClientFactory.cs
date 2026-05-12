using System.ClientModel;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using OpenAI;

namespace AdefHelpDeskGraphExplorer.Services.AI;

/// <summary>
/// Builds an <see cref="IChatClient"/> for the requested provider key,
/// using settings from <see cref="AIConfigurationService"/>.
/// </summary>
public sealed class ChatClientFactory
{
    private readonly AIConfigurationService _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IHttpClientFactory _httpClientFactory;

    public ChatClientFactory(AIConfigurationService config, ILoggerFactory loggerFactory, IHttpClientFactory httpClientFactory)
    {
        _config = config;
        _loggerFactory = loggerFactory;
        _httpClientFactory = httpClientFactory;
    }

    public (IChatClient Client, string Model) Create(string providerKey)
    {
        var key = NormalizeProviderKey(providerKey);
        var settings = _config.GetProvider(key);

        if (!settings.Enabled)
            throw new InvalidOperationException($"Provider '{providerKey}' is disabled. Enable it in Settings first.");

        IChatClient inner;
        string model;

        switch (key)
        {
            case "OpenAI":
            {
                if (string.IsNullOrWhiteSpace(settings.ApiKey))
                    throw new InvalidOperationException("OpenAI API key is not configured.");

                model = settings.DefaultModel ?? "gpt-4o-mini";
                var options = new OpenAIClientOptions();
                if (!string.IsNullOrWhiteSpace(settings.Endpoint))
                    options.Endpoint = new Uri(settings.Endpoint);

                var openAI = new OpenAIClient(new ApiKeyCredential(settings.ApiKey!), options);
                inner = openAI.GetChatClient(model).AsIChatClient();
                break;
            }
            case "AzureOpenAI":
            {
                if (string.IsNullOrWhiteSpace(settings.ApiKey) || string.IsNullOrWhiteSpace(settings.Endpoint))
                    throw new InvalidOperationException("Azure OpenAI endpoint and API key are required.");

                model = settings.DeploymentName ?? throw new InvalidOperationException("Azure OpenAI deployment name is required.");
                var azure = new AzureOpenAIClient(
                    new Uri(settings.Endpoint!),
                    new AzureKeyCredential(settings.ApiKey!));
                inner = azure.GetChatClient(model).AsIChatClient();
                break;
            }
            case "GoogleAI":
            {
                if (string.IsNullOrWhiteSpace(settings.ApiKey))
                    throw new InvalidOperationException("Google AI API key is not configured.");

                model = settings.DefaultModel ?? "gemini-2.5-flash";
                var http = _httpClientFactory.CreateClient(nameof(GoogleAIChatClient));
                inner = new GoogleAIChatClient(settings.ApiKey!, model, http);
                break;
            }
            case "Anthropic":
            {
                if (string.IsNullOrWhiteSpace(settings.ApiKey))
                    throw new InvalidOperationException("Anthropic API key is not configured.");

                model = settings.DefaultModel ?? "claude-sonnet-4-20250514";
                var http = _httpClientFactory.CreateClient(nameof(AnthropicChatClient));
                inner = new AnthropicChatClient(settings.ApiKey!, model, http);
                break;
            }
            default:
                throw new InvalidOperationException($"Unknown provider '{providerKey}'.");
        }

        var built = new ChatClientBuilder(inner)
            .UseLogging(_loggerFactory)
            .UseOpenTelemetry(_loggerFactory, sourceName: "AdefHelpDeskGraphExplorer.Chat")
            .Build();

        return (built, model);
    }

    private static string NormalizeProviderKey(string providerKey) => providerKey switch
    {
        "Azure OpenAI" => "AzureOpenAI",
        "Google AI" => "GoogleAI",
        _ => providerKey
    };
}
