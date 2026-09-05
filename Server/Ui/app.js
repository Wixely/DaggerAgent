// Dagger Agent — embedded Web UI controller.
//
// Single-file vanilla module. Talks to the same base path the HTML loaded from
// (default /agent) so a non-default ServerOptions.Path keeps working without rebuilds.
//
// Concerns, top → bottom:
//   1. base path + API client (fetch + SSE-over-POST)
//   2. global state holder
//   3. DOM lookup shortcuts
//   4. transcript rendering (streaming-friendly)
//   5. right-side tabs (mcp / tools / commands / settings / plan / writes)
//   6. composer (textarea, toggles, image attachments, queue)
//   7. job-list sidebar
//   8. SSE event dispatch
//   9. boot

// ───────────────────────────────────────────────────────────
// 1. base path + API client
// ───────────────────────────────────────────────────────────

import { $, el, escapeHtml } from "./core/dom.js";
import { createApi, resolveBasePath, getApiKey, setApiKey } from "./core/api.js";
import { createTranscript } from "./core/transcript.js";
import { createSession } from "./core/session.js";
import { createToast } from "./core/toast.js";

const BASE_PATH = resolveBasePath();

const apiKeyDialog = document.getElementById("api-key-dialog");
const apiKeyInput = document.getElementById("api-key-input");

// Handed to createApi as its 401 handler. Owning the dialog is the shell's job; the
// client only needs to know whether a fresh key was entered. createApi single-flights
// the call, so concurrent 401s share one dialog rather than double-calling showModal().
function promptForKey(reason) {
  return new Promise((resolve) => {
    apiKeyInput.value = getApiKey();
    apiKeyDialog.querySelector("p").textContent = reason || "Server rejected the request. Enter the configured API key.";
    apiKeyDialog.showModal();
    apiKeyDialog.addEventListener("close", function once() {
      apiKeyDialog.removeEventListener("close", once);
      if (apiKeyDialog.returnValue === "save") {
        setApiKey(apiKeyInput.value.trim());
        resolve(true);
      } else {
        resolve(false);
      }
    });
  });
}

const { api, streamPost } = createApi({ basePath: BASE_PATH, onUnauthorized: promptForKey });

const showToast = createToast(document.getElementById("toast"), document.querySelector(".composer"));

// ───────────────────────────────────────────────────────────
// 2. global state
// ───────────────────────────────────────────────────────────

const state = {
  jobs: [],
  jobFilter: "",
  currentJobId: null,
  currentMsg: null,         // DOM ref for the in-flight assistant message
  currentFooter: null,      // footer node — kept as the very last child of currentMsg
  lastBlock: null,          // DOM ref for the most recently appended segment block
  lastBlockType: null,      // "answer" | "thinking" | "tool_call"
  toolCallNodes: {},        // tool-call-id → DOM node (for matching tool_result events)
  streaming: false,
  abortCtrl: null,
  pendingImages: [],        // [{mediaType, base64, dataUrl}]
  queue: [],                // [{id, prompt, images}]
  settings: null,
  mcpServers: [],
  tools: [],
  plan: null,
};

// ───────────────────────────────────────────────────────────
// 3. DOM shortcuts
// ───────────────────────────────────────────────────────────

const els = {
  statusPill: $("status-pill"),
  jobIdLabel: $("job-id-label"),
  btnRefreshJob: $("btn-refresh-job"),
  btnTheme: $("btn-theme"),
  btnRightToggle: $("btn-right-toggle"),
  btnNewJob: $("btn-new-job"),
  jobSearch: $("job-search"),
  jobsList: $("jobs-list"),
  transcript: $("transcript"),
  composer: $("composer"),
  queueStrip: $("queue-strip"),
  queueList: $("queue-list"),
  imageStrip: $("image-strip"),
  workingDir: $("working-dir"),
  workingDirs: $("working-dirs"),
  endpointSelect: $("endpoint-select"),
  modelInput: $("model-input"),
  tPlan: $("t-plan"),
  tPreview: $("t-preview"),
  tShell: $("t-shell"),
  tReadonly: $("t-readonly"),
  promptBox: $("prompt-box"),
  btnImage: $("btn-image"),
  imageInput: $("image-input"),
  btnSend: $("btn-send"),
  btnCancel: $("btn-cancel"),
  tabs: document.querySelectorAll(".right-tabs .nav-link"),
  rightTabs: $("right-tabs"),
  btnAdvancedTabs: $("btn-advanced-tabs"),
  toast: $("toast"),
  composer: $("composer"),
  composerContext: $("composer-context"),
  panes: {
    endpoints: $("tab-endpoints"),
    mcp: $("tab-mcp"),
    triggers: $("tab-triggers"),
    banter: $("tab-banter"),
    tools: $("tab-tools"),
    commands: $("tab-commands"),
    settings: $("tab-settings"),
    plan: $("tab-plan"),
    writes: $("tab-writes"),
  },
  endpointsList: $("endpoints-list"),
  btnEndpointAdd: $("btn-endpoint-add"),
  btnMcpAdd: $("btn-mcp-add"),
  mcpList: $("mcp-list"),
  triggersOptionsForm: $("triggers-options-form"),
  triggersSourcesList: $("triggers-sources-list"),
  btnTriggerAdd: $("btn-trigger-add"),
  banterStatus: $("banter-status"),
  banterEnrol: $("banter-enrol"),
  banterConfigForm: $("banter-config-form"),
  toolsList: $("tools-list"),
  commandsList: $("commands-list"),
  settingsForm: $("settings-form"),
  planDisplay: $("plan-display"),
  writesList: $("writes-list"),
  appMain: document.querySelector(".app-main"),
  btnMcpReload: $("btn-mcp-reload"),
  btnBrowseCwd: $("btn-browse-cwd"),
  btnLeftToggle: $("btn-left-toggle"),
  leftPane: document.querySelector(".left-pane"),
  rightPane: document.querySelector(".right-pane"),
  drawerBackdrop: $("drawer-backdrop"),
  folderDialog: $("folder-dialog"),
  folderPath: $("folder-current-path"),
  folderEntries: $("folder-entries"),
  folderCancel: $("folder-cancel"),
  folderSelect: $("folder-select"),
};


// Unique ids for generated form controls, so each <label> can carry a matching `for`.
// Without it the right-pane forms hand a screen reader a pile of unnamed inputs.
let _fieldSeq = 0;
function fieldId(prefix) { return (prefix || "f") + "-" + (++_fieldSeq); }


// ───────────────────────────────────────────────────────────
// 4. transcript rendering
// ───────────────────────────────────────────────────────────

// How the desktop shell wants the shared transcript to read. The machinery lives in
// core/transcript.js; everything here is the wording, classes and node shapes that
// differ from mobile.
const transcriptView = {
  emptyState: () => el("div", { class: "empty-state" },
    // Matches the markup in index.html, so a cleared transcript looks like a fresh load.
    el("div", { class: "empty-mark", "aria-hidden": "true" }, "†"),
    el("h3", { class: "mb-2" }, "Ready when you are."),
    el("p", { class: "text-muted" }, "Submit a prompt below to start a new job.")),
  historyToolBlock: (text) => el("div", { class: "tool-call" },
    el("span", { class: "tc-name" }, "← tool result"),
    el("span", { class: "tc-result" }, text.slice(0, 800))),
  userImage: (img) => el("div", { class: "img-thumb" }, el("img", { src: img.dataUrl })),
  copyButton: (getText) => el("button", {
    class: "copy-btn",
    onclick: () => navigator.clipboard.writeText(getText()),
  }, "copy"),
  retryButton: (onClick) => el("button", {
    class: "retry-btn",
    title: "Resend the previous prompt",
    onclick: onClick,
  }, "retry"),
  toolName: (name) => `→ ${name}`,
  toolArgs: (args) => {
    const s = args ? formatToolArgs(args) : "";
    return s ? `(${s})` : "()";
  },
  toolPending: "…",
  toolPendingClass: "text-muted",
  toolProgress: (elapsedMs) => `… ${formatDuration(elapsedMs)}`,
  toolActivity: (name, elapsedMs) => `↳ ${name} ${formatDuration(elapsedMs)}`,
  permissionPrompt: (data, onChoose) => {
    const wrap = el("div", { class: "permission-prompt" },
      el("span", { class: "pp-title" }, `${data.agent || "delegated agent"} asks: ${data.title || "permission"}`));
    const row = el("div", { class: "pp-actions" });
    for (const opt of data.options || []) {
      row.appendChild(el("button", {
        class: `pp-btn ${String(opt.kind || "").startsWith("allow") ? "pp-allow" : "pp-reject"}`,
        onclick: () => onChoose(opt.id),
      }, opt.name || opt.id));
    }
    wrap.appendChild(row);
    return wrap;
  },
  permissionResolved: (optionId) => el("span", { class: "pp-resolved text-muted" },
    optionId ? `→ permission: ${optionId}` : "→ permission request dismissed"),
  // durationMs is the tool's own measured run time. It is absent when no measurement
  // reached the stream, and the timing is then simply omitted rather than shown as zero.
  toolResult: (excerpt, length, durationMs) =>
    `← ${excerpt} (${length} chars${durationMs == null ? "" : `, ${formatDuration(durationMs)}`})`,
  thinkingSummary: "thinking…",
  usageStampClass: "usage-stamp text-muted",
  usageText: (u) => `in:${u.inputTokens} out:${u.outputTokens} think:${u.thinkingTokens} · ${u.costUsd ? `$${Number(u.costUsd).toFixed(4)}` : "$0"}`,
};

const transcript = createTranscript({
  mount: els.transcript,
  state,
  view: transcriptView,
  // 120px leaves room for a chunk or two of buffered repaint while still letting a
  // deliberate scroll upwards break the stick.
  stickThreshold: 120,
  // Resolved at click time, so the session below can be declared after this.
  onRetry: (turn) => session.runTurn(turn.prompt, turn.images),
});

const {
  withScrollStick, clearTranscript, renderHistory, appendUserMessage, beginAssistantMessage,
  pushSegment, appendAnswerChunk, appendThinkingChunk, appendToolCall, appendToolResult,
  setUsageStamp, showRetryButton,
} = transcript;

function formatToolArgs(args) {
  if (typeof args === "string") return args.slice(0, 80);
  try {
    return Object.entries(args)
      .slice(0, 4)
      .map(([k, v]) => {
        const s = typeof v === "string" ? `"${v.slice(0, 30)}"` : JSON.stringify(v);
        return `${k}=${s && s.length > 40 ? s.slice(0, 40) + "…" : s}`;
      })
      .join(", ");
  } catch { return ""; }
}

// Tool durations span three orders of magnitude in one transcript - a cached read is
// milliseconds, a delegated CLI run is minutes - so the unit changes with the value
// rather than forcing a long run to read as "192000ms".
function formatDuration(ms) {
  if (!Number.isFinite(ms) || ms < 0) return "";
  if (ms < 1000) return `${Math.round(ms)}ms`;
  const seconds = ms / 1000;
  if (seconds < 60) return `${seconds.toFixed(1)}s`;
  const minutes = Math.floor(seconds / 60);
  return `${minutes}m ${Math.round(seconds - minutes * 60)}s`;
}

function switchTab(name) {
  for (const btn of els.tabs) {
    const on = btn.dataset.tab === name;
    btn.classList.toggle("active", on);
    // Keep the ARIA state and the roving tabindex in step with the class, or the
    // tablist announces the wrong tab and Tab lands on all eight of them.
    btn.setAttribute("aria-selected", String(on));
    if (on) btn.removeAttribute("tabindex"); else btn.setAttribute("tabindex", "-1");
  }
  for (const [k, pane] of Object.entries(els.panes)) pane.classList.toggle("active", k === name);
  if (name === "endpoints") loadEndpoints();
  if (name === "mcp") loadMcpConfig();
  if (name === "triggers") loadTriggers();
  if (name === "banter") loadBanter();
  if (name === "plan") loadPlan();
  if (name === "writes") loadPendingWrites();
  revealAdvancedIfNeeded();
  updateComposerContext();
}

