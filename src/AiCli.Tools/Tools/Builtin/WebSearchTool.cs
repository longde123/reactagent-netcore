using AiCli.Core.Logging;
using AiCli.Core.Types;

namespace AiCli.Core.Tools.Builtin;

/// <summary>
/// Parameters for WebSearch tool.
/// </summary>
public record WebSearchToolParams
{
    /// <summary>
    /// The search query.
    /// </summary>
    public required string Query { get; init; }

    /// <summary>
    /// Maximum number of results to return (default: 10).
    /// </summary>
    public int? MaxResults { get; init; } = 10;
}

/// <summary>
/// Implementation of WebSearch tool for Google web search.
/// </summary>
public class WebSearchTool : DeclarativeTool<WebSearchToolParams, ToolExecutionResult>
{
    public const string ToolName = "web_search";
    public const string DisplayName = "Google Search";
    public const string Description = "Search the web using Google Search.";

    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the WebSearchTool class.
    /// </summary>
    public WebSearchTool()
        : base(
            ToolName,
            DisplayName,
            Description,
            ToolKind.Search,
            GetParameterSchema())
    {
        _logger = LoggerHelper.ForContext<WebSearchTool>();
    }

    /// <summary>
    /// Gets the parameter schema for this tool.
    /// </summary>
    private static object GetParameterSchema()
    {
        return new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                {
                    "query",
                    new
                    {
                        type = "string",
                        description = "The search query."
                    }
                },
                {
                    "max_results",
                    new
                    {
                        type = "integer",
                        description = "Maximum number of results to return. Defaults to 10."
                    }
                }
            },
            required = new[] { "query" }
        };
    }

    /// <summary>
    /// Validates the tool parameters.
    /// </summary>
    protected override string? ValidateToolParams(WebSearchToolParams parameters)
    {
        if (string.IsNullOrWhiteSpace(parameters.Query))
        {
            return "The 'query' parameter must not be empty.";
        }

        if (parameters.MaxResults.HasValue && parameters.MaxResults.Value < 1)
        {
            return "The 'max_results' must be at least 1.";
        }

        return null;
    }

    /// <summary>
    /// Creates a tool invocation.
    /// </summary>
    public override IToolInvocation<WebSearchToolParams, ToolExecutionResult> Build(WebSearchToolParams parameters)
    {
        return new WebSearchToolInvocation(parameters, _logger);
    }
}

/// <summary>
/// Invocation for the WebSearch tool.
/// </summary>
public class WebSearchToolInvocation : BaseToolInvocation<WebSearchToolParams, ToolExecutionResult>
{
    private readonly ILogger _logger;

    public WebSearchToolInvocation(
        WebSearchToolParams parameters,
        ILogger logger) : base(parameters)
    {
        _logger = logger;
        ToolName = WebSearchTool.ToolName;
        ToolDisplayName = WebSearchTool.DisplayName;
    }

    protected override ToolKind Kind => ToolKind.Search;

    public override string GetDescription()
    {
        var maxResultsText = Parameters.MaxResults.HasValue
            ? $" (max {Parameters.MaxResults.Value} results)"
            : "";
        return $"Google Search: {Parameters.Query}{maxResultsText}";
    }

    public override IReadOnlyList<ToolLocation> GetToolLocations() => Array.Empty<ToolLocation>();

    public override async Task<ToolExecutionResult> ExecuteAsync(
        CancellationToken cancellationToken,
        Action<ToolLiveOutput>? updateOutput = null)
    {
        // WebSearch tool is a placeholder that returns a message
        // Actual implementation would require Google Custom Search API integration
        _logger.Information("Web search requested: {Query}", Parameters.Query);

        var maxResults = Parameters.MaxResults ?? 10;

        // Return a simulated response for now
        var response = $@"Google search results for: {Parameters.Query}

This tool is currently a placeholder. Actual Google Search integration requires:
- Google Custom Search API key
- API implementation in C#
- Error handling and rate limiting

For now, the search would return {maxResults} results from Google Search.

To enable web search:
1. Configure a Google Custom Search API key
2. Implement the Google Search API client
3. Handle API quotas and rate limiting
";

        return new ToolExecutionResult
        {
            LlmContent = new TextContentPart(response),
            ReturnDisplay = new MarkdownToolResultDisplay(response),
            Data = new Dictionary<string, object?>
            {
                { "query", Parameters.Query },
                { "max_results", maxResults },
                { "status", "not_implemented" }
            }
        };
    }
}
