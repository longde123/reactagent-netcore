using AiCli.Core.Logging;
using AiCli.Core.Types;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace AiCli.Core.Tools.Builtin;

/// <summary>
/// Parameters for Shell tool.
/// </summary>
public record ShellToolParams
{
    /// <summary>
    /// The shell command to execute.
    /// </summary>
    public required string Command { get; init; }

    /// <summary>
    /// Optional arguments for the command.
    /// </summary>
    public string? Args { get; init; }

    /// <summary>
    /// Working directory for the command.
    /// </summary>
    public string? WorkingDir { get; init; }

    /// <summary>
    /// Whether to stream output in real-time.
    /// </summary>
    public bool? StreamOutput { get; init; } = true;

    /// <summary>
    /// Environment variables for the command.
    /// </summary>
    public Dictionary<string, string>? Env { get; init; }
}

/// <summary>
/// Implementation of Shell tool for executing commands.
/// </summary>
public class ShellTool : DeclarativeTool<ShellToolParams, ToolExecutionResult>
{
    public const string ToolName = "shell";
    public const string DisplayName = "Shell";
    public const string Description = "Execute a shell command.";

    private readonly ILogger _logger;
    private readonly string _targetDirectory;

    /// <summary>
    /// Initializes a new instance of the ShellTool class.
    /// </summary>
    public ShellTool(string targetDirectory)
        : base(
            ToolName,
            DisplayName,
            Description,
            ToolKind.Execute,
            GetParameterSchema())
    {
        _logger = LoggerHelper.ForContext<ShellTool>();
        _targetDirectory = targetDirectory;
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
                    "command",
                    new
                    {
                        type = "string",
                        description = "The shell command to execute."
                    }
                },
                {
                    "args",
                    new
                    {
                        type = "string",
                        description = "Optional arguments for the command."
                    }
                },
                {
                    "working_dir",
                    new
                    {
                        type = "string",
                        description = "Working directory for the command."
                    }
                },
                {
                    "stream_output",
                    new
                    {
                        type = "boolean",
                        description = "Whether to stream output in real-time."
                    }
                },
                {
                    "env",
                    new
                    {
                        type = "object",
                        description = "Environment variables for the command (key-value pairs)."
                    }
                }
            },
            required = new[] { "command" }
        };
    }

    /// <summary>
    /// Validates the tool parameters.
    /// </summary>
    protected override string? ValidateToolParams(ShellToolParams parameters)
    {
        if (string.IsNullOrWhiteSpace(parameters.Command))
        {
            return "The 'command' parameter must not be empty.";
        }

        return null;
    }

    /// <summary>
    /// Creates a tool invocation for the given parameters.
    /// </summary>
    public override IToolInvocation<ShellToolParams, ToolExecutionResult> Build(ShellToolParams parameters)
    {
        var resolvedPath = string.IsNullOrWhiteSpace(parameters.WorkingDir)
            ? _targetDirectory
            : Path.GetFullPath(Path.Combine(_targetDirectory, parameters.WorkingDir!));

        return new ShellToolInvocation(parameters, resolvedPath, _logger);
    }
}

/// <summary>
/// Invocation for the Shell tool.
/// </summary>
public class ShellToolInvocation : BaseToolInvocation<ShellToolParams, ToolExecutionResult>
{
    private readonly string _resolvedPath;
    private readonly ILogger _logger;

    public ShellToolInvocation(
        ShellToolParams parameters,
        string resolvedPath,
        ILogger logger) : base(parameters)
    {
        _resolvedPath = resolvedPath;
        _logger = logger;
        ToolName = ShellTool.ToolName;
        ToolDisplayName = ShellTool.DisplayName;
    }

    protected override ToolKind Kind => ToolKind.Execute;

    public override string GetDescription()
    {
        var workingDirDisplay = string.IsNullOrEmpty(Parameters.WorkingDir)
            ? "current directory"
            : Parameters.WorkingDir;
        var argsDisplay = string.IsNullOrEmpty(Parameters.Args)
            ? ""
            : $" {Parameters.Args}";
        return $"Execute: {Parameters.Command}{argsDisplay} in {workingDirDisplay}";
    }

    public override IReadOnlyList<ToolLocation> GetToolLocations()
    {
        var basePath = Path.GetDirectoryName(_resolvedPath) ?? _resolvedPath;
        return new List<ToolLocation> { new ToolLocation { Path = basePath } };
    }

