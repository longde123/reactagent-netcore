using AiCli.Core.Logging;
using Serilog;
using System.Collections.Concurrent;

namespace AiCli.Core.Telemetry;

/// <summary>
/// Collects and manages telemetry events.
/// </summary>
public class TelemetryCollector : IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly bool _enabled;
    private readonly ConcurrentQueue<TelemetryEvent> _events;
    private readonly SemaphoreSlim _flushSemaphore;
    private CancellationTokenSource? _shutdownCts;
    private Task? _flushTask;

    /// <summary>
    /// The session ID for this run.
    /// </summary>
    public string SessionId { get; }

    /// <summary>
    /// Gets the number of pending events.
    /// </summary>
    public int PendingEvents => _events.Count;

    /// <summary>
    /// Initializes a new instance of the TelemetryCollector class.
    /// </summary>
    public TelemetryCollector(bool enabled = true)
    {
        _logger = LoggerHelper.ForContext<TelemetryCollector>();
        _enabled = enabled;
        _events = new ConcurrentQueue<TelemetryEvent>();
        _flushSemaphore = new SemaphoreSlim(1, 1);
        SessionId = Guid.NewGuid().ToString("N");
    }

    /// <summary>
    /// Starts the telemetry collector.
    /// </summary>
    public void Start()
    {
        if (!_enabled) return;

        _shutdownCts = new CancellationTokenSource();
        _flushTask = Task.Run(() => FlushLoopAsync(_shutdownCts.Token));
        _logger.Debug("Telemetry collector started with session ID: {SessionId}", SessionId);
    }

    /// <summary>
    /// Records a telemetry event.
    /// </summary>
    public void RecordEvent(TelemetryEvent telemetryEvent)
    {
        if (!_enabled) return;

        var eventWithSession = telemetryEvent with { SessionId = SessionId };
        _events.Enqueue(eventWithSession);
        _logger.Verbose("Telemetry event recorded: {EventType}", telemetryEvent.EventType);
    }

    /// <summary>
    /// Flushes pending events synchronously.
    /// </summary>
    public void Flush()
    {
        if (!_enabled) return;

        while (_events.TryDequeue(out var evt))
        {
            ProcessEvent(evt);
        }
    }

    /// <summary>
    /// Background flush loop.
    /// </summary>
    private async Task FlushLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
                await FlushAsync();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Error in telemetry flush loop");
            }
        }
    }

    /// <summary>
    /// Flushes pending events asynchronously.
    /// </summary>
    private async Task FlushAsync()
    {
        if (!await _flushSemaphore.WaitAsync(TimeSpan.Zero))
        {
            return; // Already flushing
        }

        try
        {
            var eventsToFlush = new List<TelemetryEvent>();
            while (_events.TryDequeue(out var evt))
            {
                eventsToFlush.Add(evt);
            }

            foreach (var evt in eventsToFlush)
            {
                ProcessEvent(evt);
            }
        }
        finally
        {
            _flushSemaphore.Release();
        }
    }

    /// <summary>
    /// Processes a single telemetry event.
    /// </summary>
    private void ProcessEvent(TelemetryEvent evt)
    {
        try
        {
            _logger.Verbose("Processing telemetry event: {EventType}", evt.EventType);
            // TODO: Send to telemetry backend when configured
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Error processing telemetry event: {EventType}", evt.EventType);
        }
    }

    /// <summary>
    /// Disposes the telemetry collector.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_shutdownCts is not null)
        {
            _shutdownCts.Cancel();
            await (_flushTask ?? Task.CompletedTask);
            _shutdownCts.Dispose();
        }

        Flush();
        await _flushSemaphore.WaitAsync(TimeSpan.FromSeconds(5));
        _flushSemaphore.Dispose();
    }
}
