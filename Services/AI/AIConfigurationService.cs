using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using AdefHelpDeskGraphExplorer.Models;

namespace AdefHelpDeskGraphExplorer.Services.AI;

/// <summary>
/// Loads, validates, and persists the AI configuration section of appsettings.json.
/// In Development, writes back into appsettings.Development.json so secrets are not
/// committed to source control. In Production, writes to appsettings.User.json.
/// </summary>
public sealed class AIConfigurationService
{
    private readonly IHostEnvironment _env;
    private readonly IConfigurationRoot _configuration;
    private readonly ILogger<AIConfigurationService> _logger;
    private readonly object _writeLock = new();

    public AIConfigurationService(
        IHostEnvironment env,
        IConfiguration configuration,
        ILogger<AIConfigurationService> logger)
    {
        _env = env;
        _configuration = (IConfigurationRoot)configuration;
        _logger = logger;
    }

    private string WritablePath => _env.IsDevelopment()
        ? Path.Combine(_env.ContentRootPath, "appsettings.Development.json")
        : Path.Combine(_env.ContentRootPath, "appsettings.User.json");

    public AIOptions GetOptions()
    {
        var opts = new AIOptions();
        _configuration.GetSection(AIOptions.SectionName).Bind(opts);
        return opts;
    }

    public ProviderOptions GetProvider(string providerKey)
    {
        var opts = GetOptions();
        if (!opts.Providers.TryGetValue(providerKey, out var p))
        {
            p = new ProviderOptions();
            opts.Providers[providerKey] = p;
        }
        return p;
    }

    public string GetActiveProvider() => GetOptions().ActiveProvider;

    public async Task SaveSettingsAsync(
        string providerKey,
        string? apiKey,
        string? defaultModel,
        string? endpoint = null,
        string? apiVersion = null,
        string? deploymentName = null,
        bool? enabled = null,
        bool setActive = true,
        CancellationToken cancellationToken = default)
    {
        var path = WritablePath;
        JsonNode root;

        lock (_writeLock)
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                root = JsonNode.Parse(json, new JsonNodeOptions { PropertyNameCaseInsensitive = false })
                       ?? new JsonObject();
            }
            else
            {
                root = new JsonObject();
            }

            var ai = root["AI"] as JsonObject ?? new JsonObject();
            root["AI"] = ai;

            if (setActive)
            {
                ai["ActiveProvider"] = providerKey;
            }

            var providers = ai["Providers"] as JsonObject ?? new JsonObject();
            ai["Providers"] = providers;

            var prov = providers[providerKey] as JsonObject ?? new JsonObject();
            providers[providerKey] = prov;

            if (apiKey is not null) prov["ApiKey"] = apiKey;
            if (endpoint is not null) prov["Endpoint"] = endpoint;
            if (apiVersion is not null) prov["ApiVersion"] = apiVersion;
            if (deploymentName is not null) prov["DeploymentName"] = deploymentName;
            if (defaultModel is not null)
            {
                if (string.Equals(providerKey, "AzureOpenAI", StringComparison.OrdinalIgnoreCase))
                {
                    prov["DeploymentName"] = defaultModel;
                }
                else
                {
                    prov["DefaultModel"] = defaultModel;
                }
            }
            if (enabled is not null) prov["Enabled"] = enabled.Value;

            var serialized = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, serialized);
        }

        try
        {
            _configuration.Reload();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to reload configuration after save.");
        }

        await Task.CompletedTask;
    }

    public IEnumerable<string> EnabledProviderKeys()
        => GetOptions().Providers.Where(p => p.Value.Enabled).Select(p => p.Key);

    public string ResolveModel(string providerKey)
    {
        var p = GetProvider(providerKey);
        if (string.Equals(providerKey, "AzureOpenAI", StringComparison.OrdinalIgnoreCase))
        {
            return p.DeploymentName ?? string.Empty;
        }
        return p.DefaultModel ?? string.Empty;
    }
}
