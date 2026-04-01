using AiCli.Core.Tools;
using AiCli.Core.Types;

namespace AiCli.Core.Scheduler;

public enum ToolExecutionState
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled,
    RequiresApproval
}

public class ToolExecution
{
    public required string Id { get; init; }
    public required IToolInvocation Invocation { get; init; }
    public required CancellationTokenSource CancellationTokenSource { get; init; }
    public Dictionary<string, object?> Metadata { get; } = new();
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public ToolExecutionState State { get; set; } = ToolExecutionState.Pending;
    public ToolExecutionResult? Result { get; set; }
    public Exception? Error { get; set; }
}

public class ExecutionContext
{
    public string? Name { get; set; }
    public ApprovalMode ApprovalMode { get; set; } = ApprovalMode.Auto;
    public Func<ToolExecution, Task<bool>>? ApproveToolAsync { get; set; }
}

public class ToolApprovalEventArgs : EventArgs
{
    public required ToolExecution Execution { get; init; }
    public ExecutionContext? Context { get; init; }

    public ToolApprovalEventArgs() { }

    public ToolApprovalEventArgs(ToolExecution execution, ExecutionContext? context = null)
    {
        Execution = execution;
        Context = context;
    }
}
