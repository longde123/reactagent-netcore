namespace AiCli.Core.Types;

/// <summary>
/// The role of a message in the conversation.
/// </summary>
public enum LlmRole
{
    /// <summary>
    /// Message from the user.
    /// </summary>
    User,

    /// <summary>
    /// Message from the model/assistant.
    /// </summary>
    Model,

    /// <summary>
    /// System instruction message.
    /// </summary>
    System,

    /// <summary>
    /// Message from a function/tool execution.
    /// </summary>
    Function,

    /// <summary>
    /// Assistant/model messages (alias for Model for backward compatibility).
    /// </summary>
    Assistant = Model
}

/// <summary>
/// The kind/type of a tool.
/// </summary>
public enum ToolKind
{
    /// <summary>
    /// Read-only operations (e.g., reading files).
    /// </summary>
    Read = 0,

    /// <summary>
    /// Edit operations (e.g., editing files).
    /// </summary>
    Edit = 1,

    /// <summary>
    /// Delete operations (e.g., deleting files).
    /// </summary>
    Delete = 2,

    /// <summary>
    /// Move operations (e.g., renaming/moving files).
    /// </summary>
    Move = 3,

    /// <summary>
    /// Search operations (e.g., grep search).
    /// </summary>
    Search = 4,

    /// <summary>
    /// Execute operations (e.g., running shell commands).
    /// </summary>
    Execute = 5,

    /// <summary>
    /// Internal reasoning/thinking.
    /// </summary>
    Think = 6,

    /// <summary>
    /// Agent/sub-agent invocation.
    /// </summary>
    Agent = 7,

    /// <summary>
    /// Fetch operations (e.g., web fetch).
    /// </summary>
    Fetch = 8,

    /// <summary>
    /// Communication operations.
    /// </summary>
    Communicate = 9,

    /// <summary>
    /// Plan mode operations.
    /// </summary>
    Plan = 10,

    /// <summary>
    /// Switch mode operations.
    /// </summary>
    SwitchMode = 11,

    /// <summary>
    /// Other operations.
    /// </summary>
    Other = 12
}

/// <summary>
/// Approval mode for tool execution.
/// </summary>
public enum ApprovalMode
{
    /// <summary>
    /// Auto-approve all tool executions.
    /// </summary>
    Auto = 0,

    /// <summary>
    /// Require manual approval for all tool executions.
    /// </summary>
    Manual = 1,

    /// <summary>
    /// Interactive approval - ask user when needed.
    /// </summary>
    Interactive = 2
}

/// <summary>
/// Policy decision for tool execution.
/// </summary>
public enum PolicyDecision
{
    /// <summary>
    /// Allow the tool execution.
    /// </summary>
    Allow = 0,

    /// <summary>
    /// Deny the tool execution.
    /// </summary>
    Deny = 1,

    /// <summary>
    /// Ask the user for approval.
    /// </summary>
    AskUser = 2
}

/// <summary>
/// Tool confirmation outcome.
/// </summary>
public enum ToolConfirmationOutcome
{
    /// <summary>
    /// Proceed once for this operation.
    /// </summary>
    ProceedOnce = 0,

    /// <summary>
    /// Always proceed for this tool.
    /// </summary>
    ProceedAlways = 1,

    /// <summary>
    /// Always proceed and save the decision.
    /// </summary>
    ProceedAlwaysAndSave = 2,

    /// <summary>
    /// Always proceed on the server.
    /// </summary>
    ProceedAlwaysServer = 3,

    /// <summary>
    /// Always proceed for this tool.
    /// </summary>
    ProceedAlwaysTool = 4,

    /// <summary>
    /// Modify the operation with an editor.
    /// </summary>
    ModifyWithEditor = 5,

    /// <summary>
    /// Cancel the operation.
    /// </summary>
    Cancel = 6
}

/// <summary>
/// Stream event type.
/// </summary>
public enum StreamEventType
{
    /// <summary>
    /// Content chunk received.
    /// </summary>
    Chunk = 0,

    /// <summary>
    /// Retry event.
    /// </summary>
    Retry = 1,

    /// <summary>
    /// Agent execution stopped.
    /// </summary>
    AgentExecutionStopped = 2,

    /// <summary>
    /// Agent execution blocked.
    /// </summary>
    AgentExecutionBlocked = 3
}

/// <summary>
/// Tool call status.
/// </summary>
public enum ToolCallStatus
{
    /// <summary>
    /// Tool call is pending.
    /// </summary>
    Pending,

    /// <summary>
    /// Tool call is running.
    /// </summary>
    Running,

    /// <summary>
    /// Tool call completed successfully.
    /// </summary>
    Completed,

    /// <summary>
    /// Tool call failed.
    /// </summary>
    Failed,

    /// <summary>
    /// Tool call was cancelled.
    /// </summary>
    Cancelled
}

/// <summary>
/// Agent termination mode.
/// </summary>
public enum AgentTerminateMode
{
    /// <summary>
    /// Agent terminated due to an error.
    /// </summary>
    Error,

    /// <summary>
    /// Agent terminated due to timeout.
    /// </summary>
    Timeout,

    /// <summary>
    /// Agent completed its goal.
    /// </summary>
    Goal,

    /// <summary>
    /// Agent reached maximum turns.
    /// </summary>
    MaxTurns,

    /// <summary>
    /// Agent was aborted by user.
    /// </summary>
    Aborted
}
