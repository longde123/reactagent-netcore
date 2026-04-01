using AiCli.Core.Chat;
using AiCli.Core.Logging;
using AiCli.Core.Types;

namespace AiCli.Core.Services;

/// <summary>
/// Options for generating a session summary.
/// </summary>
public record GenerateSummaryOptions
{
    public required IReadOnlyList<RecordedMessage> Messages { get; init; }
    public int MaxMessages { get; init; } = 20;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Service for generating AI summaries of chat sessions.
/// Produces a single concise sentence describing the user's primary intent.
/// Ported from packages/core/src/services/sessionSummaryService.ts
/// </summary>
public class SessionSummaryService
{
    private const int MaxMessageLength = 500;

    private const string SummaryPrompt = """
        Summarize the user's primary intent or goal in this conversation in ONE sentence (max 80 characters).
        Focus on what the user was trying to accomplish.

        Examples:
        - "Add dark mode to the app"
        - "Fix authentication bug in login flow"
        - "Understand how the API routing works"
        - "Refactor database connection logic"
        - "Debug memory leak in production"

        Conversation:
        {conversation}

        Summary (max 80 chars):
        """;

    private static readonly ILogger Logger = LoggerHelper.ForContext<SessionSummaryService>();

    private readonly IContentGenerator _contentGenerator;

    public SessionSummaryService(IContentGenerator contentGenerator)
    {
        _contentGenerator = contentGenerator;
    }

    /// <summary>
    /// Generate a one-line summary of the chat session focused on user intent.
    /// Returns null on failure (graceful degradation).
    /// </summary>
    public async Task<string?> GenerateSummaryAsync(
        GenerateSummaryOptions options,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Filter to user/model messages only
            var filtered = options.Messages
                .Where(m => m.Role is LlmRole.User or LlmRole.Model or LlmRole.Assistant)
                .Where(m => GetMessageText(m).Trim().Length > 0)
                .ToList();

            if (filtered.Count == 0) return null;

            // Sliding window: first N/2 + last N/2
            List<RecordedMessage> relevant;
            if (filtered.Count <= options.MaxMessages)
            {
                relevant = filtered;
            }
            else
            {
                var firstCount = (int)Math.Ceiling(options.MaxMessages / 2.0);
                var lastCount = options.MaxMessages / 2;
                relevant = filtered.Take(firstCount)
                    .Concat(filtered.TakeLast(lastCount))
                    .ToList();
            }

            var conversationText = string.Join("\n\n", relevant.Select(m =>
            {
                var role = m.Role == LlmRole.User ? "User" : "Assistant";
                var text = GetMessageText(m);
                if (text.Length > MaxMessageLength)
                    text = text[..MaxMessageLength] + "...";
                return $"{role}: {text}";
            }));

            var prompt = SummaryPrompt.Replace("{conversation}", conversationText);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(options.Timeout);

            var request = new GenerateContentRequest
            {
                Model = _contentGenerator.GetModelId(),
                Contents = new List<ContentMessage>
                {
                    new() { Role = LlmRole.User, Parts = new List<ContentPart> { new TextContentPart(prompt) } }
                },
            };

            var response = await _contentGenerator.GenerateContentAsync(
                request, cancellationToken: timeoutCts.Token);

            var summary = response.Candidates
                .SelectMany(c => c.Content)
                .OfType<TextContentPart>()
                .Select(p => p.Text)
                .FirstOrDefault();

            if (string.IsNullOrWhiteSpace(summary)) return null;

            // Normalize whitespace and remove surrounding quotes
            summary = System.Text.RegularExpressions.Regex.Replace(summary, @"\s+", " ").Trim();
            summary = summary.Trim('"', '\'');

            Logger.Debug("Session summary generated: {Summary}", summary);
            return summary;
        }
        catch (OperationCanceledException)
        {
            Logger.Debug("Session summary generation timed out");
            return null;
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Error generating session summary");
            return null;
        }
    }

    private static string GetMessageText(RecordedMessage message)
    {
        return string.Join(" ", message.Parts
            .OfType<TextContentPart>()
            .Select(p => p.Text));
    }
}
