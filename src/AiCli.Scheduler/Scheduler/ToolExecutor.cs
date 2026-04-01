using AiCli.Core.Logging;
using AiCli.Core.Tools;
using AiCli.Core.Types;
using Serilog;
using System.Diagnostics;

namespace AiCli.Core.Scheduler;

/// <summary>
/// Executor for running tools with error handling and monitoring.
/// </summary>
public class ToolExecutor : IDisposable
{
    private readonly ILogger _logger;
    private readonly MessageBus _messageBus;
    private readonly Dictionary<string, ToolExecution> _activeExecutions = new();
    private readonly SemaphoreSlim _concurrencySemaphore;
    private readonly object _lock = new();
    private bool _disposed;

    public ToolExecutor(MessageBus messageBus, int maxConcurrency = 1)
    {
        _logger = LoggerHelper.ForContext<ToolExecutor>();
        _messageBus = messageBus;
        _concurrencySemaphore = new SemaphoreSlim(maxConcurrency);
    }

    /// <summary>
    /// Gets the number of active executions.
    /// </summary>
    public int ActiveExecutionCount => _activeExecutions.Count;

    /// <summary>
    /// Event raised when tool execution starts.
    /// </summary>
    public event EventHandler<ToolExecutionEventArgs>? ExecutionStarted;

    /// <summary>
    /// Event raised when tool execution completes.
    /// </summary>
    public event EventHandler<ToolExecutionEventArgs>? ExecutionCompleted;

    /// <summary>
    /// Event raised when tool execution fails.
    /// </summary>
    public event EventHandler<ToolExecutionErrorEventArgs>? ExecutionFailed;

    /// <summary>
    /// Event raised when tool execution produces output.
    /// </summary>
    public event EventHandler<ToolOutputEventArgs>? OutputReceived;

    /// <summary>
    /// Executes a tool invocation.
    /// </summary>
    public async Task<ToolExecutionResult> ExecuteAsync(
        ToolExecution execution,
        ExecutionOptions options,
        CancellationToken cancellationToken = default)
    {
        await _concurrencySemaphore.WaitAsync(cancellationToken);
        execution.StartedAt = DateTime.UtcNow;

        lock (_lock)
        {
            _activeExecutions[execution.Id] = execution;
        }

        var stopwatch = Stopwatch.StartNew();
        execution.State = ToolExecutionState.Running;

        _logger.Information("Starting tool execution: {ToolName}, ID: {Id}",
            execution.Invocation.ToolName ?? execution.Invocation.GetType().Name, execution.Id);

        ExecutionStarted?.Invoke(this, new ToolExecutionEventArgs { Execution = execution });

        try
        {
            // Set up live output handler
            void UpdateOutput(ToolLiveOutput output)
            {
                OutputReceived?.Invoke(this, new ToolOutputEventArgs { Execution = execution, Output = output });
            }

            var timeoutTask = Task.Delay(options.Timeout, cancellationToken);
            var executionTask = execution.Invocation.ExecuteAsync(
                cancellationToken,
                UpdateOutput);

            var completedTask = await Task.WhenAny(executionTask, timeoutTask);

            ToolExecutionResult result;

            if (completedTask == timeoutTask)
            {
                execution.CancellationTokenSource.Cancel();
                throw new TimeoutException(
                    $"Tool execution timed out after {options.Timeout.TotalSeconds} seconds");
            }

            result = await executionTask;

            stopwatch.Stop();
            execution.CompletedAt = DateTime.UtcNow;
            execution.Result = result;
            execution.State = result.Error is not null ? ToolExecutionState.Failed : ToolExecutionState.Completed;

            _logger.Information(
                "Tool execution completed: {ToolName}, ID: {Id}, Duration: {Ms}ms, Error: {IsError}",
                execution.Invocation.GetType().Name,
                execution.Id,
                stopwatch.ElapsedMilliseconds,
                result.Error is not null);

            ExecutionCompleted?.Invoke(this, new ToolExecutionEventArgs { Execution = execution });

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopwatch.Stop();
            execution.CompletedAt = DateTime.UtcNow;
            execution.State = ToolExecutionState.Cancelled;

            _logger.Information(
                "Tool execution cancelled: {ToolName}, ID: {Id}, Duration: {Ms}ms",
                execution.Invocation.GetType().Name,
                execution.Id,
                stopwatch.ElapsedMilliseconds);

            return ToolExecutionResult.Failure(
                "Operation cancelled by user",
                ToolErrorType.Cancellation);
        }
        catch (TimeoutException ex)
        {
            stopwatch.Stop();
            execution.CompletedAt = DateTime.UtcNow;
            execution.State = ToolExecutionState.Failed;
            execution.Error = ex;

            _logger.Error(ex,
                "Tool execution timed out: {ToolName}, ID: {Id}",
                execution.Invocation.GetType().Name,
                execution.Id);

            ExecutionFailed?.Invoke(this, new ToolExecutionErrorEventArgs { Execution = execution, Error = ex });

            return ToolExecutionResult.Failure(
                ex.Message,
                ToolErrorType.Timeout);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            execution.CompletedAt = DateTime.UtcNow;
            execution.State = ToolExecutionState.Failed;
            execution.Error = ex;

            _logger.Error(ex,
                "Tool execution failed: {ToolName}, ID: {Id}",
                execution.Invocation.GetType().Name,
                execution.Id);

            ExecutionFailed?.Invoke(this, new ToolExecutionErrorEventArgs { Execution = execution, Error = ex });

            var failure = ToolExecutionResult.Failure(
                ex.Message,
                ToolErrorType.Unknown);

            return failure with
            {
                Error = failure.Error with { Exception = ex }
            };
        }
        finally
        {
            lock (_lock)
            {
                _activeExecutions.Remove(execution.Id);
            }

            _concurrencySemaphore.Release();
        }
    }

