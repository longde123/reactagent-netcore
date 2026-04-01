using AiCli.Core.Logging;
using AiCli.Core.Types;
using Serilog;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiCli.Core.Policy;

/// <summary>
/// Policy decision types.
/// </summary>
public enum PolicyDecisionType
{
    Allow,
    Deny,
    AskUser,
    AskWithExplanation
}

/// <summary>
/// Policy scope.
/// </summary>
public enum PolicyScope
{
    Global,
    Project,
    Session,
    Command
}

/// <summary>
/// Resource type that policies can apply to.
/// </summary>
public enum ResourceType
{
    FileOperation,
    NetworkRequest,
    ShellExecution,
    AgentExecution,
    ToolInvocation,
    ConfigurationChange
}

/// <summary>
/// Policy definition.
/// </summary>
public record Policy
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required PolicyScope Scope { get; init; }
    public required ResourceType ResourceType { get; init; }
    public required PolicyDecisionType Decision { get; init; }
    public Dictionary<string, object> Conditions { get; init; } = new();
    public List<string> AllowedTools { get; init; } = new();
    public List<string> BlockedTools { get; init; } = new();
    public string? BlockMessage { get; init; }
    public bool IsEnabled { get; init; } = true;
    public int Priority { get; init; } = 0;
}

/// <summary>
/// Policy evaluation context.
/// </summary>
public record PolicyContext
{
    public required string SessionId { get; init; }
    public required string? UserId { get; init; }
    public required ResourceType ResourceType { get; init; }
    public required Dictionary<string, object> Resource { get; init; } = new();
    public required string? ToolName { get; init; }
    public Dictionary<string, object> Environment { get; init; } = new();
}

/// <summary>
/// Policy evaluation result.
/// </summary>
public record PolicyEvaluation
{
    public required Policy Policy { get; init; }
    public required PolicyDecisionType Decision { get; init; }
    public required string Reason { get; init; }
    public Dictionary<string, object> Metadata { get; init; } = new();
}

/// <summary>
/// Event arguments for policy events.
/// </summary>
public class PolicyEventArgs : EventArgs
{
    public required Policy Policy { get; init; }
    public required PolicyContext Context { get; init; }
    public required PolicyEvaluation Result { get; init; }

    public PolicyEventArgs()
    {
    }

    public PolicyEventArgs(Policy policy, PolicyContext context, PolicyEvaluation result)
    {
        Policy = policy;
        Context = context;
        Result = result;
    }
}

public class PolicyChangedEventArgs : EventArgs
{
    public string? PolicyId { get; init; }
    public Policy? Policy { get; init; }
}

/// <summary>
/// Engine for evaluating policies.
/// </summary>
public class PolicyEngine : IDisposable
{
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, Policy> _policies = new();
    private readonly Dictionary<string, List<Policy>> _policiesByScope = new();
    private readonly Dictionary<string, List<Policy>> _policiesByResourceType = new();
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>
    /// Event raised when a policy is evaluated.
    /// </summary>
    public event EventHandler<PolicyEventArgs>? PolicyEvaluated;

    /// <summary>
    /// Event raised when a policy is added.
    /// </summary>
    public event EventHandler<PolicyChangedEventArgs>? PolicyAdded;

    /// <summary>
    /// Event raised when a policy is removed.
    /// </summary>
    public event EventHandler<PolicyChangedEventArgs>? PolicyRemoved;

    /// <summary>
    /// Gets the number of active policies.
    /// </summary>
    public int PolicyCount
    {
        get
        {
            lock (_lock)
            {
                return _policies.Values.Count(p => p.IsEnabled);
            }
        }
    }

    public PolicyEngine()
    {
        _logger = LoggerHelper.ForContext<PolicyEngine>();
    }

    /// <summary>
    /// Initializes the policy engine.
    /// </summary>
    public async Task InitializeAsync(string policyDirectory)
    {
        if (!Directory.Exists(policyDirectory))
        {
            _logger.Information("Policy directory not found: {Path}", policyDirectory);
            return;
        }

        var policyFiles = Directory.GetFiles(policyDirectory, "*.policy.json");
        var tomlFiles = Directory.GetFiles(policyDirectory, "*.toml");

        foreach (var file in policyFiles)
        {
            await LoadPolicyFromFileAsync(file);
        }

        // Note: TOML parsing would require a library like Tomlyn
        // For now, we only support JSON policy files
        _logger.Information("Loaded {Count} policies", _policies.Count);
    }

