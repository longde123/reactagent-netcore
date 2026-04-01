using AiCli.Core.Types;

namespace AiCli.Core.ConfirmationBus;

/// <summary>
/// Types of messages on the confirmation bus.
/// </summary>
public enum MessageBusType
{
    ToolConfirmationRequest = 0,
    ToolConfirmationResponse = 1,
    ToolPolicyRejection = 2,
    ToolExecutionSuccess = 3,
    ToolExecutionFailure = 4,
    UpdatePolicy = 5,
    ToolCallsUpdate = 6,
    AskUserRequest = 7,
    AskUserResponse = 8,
}

/// <summary>
/// Base interface for all confirmation bus messages.
/// </summary>
public interface IConfirmationMessage
{
    MessageBusType Type { get; }
}

/// <summary>
/// Represents a function call on the bus (serializable).
/// </summary>
public record FunctionCallRef
{
    public required string Name { get; init; }
    public Dictionary<string, object?> Args { get; init; } = new();
    public string? Id { get; init; }
}

/// <summary>
/// Request for tool execution confirmation.
/// </summary>
public record ToolConfirmationRequest : IConfirmationMessage
{
    public MessageBusType Type => MessageBusType.ToolConfirmationRequest;
    public required string CorrelationId { get; init; }
    public required FunctionCallRef ToolCall { get; init; }
    public string? ServerName { get; init; }
    public Dictionary<string, object>? ToolAnnotations { get; init; }
    public SerializableConfirmationDetails? Details { get; init; }
}

/// <summary>
/// Response to a tool execution confirmation request.
/// </summary>
public record ToolConfirmationResponse : IConfirmationMessage
{
    public MessageBusType Type => MessageBusType.ToolConfirmationResponse;
    public required string CorrelationId { get; init; }
    public required bool Confirmed { get; init; }
    public ToolConfirmationOutcome? Outcome { get; init; }
    public ToolConfirmationPayload? Payload { get; init; }
    /// <summary>
    /// When true, indicates ASK_USER policy decision – tool should show its own confirmation UI.
    /// </summary>
    public bool RequiresUserConfirmation { get; init; }
}

/// <summary>
/// Optional payload carried with a confirmation response (e.g. modified content).
/// </summary>
public record ToolConfirmationPayload
{
    public string? ModifiedContent { get; init; }
}

/// <summary>
/// Message indicating a tool was rejected by policy.
/// </summary>
public record ToolPolicyRejection : IConfirmationMessage
{
    public MessageBusType Type => MessageBusType.ToolPolicyRejection;
    public required FunctionCallRef ToolCall { get; init; }
}

/// <summary>
/// Message indicating a tool executed successfully.
/// </summary>
public record ToolExecutionSuccess<T> : IConfirmationMessage
{
    public MessageBusType Type => MessageBusType.ToolExecutionSuccess;
    public required FunctionCallRef ToolCall { get; init; }
    public required T Result { get; init; }
}

/// <summary>
/// Message indicating a tool execution failed.
/// </summary>
public record ToolExecutionFailure : IConfirmationMessage
{
    public MessageBusType Type => MessageBusType.ToolExecutionFailure;
    public required FunctionCallRef ToolCall { get; init; }
    public required Exception Error { get; init; }
}

/// <summary>
/// Message to update policy for a tool.
/// </summary>
public record UpdatePolicyMessage : IConfirmationMessage
{
    public MessageBusType Type => MessageBusType.UpdatePolicy;
    public required string ToolName { get; init; }
    public bool Persist { get; init; }
    public string? ArgsPattern { get; init; }
    public string[]? CommandPrefix { get; init; }
    public string? McpName { get; init; }
}

/// <summary>
/// Message carrying a snapshot of active tool calls.
/// </summary>
public record ToolCallsUpdateMessage : IConfirmationMessage
{
    public MessageBusType Type => MessageBusType.ToolCallsUpdate;
    public required IReadOnlyList<ToolCallSnapshot> ToolCalls { get; init; }
    public required string SchedulerId { get; init; }
}

/// <summary>
/// Lightweight snapshot of a tool call for bus transmission.
/// </summary>
public record ToolCallSnapshot
{
    public required string CallId { get; init; }
    public required string Name { get; init; }
    public required string Status { get; init; }
    public string? SchedulerId { get; init; }
}

/// <summary>
/// Request to ask the user questions.
/// </summary>
public record AskUserRequest : IConfirmationMessage
{
    public MessageBusType Type => MessageBusType.AskUserRequest;
    public required string CorrelationId { get; init; }
    public required IReadOnlyList<Question> Questions { get; init; }
}

/// <summary>
/// Response to an AskUser request.
/// </summary>
public record AskUserResponse : IConfirmationMessage
{
    public MessageBusType Type => MessageBusType.AskUserResponse;
    public required string CorrelationId { get; init; }
    public required Dictionary<string, string> Answers { get; init; }
    public bool Cancelled { get; init; }
}

// ─── Question Types ───────────────────────────────────────────────────────────

public enum QuestionType
{
    Choice,
    Text,
    YesNo,
}

public record QuestionOption
{
    public required string Label { get; init; }
    public required string Description { get; init; }
}

public record Question
{
    public required string Header { get; init; }
    public required string QuestionText { get; init; }
    public required QuestionType Type { get; init; }
    public IReadOnlyList<QuestionOption>? Options { get; init; }
    public bool MultiSelect { get; init; }
    public string? Placeholder { get; init; }
}

// ─── Serializable Confirmation Details ────────────────────────────────────────

/// <summary>
/// Discriminated union for confirmation details sent over the bus.
/// </summary>
public abstract record SerializableConfirmationDetails
{
    public abstract string DetailsType { get; }
}

public record InfoConfirmationDetails : SerializableConfirmationDetails
{
    public override string DetailsType => "info";
    public required string Title { get; init; }
    public required string Prompt { get; init; }
    public string[]? Urls { get; init; }
}

public record EditConfirmationDetails : SerializableConfirmationDetails
{
    public override string DetailsType => "edit";
    public required string Title { get; init; }
    public required string FileName { get; init; }
    public required string FilePath { get; init; }
    public required string FileDiff { get; init; }
    public string? OriginalContent { get; init; }
    public required string NewContent { get; init; }
    public bool IsModifying { get; init; }
}

public record ExecConfirmationDetails : SerializableConfirmationDetails
{
    public override string DetailsType => "exec";
    public required string Title { get; init; }
    public required string Command { get; init; }
    public required string RootCommand { get; init; }
    public required string[] RootCommands { get; init; }
    public string[]? Commands { get; init; }
}

public record McpConfirmationDetails : SerializableConfirmationDetails
{
    public override string DetailsType => "mcp";
    public required string Title { get; init; }
    public required string ServerName { get; init; }
    public required string ToolName { get; init; }
    public required string ToolDisplayName { get; init; }
    public Dictionary<string, object>? ToolArgs { get; init; }
    public string? ToolDescription { get; init; }
    public object? ToolParameterSchema { get; init; }
}

public record AskUserConfirmationDetails : SerializableConfirmationDetails
{
    public override string DetailsType => "ask_user";
    public required string Title { get; init; }
    public required IReadOnlyList<Question> Questions { get; init; }
}

public record ExitPlanModeConfirmationDetails : SerializableConfirmationDetails
{
    public override string DetailsType => "exit_plan_mode";
    public required string Title { get; init; }
    public required string PlanPath { get; init; }
}