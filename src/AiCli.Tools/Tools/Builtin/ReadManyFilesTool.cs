using AiCli.Core.Logging;
using AiCli.Core.Types;
using System.Text;
using System.Text.RegularExpressions;

namespace AiCli.Core.Tools.Builtin;

/// <summary>
/// Parameters for the ReadManyFiles tool.
/// </summary>
public record ReadManyFilesToolParams
{
    /// <summary>
    /// Glob patterns for files to include.
    /// </summary>
    public required IReadOnlyList<string> Include { get; init; }

    /// <summary>
    /// Optional glob patterns for files to exclude.
    /// </summary>
    public IReadOnlyList<string>? Exclude { get; init; }

    /// <summary>
    /// Apply default exclusion patterns (node_modules, .git, etc.). Defaults to true.
    /// </summary>
    public bool UseDefaultExcludes { get; init; } = true;
}

/// <summary>
/// Tool for reading and concatenating multiple files using glob patterns.
/// Ported from packages/core/src/tools/read-many-files.ts
/// </summary>
public class ReadManyFilesTool : DeclarativeTool<ReadManyFilesToolParams, ToolExecutionResult>
{
    public const string ToolName = "read_many_files";
    public const string DisplayName = "Read Many Files";
    public const string Description =
        "Read and concatenate the contents of multiple files matched by glob patterns. " +
        "Useful for reading entire directories or sets of related files at once.";

    internal static readonly string[] DefaultExcludes =
    {
        "node_modules/**",
        ".git/**",
        "bin/**",
        "obj/**",
        "dist/**",
        "build/**",
        ".next/**",
        "coverage/**",
        "*.min.js",
        "*.min.css",
        "package-lock.json",
        "yarn.lock",
    };

    internal static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico",
        ".pdf", ".doc", ".docx", ".xls", ".xlsx",
        ".zip", ".tar", ".gz", ".7z", ".rar",
        ".exe", ".dll", ".so", ".dylib",
        ".mp3", ".mp4", ".avi", ".mov",
        ".ttf", ".woff", ".woff2", ".eot",
        ".bin", ".dat",
    };

    internal const long MaxFileSizeBytes = 5 * 1024 * 1024; // 5 MB per file

    private readonly ILogger _logger;
    private readonly string _targetDirectory;

    public ReadManyFilesTool(string targetDirectory)
        : base(
            ToolName,
            DisplayName,
            Description,
            ToolKind.Read,
            GetParameterSchema())
    {
        _logger = LoggerHelper.ForContext<ReadManyFilesTool>();
        _targetDirectory = targetDirectory;
    }

    private static object GetParameterSchema() => new
    {
        type = "object",
        properties = new Dictionary<string, object>
        {
            {
                "include", new
                {
                    type = "array",
                    items = new { type = "string" },
                    description = "Glob patterns for files to include. Example: [\"src/**/*.cs\", \"*.md\"]"
                }
            },
            {
                "exclude", new
                {
                    type = "array",
                    items = new { type = "string" },
                    description = "Optional glob patterns for files to exclude."
                }
            },
            {
                "use_default_excludes", new
                {
                    type = "boolean",
                    description = "Apply default exclusion patterns (node_modules, .git, etc.). Defaults to true."
                }
            },
        },
        required = new[] { "include" }
    };

    protected override string? ValidateToolParams(ReadManyFilesToolParams parameters)
    {
        if (parameters.Include == null || parameters.Include.Count == 0)
            return "The 'include' parameter must contain at least one pattern.";
        return null;
    }

    public override IToolInvocation<ReadManyFilesToolParams, ToolExecutionResult> Build(
        ReadManyFilesToolParams parameters)
    {
        return new ReadManyFilesToolInvocation(parameters, _targetDirectory, _logger);
    }
}

/// <summary>
/// Invocation for the ReadManyFiles tool.
/// </summary>
public class ReadManyFilesToolInvocation : BaseToolInvocation<ReadManyFilesToolParams, ToolExecutionResult>
{
    private readonly string _targetDirectory;
    private readonly ILogger _logger;

