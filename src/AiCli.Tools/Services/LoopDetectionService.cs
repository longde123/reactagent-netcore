using AiCli.Core.Chat;
using AiCli.Core.Configuration;
using AiCli.Core.Logging;
using AiCli.Core.Types;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AiCli.Core.Services;

/// <summary>
/// Reasons a loop was detected.
/// </summary>
public enum LoopType
{
    ConsecutiveIdenticalToolCalls,
    ChantingIdenticalSentences,
    LlmDetectedLoop,
}

/// <summary>
/// Event raised when a loop is detected.
/// </summary>
public record LoopDetectedEventArgs
{
    public required LoopType LoopType { get; init; }
    public required string PromptId { get; init; }
    public string? ModelName { get; init; }
}

/// <summary>
/// Detects and prevents infinite loops in AI responses.
/// Monitors tool call repetitions and content sentence repetitions.
/// Ported from packages/core/src/services/loopDetectionService.ts
/// </summary>
public class LoopDetectionService
{
    private const int ToolCallLoopThreshold = 5;
    private const int ContentLoopThreshold = 10;
    private const int ContentChunkSize = 50;
    private const int MaxHistoryLength = 5000;
    private const int LlmLoopCheckHistoryCount = 20;
    private const int LlmCheckAfterTurns = 40;
    private const int DefaultLlmCheckInterval = 10;
    private const int MinLlmCheckInterval = 7;
    private const int MaxLlmCheckInterval = 15;
    private const double LlmConfidenceThreshold = 0.9;

    private static readonly ILogger Logger = LoggerHelper.ForContext<LoopDetectionService>();

    private readonly Config _config;
    private string _promptId = string.Empty;
    private string _userPrompt = string.Empty;

    // Tool call tracking
    private string? _lastToolCallKey;
    private int _toolCallRepetitionCount;

    // Content streaming tracking
    private string _streamContentHistory = string.Empty;
    private readonly Dictionary<string, List<int>> _contentStats = new();
    private int _lastContentIndex;
    private bool _loopDetected;
    private bool _inCodeBlock;

    // LLM loop check tracking
    private int _turnsInCurrentPrompt;
    private int _llmCheckInterval = DefaultLlmCheckInterval;
    private int _lastCheckTurn;

    // Session-level disable flag
    private bool _disabledForSession;

    public event EventHandler<LoopDetectedEventArgs>? LoopDetected;

    public LoopDetectionService(Config config)
    {
        _config = config;
    }

    /// <summary>
    /// Disables loop detection for the current session.
    /// </summary>
    public void DisableForSession()
    {
        _disabledForSession = true;
        Logger.Information("Loop detection disabled for session {PromptId}", _promptId);
    }

    /// <summary>
    /// Processes a stream event and checks for loop conditions.
    /// Returns true if a loop is detected.
    /// </summary>
    public bool AddAndCheck(string eventType, object? value)
    {
        if (_disabledForSession || _loopDetected)
            return _loopDetected;

        switch (eventType)
        {
            case "tool_call":
                ResetContentTracking();
                if (value is (string name, Dictionary<string, object?> args))
                    _loopDetected = CheckToolCallLoop(name, args);
                break;
            case "content":
                if (value is string content)
                    _loopDetected = CheckContentLoop(content);
                break;
        }

        return _loopDetected;
    }

    /// <summary>
    /// Checks a tool call for repetition loops.
    /// </summary>
    public bool CheckToolCall(string toolName, Dictionary<string, object?> args)
    {
        if (_disabledForSession || _loopDetected) return _loopDetected;
        ResetContentTracking();
        _loopDetected = CheckToolCallLoop(toolName, args);
        return _loopDetected;
    }

    /// <summary>
    /// Checks content chunk for repetition loops.
    /// </summary>
    public bool CheckContent(string content)
    {
        if (_disabledForSession || _loopDetected) return _loopDetected;
        _loopDetected = CheckContentLoop(content);
        return _loopDetected;
    }

    /// <summary>
    /// Signals the start of a new conversation turn.
    /// May trigger an LLM-based loop check after enough turns.
    /// </summary>
    public async Task<bool> TurnStartedAsync(
        IReadOnlyList<ContentMessage> recentHistory,
        CancellationToken cancellationToken = default)
    {
        if (_disabledForSession) return false;

        _turnsInCurrentPrompt++;

        if (_turnsInCurrentPrompt >= LlmCheckAfterTurns
            && _turnsInCurrentPrompt - _lastCheckTurn >= _llmCheckInterval)
        {
            _lastCheckTurn = _turnsInCurrentPrompt;
            return await CheckForLoopWithLlmAsync(recentHistory, cancellationToken);
        }

        return false;
    }

    /// <summary>
    /// Resets all tracking state for a new prompt.
    /// </summary>
    public void Reset(string promptId, string? userPrompt = null)
    {
        _promptId = promptId;
        _userPrompt = userPrompt ?? string.Empty;
        _lastToolCallKey = null;
        _toolCallRepetitionCount = 0;
        ResetContentTracking(resetHistory: true);
        _turnsInCurrentPrompt = 0;
        _llmCheckInterval = DefaultLlmCheckInterval;
        _lastCheckTurn = 0;
        _loopDetected = false;
    }

