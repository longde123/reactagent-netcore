namespace AiCli.Core.Configuration;

/// <summary>
/// Main configuration class that provides access to all configuration and services.
/// </summary>
public class Config : IAsyncDisposable
{
    private readonly Storage _storage;
    private Settings _settings;

    /// <summary>
    /// The current settings.
    /// </summary>
    public Settings Settings => _settings;

    /// <summary>
    /// The storage instance.
    /// </summary>
    public Storage Storage => _storage;

    /// <summary>
    /// The hierarchical memory.
    /// </summary>
    public HierarchicalMemory Memory { get; private set; }

    /// <summary>
    /// Initializes a new instance of the Config class.
    /// </summary>
    public Config(Storage? storage = null)
    {
        _storage = storage ?? new Storage();
        _settings = new Settings();
        Memory = new HierarchicalMemory();
    }

    /// <summary>
    /// Initializes the configuration asynchronously.
    /// </summary>
    public async Task InitializeAsync()
    {
        _settings = await _storage.LoadSettingsAsync();
        Memory = await _storage.LoadMemoryAsync();
    }

    /// <summary>
    /// Reloads the settings from storage.
    /// </summary>
    public async Task ReloadAsync()
    {
        _settings = await _storage.LoadSettingsAsync();
    }

    /// <summary>
    /// Gets the model to use.
    /// </summary>
    public string GetModel()
    {
        if (!string.IsNullOrWhiteSpace(_settings.Model))
        {
            return _settings.Model!;
        }

        if (IsOpenAICompatible())
        {
            if (!string.IsNullOrWhiteSpace(_settings.ThinkingModel))
            {
                return _settings.ThinkingModel!;
            }

            if (!string.IsNullOrWhiteSpace(_settings.FastModel))
            {
                return _settings.FastModel!;
            }

            if (!string.IsNullOrWhiteSpace(_settings.EmbeddingModel))
            {
                return _settings.EmbeddingModel!;
            }

            throw new InvalidOperationException(
                "Model not configured for OpenAI-compatible backend. " +
                "Set 'model' (or at least one of thinkingModel/fastModel/embeddingModel) to a /v1/models id.");
        }

        return "gemini-2.5-flash";
    }

    /// <summary>
    /// Gets the API key.
    /// </summary>
    public string GetApiKey() => _settings.ApiKey ?? throw new InvalidOperationException("API key not configured");

    /// <summary>
    /// Checks if API key is configured.
    /// </summary>
    public bool HasApiKey() => !string.IsNullOrEmpty(_settings.ApiKey);

