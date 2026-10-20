# DaggerAgent vs opencode

[anomalyco/opencode](https://github.com/anomalyco/opencode) · TypeScript · MIT · ~218k
stars (2026-10-20) · *"The open source coding agent."*

Formerly `sst/opencode`. SST — the infrastructure company, since rebranded Anomaly — put
its original product into maintenance mode and moved development here.

## What it is

The default answer to "terminal AI coding agent" for a very large number of people.
Provider-agnostic on a bring-your-own-key model with 75+ providers, local models via
Ollama and llama.cpp, and a TUI as the primary surface — now joined by a beta desktop app
for macOS, Windows and Linux and a web console. Work is split across two agents you toggle
between: **Build** (full access, the default) and **Plan** (read-only analysis; denies file
edits and asks before running bash). A general subagent handles multi-step searches.

## Where it overlaps Dagger

Both are provider-agnostic, both run local models, both keep the model choice in the
user's hands rather than bundling one. Both have converged on the same core safety idea
from different directions: opencode's Plan/Build toggle and Dagger's
`ForcePlan` + `ReadOnly` + `AllowWrite` flags are attacking the same problem — stop the
agent editing things while it is still figuring out what to do.

## Where opencode is ahead

- **Provider breadth.** 75+ providers against Dagger's OpenAI-compatible, Ollama and
  native Anthropic clients (plus Claude Code / Codex / Copilot CLIs as endpoints). In
  practice OpenAI-compatible endpoints cover most of the market, and Dagger's multi-endpoint
  config lets you register several at once — but "most" is not "all", and opencode has
  done the per-provider quirk work.
- **Plan/Build as a mode, not a flag.** opencode makes the distinction a first-class thing
  you flip mid-session and see in the UI. In Dagger it is configuration you set before the
  job. Same intent; opencode's version is the one a user actually reaches for.
- **Surface polish and reach.** TUI, desktop app, web console, and installation through
  npm, Homebrew, Nix and system package managers. Dagger ships release zips (win-x64, linux-x64) and a GHCR Docker image, but no package-manager install.
- **Sheer exercise.** 7.5M monthly developers reported as of mid-2026. Every rough edge
  has been hit by someone.

## Where Dagger is ahead

- **It stays up.** opencode is a session you open. Dagger's `serve` mode is a Windows
  Service or container with an HTTP API, a job store and a background poller. Different
  posture, and the reason the triggers feature can exist at all.
- **Triggers.** Nothing in opencode opens work by itself off a GitHub label or an Azure
  DevOps work item. Dagger's poller does, with no LLM in the discovery path.
- **Cost and token accounting as a feature.** Real BPE counting drives compression, and
  per-job input/output/cost totals are surfaced on the job record — not just shown in a
  session footer.
- **.NET.** Same point as everywhere in this folder: no Node runtime on the box.

## What Dagger could take from it

1. **Make plan-mode a runtime toggle.** Dagger already has the pieces — `ForcePlan`,
   `ReadOnly`, the tool gates. What is missing is one visible control in the web UI and
   the TUI that flips a job between "look, don't touch" and "go", mid-job, the way
   opencode's Plan/Build does. This is the single highest-value idea in this folder.
2. **Get into a package manager.** Release zips and a public Docker image already exist; a
   `dotnet tool install -g` package or a winget manifest would make trying it one command.
3. **Reconsider the desktop surface.** opencode adding a desktop app after the TUI
   suggests the TUI alone is not enough for everyone. Dagger's answer is already a web UI
   — arguably a better one for a service — but it is worth noting the direction of travel.

## Sources

- [anomalyco/opencode](https://github.com/anomalyco/opencode)
- [OpenCode Developer Guide 2026](https://www.developersdigest.tech/blog/opencode-developer-guide-2026)
- [SST Put Itself in Maintenance Mode to Build an AI Agent](https://bex.co/blog/2026/08/22/sst-maintenance-mode-opencode-650k-agent-pivot)
