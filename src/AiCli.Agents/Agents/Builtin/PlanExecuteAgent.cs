using AiCli.Core.Agents;
using AiCli.Core.Chat;
using AiCli.Core.Tools;
using AiCli.Core.Types;


namespace AiCli.Core.Agents.Builtin;

/// <summary>
/// Agent that executes an approved plan. Injects the plan text into the system prompt
/// so the model follows the plan step by step.
/// </summary>
public class PlanExecuteAgent : Agent
{
    private readonly LocalExecutor _executor;
    private readonly string _planText;

    public PlanExecuteAgent(
        string id,
        ToolRegistry toolRegistry,
        IContentGenerator chat,
        string planText)
        : base(
            id,
            "Plan Executor",
            "Executes an approved implementation plan",
            AgentKind.Code,
            new List<string> { "execution", "coding", "implementation" },
            toolRegistry,
            chat)
    {
        _executor = new LocalExecutor();
        _planText = planText;
    }

    protected override async Task<ToolExecutionResult> ExecuteToolAsync(
        IToolBuilder tool,
        Dictionary<string, object>? arguments,
        CancellationToken cancellationToken)
    {
        var invocation = tool.Build(arguments ?? new Dictionary<string, object>());
        var options = _executor.CreateOptions(ApprovalMode.Auto);
        return await _executor.ExecuteAsync(invocation, options, cancellationToken);
    }

    protected override string? GetSystemInstruction()
    {
        return $"""
                You are an execution agent. You have an APPROVED implementation plan that you MUST execute.

                === APPROVED PLAN ===
                {_planText}
                === END PLAN ===

                CRITICAL RULES:
                - You MUST execute the plan above step by step using tools.
                - Do NOT re-analyze or re-plan — the plan is already approved.
                - Use write_file to create files, edit_file to modify existing files, and shell for commands.
                - After all changes, run shell to verify (e.g. "dotnet build").
                - Finish with a brief summary of what was done.

                Execute every step. Do NOT stop early.
                """;
    }
}