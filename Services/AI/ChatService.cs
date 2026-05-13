using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using AdefHelpDeskGraphExplorer.Models;
using AdefHelpDeskGraphExplorer.Services.AI.GraphTools;
using AdefHelpDeskGraphExplorer.Services.Graph;using MEAIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using MEAIChatRole = Microsoft.Extensions.AI.ChatRole;

namespace AdefHelpDeskGraphExplorer.Services.AI;

/// <summary>
/// Stateless chat orchestrator. Streams tokens from the active <see cref="IChatClient"/>.
/// When the active provider supports tool calling, runs a hand-rolled tool-call loop over
/// <see cref="IGraphChatTools"/> until the model returns a tool-free final answer.
/// </summary>
public sealed class ChatService
{
    private readonly ChatClientFactory _factory;
    private readonly IOptionsMonitor<AIOptions> _options;
    private readonly IGraphChatTools _graphTools;
    private readonly GraphQueryService _graphQuery;
    private readonly ILogger<ChatService> _logger;

    public ChatService(
        ChatClientFactory factory,
        IOptionsMonitor<AIOptions> options,
        IGraphChatTools graphTools,
        GraphQueryService graphQuery,
        ILogger<ChatService> logger)
    {
        _factory = factory;
        _options = options;
        _graphTools = graphTools;
        _graphQuery = graphQuery;
        _logger = logger;
    }

