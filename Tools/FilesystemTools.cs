using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using Daggeragent.Configuration;
using Daggeragent.Llm;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Daggeragent.Tools;

public sealed class FilesystemTools
{
    private readonly ToolsOptions _options;
    private readonly AgentOptions _agentOptions;
    private readonly HostLaunchInfo _launchInfo;
    private readonly PendingWriteStore _pending;
    private readonly ChatClientFactory _chatClientFactory;
    private readonly ILogger<FilesystemTools> _log;

    public FilesystemTools(
        IOptions<ToolsOptions> options,
        IOptions<AgentOptions> agentOptions,
        HostLaunchInfo launchInfo,
        PendingWriteStore pending,
        ChatClientFactory chatClientFactory,
        ILogger<FilesystemTools> log)
    {
        _options = options.Value;
        _agentOptions = agentOptions.Value;
        _launchInfo = launchInfo;
        _pending = pending;
        _chatClientFactory = chatClientFactory;
        _log = log;
    }

    public IEnumerable<AITool> Build()
    {
        yield return AIFunctionFactory.Create(ReadFile, name: "read_file", description:
            "Read a text file from disk (auto-detects UTF-8/16/32, with or without BOM). Path is resolved " +
            "inside the configured working directory. Files larger than the configured summary threshold " +
            "return an LLM-generated summary instead of raw contents — use head_file / tail_file / grep " +
            "to fetch specific sections of large files. Returns content/summary or a clear error string.");

        yield return AIFunctionFactory.Create(ListFiles, name: "list_files", description:
            "List directory entries (files and subdirectories) under a path inside the working directory.");

        yield return AIFunctionFactory.Create(Glob, name: "glob", description:
            "Find files matching a glob pattern (e.g. \"**/*.cs\", \"src/*.json\") under the working directory.");

        yield return AIFunctionFactory.Create(Grep, name: "grep", description:
            "Search files under the working directory for a regular expression. Returns matching lines with line numbers.");

        yield return AIFunctionFactory.Create(HeadFile, name: "head_file", description:
            "Return the first N lines of a UTF-8 text file (default 50).");

        yield return AIFunctionFactory.Create(TailFile, name: "tail_file", description:
            "Return the last N lines of a UTF-8 text file (default 50).");

        yield return AIFunctionFactory.Create(FileInfo, name: "file_info", description:
            "Return metadata about a file or directory: size, modified time, type, exists.");

        if (_options.AllowWrite && !_options.ReadOnly)
        {
            var stagingMode = _options.WritePreview;
            var writeDescription = stagingMode
                ? "Stage a write to a UTF-8 text file. Returns a unified diff of the proposed change; the file is NOT modified until you call confirm_write on the same path."
                : "Overwrite (or create) a UTF-8 text file inside the working directory.";
            var editDescription = stagingMode
                ? "Stage a substring replacement in an existing file. old_string must appear exactly once. Returns a unified diff; the file is NOT modified until you call confirm_write on the same path."
                : "Replace an exact substring in an existing file. The old_string must appear exactly once.";

            yield return AIFunctionFactory.Create(WriteFile, name: "write_file", description: writeDescription);
            yield return AIFunctionFactory.Create(EditFile, name: "edit_file", description: editDescription);

            if (stagingMode)
            {
                yield return AIFunctionFactory.Create(ConfirmWrite, name: "confirm_write", description:
                    "Apply a previously staged write_file or edit_file change to disk. Pass the same path that was staged.");
                yield return AIFunctionFactory.Create(DiscardWrite, name: "discard_write", description:
                    "Drop a previously staged write_file or edit_file change without applying it.");
                yield return AIFunctionFactory.Create(ListPendingWrites, name: "list_pending_writes", description:
                    "List paths currently staged for write/edit, with byte counts.");
            }

            yield return AIFunctionFactory.Create(DeleteFile, name: "delete_file", description:
                "Delete a file inside the working directory. Refuses directories — use exec_shell for those if AllowShell.");
            yield return AIFunctionFactory.Create(MoveFile, name: "move_file", description:
                "Move or rename a file (or directory) inside the working directory.");
            yield return AIFunctionFactory.Create(CopyFile, name: "copy_file", description:
                "Copy a file inside the working directory.");
            yield return AIFunctionFactory.Create(CreateDirectory, name: "create_directory", description:
                "Create a directory (and any missing parents) inside the working directory.");
        }
    }

