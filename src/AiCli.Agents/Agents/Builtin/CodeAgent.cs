using AiCli.Core.Chat;
using AiCli.Core.Tools;

namespace AiCli.Core.Agents.Builtin;

/// <summary>
/// Code agent for coding, refactoring, and debugging tasks.
/// </summary>
public class CodeAgent : Agent
{
    private readonly LocalExecutor _executor;

    public CodeAgent(
        string id,
        ToolRegistry toolRegistry,
        IContentGenerator chat)
        : base(
            id,
            "Code",
            "Specialized in coding, refactoring, debugging, and code review",
            AgentKind.Code,
            new List<string>
            {
                "coding",
                "refactoring",
                "debugging",
                "code_review"
            },
            toolRegistry,
            chat)
    {
        _executor = new LocalExecutor();
    }

    /// <summary>
    /// Executes a tool with the local executor.
    /// </summary>
    protected override async Task<ToolExecutionResult> ExecuteToolAsync(
        IToolBuilder tool,
        Dictionary<string, object>? arguments,
        CancellationToken cancellationToken)
    {
        var invocation = tool.Build(arguments ?? new Dictionary<string, object>());
        var options = _executor.CreateOptions(ApprovalMode.Auto);

        return await _executor.ExecuteAsync(invocation, options, cancellationToken);
    }

    protected override string? GetSystemInstruction() => """
                                                         You are a Code agent specialized in writing and editing code.

                                                         CRITICAL RULES:
                                                         - You MUST use tools to complete tasks. Do NOT just think or explain — take action.
                                                         - Always read_file before editing an existing file.
                                                         - Use write_file to create new files, edit_file for targeted changes.
                                                         - After changes, run shell to build/test (e.g. "dotnet build").
                                                         - Finish with a brief summary of what was changed.

                                                         Tool usage:
                                                         - read_file — read before editing
                                                         - write_file — create a new file
                                                         - edit_file — targeted change to existing file
                                                         - shell — compile, run tests, execute commands
                                                         - grep / glob — find relevant files and symbols

                                                         DO NOT stop after analyzing. Write the code and verify it works.
                                                         """;
}