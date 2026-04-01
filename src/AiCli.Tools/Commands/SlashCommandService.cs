using AiCli.Core.Logging;

namespace AiCli.Core.Commands;

/// <summary>
/// Manages slash command registration, parsing, and dispatch.
/// Ported from packages/cli/src/utils/commands.ts (parseSlashCommand)
/// and packages/cli/src/ui/commandService.ts
/// </summary>
public class SlashCommandService
{
    private static readonly ILogger Logger = LoggerHelper.ForContext<SlashCommandService>();

    private readonly List<SlashCommand> _commands = new();

    // ─── Registration ─────────────────────────────────────────────────────────

    public void Register(SlashCommand command)
    {
        _commands.Add(command);
        Logger.Debug("Registered slash command: /{Name}", command.Name);
    }

    public void RegisterRange(IEnumerable<SlashCommand> commands)
    {
        foreach (var cmd in commands)
            Register(cmd);
    }

    public IReadOnlyList<SlashCommand> GetAll() => _commands.AsReadOnly();

    // ─── Parsing ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses a raw user input string into a resolved slash command and its arguments.
    /// Input must start with '/'.
    /// </summary>
    public ParsedSlashCommand Parse(string input)
    {
        var trimmed = input.TrimStart();
        if (!trimmed.StartsWith('/'))
            return new ParsedSlashCommand
            {
                CommandToExecute = null,
                Args = trimmed,
                CanonicalPath = Array.Empty<string>(),
            };

        // Split on whitespace: "/memory add some data" → ["memory", "add", "some", "data"]
        var parts = trimmed[1..].Trim()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0)
            return new ParsedSlashCommand
            {
                CommandToExecute = null,
                Args = string.Empty,
                CanonicalPath = Array.Empty<string>(),
            };

        var currentCommands = (IReadOnlyList<SlashCommand>)_commands;
        SlashCommand? commandToExecute = null;
        int pathIndex = 0;
        var canonicalPath = new List<string>();

        foreach (var part in parts)
        {
            // First: exact primary name match
            var found = currentCommands.FirstOrDefault(c =>
                string.Equals(c.Name, part, StringComparison.OrdinalIgnoreCase));

            // Second: alias match
            if (found == null)
            {
                found = currentCommands.FirstOrDefault(c =>
                    c.AltNames?.Any(a =>
                        string.Equals(a, part, StringComparison.OrdinalIgnoreCase)) == true);
            }

            if (found != null)
            {
                commandToExecute = found;
                canonicalPath.Add(found.Name);
                pathIndex++;

                if (found.SubCommands?.Count > 0)
                    currentCommands = found.SubCommands;
                else
                    break;
            }
            else
            {
                break;
            }
        }

        var args = string.Join(' ', parts.Skip(pathIndex));

        return new ParsedSlashCommand
        {
            CommandToExecute = commandToExecute,
            Args = args,
            CanonicalPath = canonicalPath,
        };
    }

    // ─── Execution ────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses and executes a slash command. Returns the result.
    /// Returns Unhandled if the command is not recognized.
    /// </summary>
    public async Task<SlashCommandResult> ExecuteAsync(
        string raw,
        CancellationToken cancellationToken = default)
    {
        var parsed = Parse(raw);

        if (parsed.CommandToExecute == null)
            return SlashCommandResult.Unhandled;

        if (parsed.CommandToExecute.Action == null)
        {
            // Parent command with sub-commands but no action → show sub-command help
            var subHelp = BuildSubCommandHelp(parsed.CommandToExecute);
            return SlashCommandResult.Print(subHelp);
        }

        var context = new SlashCommandContext
        {
            Raw = raw,
            CommandName = string.Join(" ", parsed.CanonicalPath),
            Args = parsed.Args,
        };

        try
        {
            return await parsed.CommandToExecute.Action(context, cancellationToken);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error executing slash command /{Command}", parsed.CommandToExecute.Name);
            return SlashCommandResult.Print($"Error executing command: {ex.Message}");
        }
    }

    // ─── Help ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a formatted help string listing all visible commands.
    /// </summary>
    public string BuildHelpText()
    {
        var lines = new System.Text.StringBuilder();
        lines.AppendLine("Available slash commands:");
        lines.AppendLine();

        foreach (var cmd in _commands.Where(c => !c.Hidden))
        {
            AppendCommandHelp(lines, cmd, depth: 0);
        }

        return lines.ToString().TrimEnd();
    }

    private static void AppendCommandHelp(
        System.Text.StringBuilder sb, SlashCommand cmd, int depth)
    {
        var indent = new string(' ', depth * 2);
        var aliases = cmd.AltNames?.Count > 0
            ? $" (aliases: {string.Join(", ", cmd.AltNames)})"
            : string.Empty;
        sb.AppendLine($"{indent}/{cmd.Name}{aliases}");
        sb.AppendLine($"{indent}  {cmd.Description}");

        if (cmd.SubCommands?.Count > 0)
        {
            foreach (var sub in cmd.SubCommands.Where(c => !c.Hidden))
                AppendCommandHelp(sb, sub, depth + 1);
        }
    }

    private static string BuildSubCommandHelp(SlashCommand parent)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"/{parent.Name} sub-commands:");
        sb.AppendLine();
        if (parent.SubCommands != null)
        {
            foreach (var sub in parent.SubCommands.Where(c => !c.Hidden))
                sb.AppendLine($"  /{parent.Name} {sub.Name} — {sub.Description}");
        }
        return sb.ToString().TrimEnd();
    }
}
