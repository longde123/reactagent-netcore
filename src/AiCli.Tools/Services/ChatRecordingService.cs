using AiCli.Core.Logging;
using AiCli.Core.Types;
using System.Text.Json;

namespace AiCli.Core.Services;

/// <summary>
/// Recorded message information.
/// </summary>
public record RecordedMessage
{
    public required string Id { get; init; }
    public required LlmRole Role { get; init; }
    public required List<ContentPart> Parts { get; init; }
    public required DateTime Timestamp { get; init; }
    public Dictionary<string, object> Metadata { get; } = new();
    public int TokenCount { get; init; }
}

/// <summary>
/// Chat recording session.
/// </summary>
public record ChatRecording
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required DateTime StartedAt { get; init; }
    public DateTime? EndedAt { get; init; }
    public List<RecordedMessage> Messages { get; init; } = new();
    public Dictionary<string, object> Metadata { get; } = new();
    public string Model { get; init; } = "unknown";
    public int TotalTokens => Messages.Sum(m => m.TokenCount);
}

/// <summary>
/// Service for recording chat sessions.
/// </summary>
public class ChatRecordingService : IDisposable
{
    private readonly ILogger _logger;
    private readonly Dictionary<string, ChatRecording> _recordings = new();
    private readonly Dictionary<string, string> _currentRecordingBySession = new();
    private readonly string _storagePath;
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>
    /// Event raised when a message is recorded.
    /// </summary>
    public event EventHandler<MessageRecordedEventArgs>? MessageRecorded;

    /// <summary>
    /// Event raised when a recording starts.
    /// </summary>
    public event EventHandler<RecordingStartedEventArgs>? RecordingStarted;

    /// <summary>
    /// Event raised when a recording ends.
    /// </summary>
    public event EventHandler<RecordingEndedEventArgs>? RecordingEnded;

    public ChatRecordingService(string storagePath)
    {
        _logger = LoggerHelper.ForContext<ChatRecordingService>();
        _storagePath = storagePath;

        // Ensure storage directory exists
        if (!Directory.Exists(_storagePath))
        {
            Directory.CreateDirectory(_storagePath);
        }

        _logger.Information("ChatRecordingService initialized at: {Path}", _storagePath);
    }

    /// <summary>
    /// Starts a new recording session.
    /// </summary>
    public string StartRecording(string sessionId, string title, string model = "unknown")
    {
        var recordingId = Guid.NewGuid().ToString();
        var recording = new ChatRecording
        {
            Id = recordingId,
            Title = title,
            StartedAt = DateTime.UtcNow,
            Model = model
        };

        lock (_lock)
        {
            _recordings[recordingId] = recording;
            _currentRecordingBySession[sessionId] = recordingId;
        }

        _logger.Information("Started recording: {Id}, Session: {Session}, Title: {Title}",
            recordingId, sessionId, title);

        RecordingStarted?.Invoke(this, new RecordingStartedEventArgs(recording));

        return recordingId;
    }

    /// <summary>
    /// Ends the current recording session.
    /// </summary>
    public void EndRecording(string sessionId)
    {
        lock (_lock)
        {
            if (!_currentRecordingBySession.TryGetValue(sessionId, out var recordingId))
            {
                _logger.Warning("No active recording for session: {Session}", sessionId);
                return;
            }

            if (_recordings.TryGetValue(recordingId, out var recording))
            {
                recording = recording with { EndedAt = DateTime.UtcNow };
                _recordings[recordingId] = recording;

                // Save to disk
                _ = Task.Run(() => SaveRecordingAsync(recording));

                _logger.Information("Ended recording: {Id}, Messages: {Count}, Tokens: {Tokens}",
                    recordingId, recording.Messages.Count, recording.TotalTokens);

                RecordingEnded?.Invoke(this, new RecordingEndedEventArgs(recording));
            }

            _currentRecordingBySession.Remove(sessionId);
        }
    }

    /// <summary>
    /// Records a message.
    /// </summary>
    public string RecordMessage(
        string sessionId,
        LlmRole role,
        List<ContentPart> parts,
        Dictionary<string, object>? metadata = null)
    {
        lock (_lock)
        {
            if (!_currentRecordingBySession.TryGetValue(sessionId, out var recordingId))
            {
                _logger.Warning("No active recording for session: {Session}", sessionId);
                return string.Empty;
            }

            if (!_recordings.TryGetValue(recordingId, out var recording))
            {
                return string.Empty;
            }

            var messageId = Guid.NewGuid().ToString();
            var tokenCount = EstimateTokenCount(parts);

            var message = new RecordedMessage
            {
                Id = messageId,
                Role = role,
                Parts = parts,
                Timestamp = DateTime.UtcNow,
                TokenCount = tokenCount
            };

            if (metadata != null)
            {
                foreach (var (key, value) in metadata)
                {
                    message.Metadata[key] = value;
                }
            }

            recording.Messages.Add(message);

            _logger.Verbose("Recorded message: {MessageId}, Role: {Role}, Tokens: {Tokens}",
                messageId, role, tokenCount);

            MessageRecorded?.Invoke(this, new MessageRecordedEventArgs(message));

            return messageId;
        }
    }

    /// <summary>
    /// Gets a recording by ID.
    /// </summary>
    public ChatRecording? GetRecording(string recordingId)
    {
        lock (_lock)
        {
            return _recordings.TryGetValue(recordingId, out var recording)
                ? recording
                : null;
        }
    }

