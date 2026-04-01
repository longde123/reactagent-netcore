using AiCli.Core.Types;
using System.Text.Json.Serialization;

namespace AiCli.Core.Configuration;

/// <summary>
/// Application settings.
/// </summary>
public record Settings
{
    /// <summary>
    /// The model to use for completions.
    /// </summary>
    [JsonPropertyName("model")]
    public string? Model { get; init; }

    /// <summary>
    /// The API key for Gemini.
    /// </summary>
    [JsonPropertyName("apiKey")]
    public string? ApiKey { get; init; }

    /// <summary>
    /// The base URL for the API.
    /// </summary>
    [JsonPropertyName("baseUrl")]
    public string? BaseUrl { get; init; }

    /// <summary>
    /// The approval mode for tool execution.
    /// </summary>
    [JsonPropertyName("approvalMode")]
    public ApprovalMode? ApprovalMode { get; init; }

    /// <summary>
    /// Whether agents are enabled.
    /// </summary>
    [JsonPropertyName("enableAgents")]
    public bool? EnableAgents { get; init; }

    /// <summary>
    /// Whether to enable the browser agent.
    /// </summary>
    [JsonPropertyName("enableBrowserAgent")]
    public bool? EnableBrowserAgent { get; init; }

    /// <summary>
    /// Maximum number of turns for agent execution.
    /// </summary>
    [JsonPropertyName("maxAgentTurns")]
    public int? MaxAgentTurns { get; init; }

    /// <summary>
    /// Agent timeout in seconds.
    /// </summary>
    [JsonPropertyName("agentTimeout")]
    public int? AgentTimeout { get; init; }

    /// <summary>
    /// List of enabled extensions.
    /// </summary>
    [JsonPropertyName("extensions")]
    public List<Extension>? Extensions { get; init; }

    /// <summary>
    /// Whether to enable telemetry.
    /// </summary>
    [JsonPropertyName("enableTelemetry")]
    public bool? EnableTelemetry { get; init; }

    /// <summary>
    /// Whether to enable code assistance.
    /// </summary>
    [JsonPropertyName("enableCodeAssist")]
    public bool? EnableCodeAssist { get; init; }

    /// <summary>
    /// Whether to use the sandbox for shell execution.
    /// </summary>
    [JsonPropertyName("useSandbox")]
    public bool? UseSandbox { get; init; }

    /// <summary>
    /// Sandbox type (docker, podman, or none).
    /// </summary>
    [JsonPropertyName("sandbox")]
    public string? Sandbox { get; init; }

    /// <summary>
    /// Maximum number of tokens for context.
    /// </summary>
    [JsonPropertyName("maxTokens")]
    public int? MaxTokens { get; init; }

    /// <summary>
    /// Temperature for generation.
    /// </summary>
    [JsonPropertyName("temperature")]
    public double? Temperature { get; init; }

    /// <summary>
    /// Top P for generation.
    /// </summary>
    [JsonPropertyName("topP")]
    public double? TopP { get; init; }

    /// <summary>
    /// Top K for generation.
    /// </summary>
    [JsonPropertyName("topK")]
    public int? TopK { get; init; }

    /// <summary>
    /// Whether to enable plan mode.
    /// </summary>
    [JsonPropertyName("enablePlanMode")]
    public bool? EnablePlanMode { get; init; }

    /// <summary>
    /// Custom memory content.
    /// </summary>
    [JsonPropertyName("memory")]
    public string? Memory { get; init; }

    /// <summary>
    /// Embedding model for semantic search / RAG (e.g. "bge-m3").
    /// </summary>
    [JsonPropertyName("embeddingModel")]
    public string? EmbeddingModel { get; init; }

    /// <summary>
    /// Thinking/reasoning model for complex planning and large-context tasks (e.g. "gpt-oss:20b").
    /// </summary>
    [JsonPropertyName("thinkingModel")]
    public string? ThinkingModel { get; init; }

    /// <summary>
    /// Fast model for quick code edits and simple tasks (e.g. "qwen2.5-coder:7b").
    /// </summary>
    [JsonPropertyName("fastModel")]
    public string? FastModel { get; init; }

    /// <summary>
    /// Merges another settings object into this one, preferring non-null values from other.
    /// </summary>
    public Settings Merge(Settings? other)
    {
        if (other is null) return this;

        return this with
        {
            Model = other.Model ?? Model,
            ApiKey = other.ApiKey ?? ApiKey,
            BaseUrl = other.BaseUrl ?? BaseUrl,
            ApprovalMode = other.ApprovalMode ?? ApprovalMode,
            EnableAgents = other.EnableAgents ?? EnableAgents,
            EnableBrowserAgent = other.EnableBrowserAgent ?? EnableBrowserAgent,
            MaxAgentTurns = other.MaxAgentTurns ?? MaxAgentTurns,
            AgentTimeout = other.AgentTimeout ?? AgentTimeout,
            Extensions = other.Extensions ?? Extensions,
            EnableTelemetry = other.EnableTelemetry ?? EnableTelemetry,
            EnableCodeAssist = other.EnableCodeAssist ?? EnableCodeAssist,
            UseSandbox = other.UseSandbox ?? UseSandbox,
            Sandbox = other.Sandbox ?? Sandbox,
            MaxTokens = other.MaxTokens ?? MaxTokens,
            Temperature = other.Temperature ?? Temperature,
            TopP = other.TopP ?? TopP,
            TopK = other.TopK ?? TopK,
            EnablePlanMode = other.EnablePlanMode ?? EnablePlanMode,
            Memory = other.Memory ?? Memory,
            EmbeddingModel = other.EmbeddingModel ?? EmbeddingModel,
            ThinkingModel = other.ThinkingModel ?? ThinkingModel,
            FastModel = other.FastModel ?? FastModel,
        };
    }
}

/// <summary>
/// Extension configuration.
/// </summary>
public record Extension
{
    /// <summary>
    /// The extension ID.
    /// </summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary>
    /// Whether the extension is enabled.
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Extension-specific configuration.
    /// </summary>
    [JsonPropertyName("config")]
    public Dictionary<string, object?>? Config { get; init; }
}
