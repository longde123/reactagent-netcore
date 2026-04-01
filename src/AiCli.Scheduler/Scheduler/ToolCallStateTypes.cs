using AiCli.Core.Types;
using AiCli.Core.ConfirmationBus;

namespace AiCli.Core.Scheduler;

/// <summary>
/// Internal statuses for the tool call state machine.
/// </summary>
public enum CoreToolCallStatus
{
    Validating,
    Scheduled,
    Executing,
    AwaitingApproval,
    Success,
    Error,
    Cancelled,
}

/// <summary>
/// Request info for a single tool call.
/// </summary>
public record ToolCallRequestInfo
{
    public required string CallId { get; init; }
    public required string Name { get; init; }
    public required Dictionary<string, object?> Args { get; init; }
    public string? OriginalRequestName { get; init; }
    public bool IsClientInitiated { get; init; }
    public string PromptId { get; init; } = string.Empty;
    public string? Checkpoint { get; init; }
    public string? TraceId { get; init; }
    public string? ParentCallId { get; init; }
    public string? SchedulerId { get; init; }
}

/// <summary>
/// Response info for a completed tool call.
/// </summary>
public record ToolCallResponseInfo
{
    public required string CallId { get; init; }
    public required IReadOnlyList<object> ResponseParts { get; init; }
    public string? ResultDisplay { get; init; }
    public Exception? Error { get; init; }
    public string? ErrorType { get; init; }
    public string? OutputFile { get; init; }
    public int? ContentLength { get; init; }
    public Dictionary<string, object>? Data { get; init; }
}

/// <summary>
/// Optional tail-call: execute another tool immediately after this one.
/// </summary>
public record TailToolCallRequest
{
    public required string Name { get; init; }
    public required Dictionary<string, object?> Args { get; init; }
}

// ─── State machine call shapes ────────────────────────────────────────────────

public abstract record ToolCallBase
{
    public abstract CoreToolCallStatus Status { get; }
    public required ToolCallRequestInfo Request { get; init; }
    public string? SchedulerId { get; init; }
    public ToolConfirmationOutcome? Outcome { get; init; }
    public ApprovalMode? ApprovalMode { get; init; }
}

public record ValidatingToolCall : ToolCallBase
{
    public override CoreToolCallStatus Status => CoreToolCallStatus.Validating;
    public long? StartTime { get; init; }
}

public record ScheduledToolCall : ToolCallBase
{
    public override CoreToolCallStatus Status => CoreToolCallStatus.Scheduled;
    public long? StartTime { get; init; }
}

public record ExecutingToolCall : ToolCallBase
{
    public override CoreToolCallStatus Status => CoreToolCallStatus.Executing;
    public long? StartTime { get; init; }
    public string? LiveOutput { get; init; }
    public string? ProgressMessage { get; init; }
    public double? ProgressPercent { get; init; }
    public long? Progress { get; init; }
    public long? ProgressTotal { get; init; }
    public int? Pid { get; init; }
}

public record WaitingToolCall : ToolCallBase
{
    public override CoreToolCallStatus Status => CoreToolCallStatus.AwaitingApproval;
    public required SerializableConfirmationDetails ConfirmationDetails { get; init; }
    public string? CorrelationId { get; init; }
    public long? StartTime { get; init; }
}

public record SuccessfulToolCall : ToolCallBase
{
    public override CoreToolCallStatus Status => CoreToolCallStatus.Success;
    public required ToolCallResponseInfo Response { get; init; }
    public long? DurationMs { get; init; }
    public TailToolCallRequest? TailToolCallRequest { get; init; }
}

public record ErroredToolCall : ToolCallBase
{
    public override CoreToolCallStatus Status => CoreToolCallStatus.Error;
    public required ToolCallResponseInfo Response { get; init; }
    public long? DurationMs { get; init; }
    public TailToolCallRequest? TailToolCallRequest { get; init; }
}

public record CancelledToolCall : ToolCallBase
{
    public override CoreToolCallStatus Status => CoreToolCallStatus.Cancelled;
    public required ToolCallResponseInfo Response { get; init; }
    public long? DurationMs { get; init; }
}

/// <summary>
/// Union type for any tool call state.
/// </summary>
public static class ToolCallHelpers
{
    public static bool IsTerminal(ToolCallBase call) =>
        call.Status is CoreToolCallStatus.Success
            or CoreToolCallStatus.Error
            or CoreToolCallStatus.Cancelled;

    public static bool IsCompleted(ToolCallBase call) => IsTerminal(call);
}
