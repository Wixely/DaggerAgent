# Comparisons

DaggerAgent measured against six other open-source agents, to work out what it is
actually *for* and what is worth stealing.

**Read this first.** These are not peer projects. As of 2026-10-20 the six below have
between 55,000 and 393,000 GitHub stars and, in several cases, hundreds of contributors;
DaggerAgent has one contributor and its own README calls it *scaffolded*. Nothing here is
a claim that Dagger competes with them. The useful question is narrower and answerable:
**where does Dagger's shape differ, is that difference deliberate, and what has each of
these projects already solved that Dagger has not?**

Every page ends with *What Dagger could take from it* — that is the point of the folder.

## At a glance

| Project | Language | Licence | Shape | MCP | Stars | Strongest at |
| --- | --- | --- | --- | --- | ---: | --- |
| [**DaggerAgent**](../README.md) | **C# / .NET 10** | MIT | TUI · CLI · HTTP service + web UI · ACP · chat-room agent | client | 0 | Being a long-lived *service* on Windows/.NET estates |
| [pi](pi.md) | TypeScript | MIT | TUI + npm toolkit | not built in | 116k | Being a harness you reshape in code |
| [opencode](opencode.md) | TypeScript | MIT | TUI + desktop + web console | client | 218k | Provider breadth and polish |
| [cline](cline.md) | TypeScript | Apache-2.0 | IDE extension + CLI + SDK | client | 71k | Living where the developer already is |
| [goose](goose.md) | Rust | Apache-2.0 | Desktop + CLI + API | client, deeply | 55k | MCP as the whole extension story |
| [openclaw](openclaw.md) | TypeScript | MIT | Always-on service behind chat apps | client | 393k | Being reachable from anywhere, all the time |
| [hermes](hermes.md) | Python + JS | MIT | Self-hosted service + chat gateway | client | 255k | Compounding — memory and self-written skills |

Star counts and repo facts collected 2026-09-07, star counts refreshed 2026-10-20. Note two recent moves: `sst/opencode`
now redirects to `anomalyco/opencode`, and `block/goose` to `aaif-goose/goose` after the
handoff to the Linux Foundation's Agentic AI Foundation.

## The one-paragraph summary

Five of the six are written in TypeScript or Python. Goose is Rust. **Dagger is the only
one on .NET**, and that is the honest headline: not that it is better, but that it is the
only option in this list that a Windows/.NET shop can run as a plain `sc.exe create`
service, debug in Visual Studio, and extend without introducing a Node or Python runtime
to the estate. The repo description — *"C# LLM Agent (No NodeJS, no Python!)"* — is the
whole positioning, and it is a real one.

The second difference is posture. Most of these agents are **sessions you start**: you
open a TUI or an editor, work, and close it. Dagger's centre of gravity is a **process
that stays up** — `dagger serve` as a Windows Service or container, with a web UI, an
HTTP API, a job database, and a poller that opens work on its own. Only openclaw and
hermes share that always-on posture, and neither is aimed at a corporate forge.

## Where Dagger genuinely differs

Four things in Dagger are not standard equipment in this field:

1. **Source-control triggers with a deterministic discovery path.** Dagger polls
   GitHub / GitLab / Azure DevOps through MCP servers and spawns a job per fresh ticket —
   and the *discovery* calls `list_issues` / `list_mentions_since` / `query_work_items`
   directly, with no LLM in the loop. Matching is free and repeatable; the model is only
   billed once there is something to act on. None of the six ship an equivalent.
   (See [goose](goose.md) and [hermes](hermes.md) for the nearest things.)

2. **A permission model with a master kill-switch.** Read-only tools always on; writes
   behind `Tools:AllowWrite`; shell behind `Tools:AllowShell`; `Tools:ReadOnly=true`
   overrides both regardless. Plus diff-preview mode, where writes stage a unified diff
   and wait for `confirm_write`. Most of the field gates per-action at the UI ("approve
   this edit?") rather than per-capability at the process. pi ships **no** permission
   system at all and says so plainly.

3. **Agent-drives-agent.** `delegate_to_claude` / `delegate_to_codex` /
   `delegate_to_copilot` plus ACP endpoints mean a Dagger job can hand work to another
   vendor's CLI agent and keep the result in its own transcript. Cline's SDK is the
   closest analogue, and it points the other way.

4. **Banter mode.** A key-backed identity, one-time-code enrolment, and a room server that
   elects a delegator across a *fleet* of agents. openclaw and hermes both reach you
   through chat, but through consumer messengers as a personal assistant — not as
   several agents negotiating who takes a job. See [openclaw](openclaw.md).

## Where Dagger is behind

Stated plainly, because the folder is worthless otherwise:

- **No checkpoints / undo.** Cline tracks every change and rolls back. Dagger has
  diff-preview, which prevents bad writes but cannot reverse ones already applied.
- **No skills.** Hermes writes its own reusable procedures after hard tasks; goose has
  recipes; openclaw has a plugin marketplace. Dagger has config and a fixed tool list.
- **No sandbox.** Every tool runs in-process on the host. Hermes can push execution to
  Docker, SSH, Modal, Daytona or a Vercel sandbox; openclaw offers optional sandboxing.
- **Narrower provider support.** OpenAI-compatible, Ollama and native Anthropic, plus the
  Claude Code / Codex / Copilot CLIs as endpoints. opencode claims 75+, goose 15+. In
  practice OpenAI-compatible covers most of the rest, but "most" is not "all".
- **No plan/act split as a first-class mode.** `Tools:ForcePlan` is a flag, not the
  two-mode workflow that opencode and cline have converged on independently.
- **It is unproven.** Six of these have production users. Dagger has its own README
  saying "real-world testing and feature polish in progress."

## Pages

- [pi](pi.md) — the harness-you-reshape philosophy, minus a permission model
- [opencode](opencode.md) — what "the default terminal coding agent" looks like at scale
- [cline](cline.md) — the IDE-native path, and the checkpoint idea worth copying
- [goose](goose.md) — the closest architectural cousin, in Rust
- [openclaw](openclaw.md) — always-on and reachable, the nearest thing to Banter mode
- [hermes](hermes.md) — the agent that compounds, and the sandbox story Dagger lacks
