using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace AdefHelpDeskGraphExplorer.Services.AI;

/// <summary>
/// IChatClient implementation that calls the Gemini REST endpoint directly.
/// Ported from
/// https://github.com/AIStoryBuilders/AIStoryBuildersOnline/blob/main/AI/GoogleAIChatClient.cs
/// </summary>
public class GoogleAIChatClient : IChatClient, IDisposable
{
    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta";

    private readonly string _apiKey;
    private readonly string _modelId;
    private readonly HttpClient _httpClient;

    public GoogleAIChatClient(string apiKey, string modelId, HttpClient httpClient)
    {
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _modelId = modelId ?? throw new ArgumentNullException(nameof(modelId));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public ChatClientMetadata Metadata => new ChatClientMetadata("GoogleAIChatClient", null, _modelId);

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var body = BuildRequestBody(chatMessages, options);
        var url = $"{BaseUrl}/models/{Uri.EscapeDataString(_modelId)}:generateContent?key={Uri.EscapeDataString(_apiKey)}";

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Gemini generateContent failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}");
        }

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        if (!root.TryGetProperty("candidates", out var candidates)
            || candidates.ValueKind != JsonValueKind.Array
            || candidates.GetArrayLength() == 0)
        {
            string? blockReason = null;
            if (root.TryGetProperty("promptFeedback", out var pf)
                && pf.TryGetProperty("blockReason", out var br)
                && br.ValueKind == JsonValueKind.String)
            {
                blockReason = br.GetString();
            }
            throw new InvalidOperationException(
                "Gemini returned no candidates" + (blockReason != null ? $" (blockReason={blockReason})" : "") + ".");
        }

        var responseContents = new List<AIContent>();
        var sb = new StringBuilder();
        var first = candidates[0];
        if (first.TryGetProperty("content", out var content)
            && content.TryGetProperty("parts", out var parts)
            && parts.ValueKind == JsonValueKind.Array)
        {
            int callIndex = 0;
            foreach (var part in parts.EnumerateArray())
            {
                // Skip "thinking" text parts (thought: true) — they're internal
                // chain-of-thought and shouldn't be surfaced as assistant text.
                var isThought = part.TryGetProperty("thought", out var thoughtEl)
                    && thoughtEl.ValueKind == JsonValueKind.True;

                if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                {
                    if (isThought) continue;
                    var s = text.GetString();
                    if (!string.IsNullOrEmpty(s))
                    {
                        sb.Append(s);
                    }
                }
                else if (part.TryGetProperty("functionCall", out var fc)
                    && fc.ValueKind == JsonValueKind.Object)
                {
                    var name = fc.TryGetProperty("name", out var nameEl)
                        ? nameEl.GetString() ?? string.Empty
                        : string.Empty;

                    IDictionary<string, object?> args = new Dictionary<string, object?>();
                    if (fc.TryGetProperty("args", out var argsEl)
                        && argsEl.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in argsEl.EnumerateObject())
                        {
                            args[prop.Name] = JsonElementToObject(prop.Value);
                        }
                    }

                    // Gemini doesn't return tool call IDs; synthesize one that's
                    // stable per response so we can correlate functionResponse
                    // parts when the caller sends results back.
                    var callId = $"call_{name}_{callIndex++}";
                    var fcc = new FunctionCallContent(callId, name, args);

                    // Gemini 2.5+/3.x thinking models attach a `thoughtSignature`
                    // to functionCall parts and reject the follow-up request if
                    // it isn't echoed back. Stash it on AdditionalProperties so
                    // BuildRequestBody can replay it.
                    if (part.TryGetProperty("thoughtSignature", out var sigEl)
                        && sigEl.ValueKind == JsonValueKind.String)
                    {
                        var sig = sigEl.GetString();
                        if (!string.IsNullOrEmpty(sig))
                        {
                            fcc.AdditionalProperties ??= new AdditionalPropertiesDictionary();
                            fcc.AdditionalProperties["thoughtSignature"] = sig;
                        }
                    }

                    responseContents.Add(fcc);
                }
            }
        }

        if (sb.Length > 0)
        {
            responseContents.Insert(0, new TextContent(sb.ToString()));
        }
        if (responseContents.Count == 0)
        {
            responseContents.Add(new TextContent(string.Empty));
        }

        var chatResponse = new ChatResponse(new ChatMessage(ChatRole.Assistant, responseContents));

        if (root.TryGetProperty("usageMetadata", out var usage))
        {
            chatResponse.Usage = new UsageDetails
            {
                InputTokenCount = GetLongOrZero(usage, "promptTokenCount"),
                OutputTokenCount = GetLongOrZero(usage, "candidatesTokenCount"),
                TotalTokenCount = GetLongOrZero(usage, "totalTokenCount")
            };
        }

        return chatResponse;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // We don't stream from the wire; fall back to a single non-streamed call
        // and replay its content as one update. Tool calls are preserved on the
        // update so callers routing the streaming API through this path still
        // see FunctionCallContent.
        var full = await GetResponseAsync(chatMessages, options, cancellationToken);
        if (full.Messages.Count == 0)
        {
            yield break;
        }
        var msg = full.Messages[0];
        yield return new ChatResponseUpdate(ChatRole.Assistant, msg.Contents.ToList());
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        if (serviceType == typeof(IChatClient)) return this;
        return null;
    }

    public void Dispose()
    {
        // HttpClient is owned by DI / caller.
    }

    private object BuildRequestBody(IEnumerable<ChatMessage> chatMessages, ChatOptions? options)
    {
        string? systemInstruction = null;
        var contents = new List<object>();

        // Track tool-call IDs -> tool name across the conversation so we can
        // attach the correct `name` to each functionResponse part (Gemini's
        // wire format keys responses by name, not by id).
        var callIdToName = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var msg in chatMessages)
        {
            if (msg.Role == ChatRole.System)
            {
                var t = msg.Text ?? string.Empty;
                if (string.IsNullOrEmpty(t)) continue;
                systemInstruction = string.IsNullOrEmpty(systemInstruction)
                    ? t
                    : systemInstruction + "\n\n" + t;
            }
            else if (msg.Role == ChatRole.Tool)
            {
                var parts = new List<object>();
                foreach (var c in msg.Contents)
                {
                    if (c is FunctionResultContent fr)
                    {
                        var name = callIdToName.TryGetValue(fr.CallId, out var n) ? n : fr.CallId;
                        // Gemini's functionResponse.response must be a JSON object.
                        // If the tool result is already an object, pass it through;
                        // otherwise wrap primitives/arrays in { result: ... }.
                        parts.Add(new
                        {
                            functionResponse = new
                            {
                                name,
                                response = SerializeToolResultAsObject(fr.Result)
                            }
                        });
                    }
                }
                if (parts.Count > 0)
                {
                    // Gemini expects functionResponse parts on a "user" turn.
                    contents.Add(new { role = "user", parts = parts.ToArray() });
                }
            }
            else if (msg.Role == ChatRole.Assistant)
            {
                var parts = new List<object>();
                foreach (var c in msg.Contents)
                {
                    switch (c)
                    {
                        case TextContent tc when !string.IsNullOrEmpty(tc.Text):
                            parts.Add(new { text = tc.Text });
                            break;
                        case FunctionCallContent fc:
                            callIdToName[fc.CallId] = fc.Name;

                            // Replay the per-part thoughtSignature captured from
                            // the response — Gemini 2.5+/3.x thinking models
                            // require it on every functionCall echoed back.
                            string? thoughtSignature = null;
                            if (fc.AdditionalProperties is not null
                                && fc.AdditionalProperties.TryGetValue("thoughtSignature", out var sigObj)
                                && sigObj is string sigStr
                                && !string.IsNullOrEmpty(sigStr))
                            {
                                thoughtSignature = sigStr;
                            }

                            var callDict = new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["name"] = fc.Name
                            };
                            // Omit args entirely when empty — some Gemini model
                            // versions reject an empty args struct.
                            if (fc.Arguments is { Count: > 0 })
                            {
                                callDict["args"] = ArgumentsToJsonElement(fc.Arguments);
                            }

                            var partDict = new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["functionCall"] = callDict
                            };
                            if (thoughtSignature is not null)
                            {
                                partDict["thoughtSignature"] = thoughtSignature;
                            }
                            parts.Add(partDict);
                            break;
                    }
                }
                if (parts.Count == 0)
                {
                    var text = msg.Text;
                    if (!string.IsNullOrEmpty(text))
                    {
                        parts.Add(new { text });
                    }
                    else
                    {
                        continue;
                    }
                }
                contents.Add(new { role = "model", parts = parts.ToArray() });
            }
            else // User (and any other non-tool roles)
            {
                var text = msg.Text ?? string.Empty;
                contents.Add(new
                {
                    role = "user",
                    parts = new[] { new { text } }
                });
            }
        }

        var generationConfig = new Dictionary<string, object>();
        if (options?.Temperature.HasValue == true && AICapabilities.SupportsCustomTemperature(_modelId))
            generationConfig["temperature"] = (float)options.Temperature.Value;
        if (options?.TopP.HasValue == true)
            generationConfig["topP"] = (float)options.TopP.Value;
        if (options?.ResponseFormat is ChatResponseFormatJson)
            generationConfig["responseMimeType"] = "application/json";

        if (contents.Count == 0)
        {
            var seed = string.IsNullOrEmpty(systemInstruction) ? "Hello." : systemInstruction;
            contents.Add(new
            {
                role = "user",
                parts = new[] { new { text = seed } }
            });
            if (!string.IsNullOrEmpty(systemInstruction))
            {
                systemInstruction = null;
            }
        }

        var body = new Dictionary<string, object>
        {
            ["contents"] = contents
        };
        if (!string.IsNullOrEmpty(systemInstruction))
        {
            body["systemInstruction"] = new
            {
                parts = new[] { new { text = systemInstruction } }
            };
        }
        if (generationConfig.Count > 0)
        {
            body["generationConfig"] = generationConfig;
        }

        // Native tool calling: forward AIFunction definitions as Gemini
        // functionDeclarations. Gemini accepts an OpenAPI-subset schema for the
        // parameters block, so we strip JSON Schema keywords it rejects.
        if (options?.Tools is { Count: > 0 })
        {
            var declarations = new List<object>();
            foreach (var tool in options.Tools.OfType<AIFunction>())
            {
                var schemaJson = tool.JsonSchema.GetRawText();
                using var schemaDoc = JsonDocument.Parse(schemaJson);
                var sanitized = SanitizeGeminiSchema(schemaDoc.RootElement);
                var decl = new Dictionary<string, object>
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description ?? string.Empty
                };
                if (sanitized is not null)
                {
                    decl["parameters"] = sanitized;
                }
                declarations.Add(decl);
            }
            if (declarations.Count > 0)
            {
                body["tools"] = new object[]
                {
                    new { functionDeclarations = declarations }
                };
            }
        }

        return body;
    }

    /// <summary>
    /// Coerce a tool result into a JSON object suitable for Gemini's
    /// <c>functionResponse.response</c> field (which must be a Struct).
    /// Objects pass through; primitives, strings, and arrays are wrapped in
    /// <c>{ result: ... }</c>.
    /// </summary>
    private static object SerializeToolResultAsObject(object? result)
    {
        if (result is null)
        {
            return new Dictionary<string, object?> { ["result"] = null };
        }
        if (result is string s)
        {
            return new Dictionary<string, object?> { ["result"] = s };
        }
        if (result is JsonElement je)
        {
            return je.ValueKind == JsonValueKind.Object
                ? (object)je
                : new Dictionary<string, object?> { ["result"] = je };
        }
        try
        {
            var json = JsonSerializer.Serialize(result);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement.Clone();
            return root.ValueKind == JsonValueKind.Object
                ? (object)root
                : new Dictionary<string, object?> { ["result"] = root };
        }
        catch
        {
            return new Dictionary<string, object?> { ["result"] = result.ToString() ?? string.Empty };
        }
    }

    private static JsonElement ArgumentsToJsonElement(IDictionary<string, object?>? args)
    {
        var json = args is null || args.Count == 0
            ? "{}"
            : JsonSerializer.Serialize(args);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static object? JsonElementToObject(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Number => el.TryGetInt64(out var l) ? l
            : el.TryGetDouble(out var d) ? d
            : (object)el.GetRawText(),
        JsonValueKind.Object => el.Clone(),
        JsonValueKind.Array => el.Clone(),
        _ => el.GetRawText()
    };

    /// <summary>
    /// Strip JSON Schema keywords Gemini's functionDeclarations parameters
    /// schema doesn't accept (e.g. <c>$schema</c>, <c>additionalProperties</c>,
    /// <c>$defs</c>, <c>definitions</c>). Returns null for non-object schemas.
    /// </summary>
    private static Dictionary<string, object?>? SanitizeGeminiSchema(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object) return null;
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var prop in schema.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "$schema":
                case "$id":
                case "$defs":
                case "definitions":
                case "additionalProperties":
                case "patternProperties":
                case "unevaluatedProperties":
                    continue;
                case "properties":
                    if (prop.Value.ValueKind == JsonValueKind.Object)
                    {
                        var props = new Dictionary<string, object?>(StringComparer.Ordinal);
                        foreach (var sub in prop.Value.EnumerateObject())
                        {
                            var child = SanitizeGeminiSchema(sub.Value);
                            if (child is not null) props[sub.Name] = child;
                        }
                        result["properties"] = props;
                    }
                    break;
                case "items":
                    if (prop.Value.ValueKind == JsonValueKind.Object)
                    {
                        var child = SanitizeGeminiSchema(prop.Value);
                        if (child is not null) result["items"] = child;
                    }
                    break;
                case "type":
                    // Gemini doesn't accept nullable type arrays like ["string","null"];
                    // collapse to the first non-null type.
                    if (prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var t in prop.Value.EnumerateArray())
                        {
                            if (t.ValueKind == JsonValueKind.String
                                && !string.Equals(t.GetString(), "null", StringComparison.Ordinal))
                            {
                                result["type"] = t.GetString();
                                break;
                            }
                        }
                    }
                    else if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        result["type"] = prop.Value.GetString();
                    }
                    break;
                default:
                    result[prop.Name] = JsonElementToObject(prop.Value);
                    break;
            }
        }
        // Gemini rejects empty-object schemas; ensure type=object when there are properties.
        if (!result.ContainsKey("type") && result.ContainsKey("properties"))
        {
            result["type"] = "object";
        }
        return result;
    }

    private static long GetLongOrZero(JsonElement obj, string name)
    {
        if (obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number)
        {
            if (v.TryGetInt64(out var l)) return l;
        }
        return 0;
    }
}
