using AiCli.Core.Logging;
using AiCli.Core.Tools;
using System.Diagnostics;

namespace AiCli.Core.Scheduler;

/// <summary>
/// Options for scheduler initialization.
/// </summary>
public class SchedulerOptions
{
    public int MaxConcurrentExecutions { get; init; } = 1;
    public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromMinutes(5);
    public bool RequireApproval { get; init; } = false;
    public ApprovalMode DefaultApprovalMode { get; init; } = ApprovalMode.Auto;
}

/// <summary>
/// Scheduler for coordinating tool execution.
/// Manages the execution queue, approval workflow, and error handling.
/// </summary>
public class Scheduler : IDisposable
{
    private readonly ILogger _logger;
    private readonly MessageBus _messageBus;
    private readonly ToolExecutor _executor;
    private readonly ToolExecutionQueue _queue;
    private readonly SchedulerOptions _options;
    private readonly Dictionary<string, ExecutionContext> _contexts = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly SemaphoreSlim _processingLock = new(1, 1);
    private Task? _processingTask;
    private bool _disposed;
    private bool _isRunning;

    /// <summary>
    /// Event raised when a tool execution starts.
    /// </summary>
    public event EventHandler<ToolExecutionEventArgs>? ToolStarted;

    /// <summary>
    /// Event raised when a tool execution completes.
    /// </summary>
    public event EventHandler<ToolExecutionEventArgs>? ToolCompleted;

    /// <summary>
    /// Event raised when a tool execution fails.
    /// </summary>
    public event EventHandler<ToolExecutionErrorEventArgs>? ToolFailed;

    /// <summary>
    /// Event raised when a tool requires approval.
    /// </summary>
    public event EventHandler<ToolApprovalEventArgs>? ToolRequiresApproval;

    public Scheduler(SchedulerOptions? options = null)
    {
        _logger = LoggerHelper.ForContext<Scheduler>();
        _options = options ?? new SchedulerOptions();
        _messageBus = new MessageBus();
        _executor = new ToolExecutor(_messageBus, _options.MaxConcurrentExecutions);
        _queue = new ToolExecutionQueue();

        // Subscribe to executor events
        _executor.ExecutionStarted += (s, e) => ToolStarted?.Invoke(s, e);
        _executor.ExecutionCompleted += (s, e) => ToolCompleted?.Invoke(s, e);
        _executor.ExecutionFailed += (s, e) => ToolFailed?.Invoke(s, e);

        // Subscribe to approval messages
        _messageBus.Subscribe("approval_response", HandleApprovalResponse);

        _logger.Information("Scheduler initialized with max concurrency: {MaxConcurrent}",
            _options.MaxConcurrentExecutions);
    }

    /// <summary>
    /// Gets the message bus for this scheduler.
    /// </summary>
    public MessageBus MessageBus => _messageBus;

    /// <summary>
    /// Gets the execution queue.
    /// </summary>
    public ToolExecutionQueue Queue => _queue;

    /// <summary>
    /// Gets the queue statistics.
    /// </summary>
    public QueueStatistics Statistics => _queue.GetStatistics();

    /// <summary>
    /// Starts the scheduler processing loop.
    /// </summary>
    public void Start()
    {
        if (_isRunning)
        {
            _logger.Warning("Scheduler is already running");
            return;
        }

        _isRunning = true;
        _processingTask = Task.Run(ProcessQueueAsync);

        _logger.Information("Scheduler started");
    }

    /// <summary>
    /// Stops the scheduler processing loop.
    /// </summary>
    public async Task StopAsync()
    {
        if (!_isRunning) return;

        _logger.Information("Stopping scheduler...");
        _isRunning = false;
        _shutdownCts.Cancel();

        if (_processingTask != null)
        {
            await _processingTask;
        }

        _logger.Information("Scheduler stopped");
    }

    /// <summary>
    /// Creates a new execution context.
    /// </summary>
    public string CreateContext(ExecutionContext context)
    {
        var id = Guid.NewGuid().ToString();
        _contexts[id] = context;

        _logger.Verbose("Created context: {ContextId}", id);
        return id;
    }

    /// <summary>
    /// Gets an execution context by ID.
    /// </summary>
    public ExecutionContext? GetContext(string contextId)
    {
        return _contexts.TryGetValue(contextId, out var context) ? context : null;
    }

