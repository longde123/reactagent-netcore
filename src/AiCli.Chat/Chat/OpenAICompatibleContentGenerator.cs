using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiCli.Core.Configuration;
using AiCli.Core.Logging;
using AiCli.Core.Types;

namespace AiCli.Core.Chat;

/// <summary>
/// OpenAI 兼容 API 的内容生成器。
/// 适用于 LiteLLM 网关、LM Studio、vLLM 及任何暴露 /v1/chat/completions 的服务。
/// </summary>
public sealed class OpenAICompatibleContentGenerator : IContentGenerator, IAsyncDisposable
{
    private readonly Config _config;
    private readonly string? _modelOverride;
    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public OpenAICompatibleContentGenerator(
        Config config,
        string? modelOverride = null,
        HttpClient? httpClient = null)
    {
        _config = config;
        _modelOverride = modelOverride;
        _logger = LoggerHelper.ForContext<OpenAICompatibleContentGenerator>();
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
    }

    public string GetModelId() => _modelOverride ?? _config.GetModel();

    public async Task<ContentMessage> SendMessageAsync(
        ContentMessage message,
        CancellationToken cancellationToken = default)
    {
        var request = new GenerateContentRequest
        {
            Model = GetModelId(),
            Contents = new List<ContentMessage> { message }
        };
        var response = await GenerateContentAsync(request, cancellationToken);
        var first = response.Candidates.FirstOrDefault();
        return first is null
            ? ContentMessage.ModelMessage(string.Empty)
            : new ContentMessage { Role = LlmRole.Model, Parts = first.Content };
    }

    public async IAsyncEnumerable<ContentMessage> SendMessageStreamAsync(
        ContentMessage message,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = new GenerateContentRequest
        {
            Model = GetModelId(),
            Contents = new List<ContentMessage> { message }
        };
        await foreach (var response in GenerateContentStreamAsync(request, cancellationToken))
        {
            var first = response.Candidates.FirstOrDefault();
            if (first is not null)
                yield return new ContentMessage { Role = LlmRole.Model, Parts = first.Content };
        }
    }

    public async Task<GenerateContentResponse> GenerateContentAsync(
        GenerateContentRequest request,
        CancellationToken cancellationToken = default)
    {
        var endpoint = BuildEndpoint("/v1/chat/completions");
        var payload = BuildPayload(request, stream: false);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        AddAuthHeader(httpRequest);

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"OpenAI chat failed ({(int)response.StatusCode} {response.StatusCode}): {content}");

