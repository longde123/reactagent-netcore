using AiCli.Core.Logging;
using AiCli.Core.Types;
using System.Net;
using System.Net.Http;
using System.Text;

namespace AiCli.Core.Tools.Builtin;

/// <summary>
/// Parameters for WebFetch tool.
/// </summary>
public record WebFetchToolParams
{
    /// <summary>
    /// The URL to fetch.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    /// Whether to fetch images and media (not just HTML).
    /// </summary>
    public bool? FetchMedia { get; init; } = false;

    /// <summary>
    /// Maximum content length to return.
    /// </summary>
    public int? MaxLength { get; init; } = 100000;
}

/// <summary>
/// Implementation of WebFetch tool for fetching web content.
/// </summary>
public class WebFetchTool : DeclarativeTool<WebFetchToolParams, ToolExecutionResult>
{
    public const string ToolName = "web_fetch";
    public const string DisplayName = "Web Fetch";
    public const string Description = "Fetch and read content from a URL.";

    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;

    /// <summary>
    /// Initializes a new instance of the WebFetchTool class.
    /// </summary>
    public WebFetchTool(HttpClient? httpClient = null)
        : base(
            ToolName,
            DisplayName,
            Description,
            ToolKind.Fetch,
            GetParameterSchema())
    {
        _logger = LoggerHelper.ForContext<WebFetchTool>();
        _httpClient = httpClient ?? new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        });
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
                    "url",
                    new
                    {
                        type = "string",
                        description = "The URL to fetch content from."
                    }
                },
                {
                    "fetch_media",
                    new
                    {
                        type = "boolean",
                        description = "Whether to fetch images and media (not just HTML). Defaults to false."
                    }
                },
                {
                    "max_length",
                    new
                    {
                        type = "integer",
                        description = "Maximum content length to return. Defaults to 100000 characters."
                    }
                }
            },
            required = new[] { "url" }
        };
    }

    /// <summary>
    /// Validates the tool parameters.
    /// </summary>
    protected override string? ValidateToolParams(WebFetchToolParams parameters)
    {
        if (string.IsNullOrWhiteSpace(parameters.Url))
        {
            return "The 'url' parameter must not be empty.";
        }

        if (!Uri.TryCreate(parameters.Url, UriKind.Absolute, out _))
        {
            return "The 'url' parameter is not a valid URL.";
        }

        if (parameters.MaxLength.HasValue && parameters.MaxLength.Value < 1)
        {
            return "The 'max_length' must be at least 1.";
        }

        return null;
    }

    /// <summary>
    /// Creates a tool invocation for the given parameters.
    /// </summary>
    public override IToolInvocation<WebFetchToolParams, ToolExecutionResult> Build(WebFetchToolParams parameters)
    {
        return new WebFetchToolInvocation(parameters, _httpClient, _logger);
    }
}

/// <summary>
/// Invocation for the WebFetch tool.
/// </summary>
public class WebFetchToolInvocation : BaseToolInvocation<WebFetchToolParams, ToolExecutionResult>
{
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;

    public WebFetchToolInvocation(
        WebFetchToolParams parameters,
        HttpClient httpClient,
        ILogger logger) : base(parameters)
    {
        _httpClient = httpClient;
        _logger = logger;
        ToolName = WebFetchTool.ToolName;
        ToolDisplayName = WebFetchTool.DisplayName;
    }

    protected override ToolKind Kind => ToolKind.Fetch;

    public override string GetDescription()
    {
        var maxLengthText = Parameters.MaxLength.HasValue
            ? $" (max {Parameters.MaxLength.Value} chars)"
            : "";
        return $"Fetch {Parameters.Url}{maxLengthText}";
    }

    public override IReadOnlyList<ToolLocation> GetToolLocations() => Array.Empty<ToolLocation>();