// The five configuration tabs fold behind Advanced under 780px. Nothing here does
// anything at desktop width: the class is inert and the toggle is display:none.
const ADVANCED_TABS = new Set(["endpoints", "mcp", "triggers", "banter", "tools", "commands"]);

function activeTabName() {
  const btn = Array.from(els.tabs).find((b) => b.classList.contains("active"));
  return btn ? btn.dataset.tab : null;
}

// Whatever is in view has to be reachable. If the active tab lives in the folded group,
// open the group rather than leave the one tab you are looking at hidden.
function revealAdvancedIfNeeded() {
  if (!ADVANCED_TABS.has(activeTabName())) return;
  els.rightTabs.classList.add("show-advanced");
  els.btnAdvancedTabs.setAttribute("aria-expanded", "true");
}

// The composer summary line, shown only under 780px. It has to say enough that you can
// send a turn without expanding it: where it will run, which endpoint, and any tool
// switch that changes what the agent is allowed to do. Read-only and Preview writes are
// the ones worth surfacing - not knowing they are on is how you lose a turn.
function updateComposerContext() {
  const sep = /[\\/]/;   // Windows and POSIX separators both
  const dir = els.workingDir.value.trim().replace(/[\\/]+$/, "");
  const leaf = dir.split(sep).filter(Boolean).pop() || dir || "default dir";
  const sel = els.endpointSelect;
  // Only name an endpoint when one was actually picked: the placeholder option reads
  // "(use active default)", which costs a line of a 390px summary to say nothing.
  const endpoint = sel && sel.value && sel.selectedIndex >= 0
    ? sel.options[sel.selectedIndex].textContent.trim()
    : "";
  const model = els.modelInput.value.trim();
  const flags = [
    els.tReadonly.checked && "read-only",
    els.tPreview.checked && "preview writes",
    els.tShell.checked && "shell",
    els.tPlan.checked && "plan",
  ].filter(Boolean);
  els.composerContext.textContent =
    [leaf, model || endpoint, flags.join(", ")].filter(Boolean).join("  \u00b7  ");
}

function toggleComposerContext() {
  const open = els.composer.classList.toggle("show-context");
  els.composerContext.setAttribute("aria-expanded", String(open));
}

function toggleAdvancedTabs() {
  const open = els.rightTabs.classList.toggle("show-advanced");
  els.btnAdvancedTabs.setAttribute("aria-expanded", String(open));
  // Closing the group while one of its tabs is showing would hide the active tab, so
  // fall back to Settings, which is the one that stays out at every width.
  if (!open && ADVANCED_TABS.has(activeTabName())) switchTab("settings");
}

// ───────────────────────────── endpoints (LLM CRUD) ─────────────────────────────

async function loadEndpoints() {
  try {
    state.endpoints = await api("/endpoints");
    renderEndpoints();
    populateEndpointDropdown();
  } catch (e) { console.warn("endpoints load failed", e); }
}

function populateEndpointDropdown() {
  const sel = els.endpointSelect;
  const current = sel.value;
  sel.replaceChildren(el("option", { value: "" }, "(use active default)"));
  if (!state.endpoints || !state.endpoints.items) return;
  for (const e of state.endpoints.items) {
    if (!e.enabled) continue;
    const label = e.displayName ? `${e.displayName} (${e.id})` : e.id;
    sel.appendChild(el("option", { value: e.id }, label));
  }
  // Restore previous selection if still valid; otherwise leave default.
  if (current && Array.from(sel.options).some(o => o.value === current)) sel.value = current;
  updateComposerContext();   // the endpoint list arriving moves these without an event
}

function renderEndpoints() {
  const c = els.endpointsList;
  c.replaceChildren();
  if (!state.endpoints || !state.endpoints.items || state.endpoints.items.length === 0) {
    c.appendChild(el("p", { class: "text-muted small" },
      "No endpoints configured yet. Click ", el("strong", {}, "+ Add endpoint"),
      " to wire up an LLM (OpenAI, Anthropic, Ollama, LM Studio, OpenRouter, OpenWebUI, etc.)."));
    return;
  }
  for (const e of state.endpoints.items) {
    c.appendChild(renderEndpointCard(e, e.id === state.endpoints.defaultId));
  }
}

function renderEndpointCard(e, isActive) {
  const card = el("div", { class: "endpoint-card" + (isActive ? " is-active" : "") });
  const head = el("div", { class: "endpoint-head" },
    el("span", { class: "endpoint-name" }, (e.displayName || e.id) + (isActive ? " ★" : "")),
    el("span", { class: "endpoint-provider" }, e.provider));
  card.appendChild(head);

  const meta = [];
  if (e.baseUrl) meta.push(e.baseUrl);
  if (e.defaultModel) meta.push("model: " + e.defaultModel);
  if (e.hasApiKey) meta.push("key: " + e.apiKeyMasked);
  if (!e.enabled) meta.push("disabled");
  card.appendChild(el("div", { class: "endpoint-meta" }, meta.join(" · ") || "(no details)"));

  const actions = el("div", { class: "endpoint-actions" });
  if (!isActive) {
    actions.appendChild(el("button", {
      class: "btn btn-sm btn-outline-success",
      onclick: () => activateEndpoint(e.id),
    }, "Activate"));
  }
  const editBtn = el("button", { class: "btn btn-sm btn-outline-secondary" }, "Edit");
  actions.appendChild(editBtn);
  actions.appendChild(el("button", {
    class: "btn btn-sm btn-outline-danger",
    onclick: () => deleteEndpoint(e.id),
  }, "Delete"));
  card.appendChild(actions);

  const form = renderEndpointForm(e);
  form.style.display = "none";
  card.appendChild(form);
  editBtn.addEventListener("click", () => {
    form.style.display = form.style.display === "none" ? "flex" : "none";
  });
  return card;
}

function renderEndpointForm(e, isNew = false) {
  const form = el("form", { class: "endpoint-form" });
  const field = (label, attrs) => {
    const a = attrs || {};
    const id = fieldId("fld");
    // Checkboxes render as toggle switches — the generic .form-control class stretches a
    // checkbox into an oversized rectangle, so swap to .form-check + .form-switch and put
    // the label after the control (which is the switch idiom).
    if (a.type === "checkbox") {
      const wrap = el("div", { class: "form-check form-switch" });
      const inp = el("input", Object.assign({ class: "form-check-input", role: "switch", id }, a));
      wrap.appendChild(inp);
      wrap.appendChild(el("label", { class: "form-check-label", for: id }, label));
      return { wrap, inp };
    }
    const wrap = el("div", {});
    wrap.appendChild(el("label", { for: id }, label));
    const inp = el("input", Object.assign({ class: "form-control form-control-sm", id }, a));
    wrap.appendChild(inp);
    return { wrap, inp };
  };
  const sel = (label, opts, currentVal) => {
    const id = fieldId("sel");
    const wrap = el("div", {});
    wrap.appendChild(el("label", { for: id }, label));
    const s = el("select", { class: "form-select form-select-sm", id });
    for (const o of opts) s.appendChild(el("option", { value: o }, o));
    s.value = currentVal || opts[0];
    wrap.appendChild(s);
    return { wrap, inp: s };
  };

  const idF = field("Id", { value: e.id || "", placeholder: "stable-slug", required: true });
  if (!isNew) idF.inp.disabled = true;
  const nameF = field("Display name", { value: e.displayName || "", placeholder: "Local LM Studio" });
  const provF = sel("Provider", ["OpenAI", "Anthropic", "Ollama", "ClaudeCli", "CodexCli", "CopilotCli"], e.provider || "OpenAI");
  const urlF = field("Base URL", { value: e.baseUrl || "", placeholder: "(provider default; ignored for CLI providers)" });
  const keyF = field("API key (leave blank to keep existing)", { type: "password", value: "", placeholder: e.hasApiKey ? "(masked: " + e.apiKeyMasked + ")" : "(ignored for CLI providers — uses local CLI auth)" });
  const modelF = field("Default model", { value: e.defaultModel || "", placeholder: "e.g. claude-opus-4-7 (CLI: passed via --model)" });
  const toF = field("Timeout (s)", { type: "number", value: e.requestTimeoutSeconds ?? 600 });
  const enF = field("Enabled", { type: "checkbox" });
  enF.inp.checked = e.enabled !== false;
  const ctxF = field("Max context tokens (0 = global default)", { type: "number", value: e.maxContextTokens ?? 0 });
  const outF = field("Max output tokens (0 = provider default)", { type: "number", value: e.maxOutputTokens ?? 0 });

  // CLI-flavour fields. Stored on the endpoint regardless of provider so a tab-flip in the
  // form doesn't lose them, but visually shown / hidden based on current Provider selection
  // so non-CLI endpoints don't surface irrelevant knobs.
  const claudePermModeF = sel("Claude permission mode", ["", "default", "acceptEdits", "plan", "bypassPermissions"], e.claudePermissionMode || "");
  const claudeAllowedToolsF = field("Claude allowed tools (comma- or space-separated, e.g. Bash(git:*) Edit Read)", { value: (e.claudeAllowedTools || []).join(" ") });
  const claudeSkipF = field("Claude --dangerously-skip-permissions", { type: "checkbox" });
  claudeSkipF.inp.checked = !!e.claudeDangerouslySkipPermissions;
  const codexSandboxF = sel("Codex sandbox", ["", "read-only", "workspace-write", "danger-full-access"], e.codexSandbox || "");
  const codexApprovalF = sel("Codex ask-for-approval", ["", "untrusted", "on-failure", "on-request", "never"], e.codexAskForApproval || "");
  const copilotAllowAllToolsF = field("Copilot --allow-all-tools", { type: "checkbox" });
  copilotAllowAllToolsF.inp.checked = !!e.copilotAllowAllTools;
  const copilotAllowAllPathsF = field("Copilot --allow-all-paths", { type: "checkbox" });
  copilotAllowAllPathsF.inp.checked = !!e.copilotAllowAllPaths;
  const copilotAllowAllUrlsF = field("Copilot --allow-all-urls", { type: "checkbox" });
  copilotAllowAllUrlsF.inp.checked = !!e.copilotAllowAllUrls;
  const copilotNoAskUserF = field("Copilot --no-ask-user", { type: "checkbox" });
  copilotNoAskUserF.inp.checked = !!e.copilotNoAskUser;
  const copilotAutopilotF = field("Copilot --autopilot", { type: "checkbox" });
  copilotAutopilotF.inp.checked = !!e.copilotAutopilot;
  const copilotMaxContF = field("Copilot --max-autopilot-continues (0 = leave unset)", { type: "number", value: e.copilotMaxAutopilotContinues ?? 0 });
  const copilotAllowedToolsF = field("Copilot --allow-tool patterns (space/comma-separated)", { value: (e.copilotAllowedTools || []).join(" ") });
  const copilotDeniedToolsF = field("Copilot --deny-tool patterns (space/comma-separated)", { value: (e.copilotDeniedTools || []).join(" ") });

  const claudeBlock = el("div", { class: "endpoint-cli-block" },
    el("div", { class: "endpoint-cli-heading text-muted small" }, "Claude CLI permission flags"),
    claudePermModeF.wrap,
    claudeAllowedToolsF.wrap,
    claudeSkipF.wrap);
  const codexBlock = el("div", { class: "endpoint-cli-block" },
    el("div", { class: "endpoint-cli-heading text-muted small" }, "Codex CLI permission flags"),
    codexSandboxF.wrap,
    codexApprovalF.wrap);
  const copilotBlock = el("div", { class: "endpoint-cli-block" },
    el("div", { class: "endpoint-cli-heading text-muted small" }, "GitHub Copilot CLI permission flags"),
    copilotAllowAllToolsF.wrap,
    copilotAllowAllPathsF.wrap,
    copilotAllowAllUrlsF.wrap,
    copilotNoAskUserF.wrap,
    copilotAutopilotF.wrap,
    copilotMaxContF.wrap,
    copilotAllowedToolsF.wrap,
    copilotDeniedToolsF.wrap);

  const syncProviderVisibility = () => {
    const p = (provF.inp.value || "").toLowerCase();
    claudeBlock.style.display = p === "claudecli" ? "" : "none";
    codexBlock.style.display = p === "codexcli" ? "" : "none";
    copilotBlock.style.display = p === "copilotcli" ? "" : "none";
  };
  provF.inp.addEventListener("change", syncProviderVisibility);

  form.appendChild(el("div", { class: "row-2" }, idF.wrap, provF.wrap));
  form.appendChild(nameF.wrap);
  form.appendChild(urlF.wrap);
  form.appendChild(keyF.wrap);
  form.appendChild(el("div", { class: "row-2" }, modelF.wrap, toF.wrap));
  form.appendChild(el("div", { class: "row-2" }, ctxF.wrap, outF.wrap));
  form.appendChild(enF.wrap);
  form.appendChild(claudeBlock);
  form.appendChild(codexBlock);
  form.appendChild(copilotBlock);
  syncProviderVisibility();

  const saveBtn = el("button", { type: "submit", class: "btn btn-sm btn-primary" }, isNew ? "Create" : "Save");
  const cancelBtn = el("button", { type: "button", class: "btn btn-sm btn-outline-secondary" }, "Cancel");
  form.appendChild(el("div", { class: "endpoint-actions" }, saveBtn, cancelBtn));
  cancelBtn.addEventListener("click", () => {
    if (isNew) form.remove();
    else form.style.display = "none";
  });
  form.addEventListener("submit", async (ev) => {
    ev.preventDefault();
    const splitList = (raw) => raw.trim() ? raw.trim().split(/[\s,]+/).filter(Boolean) : [];
    const body = {
      id: idF.inp.value.trim(),
      displayName: nameF.inp.value,
      provider: provF.inp.value,
      baseUrl: urlF.inp.value,
      defaultModel: modelF.inp.value,
      requestTimeoutSeconds: Number(toF.inp.value) || 600,
      enabled: enF.inp.checked,
      maxContextTokens: Number(ctxF.inp.value) || 0,
      maxOutputTokens: Number(outF.inp.value) || 0,
      claudePermissionMode: claudePermModeF.inp.value,
      claudeAllowedTools: splitList(claudeAllowedToolsF.inp.value),
      claudeDangerouslySkipPermissions: claudeSkipF.inp.checked,
      codexSandbox: codexSandboxF.inp.value,
      codexAskForApproval: codexApprovalF.inp.value,
      copilotAllowAllTools: copilotAllowAllToolsF.inp.checked,
      copilotAllowAllPaths: copilotAllowAllPathsF.inp.checked,
      copilotAllowAllUrls: copilotAllowAllUrlsF.inp.checked,
      copilotNoAskUser: copilotNoAskUserF.inp.checked,
      copilotAutopilot: copilotAutopilotF.inp.checked,
      copilotMaxAutopilotContinues: Number(copilotMaxContF.inp.value) || 0,
      copilotAllowedTools: splitList(copilotAllowedToolsF.inp.value),
      copilotDeniedTools: splitList(copilotDeniedToolsF.inp.value),
    };
    if (keyF.inp.value !== "") body.apiKey = keyF.inp.value;
    try {
      await api("/endpoints", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
      });
      await loadEndpoints();
    } catch (e) { showToast("Save failed: " + e.message); }
  });
  return form;
}