    [Description("Read a text file (auto-detects UTF-8, UTF-16, UTF-32 — with or without BOM).")]
    private async Task<string> ReadFile(
        [Description("Relative or absolute path inside the working directory.")] string path,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var resolved = ResolveScoped(path);
            if (!File.Exists(resolved)) return $"Error: file not found: {path}";

            var info = new FileInfo(resolved);
            if (info.Length > _options.MaxFileBytes)
            {
                return $"Error: file is {info.Length} bytes which exceeds MaxFileBytes={_options.MaxFileBytes}. " +
                       "Use grep or a smaller slice.";
            }

            var bytes = await File.ReadAllBytesAsync(resolved, cancellationToken).ConfigureAwait(false);
            var (encoding, skip) = DetectEncoding(bytes);
            var content = encoding.GetString(bytes, skip, bytes.Length - skip);

            var threshold = _options.ReadFileSummaryThresholdBytes;
            if (threshold > 0 && info.Length > threshold)
            {
                return await SummariseFile(path, content, info.Length, cancellationToken).ConfigureAwait(false);
            }

            return content;
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    /// <summary>
    /// Files above <see cref="ToolsOptions.ReadFileSummaryThresholdBytes"/> get a summary
    /// instead of their raw contents — stops a single read_file from blowing the context
    /// window. The summariser is the configured <see cref="AgentOptions.SummariserModel"/>
    /// (typically cheaper than the main model). If the summariser call fails we fall back
    /// to a head+tail slice so the model still has something to work with.
    /// </summary>
    private async Task<string> SummariseFile(string path, string content, long size, CancellationToken ct)
    {
        try
        {
            var model = string.IsNullOrWhiteSpace(_agentOptions.SummariserModel) ? null : _agentOptions.SummariserModel;
            var client = _chatClientFactory.Create(model);
            var prompt = $"Summarise the following file for an LLM agent. Focus on: file's purpose, " +
                         $"top-level structure, public APIs / class names / function signatures, key constants, " +
                         $"and any notable sections. Keep under ~800 words. End with one short sentence advising " +
                         $"the agent to call head_file / tail_file / grep for specific content it needs.\n\n" +
                         $"Path: {path}\nSize: {size} bytes\n\n--- BEGIN FILE ---\n{content}\n--- END FILE ---";
            var response = await client.GetResponseAsync(
                new[] { new ChatMessage(ChatRole.User, prompt) },
                cancellationToken: ct).ConfigureAwait(false);
            var summary = response.Text ?? "(summariser returned no text)";
            return $"[NOTE: file is {size} bytes, exceeding the read_file summary threshold of " +
                   $"{_options.ReadFileSummaryThresholdBytes} bytes. Below is a model-generated summary, " +
                   $"NOT the raw file. Use head_file / tail_file / grep for specific content.]\n\n{summary}";
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Summariser call failed for {Path}; falling back to head+tail slice", path);
            const int headChars = 8000;
            const int tailChars = 2000;
            if (content.Length <= headChars + tailChars) return content;
            var head = content.Substring(0, headChars);
            var tail = content.Substring(content.Length - tailChars);
            return $"[NOTE: file is {size} bytes; summariser unavailable ({ex.GetType().Name}). " +
                   $"Showing first {headChars} and last {tailChars} chars. Use head_file / tail_file / grep " +
                   $"for specific content.]\n\n{head}\n\n... [{content.Length - headChars - tailChars} chars elided] ...\n\n{tail}";
        }
    }

    /// <summary>
    /// Pick a text encoding for raw bytes. Checks BOMs first (UTF-32 LE/BE, UTF-16 LE/BE,
    /// UTF-8); failing that, decodes a sample with each candidate encoding and picks the
    /// one whose output has the highest share of printable code points. Decode-and-score
    /// is more robust than byte-pattern heuristics — files with lots of binary-ish
    /// content in their first KB (CSS, scripts, base64) trip the simpler NUL-density
    /// check, but trying the actual decoder always tells you which one produced
    /// readable text.
    /// </summary>
    private static (Encoding Encoding, int BomBytes) DetectEncoding(byte[] bytes)
    {
        // BOM checks (longest first so UTF-32 LE doesn't get mistaken for UTF-16 LE).
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0 && bytes[3] == 0)
            return (new UTF32Encoding(bigEndian: false, byteOrderMark: false), 4);
        if (bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xFE && bytes[3] == 0xFF)
            return (new UTF32Encoding(bigEndian: true, byteOrderMark: false), 4);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (Encoding.Unicode, 2);              // UTF-16 LE
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return (Encoding.BigEndianUnicode, 2);     // UTF-16 BE

        if (bytes.Length < 4)
            return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 0);