    // ─── Tool Call Loop ────────────────────────────────────────────────────────

    private bool CheckToolCallLoop(string name, Dictionary<string, object?> args)
    {
        var key = GetToolCallKey(name, args);
        if (_lastToolCallKey == key)
        {
            _toolCallRepetitionCount++;
        }
        else
        {
            _lastToolCallKey = key;
            _toolCallRepetitionCount = 1;
        }

        if (_toolCallRepetitionCount >= ToolCallLoopThreshold)
        {
            Logger.Warning("Loop detected: {Count} consecutive identical tool calls to '{Tool}'",
                _toolCallRepetitionCount, name);
            LoopDetected?.Invoke(this, new LoopDetectedEventArgs
            {
                LoopType = LoopType.ConsecutiveIdenticalToolCalls,
                PromptId = _promptId
            });
            return true;
        }
        return false;
    }

    private static string GetToolCallKey(string name, Dictionary<string, object?> args)
    {
        var keyString = $"{name}:{JsonSerializer.Serialize(args)}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(keyString));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    // ─── Content Loop ─────────────────────────────────────────────────────────

    private bool CheckContentLoop(string content)
    {
        int numFences = CountOccurrences(content, "```");
        bool hasTable = System.Text.RegularExpressions.Regex.IsMatch(content, @"(^|\n)\s*(\|.*\||[|+-]{3,})");
        bool hasListItem = System.Text.RegularExpressions.Regex.IsMatch(content, @"(^|\n)\s*[*\-+]\s|(^|\n)\s*\d+\.\s");
        bool hasHeading = System.Text.RegularExpressions.Regex.IsMatch(content, @"(^|\n)#+\s");
        bool hasBlockquote = System.Text.RegularExpressions.Regex.IsMatch(content, @"(^|\n)>\s");
        bool isDivider = System.Text.RegularExpressions.Regex.IsMatch(content, @"^[+\-_=*─━]+$");

        if (numFences > 0 || hasTable || hasListItem || hasHeading || hasBlockquote || isDivider)
            ResetContentTracking(resetHistory: false);

        bool wasInCodeBlock = _inCodeBlock;
        if (numFences % 2 != 0) _inCodeBlock = !_inCodeBlock;

        if (wasInCodeBlock || _inCodeBlock || isDivider) return false;

        _streamContentHistory += content;
        TruncateAndUpdate();
        return AnalyzeContentChunksForLoop();
    }

    private void TruncateAndUpdate()
    {
        if (_streamContentHistory.Length <= MaxHistoryLength) return;

        int truncationAmount = _streamContentHistory.Length - MaxHistoryLength;
        _streamContentHistory = _streamContentHistory[truncationAmount..];
        _lastContentIndex = Math.Max(0, _lastContentIndex - truncationAmount);

        var toRemove = new List<string>();
        foreach (var (hash, indices) in _contentStats)
        {
            var adjusted = indices
                .Select(i => i - truncationAmount)
                .Where(i => i >= 0)
                .ToList();
            if (adjusted.Count > 0)
                _contentStats[hash] = adjusted;
            else
                toRemove.Add(hash);
        }
        foreach (var key in toRemove) _contentStats.Remove(key);
    }

    private bool AnalyzeContentChunksForLoop()
    {
        while (_lastContentIndex + ContentChunkSize <= _streamContentHistory.Length)
        {
            var chunk = _streamContentHistory.Substring(_lastContentIndex, ContentChunkSize);
            var hash = GetChunkHash(chunk);

            if (IsLoopDetectedForChunk(chunk, hash))
            {
                Logger.Warning("Loop detected: repetitive content pattern in stream");
                LoopDetected?.Invoke(this, new LoopDetectedEventArgs
                {
                    LoopType = LoopType.ChantingIdenticalSentences,
                    PromptId = _promptId
                });
                return true;
            }
            _lastContentIndex++;
        }
        return false;
    }

    private bool IsLoopDetectedForChunk(string chunk, string hash)
    {
        if (!_contentStats.TryGetValue(hash, out var existingIndices))
        {
            _contentStats[hash] = new List<int> { _lastContentIndex };
            return false;
        }

        // Verify content match (avoid hash collisions)
        var originalChunk = _streamContentHistory.Substring(
            existingIndices[0],
            Math.Min(ContentChunkSize, _streamContentHistory.Length - existingIndices[0]));
        if (originalChunk != chunk) return false;

        existingIndices.Add(_lastContentIndex);
        if (existingIndices.Count < ContentLoopThreshold) return false;

        var recentIndices = existingIndices.TakeLast(ContentLoopThreshold).ToList();
        double totalDistance = recentIndices[^1] - recentIndices[0];
        double averageDistance = totalDistance / (ContentLoopThreshold - 1);
        double maxAllowedDistance = ContentChunkSize * 5;

        if (averageDistance > maxAllowedDistance) return false;

        // Verify periods are repetitive
        var periods = new HashSet<string>();
        for (int i = 0; i < recentIndices.Count - 1; i++)
        {
            periods.Add(_streamContentHistory.Substring(
                recentIndices[i],
                recentIndices[i + 1] - recentIndices[i]));
        }

        return periods.Count <= ContentLoopThreshold / 2;
    }

    private static string GetChunkHash(string chunk)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(chunk));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private void ResetContentTracking(bool resetHistory = true)
    {
        if (resetHistory) _streamContentHistory = string.Empty;
        _contentStats.Clear();
        _lastContentIndex = 0;
        _inCodeBlock = false;
    }