async function activateEndpoint(id) {
  try {
    await api(`/endpoints/${encodeURIComponent(id)}/activate`, { method: "POST" });
    await loadEndpoints();
  } catch (e) { showToast("Activate failed: " + e.message); }
}

async function deleteEndpoint(id) {
  if (!confirm(`Delete endpoint '${id}'? This can't be undone.`)) return;
  try {
    await api(`/endpoints/${encodeURIComponent(id)}`, { method: "DELETE" });
    await loadEndpoints();
  } catch (e) { showToast("Delete failed: " + e.message); }
}

function addEndpointForm() {
  const c = els.endpointsList;
  // Strip empty-state placeholder if present.
  if (c.querySelector("p")) c.replaceChildren();
  const draft = { id: "", displayName: "", provider: "OpenAI", baseUrl: "", defaultModel: "", requestTimeoutSeconds: 600, enabled: true };
  const wrapper = el("div", { class: "endpoint-card" });
  wrapper.appendChild(renderEndpointForm(draft, true));
  c.prepend(wrapper);
}

// ───────────────────────────── MCP server CRUD ─────────────────────────────

async function loadMcpConfig() {
  try {
    // Two parallel sources: connection statuses (runtime) and config (editable).
    const [statuses, configs] = await Promise.all([
      api("/mcp/servers"),
      api("/mcp-config"),
    ]);
    state.mcpServers = statuses;
    state.mcpConfigs = configs;
    renderMcp();
  } catch (e) { console.warn("MCP load failed", e); }
}

async function loadMcp() {
  try {
    state.mcpServers = await api("/mcp/servers");
    renderMcp();
  } catch (e) { console.warn("MCP load failed", e); }
}

function renderMcp() {
  const c = els.mcpList;
  c.replaceChildren();
  // Source of truth: the config list. Status info from /mcp/servers overlays where present.
  const configs = state.mcpConfigs || [];
  const statusByName = {};
  for (const s of (state.mcpServers || [])) statusByName[s.name] = s;

  if (configs.length === 0) {
    c.appendChild(el("p", { class: "text-muted small" },
      "No MCP servers configured yet. Click ", el("strong", {}, "+ Add server"), " to wire one up."));
    return;
  }

  for (const cfg of configs) {
    const status = statusByName[cfg.name];
    c.appendChild(renderMcpCard(cfg, status));
  }
}

function renderMcpCard(cfg, status) {
  const card = el("div", { class: "mcp-card" });
  const head = el("div", {},
    el("span", { class: "mcp-name" }, cfg.name),
    " ",
    el("span", { class: `status-badge ${status?.status || "unknown"}` }, status?.status || (cfg.enabled ? "pending" : "disabled")));
  card.appendChild(head);
  const transport = cfg.url ? `http: ${cfg.url}` : (cfg.command ? `stdio: ${cfg.command}` : "(unconfigured)");
  const tools = status?.toolCount ?? 0;
  card.appendChild(el("div", { class: "mcp-meta" }, `${transport} · ${tools} tool${tools === 1 ? "" : "s"}` + (cfg.passthroughToCli ? " · passthrough" : "")));
  if (status?.detail) card.appendChild(el("div", { class: "mcp-meta text-danger" }, status.detail));
  if (status?.tools && status.tools.length) {
    const ul = el("ul", { class: "mcp-tools" });
    for (const t of status.tools) {
      ul.appendChild(el("li", {},
        t.name,
        t.description ? el("small", {}, t.description) : null));
    }
    card.appendChild(el("details", {}, el("summary", { class: "small" }, "tools"), ul));
  }

  const actions = el("div", { class: "mcp-actions" });
  const editBtn = el("button", { class: "btn btn-sm btn-outline-secondary" }, "Edit");
  actions.appendChild(editBtn);
  actions.appendChild(el("button", {
    class: "btn btn-sm btn-outline-danger",
    onclick: () => deleteMcpServer(cfg.name),
  }, "Delete"));
  card.appendChild(actions);

  const form = renderMcpForm(cfg);
  form.style.display = "none";
  card.appendChild(form);
  editBtn.addEventListener("click", () => {
    form.style.display = form.style.display === "none" ? "flex" : "none";
  });
  return card;
}

function renderMcpForm(cfg, isNew = false) {
  const form = el("form", { class: "mcp-form endpoint-form" });
  const field = (label, attrs) => {
    const a = attrs || {};
    const id = fieldId("fld");
    // Checkboxes render as toggle switches — the generic .form-control class stretches a
    // checkbox into an oversized rectangle, so swap to .form-check + .form-switch and put
    // the label after the control (which is the switch idiom).
    if (a.type === "checkbox") {
      const wrap = el("div", { class: "form-check form-switch" });
      const inp = el("input", Object.assign({ class: "form-check-input", role: "switch", id }, a));
      wrap.appendChild(inp);
      wrap.appendChild(el("label", { class: "form-check-label", for: id }, label));
      return { wrap, inp };
    }
    const wrap = el("div", {});
    wrap.appendChild(el("label", { for: id }, label));
    const inp = el("input", Object.assign({ class: "form-control form-control-sm", id }, a));
    wrap.appendChild(inp);
    return { wrap, inp };
  };

  const nameF = field("Name", { value: cfg.name || "", placeholder: "github", required: true });
  if (!isNew) nameF.inp.disabled = true;
  const urlF = field("Url (HTTP transport)", { value: cfg.url || "", placeholder: "https://example.com/mcp/" });
  const authF = field("Auth header (blank to keep)", { type: "password", value: "", placeholder: cfg.hasAuthHeader ? "(masked: " + cfg.authHeaderMasked + ")" : "Bearer …" });
  const cmdF = field("Command (stdio transport)", { value: cfg.command || "", placeholder: "npx" });
  const argsF = field("Arguments (space-separated)", { value: (cfg.arguments || []).join(" ") });
  const cwdF = field("Working directory (stdio)", { value: cfg.workingDirectory || "" });

  const envId = fieldId("env");
  const envWrap = el("div", {});
  envWrap.appendChild(el("label", { for: envId }, "Env vars (KEY=VALUE per line)"));
  const envArea = el("textarea", { class: "form-control form-control-sm", rows: 3, id: envId });
  envArea.value = Object.entries(cfg.environmentVariables || {}).map(([k, v]) => `${k}=${v}`).join("\n");
  envWrap.appendChild(envArea);

  const enabledF = field("Enabled", { type: "checkbox" });
  enabledF.inp.checked = cfg.enabled !== false;
  const passthroughF = field("Passthrough to CLI agents", { type: "checkbox" });
  passthroughF.inp.checked = !!cfg.passthroughToCli;

  form.appendChild(nameF.wrap);
  form.appendChild(urlF.wrap);
  form.appendChild(authF.wrap);
  form.appendChild(cmdF.wrap);
  form.appendChild(argsF.wrap);
  form.appendChild(cwdF.wrap);
  form.appendChild(envWrap);
  form.appendChild(el("div", { class: "row-2" }, enabledF.wrap, passthroughF.wrap));

  const saveBtn = el("button", { type: "submit", class: "btn btn-sm btn-primary" }, isNew ? "Create" : "Save");
  const cancelBtn = el("button", { type: "button", class: "btn btn-sm btn-outline-secondary" }, "Cancel");
  form.appendChild(el("div", { class: "endpoint-actions" }, saveBtn, cancelBtn));
  cancelBtn.addEventListener("click", () => {
    if (isNew) form.parentElement?.remove();
    else form.style.display = "none";
  });
  form.addEventListener("submit", async (ev) => {
    ev.preventDefault();
    const env = {};
    for (const line of envArea.value.split("\n")) {
      const trimmed = line.trim();
      if (!trimmed) continue;
      const eq = trimmed.indexOf("=");
      if (eq < 0) continue;
      env[trimmed.slice(0, eq).trim()] = trimmed.slice(eq + 1);
    }
    const body = {
      name: nameF.inp.value.trim(),
      url: urlF.inp.value,
      command: cmdF.inp.value,
      arguments: argsF.inp.value.trim() ? argsF.inp.value.trim().split(/\s+/) : [],
      workingDirectory: cwdF.inp.value,
      environmentVariables: env,
      enabled: enabledF.inp.checked,
      passthroughToCli: passthroughF.inp.checked,
    };
    if (authF.inp.value !== "") body.authHeader = authF.inp.value;
    try {
      await api("/mcp-config", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
      });
      // After config CRUD we also need to reload connections to pick up the change.
      try { await api("/mcp/reload", { method: "POST" }); } catch { /* best effort */ }
      await loadMcpConfig();
    } catch (e) { showToast("Save failed: " + e.message); }
  });
  return form;
}

async function deleteMcpServer(name) {
  if (!confirm(`Delete MCP server '${name}'?`)) return;
  try {
    await api(`/mcp-config/${encodeURIComponent(name)}`, { method: "DELETE" });
    try { await api("/mcp/reload", { method: "POST" }); } catch { /* best effort */ }
    await loadMcpConfig();
  } catch (e) { showToast("Delete failed: " + e.message); }
}

