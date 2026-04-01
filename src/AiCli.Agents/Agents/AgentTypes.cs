using AiCli.Core.Types;

namespace AiCli.Core.Agents;

/// <summary>
/// Types of agents.
/// </summary>
public enum AgentKind
{
    GeneralPurpose,
    Explore,
    Plan,
    Code,
    Research,
    Worker,
    Coordinator
}

/// <summary>
/// Agent execution state.
/// </summary>
public enum AgentExecutionState
{
    Idle,
    Running,
    Paused,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// Agent configuration.
/// </summary>
public record AgentConfig
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public AgentKind Kind { get; init; } = AgentKind.GeneralPurpose;
    public List<string> Capabilities { get; init; } = new();
    public Dictionary<string, object> Parameters { get; init; } = new();
    public int MaxTurns { get; init; } = 100;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(30);
    public bool AutoContinue { get; init; } = true;
    public ApprovalMode ApprovalMode { get; init; } = ApprovalMode.Auto;
}

/// <summary>
/// Result of agent execution.
/// </summary>
public record AgentResult
{
    public required AgentExecutionState State { get; init; }
    public List<ContentMessage> Messages { get; init; } = new();
    public List<string> ToolCalls { get; init; } = new();
    public TimeSpan Duration { get; init; }
    public Exception? Error { get; init; }
    public Dictionary<string, object> Metadata { get; init; } = new();
    public bool IsSuccess => State == AgentExecutionState.Completed;
}

/// <summary>
/// Agent execution request.
/// </summary>
public record AgentExecutionRequest
{
    public required IAgent Agent { get; init; }
    public required ContentMessage InitialMessage { get; init; }
    public required AgentConfig Config { get; init; }
    public Dictionary<string, object> Context { get; init; } = new();
    public CancellationToken CancellationToken { get; init; } = default;
}

/// <summary>
/// Event data for agent execution.
/// </summary>
public class AgentEvent : EventArgs
{
    public string AgentId { get; init; } = "";
    public AgentEventType Type { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string? Message { get; init; }
    public ContentMessage? ContentMessage { get; init; }
    public Exception? Error { get; init; }
    public Dictionary<string, object> Metadata { get; init; } = new();
}

/// <summary>
/// Types of agent events.
/// </summary>
public enum AgentEventType
{
    Started,
    MessageReceived,
    ToolCalled,
    ToolCompleted,
    TurnCompleted,
    Completed,
    Failed,
    Cancelled,
    Paused,
    Resumed,
    /// <summary>模型正在输出思考 token（reasoning 模型专用）。</summary>
    Thinking
}