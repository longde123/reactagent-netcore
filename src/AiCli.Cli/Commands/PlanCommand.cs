using Spectre.Console;
using System.CommandLine;
using System.CommandLine.Invocation;
using AiCli.Core.Agents;
using AiCli.Core.Agents.Builtin;
using AiCli.Core.Chat;
using AiCli.Core.Configuration;
using AiCli.Core.Logging;
using AiCli.Core.Tools;
using AiCli.Core.Tools.Builtin;
using AiCli.Core.Types;

namespace AiCli.Cli.Commands;

/// <summary>
/// Handler for the plan command — two-phase Plan → Act workflow.
/// Phase 1 (Plan): PlanAgent explores codebase in read-only mode, produces an implementation plan.
/// Phase 2 (Act):  CodeAgent executes the approved plan with full write tools.
/// </summary>
public class PlanCommand : CommandBase
{
    private readonly Argument<string> _taskArgument;

    public override Command Command { get; }

    public PlanCommand(Config config) : base(config)
    {
        Command = new Command("plan")
        {
            Description = "Enter plan mode for creating implementation plans"
        };

        // Add subcommands
        var enterCommand = new Command("enter")
        {
            Description = "Enter plan mode: explore codebase and create an implementation plan"
        };
        _taskArgument = new Argument<string>("task")
        {
            Description = "The task to create a plan for",
            Arity = ArgumentArity.ZeroOrOne
        };
        enterCommand.AddArgument(_taskArgument);
        enterCommand.SetHandler(async context => context.ExitCode = await HandleEnterAsync(context));

        var exitCommand = new Command("exit")
        {
            Description = "Exit plan mode"
        };
        exitCommand.SetHandler(async context => context.ExitCode = await HandleExitAsync(context));

        // Add subcommands to parent
        Command.AddCommand(enterCommand);
        Command.AddCommand(exitCommand);
    }

