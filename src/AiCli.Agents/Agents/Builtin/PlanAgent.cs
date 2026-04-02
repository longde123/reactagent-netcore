using AiCli.Core.Chat;
using AiCli.Core.Logging;
using AiCli.Core.Tools;

namespace AiCli.Core.Agents.Builtin;

/// <summary>
/// Plan agent for creating and managing implementation plans.
/// </summary>
public class PlanAgent : Agent
{
    private readonly LocalExecutor _executor;
    private readonly List<string> _planSteps = new();
    private readonly bool _planOnlyMode;

    /// <summary>
    /// Creates a PlanAgent.
    /// </summary>
    /// <param name="id">Agent instance ID.</param>
    /// <param name="toolRegistry">Tool registry with available tools.</param>
    /// <param name="chat">Content generator for LLM interaction.</param>
    /// <param name="planOnlyMode">If true, agent only explores and plans without executing write operations.</param>
    public PlanAgent(
        string id,
        ToolRegistry toolRegistry,
        IContentGenerator chat,
        bool planOnlyMode = false)
        : base(
            id,
            "Plan",
            "Create detailed implementation plans with step-by-step breakdown",
            AgentKind.Plan,
            new List<string> { "planning", "task_breakdown", "strategy" },
            toolRegistry,
            chat)
    {
        _executor = new LocalExecutor();
        _planOnlyMode = planOnlyMode;
    }

    /// <summary>
    /// Gets the current plan steps.
    /// </summary>
    public IReadOnlyList<string> PlanSteps => _planSteps.AsReadOnly();

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
    /// Creates a plan for a given task.
    /// </summary>
    public async Task<List<string>> CreatePlanAsync(
        string task,
        CancellationToken cancellationToken = default)
    {
        var message = new ContentMessage
        {
            Role = LlmRole.User,
            Parts = new List<ContentPart>
            {
                new TextContentPart($"Create a detailed implementation plan for:\n\n{task}\n\n" +
                    "Break down the task into clear, actionable steps. " +
                    "Each step should be specific and measurable.")
            }
        };

        var response = await Chat.SendMessageAsync(message, cancellationToken);

        // Extract plan steps from response
        var steps = new List<string>();
        var lines = response.Parts
            .OfType<TextContentPart>()
            .SelectMany(p => p.Text.Split('\n'))
            .ToList();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0 &&
                (trimmed.StartsWith("1.") || trimmed.StartsWith("2.") ||
                 trimmed.StartsWith("3.") || trimmed.StartsWith("4.") ||
                 trimmed.StartsWith("5.") || trimmed.StartsWith("6.") ||
                 trimmed.StartsWith("7.") || trimmed.StartsWith("8.") ||
                 trimmed.StartsWith("9.") || trimmed.StartsWith("10.") ||
                 trimmed.StartsWith("-") || trimmed.StartsWith("*")))
            {
                steps.Add(trimmed);
            }
        }

        _planSteps.Clear();
        _planSteps.AddRange(steps);

        return steps;
    }

    /// <summary>
    /// Updates the plan with additional context.
    /// </summary>
    public async Task UpdatePlanAsync(
        string context,
        CancellationToken cancellationToken = default)
    {
        if (_planSteps.Count == 0)
        {
            throw new InvalidOperationException("No plan to update");
        }

        var currentPlan = string.Join("\n", _planSteps);
        var message = new ContentMessage
        {
            Role = LlmRole.User,
            Parts = new List<ContentPart>
            {
                new TextContentPart($"Update the following plan with this context:\n\n" +
                    $"Current plan:\n{currentPlan}\n\n" +
                    $"Additional context:\n{context}")
            }
        };

        await Chat.SendMessageAsync(message, cancellationToken);
    }

    /// <summary>
    /// Validates the plan.
    /// </summary>
    public async Task<bool> ValidatePlanAsync(
        CancellationToken cancellationToken = default)
    {
        if (_planSteps.Count == 0)
        {
            throw new InvalidOperationException("No plan to validate");
        }

        var message = new ContentMessage
        {
            Role = LlmRole.User,
            Parts = new List<ContentPart>
            {
                new TextContentPart($"Validate this plan for completeness and correctness:\n\n" +
                    $"{string.Join("\n", _planSteps)}\n\n" +
                    "Check for: missing steps, unclear instructions, " +
                    "dependencies, and potential issues. " +
                    "Respond with 'VALID' if the plan is good, " +
                    "or 'INVALID' followed by issues.")
            }
        };

        var response = await Chat.SendMessageAsync(message, cancellationToken);
        var text = string.Join("", response.Parts
            .OfType<TextContentPart>()
            .Select(p => p.Text));

        return text.Contains("VALID");
    }

    /// <summary>
    /// Clears the current plan.
    /// </summary>
    public void ClearPlan()
    {
        _planSteps.Clear();
    }

    protected override string? GetSystemInstruction()
    {
        if (_planOnlyMode)
        {
            return """
                You are a Plan agent in READ-ONLY planning mode.

                CRITICAL RULES:
                - You MUST use tools to explore the codebase (list_directory, glob, grep, read_file).
                - Do NOT modify any files. Do NOT run shell commands that change state.
                - Analyze the task, explore relevant code, and produce a DETAILED implementation plan.
                - Your plan must include:
                  1. Summary of what needs to be done
                  2. Files to be created or modified (with exact paths)
                  3. Step-by-step implementation details for each file
                  4. Dependencies and order of operations
                  5. Verification steps (e.g. build commands to run after changes)
                - Be specific: include function names, class names, code structure.
                - Format your plan as a numbered list with clear headings.

                After exploring, output the FULL implementation plan as your final response.
                """;
        }

        return """
            You are a Plan agent specialized in creating and EXECUTING implementation plans.

            CRITICAL RULES:
            - You MUST use tools to complete tasks. Do NOT just produce a plan — execute it too.
            - First explore the workspace with list_directory / glob / grep.
            - Then create files with write_file and run commands with shell.
            - After execution, verify the result (e.g. dotnet build).
            - Finish with a brief summary of what was done.

            DO NOT stop after planning. Carry out every step with tool calls.
            """;
    }
}