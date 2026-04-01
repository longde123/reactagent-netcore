using AiCli.Core.Types;

namespace AiCli.Core.Chat;

/// <summary>
/// Interface for generating content and counting tokens.
/// </summary>
public interface IContentGenerator
{
    /// <summary>
    /// Gets the default model id used by this generator.
    /// </summary>
    string GetModelId();

    /// <summary>
    /// Sends a single message and returns the produced content message (convenience wrapper).
    /// </summary>
    Task<ContentMessage> SendMessageAsync(
        ContentMessage message,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a message and returns streaming content messages (convenience wrapper).
    /// </summary>
    IAsyncEnumerable<ContentMessage> SendMessageStreamAsync(
        ContentMessage message,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates content (non-streaming).
    /// </summary>
    Task<GenerateContentResponse> GenerateContentAsync(
        GenerateContentRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates content with streaming.
    /// </summary>
    IAsyncEnumerable<GenerateContentResponse> GenerateContentStreamAsync(
        GenerateContentRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts tokens in the content.
    /// </summary>
    Task<CountTokensResponse> CountTokensAsync(
        CountTokensRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Embeds content.
    /// </summary>
    Task<EmbedContentResponse> EmbedContentAsync(
        EmbedContentRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Request for generating content.
/// </summary>
public record GenerateContentRequest
{
    /// <summary>
    /// The model to use.
    /// </summary>
    public required string Model { get; init; }

    /// <summary>
    /// The contents (messages) to generate from.
    /// </summary>
    public required List<ContentMessage> Contents { get; init; }

    /// <summary>
    /// System instruction.
    /// </summary>
    public string? SystemInstruction { get; init; }

    /// <summary>
    /// Available tools.
    /// </summary>
    public List<Tool>? Tools { get; init; }

    /// <summary>
    /// Tool configuration.
    /// </summary>
    public ToolConfig? ToolConfig { get; init; }

    /// <summary>
    /// Generation configuration.
    /// </summary>
    public GenerationConfig? Config { get; init; }

    /// <summary>
    /// Cancellation token.
    /// </summary>
    public CancellationToken CancellationToken { get; init; }
}

/// <summary>
/// Response from generate content.
/// </summary>
public record GenerateContentResponse
{
    /// <summary>
    /// The generated candidates.
    /// </summary>
    public required List<Candidate> Candidates { get; init; }

    /// <summary>
    /// Usage metadata (token counts).
    /// </summary>
    public UsageMetadata? UsageMetadata { get; init; }

    /// <summary>
    /// The prompt feedback.
    /// </summary>
    public PromptFeedback? PromptFeedback { get; init; }

    /// <summary>
    /// Model version info.
    /// </summary>
    public string? ModelVersion { get; init; }
}

/// <summary>
/// Generated candidate.
/// </summary>
public record Candidate
{
    /// <summary>
    /// The content parts.
    /// </summary>
    public required List<ContentPart> Content { get; init; }

    /// <summary>
    /// The finish reason.
    /// </summary>
    public FinishReason? FinishReason { get; init; }

    /// <summary>
    /// The index of this candidate.
    /// </summary>
    public int Index { get; init; }

    /// <summary>
    /// Safety ratings.
    /// </summary>
    public List<SafetyRating>? SafetyRatings { get; init; }

    /// <summary>
    /// Citation metadata.
    /// </summary>
    public CitationMetadata? CitationMetadata { get; init; }

    /// <summary>
    /// Grounding attribution.
    /// </summary>
    public GroundingAttribution? GroundingAttribution { get; init; }
}

/// <summary>
/// Finish reason.
/// </summary>
public enum FinishReason
{
    /// <summary>
    /// Finished normally.
    /// </summary>
    Stop,

    /// <summary>
    /// Max tokens reached.
    /// </summary>
    MaxTokens,

    /// <summary>
    /// Safety filter triggered.
    /// </summary>
    Safety,

    /// <summary>
    /// Content filter triggered.
    /// </summary>
    Recitation,

    /// <summary>
    /// Other reason.
    /// </summary>
    Other
    ,
    /// <summary>
    /// Function call requested by model.
    /// </summary>
    FunctionCall
}

/// <summary>
/// Usage metadata.
/// </summary>
public record UsageMetadata
{
    /// <summary>
    /// Prompt token count.
    /// </summary>
    public int PromptTokenCount { get; init; }

    /// <summary>
    /// Candidates token count.
    /// </summary>
    public int CandidatesTokenCount { get; init; }

    /// <summary>
    /// Total token count.
    /// </summary>
    public int TotalTokenCount { get; init; }
}

/// <summary>
/// Safety rating.
/// </summary>
public record SafetyRating
{
    /// <summary>
    /// The category.
    /// </summary>
    public required SafetyCategory Category { get; init; }

    /// <summary>
    /// The probability.
    /// </summary>
    public required SafetyProbability Probability { get; init; }

    /// <summary>
    /// Whether it was blocked.
    /// </summary>
    public bool Blocked { get; init; }
}

/// <summary>
/// Safety categories.
/// </summary>
public enum SafetyCategory
{
    HateSpeech,
    DangerousContent,
    Harassment,
    SexuallyExplicit,
    MedicalAdvice,
    CivicIntegrity
}

/// <summary>
/// Safety probability levels.
/// </summary>
public enum SafetyProbability
{
    Negligible,
    Low,
    Medium,
    High
}

/// <summary>
/// Tool configuration.
/// </summary>
public record ToolConfig
{
    /// <summary>
    /// Function calling mode.
    /// </summary>
    public FunctionCallingConfig? FunctionCallingConfig { get; init; }
}

/// <summary>
/// Function calling configuration.
/// </summary>
public record FunctionCallingConfig
{
    /// <summary>
    /// The mode.
    /// </summary>
    public required FunctionCallingMode Mode { get; init; }

    /// <summary>
    /// Allowed function names.
    /// </summary>
    public List<string>? AllowedFunctionNames { get; init; }
}

/// <summary>
/// Function calling mode.
/// </summary>
public enum FunctionCallingMode
{
    Auto,
    Any,
    None
}

/// <summary>
/// Generation configuration.
/// </summary>
public record GenerationConfig
{
    /// <summary>
    /// Temperature (0.0 - 2.0).
    /// </summary>
    public double? Temperature { get; init; }

    /// <summary>
    /// Top P (0.0 - 1.0).
    /// </summary>
    public double? TopP { get; init; }

    /// <summary>
    /// Top K.
    /// </summary>
    public int? TopK { get; init; }

    /// <summary>
    /// Maximum output tokens.
    /// </summary>
    public int? MaxOutputTokens { get; init; }

    /// <summary>
    /// Stop sequences.
    /// </summary>
    public List<string>? StopSequences { get; init; }
}

/// <summary>
/// Count tokens request.
/// </summary>
public record CountTokensRequest
{
    /// <summary>
    /// The model to use for counting.
    /// </summary>
    public required string Model { get; init; }

    /// <summary>
    /// The contents to count.
    /// </summary>
    public required List<ContentMessage> Contents { get; init; }
}

/// <summary>
/// Count tokens response.
/// </summary>
public record CountTokensResponse
{
    /// <summary>
    /// The total token count.
    /// </summary>
    public required int TotalTokens { get; init; }
}

/// <summary>
/// Embed content request.
/// </summary>
public record EmbedContentRequest
{
    /// <summary>
    /// The model to use.
    /// </summary>
    public required string Model { get; init; }

    /// <summary>
    /// The content to embed.
    /// </summary>
    public required ContentMessage Content { get; init; }
}

/// <summary>
/// Embed content response.
/// </summary>
public record EmbedContentResponse
{
    /// <summary>
    /// The embedding values.
    /// </summary>
    public required List<double> Embedding { get; init; }
}

/// <summary>
/// Prompt feedback.
/// </summary>
public record PromptFeedback
{
    /// <summary>
    /// The reason for the feedback.
    /// </summary>
    public required string Reason { get; init; }

    /// <summary>
    /// Safety ratings.
    /// </summary>
    public List<SafetyRating>? SafetyRatings { get; init; }
}

/// <summary>
/// Citation metadata.
/// </summary>
public record CitationMetadata
{
    /// <summary>
    /// The citations.
    /// </summary>
    public List<Citation> Citations { get; init; }
}

/// <summary>
/// Citation.
/// </summary>
public record Citation
{
    /// <summary>
    /// The start index.
    /// </summary>
    public required int StartIndex { get; init; }

    /// <summary>
    /// The end index.
    /// </summary>
    public required int EndIndex { get; init; }

    /// <summary>
    /// The URI.
    /// </summary>
    public required string? Uri { get; init; }

    /// <summary>
    /// The license.
    /// </summary>
    public required string? License { get; init; }
}

/// <summary>
/// Grounding attribution.
/// </summary>
public record GroundingAttribution
{
    /// <summary>
    /// The grounding chunks.
    /// </summary>
    public List<GroundingChunk> GroundingChunks { get; init; }

    /// <summary>
    /// The search queries.
    /// </summary>
    public List<SearchQuery> SearchQueries { get; init; }

    /// <summary>
    /// The retrieval queries.
    /// </summary>
    public List<RetrievalQuery> RetrievalQueries { get; init; }
}

/// <summary>
/// Grounding chunk.
/// </summary>
public record GroundingChunk
{
    /// <summary>
    /// The text.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// The source URI.
    /// </summary>
    public required string? SourceUri { get; init; }
}

/// <summary>
/// Search query.
/// </summary>
public record SearchQuery
{
    /// <summary>
    /// The query text.
    /// </summary>
    public required string QueryText { get; init; }
}

/// <summary>
/// Retrieval query.
/// </summary>
public record RetrievalQuery
{
    /// <summary>
    /// The query text.
    /// </summary>
    public required string QueryText { get; init; }
}
