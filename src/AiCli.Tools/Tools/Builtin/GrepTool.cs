using AiCli.Core.Logging;
using AiCli.Core.Types;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AiCli.Core.Tools.Builtin;

/// <summary>
/// Parameters for Grep tool.
/// </summary>
public record GrepToolParams
{
    /// <summary>
    /// The pattern to search for (supports regex).
    /// </summary>
    public required string Pattern { get; init; }

    /// <summary>
    /// The path to search in (default: current directory).
    /// </summary>
    public string? Path { get; init; }

    /// <summary>
    /// Maximum number of results to return (default: 100).
    /// </summary>
    public int? MaxResults { get; init; } = 100;

    /// <summary>
    /// Whether to use ripgrep (if available).
    /// </summary>
    public bool? UseRipgrep { get; init; } = true;
}

/// <summary>
/// Implementation of Grep tool for text searching.
/// </summary>
public class GrepTool : DeclarativeTool<GrepToolParams, ToolExecutionResult>
{
    public const string ToolName = "grep";
    public const string DisplayName = "Search Text";
    public const string Description = "Search for text in files using grep or ripgrep.";

    private readonly ILogger _logger;
    private readonly string _targetDirectory;

    /// <summary>
    /// Initializes a new instance of the GrepTool class.
    /// </summary>
    public GrepTool(string targetDirectory)
        : base(
            ToolName,
            DisplayName,
            Description,
            ToolKind.Search,
            GetParameterSchema())
    {
        _logger = LoggerHelper.ForContext<GrepTool>();
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
                    "pattern",
                    new
                    {
                        type = "string",
                        description = "The regex pattern to search for."
                    }
                },
                {
                    "path",
                    new
                    {
                        type = "string",
                        description = "The path to search in. Defaults to current directory."
                    }
                },
                {
                    "max_results",
                    new
                    {
                        type = "integer",
                        description = "Maximum number of results to return. Defaults to 100."
                    }
                },
                {
                    "use_ripgrep",
                    new
                    {
                        type = "boolean",
                        description = "Whether to use ripgrep if available (faster). Defaults to true."
                    }
                }
            },
            required = new[] { "pattern" }
        };
    }

    /// <summary>
    /// Validates the tool parameters.
    /// </summary>
    protected override string? ValidateToolParams(GrepToolParams parameters)
    {
        if (string.IsNullOrWhiteSpace(parameters.Pattern))
        {
            return "The 'pattern' parameter must not be empty.";
        }

        if (parameters.MaxResults.HasValue && parameters.MaxResults.Value < 1)
        {
            return "The 'max_results' must be at least 1.";
        }

        return null;
    }

    /// <summary>
    /// Creates a tool invocation for the given parameters.
    /// </summary>
    public override IToolInvocation<GrepToolParams, ToolExecutionResult> Build(GrepToolParams parameters)
    {
        var resolvedPath = string.IsNullOrWhiteSpace(parameters.Path)
            ? _targetDirectory
            : Path.GetFullPath(Path.Combine(_targetDirectory, parameters.Path!));

        var useRipgrep = parameters.UseRipgrep ?? true;
        return new GrepToolInvocation(parameters, resolvedPath, useRipgrep, _logger);
    }
}

/// <summary>
/// Invocation for the Grep tool.
/// </summary>
public class GrepToolInvocation : BaseToolInvocation<GrepToolParams, ToolExecutionResult>
{
    private readonly string _resolvedPath;
    private readonly ILogger _logger;
    private readonly bool _useRipgrep;

    public GrepToolInvocation(
        GrepToolParams parameters,
        string resolvedPath,
        bool useRipgrep,
        ILogger logger) : base(parameters)
    {
        _resolvedPath = resolvedPath;
        _logger = logger;
        _useRipgrep = useRipgrep;
        ToolName = GrepTool.ToolName;
        ToolDisplayName = GrepTool.DisplayName;
    }

    protected override ToolKind Kind => ToolKind.Search;