    // ─── LLM Loop Check ───────────────────────────────────────────────────────

    private async Task<bool> CheckForLoopWithLlmAsync(
        IReadOnlyList<ContentMessage> fullHistory,
        CancellationToken cancellationToken)
    {
        var recentHistory = fullHistory
            .TakeLast(LlmLoopCheckHistoryCount)
            .ToList();

        // Remove leading function responses and trailing function calls
        while (recentHistory.Count > 0 && IsFunctionResponse(recentHistory[0]))
            recentHistory.RemoveAt(0);
        while (recentHistory.Count > 0 && IsFunctionCall(recentHistory[^1]))
            recentHistory.RemoveAt(recentHistory.Count - 1);

        if (recentHistory.Count == 0) return false;

        try
        {
            var generator = ContentGeneratorFactory.Create(_config);

            var analysisPrompt = BuildLoopAnalysisPrompt(recentHistory);
            var request = new GenerateContentRequest
            {
                Model = _config.GetModel(),
                Contents = new List<ContentMessage>
                {
                    new()
                    {
                        Role = LlmRole.User,
                        Parts = new List<ContentPart> { new TextContentPart(analysisPrompt) }
                    }
                },
            };

            var response = await generator.GenerateContentAsync(request, cancellationToken: cancellationToken);
            var text = response.Candidates
                .SelectMany(c => c.Content)
                .OfType<TextContentPart>()
                .Select(p => p.Text)
                .FirstOrDefault();

            if (string.IsNullOrEmpty(text)) return false;

            // Parse confidence from response
            double confidence = ParseConfidence(text);
            UpdateCheckInterval(confidence);

            if (confidence >= LlmConfidenceThreshold)
            {
                Logger.Warning("LLM detected loop with confidence {Confidence:P0} in prompt {PromptId}",
                    confidence, _promptId);
                LoopDetected?.Invoke(this, new LoopDetectedEventArgs
                {
                    LoopType = LoopType.LlmDetectedLoop,
                    PromptId = _promptId
                });
                return true;
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "LLM loop check failed");
        }

        return false;
    }

    private static string BuildLoopAnalysisPrompt(IReadOnlyList<ContentMessage> history)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Analyze this conversation history and determine if the AI assistant is stuck in an unproductive loop.");
        sb.AppendLine("A loop is when the same actions repeat with no forward progress.");
        sb.AppendLine();
        sb.AppendLine("Respond with JSON: {\"unproductive_state_analysis\": \"...\", \"unproductive_state_confidence\": 0.0}");
        sb.AppendLine("where confidence is 0.0-1.0 (1.0 = definitely looping).");
        sb.AppendLine();
        sb.AppendLine("Recent conversation:");

        foreach (var msg in history)
        {
            var role = msg.Role == LlmRole.User ? "User" : "Assistant";
            foreach (var part in msg.Parts)
            {
                switch (part)
                {
                    case TextContentPart tp when !string.IsNullOrEmpty(tp.Text):
                        sb.AppendLine($"[{role}]: {tp.Text[..Math.Min(200, tp.Text.Length)]}");
                        break;
                    case FunctionCallPart fc:
                        sb.AppendLine($"[Tool Call]: {fc.FunctionName}({JsonSerializer.Serialize(fc.Arguments)})");
                        break;
                }
            }
        }

        return sb.ToString();
    }

    private static double ParseConfidence(string text)
    {
        try
        {
            // Try to parse as JSON
            var jsonStart = text.IndexOf('{');
            var jsonEnd = text.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var json = text[jsonStart..(jsonEnd + 1)];
                var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("unproductive_state_confidence", out var conf))
                    return conf.GetDouble();
            }
        }
        catch { /* ignore parse errors */ }
        return 0.0;
    }

    private void UpdateCheckInterval(double confidence)
    {
        _llmCheckInterval = (int)Math.Round(
            MinLlmCheckInterval +
            (MaxLlmCheckInterval - MinLlmCheckInterval) * (1.0 - confidence));
    }

    private static bool IsFunctionCall(ContentMessage msg)
        => msg.Parts.Any(p => p is FunctionCallPart);

    private static bool IsFunctionResponse(ContentMessage msg)
        => msg.Parts.Any(p => p is FunctionResponsePart);

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0;
        int idx = 0;
        while ((idx = text.IndexOf(pattern, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += pattern.Length;
        }
        return count;
    }
}
