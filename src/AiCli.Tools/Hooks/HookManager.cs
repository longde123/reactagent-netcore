using AiCli.Core.Logging;
using AiCli.Core.Types;
using System.Collections.Concurrent;
using System.Text.Json;

namespace AiCli.Core.Hooks;

/// <summary>
/// Hook types.
/// </summary>
public enum HookType
{
    PreTool,
    PostTool,
    PreAgent,
    PostAgent,
    PreCommand,
    PostCommand,
    PreWrite,
    PostWrite,
    PreShell,
    PostShell,
    Error,
    Approval
}

/// <summary>
/// Hook execution context.
/// </summary>
public record HookContext
{
    public required string HookId { get; init; }
    public required HookType Type { get; init; }
    public required string SessionId { get; init; }
    public required Dictionary<string, object> Arguments { get; init; } = new();
    public Dictionary<string, object> Environment { get; init; } = new();
    public required CancellationToken CancellationToken { get; init; }
}

/// <summary>
/// Hook execution result.
/// </summary>
public record HookResult
{
    public required string HookId { get; init; }
    public required bool Success { get; init; }
    public required string Output { get; init; }
    public Dictionary<string, object> Metadata { get; init; } = new();
    public Exception? Error { get; init; }
    public bool ShouldContinue { get; init; } = true;
}

/// <summary>
/// Hook definition.
/// </summary>
public record Hook
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required HookType Type { get; init; }
    public required string Handler { get; init; }
    public required string? Module { get; init; }
    public required int Priority { get; init; } = 0;
    public bool IsEnabled { get; init; } = true;
    public bool IsAsync { get; init; } = false;
    public Dictionary<string, object> Configuration { get; init; } = new();
    public string? Condition { get; init; }
}

/// <summary>
/// Event arguments for hook events.
/// </summary>
public class HookEventArgs : EventArgs
{
    public required Hook Hook { get; init; }
    public required HookContext Context { get; init; }
    public required HookResult Result { get; init; }

    public HookEventArgs()
    {
    }

    public HookEventArgs(Hook hook, HookContext context, HookResult result)
    {
        Hook = hook;
        Context = context;
        Result = result;
    }
}

/// <summary>
/// Manager for hooks.
/// </summary>
public class HookManager : IDisposable
{
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, Hook> _hooks = new();
    private readonly Dictionary<string, List<Hook>> _hooksByType = new();
    private readonly Dictionary<string, List<Hook>> _hooksByModule = new();
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>
    /// Event raised when a hook is executed.
    /// </summary>
    public event EventHandler<HookEventArgs>? HookExecuted;

    /// <summary>
    /// Event raised when a hook fails.
    /// </summary>
    public event EventHandler<HookEventArgs>? HookFailed;

    /// <summary>
    /// Event raised when a hook is added.
    /// </summary>
    public event EventHandler<HookEventArgs>? HookAdded;

    /// <summary>
    /// Event raised when a hook is removed.
    /// </summary>
    public event EventHandler<HookEventArgs>? HookRemoved;

    /// <summary>
    /// Gets the number of active hooks.
    /// </summary>
    public int HookCount
    {
        get
        {
            lock (_lock)
            {
                return _hooks.Values.Count(h => h.IsEnabled);
            }
        }
    }

    public HookManager()
    {
        _logger = LoggerHelper.ForContext<HookManager>();
    }

    /// <summary>
    /// Initializes the hook manager.
    /// </summary>
    public async Task InitializeAsync(string hooksDirectory)
    {
        if (!Directory.Exists(hooksDirectory))
        {
            _logger.Information("Hooks directory not found: {Path}", hooksDirectory);
            return;
        }

        var hookFiles = Directory.GetFiles(hooksDirectory, "*.hook.json");

        foreach (var file in hookFiles)
        {
            await LoadHookFromFileAsync(file);
        }

        _logger.Information("Loaded {Count} hooks", _hooks.Count);
    }

