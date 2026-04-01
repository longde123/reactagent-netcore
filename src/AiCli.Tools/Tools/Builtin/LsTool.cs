using AiCli.Core.Logging;
using AiCli.Core.Types;

namespace AiCli.Core.Tools.Builtin;

/// <summary>
/// Parameters for the Ls (list directory) tool.
/// </summary>
public record LsToolParams
{
    /// <summary>
    /// The path to the directory to list.
    /// </summary>
    public string? Path { get; init; } = ".";

    /// <summary>
    /// Whether to list directories only.
    /// </summary>
    public bool? DirectoriesOnly { get; init; } = false;

    /// <summary>
    /// Maximum depth to list.
    /// </summary>
    public int? MaxDepth { get; init; } = 1;
}

/// <summary>
/// Implementation of the Ls tool for listing directory contents.
/// </summary>
public class LsTool : DeclarativeTool<LsToolParams, ToolExecutionResult>
{
    public const string ToolName = "list_directory";
    public const string DisplayName = "List Directory";
    public const string Description = "List the contents of a directory.";

    private readonly ILogger _logger;
    private readonly string _targetDirectory;

    /// <summary>
    /// Initializes a new instance of the LsTool class.
    /// </summary>
    public LsTool(string targetDirectory)
        : base(
            ToolName,
            DisplayName,
            Description,
            ToolKind.Read,
            GetParameterSchema())
    {
        _logger = LoggerHelper.ForContext<LsTool>();
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
                    "path",
                    new
                    {
                        type = "string",
                        description = "The path to the directory to list. Defaults to current directory."
                    }
                },
                {
                    "directories_only",
                    new
                    {
                        type = "boolean",
                        description = "Whether to list directories only. Defaults to false."
                    }
                },
                {
                    "max_depth",
                    new
                    {
                        type = "integer",
                        description = "Maximum depth to list. Defaults to 1 (only immediate children)."
                    }
                }
            }
        };
    }

    /// <summary>
    /// Creates a tool invocation for the given parameters.
    /// </summary>
    public override IToolInvocation<LsToolParams, ToolExecutionResult> Build(LsToolParams parameters)
    {
        var resolvedPath = string.IsNullOrWhiteSpace(parameters.Path)
            ? _targetDirectory
            : Path.GetFullPath(Path.Combine(_targetDirectory, parameters.Path!));

        return new LsToolInvocation(parameters, resolvedPath, _logger);
    }
}

/// <summary>
/// Invocation for the Ls tool.
/// </summary>
public class LsToolInvocation : BaseToolInvocation<LsToolParams, ToolExecutionResult>
{
    private readonly string _resolvedPath;
    private readonly ILogger _logger;

    public LsToolInvocation(
        LsToolParams parameters,
        string resolvedPath,
        ILogger logger) : base(parameters)
    {
        _resolvedPath = resolvedPath;
        _logger = logger;
        ToolName = LsTool.ToolName;
        ToolDisplayName = LsTool.DisplayName;
    }

    protected override ToolKind Kind => ToolKind.Read;

    public override string GetDescription()
    {
        var relativePath = Path.GetRelativePath(Environment.CurrentDirectory, _resolvedPath);
        return $"List directory: {relativePath}";
    }

    public override IReadOnlyList<ToolLocation> GetToolLocations() =>
        new List<ToolLocation> { new ToolLocation { Path = _resolvedPath } };

    public override async Task<ToolExecutionResult> ExecuteAsync(
        CancellationToken cancellationToken,
        Action<ToolLiveOutput>? updateOutput = null)
    {
        try
        {
            _logger.Verbose("Listing directory: {Path}", _resolvedPath);

            if (!Directory.Exists(_resolvedPath))
            {
                return ToolExecutionResult.Failure(
                    $"Directory not found: {_resolvedPath}",
                    ToolErrorType.NotFound);
            }

            var directoriesOnly = Parameters.DirectoriesOnly ?? false;
            var maxDepth = Parameters.MaxDepth ?? 1;

            var entries = ListDirectory(_resolvedPath, maxDepth, directoriesOnly);

            var output = FormatOutput(entries);
            _logger.Verbose("Found {Count} entries", entries.Count);

            return new ToolExecutionResult
            {
                LlmContent = new TextContentPart(output),
                ReturnDisplay = new MarkdownToolResultDisplay(output),
                Data = new Dictionary<string, object>
                {
                    { "entries", entries },
                    { "path", _resolvedPath }
                }
            };
        }
        catch (OperationCanceledException)
        {
            _logger.Information("Directory listing cancelled");
            return ToolExecutionResult.Failure(
                "Operation cancelled by user",
                ToolErrorType.Cancellation);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.Warning(ex, "Permission denied listing directory: {Path}", _resolvedPath);
            return ToolExecutionResult.Failure(
                $"Permission denied: {ex.Message}",
                ToolErrorType.Permission);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Unexpected error listing directory: {Path}", _resolvedPath);
            return ToolExecutionResult.Failure(
                $"Unexpected error: {ex.Message}",
                ToolErrorType.Unknown);
        }
    }

    /// <summary>
    /// Lists directory entries recursively.
    /// </summary>
    private List<DirectoryEntry> ListDirectory(string path, int maxDepth, bool directoriesOnly)
    {
        var entries = new List<DirectoryEntry>();
        ListDirectoryRecursive(path, "", 0, maxDepth, directoriesOnly, entries);
        return entries;
    }

    /// <summary>
    /// Recursive helper for listing directories.
    /// </summary>
    private void ListDirectoryRecursive(
        string basePath,
        string relativePath,
        int currentDepth,
        int maxDepth,
        bool directoriesOnly,
        List<DirectoryEntry> entries)
    {
        if (currentDepth > maxDepth)
            return;

        var fullPath = Path.Combine(basePath, relativePath);

        try
        {
            var items = Directory.GetFileSystemEntries(fullPath);

            foreach (var item in items)
            {
                var isDirectory = Directory.Exists(item);
                var name = Path.GetFileName(item);
                var entryRelativePath = Path.Combine(relativePath, name);

                if (!directoriesOnly || isDirectory)
                {
                    entries.Add(new DirectoryEntry
                    {
                        Name = name,
                        Path = entryRelativePath,
                        Type = isDirectory ? "directory" : "file",
                        Depth = currentDepth
                    });
                }

                if (isDirectory && currentDepth < maxDepth)
                {
                    ListDirectoryRecursive(basePath, entryRelativePath, currentDepth + 1, maxDepth, directoriesOnly, entries);
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Skip directories we can't access
            _logger.Verbose("Skipping inaccessible directory: {Path}", fullPath);
        }
    }

    /// <summary>
    /// Formats the directory listing output.
    /// </summary>
    private string FormatOutput(List<DirectoryEntry> entries)
    {
        if (entries.Count == 0)
        {
            return "Directory is empty.";
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("```\n");

        var maxNameLength = entries.Max(e => e.Name.Length);
        var prefix = Path.GetPathRoot(_resolvedPath) ?? "";

        foreach (var entry in entries)
        {
            var indent = new string(' ', entry.Depth * 2);
            var typeIcon = entry.Type == "directory" ? "/" : "";
            var paddedName = entry.Name.PadRight(maxNameLength);

            sb.AppendLine($"{indent}{paddedName}{typeIcon}  [{entry.Type}]");
        }

        sb.AppendLine("```");
        sb.AppendLine($"Total: {entries.Count} items");

        return sb.ToString();
    }
}

/// <summary>
/// Represents a directory entry.
/// </summary>
internal record DirectoryEntry
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required string Type { get; init; }
    public required int Depth { get; init; }
}
