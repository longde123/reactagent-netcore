using AiCli.Core.Tools;

namespace AiCli.Core.Scheduler;

/// <summary>
/// Execution options for tools.
/// </summary>
public class ExecutionOptions
{
    public bool ContinueOnError { get; init; } = false;
    public int MaxConcurrentExecutions { get; init; } = 1;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
    public bool ValidateBeforeExecution { get; init; } = true;
}

/// <summary>
/// Event data for tool execution events.
/// </summary>
public record ToolExecutionEvent
{
    public required string ExecutionId { get; init; }
    public required ToolExecutionEventType Type { get; init; }
    public required IToolInvocation Invocation { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public ToolExecutionResult? Result { get; init; }
    public Exception? Error { get; init; }
    public Dictionary<string, object> Metadata { get; init; } = new();
}

/// <summary>
/// Types of tool execution events.
/// </summary>
public enum ToolExecutionEventType
{
    Queued,
    Started,
    Completed,
    Failed,
    Cancelled,
    RequiresApproval,
    Approved,
    Rejected
}
