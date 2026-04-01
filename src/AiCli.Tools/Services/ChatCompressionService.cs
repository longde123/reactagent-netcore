using AiCli.Core.Chat;
using AiCli.Core.Configuration;
using AiCli.Core.Logging;
using AiCli.Core.Types;
using System.Text;
using System.Text.Json;
// ReSharper disable once RedundantUsingDirective

namespace AiCli.Core.Services;

/// <summary>
/// Status of a chat compression attempt.
/// </summary>
public enum CompressionStatus
{
    Noop,
    Compressed,
    ContentTruncated,
    CompressionFailedEmptySummary,
    CompressionFailedInflatedTokenCount,
}

/// <summary>
/// Information returned from a compression attempt.
/// </summary>
public record ChatCompressionInfo
{
    public required int OriginalTokenCount { get; init; }
    public required int NewTokenCount { get; init; }
    public required CompressionStatus Status { get; init; }
}

/// <summary>
/// Result of a compression attempt.
/// </summary>
public record ChatCompressionResult
{
    public IReadOnlyList<ContentMessage>? NewHistory { get; init; }
    public required ChatCompressionInfo Info { get; init; }
}

/// <summary>
/// Compresses chat history when it grows too large, using LLM summarization.
/// Ported from packages/core/src/services/chatCompressionService.ts
/// </summary>
public class ChatCompressionService
{
    private const double DefaultCompressionTokenThreshold = 0.5;
    private const double CompressionPreserveThreshold = 0.3;
    private const int CompressionFunctionResponseTokenBudget = 50_000;

    private static readonly ILogger Logger = LoggerHelper.ForContext<ChatCompressionService>();

    // ─── Public API ───────────────────────────────────────────────────────────

