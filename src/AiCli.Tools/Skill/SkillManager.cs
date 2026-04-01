using AiCli.Core.Logging;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiCli.Core.Services;

/// <summary>
/// Skill information.
/// </summary>
public record Skill
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Version { get; init; }
    public required string Author { get; init; }
    public List<string> Keywords { get; set; } = new();
    public List<string> Capabilities { get; set; } = new();
    public Dictionary<string, object> Config { get; set; } = new();
    public string? SourcePath { get; init; }
    public DateTime InstalledAt { get; init; } = DateTime.UtcNow;
    public DateTime LastUsed { get; set; }
}

/// <summary>
/// Skill execution result.
/// </summary>
public record SkillExecutionResult
{
    public required Skill Skill { get; init; }
    public required bool Success { get; init; }
    public required string Output { get; init; }
    public Dictionary<string, object> Metadata { get; } = new();
    public Exception? Error { get; init; }
}

/// <summary>
/// Service for managing skills.
/// </summary>
public class SkillManager : IDisposable
{
    private readonly ILogger _logger;
    private readonly Dictionary<string, Skill> _skills = new();
    private readonly Dictionary<string, Skill> _skillsByName = new();
    private readonly string _skillsPath;
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>
    /// Event raised when a skill is installed.
    /// </summary>
    public event EventHandler<SkillInstalledEventArgs>? SkillInstalled;

    /// <summary>
    /// Event raised when a skill is uninstalled.
    /// </summary>
    public event EventHandler<SkillUninstalledEventArgs>? SkillUninstalled;

    /// <summary>
    /// Event raised when a skill is executed.
    /// </summary>
    public event EventHandler<SkillExecutedEventArgs>? SkillExecuted;

