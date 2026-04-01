using AiCli.Core.Logging;
using AiCli.Core.Types;

namespace AiCli.Core.Tools.Builtin;

/// <summary>
/// Parameters for AskUser tool.
/// </summary>
public record AskUserToolParams
{
    /// <summary>
    /// The question to ask the user.
    /// </summary>
    public required string Question { get; init; }

    /// <summary>
    /// Optional default answer.
    /// </summary>
    public string? Default { get; init; }
}

/// <summary>
/// Implementation of AskUser tool for prompting user input.
/// </summary>
public class AskUserTool : DeclarativeTool<AskUserToolParams, ToolExecutionResult>
{
    public const string ToolName = "ask_user";
    public const string DisplayName = "Ask User";
    public const string Description = "Ask a question to the user and get their response.";

    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the AskUserTool class.
    /// </summary>
    public AskUserTool()
        : base(
            ToolName,
            DisplayName,
            Description,
            ToolKind.Communicate,
            GetParameterSchema())
    {
        _logger = LoggerHelper.ForContext<AskUserTool>();
    }

    /// <summary>
    /// Gets the parameter schema for this tool.
    /// </summary>
    private static object GetParameterSchema()
    {
        return new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                {
                    "question",
                    new
                    {
                        type = "string",
                        description = "The question to ask the user."
                    }
                },
                {
                    "default",
                    new
                    {
                        type = "string",
                        description = "Optional default answer."
                    }
                }
            },
            required = new[] { "question" }
        };
    }

    /// <summary>
    /// Validates the tool parameters.
    /// </summary>
    protected override string? ValidateToolParams(AskUserToolParams parameters)
    {
        if (string.IsNullOrWhiteSpace(parameters.Question))
        {
            return "The 'question' parameter must not be empty.";
        }

        return null;
    }

    /// <summary>
    /// Creates a tool invocation for the given parameters.
    /// </summary>
    public override IToolInvocation<AskUserToolParams, ToolExecutionResult> Build(AskUserToolParams parameters)
    {
        return new AskUserToolInvocation(parameters, _logger);
    }
}

/// <summary>
/// Invocation for the AskUser tool.
/// </summary>
public class AskUserToolInvocation : BaseToolInvocation<AskUserToolParams, ToolExecutionResult>
{
    private readonly ILogger _logger;

    public AskUserToolInvocation(
        AskUserToolParams parameters,
        ILogger logger) : base(parameters)
    {
        _logger = logger;
        ToolName = AskUserTool.ToolName;
        ToolDisplayName = AskUserTool.DisplayName;
    }

    protected override ToolKind Kind => ToolKind.Communicate;

    public override string GetDescription()
    {
        var defaultText = string.IsNullOrEmpty(Parameters.Default)
            ? ""
            : $" (default: {Parameters.Default})";
        return $"Ask user: {Parameters.Question}{defaultText}";
    }

    public override IReadOnlyList<ToolLocation> GetToolLocations() => Array.Empty<ToolLocation>();

    public override async Task<ToolExecutionResult> ExecuteAsync(
        CancellationToken cancellationToken,
        Action<ToolLiveOutput>? updateOutput = null)
    {
        // This tool doesn't actually execute - it returns an error
        // The actual user input should be handled by the CLI layer
        _logger.Verbose("Ask user tool called: {Question}", Parameters.Question);

        return ToolExecutionResult.Failure(
            "AskUser tool should be handled by the CLI layer, not executed directly.",
            ToolErrorType.Unknown);
    }
}