    public override async Task<ToolExecutionResult> ExecuteAsync(
        CancellationToken cancellationToken,
        Action<ToolLiveOutput>? updateOutput = null)
    {
        try
        {
            _logger.Verbose("Fetching URL: {Url}", Parameters.Url);

            var request = new HttpRequestMessage(HttpMethod.Get, Parameters.Url);
            var response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = response.StatusCode switch
                {
                    HttpStatusCode.NotFound => $"URL not found: {Parameters.Url}",
                    HttpStatusCode.Forbidden => $"Access denied: {Parameters.Url}",
                    HttpStatusCode.Unauthorized => $"Unauthorized: {Parameters.Url}",
                    _ => $"HTTP {response.StatusCode}: {Parameters.Url}"
                };

                _logger.Warning("HTTP error: {StatusCode}", response.StatusCode);

                return ToolExecutionResult.Failure(
                    error,
                    ToolErrorType.IOError);
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var maxLength = Parameters.MaxLength ?? 100000;

            // Truncate if necessary
            var trimmedContent = content.Length > maxLength
                ? content.Substring(0, maxLength)
                : content;

            _logger.Verbose("Fetched {CharCount} characters", trimmedContent.Length);

            var isTruncated = content.Length > maxLength;
            var resultContent = trimmedContent;
            var displayContent = trimmedContent;

            // Extract basic metadata
            var contentType = response.Content.Headers?.Contains("Content-Type") == true
                ? response.Content.Headers.GetValues("Content-Type").FirstOrDefault()
                : null;

            // Try to detect and format HTML
            if (!string.IsNullOrEmpty(contentType) &&
                contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
            {
                resultContent = FormatHtml(trimmedContent, maxLength);
                displayContent = FormatHtmlForDisplay(trimmedContent, maxLength);
            }

            var markdownOutput = FormatAsMarkdown(resultContent, isTruncated);

            return new ToolExecutionResult
            {
                LlmContent = new TextContentPart(resultContent),
                ReturnDisplay = new MarkdownToolResultDisplay(markdownOutput),
                Data = new Dictionary<string, object>
                {
                    { "url", Parameters.Url },
                    { "content_type", contentType ?? "" },
                    { "char_count", trimmedContent.Length },
                    { "truncated", isTruncated }
                }
            };
        }
        catch (HttpRequestException ex) when (ex.InnerException is TaskCanceledException)
        {
            _logger.Information("Web fetch cancelled");
            return ToolExecutionResult.Failure(
                "Operation cancelled by user",
                ToolErrorType.Cancellation);
        }
        catch (HttpRequestException ex)
        {
            _logger.Error(ex, "HTTP error fetching URL: {Url}", Parameters.Url);
            return ToolExecutionResult.Failure(
                $"HTTP error: {ex.Message}",
                ToolErrorType.IOError);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Unexpected error fetching URL: {Url}", Parameters.Url);
            return ToolExecutionResult.Failure(
                $"Unexpected error: {ex.Message}",
                ToolErrorType.Unknown);
        }
    }

    /// <summary>
    /// Formats HTML content by stripping tags and whitespace.
    /// </summary>
    private static string FormatHtml(string html, int maxLength)
    {
        // Simple HTML tag removal (for basic use)
        var text = System.Text.RegularExpressions.Regex.Replace(
            html, @"<[^>]+>", "", System.Text.RegularExpressions.RegexOptions.Multiline);

        // Remove extra whitespace
        text = System.Text.RegularExpressions.Regex.Replace(
            text, @"\s+", " ");

        if (text.Length > maxLength)
        {
            text = text.Substring(0, maxLength);
        }

        return text;
    }

    /// <summary>
    /// Formats the content as markdown.
    /// </summary>
    private static string FormatAsMarkdown(string content, bool isTruncated)
    {
        if (isTruncated)
        {
            return $"```\n{content}\n\n[Content truncated]\n```";
        }

        return $"```\n{content}\n```";
    }

    /// <summary>
    /// Formats HTML content for display.
    /// </summary>
    private static string FormatHtmlForDisplay(string html, int maxLength)
    {
        if (html.Length > maxLength)
        {
            return $"[HTML content truncated to {maxLength} characters]";
        }

        return $"[HTML: {html.Length} characters]";
    }
}
