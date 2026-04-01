using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiCli.Core.Configuration;
using AiCli.Core.Logging;
using AiCli.Core.Types;

namespace AiCli.Core.Chat;

/// <summary>
/// Ollama API backed content generator.
/// Uses /api/chat endpoint and supports basic tool-calling payloads.
/// </summary>
public sealed class OllamaContentGenerator : IContentGenerator, IAsyncDisposable
{
    private readonly Config _config;
    private readonly string? _modelOverride;
    private readonly bool _enableThinking;
    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    /// <param name="modelOverride">覆盖 config 中的模型名，用于多模型场景。</param>
    /// <param name="enableThinking">是否向 Ollama 传 think:true，激活推理模型的思考输出。</param>
    public OllamaContentGenerator(
        Config config,
        string? modelOverride = null,
        bool enableThinking = false,
        HttpClient? httpClient = null)
    {
        _config = config;
        _modelOverride = modelOverride;
        _enableThinking = enableThinking;
        _logger = LoggerHelper.ForContext<OllamaContentGenerator>();
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
            Model = _modelOverride ?? _config.GetModel(),
            Contents = new List<ContentMessage> { message }
        };

        var response = await GenerateContentAsync(request, cancellationToken);
        var first = response.Candidates.FirstOrDefault();

        return first is null
            ? ContentMessage.ModelMessage(string.Empty)
            : new ContentMessage
            {
                Role = LlmRole.Model,
                Parts = first.Content
            };
    }

    public async IAsyncEnumerable<ContentMessage> SendMessageStreamAsync(
        ContentMessage message,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await SendMessageAsync(message, cancellationToken);
        yield return response;
    }

    public async Task<GenerateContentResponse> GenerateContentAsync(
        GenerateContentRequest request,
        CancellationToken cancellationToken = default)
    {
        var endpoint = BuildChatEndpoint();
        var payload = BuildChatPayload(request);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Ollama chat failed ({(int)response.StatusCode} {response.StatusCode}): {content}");
        }

        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        var parts = new List<ContentPart>();
        if (root.TryGetProperty("message", out var messageElement))
        {
            if (messageElement.TryGetProperty("content", out var textElement))
            {
                var text = textElement.GetString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    parts.Add(new TextContentPart(text));
                }
            }

            if (messageElement.TryGetProperty("tool_calls", out var toolCallsElement) &&
                toolCallsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var toolCall in toolCallsElement.EnumerateArray())
                {
                    if (!toolCall.TryGetProperty("function", out var fnElement))
                    {
                        continue;
                    }

                    var functionName = fnElement.TryGetProperty("name", out var fnName)
                        ? fnName.GetString() ?? string.Empty
                        : string.Empty;

                    if (string.IsNullOrWhiteSpace(functionName))
                    {
                        continue;
                    }

                    Dictionary<string, object?> args;
                    if (fnElement.TryGetProperty("arguments", out var argsElement))
                    {
                        if (argsElement.ValueKind == JsonValueKind.String)
                        {
                            var json = argsElement.GetString() ?? "{}";
                            args = JsonSerializer.Deserialize<Dictionary<string, object?>>(json) ?? new Dictionary<string, object?>();
                        }
                        else
                        {
                            args = JsonSerializer.Deserialize<Dictionary<string, object?>>(argsElement.GetRawText()) ?? new Dictionary<string, object?>();
                        }
                    }
                    else
                    {
                        args = new Dictionary<string, object?>();
                    }

                    parts.Add(new FunctionCallPart
                    {
                        FunctionName = functionName,
                        Arguments = args,
                        Id = Guid.NewGuid().ToString()
                    });
                }
            }
        }

        if (parts.Count == 0)
        {
            parts.Add(new TextContentPart(string.Empty));
        }

        return new GenerateContentResponse
        {
            Candidates = new List<Candidate>
            {
                new()
                {
                    Content = parts,
                    Index = 0,
                    FinishReason = FinishReason.Stop
                }
            },
            ModelVersion = request.Model
        };
    }

    public async IAsyncEnumerable<GenerateContentResponse> GenerateContentStreamAsync(
        GenerateContentRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var endpoint = BuildChatEndpoint();
        var payload = BuildChatPayload(request);
        payload["stream"] = true;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };

        using var response = await _httpClient.SendAsync(
            httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"Ollama stream failed ({(int)response.StatusCode}): {err}");
        }

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); }
            catch { continue; }

            using (doc)
            {
                var root = doc.RootElement;

                // Final done marker — no content
                if (root.TryGetProperty("done", out var doneEl) && doneEl.GetBoolean())
                    yield break;

                if (!root.TryGetProperty("message", out var messageEl))
                    continue;

                var parts = new List<ContentPart>();

                // Thinking tokens — Ollama native field (gpt-oss:20b, qwen3 with think:true)
                if (messageEl.TryGetProperty("thinking", out var thinkingEl))
                {
                    var thinking = thinkingEl.GetString() ?? string.Empty;
                    if (!string.IsNullOrEmpty(thinking))
                        parts.Add(new ThinkingContentPart(thinking));
                }

                // Regular content tokens (may contain <think>…</think> for deepseek-r1 style)
                if (messageEl.TryGetProperty("content", out var contentEl))
                {
                    var raw = contentEl.GetString() ?? string.Empty;
                    if (!string.IsNullOrEmpty(raw))
                        ExtractThinkTagsIntoPartsInPlace(raw, parts);
                }

                // Tool calls — Ollama streams them as a complete object in one chunk
                if (messageEl.TryGetProperty("tool_calls", out var toolCallsEl) &&
                    toolCallsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var toolCall in toolCallsEl.EnumerateArray())
                    {
                        if (!toolCall.TryGetProperty("function", out var fnEl)) continue;

                        var functionName = fnEl.TryGetProperty("name", out var fnName)
                            ? fnName.GetString() ?? string.Empty : string.Empty;
                        if (string.IsNullOrWhiteSpace(functionName)) continue;

                        Dictionary<string, object?> args;
                        if (fnEl.TryGetProperty("arguments", out var argsEl))
                        {
                            args = argsEl.ValueKind == JsonValueKind.String
                                ? JsonSerializer.Deserialize<Dictionary<string, object?>>(
                                    argsEl.GetString() ?? "{}") ?? new()
                                : JsonSerializer.Deserialize<Dictionary<string, object?>>(
                                    argsEl.GetRawText()) ?? new();
                        }
                        else { args = new(); }

                        parts.Add(new FunctionCallPart
                        {
                            FunctionName = functionName,
                            Arguments    = args,
                            Id           = Guid.NewGuid().ToString()
                        });
                    }
                }

                if (parts.Count == 0) continue;

                yield return new GenerateContentResponse
                {
                    Candidates = new List<Candidate>
                    {
                        new()
                        {
                            Content = parts,
                            Index = 0,
                            FinishReason = FinishReason.Other
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

        return Task.FromResult(new CountTokensResponse
        {
            TotalTokens = Math.Max(1, totalText / 4)
        });
    }

    public async Task<EmbedContentResponse> EmbedContentAsync(
        EmbedContentRequest request,
        CancellationToken cancellationToken = default)
    {
        var baseUrl = _config.GetBaseUrl().TrimEnd('/');
        var endpoint = $"{baseUrl}/api/embed";

        var text = string.Join(" ", request.Content.Parts
            .OfType<TextContentPart>()
            .Select(p => p.Text));

        var model = _modelOverride ?? request.Model;
        var payload = new JsonObject
        {
            ["model"] = model,
            ["input"] = text
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Ollama embed failed ({(int)response.StatusCode} {response.StatusCode}): {content}");
        }

        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        // Ollama returns { "embeddings": [[...]] }
        if (root.TryGetProperty("embeddings", out var embeddingsArray) &&
            embeddingsArray.ValueKind == JsonValueKind.Array &&
            embeddingsArray.GetArrayLength() > 0)
        {
            var first = embeddingsArray[0];
            if (first.ValueKind == JsonValueKind.Array)
            {
                var vector = first.EnumerateArray()
                    .Select(e => e.GetDouble())
                    .ToList();

                return new EmbedContentResponse { Embedding = vector };
            }
        }

        throw new InvalidOperationException($"Unexpected Ollama embed response: {content}");
    }

    public ValueTask DisposeAsync()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private string BuildChatEndpoint()
    {
        var baseUrl = _config.GetBaseUrl().TrimEnd('/');
        return $"{baseUrl}/api/chat";
    }

    private JsonObject BuildChatPayload(GenerateContentRequest request)
    {
        var messages = ToOllamaMessages(request.Contents);

        // Prepend system instruction as a system-role message if present
        if (!string.IsNullOrWhiteSpace(request.SystemInstruction))
        {
            messages.Insert(0, new JsonObject
            {
                ["role"] = "system",
                ["content"] = request.SystemInstruction
            });
        }

        var payload = new JsonObject
        {
            ["model"] = _modelOverride ?? request.Model,
            ["stream"] = false,
            ["messages"] = messages
        };

        // Enable Ollama thinking output for reasoning models (gpt-oss:20b, qwen3, deepseek-r1…)
        if (_enableThinking)
            payload["think"] = true;

        if (request.Tools is { Count: > 0 })
        {
            payload["tools"] = ToOllamaTools(request.Tools);
        }

        if (request.Config is not null)
        {
            var options = new JsonObject();
            if (request.Config.Temperature is not null) options["temperature"] = request.Config.Temperature.Value;
            if (request.Config.TopP is not null) options["top_p"] = request.Config.TopP.Value;
            if (request.Config.TopK is not null) options["top_k"] = request.Config.TopK.Value;
            if (options.Count > 0) payload["options"] = options;
        }

        return payload;
    }

    private static JsonArray ToOllamaMessages(IEnumerable<ContentMessage> contents)
    {
        var messages = new JsonArray();

        foreach (var msg in contents)
        {
            var textParts = msg.Parts.OfType<TextContentPart>().Select(p => p.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            var functionCalls = msg.Parts.OfType<FunctionCallPart>().ToList();
            var functionResponses = msg.Parts.OfType<FunctionResponsePart>().ToList();

            if (functionResponses.Count > 0)
            {
                foreach (var fr in functionResponses)
                {
                    messages.Add(new JsonObject
                    {
                        ["role"] = "tool",
                        ["name"] = fr.FunctionName,
                        ["content"] = JsonSerializer.Serialize(fr.Response)
                    });
                }

                continue;
            }

            var role = msg.Role switch
            {
                LlmRole.Model => "assistant",
                LlmRole.System => "system",
                _ => "user"
            };

            var messageObj = new JsonObject
            {
                ["role"] = role,
                ["content"] = string.Join("\n", textParts)
            };

            if (functionCalls.Count > 0)
            {
                var calls = new JsonArray();
                foreach (var fc in functionCalls)
                {
                    calls.Add(new JsonObject
                    {
                        ["function"] = new JsonObject
                        {
                            ["name"] = fc.FunctionName,
                            ["arguments"] = JsonSerializer.SerializeToNode(fc.Arguments)
                        }
                    });
                }

                messageObj["tool_calls"] = calls;
            }

            messages.Add(messageObj);
        }

        return messages;
    }

    private static JsonArray ToOllamaTools(IEnumerable<Tool> tools)
    {
        var toolArray = new JsonArray();

        foreach (var tool in tools)
        {
            foreach (var fn in tool.FunctionDeclarations ?? Array.Empty<FunctionDeclaration>())
            {
                toolArray.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = fn.Name,
                        ["description"] = fn.Description,
                        ["parameters"] = fn.Parameters is null
                            ? null
                            : JsonSerializer.SerializeToNode(fn.Parameters)
                    }
                });
            }
        }

        return toolArray;
    }

    // Parses content that may contain <think>…</think> XML tags (deepseek-r1 / qwen3 style).
    // Splits the text into alternating ThinkingContentPart / TextContentPart segments.
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
                // No more think tags — rest is plain text
                var tail = raw[pos..];
                if (!string.IsNullOrEmpty(tail))
                    parts.Add(new TextContentPart(tail));
                break;
            }

            // Text before the tag
            if (openIdx > pos)
                parts.Add(new TextContentPart(raw[pos..openIdx]));

            var contentStart = openIdx + open.Length;
            var closeIdx = raw.IndexOf(close, contentStart, StringComparison.OrdinalIgnoreCase);
            if (closeIdx < 0)
            {
                // Unclosed tag — treat rest as thinking (streaming partial chunk)
                var thinkTail = raw[contentStart..];
                if (!string.IsNullOrEmpty(thinkTail))
                    parts.Add(new ThinkingContentPart(thinkTail));
                break;
            }

            var thinkText = raw[contentStart..closeIdx];
            if (!string.IsNullOrEmpty(thinkText))
                parts.Add(new ThinkingContentPart(thinkText));

            pos = closeIdx + close.Length;
        }
    }
}
