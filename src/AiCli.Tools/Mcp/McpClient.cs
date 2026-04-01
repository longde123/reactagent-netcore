using AiCli.Core.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiCli.Core.Mcp;

/// <summary>
/// Client for a single MCP server.
/// Supports stdio and HTTP/SSE transports.
/// Ported from packages/core/src/tools/mcp-client.ts
/// </summary>
public class McpClient : IAsyncDisposable
{
    private static readonly ILogger Logger = LoggerHelper.ForContext<McpClient>();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _serverName;
    private readonly McpServerConfig _config;

    private McpServerStatus _status = McpServerStatus.Disconnected;
    private int _nextRequestId = 1;

    // Stdio transport state
    private Process? _process;
    private StreamWriter? _stdin;
    private Task? _readerTask;
    private CancellationTokenSource? _readerCts;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonRpcResponse>> _pending = new();

    // Discovered tools
    private IReadOnlyList<McpTool> _tools = Array.Empty<McpTool>();

    public string ServerName => _serverName;
    public McpServerStatus Status => _status;
    public IReadOnlyList<McpTool> Tools => _tools;

    public event EventHandler<McpServerStatus>? StatusChanged;
    public event EventHandler? ToolsUpdated;

    public McpClient(string serverName, McpServerConfig config)
    {
        _serverName = serverName;
        _config = config;
    }

    // ─── Connect ─────────────────────────────────────────────────────────────

    public async Task ConnectAsync(
        string clientName = "aicli-csharp",
        string clientVersion = "0.1.0",
        CancellationToken cancellationToken = default)
    {
        if (_status != McpServerStatus.Disconnected)
            throw new InvalidOperationException(
                $"Cannot connect: current status is {_status}");

        SetStatus(McpServerStatus.Connecting);

        try
        {
            switch (_config.Transport)
            {
                case McpTransportType.Stdio:
                    await ConnectStdioAsync(cancellationToken);
                    break;
                case McpTransportType.Sse:
                case McpTransportType.StreamableHttp:
                    throw new NotSupportedException(
                        $"Transport {_config.Transport} is not yet implemented. Use Stdio.");
                default:
                    throw new ArgumentOutOfRangeException(nameof(_config.Transport));
            }

            // Initialize MCP handshake
            await InitializeAsync(clientName, clientVersion, cancellationToken);

            SetStatus(McpServerStatus.Connected);
            Logger.Information("MCP server '{Name}' connected", _serverName);
        }
        catch (Exception ex)
        {
            SetStatus(McpServerStatus.Error);
            Logger.Error(ex, "Failed to connect to MCP server '{Name}'", _serverName);
            throw;
        }
    }

    // ─── Discover ─────────────────────────────────────────────────────────────

    public async Task DiscoverAsync(CancellationToken cancellationToken = default)
    {
        AssertConnected();

        var result = await SendRequestAsync<McpToolsListResult>(
            "tools/list", null, cancellationToken);

        _tools = result?.Tools ?? Array.Empty<McpTool>();

        Logger.Information("MCP server '{Name}': discovered {Count} tool(s)",
            _serverName, _tools.Count);

        ToolsUpdated?.Invoke(this, EventArgs.Empty);
    }

    // ─── Tool Call ────────────────────────────────────────────────────────────

    public async Task<McpCallToolResult> CallToolAsync(
        string toolName,
        Dictionary<string, object?>? arguments,
        CancellationToken cancellationToken = default)
    {
        AssertConnected();

        var @params = new McpCallToolParams
        {
            Name = toolName,
            Arguments = arguments,
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_config.TimeoutMs);

        var result = await SendRequestAsync<McpCallToolResult>(
            "tools/call", @params, timeout.Token);

        return result ?? new McpCallToolResult { Content = Array.Empty<McpContent>() };
    }

    // ─── Disconnect ───────────────────────────────────────────────────────────

