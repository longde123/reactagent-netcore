using AiCli.Core.Chat;
using AiCli.Core.Logging;
using AiCli.Core.Tools;

namespace AiCli.Core.Agents.Builtin;

/// <summary>
/// Explore agent for fast codebase exploration.
/// Uses grep and glob to find files and code patterns.
/// </summary>
public class ExploreAgent : Agent
{
    private readonly LocalExecutor _executor;

    public ExploreAgent(
        string id,
        ToolRegistry toolRegistry,
        IContentGenerator chat)
        : base(
            id,
            "Explore",
            "Fast codebase exploration using grep and glob patterns",
            AgentKind.Explore,
            new List<string> { "search", "file_discovery", "code_exploration" },
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
        You are an Explore agent specialized in codebase exploration.

        CRITICAL RULES:
        - You MUST call tools. Do NOT just think — use glob, grep, read_file to gather info.
        - Start with list_directory or glob to understand the structure.
        - Use grep to find specific symbols, classes, or patterns.
        - Read key files to understand implementation.
        - Report back with clear findings: file paths, line numbers, patterns found.

        DO NOT stop after thinking. Always call tools and produce output.
        """;
}
