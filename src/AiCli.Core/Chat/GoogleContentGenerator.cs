using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiCli.Core.Configuration;
using AiCli.Core.Logging;
using AiCli.Core.Types;

namespace AiCli.Core.Chat;

/// <summary>
/// Google Generative Language API backed content generator.
/// </summary>
public sealed class GoogleContentGenerator : IContentGenerator, IAsyncDisposable
{
    private readonly Config _config;
    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public GoogleContentGenerator(Config config, HttpClient? httpClient = null)
    {
        _config = config;
        _logger = LoggerHelper.ForContext<GoogleContentGenerator>();
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient is null;
    }

    public string GetModelId() => _config.GetModel();

    private static readonly Dictionary<string, string> LegacyModelAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["gemini-2.0-flash-exp"] = "gemini-2.5-flash",
            ["gemini-1.5-pro"] = "gemini-2.5-pro",
            ["gemini-1.5-flash"] = "gemini-2.5-flash"
        };

    public async Task<ContentMessage> SendMessageAsync(
        ContentMessage message,
        CancellationToken cancellationToken = default)
    {
        var request = new GenerateContentRequest
        {
            Model = _config.GetModel(),
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
        var request = new GenerateContentRequest
        {
            Model = _config.GetModel(),
            Contents = new List<ContentMessage> { message },
            CancellationToken = cancellationToken
        };

        await foreach (var chunk in GenerateContentStreamAsync(request, cancellationToken))
        {
            var first = chunk.Candidates.FirstOrDefault();
            if (first is not null)
            {
                yield return new ContentMessage
                {
                    Role = LlmRole.Model,
                    Parts = first.Content
                };
            }
        }
    }

    public async Task<GenerateContentResponse> GenerateContentAsync(
        GenerateContentRequest request,
        CancellationToken cancellationToken = default)
    {
        var model = ResolveModelId(request.Model);
        var endpoint = BuildEndpoint(model, "generateContent");
        var payload = BuildGeneratePayload(request);
        var json = payload.ToJsonString();

        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _httpClient.SendAsync(message, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"GenerateContent failed ({(int)response.StatusCode} {response.StatusCode}): {content}");
        }

        return ParseGenerateResponse(content, model);
    }

    public async IAsyncEnumerable<GenerateContentResponse> GenerateContentStreamAsync(
        GenerateContentRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var model = ResolveModelId(request.Model);
        var endpoint = BuildEndpoint(model, "streamGenerateContent", useSse: true);
        var payload = BuildGeneratePayload(request);

        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };

        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _httpClient.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"StreamGenerateContent failed ({(int)response.StatusCode} {response.StatusCode}): {errorContent}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var data = line[5..].Trim();
            if (string.IsNullOrWhiteSpace(data) || data == "[DONE]")
            {
                continue;
            }

            yield return ParseGenerateResponse(data, model);
        }
    }

    public async Task<CountTokensResponse> CountTokensAsync(
        CountTokensRequest request,
        CancellationToken cancellationToken = default)
    {
        var endpoint = BuildEndpoint(ResolveModelId(request.Model), "countTokens");

        var payload = new JsonObject
        {
            ["contents"] = ToApiContents(request.Contents)
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };

        using var response = await _httpClient.SendAsync(message, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"CountTokens failed ({(int)response.StatusCode} {response.StatusCode}): {content}");
        }

        using var doc = JsonDocument.Parse(content);
        var totalTokens = doc.RootElement.TryGetProperty("totalTokens", out var tt)
            ? tt.GetInt32()
            : 0;

        return new CountTokensResponse { TotalTokens = totalTokens };
    }

    public async Task<EmbedContentResponse> EmbedContentAsync(
        EmbedContentRequest request,
        CancellationToken cancellationToken = default)
    {
        var endpoint = BuildEndpoint(ResolveModelId(request.Model), "embedContent");

        var payload = new JsonObject
        {
            ["content"] = ToApiContent(request.Content)
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };

        using var response = await _httpClient.SendAsync(message, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"EmbedContent failed ({(int)response.StatusCode} {response.StatusCode}): {content}");
        }

        using var doc = JsonDocument.Parse(content);
        var values = new List<double>();

        if (doc.RootElement.TryGetProperty("embedding", out var embedding) &&
            embedding.TryGetProperty("values", out var vals) &&
            vals.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in vals.EnumerateArray())
            {
                if (value.TryGetDouble(out var d))
                {
                    values.Add(d);
                }
            }
        }

        return new EmbedContentResponse { Embedding = values };
    }

    public ValueTask DisposeAsync()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private JsonObject BuildGeneratePayload(GenerateContentRequest request)
    {
        var payload = new JsonObject
        {
            ["contents"] = ToApiContents(request.Contents)
        };

        if (!string.IsNullOrWhiteSpace(request.SystemInstruction))
        {
            payload["systemInstruction"] = new JsonObject
            {
                ["parts"] = new JsonArray { new JsonObject { ["text"] = request.SystemInstruction } }
            };
        }

        if (request.Tools is { Count: > 0 })
        {
            payload["tools"] = ToApiTools(request.Tools);
        }

        if (request.ToolConfig?.FunctionCallingConfig is not null)
        {
            var cfg = request.ToolConfig.FunctionCallingConfig;
            var toolConfig = new JsonObject
            {
                ["functionCallingConfig"] = new JsonObject
                {
                    ["mode"] = cfg.Mode.ToString().ToUpperInvariant()
                }
            };

            if (cfg.AllowedFunctionNames is { Count: > 0 })
            {
                toolConfig["functionCallingConfig"]!["allowedFunctionNames"] = new JsonArray(cfg.AllowedFunctionNames.Select(name => JsonValue.Create(name)!).ToArray());
            }

            payload["toolConfig"] = toolConfig;
        }

        if (request.Config is not null)
        {
            var generationConfig = new JsonObject();

            if (request.Config.Temperature is not null) generationConfig["temperature"] = request.Config.Temperature.Value;
            if (request.Config.TopP is not null) generationConfig["topP"] = request.Config.TopP.Value;
            if (request.Config.TopK is not null) generationConfig["topK"] = request.Config.TopK.Value;
            if (request.Config.MaxOutputTokens is not null) generationConfig["maxOutputTokens"] = request.Config.MaxOutputTokens.Value;

            if (request.Config.StopSequences is { Count: > 0 })
            {
                generationConfig["stopSequences"] = new JsonArray(request.Config.StopSequences.Select(seq => JsonValue.Create(seq)!).ToArray());
            }

            if (generationConfig.Count > 0)
            {
                payload["generationConfig"] = generationConfig;
            }
        }

        return payload;
    }

    private static JsonArray ToApiContents(IEnumerable<ContentMessage> contents)
    {
        var array = new JsonArray();
        foreach (var item in contents)
        {
            array.Add(ToApiContent(item));
        }

        return array;
    }

    private static JsonObject ToApiContent(ContentMessage message)
    {
        var role = message.Role switch
        {
            LlmRole.Model => "model",
            _ => "user"
        };

        return new JsonObject
        {
            ["role"] = role,
            ["parts"] = ToApiParts(message.Parts)
        };
    }

    private static JsonArray ToApiParts(IEnumerable<ContentPart> parts)
    {
        var array = new JsonArray();

        foreach (var part in parts)
        {
            switch (part)
            {
                case TextContentPart text:
                    array.Add(new JsonObject { ["text"] = text.Text });
                    break;

                case FunctionCallPart functionCall:
                    array.Add(new JsonObject
                    {
                        ["functionCall"] = new JsonObject
                        {
                            ["name"] = functionCall.FunctionName,
                            ["args"] = JsonSerializer.SerializeToNode(functionCall.Arguments)
                        }
                    });
                    break;

                case FunctionResponsePart functionResponse:
                    array.Add(new JsonObject
                    {
                        ["functionResponse"] = new JsonObject
                        {
                            ["name"] = functionResponse.FunctionName,
                            ["response"] = JsonSerializer.SerializeToNode(functionResponse.Response)
                        }
                    });
                    break;
            }
        }

        return array;
    }

    private static JsonArray ToApiTools(IEnumerable<Tool> tools)
    {
        var toolArray = new JsonArray();

        foreach (var tool in tools)
        {
            var declarations = new JsonArray();
            foreach (var fn in tool.FunctionDeclarations ?? Array.Empty<FunctionDeclaration>())
            {
                var fnObject = new JsonObject
                {
                    ["name"] = fn.Name,
                    ["description"] = fn.Description
                };

                if (fn.Parameters is not null)
                {
                    fnObject["parameters"] = ToApiFunctionParameters(fn.Parameters);
                }

                declarations.Add(fnObject);
            }

            toolArray.Add(new JsonObject
            {
                ["functionDeclarations"] = declarations
            });
        }

        return toolArray;
    }

    private static JsonObject ToApiFunctionParameters(FunctionParameters parameters)
    {
        var obj = new JsonObject
        {
            ["type"] = parameters.Type
        };

        if (parameters.Required is { Count: > 0 })
        {
            obj["required"] = new JsonArray(parameters.Required.Select(x => JsonValue.Create(x)!).ToArray());
        }

        if (parameters.Properties is { Count: > 0 })
        {
            var props = new JsonObject();
            foreach (var (name, schema) in parameters.Properties)
            {
                props[name] = ToApiPropertySchema(schema);
            }

            obj["properties"] = props;
        }

        return obj;
    }

    private static JsonObject ToApiPropertySchema(PropertySchema schema)
    {
        var obj = new JsonObject();

        if (!string.IsNullOrWhiteSpace(schema.Type)) obj["type"] = schema.Type;
        if (!string.IsNullOrWhiteSpace(schema.Description)) obj["description"] = schema.Description;
        if (schema.Nullable is not null) obj["nullable"] = schema.Nullable.Value;
        if (schema.Default is not null) obj["default"] = JsonSerializer.SerializeToNode(schema.Default);
        if (schema.Minimum is not null) obj["minimum"] = schema.Minimum.Value;
        if (schema.Maximum is not null) obj["maximum"] = schema.Maximum.Value;
        if (schema.MinLength is not null) obj["minLength"] = schema.MinLength.Value;
        if (schema.MaxLength is not null) obj["maxLength"] = schema.MaxLength.Value;

        if (schema.Enum is { Count: > 0 })
        {
            obj["enum"] = new JsonArray(schema.Enum.Select(x => JsonValue.Create(x)!).ToArray());
        }

        if (schema.Items is not null)
        {
            obj["items"] = ToApiPropertySchema(schema.Items);
        }

        return obj;
    }

    private GenerateContentResponse ParseGenerateResponse(string content, string fallbackModel)
    {
        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        var candidates = new List<Candidate>();
        if (root.TryGetProperty("candidates", out var candidatesElement) && candidatesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var candidateElement in candidatesElement.EnumerateArray())
            {
                candidates.Add(ParseCandidate(candidateElement));
            }
        }

        if (candidates.Count == 0)
        {
            candidates.Add(new Candidate
            {
                Content = new List<ContentPart> { new TextContentPart(string.Empty) },
                Index = 0,
                FinishReason = FinishReason.Other
            });
        }

        var usage = root.TryGetProperty("usageMetadata", out var usageElement)
            ? ParseUsage(usageElement)
            : null;

        return new GenerateContentResponse
        {
            Candidates = candidates,
            UsageMetadata = usage,
            ModelVersion = root.TryGetProperty("modelVersion", out var mv) ? mv.GetString() : fallbackModel
        };
    }

    private static Candidate ParseCandidate(JsonElement candidateElement)
    {
        var parts = new List<ContentPart>();

        if (candidateElement.TryGetProperty("content", out var contentElement) &&
            contentElement.TryGetProperty("parts", out var partsElement) &&
            partsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var partElement in partsElement.EnumerateArray())
            {
                if (partElement.TryGetProperty("text", out var text))
                {
                    parts.Add(new TextContentPart(text.GetString() ?? string.Empty));
                    continue;
                }

                if (partElement.TryGetProperty("functionCall", out var functionCall) &&
                    functionCall.TryGetProperty("name", out var nameElement))
                {
                    var args = functionCall.TryGetProperty("args", out var argsElement)
                        ? JsonSerializer.Deserialize<Dictionary<string, object?>>(argsElement.GetRawText()) ?? new Dictionary<string, object?>()
                        : new Dictionary<string, object?>();

                    parts.Add(new FunctionCallPart
                    {
                        FunctionName = nameElement.GetString() ?? string.Empty,
                        Arguments = args,
                        Id = Guid.NewGuid().ToString()
                    });

                    continue;
                }

                if (partElement.TryGetProperty("functionResponse", out var functionResponse) &&
                    functionResponse.TryGetProperty("name", out var responseNameElement))
                {
                    var responsePayload = functionResponse.TryGetProperty("response", out var responseElement)
                        ? JsonSerializer.Deserialize<Dictionary<string, object?>>(responseElement.GetRawText()) ?? new Dictionary<string, object?>()
                        : new Dictionary<string, object?>();

                    parts.Add(new FunctionResponsePart
                    {
                        FunctionName = responseNameElement.GetString() ?? string.Empty,
                        Response = responsePayload,
                        Id = Guid.NewGuid().ToString()
                    });
                }
            }
        }

        if (parts.Count == 0)
        {
            parts.Add(new TextContentPart(string.Empty));
        }

        return new Candidate
        {
            Content = parts,
            Index = candidateElement.TryGetProperty("index", out var index) ? index.GetInt32() : 0,
            FinishReason = ParseFinishReason(candidateElement)
        };
    }

    private static FinishReason ParseFinishReason(JsonElement candidateElement)
    {
        if (!candidateElement.TryGetProperty("finishReason", out var finishElement))
        {
            return FinishReason.Other;
        }

        var reason = finishElement.GetString();
        return reason?.ToUpperInvariant() switch
        {
            "STOP" => FinishReason.Stop,
            "MAX_TOKENS" => FinishReason.MaxTokens,
            "SAFETY" => FinishReason.Safety,
            "RECITATION" => FinishReason.Recitation,
            _ => FinishReason.Other
        };
    }

    private static UsageMetadata? ParseUsage(JsonElement usageElement)
    {
        return new UsageMetadata
        {
            PromptTokenCount = usageElement.TryGetProperty("promptTokenCount", out var pt) ? pt.GetInt32() : 0,
            CandidatesTokenCount = usageElement.TryGetProperty("candidatesTokenCount", out var ct) ? ct.GetInt32() : 0,
            TotalTokenCount = usageElement.TryGetProperty("totalTokenCount", out var tt) ? tt.GetInt32() : 0
        };
    }

    private string BuildEndpoint(string model, string action, bool useSse = false)
    {
        var baseUrl = _config.GetBaseUrl().TrimEnd('/');
        var apiKey = _config.GetApiKey();
        var sseSuffix = useSse ? "&alt=sse" : string.Empty;

        return $"{baseUrl}/v1beta/models/{Uri.EscapeDataString(model)}:{action}?key={Uri.EscapeDataString(apiKey)}{sseSuffix}";
    }

    private static string ResolveModelId(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return "gemini-2.5-flash";
        }

        if (LegacyModelAliases.TryGetValue(model.Trim(), out var mapped))
        {
            return mapped;
        }

        return model.Trim();
    }
}

