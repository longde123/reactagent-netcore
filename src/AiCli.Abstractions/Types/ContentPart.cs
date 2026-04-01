namespace AiCli.Core.Types;

/// <summary>
/// Base class for content parts in a message.
/// This is a discriminated union pattern implementation.
/// </summary>
public abstract record ContentPart;

/// <summary>
/// Text content part.
/// </summary>
public record TextContentPart(string Text) : ContentPart;

/// <summary>
/// Thinking/reasoning content part - emitted by thinking models (e.g. gpt-oss:20b, qwen3).
/// Contains the model's internal reasoning before generating the final response.
/// </summary>
public record ThinkingContentPart(string Text) : ContentPart;

/// <summary>
/// Function call content part.
/// </summary>
/// <summary>
/// Function call content part.
/// Exposes convenience properties for callers that previously expected direct access.
/// </summary>
public record FunctionCallPart : ContentPart
{
    public string FunctionName { get; init; } = string.Empty;
    public Dictionary<string, object?> Arguments { get; init; } = new();
    public string Id { get; init; } = Guid.NewGuid().ToString();

    public FunctionCallPart() { }

    public FunctionCallPart(FunctionCall fc)
    {
        FunctionName = fc.Name;
        Arguments = fc.Args ?? new Dictionary<string, object?>();
        Id = fc.Id;
    }
}

/// <summary>
/// Function response content part.
/// </summary>
public record FunctionResponsePart : ContentPart
{
    public string FunctionName { get; init; } = string.Empty;
    public Dictionary<string, object?> Response { get; init; } = new();
    public string Id { get; init; } = Guid.NewGuid().ToString();

    public FunctionResponsePart() { }

    public FunctionResponsePart(FunctionResponse fr)
    {
        FunctionName = fr.Name;
        Response = fr.Response ?? new Dictionary<string, object?>();
        Id = fr.Id;
    }
}

/// <summary>
/// Inline data content part (e.g., images, files).
/// </summary>
public record InlineDataPart(InlineData InlineData) : ContentPart;

/// <summary>
/// Executable code content part.
/// </summary>
public record ExecutableCodePart(string Language, string Code) : ContentPart;

/// <summary>
/// Code execution result content part.
/// </summary>
public record CodeExecutionResultPart(string Outcome, string? Output = null) : ContentPart;

/// <summary>
/// Thought/reasoning content part.
/// </summary>
public record ThoughtPart(string Thought) : ContentPart;

/// <summary>
/// File data content part.
/// </summary>
public record FileDataPart(string MimeType, string FileUri) : ContentPart;

/// <summary>
/// Extension methods for ContentPart.
/// </summary>
public static class ContentPartExtensions
{
    /// <summary>
    /// Pattern matches the content part and executes the appropriate handler.
    /// </summary>
    public static T Match<T>(this ContentPart part,
        Func<TextContentPart, T> textHandler,
        Func<FunctionCallPart, T>? functionCallHandler = null,
        Func<FunctionResponsePart, T>? functionResponseHandler = null,
        Func<InlineDataPart, T>? inlineDataHandler = null,
        Func<ExecutableCodePart, T>? executableCodeHandler = null,
        Func<CodeExecutionResultPart, T>? codeExecutionResultHandler = null,
        Func<ThoughtPart, T>? thoughtHandler = null,
        Func<FileDataPart, T>? fileDataHandler = null)
    {
        return part switch
        {
            TextContentPart t => textHandler(t),
            FunctionCallPart fc => functionCallHandler != null ? functionCallHandler(fc) : throw new InvalidOperationException("No handler for FunctionCallPart"),
            FunctionResponsePart fr => functionResponseHandler != null ? functionResponseHandler(fr) : throw new InvalidOperationException("No handler for FunctionResponsePart"),
            InlineDataPart id => inlineDataHandler != null ? inlineDataHandler(id) : throw new InvalidOperationException("No handler for InlineDataPart"),
            ExecutableCodePart ec => executableCodeHandler != null ? executableCodeHandler(ec) : throw new InvalidOperationException("No handler for ExecutableCodePart"),
            CodeExecutionResultPart cer => codeExecutionResultHandler != null ? codeExecutionResultHandler(cer) : throw new InvalidOperationException("No handler for CodeExecutionResultPart"),
            ThoughtPart th => thoughtHandler != null ? thoughtHandler(th) : throw new InvalidOperationException("No handler for ThoughtPart"),
            FileDataPart fd => fileDataHandler != null ? fileDataHandler(fd) : throw new InvalidOperationException("No handler for FileDataPart"),
            _ => throw new InvalidOperationException("Unknown content part type")
        };
    }

    /// <summary>
    /// Gets the text content if this is a TextContentPart, otherwise null.
    /// </summary>
    public static string? AsText(this ContentPart part) =>
        part is TextContentPart textPart ? textPart.Text : null;

    /// <summary>
    /// Determines if this content part is text.
    /// </summary>
    public static bool IsText(this ContentPart part) => part is TextContentPart;

    /// <summary>
    /// Determines if this content part is a function call.
    /// </summary>
    public static bool IsFunctionCall(this ContentPart part) => part is FunctionCallPart;

    /// <summary>
    /// Determines if this content part is a function response.
    /// </summary>
    public static bool IsFunctionResponse(this ContentPart part) => part is FunctionResponsePart;
}

/// <summary>
/// Inline data for content parts.
/// </summary>
public record InlineData(string MimeType, string Data);

/// <summary>
/// Function call.
/// </summary>
public record FunctionCall
{
    public required string Name { get; init; }
    public required Dictionary<string, object?> Args { get; init; }
    public required string Id { get; init; }
}

/// <summary>
/// Function response.
/// </summary>
public record FunctionResponse
{
    public required string Name { get; init; }
    public required Dictionary<string, object?> Response { get; init; }
    public required string Id { get; init; }
}