        // Score each candidate by decoding up to 4 KB and counting printable chars.
        // Whoever scores highest wins; on a tie UTF-8 takes it (default for new files).
        var sample = Math.Min(4096, bytes.Length);
        var evenSample = sample & ~1; // UTF-16 needs even-length input

        var utf8Score = ScoreDecoded(new UTF8Encoding(false, throwOnInvalidBytes: false).GetString(bytes, 0, sample));
        var utf16LeScore = evenSample >= 4
            ? ScoreDecoded(Encoding.Unicode.GetString(bytes, 0, evenSample))
            : -1;
        var utf16BeScore = evenSample >= 4
            ? ScoreDecoded(Encoding.BigEndianUnicode.GetString(bytes, 0, evenSample))
            : -1;

        var bestEncoding = (Encoding)new UTF8Encoding(false);
        var bestScore = utf8Score;
        if (utf16LeScore > bestScore + 0.05) { bestScore = utf16LeScore; bestEncoding = Encoding.Unicode; }
        if (utf16BeScore > bestScore + 0.05) { bestScore = utf16BeScore; bestEncoding = Encoding.BigEndianUnicode; }
        // 0.05 margin biases toward UTF-8 — for clearly-ASCII content UTF-8 and UTF-16 LE
        // can score near-identically and we want UTF-8 to win the tie.

