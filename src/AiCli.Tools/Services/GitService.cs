using AiCli.Core.Logging;
using LibGit2Sharp;
using System.Diagnostics;

namespace AiCli.Core.Services;

/// <summary>
/// Git status information.
/// </summary>
public record GitStatus
{
    public required string Branch { get; init; }
    public required string Hash { get; init; }
    public required string ShortHash { get; init; }
    public required DateTime CommitTime { get; init; }
    public required string Author { get; init; }
    public required string Message { get; init; }
    public bool IsDirty { get; init; }
    public List<StatusEntry> ChangedFiles { get; init; } = new();
}

/// <summary>
/// Git diff information.
/// </summary>
public record GitDiff
{
    public required string FilePath { get; init; }
    public required string Status { get; init; }
    public required string Diff { get; init; }
    public int LinesAdded { get; init; }
    public int LinesDeleted { get; init; }
}

/// <summary>
/// Service for Git operations.
/// </summary>
public class GitService : IDisposable
{
    private readonly ILogger _logger;
    private readonly Dictionary<string, Repository> _repositories = new();
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>
    /// Event raised when a Git operation is performed.
    /// </summary>
    public event EventHandler<GitOperationEventArgs>? OperationPerformed;

    public GitService()
    {
        _logger = LoggerHelper.ForContext<GitService>();
    }

    /// <summary>
    /// Gets the repository for a path.
    /// </summary>
    private Repository GetRepository(string path)
    {
        lock (_lock)
        {
            if (_repositories.TryGetValue(path, out var repo))
            {
                return repo;
            }

            // Find git repository root
            var repoPath = Repository.Discover(path);
            if (string.IsNullOrEmpty(repoPath))
            {
                throw new InvalidOperationException($"Not a Git repository: {path}");
            }

            var repository = new Repository(repoPath);
            _repositories[path] = repository;

            return repository;
        }
    }

