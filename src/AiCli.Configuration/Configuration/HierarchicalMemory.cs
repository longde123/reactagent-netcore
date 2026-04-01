namespace AiCli.Core.Configuration;

/// <summary>
/// Hierarchical memory with global, extension, and project levels.
/// </summary>
public record HierarchicalMemory
{
    /// <summary>
    /// Global memory (applies to all projects).
    /// </summary>
    public string? Global { get; init; }

    /// <summary>
    /// Extension-specific memory.
    /// </summary>
    public string? Extension { get; init; }

    /// <summary>
    /// Project-specific memory.
    /// </summary>
    public string? Project { get; init; }

    /// <summary>
    /// Creates a new hierarchical memory with the specified values.
    /// </summary>
    public static HierarchicalMemory Create(string? global = null, string? extension = null, string? project = null) =>
        new() { Global = global, Extension = extension, Project = project };

    /// <summary>
    /// Flattens the hierarchical memory into a single string.
    /// </summary>
    public string Flatten()
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(Global))
        {
            parts.Add($"# Global Memory\n{Global}");
        }

        if (!string.IsNullOrWhiteSpace(Extension))
        {
            parts.Add($"# Extension Memory\n{Extension}");
        }

        if (!string.IsNullOrWhiteSpace(Project))
        {
            parts.Add($"# Project Memory\n{Project}");
        }

        return parts.Count > 0 ? string.Join("\n\n---\n\n", parts) : "";
    }

    /// <summary>
    /// Checks if the memory is empty.
    /// </summary>
    public bool IsEmpty() => string.IsNullOrWhiteSpace(Global) &&
                           string.IsNullOrWhiteSpace(Extension) &&
                           string.IsNullOrWhiteSpace(Project);

    /// <summary>
    /// Updates a specific level of memory.
    /// </summary>
    public HierarchicalMemory WithGlobal(string? global) => this with { Global = global };
    public HierarchicalMemory WithExtension(string? extension) => this with { Extension = extension };
    public HierarchicalMemory WithProject(string? project) => this with { Project = project };
}

/// <summary>
/// Memory level enum.
/// </summary>
public enum MemoryLevel
{
    /// <summary>
    /// Global memory.
    /// </summary>
    Global,

    /// <summary>
    /// Extension memory.
    /// </summary>
    Extension,

    /// <summary>
    /// Project memory.
    /// </summary>
    Project
}
