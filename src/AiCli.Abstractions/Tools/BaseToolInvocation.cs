using AiCli.Core.Types;

namespace AiCli.Core.Tools;

/// <summary>
/// Convenience base class for tool invocations.
/// </summary>
/// <typeparam name="TParams">The type of parameters.</typeparam>
/// <typeparam name="TResult">The type of result.</typeparam>
public abstract class BaseToolInvocation<TParams, TResult> : IToolInvocation<TParams, TResult>
    where TResult : ToolExecutionResult
{
    /// <summary>
    /// Gets the validated parameters.
    /// </summary>
    public TParams Parameters { get; }

    /// <summary>
    /// Gets the tool name.
    /// </summary>
    public virtual string ToolName { get; protected set; } = "";

    /// <summary>
    /// Gets the tool display name.
    /// </summary>
    protected virtual string ToolDisplayName { get; set; } = "";

    /// <summary>
    /// Gets the tool kind.
    /// </summary>
    protected abstract ToolKind Kind { get; }

    /// <summary>
    /// Initializes a new instance of the BaseToolInvocation class.
    /// </summary>
    protected BaseToolInvocation(TParams parameters)
    {
        Parameters = parameters;
    }

    /// <summary>
    /// Gets a description of the tool operation.
    /// </summary>
    public abstract string GetDescription();

    /// <summary>
    /// Gets the file locations affected by this tool.
    /// </summary>
    public virtual IReadOnlyList<ToolLocation> GetToolLocations() => Array.Empty<ToolLocation>();

    /// <summary>
    /// Checks if the tool should be confirmed before execution.
    /// </summary>
    public virtual Task<ToolCallConfirmationDetails?> ShouldConfirmExecuteAsync(CancellationToken cancellationToken)
    {
        // Default: always ask for non-read-only tools that require confirmation
        if (Kind.RequiresConfirmation())
        {
            return Task.FromResult<ToolCallConfirmationDetails?>(new ToolCallConfirmationDetails
            {
                ToolName = ToolName,
                Description = GetDescription(),
                Parameters = new Dictionary<string, object?>(),
                Kind = Kind,
                Locations = GetToolLocations().ToList(),
                IsReadOnly = IsReadOnly(),
                RequiresConfirmation = true
            });
        }

        return Task.FromResult<ToolCallConfirmationDetails?>(null);
    }

    /// <summary>
    /// Gets whether the tool is read-only.
    /// </summary>
    protected virtual bool IsReadOnly() => Kind.IsReadOnly();

    /// <summary>
    /// Executes the tool.
    /// </summary>
    public abstract Task<TResult> ExecuteAsync(
        CancellationToken cancellationToken,
        Action<ToolLiveOutput>? updateOutput = null);

    // Explicit implementations for the non-generic IToolInvocation interface
    object IToolInvocation.Parameters => Parameters!;

    async Task<ToolExecutionResult> IToolInvocation.ExecuteAsync(CancellationToken cancellationToken, Action<ToolLiveOutput>? updateOutput)
    {
        var res = await ExecuteAsync(cancellationToken, updateOutput).ConfigureAwait(false);
        return (ToolExecutionResult)(object)res!;
    }

    // Explicit implementations for the non-generic IToolInvocation interface
    // Provide explicit non-generic interface implementations by casting
    // no explicit non-generic implementations here; keep the generic contract only
}