    public override string GetDescription()
    {
        var relativePath = Path.GetRelativePath(Environment.CurrentDirectory, _resolvedPath);
        var tool = _useRipgrep ? "ripgrep" : "grep";
        return $"Search '{Parameters.Pattern}' in {relativePath} using {tool}";
    }

    public override IReadOnlyList<ToolLocation> GetToolLocations()
    {
        var basePath = Path.GetDirectoryName(_resolvedPath) ?? _resolvedPath;
        return new List<ToolLocation> { new ToolLocation { Path = basePath } };
    }

    public override async Task<ToolExecutionResult> ExecuteAsync(
        CancellationToken cancellationToken,
        Action<ToolLiveOutput>? updateOutput = null)
    {
        try
        {
            _logger.Verbose("Searching for pattern: {Pattern}", Parameters.Pattern);

            var basePath = Path.GetDirectoryName(_resolvedPath) ?? _resolvedPath;
            var searchPath = Path.GetDirectoryName(_resolvedPath) ?? "";

            // Choose grep command based on OS and ripgrep availability
            var results = _useRipgrep && IsRipgrepAvailable()
                ? await RunRipgrepAsync(basePath, Parameters.Pattern, cancellationToken)
                : await RunGrepAsync(basePath, Parameters.Pattern, cancellationToken);

            if (results.Count == 0)
            {
                var noMatchesMessage = $"No matches found for pattern: {Parameters.Pattern}";
                _logger.Verbose(noMatchesMessage);
                return new ToolExecutionResult
                {
                    LlmContent = new TextContentPart(noMatchesMessage),
                    ReturnDisplay = new TextToolResultDisplay(noMatchesMessage)
                };
            }

            // Limit results
            var maxResults = Parameters.MaxResults ?? 100;
            var limitedResults = results.Take(maxResults).ToList();

            _logger.Verbose("Found {Count} results (limited to {MaxResults})", results.Count, maxResults);

            var output = FormatResults(limitedResults, Parameters.Pattern);

            return new ToolExecutionResult
            {
                LlmContent = new TextContentPart(output),
                ReturnDisplay = new MarkdownToolResultDisplay(output),
                Data = new Dictionary<string, object>
                {
                    { "match_count", limitedResults.Count },
                    { "pattern", Parameters.Pattern },
                    { "tool", _useRipgrep ? "ripgrep" : "grep" }
                }
            };
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.Warning(ex, "Permission denied searching in: {Path}", _resolvedPath);
            return ToolExecutionResult.Failure(
                $"Permission denied: {ex.Message}",
                ToolErrorType.Permission);
        }
        catch (IOException ex)
        {
            _logger.Error(ex, "IO error searching: {Path}", _resolvedPath);
            return ToolExecutionResult.Failure(
                $"IO error: {ex.Message}",
                ToolErrorType.IOError);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Unexpected error: {Path}", _resolvedPath);
            return ToolExecutionResult.Failure(
                $"Unexpected error: {ex.Message}",
                ToolErrorType.Unknown);
        }
    }

    /// <summary>
    /// Checks if ripgrep is available on this system.
    /// </summary>
    private static bool IsRipgrepAvailable()
    {
        var process = new Process();
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "rg.exe" : "rg",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = true,
                CreateNoWindow = true
            };

            process.StartInfo = startInfo;
            process.Start();
            process.WaitForExit(100);
            var isAvailable = process.ExitCode == 0;
            process.Kill();

