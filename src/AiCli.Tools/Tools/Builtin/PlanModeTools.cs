using AiCli.Core.Logging;
using AiCli.Core.Types;

namespace AiCli.Core.Tools.Builtin;

/// <summary>
/// Implementation of EnterPlanMode tool.
/// </summary>
public class EnterPlanModeTool : DeclarativeTool<object, ToolExecutionResult>
{
    public const string ToolName = "enter_plan_mode";
    public const string DisplayName = "Enter Plan Mode";
    public const string Description = "Enter planning mode for structured implementation.";

    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the EnterPlanModeTool class.
    /// </summary>
    public EnterPlanModeTool()
        : base(
            ToolName,
            DisplayName,
            Description,
            ToolKind.Plan,
            GetParameterSchema())
    {
        _logger = LoggerHelper.ForContext<EnterPlanModeTool>();
    }

    /// <summary>
    /// Gets the parameter schema for this tool.
    /// </summary>
    private static object GetParameterSchema()
    {
        return new
        {
            type = "object",
            description = "Enter planning mode. No parameters required."
        };
    }

    /// <summary>
    /// Validates the tool parameters.
    /// </summary>
    protected override string? ValidateToolParams(object parameters)
    {
        return null; // No parameters to validate
    }

    /// <summary>
    /// Creates a tool invocation.
    /// </summary>
    public override IToolInvocation<object, ToolExecutionResult> Build(object parameters)
    {
        return new EnterPlanModeToolInvocation(_logger);
    }
}

/// <summary>
/// Invocation for the EnterPlanMode tool.
/// </summary>
public class EnterPlanModeToolInvocation : BaseToolInvocation<object, ToolExecutionResult>
{
    private readonly ILogger _logger;

    public EnterPlanModeToolInvocation(ILogger logger) : base(new object())
    {
        _logger = logger;
        ToolName = EnterPlanModeTool.ToolName;
        ToolDisplayName = EnterPlanModeTool.DisplayName;
    }

    protected override ToolKind Kind => ToolKind.Plan;

    public override string GetDescription()
    {
        return "Enter plan mode";
    }

    public override IReadOnlyList<ToolLocation> GetToolLocations() => Array.Empty<ToolLocation>();

    public override async Task<ToolExecutionResult> ExecuteAsync(
        CancellationToken cancellationToken,
        Action<ToolLiveOutput>? updateOutput = null)
    {
        _logger.Information("Entering plan mode");

        return new ToolExecutionResult
        {
            LlmContent = new TextContentPart("Plan mode activated. In this mode, you can plan your implementation before executing."),
            ReturnDisplay = new MarkdownToolResultDisplay(
                @"```
# Plan Mode

You are now in **plan mode**. In this mode:

- You can analyze the codebase and create a structured implementation plan
- Use tools to explore and understand the code
- Document your approach in the plan file
- Review and refine the plan before execution
- Exit plan mode when ready to implement
```
")
        };
    }
}

/// <summary>
/// Implementation of ExitPlanMode tool.
/// </summary>
public class ExitPlanModeTool : DeclarativeTool<object, ToolExecutionResult>
{
    public const string ToolName = "exit_plan_mode";
    public const string DisplayName = "Exit Plan Mode";
    public const string Description = "Exit planning mode and return to normal operation.";

    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the ExitPlanModeTool class.
    /// </summary>
    public ExitPlanModeTool()
        : base(
            ToolName,
            DisplayName,
            Description,
            ToolKind.SwitchMode,
            GetParameterSchema())
    {
        _logger = LoggerHelper.ForContext<ExitPlanModeTool>();
    }

    /// <summary>
    /// Gets the parameter schema for this tool.
    /// </summary>
    private static object GetParameterSchema()
    {
        return new
        {
            type = "object",
            description = "Exit planning mode. No parameters required."
        };
    }

    /// <summary>
    /// Validates the tool parameters.
    /// </summary>
    protected override string? ValidateToolParams(object parameters)
    {
        return null; // No parameters to validate
    }

    /// <summary>
    /// Creates a tool invocation.
    /// </summary>
    public override IToolInvocation<object, ToolExecutionResult> Build(object parameters)
    {
        return new ExitPlanModeToolInvocation(_logger);
    }
}

/// <summary>
/// Invocation for the ExitPlanMode tool.
/// </summary>
public class ExitPlanModeToolInvocation : BaseToolInvocation<object, ToolExecutionResult>
{
    private readonly ILogger _logger;

    public ExitPlanModeToolInvocation(ILogger logger) : base(new object())
    {
        _logger = logger;
        ToolName = ExitPlanModeTool.ToolName;
        ToolDisplayName = ExitPlanModeTool.DisplayName;
    }

    protected override ToolKind Kind => ToolKind.SwitchMode;

    public override string GetDescription()
    {
        return "Exit plan mode";
    }

    public override IReadOnlyList<ToolLocation> GetToolLocations() => Array.Empty<ToolLocation>();

    public override async Task<ToolExecutionResult> ExecuteAsync(
        CancellationToken cancellationToken,
        Action<ToolLiveOutput>? updateOutput = null)
    {
        _logger.Information("Exiting plan mode");

        return new ToolExecutionResult
        {
            LlmContent = new TextContentPart("Exited plan mode. Returning to normal operation."),
            ReturnDisplay = new MarkdownToolResultDisplay(
                @"```

# Normal Mode

You have exited **plan mode** and returned to normal operation mode.

```
")
        };
    }
}
