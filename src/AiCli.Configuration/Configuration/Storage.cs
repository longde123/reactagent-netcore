using System.Text.Json;

namespace AiCli.Core.Configuration;

/// <summary>
/// Manages configuration storage and file paths.
/// </summary>
public class Storage
{
    private readonly string _userConfigDir;
    private readonly string _projectConfigDir;
    private readonly string _userSettingsPath;
    private readonly string _projectSettingsPath;
    private readonly string _globalMemoryPath;
    private readonly string _projectMemoryPath;
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Gets the user configuration directory.
    /// </summary>
    public string UserConfigDir => _userConfigDir;

    /// <summary>
    /// Gets the project configuration directory (if in a project).
    /// </summary>
    public string? ProjectConfigDir =>
        Directory.Exists(_projectConfigDir) ? _projectConfigDir : null;

    /// <summary>
    /// Initializes a new instance of the Storage class.
    /// </summary>
    public Storage(string? projectRoot = null)
    {
        _userConfigDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".aicli");

        _projectConfigDir = string.IsNullOrEmpty(projectRoot)
            ? FindProjectRoot(Directory.GetCurrentDirectory())
            : projectRoot;

        _userSettingsPath = Path.Combine(_userConfigDir, "settings.json");
        _globalMemoryPath = Path.Combine(_userConfigDir, "memory.md");

        if (_projectConfigDir is not null)
        {
            _projectSettingsPath = Path.Combine(_projectConfigDir, "settings.json");
            _projectMemoryPath = Path.Combine(_projectConfigDir, "memory.md");
        }
        else
        {
            _projectSettingsPath = "";
            _projectMemoryPath = "";
        }

        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
    }

    /// <summary>
    /// Loads settings from user and project configuration.
    /// Creates a default settings file if none exists at the user level.
    /// </summary>
    public async Task<Settings> LoadSettingsAsync()
    {
        // Ensure a default settings file exists for first-time users
        await EnsureDefaultSettingsFileAsync();

        var settings = new Settings();

        // Load project settings first
        if (!string.IsNullOrEmpty(_projectSettingsPath) && File.Exists(_projectSettingsPath))
        {
            var projectJson = await File.ReadAllTextAsync(_projectSettingsPath);
            var projectSettings = JsonSerializer.Deserialize<Settings>(projectJson, _jsonOptions);
            if (projectSettings is not null)
            {
                settings = settings.Merge(projectSettings);
            }
        }

        // Merge user settings (user settings override project settings)
        if (File.Exists(_userSettingsPath))
        {
            var userJson = await File.ReadAllTextAsync(_userSettingsPath);
            var userSettings = JsonSerializer.Deserialize<Settings>(userJson, _jsonOptions);
            if (userSettings is not null)
            {
                settings = settings.Merge(userSettings);
            }
        }

        // Check for environment variable overrides
        var envApiKey = Environment.GetEnvironmentVariable("AICLI_API_KEY");
        if (!string.IsNullOrEmpty(envApiKey))
        {
            settings = settings with { ApiKey = envApiKey };
        }

        return settings;
    }

    /// <summary>
    /// Saves settings to the appropriate location.
    /// </summary>
    public async Task SaveSettingsAsync(Settings settings, bool userLevel = false)
    {
        var path = userLevel ? _userSettingsPath : _projectSettingsPath;

        if (string.IsNullOrEmpty(path))
        {
            throw new InvalidOperationException("No valid settings path available");
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(settings, _jsonOptions);
        await File.WriteAllTextAsync(path, json);
    }

    /// <summary>
    /// Loads hierarchical memory.
    /// </summary>
    public async Task<HierarchicalMemory> LoadMemoryAsync()
    {
        var globalMemory = "";
        var projectMemory = "";

        if (File.Exists(_globalMemoryPath))
        {
            globalMemory = await File.ReadAllTextAsync(_globalMemoryPath);
        }

        if (!string.IsNullOrEmpty(_projectMemoryPath) && File.Exists(_projectMemoryPath))
        {
            projectMemory = await File.ReadAllTextAsync(_projectMemoryPath);
        }

        return new HierarchicalMemory
        {
            Global = globalMemory,
            Project = projectMemory
        };
    }

    /// <summary>
    /// Saves memory at the specified level.
    /// </summary>
    public async Task SaveMemoryAsync(string content, MemoryLevel level)
    {
        var path = level switch
        {
            MemoryLevel.Global => _globalMemoryPath,
            MemoryLevel.Project => _projectMemoryPath,
            _ => throw new ArgumentException($"Invalid memory level: {level}")
        };

        if (string.IsNullOrEmpty(path))
        {
            throw new InvalidOperationException("No valid memory path available");
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(path, content);
    }

    /// <summary>
    /// Creates a default settings.json with comments if it doesn't exist at the user level.
    /// </summary>
    private async Task EnsureDefaultSettingsFileAsync()
    {
        if (File.Exists(_userSettingsPath)) return;

        if (!Directory.Exists(_userConfigDir))
        {
            Directory.CreateDirectory(_userConfigDir);
        }

        var defaultJson =
@"{
  // AiCli Settings - Configuration file
  // Uncomment and modify the values below to customize your experience.
  // This file is located at ~/.aicli/settings.json

  // ""model"": ""gemini-2.5-flash"",
  // ""apiKey"": ""your-api-key-here"",
  // ""baseUrl"": ""https://generativelanguage.googleapis.com"",
  // ""approvalMode"": ""Interactive"",
  // ""enableAgents"": true,
  // ""enableBrowserAgent"": false,
  // ""maxAgentTurns"": 10,
  // ""agentTimeout"": 300,
  // ""enableTelemetry"": false,
  // ""enableCodeAssist"": false,
  // ""useSandbox"": false,
  // ""sandbox"": ""none"",
  // ""maxTokens"": 8192,
  // ""temperature"": 0.7,
  // ""topP"": 0.95,
  // ""topK"": 40,
  // ""enablePlanMode"": true,
  // ""memory"": """",
  // ""embeddingModel"": ""bge-m3"",
  // ""thinkingModel"": ""gpt-oss:20b"",
  // ""fastModel"": ""qwen2.5-coder:7b"",
  // ""extensions"": []
}";
        await File.WriteAllTextAsync(_userSettingsPath, defaultJson);
    }

    /// <summary>
    /// Finds the project root directory by looking for .aicli folder.
    /// </summary>
    private static string? FindProjectRoot(string startDir)
    {
        var currentDir = startDir;
        var maxAttempts = 20;

        for (int i = 0; i < maxAttempts && currentDir is not null; i++)
        {
            var geminiDir = Path.Combine(currentDir, ".aicli");
            if (Directory.Exists(geminiDir))
            {
                return geminiDir;
            }

            var parent = Directory.GetParent(currentDir);
            currentDir = parent?.FullName;
        }

        return null;
    }
}
