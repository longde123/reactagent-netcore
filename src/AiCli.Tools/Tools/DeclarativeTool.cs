using AiCli.Core.Types;
using System.Text.Json;

namespace AiCli.Core.Tools;

/// <summary>
/// Base class for tools that separate validation from execution.
/// New tools should extend this class.
/// </summary>
/// <typeparam name="TParams">The type of parameters.</typeparam>
/// <typeparam name="TResult">The type of result.</typeparam>
public abstract class DeclarativeTool<TParams, TResult> : IToolBuilder<TParams, TResult>
    where TParams : class
    where TResult : ToolExecutionResult
{
    /// <summary>
    /// Gets the parameter schema for this tool.
    /// </summary>
    protected object ParameterSchema { get; }

    /// <summary>
    /// Gets the tool name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the display name.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// Gets the description.
    /// </summary>
    public string Description { get; }

    /// <summary>
    /// Gets the tool kind.
    /// </summary>
    public ToolKind Kind { get; }

    /// <summary>
    /// Gets whether output is markdown.
    /// </summary>
    public bool IsOutputMarkdown { get; }

    /// <summary>
    /// Gets whether output can be updated (streaming).
    /// </summary>
    public bool CanUpdateOutput { get; }

    /// <summary>
    /// Gets whether the tool is read-only.
    /// </summary>
    public bool IsReadOnly => Kind.IsReadOnly();

    /// <summary>
    /// Gets the extension name (if this tool is from an extension).
    /// </summary>
    public string? ExtensionName { get; }

    /// <summary>
    /// Gets the extension ID (if this tool is from an extension).
    /// </summary>
    public string? ExtensionId { get; }

    /// <summary>
    /// Initializes a new instance of the DeclarativeTool class.
    /// </summary>
    protected DeclarativeTool(
        string name,
        string displayName,
        string description,
        ToolKind kind,
        object parameterSchema,
        bool isOutputMarkdown = true,
        bool canUpdateOutput = false,
        string? extensionName = null,
        string? extensionId = null)
    {
        Name = name;
        DisplayName = displayName;
        Description = description;
        Kind = kind;
        ParameterSchema = parameterSchema;
        IsOutputMarkdown = isOutputMarkdown;
        CanUpdateOutput = canUpdateOutput;
        ExtensionName = extensionName;
        ExtensionId = extensionId;
    }

    /// <summary>
    /// Gets the function declaration schema.
    /// </summary>
    public virtual FunctionDeclaration GetSchema(string? modelId = null)
    {
        return new FunctionDeclaration
        {
            Name = Name,
            Description = Description,
            Parameters = JsonToFunctionParameters()
        };
    }

    /// <summary>
    /// Validates raw tool parameters.
    /// Subclasses can override this to add custom validation logic
    /// beyond JSON schema check.
    /// </summary>
    /// <param name="parameters">The raw parameters from the model.</param>
    /// <returns>An error message string if invalid, null otherwise.</returns>
    protected virtual string? ValidateToolParams(TParams parameters)
    {
        // Base implementation can be extended by subclasses.
        return null;
    }

    /// <summary>
    /// Converts the parameter schema to a FunctionParameters object.
    /// </summary>
    private FunctionParameters? JsonToFunctionParameters()
    {
        try
        {
            var json = JsonSerializer.Serialize(ParameterSchema);
            return JsonSerializer.Deserialize<FunctionParameters>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The core of the pattern. It validates parameters and, if successful,
    /// returns a <see cref="IToolInvocation{TParams, TResult}"/> object that encapsulates
    /// logic for the specific, validated call.
    /// </summary>
    public abstract IToolInvocation<TParams, TResult> Build(TParams parameters);

    /// <summary>
    /// Validates and builds in one step.
    /// </summary>
    public IToolInvocation<TParams, TResult> SafeBuild(TParams parameters)
    {
        var validationError = ValidateToolParams(parameters);
        if (validationError is not null)
        {
            throw new InvalidOperationException($"Invalid parameters: {validationError}");
        }

        return Build(parameters);
    }

    /// <summary>
    /// A convenience method that builds and executes the tool in one step.
    /// Throws an error if validation fails.
    /// </summary>
    public async Task<TResult> BuildAndExecuteAsync(
        TParams parameters,
        CancellationToken cancellationToken,
        Action<ToolLiveOutput>? updateOutput = null)
    {
        var invocation = SafeBuild(parameters);
        return await invocation.ExecuteAsync(cancellationToken, updateOutput);
    }
}
