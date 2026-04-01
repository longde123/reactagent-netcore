using AiCli.Core.Logging;
using AiCli.Core.Types;

namespace AiCli.Core.Tools;

/// <summary>
/// Registry for managing available tools.
/// </summary>
public class ToolRegistry
{
    private readonly ILogger _logger;
    private readonly Dictionary<string, IToolBuilder> _tools;
    private readonly Dictionary<string, IToolBuilder> _discoveredTools;
    private readonly Dictionary<string, List<IToolBuilder>> _toolsByKind;

    /// <summary>
    /// Gets all registered tool names.
    /// </summary>
    public IReadOnlyList<string> AllToolNames => _tools.Keys.ToList();

    /// <summary>
    /// Gets all registered tools.
    /// </summary>
    public IReadOnlyList<IToolBuilder> AllTools => _tools.Values.ToList();

    /// <summary>
    /// Initializes a new instance of the ToolRegistry class.
    /// </summary>
    public ToolRegistry()
    {
        _logger = LoggerHelper.ForContext<ToolRegistry>();
        _tools = new Dictionary<string, IToolBuilder>(StringComparer.OrdinalIgnoreCase);
        _discoveredTools = new Dictionary<string, IToolBuilder>(StringComparer.OrdinalIgnoreCase);
        _toolsByKind = new Dictionary<string, List<IToolBuilder>>();
    }

    /// <summary>
    /// Registers a tool builder.
    /// </summary>
    /// <typeparam name="TParams">The type of parameters.</typeparam>
    /// <typeparam name="TResult">The type of result.</typeparam>
    /// <param name="tool">The tool builder to register.</param>
    public void RegisterTool<TParams, TResult>(IToolBuilder<TParams, TResult> tool)
        where TParams : class, new()
        where TResult : ToolExecutionResult
    {
        _tools[tool.Name] = tool;

        // Add to kind index
        if (!_toolsByKind.ContainsKey(tool.Kind.ToString()))
        {
            _toolsByKind[tool.Kind.ToString()] = new List<IToolBuilder>();
        }
        _toolsByKind[tool.Kind.ToString()].Add(tool);

        _logger.Debug("Registered tool: {ToolName} ({ToolKind})", tool.Name, tool.Kind);
    }

    /// <summary>
    /// Registers a discovered tool (from MCP or extension).
    /// </summary>
    public void RegisterDiscoveredTool(IToolBuilder tool)
    {
        _discoveredTools[tool.Name] = tool;

        // Also add to main registry
        _tools[tool.Name] = tool;

        // Add to kind index
        if (!_toolsByKind.ContainsKey(tool.Kind.ToString()))
        {
            _toolsByKind[tool.Kind.ToString()] = new List<IToolBuilder>();
        }
        _toolsByKind[tool.Kind.ToString()].Add(tool);

        _logger.Debug("Registered discovered tool: {ToolName} ({ToolKind})", tool.Name, tool.Kind);
    }

    /// <summary>
    /// Unregisters a tool.
    /// </summary>
    /// <param name="toolName">The name of the tool to unregister.</param>
    public bool UnregisterTool(string toolName)
    {
        if (!_tools.Remove(toolName))
        {
            return false;
        }

        _discoveredTools.Remove(toolName);

        // Rebuild kind index
        _toolsByKind.Clear();
        foreach (var tool in _tools.Values)
        {
            if (!_toolsByKind.ContainsKey(tool.Kind.ToString()))
            {
                _toolsByKind[tool.Kind.ToString()] = new List<IToolBuilder>();
            }
            _toolsByKind[tool.Kind.ToString()].Add(tool);
        }

        _logger.Debug("Unregistered tool: {ToolName}", toolName);
        return true;
    }

    /// <summary>
    /// Removes all discovered tools.
    /// </summary>
    public void RemoveDiscoveredTools()
    {
        foreach (var toolName in _discoveredTools.Keys)
        {
            UnregisterTool(toolName);
        }

        _logger.Debug("Removed all discovered tools");
    }

    /// <summary>
    /// Gets a tool by name.
    /// </summary>
    /// <param name="toolName">The name of the tool.</param>
    /// <returns>The tool builder if found, null otherwise.</returns>
    public IToolBuilder? GetTool(string toolName)
    {
        return _tools.TryGetValue(toolName, out var tool) ? tool : null;
    }

    /// <summary>
    /// Checks if a tool is registered.
    /// </summary>
    /// <param name="toolName">The name of the tool.</param>
    /// <returns>True if the tool is registered, false otherwise.</returns>
    public bool HasTool(string toolName) => _tools.ContainsKey(toolName);

    /// <summary>
    /// Gets all tools of a specific kind.
    /// </summary>
    /// <param name="kind">The kind of tools to get.</param>
    /// <returns>A list of tools of the specified kind.</returns>
    public IReadOnlyList<IToolBuilder> GetToolsByKind(ToolKind kind) =>
        _toolsByKind.TryGetValue(kind.ToString(), out var tools) ? tools : Array.Empty<IToolBuilder>();

    /// <summary>
    /// Gets all tool names of a specific kind.
    /// </summary>
    /// <param name="kind">The kind of tools to get.</param>
    /// <returns>A list of tool names of the specified kind.</returns>
    public IReadOnlyList<string> GetToolNamesByKind(ToolKind kind) =>
        _toolsByKind.TryGetValue(kind.ToString(), out var tools) ? tools.Select(t => t.Name).ToList() : Array.Empty<string>();

    /// <summary>
    /// Gets all tool schemas for the given model.
    /// </summary>
    /// <param name="modelId">Optional model identifier to get model-specific schemas.</param>
    /// <returns>A list of function declarations.</returns>
    public List<FunctionDeclaration> GetAllSchemas(string? modelId = null)
    {
        return _tools.Values
            .Select(t => t.GetSchema(modelId))
            .Where(s => s is not null)
            .ToList()!;
    }

    /// <summary>
    /// Gets the function declarations for use with the Gemini API.
    /// </summary>
    /// <param name="modelId">Optional model identifier.</param>
    /// <returns>A list of function declarations grouped by tools.</returns>
    public List<Tool> GetTools(string? modelId = null)
    {
        return new List<Tool>
        {
            new Tool
            {
                FunctionDeclarations = GetAllSchemas(modelId)
            }
        };
    }

    /// <summary>
    /// Gets read-only tools.
    /// </summary>
    public IReadOnlyList<IToolBuilder> GetReadOnlyTools() =>
        _tools.Values.Where(t => t.IsReadOnly).ToList();

    /// <summary>
    /// Gets write/modifying tools.
    /// </summary>
    public IReadOnlyList<IToolBuilder> GetModifyingTools() =>
        _tools.Values.Where(t => !t.IsReadOnly).ToList();

    /// <summary>
    /// Gets tools that require confirmation.
    /// </summary>
    public IReadOnlyList<IToolBuilder> GetToolsRequiringConfirmation() =>
        _tools.Values.Where(t => t.Kind.RequiresConfirmation()).ToList();

    /// <summary>
    /// Clears all registered tools.
    /// </summary>
    public void Clear()
    {
        _tools.Clear();
        _discoveredTools.Clear();
        _toolsByKind.Clear();
        _logger.Debug("Tool registry cleared");
    }

    /// <summary>
    /// Gets the count of registered tools.
    /// </summary>
    public int Count => _tools.Count;
}