    /// <summary>
    /// Streams the assistant's response. Pass <paramref name="onToolCall"/> to receive a
    /// callback for every tool invocation (used by the UI to render tool cards).
    /// </summary>
    public async IAsyncEnumerable<string> StreamAsync(
        IEnumerable<ChatTurn> history,
        string? providerKeyOverride = null,
        string? modelOverride = null,
        Func<ToolInvocation, Task>? onToolCall = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var snapshot = _options.CurrentValue;
        var defaults = snapshot.Defaults;
        var providerKey = providerKeyOverride ?? snapshot.ActiveProvider;

        var (client, defaultModel) = _factory.Create(providerKey);
        using var _ = client;

        var effectiveModel = string.IsNullOrWhiteSpace(modelOverride) ? defaultModel : modelOverride;
        var toolsEnabled = snapshot.Tools.Enabled
            && AICapabilities.SupportsToolCalling(providerKey, effectiveModel);

        var systemPrompt = BuildSystemPrompt(defaults.SystemPrompt, toolsEnabled);

        // For providers without tool calling, precompute a grounding block to
        // attach to the system prompt. We surface, in order:
        //   - graph stats (always)
        //   - resolved UserActivity blocks for every User the prompt names
        //   - resolved TaskParticipant blocks for every task:N referenced
        //   - a small keyword-matched excerpt
        // This is the no-tools analogue of the tool path: the answer surface is the
        // same, just precomputed. We MERGE it into the single system prompt to keep
        // every provider client (which may collapse multiple system messages)
        // behaving identically.
        string? groundingBlock = null;
        if (!toolsEnabled)
        {
            var lastUser = history.LastOrDefault(m => m.Role == ChatTurnRole.User)?.Content;

            string statsJson = "{}";
            try
            {
                var stats = _graphTools.Stats();
                statsJson = JsonSerializer.Serialize(stats);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to compute graph stats for fallback grounding.");
            }

            // Full requester roster (registered + unregistered, ranked by task count).
            // Surfaced unconditionally because requester questions are common and the
            // keyword excerpt only captures requesters whose name happens to match.
            string requestersJson = "[]";
            int requesterCount = 0;
            try
            {
                var requesters = _graphTools.ListRequesters(null, GraphChatTools.HardMaxCap);
                requesterCount = requesters.Length;
                requestersJson = JsonSerializer.Serialize(requesters);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to compute requester roster for fallback grounding.");
            }

            var excerpt = !string.IsNullOrWhiteSpace(lastUser)
                ? _graphQuery.BuildContext(scope: null, userPrompt: lastUser)
                : null;

            string? usersOfInterestJson = null;
            string? tasksOfInterestJson = null;
            int userCount = 0, taskCount = 0;
            try
            {
                // Look at the last 2 user turns so a follow-up like "Michael Washington"
                // still resolves names from the original question.
                var recent = string.Join(" ",
                    history.Where(m => m.Role == ChatTurnRole.User)
                           .TakeLast(2)
                           .Select(m => m.Content ?? string.Empty));
                if (!string.IsNullOrWhiteSpace(recent))
                {
                    var (users, tasks) = ResolveEntitiesOfInterest(recent);
                    userCount = users.Count;
                    taskCount = tasks.Count;
                    if (users.Count > 0)
                        usersOfInterestJson = JsonSerializer.Serialize(users);
                    if (tasks.Count > 0)
                        tasksOfInterestJson = JsonSerializer.Serialize(tasks);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to resolve entities of interest for fallback grounding.");
            }

            _logger.LogInformation(
                "No-tools grounding: usersOfInterest={Users} tasksOfInterest={Tasks} requesters={Requesters} hasExcerpt={Excerpt}",
                userCount, taskCount, requesterCount, !string.IsNullOrWhiteSpace(excerpt));

            var sb = new System.Text.StringBuilder();
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine("=== GROUNDING DATA (your data source — read this first) ===");
            sb.AppendLine();
            sb.Append("Graph stats (JSON): ").AppendLine(statsJson);
            sb.AppendLine();
            sb.AppendLine("All requesters in the graph — ranked by task count, DESC (JSON).");
            sb.AppendLine("Each row has: id (user:N or null), userId, name, username, email, isRegistered, taskCount, tasksByStatus, sampleTaskIds. Unregistered requesters (free-text requesterName, no User node) have id=null and isRegistered=false. This is the AUTHORITATIVE answer for 'who requested tasks?', 'list requesters', 'top requester', or any ranking by requested-task-count.");
            sb.AppendLine(requestersJson);
            if (!string.IsNullOrEmpty(usersOfInterestJson))
            {
                sb.AppendLine();
                sb.AppendLine("Users matched from the prompt — full activity rollups (JSON).");
                sb.AppendLine("Each entry has: id, userId, displayName, requestedTaskCount, commentedTaskCount, workedOnTaskCount, and task-id arrays. workedOnTaskCount is the answer to 'how many tasks did this person work on'.");
                sb.AppendLine(usersOfInterestJson);
            }
            if (!string.IsNullOrEmpty(tasksOfInterestJson))
            {
                sb.AppendLine();
                sb.AppendLine("Tasks referenced in the prompt — participants (JSON):");
                sb.AppendLine(tasksOfInterestJson);
            }
            if (!string.IsNullOrWhiteSpace(excerpt))
            {
                sb.AppendLine();
                sb.Append("Keyword-matched subgraph excerpt (JSON): ").AppendLine(excerpt);
            }
            sb.AppendLine();
            sb.AppendLine("=== END GROUNDING DATA ===");
            groundingBlock = sb.ToString();
        }

        var combinedSystem = string.IsNullOrEmpty(groundingBlock)
            ? systemPrompt
            : (systemPrompt ?? string.Empty) + groundingBlock;

        var messages = new List<MEAIChatMessage>();
        if (!history.Any(m => m.Role == ChatTurnRole.System) && !string.IsNullOrWhiteSpace(combinedSystem))
        {
            messages.Add(new MEAIChatMessage(MEAIChatRole.System, combinedSystem));
        }

        foreach (var m in history.Where(m => m.Role != ChatTurnRole.Tool))
        {
            var role = m.Role switch
            {
                ChatTurnRole.User => MEAIChatRole.User,
                ChatTurnRole.Assistant => MEAIChatRole.Assistant,
                _ => MEAIChatRole.System
            };
            messages.Add(new MEAIChatMessage(role, m.Content));
        }

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

        if (!toolsEnabled)
        {
            // Plain streaming path (legacy behaviour).
            await foreach (var update in client.GetStreamingResponseAsync(messages, chatOptions, ct))
            {
                if (!string.IsNullOrEmpty(update.Text))
                    yield return update.Text;
            }
            yield break;
        }

        // Tool-calling loop: alternate non-streamed GetResponseAsync turns (which may
        // produce tool calls) with one final streamed call (no tools) when the model
        // is done — that keeps the existing token-streaming UX for the final answer.
        var tools = GraphToolRegistration.BuildTools(_graphTools);
        chatOptions.Tools = tools;

        var maxRounds = Math.Max(1, snapshot.Tools.MaxCallsPerTurn);
        var retryWithoutTools = false;
        for (var round = 0; round < maxRounds; round++)
        {
            ct.ThrowIfCancellationRequested();

            ChatResponse? response = null;
            Exception? roundError = null;
            try
            {
                response = await client.GetResponseAsync(messages, chatOptions, ct);
            }
            catch (Exception ex) when (round == 0)
            {
                _logger.LogWarning(ex, "Tool-enabled chat round failed; retrying once without tools.");
                retryWithoutTools = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Tool-enabled chat round {Round} failed.", round);
                roundError = ex;
            }

            if (roundError is not null)
            {
                yield return $"\n\n[Provider error: {roundError.Message}]";
                yield break;
            }

            if (retryWithoutTools)
            {
                chatOptions.Tools = null;
                await foreach (var update in client.GetStreamingResponseAsync(messages, chatOptions, ct))
                {
                    if (!string.IsNullOrEmpty(update.Text))
                        yield return update.Text;
                }
                yield break;
            }

            var lastMessage = response!.Messages.Count > 0 ? response.Messages[^1] : null;
            var toolCalls = lastMessage?.Contents.OfType<FunctionCallContent>().ToList()
                ?? new List<FunctionCallContent>();

            if (toolCalls.Count == 0)
            {
                var text = response.Text ?? string.Empty;
                if (!string.IsNullOrEmpty(text))
                    yield return text;
                yield break;
            }

            messages.Add(lastMessage!);

            foreach (var call in toolCalls)
            {
                ct.ThrowIfCancellationRequested();
                var (result, summary) = await DispatchToolCallAsync(call, tools, ct);

                if (onToolCall is not null)
                {
                    var args = call.Arguments is null
                        ? "{}"
                        : JsonSerializer.Serialize(call.Arguments);
                    await onToolCall(new ToolInvocation(call.Name, args, summary));
                }

                var toolMsg = new MEAIChatMessage(MEAIChatRole.Tool, new List<AIContent>
                {
                    new FunctionResultContent(call.CallId, result)
                });
                messages.Add(toolMsg);
            }
        }

        // Round cap exhausted — one last call without tools.
        chatOptions.Tools = null;
        _logger.LogWarning("Chat tool loop hit MaxCallsPerTurn={Max}; finalising without tools.", maxRounds);
        await foreach (var update in client.GetStreamingResponseAsync(messages, chatOptions, ct))
        {
            if (!string.IsNullOrEmpty(update.Text))
                yield return update.Text;
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

    private static string BuildSystemPrompt(string basePrompt, bool toolsEnabled)
    {
        if (!toolsEnabled)
        {
            var groundedHint = "\n\nYou are answering questions about a help-desk knowledge graph "
                + "(Tasks/tickets, TaskDetails / Comments, Users, Categories). The next system "
                + "message contains:\n"
                + "  • a JSON summary of the graph (counts by node and edge type),\n"
                + "  • the FULL list of requesters ranked by task count (registered + "
                + "unregistered, every requester in the graph — not a sample),\n"
                + "  • for any user named in the question, a precomputed UserActivity block "
                + "with requestedTaskCount, commentedTaskCount and workedOnTaskCount (the union),\n"
                + "  • for any task:N referenced, the list of participants and their roles,\n"
                + "  • a small keyword-matched excerpt.\n\n"
                + "Grounding rules (must follow):\n"
                + "  1. Never say 'I don't have the data' or 'the summary doesn't include this' — "
                + "the JSON above IS your data source. If the answer requires a number, read it "
                + "from the relevant UserActivity / requesters / stats block.\n"
                + "  2. 'Worked on' a task = requested it OR is assigned to it OR commented on it. "
                + "Use workedOnTaskCount.\n"
                + "  3. For 'who requested tasks?', 'list requesters', 'rank requesters', or any "
                + "ranking by requested-task-count, read EVERY row of the requesters JSON — it is "
                + "already sorted by taskCount DESC. Do not fall back to the keyword excerpt for "
                + "requester questions.\n"
                + "  4. Cite node IDs you used (e.g. 'user:7', 'task:42').\n"
                + "  5. Only when no user/task in the grounding matches the name in the prompt may "
                + "you ask the user to clarify the name.";
            return (basePrompt ?? string.Empty) + groundedHint;
        }
        var toolHint = "\n\nYou are answering questions about a help-desk knowledge graph "
            + "(Tasks/tickets, TaskDetails / Comments, Users, Categories). You have read-only "
            + "tools listed in the function schema.\n\n"
            + "Grounding rules (must follow):\n"
            + "  1. Never answer aggregate questions ('how many', 'which', 'who') from memory — "
            + "always call a tool first. If the user disputes a number you stated, re-call the "
            + "tool before conceding.\n"
            + "  2. Never reply 'I don't have that data' or 'the summary doesn't include this'. "
            + "If you don't know, you haven't called the right tool yet.\n"
            + "  3. For 'how many tasks did <person> work on?': call FindUserByName, then "
            + "CountTasksForUser(userId, role: 'any') — or GetUserActivity(userId) if you want "
            + "the breakdown. 'Worked on' = requested OR assigned OR commented.\n"
            + "  4. For 'which users worked on task N?': call GetTaskParticipants(taskId).\n"
            + "  5. For 'how many tickets in <category>?': call GetCategoryRollup.\n"
            + "  6. ListTasksForUser and ListTasksInCategory return capped lists — never quote "
            + "their length as a total count. Use Count* / *Rollup tools for totals.\n"
            + "  7. Never invent task descriptions or comment text — call GetNode or "
            + "ListCommentsForTask first. Cite node IDs (e.g. 'task:42') in your answer.";
        return (basePrompt ?? string.Empty) + toolHint;
    }

    /// <summary>
    /// Resolve "users / tasks of interest" from the user's free-text prompt for the
    /// non-tool-calling grounding path. Splits the prompt into candidate name tokens
    /// and bigrams, runs them through <see cref="IGraphChatTools.FindUserByName"/>,
    /// and also picks up any explicit <c>task:N</c> / <c>#N</c> references.
    /// </summary>
    private (List<UserActivity> Users, List<object> Tasks) ResolveEntitiesOfInterest(string prompt)
    {
        var users = new Dictionary<int, UserActivity>();
        var tasks = new Dictionary<int, object>();

        var raw = (prompt ?? string.Empty)
            .Split(new[] { ' ', '\t', '\n', '\r', ',', '.', '?', '!', ':', ';', '"', '\'', '(', ')' },
                StringSplitOptions.RemoveEmptyEntries);

        // Word tokens >2 chars + adjacent bigrams (catches "Michael Washington").
        var candidates = new List<string>();
        for (var i = 0; i < raw.Length; i++)
        {
            var w = raw[i];
            if (w.Length > 2) candidates.Add(w);
            if (i + 1 < raw.Length && w.Length > 1 && raw[i + 1].Length > 1)
                candidates.Add(w + " " + raw[i + 1]);
        }

        foreach (var c in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // task:N or #N references → participants
            if (c.StartsWith("task:", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(c.AsSpan(5), out var tid1))
            {
                if (!tasks.ContainsKey(tid1))
                {
                    var parts = _graphTools.GetTaskParticipants(tid1);
                    tasks[tid1] = new { taskId = tid1, participants = parts };
                }
                continue;
            }
            if (c.StartsWith("#", StringComparison.Ordinal)
                && int.TryParse(c.AsSpan(1), out var tid2))
            {
                if (!tasks.ContainsKey(tid2))
                {
                    var parts = _graphTools.GetTaskParticipants(tid2);
                    tasks[tid2] = new { taskId = tid2, participants = parts };
                }
                continue;
            }

            // Person-name resolution. Skip very common stopwords to keep this cheap.
            if (IsStopword(c)) continue;

            var found = _graphTools.FindUserByName(c, 3);
            foreach (var u in found)
            {
                if (users.ContainsKey(u.UserId)) continue;
                var activity = _graphTools.GetUserActivity(u.UserId);
                if (activity is not null) users[u.UserId] = activity;
            }

            if (users.Count >= 5 && tasks.Count >= 5) break;
        }

        return (users.Values.ToList(), tasks.Values.ToList());
    }

    private static bool IsStopword(string w)
    {
        if (w.Length <= 2) return true;
        return w.ToLowerInvariant() switch
        {
            "how" or "many" or "did" or "work" or "worked" or "the" or "and" or "for"
                or "with" or "tasks" or "task" or "ticket" or "tickets" or "user" or "users"
                or "what" or "which" or "who" or "are" or "were" or "have" or "has"
                or "this" or "that" or "from" or "into" or "about" or "show" or "list" => true,
            _ => false,
        };
    }

    private async Task<(object? Result, string Summary)> DispatchToolCallAsync(
        FunctionCallContent call,
        IList<AITool> tools,
        CancellationToken ct)
    {
        var aiFunction = tools.OfType<AIFunction>()
            .FirstOrDefault(f => string.Equals(f.Name, call.Name, StringComparison.Ordinal));

        if (aiFunction is null)
        {
            var err = $"Unknown tool '{call.Name}'.";
            _logger.LogWarning("Tool dispatch: {Error}", err);
            return (new { error = err }, err);
        }

        var started = DateTime.UtcNow;
        try
        {
            var args = call.Arguments is null
                ? new AIFunctionArguments()
                : new AIFunctionArguments(call.Arguments);
            var invokeResult = await aiFunction.InvokeAsync(args, ct);
            var elapsed = (int)(DateTime.UtcNow - started).TotalMilliseconds;
            var summary = SummariseResult(invokeResult);
            _logger.LogInformation("Tool {Tool} ok in {Elapsed}ms: {Summary}", call.Name, elapsed, summary);
            return (invokeResult, summary);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tool {Tool} threw", call.Name);
            return (new { error = ex.Message }, "error: " + ex.Message);
        }
    }

    private static string SummariseResult(object? result)
    {
        if (result is null) return "null";
        if (result is string s) return s.Length > 60 ? s[..60] + "…" : s;
        if (result is System.Collections.IEnumerable e)
        {
            var count = 0;
            foreach (var _ in e) count++;
            return $"{count} item(s)";
        }
        return result.GetType().Name;
    }
}

public sealed record ToolInvocation(string Name, string ArgumentsJson, string ResultSummary);
