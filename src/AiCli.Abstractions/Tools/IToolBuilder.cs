using AiCli.Core.Types;

namespace AiCli.Core.Tools;

/// <summary>
/// Non-generic tool builder used for registry and discovery.
/// </summary>
public interface IToolBuilder
{
    string Name { get; }
    string DisplayName { get; }
    string Description { get; }
    ToolKind Kind { get; }
    FunctionDeclaration GetSchema(string? modelId = null);
    bool IsOutputMarkdown { get; }
    bool CanUpdateOutput { get; }
    bool IsReadOnly { get; }

    /// <summary>
    /// Build an untyped invocation from raw parameters.
    /// </summary>
    IToolInvocation Build(object parameters);
}

/// <summary>
/// Interface for a tool builder that validates parameters and creates invocations.
/// </summary>
/// <typeparam name="TParams">The type of parameters.</typeparam>
/// <typeparam name="TResult">The type of result (must be ToolExecutionResult or derive from it).</typeparam>
public interface IToolBuilder<TParams, TResult> : IToolBuilder
    where TParams : class
    where TResult : ToolExecutionResult
{
    new string Name { get; }
    new string DisplayName { get; }
    new string Description { get; }
    new ToolKind Kind { get; }
    new FunctionDeclaration GetSchema(string? modelId = null);
    new bool IsOutputMarkdown { get; }
    new bool CanUpdateOutput { get; }
    new bool IsReadOnly { get; }

    /// <summary>
    /// Validates raw parameters and builds a ready-to-execute invocation.
    /// </summary>
    /// <param name="parameters">The raw, untrusted parameters from the model.</param>
    /// <returns>A valid <see cref="IToolInvocation{TParams, TResult}"/> if successful.
    /// Throws an exception if validation fails.</returns>
    IToolInvocation<TParams, TResult> Build(TParams parameters);

    // Explicit non-generic build implementation required by IToolBuilder
    IToolInvocation IToolBuilder.Build(object parameters)
    {
        if (parameters is TParams typed)
        {
            return Build(typed);
        }

        // Attempt to convert from dictionary to TParams if possible (common usage)
        if (parameters is Dictionary<string, object?> dict)
        {
            // Normalize keys to PascalCase to support snake_case keys coming from models
            Dictionary<string, object?> NormalizeKeys(Dictionary<string, object?> src)
            {
                string ToPascal(string s)
                {
                    if (string.IsNullOrEmpty(s)) return s;
                    var parts = s.Split(new[] { '_', '-', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    for (int i = 0; i < parts.Length; i++)
                    {
                        var p = parts[i];
                        if (p.Length == 0) continue;
                        parts[i] = char.ToUpperInvariant(p[0]) + (p.Length > 1 ? p.Substring(1) : string.Empty);
                    }
                    return string.Join(string.Empty, parts);
                }

                var outDict = new Dictionary<string, object?>();
                foreach (var kv in src)
                {
                    var newKey = ToPascal(kv.Key);
                    outDict[newKey] = kv.Value;
                }
                return outDict;
            }

            try
            {
                var normalized = NormalizeKeys(dict);
                var json = System.Text.Json.JsonSerializer.Serialize(normalized);
                var obj = System.Text.Json.JsonSerializer.Deserialize<TParams>(json);
                if (obj is not null) return Build(obj);
            }
            catch (Exception)
            {
                // fallthrough to original error below
            }
        }

        throw new InvalidCastException($"Unable to build tool invocation from parameters of type {parameters?.GetType().FullName}");
    }
}