    /// <summary>
    /// Submits a tool for execution.
    /// </summary>
    public string SubmitTool(
        IToolInvocation invocation,
        string? contextId = null,
        Dictionary<string, object>? metadata = null)
    {
        var executionId = _queue.Enqueue(invocation, metadata);

        _logger.Information("Submitted tool: {ToolName}, ID: {ExecutionId}, Context: {ContextId}",
            invocation.ToolName, executionId, contextId ?? "none");

        // Store context ID in metadata
        if (contextId != null)
        {
            var execution = _queue.GetById(executionId);
            if (execution != null)
            {
                execution.Metadata["context_id"] = contextId;
            }
        }

        return executionId;
    }

    /// <summary>
    /// Cancels a tool execution.
    /// </summary>
    public bool CancelExecution(string executionId)
    {
        return _executor.CancelExecution(executionId);
    }

    /// <summary>
    /// Removes a tool from the queue.
    /// </summary>
    public bool RemoveTool(string executionId)
    {
        return _queue.Remove(executionId);
    }

    /// <summary>
    /// Clears the execution queue.
    /// </summary>
    public void ClearQueue()
    {
        _queue.Clear();
        _executor.CancelAllExecutions();
    }

    /// <summary>
    /// Processes the execution queue.
    /// </summary>
    private async Task ProcessQueueAsync()
    {
        while (_isRunning && !_shutdownCts.Token.IsCancellationRequested)
        {
            await _processingLock.WaitAsync(_shutdownCts.Token);

            try
            {
                var execution = _queue.Dequeue();
                if (execution == null)
                {
                    await Task.Delay(100, _shutdownCts.Token);
                    _processingLock.Release();
                    continue;
                }

                // Get execution context
                var contextId = execution.Metadata.TryGetValue("context_id", out var ctxId)
                    ? ctxId?.ToString()
                    : null;
                var context = contextId != null ? GetContext(contextId) : null;

                // Check if approval is needed
                var needsApproval = context?.ApprovalMode switch
                {
                    ApprovalMode.Manual => true,
                    ApprovalMode.Interactive => true,
                    _ => _options.RequireApproval
                };

                if (needsApproval && context?.ApproveToolAsync != null)
                {
                    execution.State = ToolExecutionState.RequiresApproval;

                    _logger.Verbose("Tool requires approval: {ToolName}, ID: {Id}",
                        execution.Invocation.ToolName, execution.Id);

                    ToolRequiresApproval?.Invoke(this,
                        new ToolApprovalEventArgs { Execution = execution, Context = context });

                    // Request approval via message bus
                    var approved = await context.ApproveToolAsync(execution);

                    if (!approved)
                    {
                        execution.State = ToolExecutionState.Failed;
                        execution.Error = new OperationCanceledException("Tool execution rejected by user");

                        _logger.Information("Tool execution rejected: {ToolName}, ID: {Id}",
                            execution.Invocation.ToolName, execution.Id);

                        _processingLock.Release();
                        continue;
                    }
                }

                // Execute the tool
                var options = new ExecutionOptions
                {
                    MaxConcurrentExecutions = _options.MaxConcurrentExecutions,
                    Timeout = _options.DefaultTimeout
                };

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _executor.ExecuteAsync(execution, options,
                            execution.CancellationTokenSource.Token);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, "Error executing tool: {ToolName}",
                            execution.Invocation.ToolName);
                    }
                    finally
                    {
                        _processingLock.Release();
                    }
                });
            }
            catch (OperationCanceledException)
            {
                // Shutdown requested
                _processingLock.Release();
                break;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error processing queue");
                _processingLock.Release();
                await Task.Delay(100, _shutdownCts.Token);
            }
        }
    }

    /// <summary>
    /// Handles approval responses from the message bus.
    /// </summary>
    private void HandleApprovalResponse(MessageBusEventArgs args)
    {
        var message = args.Message;
        if (!message.Headers.TryGetValue("execution_id", out var executionId))
        {
            _logger.Warning("Approval response missing execution_id");
            return;
        }

        var execution = _queue.GetById(executionId);
        if (execution == null)
        {
            _logger.Warning("Execution not found: {ExecutionId}", executionId);
            return;
        }

        var approved = message.Payload is bool b && b;

        _logger.Verbose("Received approval response: {ExecutionId}, Approved: {Approved}",
            executionId, approved);
    }

    /// <summary>
    /// Disposes the scheduler.
    /// </summary>
    public async void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        await StopAsync();
        ClearQueue();

        _messageBus.Dispose();
        _executor.Dispose();
        _queue.Dispose();
        _shutdownCts.Dispose();
        _processingLock.Dispose();

        _contexts.Clear();

        _logger.Information("Scheduler disposed");
    }
}
