using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiCli.Core.Mcp;

// ─── Server Configuration ────────────────────────────────────────────────────

/// <summary>
/// Transport type for MCP server connection.
/// </summary>
public enum McpTransportType
{
    Stdio,
    Sse,
    StreamableHttp,
}

/// <summary>
/// Configuration for an MCP server connection.
/// </summary>
public record McpServerConfig
{
    /// <summary>
    /// Transport type: stdio (default), sse, or streamable_http.
    /// </summary>
    public McpTransportType Transport { get; init; } = McpTransportType.Stdio;

    // Stdio transport
    public string? Command { get; init; }
    public IReadOnlyList<string>? Args { get; init; }
    public Dictionary<string, string>? Env { get; init; }
    public string? Cwd { get; init; }

    // HTTP transports
    public string? Url { get; init; }
    public Dictionary<string, string>? Headers { get; init; }

    /// <summary>
    /// Timeout in milliseconds for tool calls (default: 10 minutes).
    /// </summary>
    public int TimeoutMs { get; init; } = 10 * 60 * 1000;

    /// <summary>
    /// Whether this server is enabled.
    /// </summary>
    public bool Enabled { get; init; } = true;
}

// ─── JSON-RPC 2.0 Types ───────────────────────────────────────────────────────

public class JsonRpcRequest
{
    [JsonPropertyName("jsonrpc")] public string JsonRpc { get; init; } = "2.0";
    [JsonPropertyName("id")]      public object? Id { get; set; }
    [JsonPropertyName("method")]  public required string Method { get; init; }
    [JsonPropertyName("params")]  public object? Params { get; init; }
}

public class JsonRpcResponse
{
    [JsonPropertyName("jsonrpc")] public string? JsonRpc { get; init; }
    [JsonPropertyName("id")]      public object? Id { get; init; }
    [JsonPropertyName("result")]  public JsonElement? Result { get; init; }
    [JsonPropertyName("error")]   public JsonRpcError? Error { get; init; }
}

public class JsonRpcError
{
    [JsonPropertyName("code")]    public int Code { get; init; }
    [JsonPropertyName("message")] public required string Message { get; init; }
    [JsonPropertyName("data")]    public JsonElement? Data { get; init; }
}

public class JsonRpcNotification
{
    [JsonPropertyName("jsonrpc")] public string? JsonRpc { get; init; }
    [JsonPropertyName("method")]  public required string Method { get; init; }
    [JsonPropertyName("params")]  public JsonElement? Params { get; init; }
}

// ─── MCP Protocol Types ───────────────────────────────────────────────────────

public record McpCapabilities
{
    public Dictionary<string, object?>? Tools { get; init; }
    public Dictionary<string, object?>? Resources { get; init; }
    public Dictionary<string, object?>? Prompts { get; init; }
}

public record McpClientInfo
{
    [JsonPropertyName("name")]    public required string Name { get; init; }
    [JsonPropertyName("version")] public required string Version { get; init; }
}

public record McpInitializeParams
{
    [JsonPropertyName("protocolVersion")] public string ProtocolVersion { get; init; } = "2024-11-05";
    [JsonPropertyName("capabilities")]    public McpCapabilities Capabilities { get; init; } = new();
    [JsonPropertyName("clientInfo")]      public required McpClientInfo ClientInfo { get; init; }
}

public record McpInitializeResult
{
    [JsonPropertyName("protocolVersion")] public string? ProtocolVersion { get; init; }
    [JsonPropertyName("capabilities")]    public McpCapabilities? Capabilities { get; init; }
    [JsonPropertyName("serverInfo")]      public McpClientInfo? ServerInfo { get; init; }
}

// ─── Tools ────────────────────────────────────────────────────────────────────

public record McpTool
{
    [JsonPropertyName("name")]        public required string Name { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("inputSchema")] public JsonElement? InputSchema { get; init; }
    [JsonPropertyName("annotations")] public Dictionary<string, object?>? Annotations { get; init; }
}

public record McpToolsListResult
{
    [JsonPropertyName("tools")]      public required IReadOnlyList<McpTool> Tools { get; init; }
    [JsonPropertyName("nextCursor")] public string? NextCursor { get; init; }
}

public record McpCallToolParams
{
    [JsonPropertyName("name")]      public required string Name { get; init; }
    [JsonPropertyName("arguments")] public Dictionary<string, object?>? Arguments { get; init; }
}

public record McpContent
{
    [JsonPropertyName("type")]     public required string Type { get; init; }
    [JsonPropertyName("text")]     public string? Text { get; init; }
    [JsonPropertyName("mimeType")] public string? MimeType { get; init; }
    [JsonPropertyName("data")]     public string? Data { get; init; }
    [JsonPropertyName("uri")]      public string? Uri { get; init; }
    [JsonPropertyName("title")]    public string? Title { get; init; }
}

public record McpCallToolResult
{
    [JsonPropertyName("content")]   public IReadOnlyList<McpContent>? Content { get; init; }
    [JsonPropertyName("isError")]   public bool? IsError { get; init; }
}

// ─── Server Status ────────────────────────────────────────────────────────────

public enum McpServerStatus
{
    Disconnected,
    Connecting,
    Connected,
    Disconnecting,
    Error,
}
