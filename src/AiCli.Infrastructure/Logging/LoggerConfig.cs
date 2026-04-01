using Serilog;
using Serilog.Events;

namespace AiCli.Core.Logging;

/// <summary>
/// Configuration for application logging.
/// </summary>
public record LoggerConfig
{
    /// <summary>
    /// Minimum log level.
    /// </summary>
    public LogEventLevel MinimumLevel { get; init; } = LogEventLevel.Information;

    /// <summary>
    /// Whether to enable console output.
    /// </summary>
    public bool EnableConsole { get; init; } = true;

    /// <summary>
    /// Whether to enable file logging.
    /// </summary>
    public bool EnableFile { get; init; } = true;

    /// <summary>
    /// Path to log file.
    /// </summary>
    public string? LogFilePath { get; init; }

    /// <summary>
    /// Whether to output JSON format.
    /// </summary>
    public bool UseJsonFormat { get; init; } = false;
}

/// <summary>
/// Logger configuration helper.
/// </summary>
public static class LoggerHelper
{
    /// <summary>
    /// Creates and configures the Serilog logger.
    /// </summary>
    public static ILogger CreateLogger(LoggerConfig config)
    {
        var loggerConfiguration = new LoggerConfiguration()
            .MinimumLevel.Is(config.MinimumLevel)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "AiCli")
            ;

        if (config.EnableConsole)
        {
            loggerConfiguration = loggerConfiguration.WriteTo.Console(
                outputTemplate: config.UseJsonFormat ? null : "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}",
                restrictedToMinimumLevel: config.MinimumLevel);
        }

        if (config.EnableFile && !string.IsNullOrEmpty(config.LogFilePath))
        {
            var logPath = GetLogFilePath(config.LogFilePath);
            var directory = Path.GetDirectoryName(logPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            loggerConfiguration = loggerConfiguration.WriteTo.File(
                logPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}",
                restrictedToMinimumLevel: config.MinimumLevel);
        }

        return loggerConfiguration.CreateLogger();
    }

    /// <summary>
    /// Gets the default log file path.
    /// </summary>
    private static string GetLogFilePath(string? customPath)
    {
        if (!string.IsNullOrEmpty(customPath))
        {
            return Path.GetFullPath(customPath);
        }

        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".aicli",
            "logs");

        return Path.Combine(logDir, "aicli-.log");
    }

    /// <summary>
    /// Sets the global logger.
    /// </summary>
    public static void SetGlobalLogger(ILogger logger)
    {
        Log.Logger = logger;
        Log.Information("AiCli logger initialized");
    }

    /// <summary>
    /// Gets or creates a logger for the specified type.
    /// </summary>
    public static ILogger ForContext<T>()
    {
        return Log.ForContext("SourceContext", typeof(T).Name);
    }
}
