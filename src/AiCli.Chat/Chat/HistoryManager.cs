using AiCli.Core.Logging;
using AiCli.Core.Types;
using System.Collections.Concurrent;
using System.Text.Json;

namespace AiCli.Core.Chat;

/// <summary>
/// History entry with metadata.
/// </summary>
public record HistoryEntry
{
    public required string Id { get; init; }
    public required ContentMessage Message { get; init; }
    public required DateTime Timestamp { get; init; }
    public required string SessionId { get; init; }
    public Dictionary<string, object> Metadata { get; } = new();
    public int TokenCount { get; init; }
    public bool IsSystemMessage { get; init; }
}

/// <summary>
/// History query options.
/// </summary>
public record HistoryQueryOptions
{
    public string? SessionId { get; init; }
    public LlmRole? Role { get; init; }
    public DateTime? StartDate { get; init; }
    public DateTime? EndDate { get; init; }
    public int? Limit { get; init; }
    public bool IncludeSystemMessages { get; init; } = false;
    public string? SearchQuery { get; init; }
}

/// <summary>
/// History statistics.
/// </summary>
public record HistoryStatistics
{
    public int TotalMessages { get; init; }
    public int UserMessages { get; init; }
    public int AssistantMessages { get; init; }
    public int SystemMessages { get; init; }
    public int TotalTokens { get; init; }
    public int SessionCount { get; init; }
    public DateTime FirstMessageDate { get; init; }
    public DateTime LastMessageDate { get; init; }
}

/// <summary>
/// Event arguments for history events.
/// </summary>
public class HistoryEventArgs : EventArgs
{
    public required HistoryEntry Entry { get; init; }
}

/// <summary>
/// Manager for conversation history.
/// </summary>
public class HistoryManager : IDisposable
{
    private readonly ILogger _logger;
    private readonly List<HistoryEntry> _history = new();
    private readonly Dictionary<string, List<HistoryEntry>> _historyBySession = new();
    private readonly ConcurrentDictionary<string, HistoryEntry> _historyById = new();
    private readonly string _storagePath;
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>
    /// Event raised when an entry is added.
    /// </summary>
    public event EventHandler<HistoryEventArgs>? EntryAdded;

    /// <summary>
    /// Event raised when an entry is removed.
    /// </summary>
    public event EventHandler<HistoryEventArgs>? EntryRemoved;

    /// <summary>
    /// Event raised when history is cleared.
    /// </summary>
    public event EventHandler? HistoryCleared;