        return (bestEncoding, 0);
    }

    /// <summary>
    /// Ratio of "good" code points (printable ASCII, common whitespace, letters from any
    /// script) to total scored code points in <paramref name="decoded"/>. NUL and the
    /// U+FFFD replacement character count strongly against a candidate — both indicate
    /// the decoder produced garbage. Range: 0 (all garbage) to 1 (all printable).
    /// </summary>
    private static double ScoreDecoded(string decoded)
    {
        if (decoded.Length == 0) return 0;
        int good = 0, bad = 0;
        foreach (var c in decoded)
        {
            switch (c)
            {
                case '\0': bad += 4; continue;       // NUL — strong negative signal
                case '�': bad += 2; continue;   // decoder-substituted "invalid"
                case '\t': case '\n': case '\r': good++; continue;
            }
            if (c < 0x20) { bad++; continue; }       // other C0 control chars
            if (c < 0x7F) { good++; continue; }      // printable ASCII
            if (c < 0xA0) { bad++; continue; }       // C1 control range — usually decoder garbage
            if (char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) ||
                char.IsPunctuation(c) || char.IsSymbol(c)) good++;
            else bad++;
        }
        var total = good + bad;
        return total == 0 ? 0 : (double)good / total;
    }

    [Description("List entries in a directory.")]
    private string ListFiles(
        [Description("Relative or absolute directory path inside the working directory. Use \".\" for the root.")] string path)
    {
        try
        {
            var resolved = ResolveScoped(path);
            if (!Directory.Exists(resolved)) return $"Error: directory not found: {path}";

            var entries = Directory.EnumerateFileSystemEntries(resolved)
                .Take(_options.MaxResults)
                .Select(p => Directory.Exists(p) ? Path.GetFileName(p) + "/" : Path.GetFileName(p));
            return string.Join("\n", entries);
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    [Description("Glob for files.")]
    private string Glob(
        [Description("Glob pattern, e.g. \"**/*.cs\".")] string pattern)
    {
        try
        {
            var root = Root();
            var matcher = new Microsoft.Extensions.FileSystemGlobbing.Matcher();
            matcher.AddInclude(pattern);
            var result = matcher.Execute(new Microsoft.Extensions.FileSystemGlobbing.Abstractions.DirectoryInfoWrapper(new DirectoryInfo(root)));
            if (!result.HasMatches) return "(no matches)";
            return string.Join("\n", result.Files.Take(_options.MaxResults).Select(f => f.Path));
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    [Description("Search file contents with regex.")]
    private string Grep(
        [Description("Regular expression to search for.")] string pattern,
        [Description("Optional glob restricting which files to search. Defaults to **/*.")] string? include = null,
        [Description("Case-insensitive match. Defaults to false.")] bool ignoreCase = false)
    {
        try
        {
            var root = Root();
            var matcher = new Microsoft.Extensions.FileSystemGlobbing.Matcher();
            matcher.AddInclude(string.IsNullOrEmpty(include) ? "**/*" : include);
            matcher.AddExclude("**/bin/**").AddExclude("**/obj/**").AddExclude("**/node_modules/**").AddExclude("**/.git/**");
            var matchResult = matcher.Execute(new Microsoft.Extensions.FileSystemGlobbing.Abstractions.DirectoryInfoWrapper(new DirectoryInfo(root)));
            if (!matchResult.HasMatches) return "(no files matched include)";

            var regex = new Regex(pattern, ignoreCase ? RegexOptions.IgnoreCase | RegexOptions.Compiled : RegexOptions.Compiled);
            var hits = new List<string>();
            foreach (var file in matchResult.Files)
            {
                var fullPath = Path.Combine(root, file.Path);
                if (!File.Exists(fullPath)) continue;
                FileInfo info = new(fullPath);
                if (info.Length > _options.MaxFileBytes) continue;

                int lineNo = 0;
                foreach (var line in File.ReadLines(fullPath))
                {
                    lineNo++;
                    if (regex.IsMatch(line))
                    {
                        hits.Add($"{file.Path}:{lineNo}:{line}");
                        if (hits.Count >= _options.MaxResults) return string.Join("\n", hits) + "\n(result limit reached)";
                    }
                }
            }
            return hits.Count == 0 ? "(no matches)" : string.Join("\n", hits);
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    [Description("Write or overwrite a file.")]
    private async Task<string> WriteFile(
        [Description("Relative or absolute path inside the working directory.")] string path,
        [Description("Full UTF-8 content to write.")] string content,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var resolved = ResolveScoped(path);
            var oldContent = File.Exists(resolved)
                ? await File.ReadAllTextAsync(resolved, cancellationToken).ConfigureAwait(false)
                : "";

            if (_options.WritePreview)
            {
                _pending.Stage(new PendingChange
                {
                    AbsolutePath = resolved,
                    DisplayPath = path,
                    OldContent = oldContent,
                    NewContent = content,
                });
                var diff = PendingWriteStore.RenderUnifiedDiff(path, oldContent, content);
                return $"[staged — call confirm_write(\"{path}\") to apply]\n{diff}";
            }

            var dir = Path.GetDirectoryName(resolved);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(resolved, content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            return $"Wrote {content.Length} chars to {path}.";
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    [Description("Replace a unique substring in a file.")]
    private async Task<string> EditFile(
        [Description("Relative or absolute path inside the working directory.")] string path,
        [Description("Exact existing substring to replace. Must appear exactly once in the file.")] string old_string,
        [Description("New substring.")] string new_string,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var resolved = ResolveScoped(path);
            if (!File.Exists(resolved)) return $"Error: file not found: {path}";
            var content = await File.ReadAllTextAsync(resolved, cancellationToken).ConfigureAwait(false);
            var idx = content.IndexOf(old_string, StringComparison.Ordinal);
            if (idx < 0) return "Error: old_string not found in file.";
            var lastIdx = content.LastIndexOf(old_string, StringComparison.Ordinal);
            if (lastIdx != idx) return "Error: old_string is not unique in the file; widen the context or use multiple edits.";
            var updated = content.Substring(0, idx) + new_string + content.Substring(idx + old_string.Length);

            if (_options.WritePreview)
            {
                _pending.Stage(new PendingChange
                {
                    AbsolutePath = resolved,
                    DisplayPath = path,
                    OldContent = content,
                    NewContent = updated,
                });
                var diff = PendingWriteStore.RenderUnifiedDiff(path, content, updated);
                return $"[staged — call confirm_write(\"{path}\") to apply]\n{diff}";
            }

            await File.WriteAllTextAsync(resolved, updated, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            return $"Replaced 1 occurrence in {path}.";
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    [Description("Apply a previously staged change.")]
    private async Task<string> ConfirmWrite(
        [Description("The same path that was passed to write_file or edit_file.")] string path,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var resolved = ResolveScoped(path);
            return await _pending.ConfirmAsync(resolved, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    [Description("Drop a previously staged change.")]
    private string DiscardWrite(
        [Description("The same path that was passed to write_file or edit_file.")] string path)
    {
        try
        {
            var resolved = ResolveScoped(path);
            if (!_pending.Remove(resolved)) return $"No staged change for {path}.";
            return $"Discarded staged change for {path}.";
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    [Description("List staged writes.")]
    private string ListPendingWrites()
    {
        if (_pending.Count == 0) return "(no staged writes)";
        var sb = new StringBuilder();
        foreach (var c in _pending.All().OrderBy(c => c.DisplayPath))
        {
            sb.Append(c.DisplayPath)
              .Append("  (")
              .Append(c.OldContent.Length).Append(" -> ").Append(c.NewContent.Length).Append(" chars, staged ")
              .Append(c.StagedAt.ToString("u")).Append(')').AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    [Description("Read first N lines of a file.")]
    private async Task<string> HeadFile(
        [Description("Relative or absolute path inside the working directory.")] string path,
        [Description("Number of lines to return. Default 50.")] int lines = 50,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var resolved = ResolveScoped(path);
            if (!File.Exists(resolved)) return $"Error: file not found: {path}";
            var sb = new StringBuilder();
            var n = 0;
            using var reader = new StreamReader(resolved);
            while (n < lines)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null) break;
                sb.AppendLine(line);
                n++;
            }
            return sb.ToString();
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    [Description("Read last N lines of a file.")]
    private async Task<string> TailFile(
        [Description("Relative or absolute path inside the working directory.")] string path,
        [Description("Number of lines to return. Default 50.")] int lines = 50,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var resolved = ResolveScoped(path);
            if (!File.Exists(resolved)) return $"Error: file not found: {path}";
            var ring = new Queue<string>(lines);
            using var reader = new StreamReader(resolved);
            while (true)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null) break;
                if (ring.Count == lines) ring.Dequeue();
                ring.Enqueue(line);
            }
            return string.Join('\n', ring);
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    [Description("Get file or directory metadata.")]
    private string FileInfo(
        [Description("Relative or absolute path inside the working directory.")] string path)
    {
        try
        {
            var resolved = ResolveScoped(path);
            if (File.Exists(resolved))
            {
                var fi = new System.IO.FileInfo(resolved);
                return $"type: file\npath: {path}\nsize_bytes: {fi.Length}\nmodified_utc: {fi.LastWriteTimeUtc:O}\nreadonly: {fi.IsReadOnly}";
            }
            if (Directory.Exists(resolved))
            {
                var di = new DirectoryInfo(resolved);
                return $"type: directory\npath: {path}\nmodified_utc: {di.LastWriteTimeUtc:O}\nentries: {di.EnumerateFileSystemInfos().Count()}";
            }
            return $"type: missing\npath: {path}";
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    [Description("Delete a file.")]
    private string DeleteFile(
        [Description("Relative or absolute path inside the working directory.")] string path)
    {
        try
        {
            var resolved = ResolveScoped(path);
            if (Directory.Exists(resolved)) return $"Error: {path} is a directory. Refusing to recursively delete; use exec_shell if you really mean it.";
            if (!File.Exists(resolved)) return $"Error: file not found: {path}";
            File.Delete(resolved);
            return $"Deleted {path}.";
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    [Description("Move or rename a file/directory.")]
    private string MoveFile(
        [Description("Source path inside the working directory.")] string from,
        [Description("Destination path inside the working directory.")] string to)
    {
        try
        {
            var src = ResolveScoped(from);
            var dst = ResolveScoped(to);
            if (File.Exists(src))
            {
                var dstDir = Path.GetDirectoryName(dst);
                if (!string.IsNullOrEmpty(dstDir)) Directory.CreateDirectory(dstDir);
                File.Move(src, dst, overwrite: false);
                return $"Moved file {from} -> {to}.";
            }
            if (Directory.Exists(src))
            {
                Directory.Move(src, dst);
                return $"Moved directory {from} -> {to}.";
            }
            return $"Error: source not found: {from}";
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    [Description("Copy a file.")]
    private string CopyFile(
        [Description("Source path inside the working directory.")] string from,
        [Description("Destination path inside the working directory.")] string to,
        [Description("Overwrite the destination if it exists. Default false.")] bool overwrite = false)
    {
        try
        {
            var src = ResolveScoped(from);
            var dst = ResolveScoped(to);
            if (!File.Exists(src)) return $"Error: source file not found: {from}";
            var dstDir = Path.GetDirectoryName(dst);
            if (!string.IsNullOrEmpty(dstDir)) Directory.CreateDirectory(dstDir);
            File.Copy(src, dst, overwrite);
            return $"Copied {from} -> {to}.";
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    [Description("Create a directory (with parents).")]
    private string CreateDirectory(
        [Description("Relative or absolute path inside the working directory.")] string path)
    {
        try
        {
            var resolved = ResolveScoped(path);
            Directory.CreateDirectory(resolved);
            return $"Created directory {path}.";
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    private string Root()
    {
        var configured = _options.WorkingDirectory;
        if (string.IsNullOrWhiteSpace(configured)) return _launchInfo.OriginalWorkingDirectory;
        return Path.IsPathRooted(configured)
            ? Path.GetFullPath(configured)
            : Path.GetFullPath(Path.Combine(_launchInfo.OriginalWorkingDirectory, configured));
    }

    private string ResolveScoped(string requested)
    {
        var root = Root();
        var combined = Path.IsPathRooted(requested) ? requested : Path.Combine(root, requested);
        var full = Path.GetFullPath(combined);
        if (_options.AllowAnyPath) return full;
        var rootFull = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        if (!full.Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) &&
            !full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Path '{requested}' is outside the configured working directory '{root}'. " +
                $"Set Tools:AllowAnyPath=true (or DAGGER_Tools__AllowAnyPath=true) to permit, " +
                $"or set Tools:WorkingDirectory to a wider root.");
        }
        return full;
    }
}