    /// <summary>
    /// Loads a policy from a file.
    /// </summary>
    private async Task LoadPolicyFromFileAsync(string filePath)
    {
        try
        {
            var json = await File.ReadAllTextAsync(filePath);
            var policy = JsonSerializer.Deserialize<Policy>(json);

            if (policy == null)
            {
                _logger.Warning("Invalid policy file: {Path}", filePath);
                return;
            }

            AddPolicy(policy);

            _logger.Verbose("Loaded policy: {Name} ({Id})", policy.Name, policy.Id);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error loading policy: {Path}", filePath);
        }
    }

    /// <summary>
    /// Adds a policy.
    /// </summary>
    public void AddPolicy(Policy policy)
    {
        lock (_lock)
        {
            _policies[policy.Id] = policy;

            // Index by scope
            if (!_policiesByScope.ContainsKey(policy.Scope.ToString()))
            {
                _policiesByScope[policy.Scope.ToString()] = new List<Policy>();
            }
            _policiesByScope[policy.Scope.ToString()].Add(policy);

            // Index by resource type
            if (!_policiesByResourceType.ContainsKey(policy.ResourceType.ToString()))
            {
                _policiesByResourceType[policy.ResourceType.ToString()] = new List<Policy>();
            }
            _policiesByResourceType[policy.ResourceType.ToString()].Add(policy);
        }

        PolicyAdded?.Invoke(this, new PolicyChangedEventArgs { Policy = policy });

        _logger.Information("Added policy: {Name} ({Id})", policy.Name, policy.Id);
    }

    /// <summary>
    /// Removes a policy.
    /// </summary>
    public bool RemovePolicy(string policyId)
    {
        Policy? removedPolicy = null;

        lock (_lock)
        {
            if (!_policies.TryGetValue(policyId, out var policy))
            {
                return false;
            }

            _policies.TryRemove(policyId, out removedPolicy);
            if (removedPolicy != null)
            {
                _policiesByScope[removedPolicy.Scope.ToString()].Remove(removedPolicy);
                _policiesByResourceType[removedPolicy.ResourceType.ToString()].Remove(removedPolicy);
            }
        }

        PolicyRemoved?.Invoke(this, new PolicyChangedEventArgs { PolicyId = policyId, Policy = removedPolicy });

        if (removedPolicy != null)
        {
            _logger.Information("Removed policy: {Name} ({Id})", removedPolicy.Name, policyId);
        }

        return true;
    }

    /// <summary>
    /// Gets a policy by ID.
    /// </summary>
    public Policy? GetPolicy(string policyId)
    {
        lock (_lock)
        {
            return _policies.TryGetValue(policyId, out var policy) ? policy : null;
        }
    }

    /// <summary>
    /// Gets all policies.
    /// </summary>
    public List<Policy> GetAllPolicies()
    {
        lock (_lock)
        {
            return _policies.Values.ToList();
        }
    }

    /// <summary>
    /// Gets policies by scope.
    /// </summary>
    public List<Policy> GetPoliciesByScope(PolicyScope scope)
    {
        lock (_lock)
        {
            return _policiesByScope.TryGetValue(scope.ToString(), out var policies)
                ? policies.Where(p => p.IsEnabled).ToList()
                : new List<Policy>();
        }
    }

    /// <summary>
    /// Gets policies by resource type.
    /// </summary>
    public List<Policy> GetPoliciesByResourceType(ResourceType resourceType)
    {
        lock (_lock)
        {
            return _policiesByResourceType.TryGetValue(resourceType.ToString(), out var policies)
                ? policies.Where(p => p.IsEnabled).ToList()
                : new List<Policy>();
        }
    }

