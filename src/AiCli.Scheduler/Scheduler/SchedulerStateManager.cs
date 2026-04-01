using AiCli.Core.ConfirmationBus;
using AiCli.Core.Logging;
using AiCli.Core.Types;

namespace AiCli.Core.Scheduler;

/// <summary>
/// Callback invoked when a tool call reaches a terminal state.
/// </summary>
public delegate void TerminalCallHandler(ToolCallBase call);

/// <summary>
/// Manages the state machine for tool calls.
/// Enqueues new calls, drives state transitions, and publishes TOOL_CALLS_UPDATE snapshots
/// via the ConfirmationMessageBus.
/// Ported from packages/core/src/scheduler/state-manager.ts
/// </summary>
public class SchedulerStateManager
{
    public const string RootSchedulerId = "root";

    private static readonly ILogger Logger = LoggerHelper.ForContext<SchedulerStateManager>();

    private readonly ConfirmationMessageBus _messageBus;
    private readonly string _schedulerId;
    private readonly TerminalCallHandler? _onTerminalCall;

    private readonly Dictionary<string, ToolCallBase> _activeCalls = new();
    private readonly Queue<ToolCallBase> _queue = new();
    private List<ToolCallBase> _completedBatch = new();

    public SchedulerStateManager(
        ConfirmationMessageBus messageBus,
        string schedulerId = RootSchedulerId,
        TerminalCallHandler? onTerminalCall = null)
    {
        _messageBus = messageBus;
        _schedulerId = schedulerId;
        _onTerminalCall = onTerminalCall;
    }

    // ─── Queue Operations ────────────────────────────────────────────────────

    public void AddToolCalls(IEnumerable<ToolCallBase> calls) => Enqueue(calls);

    public void Enqueue(IEnumerable<ToolCallBase> calls)
    {
        foreach (var call in calls)
            _queue.Enqueue(call);
        EmitUpdate();
    }

    public ToolCallBase? Dequeue()
    {
        if (!_queue.TryDequeue(out var next)) return null;
        _activeCalls[next.Request.CallId] = next;
        EmitUpdate();
        return next;
    }

    public ToolCallBase? PeekQueue() => _queue.TryPeek(out var next) ? next : null;

    // ─── Properties ──────────────────────────────────────────────────────────

    public bool IsActive => _activeCalls.Count > 0;
    public int ActiveCallCount => _activeCalls.Count;
    public int QueueLength => _queue.Count;

    public IReadOnlyList<ToolCallBase> AllActiveCalls => _activeCalls.Values.ToList();
    public ToolCallBase? FirstActiveCall => _activeCalls.Values.FirstOrDefault();

    public IReadOnlyList<ToolCallBase> CompletedBatch => _completedBatch.AsReadOnly();

    // ─── Look-up ─────────────────────────────────────────────────────────────

    public ToolCallBase? GetToolCall(string callId)
    {
        if (_activeCalls.TryGetValue(callId, out var active)) return active;
        var queued = _queue.FirstOrDefault(c => c.Request.CallId == callId);
        if (queued != null) return queued;
        return _completedBatch.FirstOrDefault(c => c.Request.CallId == callId);
    }

    // ─── Status Updates ──────────────────────────────────────────────────────

    public void UpdateStatus(string callId, CoreToolCallStatus status, object? auxiliaryData = null)
    {
        if (!_activeCalls.TryGetValue(callId, out var call)) return;

        var updated = TransitionCall(call, status, auxiliaryData);
        _activeCalls[callId] = updated;
        EmitUpdate();
    }

    public void FinalizeCall(string callId)
    {
        if (!_activeCalls.TryGetValue(callId, out var call)) return;

        if (!ToolCallHelpers.IsTerminal(call)) return;

        _completedBatch.Add(call);
        _activeCalls.Remove(callId);
        _onTerminalCall?.Invoke(call);
        EmitUpdate();
    }

    public void SetOutcome(string callId, ToolConfirmationOutcome outcome)
    {
        if (!_activeCalls.TryGetValue(callId, out var call)) return;
        _activeCalls[callId] = call with { Outcome = outcome };
        EmitUpdate();
    }