    /// <summary>
    /// Loads a hook from a file.
    /// </summary>
    private async Task LoadHookFromFileAsync(string filePath)
    {
        try
        {
            var json = await File.ReadAllTextAsync(filePath);
            var hook = JsonSerializer.Deserialize<Hook>(json);

            if (hook == null)
            {
                _logger.Warning("Invalid hook file: {Path}", filePath);
                return;
            }

            AddHook(hook);

            _logger.Verbose("Loaded hook: {Name} ({Id})", hook.Name, hook.Id);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error loading hook: {Path}", filePath);
        }
    }

    /// <summary>
    /// Adds a hook.
    /// </summary>
    public void AddHook(Hook hook)
    {
        lock (_lock)
        {
            _hooks[hook.Id] = hook;

            // Index by type
            if (!_hooksByType.ContainsKey(hook.Type.ToString()))
            {
                _hooksByType[hook.Type.ToString()] = new List<Hook>();
            }
            _hooksByType[hook.Type.ToString()].Add(hook);

            // Index by module
            if (!string.IsNullOrEmpty(hook.Module))
            {
                if (!_hooksByModule.ContainsKey(hook.Module!))
                {
                    _hooksByModule[hook.Module!] = new List<Hook>();
                }
                _hooksByModule[hook.Module!].Add(hook);
            }
        }

        HookAdded?.Invoke(this, new HookEventArgs { Hook = hook, Context = new HookContext { HookId = hook.Id, Type = hook.Type, SessionId = "", Arguments = new Dictionary<string, object>(), CancellationToken = CancellationToken.None }, Result = new HookResult { HookId = hook.Id, Success = true, Output = "Added" } });

        _logger.Information("Added hook: {Name} ({Id})", hook.Name, hook.Id);
    }

    /// <summary>
    /// Removes a hook.
    /// </summary>
    public bool RemoveHook(string hookId)
    {
        lock (_lock)
        {
            if (!_hooks.TryRemove(hookId, out var hook))
            {
                return false;
            }

            if (_hooksByType.TryGetValue(hook.Type.ToString(), out var list))
            {
                list.Remove(hook);
            }

            if (!string.IsNullOrEmpty(hook.Module) && _hooksByModule.TryGetValue(hook.Module!, out var mlist))
            {
                mlist.Remove(hook);
            }

            HookRemoved?.Invoke(this, new HookEventArgs { Hook = hook, Context = new HookContext { HookId = hook.Id, Type = hook.Type, SessionId = "", Arguments = new Dictionary<string, object>(), CancellationToken = CancellationToken.None }, Result = new HookResult { HookId = hook.Id, Success = true, Output = "Removed" } });

            _logger.Information("Removed hook: {Name} ({Id})", hook.Name, hookId);

            return true;
        }
    }

    /// <summary>
    /// Gets a hook by ID.
    /// </summary>
    public Hook? GetHook(string hookId)
    {
        lock (_lock)
        {
            return _hooks.TryGetValue(hookId, out var hook) ? hook : null;
        }
    }

    /// <summary>
    /// Gets all hooks.
    /// </summary>
    public List<Hook> GetAllHooks()
    {
        lock (_lock)
        {
            return _hooks.Values.ToList();
        }
    }

    /// <summary>
    /// Gets hooks by type.
    /// </summary>
    public List<Hook> GetHooksByType(HookType type)
    {
        lock (_lock)
        {
            return _hooksByType.TryGetValue(type.ToString(), out var hooks)
                ? hooks.Where(h => h.IsEnabled).OrderByDescending(h => h.Priority).ToList()
                : new List<Hook>();
        }
    }

    /// <summary>
    /// Gets hooks by module.
    /// </summary>
    public List<Hook> GetHooksByModule(string module)
    {
        lock (_lock)
        {
            return _hooksByModule.TryGetValue(module, out var hooks)
                ? hooks.Where(h => h.IsEnabled).ToList()
                : new List<Hook>();
        }
    }

