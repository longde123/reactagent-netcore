using AiCli.Core.Logging;
using AiCli.Core.Tools;
using AiCli.Core.Types;
using System.Text;
using System.Text.Json;

namespace AiCli.Core.Mcp;

/// <summary>
/// Separator used to qualify MCP tool names: "server__tool".
/// </summary>
public static class McpToolNames
{
    public const string Separator = "__";

    public static string Qualify(string serverName, string toolName) =>
        $"{serverName}{Separator}{toolName}";

    public static bool IsMcpToolName(string name) =>
        name.Contains(Separator, StringComparison.Ordinal)
        && name.Split(new[] { Separator }, 2, StringSplitOptions.None) is { Length: 2 } parts
        && parts[0].Length > 0 && parts[1].Length > 0;

    public static (string serverName, string toolName) Parse(string qualifiedName)
    {
        var parts = qualifiedName.Split(new[] { Separator }, 2, StringSplitOptions.None);
        if (parts.Length != 2) throw new ArgumentException($"Invalid MCP tool name: {qualifiedName}");
        return (parts[0], parts[1]);
    }
}

/// <summary>
/// A DeclarativeTool wrapper for a tool discovered from an MCP server.
/// Ported from packages/core/src/tools/mcp-tool.ts
/// </summary>
public class McpDiscoveredTool : DeclarativeTool<Dictionary<string, object?>, ToolExecutionResult>
{
    private readonly McpClient _client;
    private readonly string _mcpToolName;

    public string ServerName { get; }
    public string McpToolName => _mcpToolName;

    public McpDiscoveredTool(
        string serverName,
        McpTool mcpTool,
        McpClient client)
        : base(
            McpToolNames.Qualify(serverName, mcpTool.Name),
            McpToolNames.Qualify(serverName, mcpTool.Name),
            mcpTool.Description ?? $"MCP tool from server '{serverName}'",
            ToolKind.Other,
            BuildSchema(mcpTool))
    {
        ServerName = serverName;
        _mcpToolName = mcpTool.Name;
        _client = client;
    }

    protected override string? ValidateToolParams(Dictionary<string, object?> parameters) => null;

    public override IToolInvocation<Dictionary<string, object?>, ToolExecutionResult> Build(
        Dictionary<string, object?> parameters)
    {
        return new McpToolInvocation(parameters, ServerName, _mcpToolName, _client);
    }

    private static object BuildSchema(McpTool mcpTool)
    {
        if (mcpTool.InputSchema.HasValue)
        {
            try
            {
                var raw = mcpTool.InputSchema.Value.GetRawText();
                return JsonSerializer.Deserialize<object>(raw) ?? DefaultSchema();
            }
            catch { }
        }
        return DefaultSchema();
    }

    private static object DefaultSchema() => new
    {
        type = "object",
        properties = new Dictionary<string, object>(),
    };
}

/// <summary>
/// Invocation for an MCP tool call.
/// </summary>
public class McpToolInvocation : BaseToolInvocation<Dictionary<string, object?>, ToolExecutionResult>
{
    private static readonly ILogger Logger = LoggerHelper.ForContext<McpToolInvocation>();

    private readonly string _serverName;
    private readonly string _toolName;
    private readonly McpClient _client;

    public McpToolInvocation(
        Dictionary<string, object?> parameters,
        string serverName,
        string toolName,
        McpClient client) : base(parameters)
    {
        _serverName = serverName;
        _toolName = toolName;
        _client = client;
        ToolName = McpToolNames.Qualify(serverName, toolName);
        ToolDisplayName = ToolName;
    }

    protected override ToolKind Kind => ToolKind.Other;

    public override string GetDescription() =>
        $"Call MCP tool '{_toolName}' on server '{_serverName}'";

    public override IReadOnlyList<ToolLocation> GetToolLocations() =>
        Array.Empty<ToolLocation>();

    public override async Task<ToolExecutionResult> ExecuteAsync(
        CancellationToken cancellationToken,
        Action<ToolLiveOutput>? updateOutput = null)
    {
        try
        {
            Logger.Debug("Calling MCP tool '{Tool}' on server '{Server}'",
                _toolName, _serverName);

            var result = await _client.CallToolAsync(_toolName, Parameters, cancellationToken);

            if (result.IsError == true)
            {
                var errorText = ExtractText(result.Content);
                return ToolExecutionResult.Failure(errorText, ToolErrorType.Unknown);
            }

            var content = ExtractText(result.Content);
            return new ToolExecutionResult
            {
                LlmContent = new TextContentPart(content),
                ReturnDisplay = new TextToolResultDisplay(content),
            };
        }
        catch (McpException ex)
        {
            Logger.Warning(ex, "MCP tool '{Tool}' error", _toolName);
            return ToolExecutionResult.Failure(ex.Message, ToolErrorType.Unknown);
        }
        catch (OperationCanceledException)
        {
            return ToolExecutionResult.Failure("MCP tool call cancelled.", ToolErrorType.Cancellation);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Unexpected error calling MCP tool '{Tool}'", _toolName);
            return ToolExecutionResult.Failure(ex.Message, ToolErrorType.Unknown);
        }
    }

    private static string ExtractText(IReadOnlyList<McpContent>? content)
    {
        if (content == null || content.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        foreach (var block in content)
        {
            switch (block.Type)
            {
                case "text":
                    sb.AppendLine(block.Text);
                    break;
                case "resource":
                    if (!string.IsNullOrEmpty(block.Text))
                        sb.AppendLine(block.Text);
                    break;
                case "resource_link":
                    sb.AppendLine($"[Resource: {block.Title ?? block.Uri}]({block.Uri})");
                    break;
                case "image":
                case "audio":
                    sb.AppendLine($"[Binary content: {block.Type} ({block.MimeType})]");
                    break;
                default:
                    if (!string.IsNullOrEmpty(block.Text))
                        sb.AppendLine(block.Text);
                    break;
            }
        }

        return sb.ToString().TrimEnd();
    }
}