function addMcpForm() {
  const c = els.mcpList;
  if (c.querySelector("p")) c.replaceChildren();
  const draft = { name: "", enabled: true };
  const wrapper = el("div", { class: "mcp-card" });
  const form = renderMcpForm(draft, true);
  wrapper.appendChild(form);
  c.prepend(wrapper);
}

// ───────────────────────────── triggers (ticket polling) ─────────────────────────────

async function loadTriggers() {
  try {
    state.triggers = await api("/triggers");
    renderTriggerOptions();
    renderTriggerSources();
  } catch (e) { console.warn("triggers load failed", e); }
}

function renderTriggerOptions() {
  const form = els.triggersOptionsForm;
  form.replaceChildren();
  const t = state.triggers || {};

  const field = (label, attrs) => {
    const a = attrs || {};
    const id = fieldId("fld");
    // Checkboxes render as toggle switches — the generic .form-control class stretches a
    // checkbox into an oversized rectangle, so swap to .form-check + .form-switch and put
    // the label after the control (which is the switch idiom).
    if (a.type === "checkbox") {
      const wrap = el("div", { class: "form-check form-switch" });
      const inp = el("input", Object.assign({ class: "form-check-input", role: "switch", id }, a));
      wrap.appendChild(inp);
      wrap.appendChild(el("label", { class: "form-check-label", for: id }, label));
      return { wrap, inp };
    }
    const wrap = el("div", {});
    wrap.appendChild(el("label", { for: id }, label));
    const inp = el("input", Object.assign({ class: "form-control form-control-sm", id }, a));
    wrap.appendChild(inp);
    return { wrap, inp };
  };

  const enabledF = field("Enabled", { type: "checkbox" });
  enabledF.inp.checked = !!t.enabled;
  const intervalF = field("Poll interval (seconds)", { type: "number", value: t.pollIntervalSeconds ?? 120, min: 5 });
  const phraseF = field("Mention phrase", { value: t.phrase ?? "@dagger" });
  const maxF = field("Max jobs per cycle", { type: "number", value: t.maxJobsPerCycle ?? 5, min: 1 });
  const maxConcurrentF = field("Max concurrent jobs", { type: "number", value: t.maxConcurrentJobs ?? 3, min: 1 });
  const autoResumeF = field("Max auto-resume attempts (0 = leave orphaned jobs paused)", { type: "number", value: t.maxAutoResumeAttempts ?? 3, min: 0 });
  const authorsF = field("Allowed authors (comma-separated; blank = anyone)", {
    value: (t.allowedAuthors || []).join(", "),
    placeholder: "Wixely, dagger-bot",
  });

  const preId = fieldId("pre");
  const preWrap = el("div", {});
  preWrap.appendChild(el("label", { for: preId }, "Job preamble (system context for triggered jobs)"));
  const preamble = el("textarea", { class: "form-control form-control-sm", rows: 3, id: preId });
  preamble.value = t.jobPreamble ?? "";
  preWrap.appendChild(preamble);

  form.appendChild(el("div", { class: "row-2" }, enabledF.wrap, intervalF.wrap));
  form.appendChild(phraseF.wrap);
  form.appendChild(el("div", { class: "row-2" }, maxF.wrap, maxConcurrentF.wrap));
  form.appendChild(autoResumeF.wrap);
  form.appendChild(authorsF.wrap);
  form.appendChild(preWrap);

  const saveBtn = el("button", { type: "submit", class: "btn btn-sm btn-primary" }, "Save options");
  form.appendChild(el("div", { class: "endpoint-actions" }, saveBtn));
  form.addEventListener("submit", async (ev) => {
    ev.preventDefault();
    const body = {
      enabled: enabledF.inp.checked,
      pollIntervalSeconds: Number(intervalF.inp.value) || 120,
      phrase: phraseF.inp.value,
      maxJobsPerCycle: Number(maxF.inp.value) || 5,
      maxConcurrentJobs: Number(maxConcurrentF.inp.value) || 3,
      jobPreamble: preamble.value,
      maxAutoResumeAttempts: Number(autoResumeF.inp.value) || 0,
      allowedAuthors: authorsF.inp.value.split(",").map(s => s.trim()).filter(Boolean),
    };
    try {
      await api("/triggers", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
      });
      await loadTriggers();
    } catch (e) { showToast("Save failed: " + e.message); }
  });
}

function renderTriggerSources() {
  const c = els.triggersSourcesList;
  c.replaceChildren();
  const sources = (state.triggers && state.triggers.sources) || [];
  if (sources.length === 0) {
    c.appendChild(el("p", { class: "text-muted small" },
      "No trigger sources yet. Click ", el("strong", {}, "+ Add source"),
      " to wire up GitHub / GitLab / Azure DevOps polling. Sources need a matching MCP server under the MCP tab."));
    return;
  }
  for (const s of sources) c.appendChild(renderTriggerCard(s));
}

function renderTriggerCard(s) {
  const card = el("div", { class: "endpoint-card" });
  card.appendChild(el("div", { class: "endpoint-head" },
    el("span", { class: "endpoint-name" }, s.id),
    el("span", { class: "endpoint-provider" }, `${s.kind} · ${s.mode}`)));
  const meta = [];
  if (s.scope) meta.push(s.scope);
  if (s.filter) meta.push(`filter: ${s.filter}`);
  if (s.mcpServer) meta.push(`mcp: ${s.mcpServer}`);
  if (s.endpointId) meta.push(`endpoint: ${s.endpointId}`);
  if (s.model) meta.push(`model: ${s.model}`);
  card.appendChild(el("div", { class: "endpoint-meta" }, meta.join(" · ") || "(no details)"));

  const actions = el("div", { class: "endpoint-actions" });
  const runBtn = el("button", { class: "btn btn-sm btn-outline-primary" }, "Run now");
  const editBtn = el("button", { class: "btn btn-sm btn-outline-secondary" }, "Edit");
  actions.appendChild(runBtn);
  actions.appendChild(editBtn);
  actions.appendChild(el("button", {
    class: "btn btn-sm btn-outline-danger",
    onclick: () => deleteTriggerSource(s.id),
  }, "Delete"));
  card.appendChild(actions);

  // Inline status line for "Run now" outcomes — lives under the actions row so it doesn't
  // disrupt the card layout, and gets replaced on each subsequent run.
  const runStatus = el("div", { class: "endpoint-meta", style: "margin-top: 4px;" });
  card.appendChild(runStatus);

  runBtn.addEventListener("click", () => runTriggerSourceNow(s.id, runBtn, runStatus));

  const form = renderTriggerSourceForm(s);
  form.style.display = "none";
  card.appendChild(form);
  editBtn.addEventListener("click", () => {
    form.style.display = form.style.display === "none" ? "flex" : "none";
  });
  return card;
}

async function runTriggerSourceNow(id, btn, statusEl) {
  btn.disabled = true;
  const originalText = btn.textContent;
  btn.textContent = "Running…";
  statusEl.textContent = "";
  statusEl.classList.remove("text-danger", "text-success");
  try {
    const r = await api(`/triggers/sources/${encodeURIComponent(id)}/run`, { method: "POST" });
    if (r.ok) {
      statusEl.textContent = r.spawned > 0
        ? `Spawned ${r.spawned} job${r.spawned === 1 ? "" : "s"}.`
        : "No new matches.";
      statusEl.classList.add("text-success");
      refreshJobs();
    } else {
      statusEl.textContent = `Failed: ${r.error || "unknown error"}`;
      statusEl.classList.add("text-danger");
    }
  } catch (e) {
    statusEl.textContent = `Failed: ${e.message}`;
    statusEl.classList.add("text-danger");
  } finally {
    btn.disabled = false;
    btn.textContent = originalText;
  }
}

function renderTriggerSourceForm(s, isNew = false) {
  const form = el("form", { class: "endpoint-form" });
  const field = (label, attrs) => {
    const a = attrs || {};
    const id = fieldId("fld");
    // Checkboxes render as toggle switches — the generic .form-control class stretches a
    // checkbox into an oversized rectangle, so swap to .form-check + .form-switch and put
    // the label after the control (which is the switch idiom).
    if (a.type === "checkbox") {
      const wrap = el("div", { class: "form-check form-switch" });
      const inp = el("input", Object.assign({ class: "form-check-input", role: "switch", id }, a));
      wrap.appendChild(inp);
      wrap.appendChild(el("label", { class: "form-check-label", for: id }, label));
      return { wrap, inp };
    }
    const wrap = el("div", {});
    wrap.appendChild(el("label", { for: id }, label));
    const inp = el("input", Object.assign({ class: "form-control form-control-sm", id }, a));
    wrap.appendChild(inp);
    return { wrap, inp };
  };
  const sel = (label, opts, currentVal) => {
    const id = fieldId("sel");
    const wrap = el("div", {});
    wrap.appendChild(el("label", { for: id }, label));
    const node = el("select", { class: "form-select form-select-sm", id });
    for (const o of opts) {
      const optAttrs = typeof o === "string" ? { value: o } : { value: o.value };
      const optLabel = typeof o === "string" ? o : o.label;
      node.appendChild(el("option", optAttrs, optLabel));
    }
    if (currentVal != null && Array.from(node.options).some(o => o.value === currentVal)) node.value = currentVal;
    wrap.appendChild(node);
    return { wrap, inp: node };
  };

  const mcpNames = (state.triggers && state.triggers.mcpServerNames) || [];
  const endpointOpts = [{ value: "", label: "(use active default)" }];
  if (state.endpoints && state.endpoints.items) {
    for (const e of state.endpoints.items) {
      if (!e.enabled) continue;
      endpointOpts.push({ value: e.id, label: e.displayName ? `${e.displayName} (${e.id})` : e.id });
    }
  }

  const idF = field("Id", { value: s.id || "", placeholder: "gh-triage", required: true });
  if (!isNew) idF.inp.disabled = true;
  const kindF = sel("Kind", ["GitHub", "GitLab", "AzureDevOps"], s.kind || "GitHub");
  const modeF = sel("Mode", ["Mentions", "Label", "Assignee", "AllNew"], s.mode || "Mentions");
  const filterF = field("Filter (phrase / label / assignee — empty for AllNew)", { value: s.filter || "", placeholder: "@dagger | needs-triage | dagger-bot" });
  const mcpServerF = sel("MCP server", mcpNames.length ? mcpNames : [""], s.mcpServer || "");
  const scopeF = field("Scope", { value: s.scope || "", placeholder: "owner/repo · group/project · ProjectName" });
  const endpointF = sel("Endpoint override", endpointOpts, s.endpointId || "");
  const modelF = field("Model override", { value: s.model || "", placeholder: "(endpoint default)" });

  form.appendChild(el("div", { class: "row-2" }, idF.wrap, kindF.wrap));
  form.appendChild(el("div", { class: "row-2" }, modeF.wrap, filterF.wrap));
  form.appendChild(el("div", { class: "row-2" }, mcpServerF.wrap, scopeF.wrap));
  form.appendChild(el("div", { class: "row-2" }, endpointF.wrap, modelF.wrap));

  const saveBtn = el("button", { type: "submit", class: "btn btn-sm btn-primary" }, isNew ? "Create" : "Save");
  const cancelBtn = el("button", { type: "button", class: "btn btn-sm btn-outline-secondary" }, "Cancel");
  form.appendChild(el("div", { class: "endpoint-actions" }, saveBtn, cancelBtn));
  cancelBtn.addEventListener("click", () => {
    if (isNew) form.parentElement?.remove();
    else form.style.display = "none";
  });
  form.addEventListener("submit", async (ev) => {
    ev.preventDefault();
    const body = {
      id: idF.inp.value.trim(),
      kind: kindF.inp.value,
      mode: modeF.inp.value,
      filter: filterF.inp.value,
      mcpServer: mcpServerF.inp.value,
      scope: scopeF.inp.value,
      endpointId: endpointF.inp.value,
      model: modelF.inp.value,
    };
    try {
      await api("/triggers/sources", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
      });
      await loadTriggers();
    } catch (e) { showToast("Save failed: " + e.message); }
  });
  return form;
}

async function deleteTriggerSource(id) {
  if (!confirm(`Delete trigger source '${id}'?`)) return;
  try {
    await api(`/triggers/sources/${encodeURIComponent(id)}`, { method: "DELETE" });
    await loadTriggers();
  } catch (e) { showToast("Delete failed: " + e.message); }
}

