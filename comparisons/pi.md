# DaggerAgent vs pi

[earendil-works/pi](https://github.com/earendil-works/pi) · TypeScript · MIT · ~116k stars
(2026-10-20) · *"AI agent toolkit: unified LLM API, agent loop, TUI, coding agent CLI"*

## What it is

A deliberately small coding-agent **harness** you are expected to reshape. The core is a
nested loop — call the model, run whatever tools come back, repeat — and everything above
that is published as separate npm packages: a unified LLM API across OpenAI, Anthropic,
Google and others, the agent runtime, a TUI library with differential rendering, and the
coding-agent CLI itself. The pitch is that you adapt the agent to your workflow rather
than adapting yourself to the tool, and that the agent can read its own docs and help you
extend it.

## Where it overlaps Dagger

This is the closest project in the list *philosophically*. Both are built on the premise
that the interesting part is the harness, not the model, and that the owner should be able
to change it. Both keep the loop legible rather than hiding it behind a product surface.
Both are provider-agnostic by design.

## Where pi is ahead

- **Extensibility is code, not config.** pi ships as a package set — you `import` the
  agent core or the TUI library and build something. Dagger's extension story is
  `appsettings.json`, the MCP server list, and recompiling. Anything Dagger doesn't
  already do, you add in C# and rebuild; anything pi doesn't do, you compose.
- **A real ecosystem.** npm distribution, a package system, community tooling. Dagger is
  one binary from one repo.
- **Scale of use.** 116k stars against 0 is not a quality judgement, but it does mean
  pi's loop has been exercised against far more models and edge cases than Dagger's.

## Where Dagger is ahead

- **Permissions.** pi's own docs are blunt: *"Pi does not include a built-in permission
  system for restricting filesystem, process, network, or credential access."* Its answer
  is to containerise the whole thing. Dagger's answer is in-process and graded —
  read-only by default, `AllowWrite` and `AllowShell` as separate grants, `ReadOnly` as a
  kill-switch that beats both, and diff-preview staging writes behind `confirm_write`.
  For an agent pointed at a corporate repo that difference matters.
- **It is a service.** pi is a CLI you run. Dagger is a CLI *and* a Kestrel service with a
  web UI, an HTTP API, resumable jobs in SQLite, and a Windows Service install path.
- **Durable state.** Jobs, transcripts, token and cost totals, and cross-session memory
  all persist. pi's session story is thinner — a community tool for publishing sessions
  to Hugging Face is what surfaces.
- **No Node on the box.** The entire point of the C# build.

## What Dagger could take from it

1. **Split the assembly.** Dagger is one project. The agent loop, the tool registry, the
   MCP host and the LLM client layer would each make a defensible NuGet package, and
   "use DaggerAgent as a library" becomes a real answer instead of "fork it".
2. **Let the agent read its own source and docs.** pi leans hard on the agent being able
   to explain and extend itself. Dagger already has `read_file`, `glob` and `grep` — a
   packaged system prompt that points the agent at its own repo would get most of the way
   there for free.
3. **Document the sandbox story honestly.** pi has no permission system and says so, then
   documents containerisation patterns. Dagger has permissions but no sandbox — every
   tool runs in-process on the host. Saying that as plainly as pi does would be an
   improvement over leaving it implied.

## Sources

- [earendil-works/pi](https://github.com/earendil-works/pi)
- [Pi: A Coding Agent Harness You Can Reshape Around Your Workflow](https://silenceper.com/en/article/2026-05-27-pi-coding-agent-harness/)
- [Pi: The Open-Source AI Coding Agent You Probably Haven't Tried Yet](https://dev.to/arshtechpro/pi-the-open-source-ai-coding-agent-you-probably-havent-tried-yet-2h0h)