    /// <summary>
    /// Gets the number of installed skills.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _skills.Count;
            }
        }
    }

    public SkillManager(string skillsPath)
    {
        _logger = LoggerHelper.ForContext<SkillManager>();
        _skillsPath = skillsPath;

        // Ensure skills directory exists
        if (!Directory.Exists(_skillsPath))
        {
            Directory.CreateDirectory(_skillsPath);
        }

        _logger.Information("SkillManager initialized at: {Path}", _skillsPath);
    }

    /// <summary>
    /// Initializes the skill manager and loads skills.
    /// </summary>
    public async Task InitializeAsync()
    {
        _logger.Information("Initializing SkillManager...");

        await LoadSkillsAsync();

        _logger.Information("SkillManager initialized with {Count} skills", _skills.Count);
    }

    /// <summary>
    /// Loads all skills from the skills directory.
    /// </summary>
    public async Task LoadSkillsAsync()
    {
        var skillDirs = Directory.GetDirectories(_skillsPath);

        foreach (var skillDir in skillDirs)
        {
            var skillFile = Path.Combine(skillDir, "skill.json");
            if (!File.Exists(skillFile))
            {
                continue;
            }

            try
            {
                var json = await File.ReadAllTextAsync(skillFile);
                var skillData = JsonSerializer.Deserialize<JsonNode>(json);

                if (skillData == null) continue;

                var skill = new Skill
                {
                    Id = skillData["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString(),
                    Name = skillData["name"]?.GetValue<string>() ?? Path.GetFileName(skillDir),
                    Description = skillData["description"]?.GetValue<string>() ?? "No description",
                    Version = skillData["version"]?.GetValue<string>() ?? "1.0.0",
                    Author = skillData["author"]?.GetValue<string>() ?? "Unknown",
                    SourcePath = skillDir
                };

                // Load keywords
                if (skillData["keywords"] is JsonArray keywordsArray)
                {
                    skill.Keywords = keywordsArray
                        .Select(k => k.GetValue<string>()!)
                        .Where(k => !string.IsNullOrEmpty(k))
                        .ToList();
                }

                // Load capabilities
                if (skillData["capabilities"] is JsonArray capabilitiesArray)
                {
                    skill.Capabilities = capabilitiesArray
                        .Select(c => c.GetValue<string>()!)
                        .Where(c => !string.IsNullOrEmpty(c))
                        .ToList();
                }

                // Load config
                if (skillData["config"] is JsonObject configObj)
                {
                    foreach (var prop in configObj)
                    {
                        skill.Config[prop.Key] = prop.Value;
                    }
                }

                lock (_lock)
                {
                    _skills[skill.Id] = skill;
                    _skillsByName[skill.Name] = skill;
                }

                _logger.Verbose("Loaded skill: {Name} ({Id})", skill.Name, skill.Id);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error loading skill from: {Path}", skillDir);
            }
        }
    }

    /// <summary>
    /// Installs a skill from a directory.
    /// </summary>
    public async Task<string> InstallSkillAsync(string sourcePath)
    {
        if (!Directory.Exists(sourcePath))
        {
            throw new DirectoryNotFoundException($"Source directory not found: {sourcePath}");
        }

        var skillFile = Path.Combine(sourcePath, "skill.json");
        if (!File.Exists(skillFile))
        {
            throw new FileNotFoundException($"Skill file not found: {skillFile}");
        }

        var json = await File.ReadAllTextAsync(skillFile);
        var skillData = JsonSerializer.Deserialize<JsonNode>(json);

        if (skillData == null)
        {
            throw new InvalidDataException("Invalid skill file format");
        }

        var skillName = skillData["name"]?.GetValue<string>() ?? Path.GetFileName(sourcePath);
        var skillId = skillData["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString();

        // Copy to skills directory
        var destPath = Path.Combine(_skillsPath, skillId);
        if (Directory.Exists(destPath))
        {
            Directory.Delete(destPath, recursive: true);
        }

        DirectoryCopy(sourcePath, destPath);

        var skill = new Skill
        {
            Id = skillId,
            Name = skillName,
            Description = skillData["description"]?.GetValue<string>() ?? "No description",
            Version = skillData["version"]?.GetValue<string>() ?? "1.0.0",
            Author = skillData["author"]?.GetValue<string>() ?? "Unknown",
            SourcePath = destPath,
            InstalledAt = DateTime.UtcNow
        };

        // Load keywords and capabilities
        if (skillData["keywords"] is JsonArray keywordsArray)
        {
            skill.Keywords = keywordsArray
                .Select(k => k.GetValue<string>()!)
                .Where(k => !string.IsNullOrEmpty(k))
                .ToList();
        }

        if (skillData["capabilities"] is JsonArray capabilitiesArray)
        {
            skill.Capabilities = capabilitiesArray
                .Select(c => c.GetValue<string>()!)
                .Where(c => !string.IsNullOrEmpty(c))
                .ToList();
        }

        if (skillData["config"] is JsonObject configObj)
        {
            foreach (var prop in configObj)
            {
                skill.Config[prop.Key] = prop.Value;
            }
        }

        lock (_lock)
        {
            _skills[skill.Id] = skill;
            _skillsByName[skill.Name] = skill;
        }

        _logger.Information("Installed skill: {Name} ({Id})", skill.Name, skillId);

        SkillInstalled?.Invoke(this, new SkillInstalledEventArgs { Skill = skill });

        return skillId;
    }

    /// <summary>
    /// Uninstalls a skill.
    /// </summary>
    public bool UninstallSkill(string skillId)
    {
        lock (_lock)
        {
            if (!_skills.TryGetValue(skillId, out var skill))
            {
                _logger.Warning("Skill not found: {Id}", skillId);
                return false;
            }

            // Remove from registry
            _skills.Remove(skillId);
            _skillsByName.Remove(skill.Name);

            // Delete from disk
            if (skill.SourcePath != null && Directory.Exists(skill.SourcePath))
            {
                Directory.Delete(skill.SourcePath, recursive: true);
            }

            _logger.Information("Uninstalled skill: {Name} ({Id})", skill.Name, skillId);

            SkillUninstalled?.Invoke(this, new SkillUninstalledEventArgs { Skill = skill });

            return true;
        }
    }

    /// <summary>
    /// Gets a skill by ID.
    /// </summary>
    public Skill? GetSkill(string skillId)
    {
        lock (_lock)
        {
            return _skills.TryGetValue(skillId, out var skill) ? skill : null;
        }
    }

    /// <summary>
    /// Gets a skill by name.
    /// </summary>
    public Skill? GetSkillByName(string name)
    {
        lock (_lock)
        {
            return _skillsByName.TryGetValue(name, out var skill) ? skill : null;
        }
    }

    /// <summary>
    /// Gets all skills.
    /// </summary>
    public List<Skill> GetAllSkills()
    {
        lock (_lock)
        {
            return _skills.Values.ToList();
        }
    }

    /// <summary>
    /// Searches for skills by keyword.
    /// </summary>
    public List<Skill> SearchSkills(string query)
    {
        lock (_lock)
        {
            var queryLower = query.ToLower();
            return _skills.Values
                .Where(s =>
                    s.Name.ToLower().Contains(queryLower) ||
                    s.Description.ToLower().Contains(queryLower) ||
                    s.Keywords.Any(k => k.ToLower().Contains(queryLower)))
                .OrderByDescending(s => s.LastUsed)
                .ToList();
        }
    }

    /// <summary>
    /// Finds skills by capability.
    /// </summary>
    public List<Skill> FindSkillsByCapability(string capability)
    {
        lock (_lock)
        {
            var capabilityLower = capability.ToLower();
            return _skills.Values
                .Where(s => s.Capabilities.Any(c => c.ToLower() == capabilityLower))
                .OrderByDescending(s => s.LastUsed)
                .ToList();
        }
    }

    /// <summary>
    /// Executes a skill.
    /// </summary>
    public async Task<SkillExecutionResult> ExecuteSkillAsync(
        string skillId,
        Dictionary<string, object> parameters)
    {
        Skill? skill;
        lock (_lock)
        {
            if (!_skills.TryGetValue(skillId, out skill))
            {
                return new SkillExecutionResult
                {
                    Skill = new Skill { Id = skillId, Name = "Unknown", Description = "Unknown", Version = "0.0.0", Author = "Unknown" },
                    Success = false,
                    Output = $"Skill not found: {skillId}"
                };
            }

            skill.LastUsed = DateTime.UtcNow;
        }

        _logger.Information("Executing skill: {Name} ({Id})", skill.Name, skillId);

        try
        {
            // For now, just return a placeholder result
            // In a full implementation, this would execute the skill's code
            var output = $"Skill '{skill.Name}' executed with parameters: {string.Join(", ", parameters.Keys)}";

            var result = new SkillExecutionResult
            {
                Skill = skill,
                Success = true,
                Output = output
            };

            _logger.Information("Skill executed: {Name}", skill.Name);

            SkillExecuted?.Invoke(this, new SkillExecutedEventArgs { Skill = skill, Result = result });

            return result;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error executing skill: {Name}", skill.Name);

            return new SkillExecutionResult
            {
                Skill = skill,
                Success = false,
                Output = ex.Message,
                Error = ex
            };
        }
    }

    /// <summary>
    /// Updates a skill's configuration.
    /// </summary>
    public bool UpdateSkillConfig(string skillId, Dictionary<string, object> config)
    {
        lock (_lock)
        {
            if (!_skills.TryGetValue(skillId, out var skill))
            {
                return false;
            }

            skill.Config = config;

            // Save to disk
            if (skill.SourcePath != null)
            {
                var skillFile = Path.Combine(skill.SourcePath, "skill.json");
                var json = JsonSerializer.Serialize(skill, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                File.WriteAllText(skillFile, json);
            }

            _logger.Information("Updated config for skill: {Name}", skill.Name);

            return true;
        }
    }

    /// <summary>
    /// Clears all installed skills.
    /// </summary>
    public void Clear()
    {
        int count;
        lock (_lock)
        {
            count = _skills.Count;
            _skills.Clear();
            _skillsByName.Clear();
        }

        _logger.Information("Cleared {Count} skills", count);
    }

    /// <summary>
    /// Copies a directory recursively.
    /// </summary>
    private void DirectoryCopy(string source, string destination)
    {
        var dir = new DirectoryInfo(source);
        if (!dir.Exists) return;

        var dirs = dir.GetDirectories();
        Directory.CreateDirectory(destination);

        foreach (var subdir in dirs)
        {
            DirectoryCopy(subdir.FullName, Path.Combine(destination, subdir.Name));
        }

        var files = dir.GetFiles();
        foreach (var file in files)
        {
            file.CopyTo(Path.Combine(destination, file.Name));
        }
    }

    /// <summary>
    /// Disposes the manager.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _skills.Clear();
        _skillsByName.Clear();

        _logger.Information("SkillManager disposed");
    }
}

/// <summary>
/// Event arguments for skill installation.
/// </summary>
public class SkillInstalledEventArgs : EventArgs
{
    public required Skill Skill { get; init; }

    public SkillInstalledEventArgs()
    {
    }

    public SkillInstalledEventArgs(Skill skill)
    {
        Skill = skill;
    }
}

/// <summary>
/// Event arguments for skill uninstallation.
/// </summary>
public class SkillUninstalledEventArgs : EventArgs
{
    public required Skill Skill { get; init; }

    public SkillUninstalledEventArgs()
    {
    }

    public SkillUninstalledEventArgs(Skill skill)
    {
        Skill = skill;
    }
}

/// <summary>
/// Event arguments for skill execution.
/// </summary>
public class SkillExecutedEventArgs : EventArgs
{
    public required Skill Skill { get; init; }
    public required SkillExecutionResult Result { get; init; }

    public SkillExecutedEventArgs()
    {
    }

    public SkillExecutedEventArgs(Skill skill, SkillExecutionResult result)
    {
        Skill = skill;
        Result = result;
    }
}