            return isAvailable;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Runs ripgrep command.
    /// </summary>
    private static async Task<List<GrepResult>> RunRipgrepAsync(
        string basePath,
        string pattern,
        CancellationToken cancellationToken)
    {
        var results = new List<GrepResult>();

        var process = new Process();
        var startInfo = new ProcessStartInfo
        {
            FileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "rg.exe" : "rg",
            Arguments = $"-n --json \"{pattern}\" \"{basePath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };

        process.StartInfo = startInfo;

        var outputBuilder = new System.Text.StringBuilder();
        var errorBuilder = new System.Text.StringBuilder();

        process.OutputDataReceived += (_, data) => outputBuilder.AppendLine(data?.Data);
        process.ErrorDataReceived += (_, data) => errorBuilder.AppendLine(data?.Data);

        await process.WaitForExitAsync(cancellationToken);

        var output = outputBuilder.ToString();
        var error = errorBuilder.ToString();

        if (!string.IsNullOrEmpty(error) && !string.IsNullOrEmpty(output))
        {
            return results; // ripgrep not available or returned nothing
        }

        // Parse JSON output
        try
        {
            foreach (var line in output.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                var json = System.Text.Json.JsonSerializer.Deserialize<JsonLine>(line);
                if (json?.Data?.Path is not null && json.Data?.Lines?.Count > 0)
                {
                    foreach (var match in json.Data.Lines)
                    {
                        results.Add(new GrepResult
                        {
                            Path = json.Data.Path,
                            LineNumber = match,
                            LineStart = 0,
                            Content = "" // Would need to read line content separately
                        });
                    }
                }
            }
        }
        catch
        {
            // If JSON parsing fails, fall through
        }

        return results;
    }

    /// <summary>
    /// Runs grep command.
    /// </summary>
    private static async Task<List<GrepResult>> RunGrepAsync(
        string basePath,
        string pattern,
        CancellationToken cancellationToken)
    {
        var results = new List<GrepResult>();

        var process = new Process();
        var startInfo = new ProcessStartInfo
        {
            FileName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "grep.exe" : "grep",
            Arguments = $"-rn \"{pattern}\" \"{basePath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };

        process.StartInfo = startInfo;

        var outputBuilder = new System.Text.StringBuilder();
        var errorBuilder = new System.Text.StringBuilder();

        process.OutputDataReceived += (_, data) => outputBuilder.AppendLine(data?.Data);
        process.ErrorDataReceived += (_, data) => errorBuilder.AppendLine(data?.Data);

        await process.WaitForExitAsync(cancellationToken);

        var output = outputBuilder.ToString();
        var error = errorBuilder.ToString();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"grep failed: {error}");
        }

        // Parse grep output (format: "filename:line_number:content")
        foreach (var line in output.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var parts = line.Split(':', 3);
            if (parts.Length >= 3)
            {
                var filename = parts[0].Trim();
                if (int.TryParse(parts[1], out var lineNumber) && int.TryParse(parts[2].Substring(2), out var lineStart))
                {
                    results.Add(new GrepResult
                    {
                        Path = Path.Combine(basePath, filename),
                        LineNumber = lineNumber,
                        LineStart = lineStart,
                        Content = line.Substring(parts[0].Length + parts[1].Length + parts[2].Length)
                    });
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Formats grep/ripgrep results.
    /// </summary>
    private static string FormatResults(List<GrepResult> results, string pattern)
    {
        if (results.Count == 0)
        {
            return $"No matches found for pattern: `{pattern}`";
        }

        var output = new System.Text.StringBuilder();
        output.AppendLine($"Found {results.Count} matches for pattern `{pattern}`:\n");

        var maxNameLength = results.Max(r => Path.GetFileName(r.Path)?.Length ?? 0);
        var basePath = results[0].Path;

        foreach (var result in results)
        {
            var relativePath = Path.GetRelativePath(Environment.CurrentDirectory, result.Path);
            var fileName = Path.GetFileName(result.Path);
            var paddedName = fileName.PadRight(maxNameLength);

            output.AppendLine($"```\n");
            output.AppendLine($"{paddedName}:{result.LineNumber}:");
            output.AppendLine($"    {result.Content}\n");
        }

        output.Append("```");
        output.AppendLine($"\nTotal: {results.Count} matches");

        return output.ToString();
    }
}

/// <summary>
/// Represents a grep/ripgrep result.
/// </summary>
internal record JsonLine
{
    public GrepData? Data { get; init; }
}

/// <summary>
/// Represents grep data from JSON output.
/// </summary>
internal record GrepData
{
    public required string Path { get; init; }
    public List<int>? Lines { get; init; }
}

/// <summary>
/// Represents a grep result.
/// </summary>
internal record GrepResult
{
    public required string Path { get; init; }
    public required int LineNumber { get; init; }
    public required int LineStart { get; init; }
    public required string Content { get; init; }
}
