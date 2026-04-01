using AiCli.Core.Logging;
using Serilog;
using System.Collections.Concurrent;

namespace AiCli.Core;

/// <summary>
/// Internal event subscription.
/// </summary>

/// <summary>
/// Event subscription information.
/// </summary>
public record EventSubscription
{
    public required string Id { get; init; }
    public required Action<object?, EventArgs> Handler { get; init; }
    public required bool Once { get; init; }
    public required int Priority { get; init; }
    public string? Filter { get; init; }
}

/// <summary>
/// Event emitter for inter-component communication.
/// </summary>
public class EventEmitter : IDisposable
{
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, List<EventSubscription>> _subscriptions = new();
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>
    /// Gets the number of active subscriptions.
    /// </summary>
    public int SubscriptionCount
    {
        get
        {
            return _subscriptions.Values.Sum(list => list.Count);
        }
    }

    public EventEmitter()
    {
        _logger = LoggerHelper.ForContext<EventEmitter>();
    }

    /// <summary>
    /// Subscribes to an event.
    /// </summary>
    /// <typeparam name="TEventArgs">The type of event arguments.</typeparam>
    public string On<TEventArgs>(
        string eventName,
        EventHandler<TEventArgs> handler,
        bool once = false,
        int priority = 0,
        string? filter = null)
        where TEventArgs : EventArgs
    {
        // Wrap the strongly-typed handler into a generic EventArgs handler
        Action<object?, EventArgs> wrapper = (sender, args) => handler(sender, (TEventArgs)args);

        var subscription = new EventSubscription
        {
            Id = Guid.NewGuid().ToString(),
            Handler = wrapper,
            Once = once,
            Priority = priority,
            Filter = filter
        };

        _subscriptions.AddOrUpdate(
            eventName,
            _ => new List<EventSubscription> { subscription },
            (_, existing) =>
            {
                existing.Add(subscription);
                existing.Sort((a, b) => b.Priority.CompareTo(a.Priority));
                return existing;
            });

        _logger.Verbose(
            "Subscribed to event: {Event}, Handler: {HandlerId}, Once: {Once}, Filter: {Filter}",
            eventName,
            subscription.Id,
            once,
            filter ?? "none");

        return subscription.Id;
    }

    /// <summary>
    /// Unsubscribes from an event.
    /// </summary>
    public bool Off(string subscriptionId)
    {
        lock (_lock)
        {
            foreach (var kvp in _subscriptions)
            {
                var subscription = kvp.Value.FirstOrDefault(s => s.Id == subscriptionId);
                if (subscription != null)
                {
                    kvp.Value.Remove(subscription);
                    _logger.Verbose("Unsubscribed: {SubscriptionId}", subscriptionId);
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Unsubscribes all handlers for an event.
    /// </summary>
    public void OffAll(string eventName)
    {
            lock (_lock)
            {
                if (_subscriptions.TryGetValue(eventName, out var subscriptions))
                {
                    var count = subscriptions.Count;
                    _subscriptions.TryRemove(eventName, out _);
                    _logger.Verbose("Unsubscribed all from event: {Event}, Count: {Count}", eventName, count);
                }
            }
    }

    /// <summary>
    /// Emits an event asynchronously.
    /// </summary>
    /// <typeparam name="TEventArgs">The type of event arguments.</typeparam>
    public async Task EmitAsync<TEventArgs>(
        string eventName,
        TEventArgs args,
        CancellationToken cancellationToken = default)
        where TEventArgs : EventArgs
    {
        await Task.Run(() => Emit(eventName, args, cancellationToken), cancellationToken);
        return;
    }

    /// <summary>
    /// Emits an event synchronously.
    /// </summary>
    /// <typeparam name="TEventArgs">The type of event arguments.</typeparam>
    public void Emit<TEventArgs>(
        string eventName,
        TEventArgs args,
        CancellationToken cancellationToken = default)
        where TEventArgs : EventArgs
    {
        List<EventSubscription>? subscriptions;

        lock (_lock)
        {
            if (!_subscriptions.TryGetValue(eventName, out var subs))
            {
                _logger.Verbose("No subscribers for event: {Event}", eventName);
                return;
            }

            subscriptions = subs.ToList();
        }

        _logger.Verbose("Emitting event: {Event}, Subscribers: {Count}", eventName, subscriptions.Count);

        var toRemove = new List<string>();

        foreach (var subscription in subscriptions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Check filter
            if (!string.IsNullOrEmpty(subscription.Filter))
            {
                if (!CheckFilter(subscription, args))
                {
                    continue;
                }
            }

            try
            {
                // Execute handler
                _logger.Verbose("  Calling handler: {SubscriptionId}", subscription.Id);

                // Invoke the stored wrapper handler
                subscription.Handler(null, args);

                // Mark for removal if once
                if (subscription.Once)
                {
                    toRemove.Add(subscription.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error in event handler: {SubscriptionId}", subscription.Id);
            }
        }

        // Remove one-time subscriptions
        if (toRemove.Count > 0)
        {
            lock (_lock)
            {
                if (_subscriptions.TryGetValue(eventName, out var subs))
                {
                    foreach (var id in toRemove)
                    {
                        var sub = subs.FirstOrDefault(s => s.Id == id);
                        if (sub != null)
                        {
                            subs.Remove(sub);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Checks if an event matches a subscription filter.
    /// </summary>
    private bool CheckFilter(EventSubscription subscription, EventArgs args)
    {
        // Simple filter implementation - can be extended
        var filter = subscription.Filter!;

        // Check if args contain a property matching the filter
        var properties = args.GetType().GetProperties();
        foreach (var prop in properties)
        {
            var value = prop.GetValue(args);
            if (value != null)
            {
                var s = value.ToString();
                if (s != null && s.Contains(filter))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Gets all event names.
    /// </summary>
    public List<string> GetEventNames()
    {
        lock (_lock)
        {
            return _subscriptions.Keys.ToList();
        }
    }

    /// <summary>
    /// Gets subscribers for an event.
    /// </summary>
    public int GetSubscriberCount(string eventName)
    {
        lock (_lock)
        {
            return _subscriptions.TryGetValue(eventName, out var subscriptions)
                ? subscriptions.Count
                : 0;
        }
    }

    /// <summary>
    /// Clears all subscriptions.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            var count = SubscriptionCount;
            _subscriptions.Clear();
            _logger.Information("Cleared {Count} event subscriptions", count);
        }
    }

    /// <summary>
    /// Disposes the emitter.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Clear();

        _logger.Information("EventEmitter disposed");
    }
}

/// <summary>
/// Predefined event names.
/// </summary>
public static class Events
{
    // Chat events
    public const string MessageReceived = "message:received";
    public const string MessageSent = "message:sent";
    public const string ChatStarted = "chat:started";
    public const string ChatEnded = "chat:ended";

    // Tool events
    public const string ToolInvoked = "tool:invoked";
    public const string ToolCompleted = "tool:completed";
    public const string ToolFailed = "tool:failed";

    // Agent events
    public const string AgentStarted = "agent:started";
    public const string AgentCompleted = "agent:completed";
    public const string AgentFailed = "agent:failed";

    // System events
    public const string Error = "system:error";
    public const string Warning = "system:warning";
    public const string Info = "system:info";

    // Lifecycle events
    public const string Initialized = "lifecycle:initialized";
    public const string ShuttingDown = "lifecycle:shutting_down";
}
