using Spectre.Console;
using System.CommandLine;
using AiCli.Cli.Commands;
using AiCli.Core.Configuration;
using AiCli.Core.Logging;

namespace AiCli.Cli;

/// <summary>
/// Main entry point for AiCli application.
/// </summary>
public class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Ensure UTF-8 console I/O on all platforms (prevents garbled Chinese on Windows).
        Console.InputEncoding = System.Text.Encoding.UTF8;
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // Display banner
        DisplayBanner();

        try
        {
            // Initialize logger
            LoggerHelper.SetGlobalLogger(LoggerHelper.CreateLogger(new LoggerConfig()));
            var logger = LoggerHelper.ForContext<Program>();

            // Load configuration
            await using var config = new Config();
            await config.InitializeAsync();
            logger.Information("AiCli started");

            // Build root command
            var rootCommand = new RootCommand("aicli")
            {
                Description = "AiCli - AI-powered command line assistant"
            };

            // Chat command
            var chatCommand = new ChatCommand(config);
            rootCommand.AddCommand(chatCommand.Command);

            // Prompt command
            var promptCommand = new PromptCommand(config);
            rootCommand.AddCommand(promptCommand.Command);

            // Agent command
            var agentCommand = new AgentCommand(config);
            rootCommand.AddCommand(agentCommand.Command);

            // Config command
            var configCommand = new ConfigCommand(config);
            rootCommand.AddCommand(configCommand.Command);

            // Plan command
            var planCommand = new PlanCommand(config);
            rootCommand.AddCommand(planCommand.Command);

            // Parse and invoke
            var invokeArgs = args;
            if (args.Length == 0)
            {
                invokeArgs = BuildInteractiveStartupArgs();
                if (invokeArgs.Length == 0)
                {
                    return 0;
                }
            }

            return await rootCommand.InvokeAsync(invokeArgs);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Fatal error:[/] {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Displays the application banner.
    /// </summary>
    private static void DisplayBanner()
    {
        Console.WriteLine();
        AnsiConsole.MarkupLine("  ____  _____ _     _       _____ ");
        AnsiConsole.MarkupLine(" |  _ \\|_   | |   __|__   |__  /\\ \\  ");
        AnsiConsole.MarkupLine(" | | | ||   | |  |____ |     \\_/ | |\\ |");
        AnsiConsole.MarkupLine(" | |_| ||___| |__|      /\\___/ |__| |__|");
        AnsiConsole.MarkupLine("                  ");
        AnsiConsole.MarkupLine("[bold yellow]Agent CLI[/] - C# Edition");
        AnsiConsole.MarkupLine("[dim]v0.1.0 - Porting in progress[/]");
        Console.WriteLine();
    }

    private static void ChooseWorkingDirectory()
    {
        while (true)
        {
            AnsiConsole.MarkupLine("[dim]正在打开文件夹选择窗口…[/]");

            var selected = ShowFolderBrowserDialog();
            if (selected == null)
            {
                // 用户取消，退出程序
                AnsiConsole.MarkupLine("[yellow]已取消目录选择，退出。[/]");
                Environment.Exit(0);
            }

            Console.WriteLine();
            AnsiConsole.Write(new Rule("[bold yellow]工作目录确认[/]").RuleStyle("dim"));
            AnsiConsole.MarkupLine($"  [cyan]{Markup.Escape(selected)}[/]");
            AnsiConsole.Write(new Rule().RuleStyle("dim"));
            Console.WriteLine();

            if (AnsiConsole.Confirm("使用此目录？"))
            {
                Directory.SetCurrentDirectory(selected);
                AnsiConsole.MarkupLine($"[green]✓[/] 工作目录: [cyan]{Markup.Escape(selected)}[/]");
                Console.WriteLine();
                return;
            }

            Console.WriteLine();
        }
    }

    /// <summary>
    /// 在 STA 线程中弹出 Windows 原生文件夹选择对话框。
    /// 返回选中路径，用户取消则返回 null。
    /// </summary>
    private static string? ShowFolderBrowserDialog()
    {
        string? result = null;
        var thread = new Thread(() =>
        {
            using var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "请选择工作目录（建议选择非 C 盘目录）",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true,
                RootFolder = Environment.SpecialFolder.MyComputer
            };
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                result = dialog.SelectedPath;
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    private static string[] BuildInteractiveStartupArgs()
    {
        if (!AnsiConsole.Profile.Capabilities.Interactive)
        {
            AnsiConsole.MarkupLine("[yellow]终端不支持交互式提示，使用默认非交互模式：聊天（按配置路由）[/]");
            return new[] { "chat" };
        }

        ChooseWorkingDirectory();

        var mode = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]请选择启动模式[/]")
                .AddChoices(new[]
                {
                    "聊天模式（本地生成器）",
                    "聊天模式（Google API）",
                    "Agent 模式（general）",
                    "单次 Prompt",
                    "退出"
                }));

        return mode switch
        {
            "聊天模式（本地生成器）" => BuildLocalChatArgs(),
            "聊天模式（Google API）" => new[] { "chat" },
            "Agent 模式（general）" => BuildAgentArgs(),
            "单次 Prompt" => BuildPromptArgs(),
            //_ => Array.Empty<string>()
            _ => BuildAgentArgs()
        };
    }

    private static string[] BuildLocalChatArgs()
    {
        Environment.SetEnvironmentVariable("AICLI_USE_LOCAL_GENERATOR", "true");
        return new[] { "chat" };
    }

    private static string[] BuildAgentArgs()
    {
        return new[] { "agent", "interactive", "general" };
    }

    private static string[] BuildPromptArgs()
    {
        var input = AnsiConsole.Ask<string>("[green]请输入 prompt[/]");
        return new[] { "prompt", input };
    }
}
