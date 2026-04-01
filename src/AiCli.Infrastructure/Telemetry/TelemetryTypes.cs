using AiCli.Core.Types;

namespace AiCli.Core.Telemetry;

/// <summary>
/// Telemetry event types.
/// </summary>
public enum TelemetryEventType
{
    /// <summary>
    /// Application started.
    /// </summary>
    AppStart,

    /// <summary>
    /// Application exited.
    /// </summary>
    AppExit,

    /// <summary>
    /// Tool execution started.
    /// </summary>
    ToolExecutionStart,

    /// <summary>
    /// Tool execution completed.
    /// </summary>
    ToolExecutionComplete,

    /// <summary>
    /// Tool execution failed.
    /// </summary>
    ToolExecutionFailed,

    /// <summary>
    /// Agent execution started.
    /// </summary>
    AgentExecutionStart,

    /// <summary>
    /// Agent execution completed.
    /// </summary>
    AgentExecutionComplete,

    /// <summary>
    /// Chat message sent.
    /// </summary>
    ChatMessageSent,

    /// <summary>
    /// Chat response received.
    /// </summary>
    ChatResponseReceived,

    /// <summary>
    /// Error occurred.
    /// </summary>
    Error,

    /// <summary>
    /// User action.
    /// </summary>
    UserAction
}

/// <summary>
/// Base telemetry event.
/// </summary>
public abstract record TelemetryEvent
{
    /// <summary>
    /// The event type.
    /// </summary>
    public abstract TelemetryEventType EventType { get; }

    /// <summary>
    /// The timestamp of the event.
    /// </summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// The session ID.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Additional event properties.
    /// </summary>
    public Dictionary<string, object>? Properties { get; init; }
}

/// <summary>
/// Application start event.
/// </summary>
public record AppStartEvent : TelemetryEvent
{
    public override TelemetryEventType EventType => TelemetryEventType.AppStart;

    /// <summary>
    /// The application version.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    /// The operating system.
    /// </summary>
    public required string Os { get; init; }

    /// <summary>
    /// Whether this is the first run.
    /// </summary>
    public bool IsFirstRun { get; init; }
}

/// <summary>
/// Application exit event.
/// </summary>
public record AppExitEvent : TelemetryEvent
{
    public override TelemetryEventType EventType => TelemetryEventType.AppExit;

    /// <summary>
    /// The exit code.
    /// </summary>
    public int ExitCode { get; init; }

    /// <summary>
    /// The duration of the session in seconds.
    /// </summary>
    public double DurationSeconds { get; init; }

    /// <summary>
    /// The reason for exit.
    /// </summary>
    public string? ExitReason { get; init; }
}

/// <summary>
/// Tool execution event.
/// </summary>
public record ToolExecutionEvent : TelemetryEvent
{
    public override TelemetryEventType EventType =>
        Success ? TelemetryEventType.ToolExecutionComplete : TelemetryEventType.ToolExecutionFailed;

    /// <summary>
    /// The tool name.
    /// </summary>
    public required string ToolName { get; init; }

    /// <summary>
    /// Whether the execution was successful.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// The duration in milliseconds.
    /// </summary>
    public required double DurationMs { get; init; }

    /// <summary>
    /// The error message (if failed).
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Whether the tool requires confirmation.
    /// </summary>
    public bool RequiresConfirmation { get; init; }
}

/// <summary>
/// Agent execution event.
/// </summary>
public record AgentExecutionEvent : TelemetryEvent
{
    public override TelemetryEventType EventType =>
        Success ? TelemetryEventType.AgentExecutionComplete : TelemetryEventType.AgentExecutionComplete;

    /// <summary>
    /// The agent name.
    /// </summary>
    public required string AgentName { get; init; }

    /// <summary>
    /// Whether the execution was successful.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// The number of turns executed.
    /// </summary>
    public required int Turns { get; init; }

    /// <summary>
    /// The duration in seconds.
    /// </summary>
    public required double DurationSeconds { get; init; }

    /// <summary>
    /// The termination mode.
    /// </summary>
    public AgentTerminateMode TerminateMode { get; init; }
}

/// <summary>
/// Error event.
/// </summary>
public record ErrorEvent : TelemetryEvent
{
    public override TelemetryEventType EventType => TelemetryEventType.Error;

    /// <summary>
    /// The error type.
    /// </summary>
    public required string ErrorType { get; init; }

    /// <summary>
    /// The error message.
    /// </summary>
    public required string ErrorMessage { get; init; }

    /// <summary>
    /// The stack trace.
    /// </summary>
    public string? StackTrace { get; init; }

    /// <summary>
    /// Additional error details.
    /// </summary>
    public Dictionary<string, string>? Details { get; init; }
}