    public async Task<ChatCompressionResult> CompressAsync(
        AiChat chat,
        string promptId,
        bool force,
        string model,
        Config config,
        bool hasFailedCompressionAttempt,
        CancellationToken cancellationToken = default)
    {
        var curatedHistory = chat.GetCuratedHistory();

        if (curatedHistory.Count == 0)
        {
            return new ChatCompressionResult
            {
                NewHistory = null,
                Info = new ChatCompressionInfo
                {
                    OriginalTokenCount = 0,
                    NewTokenCount = 0,
                    Status = CompressionStatus.Noop,
                }
            };
        }

        var originalTokenCount = EstimateTokenCount(curatedHistory);

        if (!force)
        {
            var threshold = DefaultCompressionTokenThreshold;
            var tokenLimit = GetTokenLimit(model);
            if (originalTokenCount < threshold * tokenLimit)
            {
                return new ChatCompressionResult
                {
                    NewHistory = null,
                    Info = new ChatCompressionInfo
                    {
                        OriginalTokenCount = originalTokenCount,
                        NewTokenCount = originalTokenCount,
                        Status = CompressionStatus.Noop,
                    }
                };
            }
        }

        var truncatedHistory = await TruncateHistoryToBudgetAsync(curatedHistory, config);

        if (hasFailedCompressionAttempt && !force)
        {
            var truncatedTokenCount = EstimateTokenCount(truncatedHistory);
            if (truncatedTokenCount < originalTokenCount)
            {
                return new ChatCompressionResult
                {
                    NewHistory = truncatedHistory,
                    Info = new ChatCompressionInfo
                    {
                        OriginalTokenCount = originalTokenCount,
                        NewTokenCount = truncatedTokenCount,
                        Status = CompressionStatus.ContentTruncated,
                    }
                };
            }

            return new ChatCompressionResult
            {
                NewHistory = null,
                Info = new ChatCompressionInfo
                {
                    OriginalTokenCount = originalTokenCount,
                    NewTokenCount = originalTokenCount,
                    Status = CompressionStatus.Noop,
                }
            };
        }

        var splitPoint = FindCompressSplitPoint(truncatedHistory, 1.0 - CompressionPreserveThreshold);
        var historyToCompress = truncatedHistory.Take(splitPoint).ToList();
        var historyToKeep = truncatedHistory.Skip(splitPoint).ToList();

        if (historyToCompress.Count == 0)
        {
            return new ChatCompressionResult
            {
                NewHistory = null,
                Info = new ChatCompressionInfo
                {
                    OriginalTokenCount = originalTokenCount,
                    NewTokenCount = originalTokenCount,
                    Status = CompressionStatus.Noop,
                }
            };
        }

        // Build compression prompt
        var summary = await GenerateSummaryAsync(chat, historyToCompress, promptId, config, cancellationToken);

        if (string.IsNullOrEmpty(summary))
        {
            Logger.Warning("Compression failed: empty summary returned");
            return new ChatCompressionResult
            {
                NewHistory = null,
                Info = new ChatCompressionInfo
                {
                    OriginalTokenCount = originalTokenCount,
                    NewTokenCount = originalTokenCount,
                    Status = CompressionStatus.CompressionFailedEmptySummary,
                }
            };
        }

        var extraHistory = new List<ContentMessage>
        {
            new() { Role = LlmRole.User, Parts = new List<ContentPart> { new TextContentPart(summary) } },
            new() { Role = LlmRole.Model, Parts = new List<ContentPart> { new TextContentPart("Got it. Thanks for the additional context!") } },
        };
        extraHistory.AddRange(historyToKeep);

        var newTokenCount = EstimateTokenCount(extraHistory);

        if (newTokenCount > originalTokenCount)
        {
            return new ChatCompressionResult
            {
                NewHistory = null,
                Info = new ChatCompressionInfo
                {
                    OriginalTokenCount = originalTokenCount,
                    NewTokenCount = newTokenCount,
                    Status = CompressionStatus.CompressionFailedInflatedTokenCount,
                }
            };
        }

        Logger.Information("Compressed chat: {Original} -> {New} tokens", originalTokenCount, newTokenCount);

        return new ChatCompressionResult
        {
            NewHistory = extraHistory,
            Info = new ChatCompressionInfo
            {
                OriginalTokenCount = originalTokenCount,
                NewTokenCount = newTokenCount,
                Status = CompressionStatus.Compressed,
            }
        };
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Finds the index of the oldest message to keep, compressing everything before it.
    /// </summary>
    public static int FindCompressSplitPoint(IReadOnlyList<ContentMessage> contents, double fraction)
    {
        if (fraction <= 0 || fraction >= 1)
            throw new ArgumentException("Fraction must be between 0 and 1", nameof(fraction));

        var charCounts = contents.Select(c => JsonSerializer.Serialize(c).Length).ToList();
        var totalCharCount = charCounts.Sum();
        var targetCharCount = totalCharCount * fraction;

        int lastSplitPoint = 0;
        double cumulativeCharCount = 0;

        for (int i = 0; i < contents.Count; i++)
        {
            var content = contents[i];
            bool isUserNonFunctionResponse = content.Role == LlmRole.User
                && !content.Parts.Any(p => p is FunctionResponsePart);

            if (isUserNonFunctionResponse)
            {
                if (cumulativeCharCount >= targetCharCount)
                    return i;
                lastSplitPoint = i;
            }
            cumulativeCharCount += charCounts[i];
        }

        // Check if we can compress everything
        var lastContent = contents.Count > 0 ? contents[^1] : null;
        if (lastContent?.Role == LlmRole.Model && !lastContent.Parts.Any(p => p is FunctionCallPart))
            return contents.Count;

        return lastSplitPoint;
    }

    private async Task<List<ContentMessage>> TruncateHistoryToBudgetAsync(
        IReadOnlyList<ContentMessage> history,
        Config config)
    {
        int functionResponseTokenCounter = 0;
        var result = new List<ContentMessage>(history.Count);

        // Iterate backwards: newest messages first
        for (int i = history.Count - 1; i >= 0; i--)
        {
            var content = history[i];
            var newParts = new List<ContentPart>();

            if (content.Parts != null)
            {
                for (int j = content.Parts.Count - 1; j >= 0; j--)
                {
                    var part = content.Parts[j];

                    if (part is FunctionResponsePart frPart)
                    {
                        var contentStr = ExtractFunctionResponseText(frPart);
                        var tokens = EstimateTokenCountText(contentStr);

                        if (functionResponseTokenCounter + tokens > CompressionFunctionResponseTokenBudget)
                        {
                            // Truncate: keep last 30 lines
                            var lines = contentStr.Split('\n');
                            var truncated = lines.Length > 30
                                ? string.Join('\n', lines.TakeLast(30))
                                : contentStr;
                            var truncatedMsg = $"[Truncated - showing last 30 lines]\n{truncated}";

                            newParts.Insert(0, new FunctionResponsePart
                            {
                                FunctionName = frPart.FunctionName,
                                Response = new Dictionary<string, object?> { ["output"] = truncatedMsg },
                                Id = frPart.Id,
                            });

                            functionResponseTokenCounter += EstimateTokenCountText(truncatedMsg);
                        }
                        else
                        {
                            functionResponseTokenCounter += tokens;
                            newParts.Insert(0, part);
                        }
                    }
                    else
                    {
                        newParts.Insert(0, part);
                    }
                }
            }

            result.Insert(0, content with { Parts = newParts });
        }

        return await Task.FromResult(result);
    }

    private async Task<string?> GenerateSummaryAsync(
        AiChat chat,
        IReadOnlyList<ContentMessage> historyToCompress,
        string promptId,
        Config config,
        CancellationToken cancellationToken)
    {
        // Build a summary prompt from the history
        var sb = new StringBuilder();
        sb.AppendLine("<state_snapshot>");
        sb.AppendLine("Summarize the conversation history below, preserving:");
        sb.AppendLine("- Key decisions and constraints established");
        sb.AppendLine("- Important file paths and their purposes");
        sb.AppendLine("- Current task state and next steps");
        sb.AppendLine("- Any critical technical details");
        sb.AppendLine();

        foreach (var msg in historyToCompress)
        {
            var roleStr = msg.Role == LlmRole.User ? "User" : "Assistant";
            foreach (var part in msg.Parts)
            {
                switch (part)
                {
                    case TextContentPart tp when !string.IsNullOrEmpty(tp.Text):
                        sb.AppendLine($"[{roleStr}]: {tp.Text}");
                        break;
                    case FunctionCallPart fc:
                        sb.AppendLine($"[Tool Call]: {fc.FunctionName}");
                        break;
                    case FunctionResponsePart fr:
                        sb.AppendLine($"[Tool Result]: {fr.FunctionName}");
                        break;
                }
            }
        }

        sb.AppendLine("</state_snapshot>");

        try
        {
            var generator = ContentGeneratorFactory.Create(config);

            var summaryMessages = new List<ContentMessage>
            {
                new()
                {
                    Role = LlmRole.User,
                    Parts = new List<ContentPart> { new TextContentPart(sb.ToString()) }
                }
            };

            var request = new GenerateContentRequest
            {
                Model = config.GetModel(),
                Contents = summaryMessages,
            };

            var response = await generator.GenerateContentAsync(request, cancellationToken);
            return response.Candidates
                .SelectMany(c => c.Content)
                .OfType<TextContentPart>()
                .Select(p => p.Text)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to generate compression summary");
            return null;
        }
    }

    private static string ExtractFunctionResponseText(FunctionResponsePart response)
    {
        if (response.Response == null) return string.Empty;
        if (response.Response.TryGetValue("output", out var output) && output is string s) return s;
        if (response.Response.TryGetValue("content", out var content) && content is string c) return c;
        return JsonSerializer.Serialize(response.Response);
    }

    private static int EstimateTokenCount(IReadOnlyList<ContentMessage> messages)
    {
        var text = JsonSerializer.Serialize(messages);
        return EstimateTokenCountText(text);
    }

    private static int EstimateTokenCountText(string text)
    {
        // Rough estimate: ~4 chars per token
        return text.Length / 4;
    }

    private static int GetTokenLimit(string model)
    {
        return model.ToLowerInvariant() switch
        {
            var m when m.Contains("pro") => 2_000_000,
            var m when m.Contains("flash") => 1_000_000,
            _ => 1_000_000,
        };
    }
}
