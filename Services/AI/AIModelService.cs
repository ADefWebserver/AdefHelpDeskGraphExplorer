using System.Net.Http.Headers;
using System.Text.Json;

namespace AdefHelpDeskGraphExplorer.Services.AI;

/// <summary>
/// Queries each provider's /models endpoint to discover available models.
/// Falls back to a hard-coded default list per provider on error.
/// </summary>
public sealed class AIModelService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<AIModelService> _logger;
    private readonly AIConfigurationService _configService;

    public AIModelService(HttpClient httpClient, ILogger<AIModelService> logger, AIConfigurationService configService)
    {
        _httpClient = httpClient;
        _logger = logger;
        _configService = configService;
    }

    public IReadOnlyList<string> GetDefaultModels(string providerKey) => providerKey switch
    {
        "OpenAI" => new[] { "gpt-4o-mini", "gpt-4o", "gpt-5-mini", "o4-mini" },
        "AzureOpenAI" => new[] { "gpt-4o", "gpt-4o-mini" },
        "Anthropic" => new[] { "claude-sonnet-4-20250514", "claude-3-5-haiku-latest" },
        "Google AI" or "GoogleAI" => new[] { "gemini-2.5-flash", "gemini-2.5-pro" },
        _ => Array.Empty<string>()
    };

    public Task<List<string>> GetModelsAsync(string providerKey, CancellationToken ct = default)
    {
        var p = _configService.GetProvider(NormalizeProviderKey(providerKey));
        var models = (p.Models is { Count: > 0 } ? p.Models : GetDefaultModels(providerKey).ToList()).ToList();
        return Task.FromResult(models);
    }

    public async Task<List<string>> RefreshModelsAsync(
        string providerKey,
        string? apiKey,
        string? endpoint,
        CancellationToken ct = default)
    {
        try
        {
            var key = NormalizeProviderKey(providerKey);
            return key switch
            {
                "OpenAI" => await FetchOpenAIModelsAsync(apiKey, endpoint, ct),
                "AzureOpenAI" => await FetchAzureModelsAsync(apiKey, endpoint, ct),
                "Anthropic" => await FetchAnthropicModelsAsync(apiKey, endpoint, ct),
                "GoogleAI" => await FetchGoogleModelsAsync(apiKey, endpoint, ct),
                _ => GetDefaultModels(providerKey).ToList()
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh models for {Provider}; using defaults.", providerKey);
            return GetDefaultModels(providerKey).ToList();
        }
    }

    private static string NormalizeProviderKey(string providerKey) => providerKey switch
    {
        "Azure OpenAI" => "AzureOpenAI",
        "Google AI" => "GoogleAI",
        _ => providerKey
    };

    private async Task<List<string>> FetchOpenAIModelsAsync(string? apiKey, string? endpoint, CancellationToken ct)
    {
        var baseUrl = string.IsNullOrWhiteSpace(endpoint) ? "https://api.openai.com/v1" : endpoint!.TrimEnd('/');
        using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/models");
        if (!string.IsNullOrWhiteSpace(apiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var resp = await _httpClient.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("data")
            .EnumerateArray()
            .Select(m => m.GetProperty("id").GetString() ?? "")
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .OrderBy(s => s)
            .ToList();
    }

    private async Task<List<string>> FetchAzureModelsAsync(string? apiKey, string? endpoint, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey))
            return GetDefaultModels("AzureOpenAI").ToList();

        var url = endpoint!.TrimEnd('/') + "/openai/deployments?api-version=2024-10-21";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("api-key", apiKey);
        var resp = await _httpClient.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("data")
            .EnumerateArray()
            .Select(m => m.GetProperty("id").GetString() ?? "")
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .OrderBy(s => s)
            .ToList();
    }

    private async Task<List<string>> FetchAnthropicModelsAsync(string? apiKey, string? endpoint, CancellationToken ct)
    {
        var baseUrl = string.IsNullOrWhiteSpace(endpoint) ? "https://api.anthropic.com" : endpoint!.TrimEnd('/');
        using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/v1/models");
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            req.Headers.Add("x-api-key", apiKey);
            req.Headers.Add("anthropic-version", "2023-06-01");
        }
        var resp = await _httpClient.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("data")
            .EnumerateArray()
            .Select(m => m.GetProperty("id").GetString() ?? "")
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .OrderBy(s => s)
            .ToList();
    }

    private async Task<List<string>> FetchGoogleModelsAsync(string? apiKey, string? endpoint, CancellationToken ct)
    {
        var baseUrl = string.IsNullOrWhiteSpace(endpoint) ? "https://generativelanguage.googleapis.com" : endpoint!.TrimEnd('/');
        var url = $"{baseUrl}/v1beta/models?key={Uri.EscapeDataString(apiKey ?? "")}";
        var resp = await _httpClient.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("models")
            .EnumerateArray()
            .Select(m => m.GetProperty("name").GetString()?.Replace("models/", "") ?? "")
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .OrderBy(s => s)
            .ToList();
    }
}
