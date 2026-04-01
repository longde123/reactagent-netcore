using AiCli.Core.Types;

using AiCli.Core.Types;

namespace AiCli.Core.Tools;

/// <summary>
/// Non-generic tool invocation interface used by executors and queueing.
/// </summary>
public interface IToolInvocation
{
    /// <summary>
    /// Untyped validated parameters for this invocation.
    /// </summary>
    object Parameters { get; }

    /// <summary>
    /// Executes the tool and returns an untyped ToolExecutionResult.
    /// </summary>
    Task<ToolExecutionResult> ExecuteAsync(
        CancellationToken cancellationToken,
        Action<ToolLiveOutput>? updateOutput = null);

    /// <summary>
    /// Internal tool name used by schedulers and logs.
    /// </summary>
    string ToolName { get; }
}

/// <summary>
/// Represents a validated and ready-to-execute tool call.
/// </summary>
/// <typeparam name="TParams">The type of parameters.</typeparam>
/// <typeparam name="TResult">The type of result (must be ToolExecutionResult).</typeparam>
public interface IToolInvocation<TParams, TResult> : IToolInvocation
    where TResult : ToolExecutionResult
{
    /// <summary>
    /// The validated parameters for this specific invocation.
    /// </summary>
    TParams Parameters { get; }

    /// <summary>
    /// Gets a pre-execution description of the tool operation.
    /// </summary>
    /// <returns>A markdown string describing what the tool will do.</returns>
    string GetDescription();

    /// <summary>
    /// Determines what file system paths the tool will affect.
    /// </summary>
    /// <returns>A list of such paths.</returns>
    IReadOnlyList<ToolLocation> GetToolLocations();

    /// <summary>
    /// Checks if the tool call should be confirmed by the user before execution.
    /// </summary>
    /// <param name="cancellationToken">A <see cref="CancellationToken"/> that can be used to cancel the confirmation request.</param>
    /// <returns>A <see cref="ToolCallConfirmationDetails"/> object if confirmation is required, or null if not.</returns>
    Task<ToolCallConfirmationDetails?> ShouldConfirmExecuteAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Executes the tool with validated parameters.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for tool cancellation.</param>
    /// <param name="updateOutput">Optional callback to stream output.</param>
    /// <returns>Result of tool execution.</returns>
    Task<TResult> ExecuteAsync(
        CancellationToken cancellationToken,
        Action<ToolLiveOutput>? updateOutput = null);
}

// NOTE: non-generic `IToolInvocation` is defined above; no alias needed here.
