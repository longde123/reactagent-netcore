using AiCli.Core.Logging;
using AiCli.Core.Types;

namespace AiCli.Core.Tools.Builtin;

/// <summary>
/// Parameters for Edit (replace) tool.
/// </summary>
public record EditToolParams
{
    /// <summary>
    /// The path to the file to edit.
    /// </summary>
    public required string FilePath { get; init; }

    /// <summary>
    /// The old string to be replaced.
    /// </summary>
    public required string OldString { get; init; }

    /// <summary>
    /// The new string to replace with.
    /// </summary>
    public required string NewString { get; init; }

    /// <summary>
    /// Number of occurrences to replace (default: all).
    /// </summary>
    public int? ReplaceCount { get; init; } = 1;

    /// <summary>
    /// Whether replacement is case-sensitive.
    /// </summary>
    public bool? CaseSensitive { get; init; } = false;
}

/// <summary>
/// Implementation of Edit tool.
/// </summary>
public class EditTool : DeclarativeTool<EditToolParams, ToolExecutionResult>
{
    public const string ToolName = "edit_file";
    public const string DisplayName = "Edit File";
    public const string Description = "Replace text in a file.";

    private readonly ILogger _logger;
    private readonly string _targetDirectory;

    /// <summary>
    /// Initializes a new instance of the EditTool class.
    /// </summary>
    public EditTool(string targetDirectory)
        : base(
            ToolName,
            DisplayName,
            Description,
            ToolKind.Edit,
            GetParameterSchema())
    {
        _logger = LoggerHelper.ForContext<EditTool>();
        _targetDirectory = targetDirectory;
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
                    "file_path",
                    new
                    {
                        type = "string",
                        description = "The path to the file to edit."
                    }
                },
                {
                    "old_string",
                    new
                    {
                        type = "string",
                        description = "The old string to be replaced."
                    }
                },
                {
                    "new_string",
                    new
                    {
                        type = "string",
                        description = "The new string to replace with."
                    }
                },
                {
                    "replace_count",
                    new
                    {
                        type = "integer",
                        description = "Number of occurrences to replace (default: all)."
                    }
                },
                {
                    "case_sensitive",
                    new
                    {
                        type = "boolean",
                        description = "Whether replacement is case-sensitive. Defaults to false."
                    }
                }
            },
            required = new[] { "file_path", "old_string", "new_string" }
        };
    }

    /// <summary>
    /// Validates the tool parameters.
    /// </summary>
    protected override string? ValidateToolParams(EditToolParams parameters)
    {
        if (string.IsNullOrWhiteSpace(parameters.FilePath))
        {
            return "The 'file_path' parameter must not be empty.";
        }

        if (string.IsNullOrWhiteSpace(parameters.OldString))
        {
            return "The 'old_string' parameter must not be empty.";
        }

        if (string.IsNullOrWhiteSpace(parameters.NewString))
        {
            return "The 'new_string' parameter must not be empty.";
        }

        if (parameters.ReplaceCount.HasValue && parameters.ReplaceCount.Value < 1)
        {
            return "The 'replace_count' must be at least 1.";
        }

        return null;
    }

    /// <summary>
    /// Creates a tool invocation for the given parameters.
    /// </summary>
    public override IToolInvocation<EditToolParams, ToolExecutionResult> Build(EditToolParams parameters)
    {
        var resolvedPath = Path.GetFullPath(Path.Combine(_targetDirectory, parameters.FilePath));
        return new EditToolInvocation(parameters, resolvedPath, _logger);
    }
}

/// <summary>
/// Invocation for the Edit tool.
/// </summary>
public class EditToolInvocation : BaseToolInvocation<EditToolParams, ToolExecutionResult>
{
    private readonly string _resolvedPath;
    private readonly ILogger _logger;

    public EditToolInvocation(
        EditToolParams parameters,
        string resolvedPath,
        ILogger logger) : base(parameters)
    {
        _resolvedPath = resolvedPath;
        _logger = logger;
        ToolName = EditTool.ToolName;
        ToolDisplayName = EditTool.DisplayName;
    }

