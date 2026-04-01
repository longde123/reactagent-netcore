using AiCli.Core.Logging;
using AiCli.Core.Tools;

namespace AiCli.Core.Agents;

/// <summary>
/// Registry for managing agents.
/// </summary>
public class AgentRegistry
{
    private readonly ILogger _logger;
    private readonly Dictionary<string, IAgent> _agents = new();
    private readonly Dictionary<string, AgentConfig> _agentConfigs = new();
    private readonly ToolRegistry _toolRegistry;
    private readonly object _lock = new();

    /// <summary>
    /// Gets all registered agent IDs.
    /// </summary>
    public IEnumerable<string> AgentIds
    {
        get
        {
            lock (_lock)
            {
                return _agents.Keys.ToList();
            }
        }
    }

    /// <summary>
    /// Gets the number of registered agents.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _agents.Count;
            }
        }
    }

    /// <summary>
    /// Event raised when an agent is registered.
    /// </summary>
    public event EventHandler<AgentRegisteredEventArgs>? AgentRegistered;

    /// <summary>
    /// Event raised when an agent is unregistered.
    /// </summary>
    public event EventHandler<AgentUnregisteredEventArgs>? AgentUnregistered;

    public AgentRegistry(ToolRegistry toolRegistry)
    {
        _logger = LoggerHelper.ForContext<AgentRegistry>();
        _toolRegistry = toolRegistry;
    }

    /// <summary>
    /// Registers an agent.
    /// </summary>
    public void RegisterAgent(IAgent agent)
    {
        lock (_lock)
        {
            if (_agents.ContainsKey(agent.Id))
            {
                throw new InvalidOperationException($"Agent already registered: {agent.Id}");
            }

            _agents[agent.Id] = agent;

            // Register agent's tools if any
            foreach (var toolName in agent.ToolRegistry.AllToolNames)
            {
                var tool = agent.ToolRegistry.GetTool(toolName);
                if (tool != null)
                {
                    _toolRegistry.RegisterDiscoveredTool(tool);
                }
            }
        }

        _logger.Information("Registered agent: {Name} ({Id})", agent.Name, agent.Id);
        AgentRegistered?.Invoke(this, new AgentRegisteredEventArgs { Agent = agent });
    }

    /// <summary>
    /// Registers an agent configuration.
    /// </summary>
    public void RegisterAgentConfig(AgentConfig config)
    {
        lock (_lock)
        {
            _agentConfigs[config.Name] = config;
        }

        _logger.Information("Registered agent config: {Name}", config.Name);
    }

    /// <summary>
    /// Unregisters an agent.
    /// </summary>
    public bool UnregisterAgent(string agentId)
    {
        lock (_lock)
        {
            if (!_agents.TryGetValue(agentId, out var agent))
            {
                return false;
            }

            _agents.Remove(agentId);
            agent.OnEvent -= (s, e) => { };

            _logger.Information("Unregistered agent: {Name} ({Id})", agent.Name, agentId);
            AgentUnregistered?.Invoke(this, new AgentUnregisteredEventArgs { Agent = agent });

            return true;
        }
    }

    /// <summary>
    /// Gets an agent by ID.
    /// </summary>
    public IAgent? GetAgent(string agentId)
    {
        lock (_lock)
        {
            return _agents.TryGetValue(agentId, out var agent) ? agent : null;
        }
    }

    /// <summary>
    /// Gets an agent by name.
    /// </summary>
    public IAgent? GetAgentByName(string name)
    {
        lock (_lock)
        {
            return _agents.Values.FirstOrDefault(a => a.Name == name);
        }
    }

    /// <summary>
    /// Gets an agent configuration by name.
    /// </summary>
    public AgentConfig? GetAgentConfig(string name)
    {
        lock (_lock)
        {
            return _agentConfigs.TryGetValue(name, out var config) ? config : null;
        }
    }

    /// <summary>
    /// Gets all agents.
    /// </summary>
    public List<IAgent> GetAllAgents()
    {
        lock (_lock)
        {
            return _agents.Values.ToList();
        }
    }

    /// <summary>
    /// Gets all agent configurations.
    /// </summary>
    public List<AgentConfig> GetAllConfigs()
    {
        lock (_lock)
        {
            return _agentConfigs.Values.ToList();
        }
    }

    /// <summary>
    /// Finds agents by kind.
    /// </summary>
    public List<IAgent> FindAgentsByKind(AgentKind kind)
    {
        lock (_lock)
        {
            return _agents.Values.Where(a => a.Kind == kind).ToList();
        }
    }

    /// <summary>
    /// Finds agents by capability.
    /// </summary>
    public List<IAgent> FindAgentsByCapability(string capability)
    {
        lock (_lock)
        {
            return _agents.Values
                .Where(a => a.Capabilities.Contains(capability))
                .ToList();
        }
    }

    /// <summary>
    /// Checks if an agent is registered.
    /// </summary>
    public bool HasAgent(string agentId)
    {
        lock (_lock)
        {
            return _agents.ContainsKey(agentId);
        }
    }

    /// <summary>
    /// Gets agents matching capabilities.
    /// </summary>
    public List<IAgent> GetAgentsMatchingCapabilities(List<string> requiredCapabilities)
    {
        lock (_lock)
        {
            return _agents.Values
                .Where(a => requiredCapabilities.All(c => a.Capabilities.Contains(c)))
                .ToList();
        }
    }

    /// <summary>
    /// Clears all registered agents.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _agents.Clear();
            _agentConfigs.Clear();
        }

        _logger.Information("Cleared all agents from registry");
    }
}

/// <summary>
/// Event arguments for agent registration.
/// </summary>
public class AgentRegisteredEventArgs : EventArgs
{
    public required IAgent Agent { get; init; }
}

/// <summary>
/// Event arguments for agent unregistration.
/// </summary>
public class AgentUnregisteredEventArgs : EventArgs
{
    public required IAgent Agent { get; init; }
}