    /// <summary>
    /// Evaluates policies for a given context.
    /// </summary>
    public PolicyEvaluation Evaluate(PolicyContext context)
    {
        var applicablePolicies = GetApplicablePolicies(context);
        var evaluation = new PolicyEvaluation
        {
            Policy = null!,
            Decision = PolicyDecisionType.Allow,
            Reason = "No applicable policies",
            Metadata = new Dictionary<string, object>
            {
                ["evaluated_policies"] = applicablePolicies.Count
            }
        };

        // Sort policies by priority (higher first)
        var sortedPolicies = applicablePolicies
            .OrderByDescending(p => p.Priority)
            .ToList();

        foreach (var policy in sortedPolicies)
        {
            var decision = EvaluateSinglePolicy(policy, context);

            // First non-allow decision wins
            if (decision != PolicyDecisionType.Allow)
            {
                evaluation = new PolicyEvaluation
                {
                    Policy = policy,
                    Decision = decision,
                    Reason = GetReason(policy, context, decision),
                    Metadata = new Dictionary<string, object>
                    {
                        ["policy_id"] = policy.Id,
                        ["policy_name"] = policy.Name
                    }
                };

                _logger.Information(
                    "Policy evaluated: {Name} -> {Decision}, Reason: {Reason}",
                    policy.Name,
                    decision,
                    evaluation.Reason);

                PolicyEvaluated?.Invoke(this, new PolicyEventArgs { Policy = policy, Context = context, Result = evaluation });

                return evaluation;
            }
        }

        _logger.Verbose("Policy evaluation: Allow (no restrictive policies)");

        PolicyEvaluated?.Invoke(this, new PolicyEventArgs { Policy = null!, Context = context, Result = evaluation });

        return evaluation;
    }

    /// <summary>
    /// Gets applicable policies for a context.
    /// </summary>
    private List<Policy> GetApplicablePolicies(PolicyContext context)
    {
        var policies = new List<Policy>();

        lock (_lock)
        {
            // Filter by resource type
            if (_policiesByResourceType.TryGetValue(context.ResourceType.ToString(), out var resourcePolicies))
            {
                policies.AddRange(resourcePolicies);
            }

            // Filter by tool name
            if (!string.IsNullOrEmpty(context.ToolName))
            {
                policies = policies.Where(p =>
                    (p.AllowedTools.Count == 0 || p.AllowedTools.Contains(context.ToolName!)) &&
                    (p.BlockedTools.Count == 0 || !p.BlockedTools.Contains(context.ToolName!))).ToList();
            }

            // Filter by enabled status
            policies = policies.Where(p => p.IsEnabled).ToList();
        }

        return policies;
    }

    /// <summary>
    /// Evaluates a single policy against a context.
    /// </summary>
    private PolicyDecisionType EvaluateSinglePolicy(Policy policy, PolicyContext context)
    {
        // Check blocked tools
        if (policy.BlockedTools.Count > 0 && !string.IsNullOrEmpty(context.ToolName))
        {
            if (policy.BlockedTools.Contains(context.ToolName))
            {
                return policy.Decision;
            }
        }

        // Check allowed tools (if specified)
        if (policy.AllowedTools.Count > 0 && !string.IsNullOrEmpty(context.ToolName))
        {
            if (!policy.AllowedTools.Contains(context.ToolName))
            {
                return PolicyDecisionType.Deny;
            }
        }

        // Evaluate conditions
        foreach (var (key, value) in policy.Conditions)
        {
            if (!EvaluateCondition(key, value, context))
            {
                return PolicyDecisionType.Allow; // Condition not met, policy doesn't apply
            }
        }

        return policy.Decision;
    }

    /// <summary>
    /// Evaluates a single policy condition.
    /// </summary>
    private bool EvaluateCondition(string key, object value, PolicyContext context)
    {
        return key switch
        {
            "user" => MatchesValue(value, context.UserId),
            "session" => MatchesValue(value, context.SessionId),
            "tool" => MatchesValue(value, context.ToolName),
            "time_range" => EvaluateTimeRange(value, context),
            "max_tokens" => EvaluateMaxTokens(value, context),
            "requires_approval" => EvaluateRequiresApproval(value, context),
            _ => true // Unknown condition, pass
        };
    }

