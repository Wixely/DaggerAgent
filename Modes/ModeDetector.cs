namespace Daggeragent.Modes;

public static class ModeDetector
{
    private static readonly HashSet<string> ServeAliases = new(StringComparer.OrdinalIgnoreCase) { "serve", "service", "--serve", "--service" };
    private static readonly HashSet<string> RunAliases = new(StringComparer.OrdinalIgnoreCase) { "run", "exec", "--run" };

    public static AppMode Detect(string[] args, bool isWindowsService)
    {
        if (isWindowsService) return AppMode.WindowsService;

        if (args.Length > 0)
        {
            if (ServeAliases.Contains(args[0])) return AppMode.Service;
            if (RunAliases.Contains(args[0])) return AppMode.Cli;
            // Bare argument list with content => one-shot CLI prompt.
            if (!args[0].StartsWith("-")) return AppMode.Cli;
            if (args.Any(a => a.Equals("--prompt", StringComparison.OrdinalIgnoreCase))) return AppMode.Cli;
            if (args.Any(a => a.Equals("--resume", StringComparison.OrdinalIgnoreCase))) return AppMode.Cli;
        }

        if (Console.IsInputRedirected) return AppMode.Cli;

        return AppMode.Interactive;
    }
}
