using AiCli.Core.Logging;
using AiCli.Core.Logging;
using AiCli.Core.Tools;
using AiCli.Core.Types;
using Serilog;
using System.Diagnostics;

namespace AiCli.Core.Agents;

/// <summary>
/// Local executor for running tools directly.
/// </summary>
public class LocalExecutor : IToolExecutor
{
    private readonly ILogger _logger;
    private readonly ApprovalMode _defaultApprovalMode;

    public LocalExecutor(ApprovalMode defaultApprovalMode = ApprovalMode.Auto)
    {
        _logger = LoggerHelper.ForContext<LocalExecutor>();
        _defaultApprovalMode = defaultApprovalMode;
    }

    /// <summary>
    /// Event raised when a tool execution starts.
    /// </summary>
    public event EventHandler<ToolExecutionEventArgs>? ToolExecutionStarted;

    /// <summary>
    /// Event raised when a tool execution completes.
    /// </summary>
    public event EventHandler<ToolExecutionEventArgs>? ToolExecutionCompleted;

    /// <summary>
    /// Event raised when a tool execution fails.
    /// </summary>
    public event EventHandler<ToolExecutionErrorEventArgs>? ToolExecutionFailed;

    /// <summary>
    /// Executes a tool locally.
    /// </summary>
    public async Task<ToolExecutionResult> ExecuteAsync(
        IToolInvocation invocation,
        ToolExecutionOptions options,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        _logger.Information(
            "Executing tool locally: {ToolName}, Options: {Options}",
            invocation.GetType().Name,
            options);

        ToolExecutionStarted?.Invoke(this, new ToolExecutionEventArgs { Invocation = invocation });

        try
        {
            // Setup live output handler
            void OnOutput(ToolLiveOutput output)
            {
                _logger.Verbose("Tool output: {ToolName}, Output: {Output}",
                    invocation.GetType().Name, output);
            }

            // Execute with timeout
            var timeoutTask = Task.Delay(options.Timeout, cancellationToken);
            var executionTask = invocation.ExecuteAsync(
                cancellationToken,
                options.LiveOutputHandler ?? OnOutput);

            var completedTask = await Task.WhenAny(executionTask, timeoutTask);

            ToolExecutionResult result;

            if (completedTask == timeoutTask)
            {
                throw new TimeoutException(
                    $"Tool execution timed out after {options.Timeout.TotalSeconds} seconds");
            }

            result = await executionTask;

            stopwatch.Stop();

            _logger.Information(
                "Tool executed: {ToolName}, Duration: {Ms}ms, Success: {IsSuccess}",
                invocation.GetType().Name,
                stopwatch.ElapsedMilliseconds,
                result.Error is null);

            ToolExecutionCompleted?.Invoke(this,
                new ToolExecutionEventArgs { Invocation = invocation, Result = result });

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();

            _logger.Information("Tool execution cancelled: {ToolName}", invocation.GetType().Name);

            return ToolExecutionResult.Failure(
                "Operation cancelled by user",
                ToolErrorType.Cancellation);
        }
        catch (TimeoutException ex)
        {
            stopwatch.Stop();

            _logger.Error(ex, "Tool execution timed out: {ToolName}", invocation.GetType().Name);

            ToolExecutionFailed?.Invoke(this,
                new ToolExecutionErrorEventArgs { Invocation = invocation, Error = ex });

            return ToolExecutionResult.Failure(
                ex.Message,
                ToolErrorType.Timeout);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            _logger.Error(ex, "Tool execution failed: {ToolName}", invocation.GetType().Name);

            ToolExecutionFailed?.Invoke(this,
                new ToolExecutionErrorEventArgs { Invocation = invocation, Error = ex });

            var failure = ToolExecutionResult.Failure(
                ex.Message,
                ToolErrorType.Unknown);

            return failure with
            {
                Error = failure.Error with { Exception = ex }
            };
        }
    }

    /// <summary>
    /// Checks if a tool requires approval.
    /// </summary>
    public bool RequiresApproval(IToolInvocation invocation)
    {
        return _defaultApprovalMode == ApprovalMode.Manual ||
               false;
    }

    /// <summary>
    /// Creates execution options.
    /// </summary>
    public ToolExecutionOptions CreateOptions(
        ApprovalMode? approvalMode = null,
        TimeSpan? timeout = null,
        bool validateFirst = true)
    {
        return new ToolExecutionOptions
        {
            ApprovalMode = approvalMode ?? _defaultApprovalMode,
            Timeout = timeout ?? TimeSpan.FromMinutes(5),
            ValidateBeforeExecution = validateFirst
        };
    }
}

/// <summary>
/// Interface for tool executors.
/// </summary>
public interface IToolExecutor
{
    event EventHandler<ToolExecutionEventArgs>? ToolExecutionStarted;
    event EventHandler<ToolExecutionEventArgs>? ToolExecutionCompleted;
    event EventHandler<ToolExecutionErrorEventArgs>? ToolExecutionFailed;

    Task<ToolExecutionResult> ExecuteAsync(
        IToolInvocation invocation,
        ToolExecutionOptions options,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Execution options for tools.
/// </summary>
public record ToolExecutionOptions
{
    public ApprovalMode ApprovalMode { get; init; } = ApprovalMode.Auto;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
    public bool ValidateBeforeExecution { get; init; } = true;
    public Action<ToolLiveOutput>? LiveOutputHandler { get; init; }
}

/// <summary>
/// Event arguments for tool execution.
/// </summary>
public class ToolExecutionEventArgs : EventArgs
{
    public required IToolInvocation Invocation { get; init; }
    public ToolExecutionResult? Result { get; init; }
}

/// <summary>
/// Event arguments for tool execution errors.
/// </summary>
public class ToolExecutionErrorEventArgs : EventArgs
{
    public required IToolInvocation Invocation { get; init; }
    public required Exception Error { get; init; }
}
