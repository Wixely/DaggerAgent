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
- **Source-control triggers**: in service mode DaggerAgent can poll GitHub / GitLab / Azure DevOps via MCP servers and spawn an agent job per fresh ticket. Discovery uses the MCP server's `list_issues` / `list_mentions_since` / `query_work_items` tools **directly** — no LLM in the discovery path, so it's deterministic and free. Matches are deduplicated in the local SQLite via a `trigger_seen` table; each fresh match seeds a regular agent job that then has the same MCP server's action tools available for the actual work. Configure via the **Trig** tab in the web UI (recommended — see [Source-control triggers](#source-control-triggers)) or by editing `Triggers` in `appsettings.json`.
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

## Source-control triggers

A background poller (`Triggers:Enabled=true`) scans configured ticket sources every `PollIntervalSeconds` and spawns an agent job per fresh match. Discovery calls the MCP server's tools directly — the LLM is only invoked once a match needs to be acted on.

**Configuration paths**

- **Web UI (recommended)** — open the **Trig** tab, set scalar options at the top, then **+ Add source** for each repo/project. Changes write to `data/runtime-config.json` and the running `TriggerService` picks them up on the next cycle — no restart.
- **appsettings.json** — same shape under the `Triggers` section; useful for first-boot defaults or version-controlling the config. Runtime-config overrides win once it exists.

**Prerequisites**

1. At least one MCP server connected (under the **MCP** tab) that talks to your forge. Your MCPSharp servers (github / gitlab / azdo) already expose the needed tools.
2. (Optional) an endpoint configured in the **Endp** tab if you want to route triggered jobs to a specific model or CLI agent.

**Per-source fields**

| Field         | Purpose                                                                                                                 |
| ------------- | ----------------------------------------------------------------------------------------------------------------------- |
| `Id`          | Stable id used for dedup state. Free-form, e.g. `gh-triage`.                                                            |
| `Kind`        | `GitHub` / `GitLab` / `AzureDevOps`. Picks the call shape against the MCP server.                                       |
| `Mode`        | `Mentions` (phrase in body/comments), `Label`, `Assignee`, or `AllNew` (every fresh issue).                             |
| `Filter`      | Mode-specific value: phrase for Mentions, label name for Label, username for Assignee, ignored for AllNew.              |
| `McpServer`   | Name of an entry in `Mcp.Servers` — the one that exposes the relevant tools for this forge.                             |
| `Scope`       | `owner/repo` for GitHub, `group/project` for GitLab, project name for Azure DevOps. Empty = MCP server's default.       |
| `EndpointId`  | Optional. Pin this source's jobs to a specific endpoint (e.g. a `ClaudeCli` / `CodexCli` endpoint). Empty = global default. |
| `Model`       | Optional. Model override passed through to the chosen endpoint. Empty = endpoint's `DefaultModel`.                       |

**Top-level fields**

| Field                  | Purpose                                                                                            |
| ---------------------- | -------------------------------------------------------------------------------------------------- |
| `Enabled`              | Master on/off. Toggling is hot — the poller picks it up on the next cycle.                         |
| `PollIntervalSeconds`  | Cycle interval. Minimum 5s in code; 120s is a sensible default.                                    |
| `Phrase`               | Global `Mentions` default when a source doesn't set `Filter`.                                      |
| `AllowedAuthors`       | Empty = anyone can drive the agent (not recommended in production). Match is case-insensitive.     |
| `MaxJobsPerCycle`      | Backstop against a flood of matches burning through tokens. Counted across all sources per cycle.  |
| `JobPreamble`          | Prefix prepended to every triggered job's seed prompt. Use it for project context or playbook nudges. |

**Example (`appsettings.json`)**

```jsonc
{
  "Triggers": {
    "Enabled": true,
    "PollIntervalSeconds": 120,
    "Phrase": "@dagger",
    "AllowedAuthors": [ "Wixely" ],
    "MaxJobsPerCycle": 5,
    "JobPreamble": "You were triggered by a ticket. Read the details below, decide whether to act, and proceed.",
    "Sources": [
      // Every issue tagged `ai-triage` in this repo, handled by the Claude Code CLI endpoint.
      { "Id": "gh-triage", "Kind": "GitHub", "Mode": "Label", "Filter": "ai-triage",
        "McpServer": "github", "Scope": "Wixely/DaggerAgent",
        "EndpointId": "claude-cli-pro", "Model": "claude-opus-4-7" },

      // Every comment containing "@dagger" on any open issue in this GitLab project.
      { "Id": "gitlab-mentions", "Kind": "GitLab", "Mode": "Mentions", "Filter": "@dagger",
        "McpServer": "gitlab", "Scope": "group/project" },

      // Every work item assigned to dagger-bot, default endpoint.
      { "Id": "azdo-bugs", "Kind": "AzureDevOps", "Mode": "Assignee", "Filter": "dagger-bot",
        "McpServer": "azdo", "Scope": "MyProject" }
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

**Hot-reload semantics**

`TriggerService` re-reads its `IOptions<TriggerOptions>` snapshot on every cycle, so edits via the UI (or hand-edits to `data/runtime-config.json`) take effect on the next iteration. Toggling `Enabled` off doesn't kill the loop — the service idles and resumes when you turn it back on. The dedup cursor (last-polled timestamp) is per-source and persists in the SQLite job DB, so flipping a source off and on later won't re-process old tickets.

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