    /// <summary>
    /// Returns true when api key uses the Ollama sentinel value.
    /// </summary>
    public bool IsOllamaKey() => string.Equals(_settings.ApiKey, "ollama", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns true when the backend speaks OpenAI-compatible /v1/chat/completions.
    /// Matches LiteLLM (default port 4000), LM Studio (default port 1234),
    /// and any endpoint whose API key starts with "sk-" (standard OpenAI format).
    /// </summary>
    public bool IsOpenAICompatible()
    {
        var baseUrl = GetBaseUrl();
        if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            if (uri.Port == 1234) return true; // LM Studio default
            if (uri.Port == 4000) return true; // LiteLLM default
        }
        var key = _settings.ApiKey ?? string.Empty;
        return key.StartsWith("sk-", StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns true when base URL points to an Ollama endpoint.
    /// </summary>
    public bool IsOllamaBaseUrl()
    {
        var baseUrl = GetBaseUrl();
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var isLocalHost = uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                          uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase);
        var isOllamaPort = uri.Port == 11434 || uri.Port == -1;

        return (isLocalHost && isOllamaPort) ||
               uri.Host.Contains("ollama", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks whether current provider requires an API key.
    /// </summary>
    public bool RequiresApiKey() => !IsOllamaKey() && !IsOllamaBaseUrl();

    /// <summary>
    /// Gets the base URL for the API.
    /// </summary>
    public string GetBaseUrl() => _settings.BaseUrl ?? (IsOllamaKey() ? "http://localhost:11434" : "https://generativelanguage.googleapis.com");

    /// <summary>
    /// Gets the approval mode.
    /// </summary>
    public ApprovalMode GetApprovalMode() => _settings.ApprovalMode ?? ApprovalMode.Interactive;

    /// <summary>
    /// Checks if agents are enabled.
    /// </summary>
    public bool IsAgentsEnabled() => _settings.EnableAgents ?? true;

    /// <summary>
    /// Gets the maximum number of agent turns.
    /// </summary>
    public int GetMaxAgentTurns() => _settings.MaxAgentTurns ?? 10;

    /// <summary>
    /// Gets the agent timeout in seconds.
    /// </summary>
    public int GetAgentTimeout() => _settings.AgentTimeout ?? 300;

    /// <summary>
    /// Checks if telemetry is enabled.
    /// </summary>
    public bool IsTelemetryEnabled() => _settings.EnableTelemetry ?? false;

    /// <summary>
    /// Gets the maximum tokens for context.
    /// </summary>
    public int GetMaxTokens() => _settings.MaxTokens ?? 8192;

    /// <summary>
    /// Gets the temperature for generation.
    /// </summary>
    public double GetTemperature() => _settings.Temperature ?? 0.7;

    /// <summary>
    /// Gets the top P for generation.
    /// </summary>
    public double GetTopP() => _settings.TopP ?? 0.95;

    /// <summary>
    /// Gets the top K for generation.
    /// </summary>
    public int GetTopK() => _settings.TopK ?? 40;

    /// <summary>
    /// Checks if plan mode is enabled.
    /// </summary>
    public bool IsPlanModeEnabled() => _settings.EnablePlanMode ?? true;

    /// <summary>
    /// Gets whether to use sandbox for shell execution.
    /// </summary>
    public bool UseSandbox() => _settings.UseSandbox ?? false;

    /// <summary>
    /// Gets the sandbox type.
    /// </summary>
    public string? GetSandbox() => _settings.Sandbox;

    /// <summary>
    /// Gets the enabled extensions.
    /// </summary>
    public List<Extension> GetExtensions() => _settings.Extensions ?? new List<Extension>();

    /// <summary>
    /// Gets whether to enable code assistance.
    /// </summary>
    public bool IsCodeAssistEnabled() => _settings.EnableCodeAssist ?? false;

    /// <summary>
    /// Gets the embedding model for semantic search / RAG.
    /// </summary>
    public string GetEmbeddingModel()
    {
        if (!string.IsNullOrWhiteSpace(_settings.EmbeddingModel))
        {
            return _settings.EmbeddingModel!;
        }

        return IsOpenAICompatible() ? GetModel() : "bge-m3";
    }

    /// <summary>
    /// Gets the thinking model for complex reasoning and large-context tasks.
    /// </summary>
    public string GetThinkingModel()
    {
        if (!string.IsNullOrWhiteSpace(_settings.ThinkingModel))
        {
            return _settings.ThinkingModel!;
        }

        return IsOpenAICompatible() ? GetModel() : "gpt-oss:20b";
    }

    /// <summary>
    /// Gets the fast model for quick code edits and simple tasks.
    /// </summary>
    public string GetFastModel()
    {
        if (!string.IsNullOrWhiteSpace(_settings.FastModel))
        {
            return _settings.FastModel!;
        }

        return IsOpenAICompatible() ? GetModel() : "qwen2.5-coder:7b";
    }

    /// <summary>
    /// Saves the settings to storage.
    /// </summary>
    public async Task SaveSettingsAsync(bool userLevel = false)
    {
        await _storage.SaveSettingsAsync(_settings, userLevel);
    }

    /// <summary>
    /// Updates a setting value.
    /// </summary>
    public Config WithModel(string model) => WithSetting(s => s with { Model = model });
    public Config WithApiKey(string apiKey) => WithSetting(s => s with { ApiKey = apiKey });
    public Config WithBaseUrl(string baseUrl) => WithSetting(s => s with { BaseUrl = baseUrl });
    public Config WithApprovalMode(ApprovalMode mode) => WithSetting(s => s with { ApprovalMode = mode });
    public Config WithAgentsEnabled(bool enabled) => WithSetting(s => s with { EnableAgents = enabled });
    public Config WithTelemetryEnabled(bool enabled) => WithSetting(s => s with { EnableTelemetry = enabled });

    public void SetModel(string model) => WithModel(model);
    public void SetApiKey(string apiKey) => WithApiKey(apiKey);

    private Config WithSetting(Func<Settings, Settings> update)
    {
        _settings = update(_settings);
        return this;
    }

    /// <summary>
    /// Disposes the configuration.
    /// </summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