    protected override ToolKind Kind => ToolKind.Edit;

    public override string GetDescription()
    {
        var caseSensitiveText = Parameters.CaseSensitive ?? false ? "(case-sensitive)" : "";
        var replaceCountText = Parameters.ReplaceCount.HasValue
            ? $" {Parameters.ReplaceCount} occurrence(s)"
            : "all occurrences";

        var relativePath = Path.GetRelativePath(Environment.CurrentDirectory, _resolvedPath);
        return $"Replace {caseSensitiveText} {replaceCountText} of '{Parameters.OldString}' with '{Parameters.NewString}' in {relativePath}";
    }

    public override IReadOnlyList<ToolLocation> GetToolLocations() =>
        new List<ToolLocation> { new ToolLocation { Path = _resolvedPath } };

    public override async Task<ToolExecutionResult> ExecuteAsync(
        CancellationToken cancellationToken,
        Action<ToolLiveOutput>? updateOutput = null)
    {
        try
        {
            _logger.Verbose("Editing file: {Path}", _resolvedPath);

            if (!File.Exists(_resolvedPath))
            {
                return ToolExecutionResult.Failure(
                    $"File not found: {_resolvedPath}",
                    ToolErrorType.NotFound);
            }

            var content = await File.ReadAllTextAsync(_resolvedPath, cancellationToken);
            var oldContent = content;

            var options = Parameters.CaseSensitive ?? false
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;

            var oldString = Parameters.OldString;
            var newString = Parameters.NewString;
            var replaceCount = Parameters.ReplaceCount ?? 1;

            var occurrences = 0;
            var lastIndex = 0;

            // Replace occurrences
            while ((lastIndex = oldContent.IndexOf(oldString, lastIndex, options)) >= 0)
            {
                oldContent = oldContent.Substring(0, lastIndex) +
                    newString +
                    oldContent.Substring(lastIndex + oldString.Length);

                lastIndex += newString.Length;
                occurrences++;

                if (replaceCount > 0 && occurrences >= replaceCount)
                {
                    break;
                }
            }

            if (occurrences == 0)
            {
                _logger.Warning("Old string not found in file: '{oldString}'", Parameters.OldString);
                return ToolExecutionResult.Failure(
                    $"Old string not found: '{Parameters.OldString}'",
                    ToolErrorType.Validation);
            }

            await File.WriteAllTextAsync(_resolvedPath, oldContent, cancellationToken);

            _logger.Verbose("Replaced {Count} occurrence(s) of '{OldString}' with '{NewString}'",
                occurrences, Parameters.OldString, Parameters.NewString);

            return new ToolExecutionResult
            {
                LlmContent = new TextContentPart($"Replaced {occurrences} occurrence(s)"),
                ReturnDisplay = new MarkdownToolResultDisplay(
                    $@"Replaced {occurrences} occurrence(s) in `{Path.GetRelativePath(Environment.CurrentDirectory, _resolvedPath)}`.

**Before:**
```
{Parameters.OldString}
```

**After:**
```
{newString}
```
"),
                Data = new Dictionary<string, object>
                {
                    { "occurrences", occurrences },
                    { "old_string", Parameters.OldString },
                    { "new_string", Parameters.NewString }
                }
            };
        }
        catch (OperationCanceledException)
        {
            _logger.Information("File edit cancelled");
            return ToolExecutionResult.Failure(
                "Operation cancelled by user",
                ToolErrorType.Cancellation);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.Warning(ex, "Permission denied editing file: {Path}", _resolvedPath);
            return ToolExecutionResult.Failure(
                $"Permission denied: {ex.Message}",
                ToolErrorType.Permission);
        }
        catch (IOException ex)
        {
            _logger.Error(ex, "IO error editing file: {Path}", _resolvedPath);
            return ToolExecutionResult.Failure(
                $"IO error: {ex.Message}",
                ToolErrorType.IOError);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Unexpected error editing file: {Path}", _resolvedPath);
            return ToolExecutionResult.Failure(
                $"Unexpected error: {ex.Message}",
                ToolErrorType.Unknown);
        }
    }
}
