namespace AiCli.Core.Types;

/// <summary>
/// Represents a message in the conversation.
/// </summary>
public record ContentMessage
{
    /// <summary>
    /// The role of the message (user, model, system, function).
    /// </summary>
    public required LlmRole Role { get; init; }

    /// <summary>
    /// The content parts of the message.
    /// </summary>
    public required IReadOnlyList<ContentPart> Parts { get; init; }

    /// <summary>
    /// The name of the function (for function messages).
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Creates a new user message with text content.
    /// </summary>
    public static ContentMessage UserMessage(string text) => new()
    {
        Role = LlmRole.User,
        Parts = new[] { new TextContentPart(text) }
    };

    /// <summary>
    /// Creates a new model message with text content.
    /// </summary>
    public static ContentMessage ModelMessage(string text) => new()
    {
        Role = LlmRole.Model,
        Parts = new[] { new TextContentPart(text) }
    };

    /// <summary>
    /// Creates a new function response message.
    /// </summary>
    public static ContentMessage FunctionResponseMessage(string name, Dictionary<string, object?> response, string id) => new()
    {
        Role = LlmRole.Function,
        Name = name,
        Parts = new[] { new FunctionResponsePart(new FunctionResponse { Name = name, Response = response, Id = id }) }
    };

    /// <summary>
    /// Creates a new system message.
    /// </summary>
    public static ContentMessage SystemMessage(string text) => new()
    {
        Role = LlmRole.System,
        Parts = new[] { new TextContentPart(text) }
    };

    /// <summary>
    /// Gets all text content from the parts.
    /// </summary>
    public string GetText() => string.Join("", Parts.OfType<TextContentPart>().Select(t => t.Text));

    /// <summary>
    /// Gets all function calls from the parts.
    /// </summary>
    public IEnumerable<FunctionCall> GetFunctionCalls() => Parts.OfType<FunctionCallPart>().Select(p => new FunctionCall
    {
        Name = p.FunctionName,
        Args = p.Arguments,
        Id = p.Id
    });

    /// <summary>
    /// Checks if this message contains any function calls.
    /// </summary>
    public bool HasFunctionCalls() => Parts.OfType<FunctionCallPart>().Any();

    /// <summary>
    /// Checks if this message is empty or contains only whitespace text.
    /// </summary>
    public bool IsEmpty() => Parts.Count == 0 || (Parts.Count == 1 && Parts[0] is TextContentPart t && string.IsNullOrWhiteSpace(t.Text));
}
