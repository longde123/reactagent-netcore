using AiCli.Core.Logging;
using AiCli.Core.Tools;

namespace AiCli.Core.Scheduler;

/// <summary>
/// Queue for managing pending tool executions.
/// </summary>
public class ToolExecutionQueue : IDisposable
{
    private readonly ILogger _logger;
    private readonly Queue<ToolExecution> _queue = new();
    private readonly Dictionary<string, ToolExecution> _executionById = new();
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>
    /// Event raised when a tool is enqueued.
    /// </summary>
    public event EventHandler<ToolExecutionEventArgs>? ToolEnqueued;

    /// <summary>
    /// Event raised when a tool is dequeued.
    /// </summary>
    public event EventHandler<ToolExecutionEventArgs>? ToolDequeued;

    /// <summary>
    /// Gets the number of pending executions.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _queue.Count;
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether the queue is empty.
    /// </summary>
    public bool IsEmpty
    {
        get
        {
            lock (_lock)
            {
                return _queue.Count == 0;
            }
        }
    }

    public ToolExecutionQueue()
    {
        _logger = LoggerHelper.ForContext<ToolExecutionQueue>();
    }

    /// <summary>
    /// Enqueues a tool execution.
    /// </summary>
    public string Enqueue(IToolInvocation invocation, Dictionary<string, object>? metadata = null)
    {
        var execution = new ToolExecution
        {
            Id = Guid.NewGuid().ToString(),
            Invocation = invocation,
            CancellationTokenSource = new CancellationTokenSource()
        };

        if (metadata != null)
        {
            foreach (var (key, value) in metadata)
            {
                execution.Metadata[key] = value;
            }
        }

        lock (_lock)
        {
            _queue.Enqueue(execution);
            _executionById[execution.Id] = execution;
        }

        _logger.Verbose("Enqueued tool: {ToolName}, ID: {Id}",
            invocation.ToolName, execution.Id);

        ToolEnqueued?.Invoke(this, new ToolExecutionEventArgs(execution));

        return execution.Id;
    }

    /// <summary>
    /// Dequeues the next tool execution.
    /// </summary>
    public ToolExecution? Dequeue()
    {
        lock (_lock)
        {
            if (_queue.Count == 0) return null;

            var execution = _queue.Dequeue();

            _logger.Verbose("Dequeued tool: {ToolName}, ID: {Id}",
                execution.Invocation.ToolName, execution.Id);

            ToolDequeued?.Invoke(this, new ToolExecutionEventArgs(execution));

            return execution;
        }
    }

    /// <summary>
    /// Peeks at the next tool execution without removing it.
    /// </summary>
    public ToolExecution? Peek()
    {
        lock (_lock)
        {
            return _queue.Count > 0 ? _queue.Peek() : null;
        }
    }

    /// <summary>
    /// Gets an execution by ID.
    /// </summary>
    public ToolExecution? GetById(string id)
    {
        lock (_lock)
        {
            return _executionById.TryGetValue(id, out var execution) ? execution : null;
        }
    }

    /// <summary>
    /// Gets all executions in the queue.
    /// </summary>
    public List<ToolExecution> GetAll()
    {
        lock (_lock)
        {
            return _queue.ToList();
        }
    }

    /// <summary>
    /// Gets all pending executions (queued + running).
    /// </summary>
    public List<ToolExecution> GetAllPending()
    {
        lock (_lock)
        {
            return _executionById.Values
                .Where(e => e.State is ToolExecutionState.Pending or ToolExecutionState.Running)
                .ToList();
        }
    }

    /// <summary>
    /// Removes an execution from the queue.
    /// </summary>
    public bool Remove(string id)
    {
        lock (_lock)
        {
            if (!_executionById.TryGetValue(id, out var execution)) return false;

            // Can only remove pending executions
            if (execution.State != ToolExecutionState.Pending) return false;

            // Rebuild queue without this item
            var newQueue = new Queue<ToolExecution>();
            while (_queue.Count > 0)
            {
                var item = _queue.Dequeue();
                if (item.Id != id)
                {
                    newQueue.Enqueue(item);
                }
            }

            foreach (var item in newQueue)
            {
                _queue.Enqueue(item);
            }

            _executionById.Remove(id);
            _logger.Verbose("Removed execution from queue: {Id}", id);

            return true;
        }
    }

    /// <summary>
    /// Clears all pending executions from the queue.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            var count = _queue.Count;
            _queue.Clear();
            _executionById.Clear();
            _logger.Information("Cleared {Count} executions from queue", count);
        }
    }

    /// <summary>
    /// Gets execution statistics.
    /// </summary>
    public QueueStatistics GetStatistics()
    {
        lock (_lock)
        {
            var pending = _executionById.Values.Count(e =>
                e.State == ToolExecutionState.Pending);
            var running = _executionById.Values.Count(e =>
                e.State == ToolExecutionState.Running);
            var completed = _executionById.Values.Count(e =>
                e.State == ToolExecutionState.Completed);
            var failed = _executionById.Values.Count(e =>
                e.State == ToolExecutionState.Failed);

            return new QueueStatistics
            {
                TotalQueued = _queue.Count,
                Pending = pending,
                Running = running,
                Completed = completed,
                Failed = failed
            };
        }
    }

    /// <summary>
    /// Disposes the queue.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_lock)
        {
            foreach (var execution in _executionById.Values)
            {
                execution.CancellationTokenSource.Dispose();
            }
            _executionById.Clear();
            _queue.Clear();
        }

        _logger.Information("ToolExecutionQueue disposed");
    }
}

/// <summary>
/// Statistics for the execution queue.
/// </summary>
public record QueueStatistics
{
    public int TotalQueued { get; init; }
    public int Pending { get; init; }
    public int Running { get; init; }
    public int Completed { get; init; }
    public int Failed { get; init; }

    public int TotalProcessed => Completed + Failed;
}
