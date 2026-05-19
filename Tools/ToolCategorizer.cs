using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Daggeragent.Tools;

/// <summary>
/// Two-stage tool routing. When <see cref="Configuration.ToolsOptions.GranularTools"/> is
/// <c>false</c> (the default), we don't hand the model all ~25 built-in tools at once —
/// instead this categorizer inspects the latest user message, picks the relevant
/// categories, and returns a focused subset. Small models with limited context windows
/// stop drowning in tool descriptions and stop ping-ponging between near-duplicate tools.
///
/// The matching is intentionally simple keyword + regex heuristics — no LLM call, no
/// embeddings, no ranking. When in doubt, the categorizer errs on the side of including a
/// category. <c>spawn_subagent</c> and the planning tools are always-on regardless.
/// </summary>
public static class ToolCategorizer
{
    public enum Category
    {
        Read,       // read-only filesystem (read_file, list_files, glob, grep, head_file, tail_file, file_info)
        Edit,       // mutating filesystem (write_file, edit_file, confirm_write, ...)
        Shell,      // exec_shell
        Web,        // http_get, http_get_bytes
        Memory,     // recall_past_work, remember
        System,     // pwd, which, list_processes
        Plan,       // make_plan, update_plan  (always-on when ForcePlan=true)
        Subagent,   // spawn_subagent  (always-on)
        Mcp,        // anything from McpToolProvider — treated as a single bucket
        Unknown,    // any tool we don't recognise — included by default so MCP servers
                    // that supply tools we haven't classified still work.
    }

    private static readonly Dictionary<string, Category> ToolCategories = new(StringComparer.Ordinal)
    {
        ["read_file"] = Category.Read,
        ["list_files"] = Category.Read,
        ["glob"] = Category.Read,
        ["grep"] = Category.Read,
        ["head_file"] = Category.Read,
        ["tail_file"] = Category.Read,
        ["file_info"] = Category.Read,

        ["write_file"] = Category.Edit,
        ["edit_file"] = Category.Edit,
        ["confirm_write"] = Category.Edit,
        ["discard_write"] = Category.Edit,
        ["list_pending_writes"] = Category.Edit,
        ["delete_file"] = Category.Edit,
        ["move_file"] = Category.Edit,
        ["copy_file"] = Category.Edit,
        ["create_directory"] = Category.Edit,

        ["exec_shell"] = Category.Shell,

        ["http_get"] = Category.Web,
        ["http_get_bytes"] = Category.Web,

        ["recall_past_work"] = Category.Memory,
        ["remember"] = Category.Memory,

        ["pwd"] = Category.System,
        ["which"] = Category.System,
        ["list_processes"] = Category.System,

        ["make_plan"] = Category.Plan,
        ["update_plan"] = Category.Plan,

        ["spawn_subagent"] = Category.Subagent,
    };

    public static Category CategoryFor(string toolName)
    {
        if (ToolCategories.TryGetValue(toolName, out var cat)) return cat;
        // MCP tools follow the namespacing convention "mcp.{server}.{tool}".
        if (toolName.StartsWith("mcp.", StringComparison.Ordinal)) return Category.Mcp;
        return Category.Unknown;
    }

