using AiCli.Core.Logging;
using AiCli.Core.Types;

namespace AiCli.Core.Services;

/// <summary>
/// Context item types.
/// </summary>
public enum ContextItemType
{
    File,
    Symbol,
    Definition,
    Documentation,
    CodeBlock,
    Configuration,
    Custom
}

/// <summary>
/// Context item with relevance score.
/// </summary>
public record ContextItem
{
    public required string Id { get; init; }
    public required ContextItemType Type { get; init; }
    public required string Content { get; init; }
    public required string Source { get; init; }
    public required string Location { get; init; }
    public double RelevanceScore { get; init; }
    public DateTime AddedAt { get; init; } = DateTime.UtcNow;
    public Dictionary<string, object> Metadata { get; } = new();
    public int TokenCount { get; init; }
}

/// <summary>
/// Context search options.
/// </summary>
public record ContextSearchOptions
{
    public List<string> Keywords { get; init; } = new();
    public List<ContextItemType> Types { get; init; } = new();
    public double MinRelevanceScore { get; init; } = 0.0;
    public int MaxResults { get; init; } = 10;
    public string? IncludeLocation { get; init; }
    public string? ExcludeLocation { get; init; }
}

/// <summary>
/// Service for managing context.
/// </summary>
public class ContextManager
{
    private readonly ILogger _logger;
    private readonly List<ContextItem> _contextItems = new();
    private readonly Dictionary<string, List<ContextItem>> _indexBySource = new();
    private readonly Dictionary<string, List<ContextItem>> _indexByType = new();
    private readonly object _lock = new();

    /// <summary>
    /// Event raised when context is added.
    /// </summary>
    public event EventHandler<ContextAddedEventArgs>? ContextAdded;

    /// <summary>
    /// Event raised when context is removed.
    /// </summary>
    public event EventHandler<ContextRemovedEventArgs>? ContextRemoved;