    /// <summary>
    /// Replaces the active call with a tail call, placing it at the front of the queue.
    /// </summary>
    public void ReplaceActiveCallWithTailCall(string callId, ToolCallBase nextCall)
    {
        if (!_activeCalls.ContainsKey(callId)) return;
        _activeCalls.Remove(callId);

        // Prepend to queue by rebuilding it
        var existing = _queue.ToArray();
        while (_queue.Count > 0) _queue.Dequeue();
        _queue.Enqueue(nextCall);
        foreach (var c in existing) _queue.Enqueue(c);

        EmitUpdate();
    }

    public void CancelAllQueued(string reason)
    {
        if (_queue.Count == 0) return;

        while (_queue.TryDequeue(out var queued))
        {
            var cancelled = ToCancelled(queued, reason);
            _completedBatch.Add(cancelled);
            _onTerminalCall?.Invoke(cancelled);
        }

        EmitUpdate();
    }

    // ─── Snapshot ────────────────────────────────────────────────────────────

    public IReadOnlyList<ToolCallBase> GetSnapshot()
    {
        var result = new List<ToolCallBase>(_completedBatch);
        result.AddRange(_activeCalls.Values);
        result.AddRange(_queue);
        return result;
    }

    public void ClearBatch()
    {
        if (_completedBatch.Count == 0) return;
        _completedBatch = new List<ToolCallBase>();
        EmitUpdate();
    }

    // ─── Private: Bus ────────────────────────────────────────────────────────

    private void EmitUpdate()
    {
        var snapshot = GetSnapshot();
        var snapshots = snapshot
            .Select(c => new ToolCallSnapshot
            {
                CallId = c.Request.CallId,
                Name = c.Request.Name,
                Status = c.Status.ToString(),
                SchedulerId = _schedulerId,
            })
            .ToList();

        _ = _messageBus.PublishAsync(new ToolCallsUpdateMessage
        {
            ToolCalls = snapshots,
            SchedulerId = _schedulerId,
        });
    }

    // ─── Private: State Transitions ──────────────────────────────────────────

    private ToolCallBase TransitionCall(ToolCallBase call, CoreToolCallStatus newStatus, object? data)
    {
        return newStatus switch
        {
            CoreToolCallStatus.Validating => ToValidating(call),
            CoreToolCallStatus.Scheduled => ToScheduled(call),
            CoreToolCallStatus.Executing => ToExecuting(call, data as ExecutingToolCallPatch),
            CoreToolCallStatus.AwaitingApproval => ToAwaitingApproval(call, data),
            CoreToolCallStatus.Success => ToSuccess(call, RequireResponseInfo(call, data, "success")),
            CoreToolCallStatus.Error => ToError(call, RequireResponseInfo(call, data, "error")),
            CoreToolCallStatus.Cancelled => ToCancelled(call, data ?? "Cancelled"),
            _ => throw new InvalidOperationException($"Unknown status: {newStatus}")
        };
    }

    private static ToolCallResponseInfo RequireResponseInfo(ToolCallBase call, object? data, string label)
    {
        if (data is ToolCallResponseInfo info) return info;
        throw new InvalidOperationException(
            $"Invalid data for '{label}' transition (callId: {call.Request.CallId})");
    }

