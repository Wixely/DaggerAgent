using Daggeragent.Agent;
using Daggeragent.Configuration;
using Daggeragent.Llm;
using Daggeragent.Mcp;
using Daggeragent.Modes;
using Daggeragent.Persistence;
using Daggeragent.Server;
using Daggeragent.Tools;
using Daggeragent.Triggers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Serilog;

namespace Daggeragent;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Ensure Unicode (†, dim ANSI, etc.) renders correctly on Windows consoles.
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { /* not a TTY */ }

        // When running as a Windows Service the working directory is C:\Windows\System32,
        // so resolve config and logs relative to the exe. Pin cwd up-front so any relative
        // path in config (Serilog file sink, jobs.db, etc.) resolves under the exe directory.
        // Capture the user's launch cwd first — filesystem/shell tools default to that
        // rather than the exe directory.
        //
        // IMPORTANT for single-file publish: AppContext.BaseDirectory points to the
        // self-extracted bundle temp dir (e.g. %LOCALAPPDATA%\.net\dagger\<hash>\), NOT
        // to where dagger.exe physically lives — so an appsettings.json sitting next to
        // the exe would never be read. Resolve from Environment.ProcessPath instead when
        // it looks like a real exe (i.e. not `dotnet` hosting a dll).
        string contentRoot;
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(processPath) &&
            !Path.GetFileName(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
            !Path.GetFileName(processPath).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            contentRoot = Path.GetDirectoryName(processPath)!;
        }
        else
        {
            contentRoot = AppContext.BaseDirectory;
        }
        var launchInfo = new Configuration.HostLaunchInfo
        {
            OriginalWorkingDirectory = Environment.CurrentDirectory,
            ContentRoot = contentRoot,
        };
        Directory.SetCurrentDirectory(contentRoot);
        var isWindowsService = WindowsServiceHelpers.IsWindowsService();
        var mode = ModeDetector.Detect(args, isWindowsService);

        // Console sink routing per mode:
        //   CLI         — route everything to stderr so `dagger.exe "..." > out.txt` only captures the reply.
        //   Interactive — fully silenced (restricted to Fatal, and even those go to stderr) so log
        //                 lines never interleave with the streaming chat output. The file sink keeps everything.
        //   Service / WindowsService — normal: Information+ on stdout for terminal/container log capture.
        var cliMode = mode == AppMode.Cli;
        var interactiveMode = mode == AppMode.Interactive;
        var consoleRestrictedLevel = interactiveMode
            ? Serilog.Events.LogEventLevel.Fatal
            : Serilog.Events.LogEventLevel.Verbose;
        var consoleStderrFromLevel = (interactiveMode || cliMode)
            ? Serilog.Events.LogEventLevel.Verbose
            : Serilog.Events.LogEventLevel.Error;

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console(restrictedToMinimumLevel: consoleRestrictedLevel, standardErrorFromLevel: consoleStderrFromLevel)
            .WriteTo.File(
                Path.Combine(contentRoot, "logs", "dagger-bootstrap-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                shared: true)
            .CreateBootstrapLogger();

        try
        {
            Log.Information("DaggerAgent starting (mode={Mode}, contentRoot={ContentRoot})", mode, contentRoot);

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                Args = args,
                ContentRootPath = contentRoot,
            });

            builder.Configuration
                .SetBasePath(contentRoot)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true)
                .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables()
                .AddEnvironmentVariables(prefix: "DAGGER_")
                .AddCommandLine(args);

            if (isWindowsService)
            {
                builder.Host.UseWindowsService(o => o.ServiceName = "DaggerAgent");
            }

            // Same Console-sink routing applied to the appsettings-loaded host logger.
            if (cliMode || interactiveMode)
            {
                foreach (var sink in builder.Configuration.GetSection("Serilog:WriteTo").GetChildren())
                {
                    if (sink.GetValue<string>("Name") == "Console")
                    {
                        sink["Args:standardErrorFromLevel"] = "Verbose";
                        if (interactiveMode)
                        {
                            sink["Args:restrictedToMinimumLevel"] = "Fatal";
                        }
                    }
                }
            }

            builder.Host.UseSerilog((ctx, services, cfg) => cfg
                .ReadFrom.Configuration(ctx.Configuration)
                .ReadFrom.Services(services)
                .Enrich.FromLogContext());

            builder.Services.AddSingleton(launchInfo);
            RegisterServices(builder);

            // Kestrel config — only matters for Service / WindowsService modes.
            var server = builder.Configuration.GetSection(ServerOptions.SectionName).Get<ServerOptions>() ?? new ServerOptions();
            builder.WebHost.ConfigureKestrel(k => k.ListenAnyIP(server.Port));

            var app = builder.Build();

            if (mode is AppMode.Service or AppMode.WindowsService)
            {
                app.UseServiceTrafficLogging();
                app.UseSerilogRequestLogging();
                app.UseApiKeyAuth();
                app.MapLanding();
                app.MapJobsApi(server.Path);
                app.MapOpenAiCompatApi();
                app.MapOllamaCompatApi();

                var store = app.Services.GetRequiredService<IJobStore>();
                await store.InitializeAsync().ConfigureAwait(false);
                await app.Services.GetRequiredService<MemoryStore>().InitializeAsync().ConfigureAwait(false);

                Log.Information("DaggerAgent service listening on http://{Host}:{Port}{Path}", server.Host, server.Port, server.Path);
                await app.RunAsync().ConfigureAwait(false);
                return 0;
            }

            // Interactive / CLI: start MCP host so tools are available, but skip app.RunAsync().
            var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
            var mcpHost = app.Services.GetRequiredService<McpClientHost>();

            // Lifetime tracing: in Interactive mode the console sink is silenced, so a
            // shutdown-signal that aborts the chat loop would otherwise be invisible.
            // Capture every transition to the file sink so we can diagnose "it just stopped".
            lifetime.ApplicationStarted.Register(() => Log.Information("Lifetime: ApplicationStarted"));
            lifetime.ApplicationStopping.Register(() => Log.Information("Lifetime: ApplicationStopping signalled (something requested shutdown)"));
            lifetime.ApplicationStopped.Register(() => Log.Information("Lifetime: ApplicationStopped"));
            Console.CancelKeyPress += (_, e) =>
            {
                Log.Information("Console.CancelKeyPress: Ctrl+{Key} pressed — requesting graceful shutdown", e.SpecialKey);
                e.Cancel = true; // suppress immediate process kill — let the cancellation token unwind cleanly
                lifetime.StopApplication();
            };
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                Log.Fatal(e.ExceptionObject as Exception, "AppDomain.UnhandledException (isTerminating={Terminating})", e.IsTerminating);
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Log.Error(e.Exception, "TaskScheduler.UnobservedTaskException");
                e.SetObserved();
            };

            await mcpHost.StartAsync(lifetime.ApplicationStopping).ConfigureAwait(false);

            try
            {
                var exitCode = mode switch
                {
                    AppMode.Interactive => await app.Services.GetRequiredService<InteractiveRunner>().RunAsync(lifetime.ApplicationStopping).ConfigureAwait(false),
                    AppMode.Cli => await app.Services.GetRequiredService<CliRunner>().RunAsync(args, lifetime.ApplicationStopping).ConfigureAwait(false),
                    _ => 1,
                };
                Log.Information("Runner returned (mode={Mode}, exitCode={ExitCode})", mode, exitCode);
                return exitCode;
            }
            finally
            {
                Log.Information("Shutting down: stopping MCP host");
                await mcpHost.StopAsync(CancellationToken.None).ConfigureAwait(false);
                await mcpHost.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "DaggerAgent terminated unexpectedly");
            return 1;
        }
        finally
        {
            await Log.CloseAndFlushAsync().ConfigureAwait(false);
        }
    }

    private static void RegisterServices(WebApplicationBuilder builder)
    {
        builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.SectionName));
        builder.Services.Configure<OpenAIOptions>(builder.Configuration.GetSection(OpenAIOptions.SectionName));
        builder.Services.Configure<McpOptions>(builder.Configuration.GetSection(McpOptions.SectionName));
        builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection(ServerOptions.SectionName));
        builder.Services.Configure<JobsOptions>(builder.Configuration.GetSection(JobsOptions.SectionName));
        builder.Services.Configure<ToolsOptions>(builder.Configuration.GetSection(ToolsOptions.SectionName));
        builder.Services.Configure<WebOptions>(builder.Configuration.GetSection(WebOptions.SectionName));
        builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));
        builder.Services.Configure<PricingOptions>(builder.Configuration.GetSection(PricingOptions.SectionName));
        builder.Services.Configure<MemoryOptions>(builder.Configuration.GetSection(MemoryOptions.SectionName));
        builder.Services.Configure<TriggerOptions>(builder.Configuration.GetSection(TriggerOptions.SectionName));

        builder.Services.AddSingleton<ChatClientFactory>();
        builder.Services.AddSingleton<EmbeddingClientFactory>();
        builder.Services.AddSingleton<MemoryStore>();
        builder.Services.AddSingleton<TokenEstimator>();
        builder.Services.AddSingleton<PersonalityProvider>();
        builder.Services.AddSingleton<ContextCompressor>();
        builder.Services.AddSingleton<IJobStore, SqliteJobStore>();
        builder.Services.AddSingleton<McpClientHost>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<McpClientHost>());
        builder.Services.AddSingleton<McpToolProvider>();

        builder.Services.AddSingleton<SubAgentManager>();
        builder.Services.AddSingleton<SpawnSubagentTool>();
        builder.Services.AddSingleton<PendingWriteStore>();
        builder.Services.AddSingleton<FilesystemTools>();
        builder.Services.AddSingleton<ShellToolset>();
        builder.Services.AddSingleton<MemoryTools>();
        builder.Services.AddSingleton<SystemTools>();
        builder.Services.AddSingleton<WebTools>();
        builder.Services.AddSingleton<PlanStore>();
        builder.Services.AddSingleton<PlanningTools>();
        builder.Services.AddSingleton<BuiltInToolRegistry>();

        builder.Services.AddTransient<LlmAgent>();

        builder.Services.AddSingleton<InteractiveRunner>();
        builder.Services.AddSingleton<CliRunner>();

        builder.Services.AddSingleton<TriggerStateStore>();
        builder.Services.AddHostedService<TriggerService>();
    }
}
