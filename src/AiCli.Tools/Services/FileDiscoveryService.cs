using AiCli.Core.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace AiCli.Core.Services;

/// <summary>
/// File discovery options.
/// </summary>
public record FileDiscoveryOptions
{
    public List<string> IncludePatterns { get; init; } = new();
    public List<string> ExcludePatterns { get; init; } = new();
    public List<string> Extensions { get; init; } = new();
    public long MaxFileSizeBytes { get; init; } = 10 * 1024 * 1024; // 10MB
    public bool FollowSymlinks { get; init; } = false;
    public int MaxDepth { get; init; } = -1; // Unlimited
}

/// <summary>
/// Discovered file information.
/// </summary>
public record DiscoveredFile
{
    public required string Path { get; init; }
    public required string RelativePath { get; init; }
    public required long Size { get; init; }
    public required DateTime LastModified { get; init; }
    public required string Extension { get; init; }
    public bool IsBinary { get; init; }
    public int LineCount { get; init; }
    public Dictionary<string, string> Metadata { get; } = new();
}

/// <summary>
/// Service for discovering and filtering files.
/// </summary>
public class FileDiscoveryService
{
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, List<DiscoveredFile>> _cache = new();
    private readonly object _lock = new();
    private readonly Dictionary<string, Func<string, bool>> _extensionCheckers = new();

    /// <summary>
    /// Event raised when a file is discovered.
    /// </summary>
    public event EventHandler<FileDiscoveredEventArgs>? FileDiscovered;

    /// <summary>
    /// Event raised when a directory is scanned.
    /// </summary>
    public event EventHandler<DirectoryScannedEventArgs>? DirectoryScanned;

    public FileDiscoveryService()
    {
        _logger = LoggerHelper.ForContext<FileDiscoveryService>();

        // Initialize extension checkers
        InitializeExtensionCheckers();
    }

    /// <summary>
    /// Discovers files in a directory.
    /// </summary>
    public async Task<List<DiscoveredFile>> DiscoverFilesAsync(
        string basePath,
        FileDiscoveryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new FileDiscoveryOptions();
        var stopwatch = Stopwatch.StartNew();
        var results = new List<DiscoveredFile>();

        _logger.Information("Discovering files in: {Path}, Options: {Options}",
            basePath, options);

        try
        {
            var directoryInfo = new DirectoryInfo(basePath);
            if (!directoryInfo.Exists)
            {
                throw new DirectoryNotFoundException($"Directory not found: {basePath}");
            }

            await Task.Run(() =>
            {
                DiscoverFilesRecursive(
                    directoryInfo,
                    string.Empty,
                    options,
                    results,
                    0,
                    cancellationToken);
            }, cancellationToken);

            stopwatch.Stop();

            _logger.Information(
                "File discovery completed: {Count} files in {Ms}ms",
                results.Count,
                stopwatch.ElapsedMilliseconds);

            return results;
        }
        catch (OperationCanceledException)
        {
            _logger.Information("File discovery cancelled");
            return results;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error discovering files in: {Path}", basePath);
            throw;
        }
    }