    /// <summary>
    /// Gets the number of context items.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _contextItems.Count;
            }
        }
    }

    /// <summary>
    /// Gets the total token count.
    /// </summary>
    public int TotalTokenCount
    {
        get
        {
            lock (_lock)
            {
                return _contextItems.Sum(i => i.TokenCount);
            }
        }
    }

    public ContextManager()
    {
        _logger = LoggerHelper.ForContext<ContextManager>();
    }

    /// <summary>
    /// Adds context to the manager.
    /// </summary>
    public string AddContext(
        ContextItemType type,
        string content,
        string source,
        string location,
        Dictionary<string, object>? metadata = null)
    {
        var id = Guid.NewGuid().ToString();
        var tokenCount = EstimateTokenCount(content);

        var item = new ContextItem
        {
            Id = id,
            Type = type,
            Content = content,
            Source = source,
            Location = location,
            RelevanceScore = 0.0,
            TokenCount = tokenCount
        };

        if (metadata != null)
        {
            foreach (var (key, value) in metadata)
            {
                item.Metadata[key] = value;
            }
        }

        lock (_lock)
        {
            _contextItems.Add(item);

            // Index by source
            if (!_indexBySource.ContainsKey(source))
            {
                _indexBySource[source] = new List<ContextItem>();
            }
            _indexBySource[source].Add(item);

            // Index by type
            if (!_indexByType.ContainsKey(type.ToString()))
            {
                _indexByType[type.ToString()] = new List<ContextItem>();
            }
            _indexByType[type.ToString()].Add(item);
        }

        _logger.Verbose("Added context: {Type}, ID: {Id}, Tokens: {TokenCount}",
            type, id, tokenCount);

        ContextAdded?.Invoke(this, new ContextAddedEventArgs(item));

        return id;
    }

    /// <summary>
    /// Removes context by ID.
    /// </summary>
    public bool RemoveContext(string id)
    {
        lock (_lock)
        {
            var item = _contextItems.FirstOrDefault(i => i.Id == id);
            if (item == null) return false;

            _contextItems.Remove(item);
            _indexBySource[item.Source].Remove(item);
            _indexByType[item.Type.ToString()].Remove(item);

            _logger.Verbose("Removed context: {Id}", id);

            ContextRemoved?.Invoke(this, new ContextRemovedEventArgs(item));

            return true;
        }
    }

    /// <summary>
    /// Clears all context.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            var count = _contextItems.Count;
            _contextItems.Clear();
            _indexBySource.Clear();
            _indexByType.Clear();
        }

        _logger.Information("Cleared {Count} context items", _contextItems.Count);
    }

    /// <summary>
    /// Searches for relevant context.
    /// </summary>
    public List<ContextItem> SearchContext(ContextSearchOptions options)
    {
        lock (_lock)
        {
            var results = _contextItems.AsEnumerable();

            // Filter by type
            if (options.Types.Count > 0)
            {
                results = results.Where(i => options.Types.Contains(i.Type));
            }

            // Filter by location
            if (!string.IsNullOrEmpty(options.IncludeLocation))
            {
                results = results.Where(i =>
                    i.Location.StartsWith(options.IncludeLocation!));
            }

            if (!string.IsNullOrEmpty(options.ExcludeLocation))
            {
                results = results.Where(i =>
                    !i.Location.StartsWith(options.ExcludeLocation!));
            }

            // Score and filter by keywords
            if (options.Keywords.Count > 0)
            {
                results = results.Select(item =>
                {
                    var score = CalculateRelevanceScore(item, options.Keywords);
                    return new { Item = item, Score = score };
                })
                .Where(x => x.Score >= options.MinRelevanceScore)
                .OrderByDescending(x => x.Score)
                .Select(x => x.Item);
            }

            return results.Take(options.MaxResults).ToList();
        }
    }

    /// <summary>
    /// Gets context by source.
    /// </summary>
    public List<ContextItem> GetContextBySource(string source)
    {
        lock (_lock)
        {
            return _indexBySource.TryGetValue(source, out var items)
                ? items.ToList()
                : new List<ContextItem>();
        }
    }

    /// <summary>
    /// Gets context by type.
    /// </summary>
    public List<ContextItem> GetContextByType(ContextItemType type)
    {
        lock (_lock)
        {
            return _indexByType.TryGetValue(type.ToString(), out var items)
                ? items.ToList()
                : new List<ContextItem>();
        }
    }

    /// <summary>
    /// Gets all context.
    /// </summary>
    public List<ContextItem> GetAllContext()
    {
        lock (_lock)
        {
            return _contextItems.ToList();
        }
    }

    /// <summary>
    /// Calculates relevance score for a context item.
    /// </summary>
    private double CalculateRelevanceScore(ContextItem item, List<string> keywords)
    {
        var score = 0.0;
        var contentLower = item.Content.ToLower();
        var locationLower = item.Location.ToLower();

        foreach (var keyword in keywords)
        {
            var keywordLower = keyword.ToLower();

            // Exact match in location
            if (locationLower.Contains(keywordLower))
            {
                score += 2.0;
            }

            // Partial match in content
            if (contentLower.Contains(keywordLower))
            {
                score += 1.0;
            }

            // Word boundary match
            var wordPattern = $@"\b{System.Text.RegularExpressions.Regex.Escape(keywordLower)}\b";
            if (System.Text.RegularExpressions.Regex.IsMatch(
                contentLower, wordPattern))
            {
                score += 0.5;
            }
        }

        return score;
    }

    /// <summary>
    /// Estimates token count for text.
    /// </summary>
    private int EstimateTokenCount(string text)
    {
        // Rough estimate: ~4 characters per token
        return text.Length / 4;
    }

    /// <summary>
    /// Creates a context summary.
    /// </summary>
    public string CreateSummary(List<ContextItem> items)
    {
        var summary = new List<string>();
        summary.Add($"Context Summary ({items.Count} items):");
        summary.Add("");

        var groupedByType = items.GroupBy(i => i.Type);
        foreach (var group in groupedByType)
        {
            summary.Add($"## {group.Key}");
            foreach (var item in group.Take(5))
            {
                summary.Add($"- {item.Location}: {item.Content[..Math.Min(100, item.Content.Length)]}...");
            }
            summary.Add("");
        }

        return string.Join("\n", summary);
    }
}

/// <summary>
/// Event arguments for context addition.
/// </summary>
public class ContextAddedEventArgs : EventArgs
{
    public ContextItem Item { get; init; } = default!;

    public ContextAddedEventArgs() { }

    public ContextAddedEventArgs(ContextItem item)
    {
        Item = item;
    }
}

/// <summary>
/// Event arguments for context removal.
/// </summary>
public class ContextRemovedEventArgs : EventArgs
{
    public ContextItem Item { get; init; } = default!;

    public ContextRemovedEventArgs() { }

    public ContextRemovedEventArgs(ContextItem item)
    {
        Item = item;
    }
}