    public ReadManyFilesToolInvocation(
        ReadManyFilesToolParams parameters,
        string targetDirectory,
        ILogger logger) : base(parameters)
    {
        _targetDirectory = targetDirectory;
        _logger = logger;
        ToolName = ReadManyFilesTool.ToolName;
        ToolDisplayName = ReadManyFilesTool.DisplayName;
    }

    protected override ToolKind Kind => ToolKind.Read;

    public override string GetDescription()
    {
        var patterns = string.Join(", ", Parameters.Include.Take(3));
        if (Parameters.Include.Count > 3)
            patterns += $", ...+{Parameters.Include.Count - 3} more";
        return $"Read files matching: {patterns}";
    }

    public override IReadOnlyList<ToolLocation> GetToolLocations() =>
        new List<ToolLocation> { new() { Path = _targetDirectory } };

    public override async Task<ToolExecutionResult> ExecuteAsync(
        CancellationToken cancellationToken,
        Action<ToolLiveOutput>? updateOutput = null)
    {
        try
        {
            if (!Directory.Exists(_targetDirectory))
                return ToolExecutionResult.Failure(
                    $"Target directory not found: {_targetDirectory}", ToolErrorType.NotFound);

            var matchedFiles = await Task.Run(
                () => DiscoverFiles(), cancellationToken);

            if (matchedFiles.Count == 0)
            {
                return new ToolExecutionResult
                {
                    LlmContent = new TextContentPart("No files matching the criteria were found."),
                    ReturnDisplay = new TextToolResultDisplay("No files found."),
                };
            }

            var sortedFiles = matchedFiles.OrderBy(f => f).ToList();
            var contentBuilder = new StringBuilder();
            var skippedFiles = new List<(string path, string reason)>();
            var processedPaths = new List<string>();

            foreach (var filePath in sortedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var relativePath = Path.GetRelativePath(_targetDirectory, filePath)
                    .Replace('\\', '/');

                var skipReason = await TryAppendFileAsync(
                    filePath, relativePath, contentBuilder, cancellationToken);

                if (skipReason != null)
                    skippedFiles.Add((relativePath, skipReason));
                else
                    processedPaths.Add(relativePath);
            }

            if (processedPaths.Count == 0)
            {
                return new ToolExecutionResult
                {
                    LlmContent = new TextContentPart("All matched files were skipped (binary or too large)."),
                    ReturnDisplay = BuildDisplayMessage(processedPaths, skippedFiles),
                };
            }

            var llmContent = contentBuilder.ToString();

            _logger.Information(
                "ReadManyFiles: read {Count} files ({Chars} chars)",
                processedPaths.Count, llmContent.Length);

            return new ToolExecutionResult
            {
                LlmContent = new TextContentPart(llmContent),
                ReturnDisplay = BuildDisplayMessage(processedPaths, skippedFiles),
                Data = new Dictionary<string, object>
                {
                    ["file_count"] = processedPaths.Count,
                    ["skipped_count"] = skippedFiles.Count,
                    ["char_count"] = llmContent.Length,
                }
            };
        }
        catch (OperationCanceledException)
        {
            return ToolExecutionResult.Failure("Operation cancelled.", ToolErrorType.Cancellation);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Unexpected error in ReadManyFilesTool");
            return ToolExecutionResult.Failure($"Unexpected error: {ex.Message}", ToolErrorType.Unknown);
        }
    }

    // ─── File Discovery ───────────────────────────────────────────────────────