    /// <summary>
    /// Handles the plan enter subcommand — full Plan → Approve → Act workflow.
    /// </summary>
    private async Task<int> HandleEnterAsync(InvocationContext context)
    {
        var task = context.ParseResult.GetValueForArgument(_taskArgument);

        _console.MarkupLine("[bold cyan]╔══════════════════════════════════════╗[/]");
        _console.MarkupLine("[bold cyan]║          Plan Mode Activated         ║[/]");
        _console.MarkupLine("[bold cyan]╚══════════════════════════════════════╝[/]");
        _console.WriteLine();
        DisplayInfo("The AI will:");
        DisplayInfo("  1. Explore the codebase to understand existing code");
        DisplayInfo("  2. Create a detailed implementation plan");
        DisplayInfo("  3. Present the plan for your approval");
        DisplayInfo("  4. Execute the plan after you confirm");
        _console.WriteLine();

        // ── 获取任务描述 ────────────────────────────────────────────
        if (string.IsNullOrEmpty(task))
        {
            _console.MarkupLine("[yellow]No task specified. Enter your task:[/]");
            var prompt = new TextPrompt<string>("[green]Task:[/]")
                .PromptStyle("green")
                .AllowEmpty();

            try
            {
                task = _console.Prompt(prompt);
            }
            catch (OperationCanceledException)
            {
                DisplayInfo("Plan mode cancelled.");
                return 0;
            }

            if (string.IsNullOrWhiteSpace(task))
            {
                DisplayError("No task provided. Exiting plan mode.");
                return 1;
            }
        }

        _console.MarkupLine($"\n[bold]Task:[/] {Markup.Escape(task)}");

        // ════════════════════════════════════════════════════════════
        //  Phase 1: Plan — 只读探索 + 生成计划
        // ════════════════════════════════════════════════════════════
        IContentGenerator? contentGenerator = null;
        try
        {
            contentGenerator = ContentGeneratorFactory.Create(_config);

            // 获取 Agent 用的生成器（与 AgentCommand 一致）
            IContentGenerator agentGen = contentGenerator;
            if (contentGenerator is MultiModelOrchestrator mmo)
            {
                agentGen = mmo.GetGenerator(ModelRole.Fast);
            }

            // 只读工具集：只注册探索工具，不注册写入工具
            var targetDir = Directory.GetCurrentDirectory();
            var planToolRegistry = new ToolRegistry();
            planToolRegistry.RegisterDiscoveredTool(new ReadFileTool(targetDir));
            planToolRegistry.RegisterDiscoveredTool(new GrepTool(targetDir));
            planToolRegistry.RegisterDiscoveredTool(new GlobTool(targetDir));
            planToolRegistry.RegisterDiscoveredTool(new LsTool(targetDir));

            // Plan-only agent（只读模式）
            var planAgent = new PlanAgent("plan-session", planToolRegistry, agentGen, planOnlyMode: true);

            // ── 实时渲染 ───────────────────────────────────────────
            var renderer = new LiveTaskListRenderer();
            var thinkingBuf = new System.Text.StringBuilder();
            var thinkingStart = DateTime.UtcNow;
            bool thinkingActive = false;

            planAgent.OnEvent += (_, e) =>
            {
                switch (e.Type)
                {
                    case AgentEventType.Thinking when !string.IsNullOrEmpty(e.Message):
                        thinkingBuf.Append(e.Message);
                        var preview = TailText(thinkingBuf.ToString(), 55);
                        if (!thinkingActive)
                        {
                            renderer.Add($"◆ 思考中  {preview}");
                            thinkingActive = true;
                        }
                        else
                        {
                            renderer.UpdateLast($"◆ 思考中  {preview}");
                        }
                        break;

                    case AgentEventType.ToolCalled when !string.IsNullOrEmpty(e.Message):
                        if (thinkingActive)
                        {
                            var secs = (DateTime.UtcNow - thinkingStart).TotalSeconds;
                            renderer.CompleteLastWith($"◆ 思考完成 ({secs:F1}s)");
                            thinkingActive = false;
                            thinkingBuf.Clear();
                            thinkingStart = DateTime.UtcNow;
                        }
                        renderer.Add(e.Message);
                        break;

                    case AgentEventType.ToolCompleted:
                        renderer.CompleteLast();
                        break;
                }
            };

            _console.MarkupLine("\n[bold yellow]Phase 1: Planning (read-only exploration)[/]");
            _console.MarkupLine("[dim]─────────────────────────────────────────[/]");

            var planResult = await planAgent.ExecuteAsync(ContentMessage.UserMessage(task));

            // 收尾思考渲染
            if (thinkingActive)
            {
                var secs = (DateTime.UtcNow - thinkingStart).TotalSeconds;
                renderer.CompleteLastWith($"◆ 思考完成 ({secs:F1}s)");
            }
            renderer.PrintCompleted();

            // ── 提取计划文本 ───────────────────────────────────────
            var planText = ExtractPlanText(planResult);

            if (string.IsNullOrWhiteSpace(planText))
            {
                DisplayWarning("Agent did not produce a usable plan.");
                return 1;
            }

            // ── 展示计划 ───────────────────────────────────────────
            _console.WriteLine();
            _console.MarkupLine("[bold yellow]════════════════════════════════════════[/]");
            _console.MarkupLine("[bold yellow]       Implementation Plan               [/]");
            _console.MarkupLine("[bold yellow]════════════════════════════════════════[/]");
            _console.WriteLine();

            // 逐行输出计划（用 Markup.Escape 防止方括号被解析）
            foreach (var line in planText.Split('\n'))
            {
                var escaped = Markup.Escape(line);
                if (line.TrimStart().StartsWith("#"))
                {
                    _console.MarkupLine($"[bold cyan]{escaped}[/]");
                }
                else if (line.TrimStart().StartsWith("- ") || line.TrimStart().StartsWith("* "))
                {
                    _console.MarkupLine($"[green]  •[/] {escaped.TrimStart().TrimStart('-', '*', ' ')}");
                }
                else
                {
                    _console.WriteLine(escaped);
                }
            }

            _console.WriteLine();
            _console.MarkupLine("[dim]─────────────────────────────────────────[/]");

            // ════════════════════════════════════════════════════════════
            //  用户审批
            // ════════════════════════════════════════════════════════════
            var choice = _console.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold]Do you want to execute this plan?[/]")
                    .AddChoices(new[] { "Execute plan", "Revise plan", "Cancel" }));

