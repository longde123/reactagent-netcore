namespace AiCli.Core.Commands;

/// <summary>
/// Category/kind of a slash command.
/// </summary>
public enum CommandKind
{
    BuiltIn,
    Extension,
    McpPrompt,
    Agent,
}

/// <summary>
/// Context passed to a slash command action.
/// Contains services and state available during command execution.
/// </summary>
public record SlashCommandContext
{
    /// <summary>
    /// The raw input string from the user.
    /// </summary>
    public required string Raw { get; init; }

    /// <summary>
    /// The matched command name.
    /// </summary>
    public required string CommandName { get; init; }

    /// <summary>
    /// The arguments string following the command path.
    /// </summary>
    public required string Args { get; init; }

    /// <summary>
    /// Additional services or state. Keyed by name for extensibility.
    /// </summary>
    public Dictionary<string, object> Services { get; init; } = new();
}

/// <summary>
/// Result returned by a slash command action.
/// </summary>
public record SlashCommandResult
{
    /// <summary>
    /// Text to display to the user.
    /// </summary>
    public string? Output { get; init; }

    /// <summary>
    /// Whether the session should be cleared after this command.
    /// </summary>
    public bool ShouldClear { get; init; }

    /// <summary>
    /// Whether the application should quit.
    /// </summary>
    public bool ShouldQuit { get; init; }

    /// <summary>
    /// Whether the command was handled (vs. not found).
    /// </summary>
    public bool Handled { get; init; } = true;

    public static readonly SlashCommandResult Unhandled = new() { Handled = false };
    public static readonly SlashCommandResult Ok = new() { Handled = true };
    public static SlashCommandResult Print(string text) =>
        new() { Handled = true, Output = text };
}

/// <summary>
/// A slash command definition.
/// Mirrors packages/cli/src/ui/commands/types.ts SlashCommand
/// </summary>
public record SlashCommand
{
    /// <summary>
    /// Primary name (without the leading '/').
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Alternative names / aliases.
    /// </summary>
    public IReadOnlyList<string>? AltNames { get; init; }

    /// <summary>
    /// Short description shown in help.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Whether this command is hidden from /help output.
    /// </summary>
    public bool Hidden { get; init; }

    /// <summary>
    /// Command category.
    /// </summary>
    public CommandKind Kind { get; init; } = CommandKind.BuiltIn;

    /// <summary>
    /// Optional action invoked when the command is executed.
    /// </summary>
    public Func<SlashCommandContext, CancellationToken, Task<SlashCommandResult>>? Action { get; init; }

    /// <summary>
    /// Optional sub-commands.
    /// </summary>
    public IReadOnlyList<SlashCommand>? SubCommands { get; init; }
}

/// <summary>
/// Result of parsing a raw slash command string.
/// </summary>
public record ParsedSlashCommand
{
    public SlashCommand? CommandToExecute { get; init; }
    public required string Args { get; init; }
    public required IReadOnlyList<string> CanonicalPath { get; init; }
}
