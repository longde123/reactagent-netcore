using AiCli.Core.Logging;
using AiCli.Core.Types;

namespace AiCli.Core.Chat;

/// <summary>
/// Manages a chat session with the Gemini model.
/// </summary>
public class AiChat : IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly IContentGenerator _contentGenerator;
    private readonly SemaphoreSlim _sendSemaphore;
    private readonly List<ContentMessage> _history;
    private string _systemInstruction;
    private List<Tool>? _tools;
    private ToolConfig? _toolConfig;
    private bool _disposed;

    /// <summary>
    /// The model being used for this chat.
    /// </summary>
    public string Model { get; }

    /// <summary>
    /// Gets the chat history.
    /// </summary>
    public IReadOnlyList<ContentMessage> History => _history.AsReadOnly();

    /// <summary>
    /// Gets or sets the system instruction.
    /// </summary>
    public string SystemInstruction
    {
        get => _systemInstruction;
        set => _systemInstruction = value;
    }

    /// <summary>
    /// Gets or sets the available tools.
    /// </summary>
    public List<Tool>? Tools
    {
        get => _tools;
        set => _tools = value;
    }

    /// <summary>
    /// Initializes a new instance of the AiChat class.
    /// </summary>
    public AiChat(
        IContentGenerator contentGenerator,
        string model,
        string systemInstruction = "",
        List<Tool>? tools = null,
        List<ContentMessage>? history = null)
    {
        _logger = LoggerHelper.ForContext<AiChat>();
        _contentGenerator = contentGenerator;
        Model = model;
        _systemInstruction = systemInstruction;
        _tools = tools;
        _history = history ?? new List<ContentMessage>();
        _sendSemaphore = new SemaphoreSlim(1, 1);

        _logger.Debug("AiChat initialized with model: {Model}, history size: {HistorySize}",
            model, _history.Count);
    }

    /// <summary>
    /// Sends a message to the model (non-streaming).
    /// </summary>
    public async Task<GenerateContentResponse> SendMessageAsync(
        IReadOnlyList<ContentPart> parts,
        CancellationToken cancellationToken = default)
    {
        await _sendSemaphore.WaitAsync(cancellationToken);

        try
        {
            var userMessage = new ContentMessage
            {
                Role = LlmRole.User,
                Parts = parts.ToList()
            };

            _history.Add(userMessage);

            _logger.Verbose("Sending message, history size: {HistorySize}", _history.Count);

            var request = new GenerateContentRequest
            {
                Model = Model,
                Contents = _history.ToList(),
                SystemInstruction = _systemInstruction,
                Tools = _tools,
                ToolConfig = _toolConfig
            };

            var response = await _contentGenerator.GenerateContentAsync(request, cancellationToken);

            AddResponseToHistory(response);

            return response;
        }
        finally
        {
            _sendSemaphore.Release();
        }
    }

    /// <summary>
    /// Sends a message to the model with streaming.
    /// </summary>
    /// <param name="generatorOverride">
    /// 可选：覆盖默认生成器，用于多模型路由场景（如快速模型处理简单指令）。
    /// </param>
    public async IAsyncEnumerable<GenerateContentResponse> SendMessageStreamAsync(
        IReadOnlyList<ContentPart> parts,
        IContentGenerator? generatorOverride = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await _sendSemaphore.WaitAsync(cancellationToken);

        var generator = generatorOverride ?? _contentGenerator;

        try
        {
            var userMessage = new ContentMessage
            {
                Role = LlmRole.User,
                Parts = parts.ToList()
            };

            _history.Add(userMessage);

            _logger.Verbose("Sending message (streaming), history size: {HistorySize}", _history.Count);

            var request = new GenerateContentRequest
            {
                Model = generator.GetModelId(),
                Contents = _history.ToList(),
                SystemInstruction = _systemInstruction,
                Tools = _tools,
                ToolConfig = _toolConfig,
                CancellationToken = cancellationToken
            };

            // Accumulate streaming response (only TextContentPart goes to history, not thinking)
            TextContentPart? accumulatedText = null;
            GenerateContentResponse? finalResponse = null;

            await foreach (var chunk in generator.GenerateContentStreamAsync(request, cancellationToken))
            {
                yield return chunk;

                if (chunk.Candidates.Count == 0) continue;

                foreach (var part in chunk.Candidates[0].Content)
                {
                    if (part is TextContentPart textPart)
                    {
                        accumulatedText = accumulatedText is null
                            ? textPart
                            : new TextContentPart(accumulatedText.Text + textPart.Text);
                        finalResponse = chunk;
                    }
                    // ThinkingContentPart is intentionally excluded from history
                }
            }

            // Add the final response to history (only accumulated text, no thinking)
            if (finalResponse is not null && accumulatedText is not null)
            {
                AddResponseToHistory(finalResponse with
                {
                    Candidates = finalResponse.Candidates.Select((c, i) =>
                        i == 0 ? c with { Content = new List<ContentPart> { accumulatedText } } : c
                    ).ToList()
                });
            }

            _logger.Verbose("Streaming completed, final history size: {HistorySize}", _history.Count);
        }
        finally
        {
            _sendSemaphore.Release();
        }
    }

    /// <summary>
    /// Adds the model's response to the history.
    /// </summary>
    private void AddResponseToHistory(GenerateContentResponse response)
    {
        if (response.Candidates.Count == 0)
        {
            _logger.Warning("Received response with no candidates");
            return;
        }

        var candidate = response.Candidates[0];
        var modelMessage = new ContentMessage
        {
            Role = LlmRole.Model,
            Parts = candidate.Content
        };

        _history.Add(modelMessage);
        _logger.Verbose("Added response to history, new size: {HistorySize}", _history.Count);
    }

    /// <summary>
    /// Clears the chat history.
    /// </summary>
    public void ClearHistory()
    {
        _history.Clear();
        _logger.Debug("Chat history cleared");
    }

    /// <summary>
    /// Sets the system instruction.
    /// </summary>
    public void SetSystemInstruction(string instruction)
    {
        _systemInstruction = instruction;
        _logger.Debug("System instruction updated");
    }

    /// <summary>
    /// Sets the tools available to the model.
    /// </summary>
    public void SetTools(List<Tool> tools)
    {
        _tools = tools;
        _logger.Debug("Tools updated, count: {ToolCount}", tools.Count);
    }

    /// <summary>
    /// Sets the tool configuration.
    /// </summary>
    public void SetToolConfig(ToolConfig config)
    {
        _toolConfig = config;
        _logger.Debug("Tool config updated");
    }

    /// <summary>
    /// Gets the curated history (removes empty/invalid messages).
    /// </summary>
    public IReadOnlyList<ContentMessage> GetCuratedHistory()
    {
        return _history
            .Where(m => !m.IsEmpty())
            .Where(m => m.Parts.Count > 0)
            .ToList();
    }

    /// <summary>
    /// Checks if there are any function calls in the last response.
    /// </summary>
    public bool LastResponseHasFunctionCalls()
    {
        if (_history.Count == 0) return false;

        var lastMessage = _history[^1];
        return lastMessage.HasFunctionCalls();
    }

    /// <summary>
    /// Gets the function calls from the last response.
    /// </summary>
    public IReadOnlyList<FunctionCall> GetLastFunctionCalls()
    {
        if (_history.Count == 0) return Array.Empty<FunctionCall>();

        var lastMessage = _history[^1];
        return lastMessage.GetFunctionCalls().ToList();
    }

    /// <summary>
    /// Disposes the chat.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        _disposed = true;
        _sendSemaphore.Dispose();
        _logger.Debug("AiChat disposed");
        await ValueTask.CompletedTask;
    }
}