/// <summary>
/// Factory for picking the active content generator implementation.
/// </summary>
public static class ContentGeneratorFactory
{
    public static IContentGenerator Create(Config config)
    {
        var forceLocal = Environment.GetEnvironmentVariable("AICLI_USE_LOCAL_GENERATOR");
        if (!string.IsNullOrWhiteSpace(forceLocal) &&
            forceLocal.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return new ContentGenerator(config);
        }

        if (config.IsOpenAICompatible())
        {
            // LiteLLM / LM Studio / vLLM：统一走多模型编排（内部使用 OpenAI 兼容实现）
            return new MultiModelOrchestrator(config);
        }

        if (CanUseOllamaApi(config))
        {
            // 多模型模式：嵌入(bge-m3) + 思考(gpt-oss:20b) + 快速执行(qwen2.5-coder:7b)
            return new MultiModelOrchestrator(config);
        }

        if (CanUseGoogleApi(config))
        {
            return new GoogleContentGenerator(config);
        }

        return new ContentGenerator(config);
    }

    private static bool CanUseGoogleApi(Config config)
    {
        var forceLocal = Environment.GetEnvironmentVariable("AICLI_USE_LOCAL_GENERATOR");
        if (!string.IsNullOrWhiteSpace(forceLocal) &&
            forceLocal.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!config.HasApiKey())
        {
            return false;
        }

        var baseUrl = config.GetBaseUrl();
        return Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) &&
               uri.Host.Contains("generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase);
    }

    private static bool CanUseOllamaApi(Config config)
    {
        if (config.IsOllamaKey())
        {
            return true;
        }

        var baseUrl = config.GetBaseUrl();
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Host.Contains("generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var isLocalOllama =
            (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase))
            && (uri.Port == 11434 || uri.Port == -1);

        var isNamedOllama = uri.Host.Contains("ollama", StringComparison.OrdinalIgnoreCase);

        return isLocalOllama || isNamedOllama;
    }
}