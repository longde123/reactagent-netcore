using AiCli.Core.Configuration;
using AiCli.Core.Types;

namespace AiCli.Core.Chat;

/// <summary>
/// 模型角色，用于路由到最合适的本地模型。
/// </summary>
public enum ModelRole
{
    /// <summary>
    /// 语义嵌入 / 向量检索（bge-m3）。
    /// 用于 RAG、相似度搜索、代码库语义索引。
    /// </summary>
    Embedding,

    /// <summary>
    /// 复杂推理 / 大上下文（gpt-oss:20b，128K context）。
    /// 用于架构规划、复杂重构分析、整体代码库理解。
    /// </summary>
    Thinking,

    /// <summary>
    /// 快速代码执行（qwen2.5-coder:7b）。
    /// 用于单文件编辑、简单修复、批量小改动。
    /// </summary>
    Fast,
}

/// <summary>
/// 多模型编排器：根据任务角色自动路由到最合适的本地模型。
/// 实现 IContentGenerator 接口，默认路由到思考模型，保持向后兼容。
/// <para>
/// 支持两种后端：
/// <list type="bullet">
///   <item>Ollama（默认，端口 11434）：直接调用 /api/chat</item>
///   <item>OpenAI 兼容（LiteLLM 端口 4000 / LM Studio 端口 1234 / sk- key）：调用 /v1/chat/completions</item>
/// </list>
/// 使用 LiteLLM 时，在 litellm_config.yaml 中配置模型别名（fast/thinking/embedding），
/// C# 端只需将 baseUrl 指向 LiteLLM 网关即可，无需修改代码。
/// </para>
/// </summary>
public sealed class MultiModelOrchestrator : IContentGenerator, IAsyncDisposable
{
    private readonly IContentGenerator _embeddingGenerator;
    private readonly IContentGenerator _thinkingGenerator;
    private readonly IContentGenerator _fastGenerator;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public MultiModelOrchestrator(Config config, HttpClient? httpClient = null)
    {
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient();

        if (config.IsOpenAICompatible())
        {
            // LiteLLM / LM Studio / vLLM：统一走 OpenAI 兼容格式
            var defaultModel = config.GetModel();
            var embeddingModel = ResolveModelForOpenAiCompatible(config.GetEmbeddingModel(), defaultModel);
            var thinkingModel  = ResolveModelForOpenAiCompatible(config.GetThinkingModel(),  defaultModel);
            var fastModel      = ResolveModelForOpenAiCompatible(config.GetFastModel(),      defaultModel);

            _embeddingGenerator = new OpenAICompatibleContentGenerator(config, embeddingModel, _httpClient);
            _thinkingGenerator  = new OpenAICompatibleContentGenerator(config, thinkingModel,  _httpClient);
            _fastGenerator      = new OpenAICompatibleContentGenerator(config, fastModel,      _httpClient);
        }
        else
        {
            // Ollama 原生格式（/api/chat）
            _embeddingGenerator = new OllamaContentGenerator(config, config.GetEmbeddingModel(), enableThinking: false, _httpClient);
            _thinkingGenerator  = new OllamaContentGenerator(config, config.GetThinkingModel(),  enableThinking: true,  _httpClient);
            _fastGenerator      = new OllamaContentGenerator(config, config.GetFastModel(),      enableThinking: false, _httpClient);
        }
    }

    /// <summary>
    /// 根据角色返回对应的生成器。
    /// </summary>
    public IContentGenerator GetGenerator(ModelRole role) => role switch
    {
        ModelRole.Embedding => _embeddingGenerator,
        ModelRole.Thinking  => _thinkingGenerator,
        ModelRole.Fast      => _fastGenerator,
        _                   => _thinkingGenerator,
    };

    /// <summary>
    /// 根据消息内容自动推断角色并路由。
    /// - 消息很短（&lt;200字）且含代码关键字 → Fast
    /// - 其余 → Thinking
    /// </summary>
    public IContentGenerator AutoSelect(ContentMessage message)
    {
        var text = string.Concat(message.Parts.OfType<TextContentPart>().Select(p => p.Text));

        if (text.Length < 200 && ContainsCodeKeywords(text))
            return _fastGenerator;

        return _thinkingGenerator;
    }

    // ── IContentGenerator（委托到思考模型，保持向后兼容）──────────────────

    public string GetModelId() => _thinkingGenerator.GetModelId();

    public Task<ContentMessage> SendMessageAsync(
        ContentMessage message,
        CancellationToken cancellationToken = default)
        => _thinkingGenerator.SendMessageAsync(message, cancellationToken);

    public IAsyncEnumerable<ContentMessage> SendMessageStreamAsync(
        ContentMessage message,
        CancellationToken cancellationToken = default)
        => _thinkingGenerator.SendMessageStreamAsync(message, cancellationToken);

    public Task<GenerateContentResponse> GenerateContentAsync(
        GenerateContentRequest request,
        CancellationToken cancellationToken = default)
        => _thinkingGenerator.GenerateContentAsync(request, cancellationToken);

    public IAsyncEnumerable<GenerateContentResponse> GenerateContentStreamAsync(
        GenerateContentRequest request,
        CancellationToken cancellationToken = default)
        => _thinkingGenerator.GenerateContentStreamAsync(request, cancellationToken);

    public Task<CountTokensResponse> CountTokensAsync(
        CountTokensRequest request,
        CancellationToken cancellationToken = default)
        => _thinkingGenerator.CountTokensAsync(request, cancellationToken);

    public Task<EmbedContentResponse> EmbedContentAsync(
        EmbedContentRequest request,
        CancellationToken cancellationToken = default)
        => _embeddingGenerator.EmbedContentAsync(request, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_embeddingGenerator is IAsyncDisposable ed) await ed.DisposeAsync();
        if (_thinkingGenerator  is IAsyncDisposable td) await td.DisposeAsync();
        if (_fastGenerator      is IAsyncDisposable fd) await fd.DisposeAsync();

        if (_ownsHttpClient)
            _httpClient.Dispose();
    }

    private static readonly string[] _codeKeywords =
    {
        "fix", "edit", "rename", "add", "remove", "replace", "insert",
        "修复", "编辑", "重命名", "添加", "删除", "替换",
    };

    private static bool ContainsCodeKeywords(string text)
    {
        var lower = text.ToLowerInvariant();
        return _codeKeywords.Any(k => lower.Contains(k));
    }

    private static string ResolveModelForOpenAiCompatible(string? configuredModel, string defaultModel)
    {
        if (string.IsNullOrWhiteSpace(configuredModel))
            return defaultModel;

        var model = configuredModel.Trim();

        // 常见角色占位名：若未在网关做 alias，直接发会导致 400（Invalid model name）。
        // 这里回退到主模型，保证 agent 至少可用。
        if (model.Equals("fast", StringComparison.OrdinalIgnoreCase) ||
            model.Equals("thinking", StringComparison.OrdinalIgnoreCase) ||
            model.Equals("embedding", StringComparison.OrdinalIgnoreCase))
        {
            return defaultModel;
        }

        return model;
    }
}