    private static ValidatingToolCall ToValidating(ToolCallBase call) =>
        new()
        {
            Request = call.Request,
            Outcome = call.Outcome,
            ApprovalMode = call.ApprovalMode,
            StartTime = GetStartTime(call) ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

    private static ScheduledToolCall ToScheduled(ToolCallBase call) =>
        new()
        {
            Request = call.Request,
            Outcome = call.Outcome,
            ApprovalMode = call.ApprovalMode,
            StartTime = GetStartTime(call),
        };

    private static ExecutingToolCall ToExecuting(ToolCallBase call, ExecutingToolCallPatch? patch) =>
        new()
        {
            Request = call.Request,
            Outcome = call.Outcome,
            ApprovalMode = call.ApprovalMode,
            StartTime = GetStartTime(call),
            LiveOutput = patch?.LiveOutput ?? GetLiveOutput(call),
            ProgressMessage = patch?.ProgressMessage ?? GetProgressMessage(call),
            ProgressPercent = patch?.ProgressPercent ?? GetProgressPercent(call),
            Progress = patch?.Progress ?? GetProgress(call),
            ProgressTotal = patch?.ProgressTotal ?? GetProgressTotal(call),
            Pid = patch?.Pid ?? GetPid(call),
        };

    private static WaitingToolCall ToAwaitingApproval(ToolCallBase call, object? data)
    {
        SerializableConfirmationDetails details;
        string? correlationId = null;

        if (data is (string corrId, SerializableConfirmationDetails scd))
        {
            correlationId = corrId;
            details = scd;
        }
        else if (data is SerializableConfirmationDetails scdDirect)
        {
            details = scdDirect;
        }
        else
        {
            throw new InvalidOperationException(
                $"Missing or invalid data for 'awaiting_approval' transition (callId: {call.Request.CallId})");
        }

        return new WaitingToolCall
        {
            Request = call.Request,
            Outcome = call.Outcome,
            ApprovalMode = call.ApprovalMode,
            StartTime = GetStartTime(call),
            ConfirmationDetails = details,
            CorrelationId = correlationId,
        };
    }

    private static SuccessfulToolCall ToSuccess(ToolCallBase call, ToolCallResponseInfo response) =>
        new()
        {
            Request = call.Request,
            Outcome = call.Outcome,
            ApprovalMode = call.ApprovalMode,
            Response = response,
            DurationMs = ComputeDuration(call),
        };

    private static ErroredToolCall ToError(ToolCallBase call, ToolCallResponseInfo response) =>
        new()
        {
            Request = call.Request,
            Outcome = call.Outcome,
            ApprovalMode = call.ApprovalMode,
            Response = response,
            DurationMs = ComputeDuration(call),
        };

    private static CancelledToolCall ToCancelled(ToolCallBase call, object reasonOrResponse)
    {
        ToolCallResponseInfo response;

        if (reasonOrResponse is ToolCallResponseInfo existing)
        {
            response = existing;
        }
        else
        {
            var reason = reasonOrResponse is string s ? s : reasonOrResponse?.ToString() ?? "Cancelled";
            var errorMessage = $"[Operation Cancelled] Reason: {reason}";
            response = new ToolCallResponseInfo
            {
                CallId = call.Request.CallId,
                ResponseParts = new[] { (object)new
                {
                    functionResponse = new
                    {
                        id = call.Request.CallId,
                        name = call.Request.Name,
                        response = new { error = errorMessage }
                    }
                }},
                ContentLength = errorMessage.Length,
            };
        }

        return new CancelledToolCall
        {
            Request = call.Request,
            Outcome = call.Outcome,
            ApprovalMode = call.ApprovalMode,
            Response = response,
            DurationMs = ComputeDuration(call),
        };
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static long? GetStartTime(ToolCallBase call) =>
        call switch
        {
            ValidatingToolCall v => v.StartTime,
            ScheduledToolCall s => s.StartTime,
            ExecutingToolCall e => e.StartTime,
            WaitingToolCall w => w.StartTime,
            _ => null
        };

    private static long? ComputeDuration(ToolCallBase call)
    {
        var start = GetStartTime(call);
        return start.HasValue ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - start.Value : null;
    }

    private static string? GetLiveOutput(ToolCallBase c) =>
        c is ExecutingToolCall e ? e.LiveOutput : null;

    private static string? GetProgressMessage(ToolCallBase c) =>
        c is ExecutingToolCall e ? e.ProgressMessage : null;

    private static double? GetProgressPercent(ToolCallBase c) =>
        c is ExecutingToolCall e ? e.ProgressPercent : null;

    private static long? GetProgress(ToolCallBase c) =>
        c is ExecutingToolCall e ? e.Progress : null;

    private static long? GetProgressTotal(ToolCallBase c) =>
        c is ExecutingToolCall e ? e.ProgressTotal : null;

    private static int? GetPid(ToolCallBase c) =>
        c is ExecutingToolCall e ? e.Pid : null;
}

/// <summary>
/// Patch data for transitioning to Executing state.
/// </summary>
public record ExecutingToolCallPatch
{
    public string? LiveOutput { get; init; }
    public string? ProgressMessage { get; init; }
    public double? ProgressPercent { get; init; }
    public long? Progress { get; init; }
    public long? ProgressTotal { get; init; }
    public int? Pid { get; init; }
}