    public override async Task<ToolExecutionResult> ExecuteAsync(
        CancellationToken cancellationToken,
        Action<ToolLiveOutput>? updateOutput = null)
    {
        using var process = new Process();
        var outputBuilder = new StringBuilder();
        var errorBuilder = new StringBuilder();

        var streamOutput = Parameters.StreamOutput ?? true;

        try
        {
            _logger.Verbose("Executing command: {Command}", Parameters.Command);

            var command = string.IsNullOrWhiteSpace(Parameters.Args)
                ? Parameters.Command
                : $"{Parameters.Command} {Parameters.Args}";

            var shell = GetShell();
            var workingDirectory = ResolveWorkingDirectory();

            var startInfo = new ProcessStartInfo
            {
                FileName = shell,
                Arguments = BuildShellArguments(shell, command),
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };

            if (Parameters.Env is not null && Parameters.Env.Count > 0)
            {
                foreach (var kvp in Parameters.Env)
                {
                    startInfo.Environment[kvp.Key] = kvp.Value;
                }
            }

            process.StartInfo = startInfo;

            process.OutputDataReceived += (_, data) =>
            {
                if (string.IsNullOrEmpty(data.Data)) return;
                outputBuilder.AppendLine(data.Data);
                if (streamOutput && updateOutput is not null)
                {
                    updateOutput(new ToolLiveOutput
                    {
                        Text = data.Data,
                        IsError = false,
                        IsFinal = false
                    });
                }
            };

            process.ErrorDataReceived += (_, data) =>
            {
                if (string.IsNullOrEmpty(data.Data)) return;
                errorBuilder.AppendLine(data.Data);
                if (streamOutput && updateOutput is not null)
                {
                    updateOutput(new ToolLiveOutput
                    {
                        Text = data.Data,
                        IsError = true,
                        IsFinal = false
                    });
                }
            };

            try
            {
                process.Start();
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 267)
            {
                var fallbackDirectory = Directory.GetCurrentDirectory();
                _logger.Warning(ex,
                    "Invalid working directory: {WorkingDirectory}. Retrying with fallback: {FallbackDirectory}",
                    startInfo.WorkingDirectory,
                    fallbackDirectory);

                startInfo.WorkingDirectory = fallbackDirectory;
                process.StartInfo = startInfo;
                process.Start();

                var warning = $"[shell] invalid working directory, fallback to: {fallbackDirectory}";
                errorBuilder.AppendLine(warning);
                if (streamOutput && updateOutput is not null)
                {
                    updateOutput(new ToolLiveOutput
                    {
                        Text = warning,
                        IsError = true,
                        IsFinal = false
                    });
                }
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(cancellationToken);

            var exitCode = process.ExitCode;
            var standardOutput = outputBuilder.ToString().TrimEnd();
            var errorOutput = errorBuilder.ToString().TrimEnd();

            _logger.Verbose("Command completed with exit code: {ExitCode}", exitCode);

            if (exitCode == 0)
            {
                var output = string.IsNullOrWhiteSpace(standardOutput)
                    ? "Command completed successfully"
                    : standardOutput;

                if (!string.IsNullOrWhiteSpace(errorOutput))
                {
                    output = $"{output}\n{errorOutput}";
                }

                return new ToolExecutionResult
                {
                    LlmContent = new TextContentPart(output),
                    ReturnDisplay = new TextToolResultDisplay(output),
                    IsError = false,
                    Output = output,
                    Content = new List<ContentPart> { new TextContentPart(output) }
                };
            }
            else
            {
                var error = string.IsNullOrWhiteSpace(errorOutput)
                    ? (string.IsNullOrWhiteSpace(standardOutput)
                        ? $"Command failed with exit code: {exitCode}"
                        : standardOutput)
                    : errorOutput;

                _logger.Error("Command failed: {Error}", error);

                return new ToolExecutionResult
                {
                    LlmContent = new TextContentPart(error),
                    ReturnDisplay = new TextToolResultDisplay(error),
                    Error = new ToolError
                    {
                        Message = error,
                        ErrorType = exitCode switch
                        {
                            1 => ToolErrorType.Execution,
                            126 or 127 => ToolErrorType.NotFound,
                            2 => ToolErrorType.Validation,
                            3 => ToolErrorType.Unknown,
                            _ => ToolErrorType.Execution
                        }
                    },
                    IsError = true,
                    Output = error,
                    Content = new List<ContentPart> { new TextContentPart(error) }
                };
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Information("Shell command cancelled");

            return ToolExecutionResult.Failure(
                "Operation cancelled by user",
                ToolErrorType.Cancellation);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Unexpected error executing command");

            return ToolExecutionResult.Failure(
                $"Unexpected error: {ex.Message}",
                ToolErrorType.Unknown);
        }
    }

    private string ResolveWorkingDirectory()
    {
        if (Directory.Exists(_resolvedPath))
        {
            return _resolvedPath;
        }

        var current = Directory.GetCurrentDirectory();
        if (Directory.Exists(current))
        {
            _logger.Warning("Working directory does not exist: {Path}. Using current directory: {Current}", _resolvedPath, current);
            return current;
        }

        _logger.Warning("Working directory does not exist: {Path}. Falling back to temp directory.", _resolvedPath);
        return Path.GetTempPath();
    }

    private static string BuildShellArguments(string shell, string command)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && shell.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase))
        {
            // Switch code page to UTF-8 before running the command so that
            // process output can be read as UTF-8 without garbled characters.
            return $"/d /s /c \"chcp 65001 >nul 2>&1 & {command}\"";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var escaped = command.Replace("\"", "\\\"");
            return $"-lc \"{escaped}\"";
        }

        return command;
    }

    /// <summary>
    /// Gets the appropriate shell for the current OS.
    /// </summary>
    private static string GetShell()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "cmd.exe";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return "/bin/bash";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "/bin/zsh";
        }

        return "/bin/sh";
    }
}
