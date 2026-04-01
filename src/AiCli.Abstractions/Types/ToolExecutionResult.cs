namespace AiCli.Core.Types;

/// <summary>
/// Result of a tool execution.
/// </summary>
public record ToolExecutionResult
{
    /// <summary>
    /// The content to be sent to the LLM.
    /// (optional for compatibility)
    /// </summary>
    public ContentPart? LlmContent { get; init; } = new TextContentPart("");

    /// <summary>
    /// The content to display to the user.
    /// (optional for compatibility)
    /// </summary>
    public ToolResultDisplay ReturnDisplay { get; init; } = new ToolResultDisplay.TextToolResultDisplay("");

    /// <summary>
    /// Error information if the tool execution failed.
    /// </summary>
    public ToolError? Error { get; init; }

    /// <summary>
    /// Additional structured data associated with the result.
    /// </summary>
    public Dictionary<string, object>? Data { get; init; }

    /// <summary>
    /// Tail tool call request for chaining tool calls.
    /// </summary>
    public ToolTailCall? TailToolCallRequest { get; init; }

    // Compatibility properties used by older code paths
    public bool IsError { get; set; }
    public string? Output { get; set; }
    public List<ContentPart>? Content { get; set; }

    /// <summary>
    /// Creates a successful result.
    /// </summary>
    public static ToolExecutionResult Success(string text, ToolResultDisplay display, Dictionary<string, object>? data = null) => new()
    {
        LlmContent = new TextContentPart(text),
        ReturnDisplay = display,
        Data = data,
        IsError = false,
        Output = text,
        Content = new List<ContentPart> { new TextContentPart(text) }
    };

    /// <summary>
    /// Creates a successful result with function response.
    /// </summary>
    public static ToolExecutionResult SuccessWithFunctionResponse(string name, Dictionary<string, object?> response, string id, ToolResultDisplay display) => new()
    {
        LlmContent = new FunctionResponsePart(new FunctionResponse { Name = name, Response = response, Id = id }),
        ReturnDisplay = display,
        IsError = false,
        Output = response != null ? System.Text.Json.JsonSerializer.Serialize(response) : null,
        Content = new List<ContentPart> { new FunctionResponsePart(new FunctionResponse { Name = name, Response = response, Id = id }) }
    };

    /// <summary>
    /// Creates a failed result.
    /// </summary>
    public static ToolExecutionResult Failure(string message, ToolErrorType errorType = ToolErrorType.Execution) => new()
    {
        LlmContent = new TextContentPart($"Error: {message}"),
        ReturnDisplay = new ToolResultDisplay.TextToolResultDisplay(message),
        Error = new ToolError { Message = message, ErrorType = errorType },
        IsError = true,
        Output = message,
        Content = new List<ContentPart> { new TextContentPart(message) }
    };
}

/// <summary>
/// Tool result display format.
/// </summary>
public abstract record ToolResultDisplay
{
    /// <summary>
    /// Text display result.
    /// </summary>
    public record TextToolResultDisplay(string Text) : ToolResultDisplay;

    /// <summary>
    /// Markdown display result.
    /// </summary>
    public record MarkdownToolResultDisplay(string Markdown) : ToolResultDisplay;

    /// <summary>
    /// JSON display result.
    /// </summary>
    public record JsonToolResultDisplay(string Json) : ToolResultDisplay;

    /// <summary>
    /// Silent display (no output shown to user).
    /// </summary>
    public record SilentToolResultDisplay : ToolResultDisplay;

    /// <summary>
    /// Gets the display text.
    /// </summary>
    public virtual string GetDisplayText() => this switch
    {
        TextToolResultDisplay t => t.Text,
        MarkdownToolResultDisplay m => m.Markdown,
        JsonToolResultDisplay j => j.Json,
        SilentToolResultDisplay => "",
        _ => ""
    };
}

// Backwards-compatible top-level types expected by older codepaths
public record TextToolResultDisplay(string Text) : ToolResultDisplay;
public record MarkdownToolResultDisplay(string Markdown) : ToolResultDisplay;
public record JsonToolResultDisplay(string Json) : ToolResultDisplay;

/// <summary>
/// Tool error information.
/// </summary>
public record ToolError
{
    /// <summary>
    /// The error message.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// The type of error.
    /// </summary>
    public required ToolErrorType ErrorType { get; init; }

    /// <summary>
    /// Additional error details.
    /// </summary>
    public Dictionary<string, object>? Details { get; init; }

    /// <summary>
    /// The original exception (if available).
    /// </summary>
    public Exception? Exception { get; init; }
}

/// <summary>
/// Types of tool errors.
/// </summary>
public enum ToolErrorType
{
    /// <summary>
    /// Validation error (invalid parameters).
    /// </summary>
    Validation,

    /// <summary>
    /// Execution error (tool failed to execute).
    /// </summary>
    Execution,

    /// <summary>
    /// Permission error (user denied permission).
    /// </summary>
    Permission,

    /// <summary>
    /// Timeout error.
    /// </summary>
    Timeout,

    /// <summary>
    /// Cancellation error.
    /// </summary>
    Cancellation,

    /// <summary>
    /// Not found error.
    /// </summary>
    NotFound,

    /// <summary>
    /// IO error (file system, network, etc.).
    /// </summary>
    IOError,

    /// <summary>
    /// Unknown error.
    /// </summary>
    Unknown
}

/// <summary>
/// Tail tool call request for chaining tool calls.
/// </summary>
public record ToolTailCall
{
    /// <summary>
    /// The name of the tool to call.
    /// </summary>
    public required string ToolName { get; init; }

    /// <summary>
    /// The parameters for the tool call.
    /// </summary>
    public required Dictionary<string, object?> Parameters { get; init; }
}