function addTriggerSourceForm() {
  const c = els.triggersSourcesList;
  if (c.querySelector("p")) c.replaceChildren();
  const draft = { id: "", kind: "GitHub", mode: "Mentions", filter: "", mcpServer: "", scope: "", endpointId: "", model: "" };
  const wrapper = el("div", { class: "endpoint-card" });
  wrapper.appendChild(renderTriggerSourceForm(draft, true));
  c.prepend(wrapper);
}

// ───────────────────────────── banter (room-server connection) ─────────────────────────────
//
// Manages the service-hosted Banter connection: live status with connect/disconnect, the
// enrolment box (paste a one-time code, get an identity and a key fingerprint back), and the
// config section that persists into runtime-config.json. `dagger banter` remains the
// standalone equivalent; this tab is the same thing without a second process.

async function loadBanter() {
  try {
    state.banter = await api("/banter");
    // The endpoint picker needs the endpoints list; fetched here rather than relying on the
    // Endp tab having been opened first.
    try { state.banterEndpoints = (await api("/endpoints")).items || []; }
    catch { state.banterEndpoints = []; }
    renderBanterStatus();
    renderBanterEnrol();
    renderBanterConfig();
  } catch (e) { console.warn("banter load failed", e); }
}

function renderBanterStatus() {
  const c = els.banterStatus;
  c.replaceChildren();
  const st = (state.banter && state.banter.status) || {};
  const cfg = (state.banter && state.banter.config) || {};
  const connected = st.state === "connected";

  const card = el("div", { class: "endpoint-card" });
  const badge = el("span", {
    class: `badge ${connected ? "bg-success" : st.state === "connecting" ? "bg-warning" : "bg-secondary"}`,
  }, st.state || "disconnected");
  card.appendChild(el("div", { class: "endpoint-head" },
    el("span", { class: "endpoint-name" }, "Banter connection"),
    badge));

  const meta = [];
  if (connected) {
    meta.push(`${st.user} on ${st.server}`);
    meta.push(`rooms: ${(st.rooms || []).join(", ")}`);
    if (st.model) meta.push(`model: ${st.model}`);
    meta.push(st.authMode === "key" ? `key ${st.keyFingerprint}` : "password login");
  } else {
    meta.push(`would connect as ${cfg.user} to ${cfg.server}`);
    if (cfg.keyPresent) meta.push(cfg.keyFingerprint ? `key ${cfg.keyFingerprint}` : "key file present but unusable");
    else if (cfg.hasPassword) meta.push("password login (consider enrolling)");
    else meta.push("no credential yet — enrol below");
  }
  card.appendChild(el("div", { class: "endpoint-meta" }, meta.join(" · ")));

  if (st.lastError) {
    card.appendChild(el("div", { class: "endpoint-meta text-danger" }, `last error: ${st.lastError}`));
  }

  const actions = el("div", { class: "endpoint-actions" });
  const mainBtn = el("button", {
    class: `btn btn-sm ${connected ? "btn-outline-danger" : "btn-primary"}`,
    type: "button",
  }, connected ? "Disconnect" : "Connect");
  mainBtn.addEventListener("click", async () => {
    mainBtn.disabled = true;
    try {
      const r = await api(`/banter/${connected ? "disconnect" : "connect"}`, { method: "POST" });
      if (!connected && !r.ok) showToast("Connect failed: " + (r.error || "unknown error"));
    } catch (e) { showToast((connected ? "Disconnect" : "Connect") + " failed: " + e.message); }
    await loadBanter();
  });
  actions.appendChild(mainBtn);
  actions.appendChild(el("button", {
    class: "btn btn-sm btn-outline-secondary", type: "button",
    onclick: () => loadBanter(),
  }, "↻ Refresh"));
  card.appendChild(actions);
  c.appendChild(card);
}

function renderBanterEnrol() {
  const c = els.banterEnrol;
  c.replaceChildren();
  const cfg = (state.banter && state.banter.config) || {};

  const card = el("div", { class: "endpoint-card" });
  card.appendChild(el("div", { class: "endpoint-head" },
    el("span", { class: "endpoint-name" }, "Enrol this machine")));

  if (cfg.keyPresent) {
    card.appendChild(el("div", { class: "endpoint-meta" },
      `A key already exists at ${cfg.keyFile}` +
      (cfg.keyFingerprint ? ` (${cfg.keyFingerprint})` : "") +
      ". Enrolling again needs that file moved aside first — overwriting it would strand the identity it belongs to."));
    c.appendChild(card);
    return;
  }

  card.appendChild(el("div", { class: "endpoint-meta" },
    "Paste the one-time code from the Banter client's agents page. This machine makes its own key, " +
    "sends only the public half, and keeps the private half at the configured key path."));

  const codeId = fieldId("bnt");
  const wrap = el("div", { class: "endpoint-form", style: "display:flex;" });
  const codeWrap = el("div", {});
  codeWrap.appendChild(el("label", { for: codeId }, "Enrolment code"));
  const codeInput = el("input", {
    class: "form-control form-control-sm", id: codeId,
    placeholder: "banter-enrol-…", autocomplete: "off", spellcheck: "false",
  });
  codeWrap.appendChild(codeInput);
  wrap.appendChild(codeWrap);

  const enrolBtn = el("button", { class: "btn btn-sm btn-primary", type: "button" }, "Enrol");
  const result = el("div", { class: "endpoint-meta" });
  enrolBtn.addEventListener("click", async () => {
    const code = codeInput.value.trim();
    if (!code) { showToast("Paste the enrolment code first."); return; }
    enrolBtn.disabled = true;
    enrolBtn.textContent = "Enrolling…";
    try {
      const r = await api("/banter/enrol", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ code }),
      });
      result.classList.remove("text-danger");
      result.textContent =
        `Enrolled as '${r.nick}' — fingerprint ${r.fingerprint}. The code is spent; ` +
        `the key stays on this machine${r.dpapiProtected ? " (DPAPI-protected)" : ""}.`;
      showToast(`Enrolled as ${r.nick}`);
      await loadBanter();
    } catch (e) {
      result.classList.add("text-danger");
      result.textContent = "Enrolment failed: " + e.message;
      enrolBtn.disabled = false;
      enrolBtn.textContent = "Enrol";
    }
  });

  wrap.appendChild(el("div", { class: "endpoint-actions" }, enrolBtn));
  card.appendChild(wrap);
  card.appendChild(result);
  c.appendChild(card);
}

function renderBanterConfig() {
  const form = els.banterConfigForm;
  form.replaceChildren();
  const cfg = (state.banter && state.banter.config) || {};
  const connected = state.banter && state.banter.status && state.banter.status.state === "connected";

  const field = (label, attrs) => {
    const a = attrs || {};
    const id = fieldId("bnf");
    if (a.type === "checkbox") {
      const wrap = el("div", { class: "form-check form-switch" });
      const inp = el("input", Object.assign({ class: "form-check-input", role: "switch", id }, a));
      wrap.appendChild(inp);
      wrap.appendChild(el("label", { class: "form-check-label", for: id }, label));
      return { wrap, inp };
    }
    const wrap = el("div", {});
    wrap.appendChild(el("label", { for: id }, label));
    const inp = el("input", Object.assign({ class: "form-control form-control-sm", id }, a));
    wrap.appendChild(inp);
    return { wrap, inp };
  };
  const selectField = (label, options, value) => {
    const id = fieldId("bnf");
    const wrap = el("div", {});
    wrap.appendChild(el("label", { for: id }, label));
    const inp = el("select", { class: "form-select form-select-sm", id });
    for (const o of options) {
      const opt = el("option", { value: o }, o);
      if (o === value) opt.selected = true;
      inp.appendChild(opt);
    }
    wrap.appendChild(inp);
    return { wrap, inp };
  };

  const serverF = field("Server", { value: cfg.server ?? "tcp://127.0.0.1:7770" });
  const userF = field("User", { value: cfg.user ?? "dagger" });
  const roomsF = field("Rooms to join (comma-separated)", { value: (cfg.rooms || []).join(", "), placeholder: "#main" });
  const keyFileF = field("Key file path", { value: cfg.keyFile ?? "banter.key" });
  const passF = field(cfg.hasPassword ? "Password (set — leave blank to keep)" : "Password (blank = key login)", {
    type: "password", autocomplete: "new-password", placeholder: cfg.hasPassword ? "••••••" : "",
  });
  const endpointId = fieldId("bnf");
  const endpointWrap = el("div", {});
  endpointWrap.appendChild(el("label", { for: endpointId }, "LLM endpoint"));
  const endpointSel = el("select", { class: "form-select form-select-sm", id: endpointId });
  endpointSel.appendChild(el("option", { value: "" }, "(use active default)"));
  for (const ep of state.banterEndpoints || []) {
    const opt = el("option", { value: ep.id }, `${ep.displayName || ep.id} (${ep.id})`);
    if (ep.id === cfg.endpointId) opt.selected = true;
    endpointSel.appendChild(opt);
  }
  endpointWrap.appendChild(endpointSel);
  const modelF = field("Model (blank = endpoint default)", { value: cfg.model ?? "" });
  const skillsF = field("Skills (comma-separated)", { value: (cfg.skills || []).join(", "), placeholder: "code, tools" });
  const localityF = selectField("Locality", ["local", "frontier"], cfg.locality ?? "local");
  const clearanceF = selectField("Clearance", ["public", "internal", "sensitive"], cfg.clearance ?? "sensitive");
  const costF = field("Cost tier (lower is cheaper)", { type: "number", value: cfg.costTier ?? 1, min: 0 });
  const descF = field("Roster description (blank = auto)", { value: cfg.description ?? "" });
  const answerAllF = field("Answer every message (mention-mode rooms only)", { type: "checkbox" });
  answerAllF.inp.checked = !!cfg.respondToEveryMessage;
  const delegatorF = field("Ask to be the room's delegator", { type: "checkbox" });
  delegatorF.inp.checked = !!cfg.wantsDelegator;
  const autoConnectF = field("Connect automatically when the service starts", { type: "checkbox" });
  autoConnectF.inp.checked = !!cfg.autoConnect;

  const promptId = fieldId("bnf");
  const promptWrap = el("div", {});
  promptWrap.appendChild(el("label", { for: promptId }, "System prompt (blank = agent prompt + group-chat addendum)"));
  const promptArea = el("textarea", { class: "form-control form-control-sm", rows: 3, id: promptId });
  promptArea.value = cfg.systemPrompt ?? "";
  promptWrap.appendChild(promptArea);

  // Two groups, split by who decides. Connection & answering is entirely this side's
  // business; the announced attributes are REQUESTS the server weighs against the admin's
  // agent settings, delegator election, dispatch mode and room rules.
  form.appendChild(el("div", { class: "endpoint-head" },
    el("span", { class: "endpoint-name" }, "Connection & answering")));
  form.appendChild(el("p", { class: "text-muted small" },
    "How DaggerAgent reaches the server and produces replies. Applied on the next connect."));
  form.appendChild(el("div", { class: "row-2" }, serverF.wrap, userF.wrap));
  form.appendChild(keyFileF.wrap);
  form.appendChild(passF.wrap);
  form.appendChild(el("div", { class: "row-2" }, endpointWrap, modelF.wrap));
  form.appendChild(promptWrap);
  form.appendChild(autoConnectF.wrap);

  form.appendChild(el("div", { class: "endpoint-head", style: "margin-top: 10px;" },
    el("span", { class: "endpoint-name" }, "Requested settings")));
  form.appendChild(el("p", { class: "text-muted small" },
    "Announced to the Banter server on connect — requests, not guarantees. The server has the " +
    "final say: an admin's agent settings, delegator election, the room's dispatch mode, " +
    "clearance rules and rate guardrails can override or refuse any of them. The agents page " +
    "in the Banter client shows what actually applies."));
  form.appendChild(roomsF.wrap);
  form.appendChild(skillsF.wrap);
  form.appendChild(el("div", { class: "row-2" }, localityF.wrap, clearanceF.wrap));
  form.appendChild(el("div", { class: "row-2" }, costF.wrap, descF.wrap));
  form.appendChild(answerAllF.wrap);
  form.appendChild(delegatorF.wrap);

  const saveBtn = el("button", { type: "submit", class: "btn btn-sm btn-primary" }, "Save");
  form.appendChild(el("div", { class: "endpoint-actions" }, saveBtn));
  form.addEventListener("submit", async (ev) => {
    ev.preventDefault();
    const body = {
      server: serverF.inp.value.trim(),
      user: userF.inp.value.trim(),
      rooms: roomsF.inp.value.split(",").map(s => s.trim()).filter(Boolean),
      keyFile: keyFileF.inp.value.trim(),
      endpointId: endpointSel.value,
      model: modelF.inp.value,
      skills: skillsF.inp.value.split(",").map(s => s.trim()).filter(Boolean),
      locality: localityF.inp.value,
      clearance: clearanceF.inp.value,
      costTier: Number(costF.inp.value) || 1,
      description: descF.inp.value,
      respondToEveryMessage: answerAllF.inp.checked,
      wantsDelegator: delegatorF.inp.checked,
      autoConnect: autoConnectF.inp.checked,
      systemPrompt: promptArea.value,
    };
    // A blank password box means "leave it alone" — typing sets it, and enrolment clears it.
    if (passF.inp.value.length > 0) body.password = passF.inp.value;
    try {
      await api("/banter/config", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
      });
      showToast(connected
        ? "Saved. Disconnect and reconnect to apply to the live connection."
        : "Saved.");
      await loadBanter();
    } catch (e) { showToast("Save failed: " + e.message); }
  });
}