    private List<string> DiscoverFiles()
    {
        var includeRegexes = Parameters.Include
            .Select(p => GlobToRegex(p))
            .ToList();

        var excludePatterns = Parameters.UseDefaultExcludes
            ? ReadManyFilesTool.DefaultExcludes.Concat(Parameters.Exclude ?? Array.Empty<string>())
            : (IEnumerable<string>)(Parameters.Exclude ?? Array.Empty<string>());

        var excludeRegexes = excludePatterns
            .Select(p => GlobToRegex(p))
            .ToList();

        return Directory
            .GetFiles(_targetDirectory, "*", SearchOption.AllDirectories)
            .Where(filePath =>
            {
                var rel = Path.GetRelativePath(_targetDirectory, filePath)
                    .Replace('\\', '/');

                // Must match at least one include pattern
                if (!includeRegexes.Any(r => r.IsMatch(rel)))
                    return false;

                // Must not match any exclude pattern
                if (excludeRegexes.Any(r => r.IsMatch(rel)))
                    return false;

                return true;
            })
            .ToList();
    }

    /// <summary>
    /// Converts a glob pattern to a Regex. Supports *, **, and ? wildcards.
    /// </summary>
    private static Regex GlobToRegex(string glob)
    {
        // Normalize separators
        glob = glob.Replace('\\', '/');

        var sb = new StringBuilder("^");
        int i = 0;
        while (i < glob.Length)
        {
            if (glob[i] == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                // ** matches any path segment(s), including slashes
                sb.Append(".*");
                i += 2;
                // Skip optional trailing slash after **
                if (i < glob.Length && glob[i] == '/') i++;
            }
            else if (glob[i] == '*')
            {
                // * matches anything except /
                sb.Append("[^/]*");
                i++;
            }
            else if (glob[i] == '?')
            {
                sb.Append("[^/]");
                i++;
            }
            else
            {
                sb.Append(Regex.Escape(glob[i].ToString()));
                i++;
            }
        }
        sb.Append('$');

        return new Regex(sb.ToString(), RegexOptions.IgnoreCase);
    }

    // ─── File Reading ─────────────────────────────────────────────────────────

    /// <summary>
    /// Appends a file's content to the builder. Returns null on success, or a skip reason.
    /// </summary>
    private async Task<string?> TryAppendFileAsync(
        string filePath,
        string relativePath,
        StringBuilder contentBuilder,
        CancellationToken cancellationToken)
    {
        var ext = Path.GetExtension(filePath);
        if (ReadManyFilesTool.BinaryExtensions.Contains(ext))
            return $"binary/asset file ({ext})";

        FileInfo fi;
        try { fi = new FileInfo(filePath); }
        catch { return "cannot access file info"; }

        if (fi.Length > ReadManyFilesTool.MaxFileSizeBytes)
            return $"file too large ({fi.Length / 1024:N0} KB, max 5 MB)";

        string content;
        try
        {
            content = await File.ReadAllTextAsync(filePath, cancellationToken);
        }
        catch (Exception ex)
        {
            return $"read error: {ex.Message}";
        }

        contentBuilder.Append($"--- {relativePath} ---\n\n");
        contentBuilder.Append(content);
        contentBuilder.Append("\n\n");

        return null;
    }

    // ─── Display ─────────────────────────────────────────────────────────────

    private TextToolResultDisplay BuildDisplayMessage(
        List<string> processed,
        List<(string path, string reason)> skipped)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"### ReadManyFiles Result (Target: `{_targetDirectory}`)");
        sb.AppendLine();

        if (processed.Count > 0)
        {
            sb.AppendLine($"Successfully read **{processed.Count} file(s)**.");
            sb.AppendLine();
            sb.AppendLine("**Processed Files:**");
            foreach (var p in processed.Take(10))
                sb.AppendLine($"- `{p}`");
            if (processed.Count > 10)
                sb.AppendLine($"- ...and {processed.Count - 10} more.");
        }

        if (skipped.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"**Skipped {skipped.Count} item(s):**");
            foreach (var (path, reason) in skipped.Take(5))
                sb.AppendLine($"- `{path}` ({reason})");
            if (skipped.Count > 5)
                sb.AppendLine($"- ...and {skipped.Count - 5} more.");
        }

        if (processed.Count == 0 && skipped.Count == 0)
            sb.AppendLine("No files found matching the criteria.");

        return new TextToolResultDisplay(sb.ToString().TrimEnd());
    }
}
