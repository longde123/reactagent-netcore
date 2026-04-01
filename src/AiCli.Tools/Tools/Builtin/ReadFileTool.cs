using AiCli.Core.Logging;
using AiCli.Core.Types;

namespace AiCli.Core.Tools.Builtin;

/// <summary>
/// Parameters for the ReadFile tool.
/// </summary>
public record ReadFileToolParams
{
    /// <summary>
    /// The path to the file to read.
    /// </summary>
    public required string FilePath { get; init; }

    /// <summary>
    /// The line number to start reading from (optional, 1-based).
    /// </summary>
    public int? StartLine { get; init; }

    /// <summary>
    /// The line number to end reading at (optional, 1-based, inclusive).
    /// </summary>
    public int? EndLine { get; init; }
}

/// <summary>
/// Implementation of the ReadFile tool.
/// </summary>
public class ReadFileTool : DeclarativeTool<ReadFileToolParams, ToolExecutionResult>
{
    public const string ToolName = "read_file";
    public const string DisplayName = "Read File";
    public const string Description = "Read the contents of a file. Supports reading specific line ranges.";

    private readonly ILogger _logger;
    private readonly string _targetDirectory;

    /// <summary>
    /// Initializes a new instance of the ReadFileTool class.
    /// </summary>
    public ReadFileTool(string targetDirectory)
        : base(
            ToolName,
            DisplayName,
            Description,
            ToolKind.Read,
            GetParameterSchema())
    {
        _logger = LoggerHelper.ForContext<ReadFileTool>();
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
                        description = "The path to the file to read."
                    }
                },
                {
                    "start_line",
                    new
                    {
                        type = "integer",
                        description = "The line number to start reading from (1-based)."
                    }
                },
                {
                    "end_line",
                    new
                    {
                        type = "integer",
                        description = "The line number to end reading at (1-based, inclusive)."
                    }
                }
            },
            required = new[] { "file_path" }
        };
    }

    /// <summary>
    /// Validates the tool parameters.
    /// </summary>
    protected override string? ValidateToolParams(ReadFileToolParams parameters)
    {
        if (string.IsNullOrWhiteSpace(parameters.FilePath))
        {
            return "The 'file_path' parameter must not be empty.";
        }

        if (parameters.StartLine.HasValue && parameters.StartLine.Value < 1)
        {
            return "start_line must be at least 1.";
        }

        if (parameters.EndLine.HasValue && parameters.EndLine.Value < 1)
        {
            return "end_line must be at least 1.";
        }

        if (parameters.StartLine.HasValue && parameters.EndLine.HasValue &&
            parameters.StartLine.Value > parameters.EndLine.Value)
        {
            return "start_line cannot be greater than end_line.";
        }

        return null;
    }

    /// <summary>
    /// Creates a tool invocation for the given parameters.
    /// </summary>
    public override IToolInvocation<ReadFileToolParams, ToolExecutionResult> Build(ReadFileToolParams parameters)
    {
        var resolvedPath = Path.GetFullPath(Path.Combine(_targetDirectory, parameters.FilePath));
        return new ReadFileToolInvocation(parameters, resolvedPath, _logger);
    }
}

/// <summary>
/// Invocation for the ReadFile tool.
/// </summary>
public class ReadFileToolInvocation : BaseToolInvocation<ReadFileToolParams, ToolExecutionResult>
{
    private readonly string _resolvedPath;
    private readonly ILogger _logger;

    public ReadFileToolInvocation(
        ReadFileToolParams parameters,
        string resolvedPath,
        ILogger logger) : base(parameters)
    {
        _resolvedPath = resolvedPath;
        _logger = logger;
        ToolName = ReadFileTool.ToolName;
        ToolDisplayName = ReadFileTool.DisplayName;
    }

    protected override ToolKind Kind => ToolKind.Read;

    public override string GetDescription()
    {
        var relativePath = Path.GetRelativePath(Environment.CurrentDirectory, _resolvedPath);
        return $"Read file: {relativePath}";
    }

    public override IReadOnlyList<ToolLocation> GetToolLocations() =>
        new List<ToolLocation>
        {
            new ToolLocation { Path = _resolvedPath, Line = Parameters.StartLine }
        };

    public override async Task<ToolExecutionResult> ExecuteAsync(
        CancellationToken cancellationToken,
        Action<ToolLiveOutput>? updateOutput = null)
    {
        try
        {
            _logger.Verbose("Reading file: {Path}", _resolvedPath);

            if (!File.Exists(_resolvedPath))
            {
                return ToolExecutionResult.Failure(
                    $"File not found: {_resolvedPath}",
                    ToolErrorType.NotFound);
            }

            var content = await File.ReadAllTextAsync(_resolvedPath, cancellationToken);
            var lines = content.Split('\n');
            var totalLines = lines.Length;

            // Apply line range if specified
            if (Parameters.StartLine.HasValue || Parameters.EndLine.HasValue)
            {
                var startIndex = (Parameters.StartLine ?? 1) - 1;
                var endIndex = (Parameters.EndLine ?? totalLines) - 1;

                if (startIndex >= totalLines)
                {
                    return ToolExecutionResult.Failure(
                        $"start_line ({Parameters.StartLine}) is beyond file length ({totalLines})",
                        ToolErrorType.Validation);
                }

                endIndex = Math.Min(endIndex, totalLines - 1);
                var selectedLines = lines[startIndex..(endIndex + 1)];
                content = string.Join('\n', selectedLines);

                var isTruncated = endIndex < totalLines - 1;
                if (isTruncated)
                {
                    content = $@"IMPORTANT: The file content has been truncated.
Status: Showing lines {Parameters.StartLine}-{Parameters.EndLine} of {totalLines} total lines.
Action: To read more of the file, you can use 'start_line' and 'end_line' parameters in a subsequent 'read_file' call. For example, to read the next section of the file, use start_line: {Parameters.EndLine + 1}.

--- FILE CONTENT (truncated) ---
{content}";
                }
            }

            _logger.Verbose("Successfully read {CharCount} characters from file", content.Length);

            var displayContent = new TextToolResultDisplay(content);
            return new ToolExecutionResult
            {
                LlmContent = new TextContentPart(content),
                ReturnDisplay = displayContent,
                Data = new Dictionary<string, object>
                {
                    { "line_count", totalLines },
                    { "char_count", content.Length }
                }
            };
        }
        catch (OperationCanceledException)
        {
            _logger.Information("File read cancelled");
            return ToolExecutionResult.Failure(
                "Operation cancelled by user",
                ToolErrorType.Cancellation);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.Warning(ex, "Permission denied reading file: {Path}", _resolvedPath);
            return ToolExecutionResult.Failure(
                $"Permission denied: {ex.Message}",
                ToolErrorType.Permission);
        }
        catch (IOException ex)
        {
            _logger.Error(ex, "IO error reading file: {Path}", _resolvedPath);
            return ToolExecutionResult.Failure(
                $"IO error: {ex.Message}",
                ToolErrorType.IOError);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Unexpected error reading file: {Path}", _resolvedPath);
            return ToolExecutionResult.Failure(
                $"Unexpected error: {ex.Message}",
                ToolErrorType.Unknown);
        }
    }
}
