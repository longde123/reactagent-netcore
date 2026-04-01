using AiCli.Core.Chat;
using AiCli.Core.Logging;
using AiCli.Core.Tools;
using System.Diagnostics;
using System.Text.Json;

namespace AiCli.Core.Agents;

/// <summary>
/// Base agent class with common functionality.
/// </summary>
public abstract class Agent : IAgent
{
    protected readonly ILogger _logger;
    protected readonly CancellationTokenSource _cts = new();
    protected readonly List<ContentMessage> _messageHistory = new();
    protected readonly List<string> _toolCallHistory = new();
    protected readonly object _stateLock = new();

    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    public AgentKind Kind { get; }
    public List<string> Capabilities { get; }
    public ToolRegistry ToolRegistry { get; }
    public IContentGenerator Chat { get; }
    public AgentExecutionState State { get; protected set; } = AgentExecutionState.Idle;

    public event EventHandler<AgentEvent>? OnEvent;

    protected Agent(
        string id,
        string name,
        string description,
        AgentKind kind,
        List<string> capabilities,
        ToolRegistry toolRegistry,
        IContentGenerator chat)
    {
        Id = id;
        Name = name;
        Description = description;
        Kind = kind;
        Capabilities = capabilities;
        ToolRegistry = toolRegistry;
        Chat = chat;
        _logger = LoggerHelper.ForContext<Agent>();
    }

    /// <summary>
    /// Executes the agent with an initial message.
    /// </summary>
    public virtual async Task<AgentResult> ExecuteAsync(
        ContentMessage message,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var messages = new List<ContentMessage>();
        var toolCalls = new List<string>();
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            _cts.Token, cancellationToken);

        lock (_stateLock)
        {
            State = AgentExecutionState.Running;
        }

        EmitEvent(AgentEventType.Started, $"Starting agent: {Name}");

        try
        {
            var initialMessage = await TryEnrichInitialMessageAsync(message, linkedCts.Token);

            // Add initial message to history
            _messageHistory.Add(initialMessage);
            messages.Add(initialMessage);

            // Process message through generator with tool schemas enabled
            var response = await GenerateResponseWithToolsAsync(linkedCts.Token);
            _messageHistory.Add(response);
            messages.Add(response);

            // Handle any tool calls in the response
            var turnCount = 0;
            while (ContainsToolCalls(response) && turnCount < 100)
            {
                turnCount++;

                // Extract and execute tool calls
                var toolCallParts = response.Parts
                    .OfType<FunctionCallPart>()
                    .ToList();

                foreach (var toolCall in toolCallParts)
                {
                    var argsJson = JsonSerializer.Serialize(toolCall.Arguments);
                    _logger.Information("Preparing to call tool {Tool} with args: {ArgsJson}", toolCall.FunctionName, argsJson);
                    toolCalls.Add($"{toolCall.FunctionName}({argsJson})");

                    // 使用带参数摘要的描述性标签（而不是裸工具名）
                    var callLabel = BuildToolCallLabel(toolCall.FunctionName, toolCall.Arguments);
                    EmitEvent(AgentEventType.ToolCalled, callLabel);

                    var tool = ToolRegistry.GetTool(toolCall.FunctionName);
                    if (tool == null)
                    {
                        var errorMsg = $"Tool not found: {toolCall.FunctionName}";
                        _logger.Warning(errorMsg);
                        _messageHistory.Add(CreateToolResultMessage(toolCall.FunctionName, new ToolExecutionResult
                        {
                            IsError = true,
                            Output = errorMsg,
                            Content = new List<ContentPart> { new TextContentPart(errorMsg) }
                        }));
                        EmitEvent(AgentEventType.ToolCompleted, callLabel);   // 确保 ◌ → ✓
                    }
                    else
                    {
                        try
                        {
                            var result = await ExecuteToolAsync(tool, toolCall.Arguments, linkedCts.Token);
                            _logger.Information("Tool {Tool} executed. Success: {Success}, OutputLen: {Len}",
                                toolCall.FunctionName, result.Error is null, result.Output?.Length ?? 0);
                            _messageHistory.Add(CreateToolResultMessage(toolCall.FunctionName, result));
                            EmitEvent(AgentEventType.ToolCompleted, callLabel);
                        }
                        catch (Exception ex)
                        {
                            _logger.Error(ex, "Error executing tool: {Tool}", toolCall.FunctionName);
                            _messageHistory.Add(CreateToolResultMessage(toolCall.FunctionName, new ToolExecutionResult
                            {
                                IsError = true,
                                Output = ex.Message,
                                Content = new List<ContentPart> { new TextContentPart(ex.Message) }
                            }));
                            EmitEvent(AgentEventType.ToolCompleted, callLabel);   // 确保 ◌ → ✓
                        }
                    }
                }

                // Get next response from updated history (which now includes function responses)
                response = await GenerateResponseWithToolsAsync(linkedCts.Token);
                _messageHistory.Add(response);
                messages.Add(response);

                EmitEvent(AgentEventType.TurnCompleted, $"Turn {turnCount} completed");
            }

            stopwatch.Stop();

            lock (_stateLock)
            {
                State = AgentExecutionState.Completed;
            }

            EmitEvent(AgentEventType.Completed, $"Agent completed in {stopwatch.ElapsedMilliseconds}ms");

            return new AgentResult
            {
                State = AgentExecutionState.Completed,
                Messages = messages,
                ToolCalls = toolCalls,
                Duration = stopwatch.Elapsed
            };
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();

            lock (_stateLock)
            {
                State = AgentExecutionState.Cancelled;
            }

            EmitEvent(AgentEventType.Cancelled, "Agent cancelled");

            return new AgentResult
            {
                State = AgentExecutionState.Cancelled,
                Messages = messages,
                ToolCalls = toolCalls,
                Duration = stopwatch.Elapsed
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.Error(ex, "Agent execution failed");

            lock (_stateLock)
            {
                State = AgentExecutionState.Failed;
            }

            EmitEvent(AgentEventType.Failed, $"Agent failed: {ex.Message}", error: ex);

            return new AgentResult
            {
                State = AgentExecutionState.Failed,
                Messages = messages,
                ToolCalls = toolCalls,
                Duration = stopwatch.Elapsed,
                Error = ex
            };
        }
    }

