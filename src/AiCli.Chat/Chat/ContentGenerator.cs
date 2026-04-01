using AiCli.Core.Configuration;
using AiCli.Core.Logging;
using AiCli.Core.Types;
using System.Text.Json;

namespace AiCli.Core.Chat;

/// <summary>
/// Lightweight content generator placeholder implementation.
/// </summary>
public class ContentGenerator : IContentGenerator, IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly Config _config;

    public ContentGenerator(Config config, string? apiKey = null)
    {
        _logger = LoggerHelper.ForContext<ContentGenerator>();
        _config = config;
    }

    public string GetModelId() => _config.GetModel();

    public async Task<ContentMessage> SendMessageAsync(
        ContentMessage message,
        CancellationToken cancellationToken = default)
    {
        // Build request from single message and call generate
        var request = new GenerateContentRequest
        {
            Model = _config.GetModel(),
            Contents = new List<ContentMessage> { message }
        };

        var response = await GenerateContentAsync(request, cancellationToken);

        // Convert first candidate to ContentMessage
        var candidate = response.Candidates.FirstOrDefault();
        if (candidate == null)
        {
            return ContentMessage.ModelMessage(string.Empty);
        }

        return new ContentMessage
        {
            Role = LlmRole.Model,
            Parts = candidate.Content
        };
    }

    public async IAsyncEnumerable<ContentMessage> SendMessageStreamAsync(
        ContentMessage message,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = new GenerateContentRequest
        {
            Model = _config.GetModel(),
            Contents = new List<ContentMessage> { message },
            CancellationToken = cancellationToken
        };

        await foreach (var chunk in GenerateContentStreamAsync(request, cancellationToken))
        {
            var candidate = chunk.Candidates.FirstOrDefault();
            if (candidate != null)
            {
                yield return new ContentMessage
                {
                    Role = LlmRole.Model,
                    Parts = candidate.Content
                };
            }
        }
    }

    public Task<GenerateContentResponse> GenerateContentAsync(
        GenerateContentRequest request,
        CancellationToken cancellationToken = default)
    {
        var functionResponse = request.Contents
            .SelectMany(c => c.Parts)
            .OfType<FunctionResponsePart>()
            .LastOrDefault();

        if (functionResponse != null)
        {
            var resultText = functionResponse.Response.TryGetValue("result", out var resultObj)
                ? resultObj?.ToString()
                : null;

            var toolReply = string.IsNullOrWhiteSpace(resultText)
                ? $"Tool {functionResponse.FunctionName} executed."
                : resultText;

            return Task.FromResult(new GenerateContentResponse
            {
                Candidates = new List<Candidate>
                {
                    new()
                    {
                        Content = new List<ContentPart>
                        {
                            new TextContentPart(toolReply ?? string.Empty)
                        },
                        FinishReason = FinishReason.Stop,
                        Index = 0
                    }
                },
                ModelVersion = _config.GetModel()
            });
        }

        var text = request.Contents
            .SelectMany(c => c.Parts)
            .OfType<TextContentPart>()
            .Select(p => p.Text)
            .LastOrDefault() ?? "";

        _logger.Information("GenerateContentAsync received text: {Text}", text);

        // Simple heuristic: if user asks to create a Program.cs, return a function call to write_file
        if (text.Contains("Program.cs", StringComparison.OrdinalIgnoreCase) ||
            (text.Contains("创建", StringComparison.OrdinalIgnoreCase) && text.Contains("Program", StringComparison.OrdinalIgnoreCase)))
        {
            var code = "// Program.cs\n\nConsole.WriteLine(\"Hello from generated program!\");\n";
            var args = new Dictionary<string, object?>
            {
                ["file_path"] = "Program.cs",
                ["content"] = code
            };

            var funcResponse = new GenerateContentResponse
            {
                Candidates = new List<Candidate>
                {
                    new()
                    {
                        Content = new List<ContentPart>
                        {
                            new FunctionCallPart
                            {
                                FunctionName = "write_file",
                                Arguments = args
                            }
                        },
                        FinishReason = FinishReason.FunctionCall,
                        Index = 0
                    }
                },
                ModelVersion = _config.GetModel()
            };

            _logger.Information("GenerateContentAsync returning FunctionCall write_file with args: {Args}", JsonSerializer.Serialize(args));
            return Task.FromResult(funcResponse);
        }

        var response = new GenerateContentResponse
        {
            Candidates = new List<Candidate>
            {
                new()
                {
                    Content = new List<ContentPart>
                    {
                        new TextContentPart($"[placeholder] {text}")
                    },
                    FinishReason = FinishReason.Stop,
                    Index = 0
                }
            },
            ModelVersion = _config.GetModel()
        };

        return Task.FromResult(response);
    }

    public async IAsyncEnumerable<GenerateContentResponse> GenerateContentStreamAsync(
        GenerateContentRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return await GenerateContentAsync(request, cancellationToken);
    }

    public Task<CountTokensResponse> CountTokensAsync(
        CountTokensRequest request,
        CancellationToken cancellationToken = default)
    {
        var totalText = request.Contents
            .SelectMany(c => c.Parts)
            .OfType<TextContentPart>()
            .Sum(p => p.Text.Length);

        return Task.FromResult(new CountTokensResponse
        {
            TotalTokens = Math.Max(1, totalText / 4)
        });
    }

    public Task<EmbedContentResponse> EmbedContentAsync(
        EmbedContentRequest request,
        CancellationToken cancellationToken = default)
    {
        _logger.Verbose("EmbedContentAsync called for model {Model}", request.Model);

        return Task.FromResult(new EmbedContentResponse
        {
            Embedding = new List<double> { 0.0, 0.0, 0.0 }
        });
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
