using AiCli.Core.Logging;
using AiCli.Core.Types;

namespace AiCli.Core.Tools.Builtin;

/// <summary>
/// Parameters for WriteFile tool.
/// </summary>
public record WriteFileToolParams
{
    /// <summary>
    /// The path to the file to write.
    /// </summary>
    public required string FilePath { get; init; }

    /// <summary>
    /// The content to write to the file.
    /// </summary>
    public required string Content { get; init; }
}

/// <summary>
/// Implementation of WriteFile tool.
/// </summary>
public class WriteFileTool : DeclarativeTool<WriteFileToolParams, ToolExecutionResult>
{
    public const string ToolName = "write_file";
    public const string DisplayName = "Write File";
    public const string Description = "Write contents to a file.";

    private readonly ILogger _logger;
    private readonly string _targetDirectory;

    /// <summary>
    /// Initializes a new instance of the WriteFileTool class.
    /// </summary>
    public WriteFileTool(string targetDirectory)
        : base(
            ToolName,
            DisplayName,
            Description,
            ToolKind.Edit,
            GetParameterSchema())
    {
        _logger = LoggerHelper.ForContext<WriteFileTool>();
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
                        description = "The path to the file to write."
                    }
                },
                {
                    "content",
                    new
                    {
                        type = "string",
                        description = "The content to write to the file."
                    }
                }
            },
            required = new[] { "file_path", "content" }
        };
    }

    /// <summary>
    /// Validates the tool parameters.
    /// </summary>
    protected override string? ValidateToolParams(WriteFileToolParams parameters)
    {
        if (string.IsNullOrWhiteSpace(parameters.FilePath))
        {
            return "The 'file_path' parameter must not be empty.";
        }

        if (string.IsNullOrWhiteSpace(parameters.Content))
        {
            return "The 'content' parameter must not be empty.";
        }

        return null;
    }

    /// <summary>
    /// Creates a tool invocation for the given parameters.
    /// </summary>
    public override IToolInvocation<WriteFileToolParams, ToolExecutionResult> Build(WriteFileToolParams parameters)
    {
        var resolvedPath = Path.GetFullPath(Path.Combine(_targetDirectory, parameters.FilePath));
        return new WriteFileToolInvocation(parameters, resolvedPath, _logger);
    }
}

/// <summary>
/// Invocation for the WriteFile tool.
/// </summary>
public class WriteFileToolInvocation : BaseToolInvocation<WriteFileToolParams, ToolExecutionResult>
{
    private readonly string _resolvedPath;
    private readonly ILogger _logger;

    public WriteFileToolInvocation(
        WriteFileToolParams parameters,
        string resolvedPath,
        ILogger logger) : base(parameters)
    {
        _resolvedPath = resolvedPath;
        _logger = logger;
        ToolName = WriteFileTool.ToolName;
        ToolDisplayName = WriteFileTool.DisplayName;
    }

    protected override ToolKind Kind => ToolKind.Edit;

    public override string GetDescription()
    {
        var relativePath = Path.GetRelativePath(Environment.CurrentDirectory, _resolvedPath);
        return $"Write {Parameters.Content.Length} characters to file: {relativePath}";
    }

    public override IReadOnlyList<ToolLocation> GetToolLocations() =>
        new List<ToolLocation> { new ToolLocation { Path = _resolvedPath } };

    public override async Task<ToolExecutionResult> ExecuteAsync(
        CancellationToken cancellationToken,
        Action<ToolLiveOutput>? updateOutput = null)
    {
        try
        {
            _logger.Verbose("Writing {CharCount} characters to file: {Path}",
                Parameters.Content.Length, _resolvedPath);

            // Create directory if it doesn't exist
            var directory = Path.GetDirectoryName(_resolvedPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Write content to file
            await File.WriteAllTextAsync(_resolvedPath, Parameters.Content, cancellationToken);

            _logger.Verbose("Successfully wrote {CharCount} characters to file",
                Parameters.Content.Length);

            return new ToolExecutionResult
            {
                LlmContent = new TextContentPart(
                    $"Successfully wrote {Parameters.Content.Length} characters to {Parameters.FilePath}"),
                ReturnDisplay = new MarkdownToolResultDisplay(
                    $"```\nWritten to file: {Parameters.FilePath}\n\n{Parameters.Content}\n```")
            };
        }
        catch (OperationCanceledException)
        {
            _logger.Information("File write cancelled");
            return ToolExecutionResult.Failure(
                "Operation cancelled by user",
                ToolErrorType.Cancellation);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.Warning(ex, "Permission denied writing file: {Path}", _resolvedPath);
            return ToolExecutionResult.Failure(
                $"Permission denied: {ex.Message}",
                ToolErrorType.Permission);
        }
        catch (IOException ex)
        {
            _logger.Error(ex, "IO error writing file: {Path}", _resolvedPath);
            return ToolExecutionResult.Failure(
                $"IO error: {ex.Message}",
                ToolErrorType.IOError);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Unexpected error writing file: {Path}", _resolvedPath);
            return ToolExecutionResult.Failure(
                $"Unexpected error: {ex.Message}",
                ToolErrorType.Unknown);
        }
    }
}
