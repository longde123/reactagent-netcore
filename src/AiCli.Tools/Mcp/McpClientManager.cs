using AiCli.Core.Logging;
using AiCli.Core.Tools;

namespace AiCli.Core.Mcp;

/// <summary>
/// Manages connections to multiple MCP servers and registers their tools.
/// Ported from packages/core/src/tools/mcp-client-manager.ts
/// </summary>
public class McpClientManager : IAsyncDisposable
{
    private static readonly ILogger Logger = LoggerHelper.ForContext<McpClientManager>();

    private readonly Dictionary<string, McpServerConfig> _serverConfigs;
    private readonly ToolRegistry _toolRegistry;
    private readonly Dictionary<string, McpClient> _clients = new();
    private readonly string _clientName;
    private readonly string _clientVersion;

    public McpClientManager(
        Dictionary<string, McpServerConfig> serverConfigs,
        ToolRegistry toolRegistry,
        string clientName = "aicli-csharp",
        string clientVersion = "0.1.0")
    {
        _serverConfigs = serverConfigs;
        _toolRegistry = toolRegistry;
        _clientName = clientName;
        _clientVersion = clientVersion;
    }

    // ─── Lifecycle ────────────────────────────────────────────────────────────

    /// <summary>
    /// Connects to all configured MCP servers and discovers their tools.
    /// Failures are logged but do not prevent other servers from connecting.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var tasks = _serverConfigs
            .Where(kvp => kvp.Value.Enabled)
            .Select(kvp => ConnectServerAsync(kvp.Key, kvp.Value, cancellationToken))
            .ToList();

        await Task.WhenAll(tasks);

        Logger.Information("MCP initialization complete: {Connected}/{Total} servers connected",
            _clients.Count(c => c.Value.Status == McpServerStatus.Connected),
            _serverConfigs.Count(c => c.Value.Enabled));
    }

    /// <summary>
    /// Connects to a single MCP server and registers its tools.
    /// </summary>
    public async Task ConnectServerAsync(
        string serverName,
        McpServerConfig config,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var client = new McpClient(serverName, config);
            client.ToolsUpdated += (_, _) =>
                _ = Task.Run(() => RefreshToolsAsync(serverName, client, cancellationToken));

            await client.ConnectAsync(_clientName, _clientVersion, cancellationToken);
            await client.DiscoverAsync(cancellationToken);

            _clients[serverName] = client;
            RegisterServerTools(serverName, client);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to connect to MCP server '{Name}'", serverName);
        }
    }

    /// <summary>
    /// Disconnects a server and unregisters its tools.
    /// </summary>
    public async Task DisconnectServerAsync(string serverName)
    {
        if (!_clients.TryGetValue(serverName, out var client)) return;

        UnregisterServerTools(serverName, client);
        await client.DisconnectAsync();
        _clients.Remove(serverName);
    }

    // ─── Server Status ────────────────────────────────────────────────────────

    public McpServerStatus GetServerStatus(string serverName) =>
        _clients.TryGetValue(serverName, out var client)
            ? client.Status
            : McpServerStatus.Disconnected;

    public IReadOnlyDictionary<string, McpServerStatus> GetAllServerStatuses() =>
        _clients.ToDictionary(c => c.Key, c => c.Value.Status);

    public IReadOnlyList<McpDiscoveredTool> GetDiscoveredTools() =>
        _clients.Values
            .SelectMany(c => c.Tools
                .Select(t => new McpDiscoveredTool(c.ServerName, t, c)))
            .ToList();

    // ─── Tool Registration ────────────────────────────────────────────────────

    private void RegisterServerTools(string serverName, McpClient client)
    {
        foreach (var mcpTool in client.Tools)
        {
            var tool = new McpDiscoveredTool(serverName, mcpTool, client);
            _toolRegistry.RegisterDiscoveredTool(tool);
            Logger.Debug("Registered MCP tool: {Name}", tool.Name);
        }
    }

    private async Task RefreshToolsAsync(
        string serverName, McpClient client, CancellationToken cancellationToken)
    {
        try
        {
            UnregisterServerTools(serverName, client);
            await client.DiscoverAsync(cancellationToken);
            RegisterServerTools(serverName, client);
            Logger.Information("Refreshed tools for MCP server '{Name}'", serverName);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to refresh tools for MCP server '{Name}'", serverName);
        }
    }

    private void UnregisterServerTools(string serverName, McpClient client)
    {
        foreach (var mcpTool in client.Tools)
        {
            var qualifiedName = McpToolNames.Qualify(serverName, mcpTool.Name);
            _toolRegistry.UnregisterTool(qualifiedName);
        }
    }

    // ─── Dispose ─────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        var disconnectTasks = _clients.Values
            .Select(c => c.DisposeAsync().AsTask())
            .ToList();

        await Task.WhenAll(disconnectTasks);
        _clients.Clear();
    }
}
