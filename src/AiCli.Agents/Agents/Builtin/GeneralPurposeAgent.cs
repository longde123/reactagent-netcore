using AiCli.Core.Chat;
using AiCli.Core.Logging;
using AiCli.Core.Tools;

namespace AiCli.Core.Agents.Builtin;

/// <summary>
/// General purpose agent for a wide range of tasks.
/// </summary>
public class GeneralPurposeAgent : Agent
{
    private readonly LocalExecutor _executor;

    public GeneralPurposeAgent(
        string id,
        ToolRegistry toolRegistry,
        IContentGenerator chat)
        : base(
            id,
            "General Purpose",
            "Capable of handling a wide range of tasks and delegating to specialists",
            AgentKind.GeneralPurpose,
            new List<string>
            {
                "general",
                "delegation",
                "coordination",
                "multi_task"
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

    /// <summary>
    /// Determines if a task should be delegated to a specialist agent.
    /// </summary>
    public bool ShouldDelegate(string task, Dictionary<string, string> availableAgents)
    {
        // Check for specialist keywords
        var exploreKeywords = new[] { "explore", "search", "find", "locate", "discover" };
        var planKeywords = new[] { "plan", "breakdown", "strategy", "implement" };
        var codeKeywords = new[] { "refactor", "debug", "fix", "optimize", "code" };

        var lowerTask = task.ToLower();

        if (exploreKeywords.Any(k => lowerTask.Contains(k)) &&
            availableAgents.ContainsKey("explore"))
        {
            return true;
        }

        if (planKeywords.Any(k => lowerTask.Contains(k)) &&
            availableAgents.ContainsKey("plan"))
        {
            return true;
        }

        if (codeKeywords.Any(k => lowerTask.Contains(k)) &&
            availableAgents.ContainsKey("code"))
        {
            return true;
        }

        return false;
    }

    protected override string? GetSystemInstruction() => """
        You are a General Purpose AI agent. Your job is to accomplish the user's task by calling tools.

        CRITICAL RULES:
        - You MUST call tools to complete tasks. Do NOT just think or explain — take action.
        - After creating files, run shell to verify (e.g. "dotnet build").
        - Finish with a brief summary of what was created or changed.

        NEW PROJECT RULES (strictly follow):
        - When creating a NEW standalone project, ALWAYS use "dotnet new" via shell first:
            shell command: dotnet new console -n ProjectName -o ProjectName
          This creates the correct directory structure automatically.
        - NEVER place a new project's files directly in the current directory or inside
          existing project folders (e.g. never write to Commands/, src/, or ./).
        - The correct .NET SDK name is "Microsoft.NET.Sdk", NOT "Microsoft.NET.Sdk.Console".
        - New project files must be under their own subdirectory:
            ProjectName/Program.cs
            ProjectName/ProjectName.csproj

        Tool usage guide:
        - list_directory / glob / grep — explore the workspace
        - read_file — read a file before editing it
        - write_file — create or overwrite a file (always in a proper subdirectory)
        - edit_file — make targeted changes to an existing file
        - shell — run commands: dotnet new / dotnet build / dotnet run / git / etc.
        - web_search / web_fetch — look up docs or examples

        DO NOT stop after planning. Execute every step until the task is fully done.
        """;
}

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
