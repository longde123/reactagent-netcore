using AiCli.Core.Logging;
using AiCli.Core.Types;

namespace AiCli.Core.Tools.Builtin;

/// <summary>
/// Parameters for the Glob tool.
/// </summary>
public record GlobToolParams
{
    /// <summary>
    /// The glob pattern to match files against.
    /// </summary>
    public required string Pattern { get; init; }

    /// <summary>
    /// The directory to search in (optional, defaults to current directory).
    /// </summary>
    public string? Path { get; init; }
}

/// <summary>
/// Implementation of the Glob tool for file pattern matching.
/// </summary>
public class GlobTool : DeclarativeTool<GlobToolParams, ToolExecutionResult>
{
    public const string ToolName = "glob";
    public const string DisplayName = "Glob";
    public const string Description = "Find files matching a glob pattern. Supports patterns like **/*.cs or src/**/*.json.";

    private readonly ILogger _logger;
    private readonly string _basePath;

    /// <summary>
    /// Initializes a new instance of the GlobTool class.
    /// </summary>
    public GlobTool(string basePath)
        : base(
            ToolName,
            DisplayName,
            Description,
            ToolKind.Search,
            GetParameterSchema())
    {
        _logger = LoggerHelper.ForContext<GlobTool>();
        _basePath = basePath;
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
                    "pattern",
                    new
                    {
                        type = "string",
                        description = "The glob pattern to match files against."
                    }
                },
                {
                    "path",
                    new
                    {
                        type = "string",
                        description = "The directory to search in (optional)."
                    }
                }
            },
            required = new[] { "pattern" }
        };
    }

    /// <summary>
    /// Validates the tool parameters.
    /// </summary>
    protected override string? ValidateToolParams(GlobToolParams parameters)
    {
        if (string.IsNullOrWhiteSpace(parameters.Pattern))
        {
            return "The 'pattern' parameter must not be empty.";
        }

        // Validate path if provided
        if (!string.IsNullOrWhiteSpace(parameters.Path))
        {
            var fullPath = System.IO.Path.IsPathRooted(parameters.Path)
                ? parameters.Path
                : System.IO.Path.Combine(_basePath, parameters.Path);

            if (!Directory.Exists(fullPath))
            {
                return $"Directory not found: {parameters.Path}";
            }
        }

        return null;
    }

    public override IToolInvocation<GlobToolParams, ToolExecutionResult> Build(GlobToolParams parameters)
    {
        var resolvedPath = string.IsNullOrWhiteSpace(parameters.Path)
            ? _basePath
            : Path.IsPathRooted(parameters.Path)
                ? parameters.Path
                : Path.Combine(_basePath, parameters.Path);

        return new GlobToolInvocation(parameters, resolvedPath, _logger);
    }
}

public class GlobToolInvocation : BaseToolInvocation<GlobToolParams, ToolExecutionResult>
{
    private readonly string _resolvedPath;
    private readonly ILogger _logger;

    public GlobToolInvocation(GlobToolParams parameters, string resolvedPath, ILogger logger) : base(parameters)
    {
        _resolvedPath = resolvedPath;
        _logger = logger;
        ToolName = GlobTool.ToolName;
        ToolDisplayName = GlobTool.DisplayName;
    }

    protected override ToolKind Kind => ToolKind.Search;

    public override string GetDescription() => $"Find files matching '{Parameters.Pattern}' in '{_resolvedPath}'";

    public override IReadOnlyList<ToolLocation> GetToolLocations() =>
        new List<ToolLocation> { new ToolLocation { Path = _resolvedPath } };

    public override async Task<ToolExecutionResult> ExecuteAsync(CancellationToken cancellationToken, Action<ToolLiveOutput>? updateOutput = null)
    {
        try
        {
            if (!Directory.Exists(_resolvedPath))
            {
                return ToolExecutionResult.Failure($"Directory not found: {_resolvedPath}", ToolErrorType.NotFound);
            }

            var regex = new System.Text.RegularExpressions.Regex(
                "^" + System.Text.RegularExpressions.Regex.Escape(Parameters.Pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            var matches = await Task.Run(() =>
                Directory.GetFiles(_resolvedPath, "*", SearchOption.AllDirectories)
                    .Select(file => Path.GetRelativePath(_resolvedPath, file))
                    .Where(path => regex.IsMatch(path))
                    .ToList(),
                cancellationToken);

            var text = matches.Count == 0
                ? $"No files found matching pattern '{Parameters.Pattern}'"
                : string.Join('\n', matches);

            return new ToolExecutionResult
            {
                LlmContent = new TextContentPart(text),
                ReturnDisplay = new ToolResultDisplay.TextToolResultDisplay(text),
                Data = new Dictionary<string, object>
                {
                    { "file_count", matches.Count },
                    { "pattern", Parameters.Pattern }
                }
            };
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Glob execution failed for path: {Path}", _resolvedPath);
            return ToolExecutionResult.Failure(ex.Message, ToolErrorType.Unknown);
        }
    }
}