    /// <summary>
    /// Gets the current recording for a session.
    /// </summary>
    public ChatRecording? GetCurrentRecording(string sessionId)
    {
        lock (_lock)
        {
            if (!_currentRecordingBySession.TryGetValue(sessionId, out var recordingId))
            {
                return null;
            }

            return _recordings.TryGetValue(recordingId, out var recording)
                ? recording
                : null;
        }
    }

    /// <summary>
    /// Gets all recordings.
    /// </summary>
    public List<ChatRecording> GetAllRecordings()
    {
        lock (_lock)
        {
            return _recordings.Values.ToList();
        }
    }

    /// <summary>
    /// Searches recordings.
    /// </summary>
    public List<ChatRecording> SearchRecordings(string query)
    {
        lock (_lock)
        {
            var queryLower = query.ToLower();
            return _recordings.Values
                .Where(r =>
                    r.Title.ToLower().Contains(queryLower) ||
                    r.Messages.Any(m =>
                        string.Join("", m.Parts).ToLower().Contains(queryLower)))
                .OrderByDescending(r => r.StartedAt)
                .ToList();
        }
    }

    /// <summary>
    /// Deletes a recording.
    /// </summary>
    public bool DeleteRecording(string recordingId)
    {
        lock (_lock)
        {
            if (!_recordings.TryGetValue(recordingId, out var recording))
            {
                return false;
            }

            _recordings.Remove(recordingId);

            // Delete from disk
            var filePath = GetRecordingFilePath(recordingId);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            _logger.Information("Deleted recording: {Id}", recordingId);

            return true;
        }
    }

    /// <summary>
    /// Saves a recording to disk.
    /// </summary>
    private async Task SaveRecordingAsync(ChatRecording recording)
    {
        try
        {
            var filePath = GetRecordingFilePath(recording.Id);
            var directory = Path.GetDirectoryName(filePath);

            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(recording, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            await File.WriteAllTextAsync(filePath, json);

            _logger.Verbose("Saved recording to: {Path}", filePath);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error saving recording: {Id}", recording.Id);
        }
    }

    /// <summary>
    /// Loads a recording from disk.
    /// </summary>
    public async Task<ChatRecording?> LoadRecordingAsync(string recordingId)
    {
        try
        {
            var filePath = GetRecordingFilePath(recordingId);
            if (!File.Exists(filePath))
            {
                return null;
            }

            var json = await File.ReadAllTextAsync(filePath);
            var recording = JsonSerializer.Deserialize<ChatRecording>(json);

            if (recording != null)
            {
                lock (_lock)
                {
                    _recordings[recordingId] = recording;
                }
            }

            return recording;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error loading recording: {Id}", recordingId);
            return null;
        }
    }

    /// <summary>
    /// Gets the file path for a recording.
    /// </summary>
    private string GetRecordingFilePath(string recordingId)
    {
        return Path.Combine(_storagePath, $"{recordingId}.json");
    }

    /// <summary>
    /// Estimates token count for content parts.
    /// </summary>
    private int EstimateTokenCount(List<ContentPart> parts)
    {
        var text = string.Join("", parts.Select(p =>
            p switch
            {
                TextContentPart tcp => tcp.Text,
                _ => string.Empty
            }));

        // Rough estimate: ~4 characters per token
        return text.Length / 4;
    }

    /// <summary>
    /// Exports recordings to a directory.
    /// </summary>
    public async Task ExportAsync(string exportPath)
    {
        lock (_lock)
        {
            if (!Directory.Exists(exportPath))
            {
                Directory.CreateDirectory(exportPath);
            }
        }

        var tasks = _recordings.Values.Select(recording =>
            File.WriteAllTextAsync(
                Path.Combine(exportPath, $"{recording.Id}.json"),
                JsonSerializer.Serialize(recording, new JsonSerializerOptions
                {
                    WriteIndented = true
                })));

        await Task.WhenAll(tasks);

        _logger.Information("Exported {Count} recordings to: {Path}",
            _recordings.Count, exportPath);
    }

    /// <summary>
    /// Disposes the service.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // End all active recordings
        var sessions = _currentRecordingBySession.Keys.ToList();
        foreach (var sessionId in sessions)
        {
            EndRecording(sessionId);
        }

        _logger.Information("ChatRecordingService disposed");
    }
}

/// <summary>
/// Event arguments for message recording.
/// </summary>
public class MessageRecordedEventArgs : EventArgs
{
    public RecordedMessage Message { get; init; } = default!;

    public MessageRecordedEventArgs() { }

    public MessageRecordedEventArgs(RecordedMessage message)
    {
        Message = message;
    }
}

/// <summary>
/// Event arguments for recording started.
/// </summary>
public class RecordingStartedEventArgs : EventArgs
{
    public ChatRecording Recording { get; init; } = default!;

    public RecordingStartedEventArgs() { }

    public RecordingStartedEventArgs(ChatRecording recording)
    {
        Recording = recording;
    }
}

/// <summary>
/// Event arguments for recording ended.
/// </summary>
public class RecordingEndedEventArgs : EventArgs
{
    public ChatRecording Recording { get; init; } = default!;

    public RecordingEndedEventArgs() { }

    public RecordingEndedEventArgs(ChatRecording recording)
    {
        Recording = recording;
    }
}