    protected virtual async Task<ContentMessage> TryEnrichInitialMessageAsync(
        ContentMessage message,
        CancellationToken cancellationToken)
    {
        if (message.Role != LlmRole.User)
        {
            return message;
        }

        var userText = message.GetText();
        if (string.IsNullOrWhiteSpace(userText))
        {
            return message;
        }

        var listTool = ToolRegistry.GetTool("list_directory");
        if (listTool == null)
        {
            return message;
        }

        try
        {
            var args = new Dictionary<string, object>
            {
                ["path"] = ".",
                ["directories_only"] = true,
                ["max_depth"] = 2
            };

            var result = await ExecuteToolAsync(listTool, args, cancellationToken);
            var contextText = result.Output
                ?? (result.LlmContent as TextContentPart)?.Text
                ?? string.Empty;

            if (result.IsError || string.IsNullOrWhiteSpace(contextText))
            {
                return message;
            }

            var lines = contextText
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Take(60);
            var snapshot = string.Join("\n", lines);

            if (string.IsNullOrWhiteSpace(snapshot))
            {
                return message;
            }

            _toolCallHistory.Add("preprocess:list_directory");

            return ContentMessage.UserMessage($"""
{userText}

[Preloaded workspace snapshot]
The following directory snapshot was collected before reasoning:
{snapshot}
""");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Preprocessing tool call failed. Continuing with original user prompt.");
            return message;
        }
    }

    /// <summary>
    /// Sends a message to the agent.
    /// </summary>
    public async Task<ContentMessage> SendMessageAsync(
        ContentMessage message,
        CancellationToken cancellationToken = default)
    {
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            _cts.Token, cancellationToken);

        _messageHistory.Add(message);
        var response = await Chat.SendMessageAsync(message, linkedCts.Token);
        _messageHistory.Add(response);

        EmitEvent(AgentEventType.MessageReceived, "Message received");

