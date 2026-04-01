using AiCli.Core.Chat;
using AiCli.Core.Tools;

namespace AiCli.Core.Agents;

/// <summary>
/// Interface for agents.
/// </summary>
public interface IAgent
{
    /// <summary>
    /// Gets the agent ID.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Gets the agent name.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets the agent description.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Gets the agent kind.
    /// </summary>
    AgentKind Kind { get; }

    /// <summary>
    /// Gets the agent capabilities.
    /// </summary>
    List<string> Capabilities { get; }

    /// <summary>
    /// Gets the tool registry for this agent.
    /// </summary>
    ToolRegistry ToolRegistry { get; }

    /// <summary>
    /// Gets the chat session for this agent.
    /// </summary>
    IContentGenerator Chat { get; }

    /// <summary>
    /// Gets the execution state.
    /// </summary>
    AgentExecutionState State { get; }

    /// <summary>
    /// Executes the agent with an initial message.
    /// </summary>
    Task<AgentResult> ExecuteAsync(
        ContentMessage message,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a message to the agent.
    /// </summary>
    Task<ContentMessage> SendMessageAsync(
        ContentMessage message,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Pauses the agent execution.
    /// </summary>
    Task PauseAsync();

    /// <summary>
    /// Resumes the agent execution.
    /// </summary>
    Task ResumeAsync();

    /// <summary>
    /// Cancels the agent execution.
    /// </summary>
    Task CancelAsync();

    /// <summary>
    /// Resets the agent state.
    /// </summary>
    void Reset();

    /// <summary>
    /// Event raised when the agent emits an event.
    /// </summary>
    event EventHandler<AgentEvent>? OnEvent;
}