    /// <summary>
    /// Discovers files recursively.
    /// </summary>
    private void DiscoverFilesRecursive(
        DirectoryInfo directory,
        string relativePath,
        FileDiscoveryOptions options,
        List<DiscoveredFile> results,
        int depth,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Check depth limit
        if (options.MaxDepth >= 0 && depth > options.MaxDepth)
        {
            return;
        }

        // Raise directory scanned event
        DirectoryScanned?.Invoke(this,
            new DirectoryScannedEventArgs(directory.FullName, relativePath));

        try
        {
            var files = directory.GetFiles();
            var directories = directory.GetDirectories();

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (ShouldIncludeFile(file, relativePath, options))
                {
                    var discoveredFile = CreateDiscoveredFile(
                        file,
                        relativePath,
                        directory.FullName);

                    results.Add(discoveredFile);
                    FileDiscovered?.Invoke(this,
                        new FileDiscoveredEventArgs(discoveredFile));
                }
            }

            foreach (var subDir in directories)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var subRelativePath = string.IsNullOrEmpty(relativePath)
                    ? subDir.Name
                    : Path.Combine(relativePath, subDir.Name);

                if (!ShouldExcludeDirectory(subDir, subRelativePath, options))
                {
                    DiscoverFilesRecursive(
                        subDir,
                        subRelativePath,
                        options,
                        results,
                        depth + 1,
                        cancellationToken);
                }
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.Warning(ex, "Permission denied accessing: {Path}", directory.FullName);
        }
        catch (IOException ex)
        {
            _logger.Warning(ex, "IO error accessing: {Path}", directory.FullName);
        }
    }

    /// <summary>
    /// Creates a discovered file record.
    /// </summary>
    private DiscoveredFile CreateDiscoveredFile(
        FileInfo file,
        string relativePath,
        string basePath)
    {
        var fileRelativePath = string.IsNullOrEmpty(relativePath)
            ? file.Name
            : Path.Combine(relativePath, file.Name);

        var lineCount = 0;
        var isBinary = false;

        if (file.Length < 1024 * 1024) // Only check files < 1MB
        {
            try
            {
                var content = File.ReadAllText(file.FullName);
                lineCount = content.Count(c => c == '\n');
                isBinary = IsBinaryFile(content);
            }
            catch (Exception ex)
            {
                _logger.Verbose(ex, "Could not read file for line counting: {Path}", file.FullName);
            }
        }

        return new DiscoveredFile
        {
            Path = file.FullName,
            RelativePath = fileRelativePath,
            Size = file.Length,
            LastModified = file.LastWriteTimeUtc,
            Extension = file.Extension,
            IsBinary = isBinary,
            LineCount = lineCount
        };
    }

    /// <summary>
    /// Checks if a file should be included.
    /// </summary>
    private bool ShouldIncludeFile(
        FileInfo file,
        string relativePath,
        FileDiscoveryOptions options)
    {
        // Check file size
        if (file.Length > options.MaxFileSizeBytes)
        {
            return false;
        }

        // Check extension filter
        if (options.Extensions.Count > 0)
        {
            var fileExt = file.Extension.ToLower();
            if (!options.Extensions.Any(ext =>
                fileExt == ext.ToLower() ||
                fileExt.EndsWith(ext.ToLower())))
            {
                return false;
            }
        }

        // Check include patterns
        if (options.IncludePatterns.Count > 0)
        {
            var fullRelativePath = string.IsNullOrEmpty(relativePath)
                ? file.Name
                : Path.Combine(relativePath, file.Name);

            if (!options.IncludePatterns.Any(pattern =>
                MatchesPattern(fullRelativePath, pattern)))
            {
                return false;
            }
        }

        // Check exclude patterns
        if (options.ExcludePatterns.Count > 0)
        {
            var fullRelativePath = string.IsNullOrEmpty(relativePath)
                ? file.Name
                : Path.Combine(relativePath, file.Name);

            if (options.ExcludePatterns.Any(pattern =>
                MatchesPattern(fullRelativePath, pattern)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Checks if a directory should be excluded.
    /// </summary>
    private bool ShouldExcludeDirectory(
        DirectoryInfo directory,
        string relativePath,
        FileDiscoveryOptions options)
    {
        // Default excluded directories
        var excludedDirs = new[] { ".git", ".svn", ".hg", "node_modules", "bin", "obj", "dist", "build" };

        if (excludedDirs.Contains(directory.Name.ToLower()))
        {
            return true;
        }

        // Check exclude patterns
        if (options.ExcludePatterns.Count > 0)
        {
            if (options.ExcludePatterns.Any(pattern =>
                MatchesPattern(relativePath, pattern)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if a path matches a glob pattern.
    /// </summary>
    private bool MatchesPattern(string path, string pattern)
    {
        // Simple pattern matching
        var regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace(@"\*", ".*")
            .Replace(@"\?", ".") + "$";

        return System.Text.RegularExpressions.Regex.IsMatch(path, regexPattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Checks if content appears to be binary.
    /// </summary>
    private bool IsBinaryFile(string content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return false;
        }

        var nullCount = content.Count(c => c == '\0');
        return nullCount > 0;
    }

    /// <summary>
    /// Initializes extension checkers.
    /// </summary>
    private void InitializeExtensionCheckers()
    {
        // Text file extensions
        var textExtensions = new[] { ".txt", ".md", ".json", ".xml", ".yaml", ".yml",
            ".csv", ".ini", ".cfg", ".conf", ".toml", ".properties" };

        foreach (var ext in textExtensions)
        {
            _extensionCheckers[ext] = _ => true;
        }
    }

    /// <summary>
    /// Gets cached discovery results.
    /// </summary>
    public List<DiscoveredFile>? GetCachedResults(string basePath)
    {
        return _cache.TryGetValue(basePath, out var results) ? results : null;
    }

    /// <summary>
    /// Clears the discovery cache.
    /// </summary>
    public void ClearCache()
    {
        _cache.Clear();
        _logger.Information("File discovery cache cleared");
    }
}

/// <summary>
/// Event arguments for file discovery.
/// </summary>
public class FileDiscoveredEventArgs : EventArgs
{
    public DiscoveredFile File { get; init; } = default!;

    public FileDiscoveredEventArgs() { }

    public FileDiscoveredEventArgs(DiscoveredFile file)
    {
        File = file;
    }
}

/// <summary>
/// Event arguments for directory scanning.
/// </summary>
public class DirectoryScannedEventArgs : EventArgs
{
    public string DirectoryPath { get; init; } = string.Empty;
    public string RelativePath { get; init; } = string.Empty;

    public DirectoryScannedEventArgs() { }

    public DirectoryScannedEventArgs(string directoryPath, string relativePath)
    {
        DirectoryPath = directoryPath;
        RelativePath = relativePath;
    }
}
