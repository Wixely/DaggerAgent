# DaggerAgent vs Hermes

[NousResearch/hermes-agent](https://github.com/NousResearch/hermes-agent) · Python + JS ·
MIT · ~255k stars (2026-10-20) · *"The agent that grows with you"*

Released February 2026 by Nous Research. ~5,800 commits and 760+ contributors by the
August 2026 "Pantheon" release.

## What it is

A self-hosted agent built around **compounding**. Three ideas carry it:

- **Persistent memory** — agent-curated, with FTS5 session search and LLM summarisation
  for cross-session recall.
- **Skills** — after a hard task the agent writes a reusable procedure for itself, and
  those skills improve as they are used. Compatible with the agentskills.io standard.
- **Deployment as a design constraint** — a $5 VPS, a GPU cluster or serverless, with
  **seven terminal backends**: local, Docker, SSH, Singularity, Modal, Daytona and Vercel
  Sandbox.

Around that: a unified gateway to Telegram, Discord, Slack, WhatsApp, Signal and CLI; 40+
tools; MCP support; isolated subagents for parallel work; and desktop apps with a "Bot
Mode" where several named agents talk to each other and to you.

## Where it overlaps Dagger

More than is obvious. Both persist memory across sessions with summarisation and search —
Dagger saves every compression summary as an embedded memory in the same SQLite database
and exposes `recall_past_work`, which is recognisably the same mechanism. Both spawn
isolated sub-agents with their own context. Both are self-hosted services rather than
sessions. Both are MCP clients. Both reach users through chat — Hermes through consumer
messengers, Dagger through Banter rooms.

Hermes' Bot Mode — several named agents with their own faces, talking to each other in
group chats — is startlingly close to what Banter mode is for.

## Where Hermes is ahead

- **Skills.** The agent writing its own reusable procedures and improving them in use is
  the one capability here that Dagger has no analogue for at all. Dagger's memory
  *remembers*; it does not turn what it learned into a callable thing.
- **Execution backends.** Seven of them, including remote and sandboxed. Dagger runs every
  tool in-process on the host, full stop. For anything you would not want touching your
  workstation, Hermes has an answer and Dagger does not.
- **Gateway breadth.** Six chat platforms against Banter's one.
- **Momentum.** 760+ contributors. This matters more for an agent that self-improves than
  it would elsewhere — the skill and memory machinery is where the exercise pays off.

## Where Dagger is ahead

- **Determinism where it counts.** Dagger's trigger discovery deliberately keeps the LLM
  out of the loop: `list_issues` / `query_work_items` called directly, matches deduped in
  SQLite. Hermes' whole design leans the other way — an agent that changes its own
  behaviour over time. Both are defensible; only one is auditable, and for a thing pointed
  at a corporate backlog that is the point.
- **Graded permissions and diff-preview.** `ReadOnly` as a master kill-switch, writes and
  shell as separate grants, `confirm_write` staging unified diffs. A self-improving agent
  makes that kind of hard boundary *more* necessary, not less.
- **Cost accounting.** Real BPE counting and per-job USD totals. When an agent invents its
  own work, knowing what it spent is not a nice-to-have.
- **.NET, and no Python or Node runtime.** Against a Python+JS agent this is the sharpest
  the contrast gets.

## What Dagger could take from it

1. **Turn memory into skills.** Dagger already saves compression summaries as embedded
   memories and can retrieve them. The missing half is promotion: when a job finishes
   successfully, let the agent write a short named procedure — the tools it used, the order,
   the gotchas — and offer those back on later jobs. It is a much smaller step from where
   Dagger already is than it looks.
2. **A remote execution backend.** Even one — `exec_shell` against a Docker container or
   an SSH host instead of the local process — would change what Dagger can safely be
   pointed at. Hermes having seven suggests the abstraction is worth the effort.
3. **Take the self-modification warning seriously.** Hermes is the cautionary case as much
   as the inspiration: an agent that rewrites its own skills is very hard to audit after
   the fact. If Dagger grows skills, they should be files in the repo that a human can
   read and diff — not opaque rows in the job database.

## Sources

- [NousResearch/hermes-agent](https://github.com/NousResearch/hermes-agent)
- [Hermes Agent — Open-Source AI Agent with Persistent Memory](https://hermes-agent.org/)
- [Official Hermes Agent Breakdown (2026) — Vellum](https://www.vellum.ai/blog/official-hermes-agent-breakdown)
- [Hermes Agent 2026: Self-Improving Open-Source AI Agent](https://www.ai.cc/blogs/hermes-agent-2026-self-improving-open-source-ai-agent-vs-openclaw-guide/)
