namespace AiCli.Core.Types;

/// <summary>
/// Stream event from the LLM API.
/// </summary>
public abstract record StreamEvent
{
    /// <summary>
    /// Content chunk event.
    /// </summary>
    public record ChunkStreamEvent(ContentMessage Content) : StreamEvent;

    /// <summary>
    /// Retry event (when the API needs to retry).
    /// </summary>
    public record RetryStreamEvent(int RetryCount, string? Reason) : StreamEvent;

    /// <summary>
    /// Agent execution stopped event.
    /// </summary>
    public record AgentExecutionStoppedEvent(string Reason) : StreamEvent;

    /// <summary>
    /// Agent execution blocked event.
    /// </summary>
    public record AgentExecutionBlockedEvent(string Reason) : StreamEvent;

    /// <summary>
    /// Gets the stream event type.
    /// </summary>
    public StreamEventType Type => this switch
    {
        ChunkStreamEvent => StreamEventType.Chunk,
        RetryStreamEvent => StreamEventType.Retry,
        AgentExecutionStoppedEvent => StreamEventType.AgentExecutionStopped,
        AgentExecutionBlockedEvent => StreamEventType.AgentExecutionBlocked,
        _ => throw new InvalidOperationException("Unknown stream event type")
    };
}

/// <summary>
/// Tool location for tracking files affected by tool execution.
/// </summary>
public record ToolLocation
{
    /// <summary>
    /// The file path.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// The line number (if applicable).
    /// </summary>
    public int? Line { get; init; }

    /// <summary>
    /// The column (if applicable).
    /// </summary>
    public int? Column { get; init; }
}

/// <summary>
/// Live output from a tool during execution.
/// </summary>
public record ToolLiveOutput
{
    /// <summary>
    /// The output text.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// Whether this is an error output.
    /// </summary>
    public bool IsError { get; init; }

    /// <summary>
    /// Whether this is the last output.
    /// </summary>
    public bool IsFinal { get; init; }
}

/// <summary>
/// Tool call confirmation details.
/// </summary>
public record ToolCallConfirmationDetails
{
    /// <summary>
    /// The tool name.
    /// </summary>
    public required string ToolName { get; init; }

    /// <summary>
    /// The tool description.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// The parameters for the tool call.
    /// </summary>
    public required Dictionary<string, object?> Parameters { get; init; }

    /// <summary>
    /// The tool kind.
    /// </summary>
    public required ToolKind Kind { get; init; }

    /// <summary>
    /// Files that will be affected by this tool call.
    /// </summary>
    public required IReadOnlyList<ToolLocation> Locations { get; init; }

    /// <summary>
    /// Whether the tool is read-only.
    /// </summary>
    public required bool IsReadOnly { get; init; }

    /// <summary>
    /// Whether user confirmation is required based on policy.
    /// </summary>
    public required bool RequiresConfirmation { get; init; }

    /// <summary>
    /// The policy decision (if any).
    /// </summary>
    public PolicyDecision? PolicyDecision { get; init; }
}