    private static readonly Regex UrlPattern = new(@"https?://|www\.[a-z]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly (string Keyword, Category Cat)[] Keywords =
    {
        // Read / search
        ("read",     Category.Read),
        ("open",     Category.Read),
        ("view",     Category.Read),
        ("show",     Category.Read),
        ("look",     Category.Read),
        ("inspect",  Category.Read),
        ("find",     Category.Read),
        ("search",   Category.Read),
        ("grep",     Category.Read),
        ("list",     Category.Read),
        ("ls ",      Category.Read),
        ("cat ",     Category.Read),
        ("file",     Category.Read),
        ("director", Category.Read),
        ("folder",   Category.Read),
        ("contents", Category.Read),
        ("explore",  Category.Read),

        // Edit / write
        ("write",    Category.Edit),
        ("create",   Category.Edit),
        ("edit",     Category.Edit),
        ("modify",   Category.Edit),
        ("update",   Category.Edit),
        ("change",   Category.Edit),
        ("rename",   Category.Edit),
        ("delete",   Category.Edit),
        ("remove",   Category.Edit),
        ("move",     Category.Edit),
        ("copy",     Category.Edit),
        ("add",      Category.Edit),
        ("replace",  Category.Edit),
        ("refactor", Category.Edit),
        ("patch",    Category.Edit),
        ("fix",      Category.Edit),
        ("implement",Category.Edit),
        ("scaffold", Category.Edit),
        ("save ",    Category.Edit),

        // Shell
        ("run ",     Category.Shell),
        ("execute",  Category.Shell),
        ("invoke",   Category.Shell),
        ("shell",    Category.Shell),
        ("command",  Category.Shell),
        ("npm ",     Category.Shell),
        ("dotnet ",  Category.Shell),
        ("git ",     Category.Shell),
        ("build",    Category.Shell),
        ("compile",  Category.Shell),
        ("test",     Category.Shell),
        ("install",  Category.Shell),

        // Web
        ("http",     Category.Web),
        ("url",      Category.Web),
        ("download", Category.Web),
        ("fetch",    Category.Web),
        ("api ",     Category.Web),
        ("rest ",    Category.Web),
        ("endpoint", Category.Web),
        ("website",  Category.Web),
        ("webpage",  Category.Web),

        // Memory
        ("remember", Category.Memory),
        ("recall",   Category.Memory),
        ("memory",   Category.Memory),
        ("memorise", Category.Memory),
        ("memorize", Category.Memory),
        ("previous", Category.Memory),
        ("past",     Category.Memory),

        // System
        ("process",  Category.System),
        ("environment", Category.System),
        ("pid ",     Category.System),
        ("system info", Category.System),
        ("which ",   Category.System),
    };

    /// <summary>
    /// Pick the categories that look relevant to <paramref name="userMessage"/>. Always
    /// includes <see cref="Category.Subagent"/>; includes <see cref="Category.Plan"/>
    /// when <paramref name="includePlan"/> is true. Falls back to Read + Edit + Shell
    /// when no keyword hits — that's the typical "do something with the code" intent.
    /// </summary>
    public static HashSet<Category> Route(string userMessage, bool includePlan)
    {
        var enabled = new HashSet<Category> { Category.Subagent, Category.Mcp, Category.Unknown };
        if (includePlan) enabled.Add(Category.Plan);

        if (string.IsNullOrWhiteSpace(userMessage))
        {
            enabled.UnionWith(new[] { Category.Read, Category.Edit, Category.Shell });
            return enabled;
        }

        var lower = userMessage.ToLowerInvariant();

        if (UrlPattern.IsMatch(userMessage)) enabled.Add(Category.Web);

        foreach (var (kw, cat) in Keywords)
        {
            if (lower.Contains(kw, StringComparison.Ordinal)) enabled.Add(cat);
        }

        // Read implies the model probably wants to inspect — keep it cheap by also
        // bringing Edit/Shell only when their own keywords hit. But if nothing besides
        // Subagent/Plan came through, broaden to the default working set.
        var hasWorkCategory = enabled.Any(c =>
            c == Category.Read || c == Category.Edit || c == Category.Shell ||
            c == Category.Web || c == Category.Memory || c == Category.System);
        if (!hasWorkCategory)
        {
            enabled.Add(Category.Read);
            enabled.Add(Category.Edit);
            enabled.Add(Category.Shell);
        }

        return enabled;
    }

    /// <summary>
    /// Filter a tool list to only those whose category is in <paramref name="enabledCategories"/>.
    /// Used by <see cref="Agent.LlmAgent"/> when GranularTools=false.
    /// </summary>
    public static List<AITool> Filter(IEnumerable<AITool> tools, HashSet<Category> enabledCategories)
    {
        var result = new List<AITool>();
        foreach (var tool in tools)
        {
            var name = tool is AIFunction fn ? fn.Name : tool.GetType().Name;
            if (enabledCategories.Contains(CategoryFor(name))) result.Add(tool);
        }
        return result;
    }
}