        return response;
    }

    /// <summary>
    /// Pauses the agent execution.
    /// </summary>
    public virtual Task PauseAsync()
    {
        lock (_stateLock)
        {
            if (State != AgentExecutionState.Running)
            {
                throw new InvalidOperationException($"Cannot pause agent in state: {State}");
            }
            State = AgentExecutionState.Paused;
        }

        EmitEvent(AgentEventType.Paused, "Agent paused");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Resumes the agent execution.
    /// </summary>
    public virtual Task ResumeAsync()
    {
        lock (_stateLock)
        {
            if (State != AgentExecutionState.Paused)
            {
                throw new InvalidOperationException($"Cannot resume agent in state: {State}");
            }
            State = AgentExecutionState.Running;
        }

        EmitEvent(AgentEventType.Resumed, "Agent resumed");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Cancels the agent execution.
    /// </summary>
    public virtual Task CancelAsync()
    {
        _cts.Cancel();

        lock (_stateLock)
        {
            if (State == AgentExecutionState.Running)
            {
                State = AgentExecutionState.Cancelled;
            }
        }

        EmitEvent(AgentEventType.Cancelled, "Agent cancelled");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Resets the agent state.
    /// </summary>
    public virtual void Reset()
    {
        lock (_stateLock)
        {
            State = AgentExecutionState.Idle;
        }

        _messageHistory.Clear();
        _toolCallHistory.Clear();

        EmitEvent(AgentEventType.Completed, "Agent reset");
    }

    /// <summary>
    /// Checks if a message contains tool calls.
    /// </summary>
    protected bool ContainsToolCalls(ContentMessage message)
    {
        return message.Parts.OfType<FunctionCallPart>().Any();
    }

    /// <summary>
    /// Returns the system instruction for this agent. Override in subclasses.
    /// </summary>
    protected virtual string? GetSystemInstruction() => null;

    /// <summary>
    /// Generates a model response using full history and currently registered tools.
    /// </summary>
    protected virtual async Task<ContentMessage> GenerateResponseWithToolsAsync(CancellationToken cancellationToken)
    {
        var modelId = Chat.GetModelId();
        var request = new GenerateContentRequest
        {
            Model = modelId,
            Contents = new List<ContentMessage>(_messageHistory),
            SystemInstruction = GetSystemInstruction(),
            Tools = ToolRegistry.GetTools(modelId),
            ToolConfig = new ToolConfig
            {
                FunctionCallingConfig = new FunctionCallingConfig
                {
                    Mode = FunctionCallingMode.Any,
                    AllowedFunctionNames = ToolRegistry.AllToolNames.ToList()
                }
            },
            CancellationToken = cancellationToken
        };

        // Stream response: emit thinking events live, accumulate text + tool calls for history
        var textChunks  = new List<string>();
        var toolCalls   = new List<FunctionCallPart>();

        await foreach (var chunk in Chat.GenerateContentStreamAsync(request, cancellationToken))
        {
            var candidate = chunk.Candidates.FirstOrDefault();
            if (candidate is null) continue;

            foreach (var part in candidate.Content)
            {
                switch (part)
                {
                    case ThinkingContentPart tp when !string.IsNullOrEmpty(tp.Text):
                        EmitEvent(AgentEventType.Thinking, tp.Text);
                        break;
                    case TextContentPart tx when !string.IsNullOrEmpty(tx.Text):
                        textChunks.Add(tx.Text);
                        break;
                    case FunctionCallPart fc:
                        toolCalls.Add(fc);
                        break;
                }
            }
        }

        // Fallback：部分模型（如 qwen2.5-coder）将工具调用以文本 JSON 格式输出
        // 而不是 Ollama 原生的 tool_calls 字段，此处做兼容解析。
        if (toolCalls.Count == 0 && textChunks.Count > 0)
        {
            var merged = string.Concat(textChunks).Trim();
            var textParsed = ParseTextFormatToolCalls(merged);
            if (textParsed.Count > 0)
            {
                _logger.Information("Detected {Count} text-format tool call(s) in model output", textParsed.Count);
                toolCalls.AddRange(textParsed);
                textChunks.Clear();
            }
        }

        var finalParts = new List<ContentPart>();

        // 如果有工具调用，优先返回工具调用（模型决定调用工具，不返回文字）
        if (toolCalls.Count > 0)
        {
            finalParts.AddRange(toolCalls);
        }
        else
        {
            var merged = string.Concat(textChunks);
            if (!string.IsNullOrEmpty(merged))
                finalParts.Add(new TextContentPart(merged));
        }

        if (finalParts.Count == 0)
            return ContentMessage.ModelMessage(string.Empty);

        return new ContentMessage { Role = LlmRole.Model, Parts = finalParts };
    }

    /// <summary>
    /// 根据工具名和参数构建面向用户的描述性标签，例如 "write_file  Program.cs"。
    /// </summary>
    private static string BuildToolCallLabel(string toolName, Dictionary<string, object?>? args)
    {
        if (args == null || args.Count == 0) return toolName;

        static string? Str(Dictionary<string, object?> a, string key)
        {
            if (!a.TryGetValue(key, out var v)) return null;
            if (v is JsonElement je && je.ValueKind == JsonValueKind.String) return je.GetString();
            return v?.ToString();
        }

        static string Cap(string s, int max) => s.Length <= max ? s : s[..max] + "…";

        var detail = toolName switch
        {
            "write_file" or "read_file" or "edit_file" =>
                Str(args, "file_path") is { } fp ? System.IO.Path.GetFileName(fp) : null,
            "shell" =>
                Str(args, "command") is { } cmd ? Cap(cmd, 55) : null,
            "grep" =>
                Str(args, "pattern") is { } pat ? Cap(pat, 40) : null,
            "glob" =>
                Str(args, "pattern") is { } gpat ? Cap(gpat, 40) : null,
            "list_directory" =>
                Str(args, "path") is { } lp ? lp : null,
            "google_web_search" or "web_search" =>
                Str(args, "query") is { } q ? Cap(q, 45) : null,
            "web_fetch" =>
                Str(args, "url") is { } url ? Cap(url, 50) : null,
            _ => null
        };

        return detail is null ? toolName : $"{toolName}  {detail}";
    }

    /// <summary>
    /// 尝试将文本内容解析为工具调用（兼容模型输出文本 JSON 格式的工具调用）。
    /// 支持单个对象 {"name":"...","arguments":{}} 或每行一个。
    /// </summary>
    private static List<FunctionCallPart> ParseTextFormatToolCalls(string text)
    {
        var result = new List<FunctionCallPart>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        // 尝试整段文本作为单个调用
        if (TryParseOneTextCall(text, out var single))
        {
            result.Add(single!);
            return result;
        }

        // 尝试逐行解析（每行一个工具调用）
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("{") && TryParseOneTextCall(trimmed, out var fc))
                result.Add(fc!);
        }

        return result;
    }

    private static bool TryParseOneTextCall(string json, out FunctionCallPart? fc)
    {
        fc = null;
        var trimmed = json.Trim();
        if (!trimmed.StartsWith("{") || !trimmed.EndsWith("}")) return false;

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;

            string? name = null;
            var args = new Dictionary<string, object?>();

            // 格式 A: {"name": "func", "arguments": {...}}
            if (root.TryGetProperty("name", out var nameEl))
            {
                name = nameEl.GetString();
                if (root.TryGetProperty("arguments", out var argsEl))
                    args = JsonSerializer.Deserialize<Dictionary<string, object?>>(argsEl.GetRawText()) ?? new();
            }
            // 格式 B: {"function": {"name": "func", "arguments": {...}}}
            else if (root.TryGetProperty("function", out var fnEl))
            {
                if (fnEl.TryGetProperty("name", out var fnNameEl)) name = fnNameEl.GetString();
                if (fnEl.TryGetProperty("arguments", out var fnArgsEl))
                    args = JsonSerializer.Deserialize<Dictionary<string, object?>>(fnArgsEl.GetRawText()) ?? new();
            }

            if (string.IsNullOrWhiteSpace(name)) return false;

            fc = new FunctionCallPart { FunctionName = name!, Arguments = args, Id = Guid.NewGuid().ToString() };
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Executes a tool.
    /// </summary>
    protected abstract Task<ToolExecutionResult> ExecuteToolAsync(
        IToolBuilder tool,
        Dictionary<string, object>? arguments,
        CancellationToken cancellationToken);

    /// <summary>
    /// Creates a tool result message.
    /// </summary>
    protected ContentMessage CreateToolResultMessage(
        string functionName,
        ToolExecutionResult result)
    {
        return new ContentMessage
        {
            Role = LlmRole.Function,
            Parts = new List<ContentPart>
            {
                new FunctionResponsePart
                {
                    FunctionName = functionName,
                    Response = new Dictionary<string, object?>
                    {
                        ["result"] = result.Output,
                        ["is_error"] = result.IsError
                    }
                }
            }
        };
    }

    /// <summary>
    /// Creates a continuation message for the chat.
    /// </summary>
    protected ContentMessage CreateContinuationMessage()
    {
        return new ContentMessage
        {
            Role = LlmRole.User,
            Parts = new List<ContentPart>
            {
                new TextContentPart("Please continue.")
            }
        };
    }

    /// <summary>
    /// Emits an agent event.
    /// </summary>
    protected void EmitEvent(
        AgentEventType type,
        string? message = null,
        ContentMessage? contentMessage = null,
        Exception? error = null)
    {
        OnEvent?.Invoke(this, new AgentEvent
        {
            AgentId = Id,
            Type = type,
            Message = message,
            ContentMessage = contentMessage,
            Error = error
        });
    }
}
