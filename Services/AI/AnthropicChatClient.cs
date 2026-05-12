using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace AdefHelpDeskGraphExplorer.Services.AI;

/// <summary>
/// IChatClient implementation that calls the Anthropic REST API directly.
/// Supports native tool calling (tool_use / tool_result content blocks) so the
/// shared <see cref="ChatService"/> tool loop can drive Claude end-to-end.
/// Ported from
/// https://github.com/AIStoryBuilders/AIStoryBuildersOnline/blob/main/AI/AnthropicChatClient.cs
/// </summary>
public class AnthropicChatClient : IChatClient, IDisposable
{
    private readonly string _apiKey;
    private readonly string _modelId;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private const string ApiUrl = "https://api.anthropic.com/v1/messages";

    public AnthropicChatClient(string apiKey, string modelId, HttpClient? httpClient = null)
    {
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _modelId = modelId ?? throw new ArgumentNullException(nameof(modelId));

        if (httpClient != null)
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }
        else
        {
            _httpClient = new HttpClient();
            _ownsHttpClient = true;
        }

        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("x-api-key", _apiKey);
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("anthropic-dangerous-direct-browser-access", "true");
    }

    public ChatClientMetadata Metadata => new ChatClientMetadata("AnthropicChatClient", null, _modelId);

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var systemParts = new List<string>();
        var messages = new List<object>();
        List<object>? pendingToolResults = null;

        void FlushToolResults()
        {
            if (pendingToolResults is { Count: > 0 })
            {
                messages.Add(new { role = "user", content = pendingToolResults.ToArray() });
            }
            pendingToolResults = null;
        }

        foreach (var msg in chatMessages)
        {
            if (msg.Role == ChatRole.System)
            {
                var t = msg.Text;
                if (!string.IsNullOrEmpty(t)) systemParts.Add(t);
            }
            else if (msg.Role == ChatRole.Tool)
            {
                pendingToolResults ??= new List<object>();
                foreach (var c in msg.Contents)
                {
                    if (c is FunctionResultContent fr)
                    {
                        pendingToolResults.Add(new
                        {
                            type = "tool_result",
                            tool_use_id = fr.CallId,
                            content = SerializeToolResult(fr.Result)
                        });
                    }
                }
            }
            else if (msg.Role == ChatRole.User)
            {
                FlushToolResults();
                messages.Add(new { role = "user", content = msg.Text ?? "" });
            }
            else if (msg.Role == ChatRole.Assistant)
            {
                FlushToolResults();
                var blocks = new List<object>();
                foreach (var c in msg.Contents)
                {
                    switch (c)
                    {
                        case TextContent tc when !string.IsNullOrEmpty(tc.Text):
                            blocks.Add(new { type = "text", text = tc.Text });
                            break;
                        case FunctionCallContent fc:
                            blocks.Add(new
                            {
                                type = "tool_use",
                                id = fc.CallId,
                                name = fc.Name,
                                input = ArgumentsToJsonObject(fc.Arguments)
                            });
                            break;
                    }
                }
                if (blocks.Count == 0)
                {
                    var text = msg.Text;
                    if (!string.IsNullOrEmpty(text))
                        messages.Add(new { role = "assistant", content = text });
                }
                else
                {
                    messages.Add(new { role = "assistant", content = blocks.ToArray() });
                }
            }
        }
        FlushToolResults();

        var systemText = string.Join("\n\n", systemParts);

        if (messages.Count == 0 && !string.IsNullOrEmpty(systemText))
        {
            messages.Add(new { role = "user", content = systemText });
            systemText = "";
        }

        var requestBody = new Dictionary<string, object>
        {
            ["model"] = _modelId,
            ["max_tokens"] = 4096,
            ["messages"] = messages
        };

        if (!string.IsNullOrEmpty(systemText))
        {
            requestBody["system"] = systemText;
        }

        if (options?.Temperature.HasValue == true
            && AICapabilities.AnthropicSupportsTemperature(_modelId))
        {
            requestBody["temperature"] = options.Temperature.Value;
        }

        // Native tool calling: forward AIFunction definitions as Anthropic tools.
        if (options?.Tools is { Count: > 0 })
        {
            var anthropicTools = new List<object>();
            foreach (var tool in options.Tools.OfType<AIFunction>())
            {
                var schemaJson = tool.JsonSchema.GetRawText();
                anthropicTools.Add(new
                {
                    name = tool.Name,
                    description = tool.Description ?? string.Empty,
                    input_schema = JsonSerializer.Deserialize<JsonElement>(schemaJson)
                });
            }
            if (anthropicTools.Count > 0)
            {
                requestBody["tools"] = anthropicTools;
            }
        }

        var json = JsonSerializer.Serialize(requestBody);
        var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        var httpResponse = await _httpClient.SendAsync(request, cancellationToken);
        var responseJson = await httpResponse.Content.ReadAsStringAsync(cancellationToken);

        if (!httpResponse.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Anthropic API error ({httpResponse.StatusCode}): {responseJson}");
        }

        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        var responseContents = new List<AIContent>();
        if (root.TryGetProperty("content", out var contentArray))
        {
            foreach (var block in contentArray.EnumerateArray())
            {
                if (!block.TryGetProperty("type", out var typeEl)) continue;
                var blockType = typeEl.GetString();
                if (blockType == "text" && block.TryGetProperty("text", out var textEl))
                {
                    var text = textEl.GetString();
                    if (!string.IsNullOrEmpty(text))
                        responseContents.Add(new TextContent(text));
                }
                else if (blockType == "tool_use")
                {
                    var id = block.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? string.Empty : string.Empty;
                    var name = block.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? string.Empty : string.Empty;
                    IDictionary<string, object?> args = new Dictionary<string, object?>();
                    if (block.TryGetProperty("input", out var inputEl)
                        && inputEl.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in inputEl.EnumerateObject())
                        {
                            args[prop.Name] = JsonElementToObject(prop.Value);
                        }
                    }
                    responseContents.Add(new FunctionCallContent(id, name, args));
                }
            }
        }

        if (responseContents.Count == 0)
        {
            responseContents.Add(new TextContent(string.Empty));
        }

        var responseMessage = new ChatMessage(ChatRole.Assistant, responseContents);
        var chatResponse = new ChatResponse(responseMessage);

        if (root.TryGetProperty("usage", out var usage))
        {
            var inputTokens = usage.TryGetProperty("input_tokens", out var inp) ? inp.GetInt32() : 0;
            var outputTokens = usage.TryGetProperty("output_tokens", out var outp) ? outp.GetInt32() : 0;
            chatResponse.Usage = new UsageDetails
            {
                InputTokenCount = inputTokens,
                OutputTokenCount = outputTokens,
                TotalTokenCount = inputTokens + outputTokens
            };
        }

        return chatResponse;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // We don't stream from the wire today; fall back to a single non-streamed
        // call and replay its content as one update. Tool calls are preserved on
        // the update so callers routing the streaming API through this path still
        // see FunctionCallContent.
        var response = await GetResponseAsync(chatMessages, options, cancellationToken);
        if (response.Messages.Count == 0)
        {
            yield break;
        }
        var msg = response.Messages[0];
        yield return new ChatResponseUpdate(ChatRole.Assistant, msg.Contents.ToList());
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceType == typeof(IChatClient)) return this;
        return null;
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient?.Dispose();
        }
    }

    private static string SerializeToolResult(object? result)
    {
        if (result is null) return string.Empty;
        if (result is string s) return s;
        if (result is JsonElement je) return je.GetRawText();
        try
        {
            return JsonSerializer.Serialize(result);
        }
        catch
        {
            return result.ToString() ?? string.Empty;
        }
    }

    /// <summary>
    /// Anthropic's <c>tool_use.input</c> must be a JSON object. MEAI hands us
    /// arguments as <see cref="IDictionary{TKey, TValue}"/> of CLR values (often
    /// <see cref="JsonElement"/>). Re-serialise through JSON so embedded
    /// JsonElements round-trip cleanly when we embed them in the request body.
    /// </summary>
    private static JsonElement ArgumentsToJsonObject(IDictionary<string, object?>? args)
    {
        var json = args is null || args.Count == 0
            ? "{}"
            : JsonSerializer.Serialize(args);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Convert a JSON value coming back from Anthropic's <c>tool_use.input</c>
    /// to a CLR object suitable for <see cref="AIFunctionArguments"/>. Objects
    /// and arrays are kept as <see cref="JsonElement"/> so MEAI's argument
    /// binder can deserialize them into the target parameter type.
    /// </summary>
    private static object? JsonElementToObject(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Number => el.TryGetInt64(out var l) ? l
            : el.TryGetDouble(out var d) ? d
            : (object)el.GetRawText(),
        _ => el.Clone(),
    };
}