    /// <summary>
    /// Checks if a value matches.
    /// </summary>
    private bool MatchesValue(object expected, string? actual)
    {
        if (expected is string str)
        {
            return str == "*" || str.Equals(actual, StringComparison.OrdinalIgnoreCase);
        }

        if (expected is JsonArray arr)
        {
            return arr.Contains(actual);
        }

        return false;
    }

    /// <summary>
    /// Evaluates a time range condition.
    /// </summary>
    private bool EvaluateTimeRange(object value, PolicyContext context)
    {
        if (value is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item is JsonObject)
                {
                    // Parse time range object
                    // For simplicity, always allow for now
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Evaluates a max tokens condition.
    /// </summary>
    private bool EvaluateMaxTokens(object value, PolicyContext context)
    {
        var maxTokens = value switch
        {
            JsonValue jv when jv.TryGetValue<int>(out var parsed) => parsed,
            JsonElement elem when elem.ValueKind == JsonValueKind.Number => elem.GetInt32(),
            int i => i,
            long l => (int)l,
            _ => (int?)null
        };

        if (!maxTokens.HasValue)
        {
            return true;
        }

        var sessionTokensObj = context.Environment.GetValueOrDefault("session_tokens", 0);
        var sessionTokens = sessionTokensObj switch
        {
            int i => i,
            long l => (int)l,
            JsonElement elem when elem.ValueKind == JsonValueKind.Number => elem.GetInt32(),
            JsonValue jv when jv.TryGetValue<int>(out var parsed) => parsed,
            _ => 0
        };

        return sessionTokens < maxTokens.Value;
    }

    /// <summary>
    /// Evaluates a requires approval condition.
    /// </summary>
    private bool EvaluateRequiresApproval(object value, PolicyContext context)
    {
        return value switch
        {
            bool b => b,
            JsonElement elem when elem.ValueKind == JsonValueKind.True => true,
            JsonValue jv when jv.TryGetValue<bool>(out var parsed) => parsed,
            _ => false
        };
    }

    /// <summary>
    /// Gets a human-readable reason for a policy decision.
    /// </summary>
    private string GetReason(Policy policy, PolicyContext context, PolicyDecisionType decision)
    {
        return decision switch
        {
            PolicyDecisionType.Allow => $"Policy '{policy.Name}' allows this action",
            PolicyDecisionType.Deny => policy.BlockMessage ?? $"Policy '{policy.Name}' denies this action",
            PolicyDecisionType.AskUser => $"Policy '{policy.Name}' requires user approval",
            PolicyDecisionType.AskWithExplanation => $"Policy '{policy.Name}' requires approval: {policy.Description}",
            _ => "Unknown policy decision"
        };
    }

    /// <summary>
    /// Clears all policies.
    /// </summary>
    public void Clear()
    {
        var count = 0;
        lock (_lock)
        {
            count = _policies.Count;
            _policies.Clear();
            _policiesByScope.Clear();
            _policiesByResourceType.Clear();
        }

        _logger.Information("Cleared {Count} policies", count);
    }

    /// <summary>
    /// Exports policies to a file.
    /// </summary>
    public async Task ExportAsync(string filePath)
    {
        List<Policy> policies;

        lock (_lock)
        {
            policies = _policies.Values.ToList();
        }

        var json = JsonSerializer.Serialize(policies, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        await File.WriteAllTextAsync(filePath, json);

        _logger.Information("Exported {Count} policies to: {Path}", policies.Count, filePath);
    }

    /// <summary>
    /// Imports policies from a file.
    /// </summary>
    public async Task ImportAsync(string filePath)
    {
        var json = await File.ReadAllTextAsync(filePath);
        var policies = JsonSerializer.Deserialize<List<Policy>>(json) ?? new();

        foreach (var policy in policies)
        {
            AddPolicy(policy);
        }

        _logger.Information("Imported {Count} policies from: {Path}", policies.Count, filePath);
    }

    /// <summary>
    /// Disposes the engine.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Clear();

        _logger.Information("PolicyEngine disposed");
    }
}