        using var doc = JsonDocument.Parse(content);
        return ParseNonStreamResponse(doc.RootElement, _modelOverride ?? request.Model);
    }

    public async IAsyncEnumerable<GenerateContentResponse> GenerateContentStreamAsync(
        GenerateContentRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var endpoint = BuildEndpoint("/v1/chat/completions");
        var payload = BuildPayload(request, stream: true);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        AddAuthHeader(httpRequest);

        using var response = await _httpClient.SendAsync(
            httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"OpenAI stream failed ({(int)response.StatusCode}): {err}");
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        // tool_calls 在流式响应中是按 delta index 分片传输的，需要逐块拼接
        var toolCallAccumulator = new Dictionary<int, ToolCallAccumulation>();

        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!line.StartsWith("data:")) continue;

            var data = line["data:".Length..].Trim();
            if (data == "[DONE]") break;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(data); }
            catch { continue; }

            using (doc)
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                    continue;

                var choice = choices[0];
                if (!choice.TryGetProperty("delta", out var delta)) continue;

                var parts = new List<ContentPart>();

                // 文本 delta（可能包含 <think> 标签）
                if (delta.TryGetProperty("content", out var contentEl) &&
                    contentEl.ValueKind != JsonValueKind.Null)
                {
                    var raw = contentEl.GetString() ?? string.Empty;
                    if (!string.IsNullOrEmpty(raw))
                        ExtractThinkTagsIntoPartsInPlace(raw, parts);
                }

                // tool_calls delta：按 index 累积
                if (delta.TryGetProperty("tool_calls", out var toolCallsEl) &&
                    toolCallsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tc in toolCallsEl.EnumerateArray())
                    {
                        var index = tc.TryGetProperty("index", out var idxEl) ? idxEl.GetInt32() : 0;
                        if (!toolCallAccumulator.TryGetValue(index, out var acc))
                        {
                            acc = new ToolCallAccumulation();
                            toolCallAccumulator[index] = acc;
                        }

                        if (tc.TryGetProperty("id", out var idEl))
                            acc.Id = idEl.GetString() ?? acc.Id;

                        if (tc.TryGetProperty("function", out var fnEl))
                        {
                            if (fnEl.TryGetProperty("name", out var nameEl))
                                acc.Name = nameEl.GetString() ?? acc.Name;
                            if (fnEl.TryGetProperty("arguments", out var argsEl))
                                acc.ArgumentsJson += argsEl.GetString() ?? string.Empty;
                        }
                    }
                }

                // finish_reason 出现时 flush 累积的 tool calls
                var finishReason = choice.TryGetProperty("finish_reason", out var frEl) &&
                                   frEl.ValueKind != JsonValueKind.Null
                    ? frEl.GetString()
                    : null;

                if (finishReason is "tool_calls" || (finishReason is not null && toolCallAccumulator.Count > 0))
                {
                    foreach (var (_, acc) in toolCallAccumulator.OrderBy(x => x.Key))
                    {
                        if (string.IsNullOrWhiteSpace(acc.Name)) continue;
                        Dictionary<string, object?> args;
                        try
                        {
                            args = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                                       string.IsNullOrWhiteSpace(acc.ArgumentsJson) ? "{}" : acc.ArgumentsJson)
                                   ?? new Dictionary<string, object?>();
                        }
                        catch { args = new Dictionary<string, object?>(); }

                        parts.Add(new FunctionCallPart
                        {
                            FunctionName = acc.Name,
                            Arguments    = args,
                            Id           = acc.Id
                        });
                    }
                    toolCallAccumulator.Clear();
                }

                if (parts.Count == 0) continue;

                yield return new GenerateContentResponse
                {
                    Candidates = new List<Candidate>
                    {
                        new()
                        {
                            Content      = parts,
                            Index        = 0,
                            FinishReason = finishReason == "stop" ? FinishReason.Stop : FinishReason.Other
                        }
                    },
                    ModelVersion = _modelOverride ?? request.Model
                };
            }
        }
    }

    public Task<CountTokensResponse> CountTokensAsync(
        CountTokensRequest request,
        CancellationToken cancellationToken = default)
    {
        var totalText = request.Contents
            .SelectMany(c => c.Parts)
            .OfType<TextContentPart>()
            .Sum(p => p.Text.Length);
        return Task.FromResult(new CountTokensResponse { TotalTokens = Math.Max(1, totalText / 4) });
    }

    public async Task<EmbedContentResponse> EmbedContentAsync(
        EmbedContentRequest request,
        CancellationToken cancellationToken = default)
    {
        var endpoint = BuildEndpoint("/v1/embeddings");
        var text = string.Join(" ", request.Content.Parts
            .OfType<TextContentPart>()
            .Select(p => p.Text));

        var payload = new JsonObject
        {
            ["model"] = _modelOverride ?? request.Model,
            ["input"] = text
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        AddAuthHeader(httpRequest);

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"OpenAI embed failed ({(int)response.StatusCode} {response.StatusCode}): {content}");

        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        // OpenAI 格式: { "data": [{ "embedding": [...] }] }
        if (root.TryGetProperty("data", out var dataEl) &&
            dataEl.ValueKind == JsonValueKind.Array &&
            dataEl.GetArrayLength() > 0)
        {
            var first = dataEl[0];
            if (first.TryGetProperty("embedding", out var embEl) &&
                embEl.ValueKind == JsonValueKind.Array)
            {
                var vector = embEl.EnumerateArray().Select(e => e.GetDouble()).ToList();
                return new EmbedContentResponse { Embedding = vector };
            }
        }

        throw new InvalidOperationException($"Unexpected OpenAI embed response: {content}");
    }

    public ValueTask DisposeAsync()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
        return ValueTask.CompletedTask;
    }

    // ── 私有辅助方法 ─────────────────────────────────────────────────────────

    private string BuildEndpoint(string path)
    {
        var baseUrl = _config.GetBaseUrl().TrimEnd('/');
        return $"{baseUrl}{path}";
    }

    private void AddAuthHeader(HttpRequestMessage request)
    {
        if (_config.HasApiKey())
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _config.GetApiKey());
    }

    private JsonObject BuildPayload(GenerateContentRequest request, bool stream)
    {
        var messages = ToOpenAIMessages(request.Contents);

        if (!string.IsNullOrWhiteSpace(request.SystemInstruction))
        {
            messages.Insert(0, new JsonObject
            {
                ["role"]    = "system",
                ["content"] = request.SystemInstruction
            });
        }

        var payload = new JsonObject
        {
            ["model"]    = _modelOverride ?? request.Model,
            ["stream"]   = stream,
            ["messages"] = new JsonArray(messages.Select(m => (JsonNode)m).ToArray())
        };

        if (request.Config is not null)
        {
            if (request.Config.Temperature is not null)
                payload["temperature"] = request.Config.Temperature.Value;
            if (request.Config.TopP is not null)
                payload["top_p"] = request.Config.TopP.Value;
            if (request.Config.MaxOutputTokens is not null)
                payload["max_tokens"] = request.Config.MaxOutputTokens.Value;
        }

        if (request.Tools is { Count: > 0 })
            payload["tools"] = ToOpenAITools(request.Tools);

        return payload;
    }

    private static List<JsonObject> ToOpenAIMessages(IEnumerable<ContentMessage> contents)
    {
        var messages = new List<JsonObject>();

        foreach (var msg in contents)
        {
            var textParts        = msg.Parts.OfType<TextContentPart>().Select(p => p.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            var functionCalls    = msg.Parts.OfType<FunctionCallPart>().ToList();
            var functionResponses = msg.Parts.OfType<FunctionResponsePart>().ToList();

            // 工具结果 → role:tool，OpenAI 要求每个结果单独一条消息，并附带 tool_call_id
            if (functionResponses.Count > 0)
            {
                foreach (var fr in functionResponses)
                {
                    messages.Add(new JsonObject
                    {
                        ["role"]         = "tool",
                        ["tool_call_id"] = fr.Id,
                        ["content"]      = JsonSerializer.Serialize(fr.Response)
                    });
                }
                continue;
            }

            var role = msg.Role switch
            {
                LlmRole.Model  => "assistant",
                LlmRole.System => "system",
                _              => "user"
            };

            var msgObj = new JsonObject
            {
                ["role"]    = role,
                ["content"] = string.Join("\n", textParts)
            };

            // 助手消息中的工具调用
            if (functionCalls.Count > 0)
            {
                var calls = new JsonArray();
                foreach (var fc in functionCalls)
                {
                    calls.Add(new JsonObject
                    {
                        ["id"]   = fc.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"]      = fc.FunctionName,
                            // OpenAI 要求 arguments 为 JSON 字符串
                            ["arguments"] = JsonSerializer.Serialize(fc.Arguments)
                        }
                    });
                }
                msgObj["tool_calls"] = calls;
            }

            messages.Add(msgObj);
        }

        return messages;
    }

    private static JsonArray ToOpenAITools(IEnumerable<Tool> tools)
    {
        var arr = new JsonArray();
        foreach (var tool in tools)
        {
            foreach (var fn in tool.FunctionDeclarations ?? Array.Empty<FunctionDeclaration>())
            {
                JsonNode? parametersNode = null;
                if (fn.Parameters is not null)
                {
                    parametersNode = JsonSerializer.SerializeToNode(fn.Parameters, new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                    });

                    // OpenAI tool schema 要求 parameters.type 为 "object"。
                    if (parametersNode is JsonObject obj)
                    {
                        var hasType = obj.TryGetPropertyValue("type", out var typeNode);
                        var typeValue = typeNode?.ToString();
                        if (!hasType || string.IsNullOrWhiteSpace(typeValue))
                        {
                            obj["type"] = "object";
                        }
                    }
                }

                arr.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"]        = fn.Name,
                        ["description"] = fn.Description,
                        ["parameters"]  = parametersNode
                    }
                });
            }
        }
        return arr;
    }

    private static GenerateContentResponse ParseNonStreamResponse(JsonElement root, string modelId)
    {
        var parts = new List<ContentPart>();

        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            var choice = choices[0];
            if (choice.TryGetProperty("message", out var msg))
            {
                if (msg.TryGetProperty("content", out var contentEl) &&
                    contentEl.ValueKind != JsonValueKind.Null)
                {
                    var text = contentEl.GetString() ?? string.Empty;
                    if (!string.IsNullOrEmpty(text))
                        ExtractThinkTagsIntoPartsInPlace(text, parts);
                }

                if (msg.TryGetProperty("tool_calls", out var toolCallsEl) &&
                    toolCallsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tc in toolCallsEl.EnumerateArray())
                    {
                        if (!tc.TryGetProperty("function", out var fn)) continue;

                        var name = fn.TryGetProperty("name", out var nameEl)
                            ? nameEl.GetString() ?? string.Empty
                            : string.Empty;
                        if (string.IsNullOrWhiteSpace(name)) continue;

                        var id = tc.TryGetProperty("id", out var idEl)
                            ? idEl.GetString() ?? Guid.NewGuid().ToString()
                            : Guid.NewGuid().ToString();

                        Dictionary<string, object?> args;
                        if (fn.TryGetProperty("arguments", out var argsEl))
                        {
                            var raw = argsEl.ValueKind == JsonValueKind.String
                                ? argsEl.GetString() ?? "{}"
                                : argsEl.GetRawText();
                            args = JsonSerializer.Deserialize<Dictionary<string, object?>>(raw) ?? new();
                        }
                        else { args = new(); }

                        parts.Add(new FunctionCallPart { FunctionName = name, Arguments = args, Id = id });
                    }
                }
            }
        }

        if (parts.Count == 0)
            parts.Add(new TextContentPart(string.Empty));

        return new GenerateContentResponse
        {
            Candidates   = new List<Candidate> { new() { Content = parts, Index = 0, FinishReason = FinishReason.Stop } },
            ModelVersion = modelId
        };
    }

    // 与 OllamaContentGenerator 相同的 <think> 标签提取逻辑（deepseek-r1 / qwen3 风格）
    private static void ExtractThinkTagsIntoPartsInPlace(string raw, List<ContentPart> parts)
    {
        const string open  = "<think>";
        const string close = "</think>";

        var pos = 0;
        while (pos < raw.Length)
        {
            var openIdx = raw.IndexOf(open, pos, StringComparison.OrdinalIgnoreCase);
            if (openIdx < 0)
            {
                var tail = raw[pos..];
                if (!string.IsNullOrEmpty(tail)) parts.Add(new TextContentPart(tail));
                break;
            }

            if (openIdx > pos) parts.Add(new TextContentPart(raw[pos..openIdx]));

            var contentStart = openIdx + open.Length;
            var closeIdx     = raw.IndexOf(close, contentStart, StringComparison.OrdinalIgnoreCase);
            if (closeIdx < 0)
            {
                var thinkTail = raw[contentStart..];
                if (!string.IsNullOrEmpty(thinkTail)) parts.Add(new ThinkingContentPart(thinkTail));
                break;
            }

            var thinkText = raw[contentStart..closeIdx];
            if (!string.IsNullOrEmpty(thinkText)) parts.Add(new ThinkingContentPart(thinkText));
            pos = closeIdx + close.Length;
        }
    }

    private sealed class ToolCallAccumulation
    {
        public string Id            { get; set; } = Guid.NewGuid().ToString();
        public string Name          { get; set; } = string.Empty;
        public string ArgumentsJson { get; set; } = string.Empty;
    }
}
