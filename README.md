# DaggerAgent

A pure-C# .NET 10 LLM agent that talks to OpenAI-protocol endpoints. Runs in three modes — interactive REPL, one-shot CLI, and HTTP service — and is deployable as a Windows Service or a Docker container.

> Status: scaffolded. Wiring is in place for MCP clients, resumable jobs (SQLite), context compression, and sub-agents. Real-world testing and feature polish in progress.

## Features

- **Three modes**: interactive REPL, one-shot CLI, HTTP service (Kestrel).
- **Multi-protocol upstream client**: `OpenAI` (OpenAI itself, Azure OpenAI, LM Studio, vLLM, any OpenAI-compatible endpoint) or `Ollama` (native `/api/chat` via OllamaSharp) — pick with `OpenAI:Provider` in config. Same code, swappable transport.
- **MCP client**: connects to any number of MCP servers over HTTP (streamable-HTTP transport) **or** stdio (child process); their tools are surfaced to the LLM alongside the built-in tools. See [MCP servers](#mcp-servers).
- **Built-in tools**:
  - **Always available (read-only safe)**: `read_file`, `list_files`, `glob`, `grep`, `head_file`, `tail_file`, `file_info`, `pwd`, `which`, `list_processes`, `http_get`, `spawn_subagent`, `recall_past_work` (if memory enabled).
  - **Opt-in via `Tools:AllowWrite`**: `write_file`, `edit_file`, `delete_file`, `move_file`, `copy_file`, `create_directory`, `remember` (if memory enabled). Off by default.
  - **Opt-in via `Tools:AllowShell`**: `exec_shell` with `shell` parameter (auto/cmd/powershell/pwsh/bash/sh). Off by default.
  - **`Tools:ReadOnly=true`** is a master kill-switch: blocks every mutating tool regardless of the AllowWrite/AllowShell flags. Useful for "let the agent investigate but never change anything" runs.
- **Diff-preview mode** (`Tools:WritePreview=true`): `write_file`/`edit_file` stage proposed changes and return a unified diff instead of writing; agent (or caller) must call `confirm_write` to apply. Adds `list_pending_writes` and `discard_write` tools.
- **API-key auth on inbound HTTP**: populate `Auth:ApiKeys` (or `DAGGER_Auth__ApiKeys__0`) to require an `X-Api-Key` header (or `Authorization: Bearer <key>`) on every request. Empty list = auth disabled (dev default). `/agent/healthz`, `/api/version`, `/v1/models` bypass the check.
- **Token + cost tracking**: real BPE counting (o200k Tiktoken) drives compression triggers. Every turn logs input/output/total tokens and per-model USD cost; cumulative totals surface on `JobView.totalInputTokens` / `totalOutputTokens` / `totalCostUsd`.
- **Cross-session memory (opt-in)**: `Memory:Enabled=true` activates a `recall_past_work` tool and an automatic save of every compression summary as an embedded memory. Embedding provider follows `OpenAI:Provider`; vector store lives in the same SQLite database.
- **Interactive hotkeys**: F2 lists slash commands; F3 opens a recent-sessions picker so you can resume by number instead of typing a job id.
- **Mention triggers**: when running in service mode, DaggerAgent can poll GitHub/GitLab/Azure DevOps via MCP servers (using their `list_mentions_since` tool) looking for a configurable phrase (default `@dagger`). Every fresh match spawns an agent job. Opt-in via `Triggers:Enabled=true`. Example:

```jsonc
{
  "Triggers": {
    "Enabled": true,
    "PollIntervalSeconds": 120,
    "Phrase": "@dagger",
    "AllowedAuthors": [ "Wixely" ],
    "MaxJobsPerCycle": 5,
    "Sources": [
      { "Id": "github-main", "Kind": "GitHub",      "McpServer": "github", "Scope": "Wixely/DaggerAgent" },
      { "Id": "gitlab-main", "Kind": "GitLab",      "McpServer": "gitlab", "Scope": "group/project" },
      { "Id": "azdo-main",   "Kind": "AzureDevOps", "McpServer": "azdo",   "Scope": "MyProject" }
    ]
  },
  "Mcp": {
    "Servers": [
      { "Name": "github", "Url": "http://localhost:5101/mcp" },
      { "Name": "gitlab", "Url": "http://localhost:5102/mcp" },
      { "Name": "azdo",   "Url": "http://localhost:5089/mcp" }
    ]
  }
}
```

The polling layer calls the MCP server's `list_mentions_since` tool **directly** (no LLM in the discovery path — deterministic and free). Matches are deduplicated in the local SQLite via a `trigger_seen` table. Each fresh match seeds a regular agent job which then has access to the same MCP server's action tools for the actual work.
- **Resumable jobs**: every turn is persisted to a local SQLite database; resume by job id.
- **Context compression**: when token usage exceeds a threshold, older history is summarised via the LLM itself and replaced by a single summary message.
- **Sub-agents**: the agent can spawn child agents with isolated context and budgets (depth + turn caps).
- **Windows Service / Docker** ready.

## Quick start

```bash
# Interactive REPL
dotnet run

# CLI one-shot
dotnet run -- run --prompt "Summarise the README in two sentences"

# HTTP service
dotnet run -- serve
curl http://localhost:5090/agent/healthz
```

## Configuration

Configuration is layered: `appsettings.json` → `appsettings.{Env}.json` → `appsettings.Local.json` → environment variables (no prefix) → environment variables with `DAGGER_` prefix → command-line arguments.

Secrets (`OpenAI:ApiKey`, MCP `AuthHeader`) belong in `appsettings.Local.json` (gitignored) or `DAGGER_OpenAI__ApiKey` environment variables.

The default committed config points at LM Studio on `http://localhost:1234/v1` with model `qwen/qwen3.6-27b` — change as needed.

### Talking to Ollama upstream

To talk to an Ollama server instead of an OpenAI-compatible one, set:

```jsonc
{
  "OpenAI": {
    "Provider": "Ollama",
    "BaseUrl": "http://localhost:11434",
    "DefaultModel": "llama3.1:8b"
  }
}
```

(Note the URL does **not** end in `/v1` for Ollama — that's only for its OpenAI-compat shim. Use `http://localhost:11434` for the native protocol.)

## MCP servers

DaggerAgent connects to MCP servers via two transports. There's no explicit `Type` field — the transport is chosen by which fields are set:

- **HTTP** (streamable-HTTP transport) — when `Url` is set. The server is contacted at that URL; pass an `Authorization` header value via `AuthHeader`.
- **stdio** (child process) — when `Url` is empty and `Command` is set. DaggerAgent spawns the command as a subprocess and speaks MCP over the child's stdin/stdout. This is how most published MCP servers ship (npm `@modelcontextprotocol/server-*`, the GitHub MCP server, Python MCP servers, etc.).

If both `Url` and `Command` are set, `Url` wins.

```jsonc
{
  "Mcp": {
    "Servers": [
      // HTTP — a remote / sidecar MCP server that exposes an HTTP endpoint.
      {
        "Name": "github-http",
        "Url": "http://localhost:5101/mcp",
        "AuthHeader": "Bearer my-token"
      },
      // stdio — npx-launched filesystem server, passing the allowed root as an arg.
      {
        "Name": "fs",
        "Command": "npx",
        "Arguments": [ "-y", "@modelcontextprotocol/server-filesystem", "C:\\Users\\me\\projects" ]
      },
      // stdio — uvx-launched Python MCP server with extra env vars and a working dir.
      {
        "Name": "search",
        "Command": "uvx",
        "Arguments": [ "mcp-server-fetch" ],
        "WorkingDirectory": "C:\\tools",
        "EnvironmentVariables": { "HTTP_PROXY": "http://proxy.local:8080" }
      }
    ]
  }
}
```

Tool names are surfaced to the LLM as `mcp.{Server-Name}.{tool}`; e.g. the filesystem server above would offer `mcp.fs.read_file`, `mcp.fs.list_directory`, etc.

## Running as a Windows Service

```cmd
dotnet publish -c Release -o publish
sc.exe create DaggerAgent binPath= "%CD%\publish\dagger.exe serve" start= auto
sc.exe start DaggerAgent
```

The app detects `WindowsServiceHelpers.IsWindowsService()` automatically; the explicit `serve` arg makes Service mode reliable inside containers too.

## Running in Docker

```bash
docker build -t daggeragent .
docker run --rm -p 5090:5090 \
  -e DAGGER_OpenAI__BaseUrl=http://host.docker.internal:1234/v1 \
  -v daggeragent-data:/data -v daggeragent-logs:/app/logs \
  daggeragent
```

## License

MIT.