async function loadTools() {
  try {
    state.tools = await api("/tools");
    renderTools();
  } catch (e) { console.warn("tools load failed", e); }
}

function renderTools() {
  const c = els.toolsList;
  c.replaceChildren();
  if (!state.tools || !state.tools.length) {
    c.appendChild(el("p", { class: "text-muted small" }, "No tools registered."));
    return;
  }
  const groups = {};
  for (const t of state.tools) (groups[t.category] ||= []).push(t);
  for (const cat of Object.keys(groups).sort()) {
    const group = el("div", { class: "tools-group" });
    group.appendChild(el("h6", {}, cat));
    for (const t of groups[cat]) {
      const cls = t.enabled ? "tool" : "tool disabled";
      const node = el("div", { class: cls, title: t.disabledReason || "" },
        t.name,
        t.description ? el("small", {}, t.description) : null);
      group.appendChild(node);
    }
    c.appendChild(group);
  }
}

async function loadCommands() {
  try {
    const list = await api("/commands");
    const c = els.commandsList;
    c.replaceChildren();
    for (const cmd of list) {
      c.appendChild(el("li", { onclick: () => insertCommand(cmd) },
        el("span", { class: "cmd-name" }, cmd.command),
        el("span", { class: "cmd-desc" }, cmd.description),
        cmd.usageHint ? el("span", { class: "cmd-usage" }, cmd.usageHint) : null));
    }
  } catch (e) { console.warn("commands load failed", e); }
}

function insertCommand(cmd) {
  const t = els.promptBox;
  t.value = cmd.usageHint || cmd.command + " ";
  t.focus();
  t.setSelectionRange(t.value.length, t.value.length);
}

async function loadSettings() {
  try {
    state.settings = await api("/settings");
    syncSettingsToToggles();
    renderSettingsForm();
  } catch (e) { console.warn("settings load failed", e); }
}

function syncSettingsToToggles() {
  if (!state.settings) return;
  els.tPlan.checked = state.settings.forcePlan;
  els.tPreview.checked = state.settings.writePreview;
  els.tShell.checked = state.settings.allowShell;
  els.tReadonly.checked = state.settings.readOnly;
  updateComposerContext();   // settings arriving moves these without an event
}

async function patchSettings(patch) {
  try {
    state.settings = await api("/settings", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(patch),
    });
    syncSettingsToToggles();
    renderSettingsForm();
    loadTools();
  } catch (e) {
    console.warn("settings update failed", e);
    flashStatus("settings error", "error");
  }
}

function renderSettingsForm() {
  const f = els.settingsForm;
  f.replaceChildren();
  if (!state.settings) return;
  const s = state.settings;

  const boolField = (id, label, key, hint) => {
    const row = el("div", { class: "form-check form-switch" });
    const inp = el("input", { type: "checkbox", class: "form-check-input", id });
    inp.checked = !!s[key];
    inp.addEventListener("change", () => patchSettings({ [keyMap[key] || key]: inp.checked }));
    row.appendChild(inp);
    row.appendChild(el("label", { class: "form-check-label", for: id }, label,
      hint ? el("small", {}, hint) : null));
    return row;
  };
  const textField = (id, label, key, hint) => {
    const row = el("div", {});
    row.appendChild(el("label", { class: "form-label", for: id }, label));
    const inp = el("input", { type: "text", class: "form-control form-control-sm", id, value: s[key] || "" });
    inp.addEventListener("change", () => patchSettings({ [keyMap[key] || key]: inp.value }));
    row.appendChild(inp);
    if (hint) row.appendChild(el("small", { class: "text-muted" }, hint));
    return row;
  };
  const numField = (id, label, key, hint) => {
    const row = el("div", {});
    row.appendChild(el("label", { class: "form-label", for: id }, label));
    const inp = el("input", { type: "number", class: "form-control form-control-sm", id, value: s[key] });
    inp.addEventListener("change", () => patchSettings({ [keyMap[key] || key]: Number(inp.value) }));
    row.appendChild(inp);
    if (hint) row.appendChild(el("small", { class: "text-muted" }, hint));
    return row;
  };

  // map ViewProp → PatchProp (PascalCase props on the patch DTO use the same names; .NET JSON
  // serialiser will accept either camel- or Pascal-case for input by default.)
  const keyMap = {};

  f.appendChild(textField("set-cwd", "Working directory", "workingDirectory", "Filesystem root the agent is jailed to."));
  f.appendChild(boolField("set-readonly", "Read-only", "readOnly", "Block every write/shell/memory-save tool."));
  f.appendChild(boolField("set-write", "Allow write", "allowWrite", "Opt-in to write_file / edit_file."));
  f.appendChild(boolField("set-preview", "Preview writes", "writePreview", "Stage writes and require confirmation."));
  f.appendChild(boolField("set-shell", "Allow shell", "allowShell", "Opt-in to exec_shell."));
  f.appendChild(boolField("set-anypath", "Allow any path", "allowAnyPath", "Permit reads/writes outside the working dir."));
  f.appendChild(boolField("set-plan", "Force plan mode", "forcePlan", "Make the agent plan before tool calls."));
  f.appendChild(boolField("set-granular", "Granular tools", "granularTools", "Show the full tool list every turn."));
  f.appendChild(numField("set-mfb", "Max file bytes", "maxFileBytes"));
  f.appendChild(numField("set-mr", "Max results", "maxResults", "Grep / glob cap."));
  f.appendChild(numField("set-st", "Shell timeout (s)", "shellTimeoutSeconds"));
  f.appendChild(numField("set-rsb", "Read-file summary threshold (bytes)", "readFileSummaryThresholdBytes"));
  f.appendChild(numField("set-mtrc", "Max tool result chars", "maxToolResultChars",
    "Tool results larger than this get stashed; the agent reads slices via read_tool_result. 0 disables."));
  f.appendChild(boolField("set-cli", "Allow CLI delegation", "allowCliDelegation",
    "Expose delegate_to_claude / delegate_to_codex / delegate_to_copilot tools — agent can shell out to external CLI agents."));
  f.appendChild(textField("set-claude-path", "Claude CLI path", "claudeCliPath",
    "Absolute path to the claude executable. Leave blank to resolve via PATH."));
  f.appendChild(textField("set-codex-path", "Codex CLI path", "codexCliPath",
    "Absolute path to the codex executable. Leave blank to resolve via PATH."));
  f.appendChild(textField("set-copilot-path", "Copilot CLI path", "copilotCliPath",
    "Absolute path to the GitHub Copilot CLI executable (e.g. %ProgramFiles%\\GitHub Copilot CLI\\copilot.exe). Leave blank to resolve via PATH."));
}

async function loadPlan() {
  if (!state.currentJobId) {
    els.planDisplay.className = "plan-display empty";
    els.planDisplay.replaceChildren(el("p", { class: "text-muted small" }, "Pick a job to see its plan."));
    return;
  }
  try {
    const plan = await api(`/plan/${encodeURIComponent(state.currentJobId)}`);
    state.plan = plan;
    renderPlan();
  } catch (e) { console.warn("plan load failed", e); }
}

function renderPlan() {
  const p = els.planDisplay;
  p.replaceChildren();
  const plan = state.plan;
  if (!plan || !plan.steps || !plan.steps.length) {
    p.className = "plan-display empty";
    p.appendChild(el("p", { class: "text-muted small" },
      "No plan yet for this job. Plans show up when the agent calls ", el("code", {}, "make_plan"), "."));
    return;
  }
  p.className = "plan-display";
  for (const step of plan.steps) {
    const mark = step.status === "done" ? "[x]" : step.status === "in_progress" ? "[~]" : step.status === "blocked" ? "[!]" : "[ ]";
    const node = el("div", { class: `plan-step ${step.status}` },
      el("span", { class: "step-mark" }, mark),
      el("span", {},
        step.description,
        step.note ? el("span", { class: "step-note" }, step.note) : null));
    p.appendChild(node);
  }
}

async function loadPendingWrites() {
  try {
    const rows = await api("/pending-writes");
    renderPendingWrites(rows);
  } catch (e) { console.warn("pending-writes load failed", e); }
}

function renderPendingWrites(rows) {
  const c = els.writesList;
  c.replaceChildren();
  if (!rows || !rows.length) {
    c.className = "writes-list empty";
    c.appendChild(el("p", { class: "text-muted small" },
      "No staged writes. Enable ", el("em", {}, "Preview writes"), " to require approval before files change."));
    return;
  }
  c.className = "writes-list";
  for (const r of rows) {
    const card = el("div", { class: "write-card" });
    const head = el("div", { class: "write-head" },
      el("span", { class: "w-path" }, r.displayPath),
      el("span", { class: "w-size" }, `${r.oldLength}→${r.newLength}`));
    const diff = el("pre", { class: "write-diff" });
    diff.innerHTML = renderDiffHtml(r.unifiedDiff);
    diff.style.display = "none";
    head.addEventListener("click", () => {
      diff.style.display = diff.style.display === "none" ? "block" : "none";
    });
    const actions = el("div", { class: "write-actions" },
      el("button", {
        class: "btn btn-sm btn-success",
        onclick: () => approveWrite(r.absolutePath),
      }, "Approve"),
      el("button", {
        class: "btn btn-sm btn-outline-secondary",
        onclick: () => discardWrite(r.absolutePath),
      }, "Discard"));
    card.appendChild(head);
    card.appendChild(diff);
    card.appendChild(actions);
    c.appendChild(card);
  }
}

function renderDiffHtml(text) {
  return text
    .split("\n")
    .map((line) => {
      if (line.startsWith("+")) return `<span class="d-add">${escapeHtml(line)}</span>`;
      if (line.startsWith("-")) return `<span class="d-del">${escapeHtml(line)}</span>`;
      return escapeHtml(line);
    })
    .join("\n");
}

async function approveWrite(absPath) {
  try {
    await api("/pending-writes/confirm", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ path: absPath }),
    });
    loadPendingWrites();
  } catch (e) { console.warn("approve failed", e); }
}

// ───────────────────────────────────────────────────────────
// folder browser (server-side fs)
// ───────────────────────────────────────────────────────────

let _folderCurrent = "";

async function openFolderBrowser() {
  const initial = els.workingDir.value.trim();
  await loadFolder(initial);
  els.folderDialog.showModal();
  return new Promise((resolve) => {
    const onCancel = () => { cleanup(); resolve(null); };
    const onSelect = () => { cleanup(); resolve(_folderCurrent); };
    function cleanup() {
      els.folderDialog.close();
      els.folderCancel.removeEventListener("click", onCancel);
      els.folderSelect.removeEventListener("click", onSelect);
    }
    els.folderCancel.addEventListener("click", onCancel);
    els.folderSelect.addEventListener("click", onSelect);
  });
}