    /// <summary>
    /// Checks if a path is in a Git repository.
    /// </summary>
    public bool IsGitRepository(string path)
    {
        try
        {
            var repoPath = Repository.Discover(path);
            return !string.IsNullOrEmpty(repoPath);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Gets the current Git status.
    /// </summary>
    public async Task<GitStatus> GetStatusAsync(string path)
    {
        return await Task.Run(() =>
        {
            var stopwatch = Stopwatch.StartNew();
            var repo = GetRepository(path);
            var status = repo.RetrieveStatus();

            var head = repo.Head;
            var commit = head.Tip;
            var author = commit.Author;
            var message = commit.Message;

            stopwatch.Stop();

            _logger.Verbose("Got Git status in {Ms}ms", stopwatch.ElapsedMilliseconds);

            var gitStatus = new GitStatus
            {
                Branch = head.FriendlyName,
                Hash = commit.Sha,
                ShortHash = commit.Sha[..7],
                CommitTime = author.When.UtcDateTime,
                Author = author.Name,
                Message = message.Split('\n')[0],
                IsDirty = status.IsDirty,
                ChangedFiles = status.Select(entry => entry).ToList()
            };

            OperationPerformed?.Invoke(this,
                new GitOperationEventArgs("status", path, gitStatus));

            return gitStatus;
        });
    }

    /// <summary>
    /// Gets the diff for a file.
    /// </summary>
    public async Task<GitDiff?> GetDiffAsync(string path, string filePath)
    {
        return await Task.Run(() =>
        {
            var stopwatch = Stopwatch.StartNew();
            var repo = GetRepository(path);
            var status = repo.RetrieveStatus();

            var entry = status.FirstOrDefault(e =>
                e.FilePath == filePath ||
                e.FilePath.EndsWith(filePath));

            if (entry == null)
            {
                return null;
            }

            Patch patch;
            var content = string.Empty;
            var linesAdded = 0;
            var linesDeleted = 0;

            if (entry.State == FileStatus.NewInWorkdir)
            {
                // New file - show full content
                if (File.Exists(filePath))
                {
                    content = File.ReadAllText(filePath);
                }
            }
            else
            {
                try
                {
                    // Get diff against HEAD
                    var tree = repo.Head.Tip.Tree;
                    var treeEntry = tree[filePath];

                    if (treeEntry != null)
                    {
                        var oldContent = treeEntry.Target is Blob blob
                            ? blob.GetContentText()
                            : string.Empty;

                        var newContent = File.Exists(filePath)
                            ? File.ReadAllText(filePath)
                            : string.Empty;

                        // Simple line-based diff
                        var oldLines = oldContent.Split('\n');
                        var newLines = newContent.Split('\n');

                        var diffLines = new List<string>();
                        var oldIndex = 0;
                        var newIndex = 0;

                        while (oldIndex < oldLines.Length || newIndex < newLines.Length)
                        {
                            if (oldIndex < oldLines.Length && newIndex < newLines.Length &&
                                oldLines[oldIndex] == newLines[newIndex])
                            {
                                oldIndex++;
                                newIndex++;
                            }
                            else if (newIndex < newLines.Length)
                            {
                                diffLines.Add($"+ {newLines[newIndex]}");
                                linesAdded++;
                                newIndex++;
                            }
                            else if (oldIndex < oldLines.Length)
                            {
                                diffLines.Add($"- {oldLines[oldIndex]}");
                                linesDeleted++;
                                oldIndex++;
                            }
                        }

                        content = string.Join('\n', diffLines);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Error getting diff for: {FilePath}", filePath);
                }
            }

            stopwatch.Stop();
            _logger.Verbose("Got Git diff in {Ms}ms", stopwatch.ElapsedMilliseconds);

            var diff = new GitDiff
            {
                FilePath = filePath,
                Status = entry.State.ToString(),
                Diff = content,
                LinesAdded = linesAdded,
                LinesDeleted = linesDeleted
            };

            OperationPerformed?.Invoke(this,
                new GitOperationEventArgs("diff", path, diff));

            return diff;
        });
    }

    /// <summary>
    /// Creates a new Git commit.
    /// </summary>
    public async Task<string> CommitAsync(
        string path,
        string message,
        List<string>? files = null)
    {
        return await Task.Run(() =>
        {
            var stopwatch = Stopwatch.StartNew();
            var repo = GetRepository(path);
            var status = repo.RetrieveStatus();

            // Stage files
            var signature = repo.Config.BuildSignature(DateTimeOffset.Now);
            var options = new CommitOptions
            {
                AllowEmptyCommit = false
            };

            if (files != null && files.Count > 0)
            {
                foreach (var file in files)
                {
                    LibGit2Sharp.Commands.Stage(repo, file);
                }
            }
            else
            {
                // Stage all changes
                LibGit2Sharp.Commands.Stage(repo, "*");
            }

            // Create commit
            var commit = repo.Commit(message, signature, signature, options);
            stopwatch.Stop();

            _logger.Information(
                "Created commit: {Hash}, Message: {Message}",
                commit.Sha[..7],
                message);

            OperationPerformed?.Invoke(this,
                new GitOperationEventArgs("commit", path,
                    new { Hash = commit.Sha, Message = message }));

            return commit.Sha;
        });
    }

    /// <summary>
    /// Gets the Git history.
    /// </summary>
    public async Task<List<Commit>> GetHistoryAsync(
        string path,
        int maxCount = 10)
    {
        return await Task.Run(() =>
        {
            var stopwatch = Stopwatch.StartNew();
            var repo = GetRepository(path);

            var commits = repo.Commits
                .Take(maxCount)
                .ToList();

            stopwatch.Stop();
            _logger.Verbose("Got Git history in {Ms}ms", stopwatch.ElapsedMilliseconds);

            return commits;
        });
    }

    /// <summary>
    /// Gets the current branch name.
    /// </summary>
    public async Task<string> GetCurrentBranchAsync(string path)
    {
        return await Task.Run(() =>
        {
            var repo = GetRepository(path);
            return repo.Head.FriendlyName;
        });
    }

    /// <summary>
    /// Disposes the service.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var repo in _repositories.Values)
        {
            repo.Dispose();
        }
        _repositories.Clear();

        _logger.Information("GitService disposed");
    }
}

/// <summary>
/// Event arguments for Git operations.
/// </summary>
public class GitOperationEventArgs : EventArgs
{
    public string Operation { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public object Result { get; init; } = default!;

    public GitOperationEventArgs() { }

    public GitOperationEventArgs(string operation, string path, object result)
    {
        Operation = operation;
        Path = path;
        Result = result;
    }
}
