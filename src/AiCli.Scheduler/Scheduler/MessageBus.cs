using System.Collections.Concurrent;
using AiCli.Core.Logging;

namespace AiCli.Core.Scheduler;

/// <summary>
/// Message for the message bus.
/// </summary>
    public record MessageBusMessage
    {
        public string Id { get; init; } = Guid.NewGuid().ToString();
        public required string Type { get; init; }
        public required object Payload { get; init; }
        public DateTime Timestamp { get; init; } = DateTime.UtcNow;
        public Dictionary<string, string> Headers { get; } = new();
        public bool IsResponse { get; init; }
        public string? InResponseTo { get; init; }
    }

/// <summary>
/// Event arguments for message bus events.
/// </summary>
public class MessageBusEventArgs : EventArgs
{
    public required MessageBusMessage Message { get; init; }
}

/// <summary>
/// Message bus for inter-component communication.
/// Used for confirmations, approvals, and other coordination messages.
/// </summary>
public class MessageBus : IDisposable
{
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<MessageBusMessage>> _pendingResponses = new();
    private readonly ConcurrentDictionary<string, List<Action<MessageBusEventArgs>>> _subscriptions = new();
    private readonly ConcurrentQueue<MessageBusMessage> _messageQueue = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _processingTask;
    private bool _disposed;

    public MessageBus()
    {
        _logger = LoggerHelper.ForContext<MessageBus>();
        _processingTask = Task.Run(ProcessMessagesAsync);
    }

    /// <summary>
    /// Event raised when a message is received.
    /// </summary>
    public event EventHandler<MessageBusEventArgs>? MessageReceived;

    /// <summary>
    /// Publishes a message to the bus.
    /// </summary>
    public void Publish(string type, object payload, Dictionary<string, string>? headers = null)
    {
        var message = new MessageBusMessage
        {
            Type = type,
            Payload = payload
        };

        if (headers != null)
        {
            foreach (var (key, value) in headers)
            {
                message.Headers[key] = value;
            }
        }

        _messageQueue.Enqueue(message);
        _logger.Verbose("Published message: {Type}, ID: {Id}", type, message.Id);
    }

    /// <summary>
    /// Publishes a message and waits for a response.
    /// </summary>
    public async Task<MessageBusMessage> PublishAndWaitAsync(
        string type,
        object payload,
        TimeSpan timeout,
        Dictionary<string, string>? headers = null)
    {
        var message = new MessageBusMessage
        {
            Type = type,
            Payload = payload
        };

        if (headers != null)
        {
            foreach (var (key, value) in headers)
            {
                message.Headers[key] = value;
            }
        }

        var tcs = new TaskCompletionSource<MessageBusMessage>();
        _pendingResponses[message.Id] = tcs;

        _messageQueue.Enqueue(message);
        _logger.Verbose("Published message (waiting for response): {Type}, ID: {Id}", type, message.Id);

        using var timeoutCts = new CancellationTokenSource(timeout);
        timeoutCts.Token.Register(() =>
        {
            if (tcs.TrySetCanceled())
            {
                _pendingResponses.TryRemove(message.Id, out _);
            }
        });

        return await tcs.Task;
    }

    /// <summary>
    /// Publishes a response to a previous message.
    /// </summary>
    public void Respond(string inResponseTo, object payload, Dictionary<string, string>? headers = null)
    {
        var message = new MessageBusMessage
        {
            Type = "response",
            Payload = payload,
            IsResponse = true,
            InResponseTo = inResponseTo
        };

        if (headers != null)
        {
            foreach (var (key, value) in headers)
            {
                message.Headers[key] = value;
            }
        }

        _messageQueue.Enqueue(message);
        _logger.Verbose("Published response to: {InResponseTo}", inResponseTo);
    }

    /// <summary>
    /// Subscribes to messages of a specific type.
    /// </summary>
    public void Subscribe(string type, Action<MessageBusEventArgs> handler)
    {
        _subscriptions.AddOrUpdate(
            type,
            _ => new List<Action<MessageBusEventArgs>> { handler },
            (_, existing) =>
            {
                existing.Add(handler);
                return existing;
            });

        _logger.Verbose("Subscribed to message type: {Type}", type);
    }

    /// <summary>
    /// Unsubscribes from messages of a specific type.
    /// </summary>
    public void Unsubscribe(string type, Action<MessageBusEventArgs> handler)
    {
        if (_subscriptions.TryGetValue(type, out var handlers))
        {
            handlers.Remove(handler);
            if (handlers.Count == 0)
            {
                _subscriptions.TryRemove(type, out _);
            }
        }

        _logger.Verbose("Unsubscribed from message type: {Type}", type);
    }

    /// <summary>
    /// Processes messages from the queue.
    /// </summary>
    private async Task ProcessMessagesAsync()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            while (_messageQueue.TryDequeue(out var message))
            {
                await ProcessMessageAsync(message);
            }

            await Task.Delay(50, _cts.Token);
        }
    }

    /// <summary>
    /// Processes a single message.
    /// </summary>
    private async Task ProcessMessageAsync(MessageBusMessage message)
    {
        _logger.Verbose("Processing message: {Type}, ID: {Id}", message.Type, message.Id);

        // Handle responses
        if (message.IsResponse && !string.IsNullOrEmpty(message.InResponseTo))
        {
            if (_pendingResponses.TryRemove(message.InResponseTo, out var tcs))
            {
                tcs.TrySetResult(message);
            }
            return;
        }

        // Notify subscribers
        MessageBusEventArgs? args = null;
        if (_subscriptions.TryGetValue(message.Type, out var handlers))
        {
            args = new MessageBusEventArgs { Message = message };
            foreach (var handler in handlers)
            {
                try
                {
                    handler(args);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Error in message handler for type: {Type}", message.Type);
                }
            }
        }

        // Raise event
        if (args != null)
        {
            MessageReceived?.Invoke(this, args);
        }
    }

    /// <summary>
    /// Disposes the message bus.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts.Cancel();
        _cts.Dispose();

        foreach (var tcs in _pendingResponses.Values)
        {
            tcs.TrySetCanceled();
        }
        _pendingResponses.Clear();

        _subscriptions.Clear();

        _logger.Information("MessageBus disposed");
    }
}