async function loadFolder(path) {
  let view;
  try {
    view = await api(`/browse?path=${encodeURIComponent(path || "")}`);
  } catch (e) {
    els.folderPath.textContent = `(error: ${e.message})`;
    els.folderEntries.replaceChildren(el("li", { class: "empty" }, "Couldn't list this folder."));
    return;
  }
  _folderCurrent = view.path || "";
  els.folderPath.textContent = _folderCurrent || "(filesystem root)";
  const list = els.folderEntries;
  list.replaceChildren();
  if (view.parent !== null && view.parent !== undefined) {
    list.appendChild(el("li", {
      class: "parent",
      onclick: () => loadFolder(view.parent),
    }, el("span", { class: "ico" }, "↰"), el("span", {}, ".. (parent)")));
  } else if (view.path !== "" && !view.path?.match(/^[a-zA-Z]:\\?$/)) {
    // On non-Windows, the parent of "/" is null. Offer a "roots" entry only on Windows when we want drives.
    // On Linux/macOS the user just stays at "/".
  } else if (view.path?.match(/^[a-zA-Z]:\\?$/)) {
    // Windows drive root — offer "go to drives list".
    list.appendChild(el("li", { class: "parent", onclick: () => loadFolder("") },
      el("span", { class: "ico" }, "↰"), el("span", {}, ".. (drives)")));
  }
  if (!view.entries || view.entries.length === 0) {
    list.appendChild(el("li", { class: "empty" }, view.error ? `(${view.error})` : "(no subdirectories)"));
    return;
  }
  for (const e of view.entries) {
    list.appendChild(el("li", {
      onclick: () => loadFolder(e.path),
    }, el("span", { class: "ico" }, "📁"), el("span", {}, e.name)));
  }
}

async function discardWrite(absPath) {
  try {
    await api("/pending-writes/discard", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ path: absPath }),
    });
    loadPendingWrites();
  } catch (e) { console.warn("discard failed", e); }
}

// ───────────────────────────────────────────────────────────
// 6. composer
// ───────────────────────────────────────────────────────────


function renderImageStrip() {
  const s = els.imageStrip;
  s.replaceChildren();
  if (!state.pendingImages.length) { s.classList.add("is-empty"); return; }
  s.classList.remove("is-empty");
  state.pendingImages.forEach((img, idx) => {
    const thumb = el("div", { class: "img-thumb" },
      el("img", { src: img.dataUrl, alt: "" }),
      el("button", { class: "rm", onclick: () => {
        state.pendingImages.splice(idx, 1);
        renderImageStrip();
      } }, "×"));
    s.appendChild(thumb);
  });
}

function renderQueue() {
  const c = els.queueList;
  c.replaceChildren();
  if (!state.queue.length) { els.queueStrip.classList.add("is-empty"); return; }
  els.queueStrip.classList.remove("is-empty");
  state.queue.forEach((q, idx) => {
    c.appendChild(el("li", {},
      el("span", { class: "q-text" }, q.prompt),
      el("button", { class: "q-btn", title: "Move up",
        onclick: () => { if (idx > 0) { [state.queue[idx - 1], state.queue[idx]] = [state.queue[idx], state.queue[idx - 1]]; renderQueue(); } } }, "↑"),
      el("button", { class: "q-btn", title: "Move down",
        onclick: () => { if (idx < state.queue.length - 1) { [state.queue[idx + 1], state.queue[idx]] = [state.queue[idx], state.queue[idx + 1]]; renderQueue(); } } }, "↓"),
      el("button", { class: "q-btn", title: "Remove",
        onclick: () => { state.queue.splice(idx, 1); renderQueue(); } }, "×")
    ));
  });
}



function flashStatus(text, cls) {
  els.statusPill.textContent = text;
  els.statusPill.className = `badge bg-secondary ${cls || ""}`;
}

// Everything the shared turn engine needs to know about this shell: which controls mean
// "busy", how a status reads, and what a request body looks like here.
const session = createSession({
  api,
  streamPost,
  state,
  transcript,
  host: {
    onTurnStart() {
      els.btnSend.classList.add("d-none");
      els.btnCancel.classList.remove("d-none");
      flashStatus("streaming", "streaming");
    },
    onTurnEnd() {
      els.btnSend.classList.remove("d-none");
      els.btnCancel.classList.add("d-none");
      // Only fall back to "paused" if nothing else has claimed the pill since.
      if (els.statusPill.classList.contains("streaming")) flashStatus("paused", "paused");
      refreshJobs();
      if (els.panes.writes.classList.contains("active")) loadPendingWrites();
    },
    onTurnError: () => flashStatus("error", "error"),
    onJobId: (id) => { els.jobIdLabel.textContent = id; },
    onSseStatus: (data) => flashStatus(
      data.cancelled ? "cancelled" : (data.status || "paused").toLowerCase(),
      data.cancelled ? "error" : "paused"),
    onPlanUpdate: () => { if (els.panes.plan.classList.contains("active")) loadPlan(); },
    formatError: (message) => `\n[error: ${message}]`,
    buildBody: (prompt, images) => ({
      prompt,
      model: els.modelInput.value.trim() || null,
      workingDirectory: els.workingDir.value.trim() || null,
      images: (images || []).map(({ mediaType, base64 }) => ({ mediaType, base64 })),
      system: null,
      endpointId: els.endpointSelect?.value || null,
    }),
    onQueueChange: () => renderQueue(),
    onImagesChange: () => renderImageStrip(),
    contextInputs: { workingDir: els.workingDir, endpoint: els.endpointSelect },
  },
});

const { runTurn, handleSseEvent, enqueue, drainQueue, addImage, loadJob } = session;


async function onSendClick(forceQueue) {
  const prompt = els.promptBox.value.trim();
  if (!prompt) return;
  const images = state.pendingImages.slice();
  els.promptBox.value = "";
  state.pendingImages = [];
  renderImageStrip();
  autoGrowPrompt();

  // Slash commands are handled locally — they used to be sent verbatim to the LLM, so
  // typing /mcpreload at a claude-cli endpoint would spawn claude.exe and forward the
  // string (causing the MCP server to log "claude connecting" instead of doing the reload).
  if (prompt.startsWith("/") && await tryHandleSlashCommand(prompt)) return;

  // Queueing is implicit: any submit while a turn is streaming joins the queue (matches CLI).
  // Ctrl+Enter from idle still enqueues without starting, useful for batching up several prompts before launching.
  if (forceQueue || state.streaming) {
    enqueue(prompt, images);
    if (!state.streaming) drainQueue();
    return;
  }
  await runTurn(prompt, images);
}

async function tryHandleSlashCommand(raw) {
  const [cmd, ...rest] = raw.split(/\s+/);
  const arg = rest.join(" ").trim();
  const echo = (text) => {
    appendUserMessage(raw, []);
    const msg = el("div", { class: "msg assistant" }, el("em", {}, text));
    withScrollStick(() => els.transcript.appendChild(msg));
  };
  switch (cmd.toLowerCase()) {
    case "/new":
      newJob();
      return true;
    case "/jobs":
      refreshJobs();
      echo("Refreshed jobs list — see the left sidebar.");
      return true;
    case "/help":
      try {
        const list = await api("/commands");
        const lines = list.map(c => `${c.command} — ${c.description}`).join("\n");
        echo(lines || "(no commands registered)");
      } catch (e) { echo(`Failed to load commands: ${e.message}`); }
      return true;
    case "/mcpreload":
      try {
        await api("/mcp/reload", { method: "POST" });
        await loadMcpConfig();
        loadTools();
        echo("MCP servers reloaded.");
      } catch (e) { echo(`MCP reload failed: ${e.message}`); }
      return true;
    case "/resume":
      if (!arg) { echo("Usage: /resume <jobId>"); return true; }
      await selectJob(arg);
      return true;
    case "/compress":
      // No server endpoint for compression yet — surface that rather than silently sending
      // the literal "/compress" string to the LLM.
      echo("Compression is not wired to a server endpoint yet — start a fresh job with /new instead.");
      return true;
    default:
      return false; // unknown slash command falls through to be sent to the LLM
  }
}

function onCancelClick() {
  session.cancelTurn();
}

function autoGrowPrompt() {
  const t = els.promptBox;
  t.style.height = "auto";
  t.style.height = Math.min(t.scrollHeight, 240) + "px";
}

// ───────────────────────────────────────────────────────────
// 7. job-list sidebar
// ───────────────────────────────────────────────────────────

async function refreshJobs() {
  try {
    state.jobs = await api("/jobs");
    renderJobsList();
  } catch (e) { console.warn("jobs load failed", e); }
}

function renderJobsList() {
  const c = els.jobsList;
  closeContextMenu();            // drop any menu anchored to a row we're about to replace
  c.replaceChildren();
  const filter = state.jobFilter.toLowerCase();
  for (const j of state.jobs) {
    if (filter && !j.jobId.toLowerCase().includes(filter) && !(j.model || "").toLowerCase().includes(filter)) continue;
    const statusBits = [j.status, new Date(j.updatedAt).toLocaleString()];
    if (j.interrupted) statusBits.unshift("interrupted");
    if (j.triggerSourceId) statusBits.push(`src: ${j.triggerSourceId}`);
    const li = el("li", {
      class: "job-item"
        + (j.jobId === state.currentJobId ? " active" : "")
        + (j.interrupted ? " interrupted" : ""),
      onclick: () => selectJob(j.jobId),
      oncontextmenu: (ev) => showContextMenu(ev, [
        { label: "Open", action: () => selectJob(j.jobId) },
        ...(j.interrupted ? [{ label: "Resume", action: () => resumeJob(j.jobId) }] : []),
        { sep: true },
        { label: "Delete", danger: true, action: () => deleteJob(j.jobId) },
      ], li),
    },
      el("span", {}, j.model || "?"),
      el("span", { class: "job-id" }, j.jobId.slice(0, 12)),
      el("span", { class: "job-status text-muted" }, statusBits.join(" · ")));

    // Resume button — surfaced for any interrupted job, regardless of whether a human or
    // the trigger sweep created it. Click stops propagation so it doesn't also fire selectJob.
    if (j.interrupted) {
      const resumeBtn = el("button", {
        class: "btn btn-sm btn-outline-warning",
        style: "margin-left: 4px; padding: 0 6px; font-size: 0.7rem;",
        title: "Resume this interrupted job",
        onclick: (ev) => { ev.stopPropagation(); resumeJob(j.jobId); },
      }, "Resume");
      li.appendChild(resumeBtn);
    }
    c.appendChild(li);
  }
}

// ── right-click context menu (job actions) ───────────────────────────────────
// One reusable menu, rebuilt per open, positioned at the cursor and clamped to the
// viewport. `items` is [{label, action, danger?} | {sep:true}]. Dismissed by an outside
// mousedown, Escape, window blur/resize, or opening another menu.
let _ctxMenu = null, _ctxAnchor = null;
function closeContextMenu() {
  if (_ctxMenu) { _ctxMenu.remove(); _ctxMenu = null; }
  if (_ctxAnchor) { _ctxAnchor.classList.remove("context-target"); _ctxAnchor = null; }
}
function showContextMenu(ev, items, anchorEl) {
  ev.preventDefault();
  closeContextMenu();
  const menu = el("div", { class: "context-menu" });
  for (const it of items) {
    if (it.sep) { menu.appendChild(el("div", { class: "cm-sep" })); continue; }
    menu.appendChild(el("button", {
      class: "cm-item" + (it.danger ? " danger" : ""),
      type: "button",
      onclick: () => { closeContextMenu(); it.action(); },
    }, it.label));
  }
  menu.style.visibility = "hidden";              // measure off-screen, then clamp on-screen
  document.body.appendChild(menu);
  const x = Math.min(ev.clientX, window.innerWidth - menu.offsetWidth - 6);
  const y = Math.min(ev.clientY, window.innerHeight - menu.offsetHeight - 6);
  menu.style.left = Math.max(6, x) + "px";
  menu.style.top = Math.max(6, y) + "px";
  menu.style.visibility = "";
  _ctxMenu = menu;
  if (anchorEl) { anchorEl.classList.add("context-target"); _ctxAnchor = anchorEl; }
}
// mousedown fires for both buttons and before contextmenu/click, so it closes the current
// menu before another right-click reopens one — and leaves clicks inside the menu alone.
document.addEventListener("mousedown", (e) => { if (_ctxMenu && !_ctxMenu.contains(e.target)) closeContextMenu(); });
document.addEventListener("keydown", (e) => { if (e.key === "Escape") closeContextMenu(); });
window.addEventListener("blur", closeContextMenu);
window.addEventListener("resize", closeContextMenu);

