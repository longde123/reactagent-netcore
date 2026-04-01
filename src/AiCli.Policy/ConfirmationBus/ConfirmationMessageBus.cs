using AiCli.Core.Logging;
using AiCli.Core.Policy;
using AiCli.Core.Types;
using Serilog;
using System.Collections.Concurrent;

namespace AiCli.Core.ConfirmationBus;

/// <summary>
/// Policy-aware message bus for tool confirmation flow.
/// Intercepts TOOL_CONFIRMATION_REQUEST messages, checks policy, and routes accordingly.
/// Ported from packages/core/src/confirmation-bus/message-bus.ts
/// </summary>
public class ConfirmationMessageBus : IDisposable
{
    private static readonly ILogger Logger = LoggerHelper.ForContext<ConfirmationMessageBus>();

    private readonly PolicyEngine _policyEngine;
    private readonly ConcurrentDictionary<string, List<Action<IConfirmationMessage>>> _subscriptions = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IConfirmationMessage>> _pendingRequests = new();
    private bool _disposed;

    public event EventHandler<Exception>? Error;

    public ConfirmationMessageBus(PolicyEngine policyEngine)
    {
        _policyEngine = policyEngine;
    }

    // ─── Subscribe / Unsubscribe ─────────────────────────────────────────────

    public void Subscribe(MessageBusType type, Action<IConfirmationMessage> listener)
    {
        var key = type.ToString();
        _subscriptions.AddOrUpdate(
            key,
            _ => new List<Action<IConfirmationMessage>> { listener },
            (_, existing) => { existing.Add(listener); return existing; });
    }

    public void Unsubscribe(MessageBusType type, Action<IConfirmationMessage> listener)
    {
        var key = type.ToString();
        if (_subscriptions.TryGetValue(key, out var handlers))
        {
            handlers.Remove(listener);
            if (handlers.Count == 0)
                _subscriptions.TryRemove(key, out _);
        }
    }

    public int ListenerCount(MessageBusType type)
    {
        var key = type.ToString();
        return _subscriptions.TryGetValue(key, out var handlers) ? handlers.Count : 0;
    }

    // ─── Publish ─────────────────────────────────────────────────────────────

    public async Task PublishAsync(IConfirmationMessage message)
    {
        try
        {
            if (message is ToolConfirmationRequest request)
            {
                await HandleConfirmationRequestAsync(request);
            }
            else
            {
                EmitMessage(message);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error publishing message of type {Type}", message.Type);
            Error?.Invoke(this, ex);
        }
    }

    /// <summary>
    /// Request-response pattern: publishes a confirmation request and waits for the response.
    /// The correlationId is used to match the response.
    /// </summary>
    public async Task<ToolConfirmationResponse> RequestConfirmationAsync(
        ToolConfirmationRequest request,
        TimeSpan? timeout = null)
    {
        var correlationId = request.CorrelationId;
        var tcs = new TaskCompletionSource<IConfirmationMessage>();
        _pendingRequests[correlationId] = tcs;

        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(60);
        using var cts = new CancellationTokenSource(effectiveTimeout);
        cts.Token.Register(() =>
        {
            if (_pendingRequests.TryRemove(correlationId, out _))
                tcs.TrySetException(new TimeoutException(
                    $"Request timed out waiting for confirmation response (correlationId={correlationId})"));
        });

        // Subscribe to responses to match this correlation
        Action<IConfirmationMessage>? handler = null;
        handler = msg =>
        {
            if (msg is ToolConfirmationResponse resp && resp.CorrelationId == correlationId)
            {
                if (_pendingRequests.TryRemove(correlationId, out _))
                {
                    Unsubscribe(MessageBusType.ToolConfirmationResponse, handler!);
                    tcs.TrySetResult(msg);
                }
            }
        };
        Subscribe(MessageBusType.ToolConfirmationResponse, handler);

        await PublishAsync(request);

        return (ToolConfirmationResponse)await tcs.Task;
    }

    // ─── Internal ────────────────────────────────────────────────────────────

    private async Task HandleConfirmationRequestAsync(ToolConfirmationRequest request)
    {
        var context = new PolicyContext
        {
            SessionId = request.CorrelationId,
            UserId = null,
            ResourceType = ResourceType.ToolInvocation,
            Resource = BuildResource(request),
            ToolName = request.ToolCall.Name,
        };

        var evaluation = _policyEngine.Evaluate(context);

        switch (evaluation.Decision)
        {
            case PolicyDecisionType.Allow:
                EmitMessage(new ToolConfirmationResponse
                {
                    CorrelationId = request.CorrelationId,
                    Confirmed = true,
                });
                break;

            case PolicyDecisionType.Deny:
                EmitMessage(new ToolPolicyRejection { ToolCall = request.ToolCall });
                EmitMessage(new ToolConfirmationResponse
                {
                    CorrelationId = request.CorrelationId,
                    Confirmed = false,
                });
                break;

            case PolicyDecisionType.AskUser:
            case PolicyDecisionType.AskWithExplanation:
                // Pass to UI if there are listeners; otherwise auto-deny with user-confirmation flag
                if (ListenerCount(MessageBusType.ToolConfirmationRequest) > 0)
                {
                    EmitMessage(request);
                }
                else
                {
                    EmitMessage(new ToolConfirmationResponse
                    {
                        CorrelationId = request.CorrelationId,
                        Confirmed = false,
                        RequiresUserConfirmation = true,
                    });
                }
                break;

            default:
                throw new InvalidOperationException($"Unknown policy decision: {evaluation.Decision}");
        }

        await Task.CompletedTask;
    }

    private static Dictionary<string, object> BuildResource(ToolConfirmationRequest request)
    {
        var resource = new Dictionary<string, object>
        {
            ["toolName"] = request.ToolCall.Name,
        };

        if (request.ToolCall.Args != null)
        {
            foreach (var (k, v) in request.ToolCall.Args)
            {
                if (v != null) resource[$"arg.{k}"] = v;
            }
        }

        if (request.ToolAnnotations != null)
        {
            foreach (var (k, v) in request.ToolAnnotations)
                resource[k] = v;
        }

        return resource;
    }

    private void EmitMessage(IConfirmationMessage message)
    {
        var key = message.Type.ToString();
        if (!_subscriptions.TryGetValue(key, out var handlers)) return;

        foreach (var handler in handlers.ToList())
        {
            try
            {
                handler(message);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error in confirmation bus handler for type {Type}", message.Type);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var tcs in _pendingRequests.Values)
            tcs.TrySetCanceled();

        _pendingRequests.Clear();
        _subscriptions.Clear();
    }
}