    /// <summary>
    /// Gets the number of history entries.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _history.Count;
            }
        }
    }

    /// <summary>
    /// Gets the total token count.
    /// </summary>
    public int TotalTokens
    {
        get
        {
            lock (_lock)
            {
                return _history.Sum(e => e.TokenCount);
            }
        }
    }

    public HistoryManager(string storagePath)
    {
        _logger = LoggerHelper.ForContext<HistoryManager>();
        _storagePath = storagePath;

        _logger.Information("HistoryManager initialized at: {Path}", storagePath);
    }

    /// <summary>
    /// Initializes the history manager.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (!File.Exists(_storagePath))
        {
            _logger.Verbose("No history file found, starting fresh");
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_storagePath);
            var entries = JsonSerializer.Deserialize<List<HistoryEntry>>(json) ?? new();

            lock (_lock)
            {
                _history.Clear();
                _history.AddRange(entries);

                foreach (var entry in entries)
                {
                    _historyById[entry.Id] = entry;

                    if (!_historyBySession.ContainsKey(entry.SessionId))
                    {
                        _historyBySession[entry.SessionId] = new List<HistoryEntry>();
                    }
                    _historyBySession[entry.SessionId].Add(entry);
                }
            }

            _logger.Information("Loaded {Count} history entries", entries.Count);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error loading history");
        }
    }

    /// <summary>
    /// Adds an entry to the history.
    /// </summary>
    public string AddEntry(ContentMessage message, string sessionId, Dictionary<string, object>? metadata = null)
    {
        var id = Guid.NewGuid().ToString();
        var entry = new HistoryEntry
        {
            Id = id,
            Message = message,
            Timestamp = DateTime.UtcNow,
            SessionId = sessionId,
            TokenCount = EstimateTokenCount(message),
            IsSystemMessage = message.Role == LlmRole.System
        };

        if (metadata != null)
        {
            foreach (var (key, value) in metadata)
            {
                entry.Metadata[key] = value;
            }
        }

        lock (_lock)
        {
            _history.Add(entry);
            _historyById[id] = entry;

            if (!_historyBySession.ContainsKey(sessionId))
            {
                _historyBySession[sessionId] = new List<HistoryEntry>();
            }
            _historyBySession[sessionId].Add(entry);
        }

        // Persist to disk asynchronously
        _ = Task.Run(() => PersistAsync());

        _logger.Verbose("Added history entry: {Id}, Role: {Role}", id, message.Role);

        EntryAdded?.Invoke(this, new HistoryEventArgs { Entry = entry });

        return id;
    }

    /// <summary>
    /// Removes an entry by ID.
    /// </summary>
    public bool RemoveEntry(string id)
    {
        HistoryEntry? entry = null;
        lock (_lock)
        {
            if (!_historyById.TryGetValue(id, out entry))
            {
                return false;
            }

            _history.Remove(entry);
            _historyById.TryRemove(id, out _);
            _historyBySession[entry.SessionId].Remove(entry);

            if (_historyBySession[entry.SessionId].Count == 0)
            {
                _historyBySession.Remove(entry.SessionId);
            }
        }

        _ = Task.Run(() => PersistAsync());

        _logger.Verbose("Removed history entry: {Id}", id);

        if (entry != null)
        {
            EntryRemoved?.Invoke(this, new HistoryEventArgs { Entry = entry });
        }

        return true;
    }

    /// <summary>
    /// Gets an entry by ID.
    /// </summary>
    public HistoryEntry? GetEntry(string id)
    {
        lock (_lock)
        {
            return _historyById.TryGetValue(id, out var entry) ? entry : null;
        }
    }

    /// <summary>
    /// Gets all entries.
    /// </summary>
    public List<HistoryEntry> GetAllEntries()
    {
        lock (_lock)
        {
            return _history.ToList();
        }
    }

    /// <summary>
    /// Gets entries for a session.
    /// </summary>
    public List<HistoryEntry> GetSessionEntries(string sessionId)
    {
        lock (_lock)
        {
            return _historyBySession.TryGetValue(sessionId, out var entries)
                ? entries.ToList()
                : new List<HistoryEntry>();
        }
    }

    /// <summary>
    /// Queries the history with options.
    /// </summary>
    public List<HistoryEntry> Query(HistoryQueryOptions options)
    {
        lock (_lock)
        {
            var results = _history.AsEnumerable();

            // Filter by session
            if (!string.IsNullOrEmpty(options.SessionId))
            {
                results = results.Where(e => e.SessionId == options.SessionId);
            }

            // Filter by role
            if (options.Role.HasValue)
            {
                results = results.Where(e => e.Message.Role == options.Role.Value);
            }

            // Filter by date range
            if (options.StartDate.HasValue)
            {
                results = results.Where(e => e.Timestamp >= options.StartDate.Value);
            }

            if (options.EndDate.HasValue)
            {
                results = results.Where(e => e.Timestamp <= options.EndDate.Value);
            }

            // Filter system messages
            if (!options.IncludeSystemMessages)
            {
                results = results.Where(e => !e.IsSystemMessage);
            }

            // Search query
            if (!string.IsNullOrEmpty(options.SearchQuery))
            {
                var query = options.SearchQuery!.ToLower();
                results = results.Where(e =>
                {
                    var content = string.Join("", e.Message.Parts.OfType<TextContentPart>()
                        .Select(p => p.Text))
                        .ToLower();
                    return content.Contains(query);
                });
            }

            // Limit results
            if (options.Limit.HasValue)
            {
                results = results.Take(options.Limit.Value);
            }

            return results.OrderBy(e => e.Timestamp).ToList();
        }
    }

    /// <summary>
    /// Clears all history.
    /// </summary>
    public void Clear()
    {
        int count;
        lock (_lock)
        {
            count = _history.Count;
            _history.Clear();
            _historyBySession.Clear();
            _historyById.Clear();
        }

        _ = Task.Run(() => PersistAsync());

        _logger.Information("Cleared {Count} history entries", count);

        HistoryCleared?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Clears history for a session.
    /// </summary>
    public void ClearSession(string sessionId)
    {
        lock (_lock)
        {
            if (!_historyBySession.TryGetValue(sessionId, out var entries))
            {
                return;
            }

            foreach (var entry in entries)
            {
                _history.Remove(entry);
                _historyById.TryRemove(entry.Id, out _);
            }

            _historyBySession.Remove(sessionId);
        }

        _ = Task.Run(() => PersistAsync());

        _logger.Information("Cleared session history: {SessionId}", sessionId);
    }

    /// <summary>
    /// Gets history statistics.
    /// </summary>
    public HistoryStatistics GetStatistics()
    {
        lock (_lock)
        {
            if (_history.Count == 0)
            {
                return new HistoryStatistics
                {
                    TotalMessages = 0,
                    UserMessages = 0,
                    AssistantMessages = 0,
                    SystemMessages = 0,
                    TotalTokens = 0,
                    SessionCount = 0,
                    FirstMessageDate = DateTime.UtcNow,
                    LastMessageDate = DateTime.UtcNow
                };
            }

            return new HistoryStatistics
            {
                TotalMessages = _history.Count,
                UserMessages = _history.Count(e => e.Message.Role == LlmRole.User),
                AssistantMessages = _history.Count(e => e.Message.Role == LlmRole.Assistant),
                SystemMessages = _history.Count(e => e.IsSystemMessage),
                TotalTokens = _history.Sum(e => e.TokenCount),
                SessionCount = _historyBySession.Count,
                FirstMessageDate = _history.Min(e => e.Timestamp),
                LastMessageDate = _history.Max(e => e.Timestamp)
            };
        }
    }

    /// <summary>
    /// Exports history to a file.
    /// </summary>
    public async Task ExportAsync(string filePath)
    {
        List<HistoryEntry> entries;

        lock (_lock)
        {
            entries = _history.ToList();
        }

        var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        await File.WriteAllTextAsync(filePath, json);

        _logger.Information("Exported {Count} history entries to: {Path}", entries.Count, filePath);
    }

    /// <summary>
    /// Imports history from a file.
    /// </summary>
    public async Task ImportAsync(string filePath)
    {
        var json = await File.ReadAllTextAsync(filePath);
        var entries = JsonSerializer.Deserialize<List<HistoryEntry>>(json) ?? new();

        lock (_lock)
        {
            _history.AddRange(entries);

            foreach (var entry in entries)
            {
                _historyById[entry.Id] = entry;

                if (!_historyBySession.ContainsKey(entry.SessionId))
                {
                    _historyBySession[entry.SessionId] = new List<HistoryEntry>();
                }
                _historyBySession[entry.SessionId].Add(entry);
            }
        }

        _logger.Information("Imported {Count} history entries from: {Path}", entries.Count, filePath);

        await PersistAsync();
    }

    /// <summary>
    /// Persists the history to disk.
    /// </summary>
    private async Task PersistAsync()
    {
        try
        {
            List<HistoryEntry> entries;

            lock (_lock)
            {
                entries = _history.ToList();
            }

            var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            await File.WriteAllTextAsync(_storagePath, json);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error persisting history");
        }
    }

    /// <summary>
    /// Estimates token count for a message.
    /// </summary>
    private int EstimateTokenCount(ContentMessage message)
    {
        var text = string.Join("", message.Parts.OfType<TextContentPart>()
            .Select(p => p.Text));

        // Rough estimate: ~4 characters per token
        return text.Length / 4;
    }

    /// <summary>
    /// Disposes the manager.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Clear();

        _logger.Information("HistoryManager disposed");
    }
}
