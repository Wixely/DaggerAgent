# DaggerAgent vs goose

[aaif-goose/goose](https://github.com/aaif-goose/goose) · Rust · Apache-2.0 · ~55k stars
(2026-10-20) · *"an open source, extensible AI agent that goes beyond code suggestions"*

Formerly `block/goose`, built at Block (Square, Cash App). Moved to the Linux Foundation's
Agentic AI Foundation in 2026, so it is now foundation-governed rather than one company's
project.

## What it is

A general-purpose local agent — code, research, writing, automation, data analysis —
shipped as a native desktop app for macOS/Linux/Windows, a CLI, and an API. 15+ model
providers including Anthropic, OpenAI, Google, Ollama, OpenRouter, Azure and Bedrock. Its
defining choice is that **extension means MCP**: 70+ documented extensions covering
databases, browsers, GitHub, Drive, Slack, Jira and calendars, with one of the deepest MCP
integrations in the ecosystem.

## Where it overlaps Dagger

Of the six, this is the closest architectural cousin. Both are general-purpose rather than
coding-only. Both are compiled, single-binary, no-scripting-runtime agents — Rust and .NET
respectively — in a field otherwise made of TypeScript. Both run locally against whatever
model you point them at. Both treat MCP as the way capability arrives. Both offer more than
one surface (goose: desktop + CLI + API; Dagger: REPL + CLI + web + ACP + rooms).

If you want to know what Dagger might look like with five years and a payments company
behind it, it looks a fair bit like goose.

## Where goose is ahead

- **Extensions as an ecosystem.** 70+ curated MCP extensions with docs. Dagger connects to
  any MCP server too — same protocol, same capability — but there is no catalogue, no
  install flow, and nothing to browse. That is the whole difference between "supports MCP"
  and "has an extension story".
- **Governance.** A Linux Foundation project outlives its founders. That matters to anyone
  choosing a dependency, and it is the one axis where a solo project can never compete.
- **Production evidence.** Goose automates real engineering at Block. Dagger's README says
  "real-world testing in progress."
- **Recipes.** Repeatable, shareable task definitions — the thing you reach for when the
  same job comes up weekly.

## Where Dagger is ahead

- **Triggers.** Goose can *do* GitHub work through an MCP extension when you ask it to.
  Dagger goes looking — polling GitHub / GitLab / Azure DevOps on a timer and opening a job
  per fresh match, with the discovery calls made directly against the MCP server so no
  tokens are spent deciding there is nothing to do. This is Dagger's most defensible
  feature and goose does not have it.
- **Job persistence as a first-class model.** Resumable jobs with ids, transcripts, token
  and cost totals, and compression summaries in SQLite. Dagger is built around the job
  record; goose around the session.
- **Enterprise-forge posture.** Azure DevOps support, API-key auth on inbound HTTP, and a
  Windows Service install path point at a specific customer that goose does not target.
- **.NET, and the Windows estate that implies.**

## What Dagger could take from it

1. **Recipes.** The single most useful borrowable feature after Cline's checkpoints. A
   named, versioned, shareable "here is the job, the tools it may use, the endpoint it
   runs on" definition would slot straight into Dagger's existing job model — and it is
   most of what `JobPreamble` on a trigger source is already groping towards.
2. **An extension catalogue, even a small one.** Dagger has full MCP support and nothing
   that helps a user find a server to point it at. A curated list in the docs, and a
   one-click add in the MCP tab, would cost little.
3. **Watch the governance lesson.** Not something to copy, but worth knowing: goose's
   move to AAIF is what a serious project does when it wants adopters to trust it. For
   Dagger the equivalent is a licence people can rely on (MIT, done) and a clear statement
   of what is stable versus scaffolded.

## Sources

- [aaif-goose/goose](https://github.com/aaif-goose/goose)
- [goose docs](https://goose-docs.ai/)
- [Introduction to Goose: Extending Block's Open-Source Agent with MCP](https://zenn.dev/kai_kou/articles/198-goose-aaif-mcp-agent-developer-guide?locale=en)
- [Goose CLI Review: After the Linux Foundation Handoff](https://pickuma.com/for-dev/goose-cli-review-block-open-source-agent/)