    /// <summary>
    /// Cancels an active execution.
    /// </summary>
    public bool CancelExecution(string executionId)
    {
        lock (_lock)
        {
            if (_activeExecutions.TryGetValue(executionId, out var execution))
            {
                execution.CancellationTokenSource.Cancel();
                _logger.Information("Cancelling execution: {Id}", executionId);
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Cancels all active executions.
    /// </summary>
    public void CancelAllExecutions()
    {
        lock (_lock)
        {
            foreach (var execution in _activeExecutions.Values)
            {
                execution.CancellationTokenSource.Cancel();
            }
            _logger.Information("Cancelled all {Count} active executions", _activeExecutions.Count);
        }
    }

    /// <summary>
    /// Disposes the executor.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        CancelAllExecutions();
        _concurrencySemaphore.Dispose();

        _logger.Information("ToolExecutor disposed");
    }
}

/// <summary>
/// Event arguments for tool execution events.
/// </summary>
public class ToolExecutionEventArgs : EventArgs
{
    public ToolExecution Execution { get; init; } = default!;

    public ToolExecutionEventArgs() { }

    public ToolExecutionEventArgs(ToolExecution execution)
    {
        Execution = execution;
    }
}

/// <summary>
/// Event arguments for tool execution errors.
/// </summary>
public class ToolExecutionErrorEventArgs : EventArgs
{
    public ToolExecution Execution { get; init; } = default!;
    public Exception Error { get; init; } = default!;

    public ToolExecutionErrorEventArgs() { }

    public ToolExecutionErrorEventArgs(ToolExecution execution, Exception error)
    {
        Execution = execution;
        Error = error;
    }
}

/// <summary>
/// Event arguments for tool output events.
/// </summary>
public class ToolOutputEventArgs : EventArgs
{
    public ToolExecution Execution { get; init; } = default!;
    public ToolLiveOutput Output { get; init; } = default!;
}
