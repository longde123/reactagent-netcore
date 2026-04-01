namespace AiCli.Core.Types;

/// <summary>
/// Represents a function/tool declaration for schema generation.
/// </summary>
public record FunctionDeclaration
{
    /// <summary>
    /// The name of the function.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The description of the function.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// The parameters schema for the function.
    /// </summary>
    public FunctionParameters? Parameters { get; init; }
}

/// <summary>
/// Function parameters schema.
/// </summary>
public record FunctionParameters
{
    /// <summary>
    /// The type of the parameters (always "object").
    /// </summary>
    public string Type { get; init; } = "object";

    /// <summary>
    /// The required parameter names.
    /// </summary>
    public IReadOnlyList<string>? Required { get; init; }

    /// <summary>
    /// The property definitions.
    /// </summary>
    public Dictionary<string, PropertySchema>? Properties { get; init; }
}

/// <summary>
/// Property schema definition.
/// </summary>
public record PropertySchema
{
    /// <summary>
    /// The type of the property.
    /// </summary>
    public string? Type { get; init; }

    /// <summary>
    /// The description of the property.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// The enum values for this property.
    /// </summary>
    public IReadOnlyList<string>? Enum { get; init; }

    /// <summary>
    /// The items schema (for array types).
    /// </summary>
    public PropertySchema? Items { get; init; }

    /// <summary>
    /// Whether this property allows null values.
    /// </summary>
    public bool? Nullable { get; init; }

    /// <summary>
    /// The default value for this property.
    /// </summary>
    public object? Default { get; init; }

    /// <summary>
    /// The minimum value for numeric types.
    /// </summary>
    public double? Minimum { get; init; }

    /// <summary>
    /// The maximum value for numeric types.
    /// </summary>
    public double? Maximum { get; init; }

    /// <summary>
    /// The minimum length for string types.
    /// </summary>
    public int? MinLength { get; init; }

    /// <summary>
    /// The maximum length for string types.
    /// </summary>
    public int? MaxLength { get; init; }
}

/// <summary>
/// Represents a tool that can be called by the model.
/// </summary>
public record Tool
{
    /// <summary>
    /// Function declarations for this tool.
    /// </summary>
    public IReadOnlyList<FunctionDeclaration>? FunctionDeclarations { get; init; }
}