            if (choice == "Cancel")
            {
                DisplayInfo("Plan cancelled.");
                return 0;
            }

            if (choice == "Revise plan")
            {
                _console.MarkupLine("[yellow]Enter revision instructions (or press Enter to cancel):[/]");
                var revision = _console.Prompt(
                    new TextPrompt<string>("[green]Revision:[/]")
                        .AllowEmpty()
                        .PromptStyle("green"));

                if (string.IsNullOrWhiteSpace(revision))
                {
                    DisplayInfo("Revision cancelled.");
                    return 0;
                }

                // 追加修改意见后重新规划
                task = $"{task}\n\nAdditional revision notes: {revision}";
                _console.MarkupLine($"\n[bold]Revised task:[/] {Markup.Escape(task)}");
                _console.MarkupLine("[dim]Re-planning...[/]");

                // 递归调用自身（简化处理，最多递归一次）
                var revisedResult = await planAgent.ExecuteAsync(ContentMessage.UserMessage(task));
                planText = ExtractPlanText(revisedResult);

                if (string.IsNullOrWhiteSpace(planText))
                {
                    DisplayWarning("Re-planning did not produce a usable plan.");
                    return 1;
                }

                _console.MarkupLine("\n[bold yellow]════════ Revised Plan ════════[/]");
                foreach (var line in planText.Split('\n'))
                {
                    _console.WriteLine(Markup.Escape(line));
                }

                var retryChoice = _console.Prompt(
                    new SelectionPrompt<string>()
                        .Title("[bold]Execute revised plan?[/]")
                        .AddChoices(new[] { "Execute", "Cancel" }));

                if (retryChoice == "Cancel")
                {
                    DisplayInfo("Plan cancelled.");
                    return 0;
                }
            }

            // ════════════════════════════════════════════════════════════
            //  Phase 2: Act — 按计划执行
            // ════════════════════════════════════════════════════════════
            _console.WriteLine();
            _console.MarkupLine("[bold green]Phase 2: Executing plan[/]");
            _console.MarkupLine("[dim]─────────────────────────────────────────[/]");

            // 完整工具集
            var actToolRegistry = new ToolRegistry();
            actToolRegistry.RegisterDiscoveredTool(new ReadFileTool(targetDir));
            actToolRegistry.RegisterDiscoveredTool(new WriteFileTool(targetDir));
            actToolRegistry.RegisterDiscoveredTool(new ShellTool(targetDir));
            actToolRegistry.RegisterDiscoveredTool(new GrepTool(targetDir));
            actToolRegistry.RegisterDiscoveredTool(new GlobTool(targetDir));
            actToolRegistry.RegisterDiscoveredTool(new LsTool(targetDir));
            actToolRegistry.RegisterDiscoveredTool(new EditTool(targetDir));
            actToolRegistry.RegisterDiscoveredTool(new WebFetchTool());
            actToolRegistry.RegisterDiscoveredTool(new WebSearchTool());
            actToolRegistry.RegisterDiscoveredTool(new MemoryTool(_config));

            var actAgent = new PlanExecuteAgent("plan-exec", actToolRegistry, agentGen, planText);

            // ── 实时渲染（Phase 2） ────────────────────────────────
            var actRenderer = new LiveTaskListRenderer();
            var actThinkingBuf = new System.Text.StringBuilder();
            var actThinkingStart = DateTime.UtcNow;
            bool actThinkingActive = false;