    public async Task DisconnectAsync()
    {
        if (_status == McpServerStatus.Disconnected) return;

        SetStatus(McpServerStatus.Disconnecting);

        try
        {
            _readerCts?.Cancel();
            if (_readerTask != null)
            {
                try { await _readerTask; }
                catch { /* suppress */ }
            }
        }
        finally
        {
            CleanupProcess();
            SetStatus(McpServerStatus.Disconnected);
            Logger.Information("MCP server '{Name}' disconnected", _serverName);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }

    // ─── Stdio Transport ──────────────────────────────────────────────────────

    private async Task ConnectStdioAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_config.Command))
            throw new InvalidOperationException(
                $"MCP server '{_serverName}': command is required for stdio transport");

        var psi = new ProcessStartInfo
        {
            FileName = _config.Command,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _config.Cwd ?? Directory.GetCurrentDirectory(),
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };

        if (_config.Args != null)
            foreach (var arg in _config.Args)
                psi.ArgumentList.Add(arg);

        // Pass through environment variables
        if (_config.Env != null)
            foreach (var (k, v) in _config.Env)
                psi.Environment[k] = v;

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };

        _process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                Logger.Debug("[MCP stderr {Name}] {Line}", _serverName, e.Data);
        };

        _process.Exited += (_, _) =>
        {
            if (_status == McpServerStatus.Connected)
            {
                Logger.Warning("MCP server '{Name}' exited unexpectedly", _serverName);
                SetStatus(McpServerStatus.Disconnected);
            }
        };

        if (!_process.Start())
            throw new InvalidOperationException(
                $"Failed to start MCP server process: {_config.Command}");

        _process.BeginErrorReadLine();
        _stdin = _process.StandardInput;

        // Start background reader
        _readerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _readerTask = Task.Run(
            () => ReadResponsesAsync(_process.StandardOutput, _readerCts.Token),
            _readerCts.Token);

        await Task.CompletedTask;
    }

    private async Task ReadResponsesAsync(
        StreamReader reader, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line == null) break; // process ended

                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    ProcessIncomingLine(line);
                }
                catch (Exception ex)
                {
                    Logger.Debug(ex, "Error processing MCP response line");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.Warning(ex, "MCP reader for '{Name}' ended with error", _serverName);
        }
    }

    private void ProcessIncomingLine(string line)
    {
        var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;

        // Check for JSON-RPC response (has 'id')
        if (root.TryGetProperty("id", out var idEl))
        {
            var response = JsonSerializer.Deserialize<JsonRpcResponse>(line, JsonOptions);
            if (response != null)
            {
                var idStr = idEl.ValueKind == JsonValueKind.Number
                    ? idEl.GetInt32().ToString()
                    : idEl.GetString() ?? "";

                if (_pending.TryRemove(idStr, out var tcs))
                    tcs.TrySetResult(response);
            }
            return;
        }

        // Check for notification (has 'method' but no 'id')
        if (root.TryGetProperty("method", out var methodEl))
        {
            var method = methodEl.GetString() ?? "";
            Logger.Debug("MCP notification from '{Name}': {Method}", _serverName, method);

            if (method == "notifications/tools/list_changed")
                _ = Task.Run(() => DiscoverAsync());
        }
    }

    // ─── Request / Response ───────────────────────────────────────────────────

    private async Task InitializeAsync(
        string clientName, string clientVersion, CancellationToken cancellationToken)
    {
        var initParams = new McpInitializeParams
        {
            ClientInfo = new McpClientInfo { Name = clientName, Version = clientVersion },
            Capabilities = new McpCapabilities
            {
                Tools = new Dictionary<string, object?>(),
            },
        };

        var result = await SendRequestAsync<McpInitializeResult>(
            "initialize", initParams, cancellationToken);

        Logger.Debug("MCP '{Name}' initialized, server: {Server} {Version}",
            _serverName,
            result?.ServerInfo?.Name ?? "unknown",
            result?.ServerInfo?.Version ?? "");

        // Send initialized notification
        await SendNotificationAsync("notifications/initialized", cancellationToken);
    }

    private async Task SendNotificationAsync(string method, CancellationToken cancellationToken)
    {
        var notification = new { jsonrpc = "2.0", method };
        var json = JsonSerializer.Serialize(notification, JsonOptions);
        await WriteLineAsync(json, cancellationToken);
    }

    private async Task<T?> SendRequestAsync<T>(
        string method, object? @params, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextRequestId).ToString();

        var request = new JsonRpcRequest
        {
            Id = id,
            Method = method,
            Params = @params,
        };

        var json = JsonSerializer.Serialize(request, JsonOptions);
        var tcs = new TaskCompletionSource<JsonRpcResponse>();
        _pending[id] = tcs;

        using var reg = cancellationToken.Register(() =>
        {
            if (_pending.TryRemove(id, out _))
                tcs.TrySetCanceled(cancellationToken);
        });

        await WriteLineAsync(json, cancellationToken);

        var response = await tcs.Task;

        if (response.Error != null)
            throw new McpException(
                $"MCP error {response.Error.Code}: {response.Error.Message}",
                response.Error.Code);

        if (response.Result == null) return default;

        return JsonSerializer.Deserialize<T>(response.Result.Value.GetRawText(), JsonOptions);
    }

    private async Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        if (_stdin == null)
            throw new InvalidOperationException("Not connected via stdio");

        await _stdin.WriteLineAsync(line.AsMemory(), cancellationToken);
        await _stdin.FlushAsync(cancellationToken);
    }

    private void CleanupProcess()
    {
        _readerCts?.Cancel();
        _readerCts?.Dispose();
        _readerCts = null;

        try { _stdin?.Dispose(); } catch { }
        _stdin = null;

        try
        {
            if (_process != null && !_process.HasExited)
                _process.Kill(entireProcessTree: true);
            _process?.Dispose();
        }
        catch { }
        _process = null;

        // Cancel all pending requests
        foreach (var tcs in _pending.Values)
            tcs.TrySetException(new McpException("MCP client disconnected", -32000));
        _pending.Clear();
    }

    private void SetStatus(McpServerStatus status)
    {
        if (_status == status) return;
        _status = status;
        StatusChanged?.Invoke(this, status);
    }

    private void AssertConnected()
    {
        if (_status != McpServerStatus.Connected)
            throw new InvalidOperationException(
                $"MCP server '{_serverName}' is not connected (status: {_status})");
    }
}

/// <summary>
/// Exception thrown when an MCP server returns an error.
/// </summary>
public class McpException : Exception
{
    public int ErrorCode { get; }

    public McpException(string message, int errorCode = 0) : base(message)
    {
        ErrorCode = errorCode;
    }
}
