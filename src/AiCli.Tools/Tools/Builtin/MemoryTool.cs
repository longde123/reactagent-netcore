using AiCli.Core.Configuration;
using AiCli.Core.Logging;
using AiCli.Core.Types;

namespace AiCli.Core.Tools.Builtin;

/// <summary>
/// Parameters for Memory tool.
/// </summary>
public record MemoryToolParams
{
    /// <summary>
    /// The memory content to save.
    /// </summary>
    public required string Content { get; init; }

    /// <summary>
    /// The memory level to save to.
    /// </summary>
    public MemoryLevel? Level { get; init; } = MemoryLevel.Project;
}

/// <summary>
/// Implementation of Memory tool for managing hierarchical memory.
/// </summary>
public class MemoryTool : DeclarativeTool<MemoryToolParams, ToolExecutionResult>
{
    public const string ToolName = "save_memory";
    public const string DisplayName = "Save Memory";
    public const string Description = "Save information to memory (global or project-specific).";

    private readonly ILogger _logger;
    private readonly Storage _storage;
    private readonly string _targetDirectory;

    /// <summary>
    /// Initializes a new instance of the MemoryTool class.
    /// </summary>
    public MemoryTool(Config config)
        : base(
            ToolName,
            DisplayName,
            Description,
            ToolKind.Other,
            GetParameterSchema())
    {
        _logger = LoggerHelper.ForContext<MemoryTool>();
        _storage = config.Storage;
        _targetDirectory = config.Storage.ProjectConfigDir ?? config.Storage.UserConfigDir;
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
                    "content",
                    new
                    {
                        type = "string",
                        description = "The memory content to save."
                    }
                },
                {
                    "level",
                    new
                    {
                        type = "string",
                        description = "The memory level: 'global', 'extension', or 'project'. Defaults to 'project'.",
                        @enum = new[] { "global", "extension", "project" }
                    }
                }
            },
            required = new[] { "content" }
        };
    }

    /// <summary>
    /// Validates the tool parameters.
    /// </summary>
    protected override string? ValidateToolParams(MemoryToolParams parameters)
    {
        if (string.IsNullOrWhiteSpace(parameters.Content))
        {
            return "The 'content' parameter must not be empty.";
        }

        return null;
    }

    /// <summary>
    /// Creates a tool invocation for the given parameters.
    /// </summary>
    public override IToolInvocation<MemoryToolParams, ToolExecutionResult> Build(MemoryToolParams parameters)
    {
        return new MemoryToolInvocation(parameters, _storage, _targetDirectory, _logger);
    }
}

/// <summary>
/// Invocation for the Memory tool.
/// </summary>
public class MemoryToolInvocation : BaseToolInvocation<MemoryToolParams, ToolExecutionResult>
{
    private readonly Storage _storage;
    private readonly string _targetDirectory;
    private readonly ILogger _logger;

    public MemoryToolInvocation(
        MemoryToolParams parameters,
        Storage storage,
        string targetDirectory,
        ILogger logger) : base(parameters)
    {
        _storage = storage;
        _targetDirectory = targetDirectory;
        _logger = logger;
        ToolName = MemoryTool.ToolName;
        ToolDisplayName = MemoryTool.DisplayName;
    }

    protected override ToolKind Kind => ToolKind.Other;

    public override string GetDescription()
    {
        var levelText = Parameters.Level.HasValue
            ? $" ({Parameters.Level})"
            : "";
        return $"Save memory{levelText}";
    }

    public override IReadOnlyList<ToolLocation> GetToolLocations()
    {
        var memoryPath = Parameters.Level switch
        {
            MemoryLevel.Global => Path.Combine(_storage.UserConfigDir, "memory.md"),
            MemoryLevel.Project => Path.Combine(_targetDirectory ?? "", "memory.md"),
            MemoryLevel.Extension => ""
        };

        return new List<ToolLocation> { new ToolLocation { Path = memoryPath } };
    }

    public override async Task<ToolExecutionResult> ExecuteAsync(
        CancellationToken cancellationToken,
        Action<ToolLiveOutput>? updateOutput = null)
    {
        var memoryPath = Parameters.Level switch
        {
            MemoryLevel.Global => Path.Combine(_storage.UserConfigDir, "memory.md"),
            MemoryLevel.Project => Path.Combine(_targetDirectory ?? "", "memory.md"),
            MemoryLevel.Extension => ""
        };

        try
        {
            _logger.Verbose("Saving memory to level: {Level}, path: {Path}", Parameters.Level, memoryPath);

            // Save memory content
            await _storage.SaveMemoryAsync(Parameters.Content, Parameters.Level ?? MemoryLevel.Project);

            _logger.Verbose("Successfully saved {CharCount} characters to memory", Parameters.Content.Length);

            return new ToolExecutionResult
            {
                LlmContent = new TextContentPart($"Memory saved successfully"),
                ReturnDisplay = new TextToolResultDisplay(
                    $"Memory saved to {Parameters.Level} level ({Parameters.Content.Length} characters)"),
                Data = new Dictionary<string, object>
                {
                    { "level", Parameters.Level?.ToString() ?? "project" },
                    { "char_count", Parameters.Content.Length }
                }
            };
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.Warning(ex, "Permission denied saving memory: {Path}", memoryPath);
            return ToolExecutionResult.Failure(
                $"Permission denied: {ex.Message}",
                ToolErrorType.Permission);
        }
        catch (IOException ex)
        {
            _logger.Error(ex, "IO error saving memory: {Path}", memoryPath);
            return ToolExecutionResult.Failure(
                $"IO error: {ex.Message}",
                ToolErrorType.IOError);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Unexpected error saving memory: {Path}", memoryPath);
            return ToolExecutionResult.Failure(
                $"Unexpected error: {ex.Message}",
                ToolErrorType.Unknown);
        }
    }
}