async function deleteJob(jobId) {
  const short = jobId.slice(0, 12);
  if (!confirm(`Delete job ${short}? This permanently removes the job and its history and can't be undone.`)) return;
  try {
    await api(`/jobs/${encodeURIComponent(jobId)}`, { method: "DELETE" });
    if (state.currentJobId === jobId) newJob();   // clear the transcript + selection if the open job went away
    refreshJobs();
  } catch (e) { showToast("Delete failed: " + e.message); }
}

async function resumeJob(jobId) {
  await selectJob(jobId);
  // Use the same streaming pipeline a normal message takes — the recovery prompt is server-side
  // canonical text that nudges the model to pick up from in-progress steps.
  const recoveryPrompt =
    "The previous turn was cut short because the DaggerAgent service stopped mid-execution. " +
    "Pick up from where you left off based on the conversation above — re-check any plan steps " +
    "that were in_progress and resume the task. Don't repeat tool calls you already completed.";
  await runTurn(recoveryPrompt, []);
  refreshJobs();
}

async function selectJob(jobId) {
  state.currentJobId = jobId;
  els.jobIdLabel.textContent = jobId;
  if (els.btnRefreshJob) els.btnRefreshJob.style.display = "";
  flashStatus("idle");
  // loadJob renders the history and restores the endpoint + cwd this job was using, so
  // the next turn stays on the same provider and directory.
  try {
    await loadJob(jobId);
  } catch (e) { console.warn("job load failed", e); clearTranscript(); }
  updateComposerContext();   // loadJob restores cwd + endpoint without firing events
  renderJobsList();
  if (els.panes.plan.classList.contains("active")) loadPlan();
}

async function refreshCurrentJob() {
  // Manual reload from disk — pairs with the background trigger / auto-resume turns whose
  // updates don't stream over the open SSE channel. Re-uses selectJob so endpoint/cwd
  // restore behaviour stays consistent.
  if (!state.currentJobId) return;
  await selectJob(state.currentJobId);
  refreshJobs();
}

function newJob() {
  state.currentJobId = null;
  els.jobIdLabel.textContent = "";
  if (els.btnRefreshJob) els.btnRefreshJob.style.display = "none";
  flashStatus("idle");
  clearTranscript();
  renderJobsList();
  els.promptBox.focus();
}

// ───────────────────────────────────────────────────────────
// 8. SSE event dispatch
// ───────────────────────────────────────────────────────────


// ───────────────────────────────────────────────────────────
// 9. boot
// ───────────────────────────────────────────────────────────

function wireEvents() {
  // tab switch
  for (const btn of els.tabs) {
    btn.addEventListener("click", () => switchTab(btn.dataset.tab));
  }
  els.btnAdvancedTabs.addEventListener("click", toggleAdvancedTabs);
  els.composerContext.addEventListener("click", toggleComposerContext);
  // Keep the summary honest: it is the only view of these while collapsed.
  for (const n of [els.workingDir, els.modelInput]) n.addEventListener("input", updateComposerContext);
  for (const n of [els.endpointSelect, els.tPlan, els.tPreview, els.tShell, els.tReadonly]) {
    n.addEventListener("change", updateComposerContext);
  }
  // Arrow keys move between tabs, as a tablist is expected to. The strip is a 4x2 grid,
  // so Up/Down step a whole row; Left/Right step one and wrap.
  const tabs = Array.from(els.tabs);
  for (const btn of tabs) {
    btn.addEventListener("keydown", (ev) => {
      const step = { ArrowRight: 1, ArrowLeft: -1, ArrowDown: 4, ArrowUp: -4 }[ev.key];
      const jump = ev.key === "Home" ? 0 : ev.key === "End" ? tabs.length - 1 : null;
      if (step === undefined && jump === null) return;
      ev.preventDefault();
      const i = jump !== null ? jump
        : (tabs.indexOf(btn) + step + tabs.length) % tabs.length;
      switchTab(tabs[i].dataset.tab);
      tabs[i].focus();
    });
  }

  // theme + right-pane toggles
  els.btnTheme.addEventListener("click", () => {
    const html = document.documentElement;
    const next = html.getAttribute("data-theme") === "dark" ? "light" : "dark";
    html.setAttribute("data-theme", next);
    localStorage.setItem("daggerTheme", next);
  });
  const savedTheme = localStorage.getItem("daggerTheme");
  if (savedTheme) document.documentElement.setAttribute("data-theme", savedTheme);

  // Side panes are drawers on phones, in-grid columns on desktop.
  // Same button → different behaviour depending on viewport.
  const mobileQuery = window.matchMedia("(max-width: 780px)");
  function isMobile() { return mobileQuery.matches; }
  function syncBackdrop() {
    const open = els.leftPane.classList.contains("open") || els.rightPane.classList.contains("open");
    els.drawerBackdrop.classList.toggle("open", open);
  }
  function closeDrawers() {
    els.leftPane.classList.remove("open");
    els.rightPane.classList.remove("open");
    syncBackdrop();
    syncRightExpanded();
  }
  // The button's aria-expanded has to track whichever mechanism is in play — the drawer
  // class on mobile, the grid-collapse class on desktop.
  function syncRightExpanded() {
    const open = isMobile()
      ? els.rightPane.classList.contains("open")
      : !els.appMain.classList.contains("collapsed-right");
    els.btnRightToggle.setAttribute("aria-expanded", String(open));
  }
  els.btnRightToggle.addEventListener("click", () => {
    if (isMobile()) {
      // One button dismisses whichever sheet is up. It used to swap the jobs sheet for
      // this one, which left the jobs sheet with no obvious way to close: the button you
      // reach for is the menu button, and it opened something else instead.
      if (els.leftPane.classList.contains("open") || els.rightPane.classList.contains("open")) {
        closeDrawers();
        return;
      }
      els.rightPane.classList.add("open");
      syncBackdrop();
    } else {
      els.appMain.classList.toggle("collapsed-right");
    }
    syncRightExpanded();
  });
  els.btnLeftToggle.addEventListener("click", () => {
    els.rightPane.classList.remove("open");
    els.leftPane.classList.toggle("open");
    syncBackdrop();
    syncRightExpanded();
  });
  els.drawerBackdrop.addEventListener("click", closeDrawers);
  // A sheet is modal, so Escape should dismiss it - that is what mobile gets free from
  // <dialog>. Only acts when one is actually open, so it does not swallow the key.
  document.addEventListener("keydown", (ev) => {
    if (ev.key !== "Escape") return;
    if (!els.leftPane.classList.contains("open") && !els.rightPane.classList.contains("open")) return;
    closeDrawers();
  });
  // Collapse drawers automatically when the user picks something from inside them on mobile.
  els.jobsList.addEventListener("click", () => { if (isMobile()) closeDrawers(); });
  els.btnNewJob.addEventListener("click", () => { if (isMobile()) closeDrawers(); });
  els.commandsList.addEventListener("click", () => { if (isMobile()) closeDrawers(); });
  // Resizing past the breakpoint shouldn't leave drawer state stuck.
  mobileQuery.addEventListener("change", closeDrawers);
  syncRightExpanded();

  // toggles
  els.tPlan.addEventListener("change", () => patchSettings({ forcePlan: els.tPlan.checked }));
  els.tPreview.addEventListener("change", () => patchSettings({ writePreview: els.tPreview.checked, allowWrite: els.tPreview.checked || (state.settings?.allowWrite ?? false) }));
  els.tShell.addEventListener("change", () => patchSettings({ allowShell: els.tShell.checked }));
  els.tReadonly.addEventListener("change", () => patchSettings({ readOnly: els.tReadonly.checked }));

  // composer
  els.btnSend.addEventListener("click", () => onSendClick(false));
  els.btnCancel.addEventListener("click", onCancelClick);
  els.btnImage.addEventListener("click", () => els.imageInput.click());
  els.imageInput.addEventListener("change", () => {
    for (const f of els.imageInput.files) addImage(f);
    els.imageInput.value = "";
  });
  els.promptBox.addEventListener("input", autoGrowPrompt);
  els.promptBox.addEventListener("keydown", (e) => {
    if (e.key === "Enter" && !e.shiftKey && !e.ctrlKey && !e.metaKey) {
      e.preventDefault();
      onSendClick(false);
    } else if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) {
      e.preventDefault();
      onSendClick(true);
    }
  });

  // drag + paste images
  for (const ev of ["dragenter", "dragover"]) {
    els.composer.addEventListener(ev, (e) => { e.preventDefault(); els.composer.classList.add("drag-over"); });
  }
  for (const ev of ["dragleave", "drop"]) {
    els.composer.addEventListener(ev, (e) => { e.preventDefault(); els.composer.classList.remove("drag-over"); });
  }
  els.composer.addEventListener("drop", (e) => {
    e.preventDefault();
    for (const f of e.dataTransfer.files) addImage(f);
  });
  els.promptBox.addEventListener("paste", (e) => {
    for (const item of e.clipboardData.items) {
      if (item.type.startsWith("image/")) {
        const f = item.getAsFile();
        if (f) addImage(f);
      }
    }
  });

  // jobs
  els.btnNewJob.addEventListener("click", newJob);
  els.jobSearch.addEventListener("input", () => {
    state.jobFilter = els.jobSearch.value;
    renderJobsList();
  });

  // folder browser
  els.btnBrowseCwd.addEventListener("click", async () => {
    const picked = await openFolderBrowser();
    if (picked) {
      els.workingDir.value = picked;
      // Trigger same code path the user typing would — sync onto settings + datalist.
      els.workingDir.dispatchEvent(new Event("change", { bubbles: true }));
    }
  });
  els.workingDir.addEventListener("change", () => {
    const v = els.workingDir.value.trim();
    if (v) patchSettings({ workingDirectory: v });
  });

  // mcp tab actions
  els.btnMcpReload.addEventListener("click", async () => {
    els.btnMcpReload.disabled = true;
    try { await api("/mcp/reload", { method: "POST" }); }
    catch (e) { console.warn("mcp reload failed", e); }
    finally { els.btnMcpReload.disabled = false; loadMcpConfig(); loadTools(); }
  });
  els.btnMcpAdd?.addEventListener("click", () => addMcpForm());
  els.btnEndpointAdd?.addEventListener("click", () => addEndpointForm());
  els.btnTriggerAdd?.addEventListener("click", () => addTriggerSourceForm());

  if (els.btnRefreshJob) {
    els.btnRefreshJob.addEventListener("click", async () => {
      els.btnRefreshJob.disabled = true;
      try { await refreshCurrentJob(); }
      finally { els.btnRefreshJob.disabled = false; }
    });
  }

  // api key dialog
  apiKeyDialog.querySelector("form").addEventListener("submit", () => {});
}

async function boot() {
  wireEvents();
  flashStatus("idle");
  // The tab marked active in the HTML is Endpoints, which lives in the folded group, so
  // a phone-width first load would otherwise open on a tab it cannot show.
  revealAdvancedIfNeeded();
  await Promise.allSettled([
    refreshJobs(),
    loadEndpoints(),
    loadMcpConfig(),
    loadTools(),
    loadCommands(),
    loadSettings(),
    (async () => {
      try {
        const dirs = await api("/working-directories");
        els.workingDirs.replaceChildren();
        for (const d of dirs) els.workingDirs.appendChild(el("option", { value: d }));
        if (dirs.length && !els.workingDir.value) els.workingDir.value = dirs[0];
      } catch (e) { console.warn("working-dirs load failed", e); }
    })(),
  ]);
}

boot();