    /// <summary>
    /// Executes hooks for a given type.
    /// </summary>
    public async Task<HookResult> ExecuteHooksAsync(
        HookType type,
        HookContext context,
        bool stopOnFailure = false)
    {
        var hooks = GetHooksByType(type);
        var results = new List<HookResult>();

        _logger.Verbose("Executing {Count} hooks for type: {Type}", hooks.Count, type);

        foreach (var hook in hooks)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            try
            {
                var result = await ExecuteSingleHookAsync(hook, context);

                results.Add(result);

                if (!result.Success && stopOnFailure)
                {
                    _logger.Error("Hook failed, stopping execution: {Name}", hook.Name);
                    return result;
                }

                if (!result.ShouldContinue)
                {
                    _logger.Information("Hook requested to stop: {Name}", hook.Name);
                    return result;
                }
            }
            catch (Exception ex)
            {
                var errorResult = new HookResult
                {
                    HookId = hook.Id,
                    Success = false,
                    Output = $"Hook execution failed: {ex.Message}",
                    Error = ex,
                    ShouldContinue = true
                };

                results.Add(errorResult);
                HookFailed?.Invoke(this, new HookEventArgs { Hook = hook, Context = context, Result = errorResult });

                if (stopOnFailure)
                {
                    return errorResult;
                }
            }
        }

        // Return the last result or success
        var finalResult = results.Count > 0
            ? results[^1]
            : new HookResult
            {
                HookId = "none",
                Success = true,
                Output = "No hooks executed",
                ShouldContinue = true
            };

        return finalResult;
    }

    /// <summary>
    /// Executes a single hook.
    /// </summary>
    private async Task<HookResult> ExecuteSingleHookAsync(Hook hook, HookContext context)
    {
        _logger.Verbose("Executing hook: {Name} ({Id})", hook.Name, hook.Id);

        var output = string.Empty;
        var shouldContinue = true;

        // For now, hooks are just placeholders
        // In a real implementation, this would:
        // 1. Load the handler module
        // 2. Execute the handler function
        // 3. Capture output and errors

        if (hook.IsAsync)
        {
            await Task.Delay(10); // Simulate async work
        }

        output = $"Hook '{hook.Name}' executed successfully";

        var result = new HookResult
        {
            HookId = hook.Id,
            Success = true,
            Output = output,
            ShouldContinue = shouldContinue,
            Metadata = new Dictionary<string, object>
            {
                ["hook_name"] = hook.Name,
                ["hook_type"] = hook.Type.ToString()
            }
        };

        HookExecuted?.Invoke(this, new HookEventArgs { Hook = hook, Context = context, Result = result });

        return result;
    }

    /// <summary>
    /// Clears all hooks.
    /// </summary>
    public void Clear()
    {
        int count;

        lock (_lock)
        {
            count = _hooks.Count;
            _hooks.Clear();
            _hooksByType.Clear();
            _hooksByModule.Clear();
        }

        _logger.Information("Cleared {Count} hooks", count);
    }

    /// <summary>
    /// Exports hooks to a file.
    /// </summary>
    public async Task ExportAsync(string filePath)
    {
        List<Hook> hooks;

        lock (_lock)
        {
            hooks = _hooks.Values.ToList();
        }

        var json = JsonSerializer.Serialize(hooks, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        await File.WriteAllTextAsync(filePath, json);

        _logger.Information("Exported {Count} hooks to: {Path}", hooks.Count, filePath);
    }

    /// <summary>
    /// Imports hooks from a file.
    /// </summary>
    public async Task ImportAsync(string filePath)
    {
        var json = await File.ReadAllTextAsync(filePath);
        var hooks = JsonSerializer.Deserialize<List<Hook>>(json) ?? new();

        foreach (var hook in hooks)
        {
            AddHook(hook);
        }

        _logger.Information("Imported {Count} hooks from: {Path}", hooks.Count, filePath);
    }

    /// <summary>
    /// Disposes manager.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Clear();

        _logger.Information("HookManager disposed");
    }
}