            actAgent.OnEvent += (_, e) =>
            {
                switch (e.Type)
                {
                    case AgentEventType.Thinking when !string.IsNullOrEmpty(e.Message):
                        actThinkingBuf.Append(e.Message);
                        var preview = TailText(actThinkingBuf.ToString(), 55);
                        if (!actThinkingActive)
                        {
                            actRenderer.Add($"◆ 思考中  {preview}");
                            actThinkingActive = true;
                        }
                        else
                        {
                            actRenderer.UpdateLast($"◆ 思考中  {preview}");
                        }
                        break;

                    case AgentEventType.ToolCalled when !string.IsNullOrEmpty(e.Message):
                        if (actThinkingActive)
                        {
                            var secs = (DateTime.UtcNow - actThinkingStart).TotalSeconds;
                            actRenderer.CompleteLastWith($"◆ 思考完成 ({secs:F1}s)");
                            actThinkingActive = false;
                            actThinkingBuf.Clear();
                            actThinkingStart = DateTime.UtcNow;
                        }
                        actRenderer.Add(e.Message);
                        break;

                    case AgentEventType.ToolCompleted:
                        actRenderer.CompleteLast();
                        break;
                }
            };

            var actResult = await actAgent.ExecuteAsync(ContentMessage.UserMessage(task));

            // 收尾
            if (actThinkingActive)
            {
                var secs = (DateTime.UtcNow - actThinkingStart).TotalSeconds;
                actRenderer.CompleteLastWith($"◆ 思考完成 ({secs:F1}s)");
            }
            actRenderer.PrintCompleted();

            // ── 渲染结果 ───────────────────────────────────────────
            RenderActResult(actResult);

            return actResult.State == AgentExecutionState.Failed ? 1 : 0;
        }
        catch (Exception ex)
        {
            DisplayError($"Error in plan mode: {ex.Message}");
            return 1;
        }
        finally
        {
            if (contentGenerator is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Handles the plan exit subcommand.
    /// </summary>
    private Task<int> HandleExitAsync(InvocationContext context)
    {
        DisplayInfo("Exiting plan mode.");
        return Task.FromResult(0);
    }

    // ─── Helper Methods ──────────────────────────────────────────────────

    /// <summary>
    /// Extracts the plan text from the agent result (last model text response).
    /// </summary>
    private static string ExtractPlanText(AgentResult result)
    {
        // 取最后一条模型文本响应作为计划
        var lastText = result.Messages
            .Where(m => m.Role == LlmRole.Model)
            .SelectMany(m => m.Parts.OfType<TextContentPart>())
            .Where(p => !string.IsNullOrWhiteSpace(p.Text))
            .Select(p => p.Text)
            .LastOrDefault();

        return lastText ?? string.Empty;
    }

    /// <summary>
    /// Renders the execution result summary.
    /// </summary>
    private void RenderActResult(AgentResult result)
    {
        _console.MarkupLine($"\n[dim]{"─".PadRight(60, '─')}[/]");

        if (result.State == AgentExecutionState.Failed)
        {
            DisplayError($"Execution failed: {result.Error?.Message ?? "Unknown error"}");
            return;
        }

        var toolCount = result.ToolCalls?.Count ?? 0;
        var duration = result.Duration.TotalSeconds;
        var summary = toolCount > 0
            ? $"[dim]Executed {toolCount} tool calls in {duration:F1}s[/]"
            : $"[dim]Completed in {duration:F1}s[/]";
        _console.MarkupLine(summary);
        _console.WriteLine();

        var lastText = result.Messages
            .Where(m => m.Role == LlmRole.Model)
            .SelectMany(m => m.Parts.OfType<TextContentPart>())
            .Where(p => !string.IsNullOrWhiteSpace(p.Text))
            .Select(p => p.Text)
            .LastOrDefault();

        if (!string.IsNullOrWhiteSpace(lastText))
        {
            _console.MarkupLine($"[cyan]AI:[/] {Markup.Escape(lastText)}");
        }
        else if (toolCount > 0)
        {
            _console.MarkupLine("[green]✓ Plan execution completed[/]");
        }
        else
        {
            _console.MarkupLine("[dim](No output)[/]");
        }
    }

    /// <summary>
    /// Truncates text to show the tail (for thinking preview).
    /// </summary>
    private static string TailText(string text, int maxLen)
    {
        var flat = text.Replace('\n', ' ').Replace('\r', ' ').TrimEnd();
        return flat.Length <= maxLen ? flat : "…" + flat[^maxLen..];
    }
}