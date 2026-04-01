namespace AiCli.Core.Types;

/// <summary>
/// Extension methods for various core types.
/// </summary>
public static class CoreTypeExtensions
{
    /// <summary>
    /// Determines if a tool kind is read-only.
    /// </summary>
    public static bool IsReadOnly(this ToolKind kind) => kind switch
    {
        ToolKind.Read or ToolKind.Search or ToolKind.Think or ToolKind.Plan or ToolKind.Fetch => true,
        _ => false
    };

    /// <summary>
    /// Determines if a tool kind is destructive (writes/modifies data).
    /// </summary>
    public static bool IsDestructive(this ToolKind kind) => kind switch
    {
        ToolKind.Edit or ToolKind.Delete or ToolKind.Move => true,
        _ => false
    };

    /// <summary>
    /// Determines if a tool kind requires user confirmation by default.
    /// </summary>
    public static bool RequiresConfirmation(this ToolKind kind) => kind switch
    {
        ToolKind.Execute or ToolKind.Edit or ToolKind.Delete or ToolKind.Move => true,
        _ => false
    };

    /// <summary>
    /// Determines if a tool call status is terminal (no longer active).
    /// </summary>
    public static bool IsTerminal(this ToolCallStatus status) => status switch
    {
        ToolCallStatus.Completed or ToolCallStatus.Failed or ToolCallStatus.Cancelled => true,
        _ => false
    };

    /// <summary>
    /// Determines if an agent terminate mode is a success.
    /// </summary>
    public static bool IsSuccess(this AgentTerminateMode mode) => mode switch
    {
        AgentTerminateMode.Goal => true,
        _ => false
    };

    /// <summary>
    /// Determines if a tool error is recoverable.
    /// </summary>
    public static bool IsRecoverable(this ToolError error) => error.ErrorType switch
    {
        ToolErrorType.Timeout or ToolErrorType.IOError => true,
        _ => false
    };
}
