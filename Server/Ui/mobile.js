// Dagger Agent mobile UI. Intentionally focused on conversations; full configuration
// stays available through the desktop UI linked from the options sheet.

import { $, el } from "./core/dom.js";
import { createApi, resolveBasePath, getApiKey, setApiKey } from "./core/api.js";
import { createTranscript } from "./core/transcript.js";

const BASE_PATH = resolveBasePath();

const els = {
  transcript: $("transcript"),
  status: $("status"),
  statusText: $("status").querySelector("span"),
  jobLabel: $("job-label"),
  currentJob: $("current-job"),
  newJob: $("new-job"),
  openJobs: $("open-jobs"),
  openSettings: $("open-settings"),
  jobsSheet: $("jobs-sheet"),
  settingsSheet: $("settings-sheet"),
  writesSheet: $("writes-sheet"),
  sheetNewJob: $("sheet-new-job"),
  jobSearch: $("job-search"),
  jobsList: $("jobs-list"),
  composer: $("composer"),
  prompt: $("prompt"),
  send: $("send"),
  stop: $("stop"),
  attach: $("attach"),
  imageInput: $("image-input"),
  imageStrip: $("image-strip"),
  queueChip: $("queue-chip"),
  contextButton: $("context-button"),
  contextLabel: $("context-label"),
  endpoint: $("endpoint"),
  model: $("model"),
  workingDir: $("working-dir"),
  workingDirs: $("working-dirs"),
  tPlan: $("toggle-plan"),
  tPreview: $("toggle-preview"),
  tShell: $("toggle-shell"),
  tReadonly: $("toggle-readonly"),
  toggleTheme: $("toggle-theme"),
  themeLabel: $("theme-label"),
  desktopLink: $("desktop-link"),
  openWrites: $("open-writes"),
  writesCount: $("writes-count"),
  writesList: $("writes-list"),
  apiKeyDialog: $("api-key-dialog"),
  apiKeyInput: $("api-key-input"),
  apiKeyReason: $("api-key-reason"),
  toast: $("toast"),
};

const state = {
  jobs: [],
  currentJobId: null,
  currentMsg: null,
  currentFooter: null,
  lastBlock: null,
  lastBlockType: null,
  toolCallNodes: {},
  streaming: false,
  abortCtrl: null,
  pendingImages: [],
  queue: [],
  settings: null,
  endpoints: null,
  lastTurn: null,
};


// Handed to createApi as its 401 handler. createApi single-flights the call, so the
// local memo this used to keep against concurrent 401s is no longer needed.
function promptForKey(reason) {
  return new Promise((resolve) => {
    els.apiKeyInput.value = getApiKey();
    els.apiKeyReason.textContent = reason || "Enter the API key configured for this Dagger server.";
    els.apiKeyDialog.showModal();
    els.apiKeyDialog.addEventListener("close", function onClose() {
      els.apiKeyDialog.removeEventListener("close", onClose);
      const saved = els.apiKeyDialog.returnValue === "save";
      if (saved) setApiKey(els.apiKeyInput.value.trim());
      resolve(saved);
    });
  });
}

const { api, streamPost } = createApi({ basePath: BASE_PATH, onUnauthorized: promptForKey });

function setStatus(label, status = "idle") {
  const friendly = status === "streaming" ? "Working" : status === "error" ? "Error" : status === "paused" ? "Paused" : label;
  els.status.dataset.state = status;
  els.statusText.textContent = friendly || "Ready";
}

function showToast(message) {
  clearTimeout(showToast.timer);
  els.toast.textContent = message;
  els.toast.hidden = false;
  showToast.timer = setTimeout(() => { els.toast.hidden = true; }, 2600);
}

function emptyState() {
  return el("section", { class: "empty-state" },
    el("div", { class: "empty-mark", "aria-hidden": "true" }, "†"),
    el("h1", {}, "What are we building?"),
    el("p", {}, "Describe the outcome. Dagger will plan, use tools, and keep you posted."),
    el("div", { class: "starter-chips", "aria-label": "Example prompts" },
      el("button", { type: "button", onclick: () => fillPrompt("Review this project and suggest the highest-impact improvement.") }, "Review this project"),
      el("button", { type: "button", onclick: () => fillPrompt("Find and fix the most important failing test.") }, "Fix a failing test")));
}

async function copyText(text) {
  try {
    await navigator.clipboard.writeText(text);
    showToast("Copied");
  } catch { showToast("Could not copy"); }
}

function formatToolArgs(args) {
  if (typeof args === "string") return args.slice(0, 100);
  try {
    return Object.entries(args || {}).slice(0, 3).map(([key, value]) => {
      const rendered = typeof value === "string" ? value : JSON.stringify(value);
      return `${key}=${String(rendered).slice(0, 36)}`;
    }).join(", ");
  } catch { return ""; }
}

// How the mobile shell wants the shared transcript to read. Shorter labels and a
// usage line that fits a 390px row; the machinery is core/transcript.js.
const transcriptView = {
  emptyState,
  historyToolBlock: (text) => el("details", { class: "thinking-block" },
    el("summary", {}, "Tool result"),
    el("pre", { class: "thinking-body" }, text.slice(0, 1600))),
  userImage: (image) => el("div", { class: "image-thumb" },
    el("img", { src: image.dataUrl, alt: "Attached image" })),
  copyButton: (getText) => el("button", { type: "button", onclick: () => copyText(getText()) }, "Copy"),
  retryButton: (onClick) => el("button", { class: "retry", type: "button", onclick: onClick }, "Retry"),
  toolName: (name) => name || "Tool",
  toolArgs: (args) => formatToolArgs(args),
  toolPending: "Running…",
  toolPendingClass: "",
  toolResult: (excerpt, length) => `${excerpt || "Complete"}${length ? ` · ${length} chars` : ""}`,
  thinkingSummary: "Thinking",
  usageStampClass: "usage-stamp",
  usageText: (u) => `${u.inputTokens || 0} in · ${u.outputTokens || 0} out · ${u.costUsd ? `$${Number(u.costUsd).toFixed(4)}` : "$0"}`,
};

const {
  withScrollStick, clearTranscript, renderHistory, appendUserMessage, beginAssistantMessage,
  pushSegment, appendAnswerChunk, appendThinkingChunk, appendToolCall, appendToolResult,
  setUsageStamp, showRetryButton,
} = createTranscript({
  mount: els.transcript,
  state,
  view: transcriptView,
  // A touch more slack than desktop: a thumb scroll overshoots more than a wheel does.
  stickThreshold: 140,
  onRetry: (turn) => runTurn(turn.prompt, turn.images),
});

function handleSseEvent(name, data) {
  switch (name) {
    case "job":
      state.currentJobId = data.jobId;
      updateJobLabel();
      break;
    case "delta": appendAnswerChunk(data.text || ""); break;
    case "thinking": appendThinkingChunk(data.text || ""); break;
    case "tool_call": appendToolCall(data.id, data.name, data.args); break;
    case "tool_result": appendToolResult(data.id, data.excerpt, data.length); break;
    case "usage": setUsageStamp(data); break;
    case "status": setStatus(data.cancelled ? "Stopped" : data.status || "Paused", data.cancelled ? "error" : "paused"); break;
    case "error":
      appendAnswerChunk(`\n\n**Error:** ${data.message || "The turn failed."}`);
      setStatus("Error", "error");
      showRetryButton();
      break;
    default: break;
  }
}

async function runTurn(prompt, images) {
  state.streaming = true;
  state.lastTurn = { prompt, images };
  state.abortCtrl = new AbortController();
  els.send.hidden = true;
  els.stop.hidden = false;
  setStatus("Working", "streaming");
  appendUserMessage(prompt, images);
  beginAssistantMessage();

  const body = {
    prompt,
    model: els.model.value.trim() || null,
    workingDirectory: els.workingDir.value.trim() || null,
    endpointId: els.endpoint.value || null,
    images: (images || []).map(({ mediaType, base64 }) => ({ mediaType, base64 })),
    system: null,
  };
  const path = state.currentJobId
    ? `/jobs/${encodeURIComponent(state.currentJobId)}/messages/stream`
    : "/jobs/stream";

  try {
    await streamPost(path, body, handleSseEvent, state.abortCtrl.signal);
  } catch (error) {
    if (error.name !== "AbortError") {
      appendAnswerChunk(`\n\n**Error:** ${error.message}`);
      setStatus("Error", "error");
      showRetryButton();
    }
  } finally {
    state.streaming = false;
    state.abortCtrl = null;
    state.currentMsg = null;
    els.send.hidden = false;
    els.stop.hidden = true;
    if (els.status.dataset.state === "streaming") setStatus("Ready", "idle");
    refreshJobs();
    refreshPendingWrites();
    if (state.queue.length) runNextQueued();
  }
}

async function submitPrompt() {
  const prompt = els.prompt.value.trim();
  if (!prompt) return;
  const images = state.pendingImages.slice();
  els.prompt.value = "";
  state.pendingImages = [];
  renderImages();
  autoGrowPrompt();
  updateSendState();

  if (prompt.startsWith("/") && await handleSlashCommand(prompt)) return;
  if (state.streaming) {
    state.queue.push({ prompt, images });
    renderQueue();
    showToast("Prompt queued");
    return;
  }
  await runTurn(prompt, images);
}

async function handleSlashCommand(raw) {
  const [command, ...rest] = raw.split(/\s+/);
  const argument = rest.join(" ").trim();
  switch (command.toLowerCase()) {
    case "/new": newJob(); return true;
    case "/jobs": await openJobsSheet(); return true;
    case "/resume":
      if (!argument) { showToast("Use /resume followed by a job id"); return true; }
      await selectJob(argument); return true;
    case "/help":
      try {
        const commands = await api("/commands");
        appendUserMessage(raw, []);
        beginAssistantMessage();
        appendAnswerChunk(commands.map((item) => `**${item.command}** — ${item.description}`).join("\n\n") || "No commands are registered.");
        state.currentMsg = null;
      } catch (error) { showToast(error.message); }
      return true;
    default: return false;
  }
}

async function runNextQueued() {
  if (state.streaming) return;
  const next = state.queue.shift();
  renderQueue();
  if (next) await runTurn(next.prompt, next.images);
}

function renderQueue() {
  els.queueChip.hidden = state.queue.length === 0;
  els.queueChip.textContent = `${state.queue.length} queued · tap to clear`;
}

function addImage(file) {
  if (!file.type.startsWith("image/")) return;
  const reader = new FileReader();
  reader.onload = () => {
    const dataUrl = String(reader.result || "");
    state.pendingImages.push({ mediaType: file.type, base64: dataUrl.split(",")[1] || dataUrl, dataUrl });
    renderImages();
    updateSendState();
  };
  reader.readAsDataURL(file);
}

function renderImages() {
  els.imageStrip.replaceChildren();
  els.imageStrip.hidden = state.pendingImages.length === 0;
  state.pendingImages.forEach((image, index) => {
    els.imageStrip.appendChild(el("div", { class: "image-thumb" },
      el("img", { src: image.dataUrl, alt: `Attachment ${index + 1}` }),
      el("button", { type: "button", "aria-label": "Remove attachment", onclick: () => {
        state.pendingImages.splice(index, 1);
        renderImages();
        updateSendState();
      } }, "×")));
  });
}

function autoGrowPrompt() {
  els.prompt.style.height = "auto";
  els.prompt.style.height = `${Math.min(els.prompt.scrollHeight, 148)}px`;
}

function updateSendState() { els.send.disabled = !els.prompt.value.trim() && !state.pendingImages.length; }
function fillPrompt(text) { els.prompt.value = text; autoGrowPrompt(); updateSendState(); els.prompt.focus(); }

function relativeTime(value) {
  const timestamp = new Date(value).getTime();
  if (!Number.isFinite(timestamp)) return "";
  const seconds = Math.round((timestamp - Date.now()) / 1000);
  const formatter = new Intl.RelativeTimeFormat(undefined, { numeric: "auto" });
  if (Math.abs(seconds) < 60) return formatter.format(seconds, "second");
  const minutes = Math.round(seconds / 60);
  if (Math.abs(minutes) < 60) return formatter.format(minutes, "minute");
  const hours = Math.round(minutes / 60);
  if (Math.abs(hours) < 24) return formatter.format(hours, "hour");
  return formatter.format(Math.round(hours / 24), "day");
}

async function refreshJobs() {
  try {
    state.jobs = await api("/jobs");
    renderJobs();
  } catch (error) { console.warn("Could not load jobs", error); }
}

function renderJobs() {
  const query = els.jobSearch.value.trim().toLowerCase();
  const jobs = state.jobs.filter((job) => typeof job?.jobId === "string" && job.jobId && (
    !query || job.jobId.toLowerCase().includes(query) || String(job.model || "").toLowerCase().includes(query)
  ));
  els.jobsList.replaceChildren();
  if (!jobs.length) {
    els.jobsList.appendChild(el("div", { class: "jobs-empty" }, query ? "No matching jobs." : "No jobs yet."));
    return;
  }
  for (const job of jobs) {
    const meta = [job.status, relativeTime(job.updatedAt), job.jobId.slice(0, 8)].filter(Boolean).join(" · ");
    const actions = el("div", { class: "job-actions" });
    if (job.interrupted) actions.appendChild(el("button", { type: "button", onclick: () => resumeJob(job.jobId) }, "Resume"));
    actions.appendChild(el("button", { class: "delete", type: "button", "aria-label": "Delete job", onclick: () => deleteJob(job.jobId) }, "Delete"));
    els.jobsList.appendChild(el("div", { class: `job-row${job.jobId === state.currentJobId ? " active" : ""}` },
      el("button", { class: "job-main", type: "button", onclick: () => selectJob(job.jobId) },
        el("span", { class: "job-title" }, job.model || "Dagger job"),
        el("span", { class: "job-meta" }, meta)),
      actions));
  }
}

async function selectJob(jobId) {
  state.currentJobId = jobId;
  updateJobLabel();
  setStatus("Ready", "idle");
  closeSheet(els.jobsSheet);
  try {
    const view = await api(`/jobs/${encodeURIComponent(jobId)}`);
    renderHistory(view.history || []);
    if (view.workingDirectory) els.workingDir.value = view.workingDirectory;
    if (view.endpointId && Array.from(els.endpoint.options).some((option) => option.value === view.endpointId)) els.endpoint.value = view.endpointId;
    updateContextLabel();
  } catch (error) {
    clearTranscript();
    showToast(`Could not open job: ${error.message}`);
  }
  renderJobs();
}

async function refreshCurrentJob() {
  if (!state.currentJobId || state.streaming) return;
  await selectJob(state.currentJobId);
  showToast("Conversation refreshed");
}

function newJob() {
  if (state.streaming && !confirm("Stop the current response and start a new job?")) return;
  state.abortCtrl?.abort();
  state.currentJobId = null;
  state.queue = [];
  renderQueue();
  updateJobLabel();
  clearTranscript();
  closeSheet(els.jobsSheet);
  setStatus("Ready", "idle");
  setTimeout(() => els.prompt.focus(), 80);
}

async function resumeJob(jobId) {
  await selectJob(jobId);
  await runTurn(
    "The previous turn was cut short because the DaggerAgent service stopped mid-execution. Pick up from where you left off. Re-check in-progress plan steps and do not repeat completed tool calls.",
    []);
}

async function deleteJob(jobId) {
  if (!confirm(`Delete job ${jobId.slice(0, 8)} and its history?`)) return;
  try {
    await api(`/jobs/${encodeURIComponent(jobId)}`, { method: "DELETE" });
    if (state.currentJobId === jobId) newJob();
    await refreshJobs();
  } catch (error) { showToast(`Delete failed: ${error.message}`); }
}

function updateJobLabel() { els.jobLabel.textContent = state.currentJobId ? state.currentJobId.slice(0, 8) : "New job"; }

async function loadEndpoints() {
  try {
    state.endpoints = await api("/endpoints");
    const previous = els.endpoint.value;
    els.endpoint.replaceChildren();
    const active = state.endpoints.items?.find((item) => item.id === state.endpoints.defaultId);
    els.endpoint.appendChild(el("option", { value: "" }, active ? `Active · ${active.displayName || active.id}` : "Active default"));
    for (const endpoint of state.endpoints.items || []) {
      els.endpoint.appendChild(el("option", { value: endpoint.id }, endpoint.displayName || endpoint.id));
    }
    if (Array.from(els.endpoint.options).some((option) => option.value === previous)) els.endpoint.value = previous;
    updateContextLabel();
  } catch (error) { console.warn("Could not load endpoints", error); }
}

async function loadSettings() {
  try {
    state.settings = await api("/settings");
    syncSettings();
  } catch (error) { console.warn("Could not load settings", error); }
}

function syncSettings() {
  if (!state.settings) return;
  els.tPlan.checked = Boolean(state.settings.forcePlan);
  els.tPreview.checked = Boolean(state.settings.writePreview);
  els.tShell.checked = Boolean(state.settings.allowShell);
  els.tReadonly.checked = Boolean(state.settings.readOnly);
  if (!els.workingDir.value && state.settings.workingDirectory) els.workingDir.value = state.settings.workingDirectory;
}

async function patchSettings(patch) {
  try {
    state.settings = await api("/settings", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(patch),
    });
    syncSettings();
  } catch (error) {
    syncSettings();
    showToast(`Could not save: ${error.message}`);
  }
}

async function refreshPendingWrites() {
  try {
    const writes = await api("/pending-writes");
    renderPendingWrites(writes || []);
  } catch (error) { console.warn("Could not load staged changes", error); }
}

function renderPendingWrites(writes) {
  els.writesCount.textContent = writes.length ? `${writes.length} waiting` : "None waiting";
  els.writesList.replaceChildren();
  if (!writes.length) {
    els.writesList.appendChild(el("div", { class: "writes-empty" }, "No staged changes. Turn on Preview writes to approve file edits before they land."));
    return;
  }
  for (const write of writes) {
    const details = el("details", {},
      el("summary", {},
        el("span", { class: "write-path" }, write.displayPath || write.absolutePath || "File change"),
        el("span", { class: "write-size" }, `${write.oldLength || 0} → ${write.newLength || 0}`)),
      el("pre", { class: "write-diff" }, write.unifiedDiff || "No diff available."));
    const actions = el("div", { class: "write-actions" },
      el("button", { class: "approve", type: "button", onclick: () => resolveWrite("confirm", write.absolutePath) }, "Approve"),
      el("button", { type: "button", onclick: () => resolveWrite("discard", write.absolutePath) }, "Discard"));
    els.writesList.appendChild(el("article", { class: "write-card" }, details, actions));
  }
}

async function resolveWrite(action, path) {
  try {
    await api(`/pending-writes/${action}`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ path }),
    });
    showToast(action === "confirm" ? "Change approved" : "Change discarded");
    await refreshPendingWrites();
  } catch (error) { showToast(`Could not update change: ${error.message}`); }
}

function updateContextLabel() {
  const selected = els.endpoint.options[els.endpoint.selectedIndex];
  const endpointName = selected?.textContent || "Default endpoint";
  els.contextLabel.textContent = els.model.value.trim() ? `${endpointName} · ${els.model.value.trim()}` : endpointName;
}

function openSheet(sheet) {
  if (!sheet.open) sheet.showModal();
}
function closeSheet(sheet) { if (sheet.open) sheet.close(); }
async function openJobsSheet() { openSheet(els.jobsSheet); await refreshJobs(); }

function applyTheme(theme) {
  const next = theme === "light" ? "light" : "dark";
  document.documentElement.dataset.theme = next;
  els.themeLabel.textContent = next === "dark" ? "Dark" : "Light";
  document.querySelector("meta[name='theme-color']")?.setAttribute("content", next === "dark" ? "#0e0e10" : "#f5f3ef");
  localStorage.setItem("daggerTheme", next);
}

function wireEvents() {
  els.openJobs.addEventListener("click", openJobsSheet);
  els.openSettings.addEventListener("click", () => openSheet(els.settingsSheet));
  els.contextButton.addEventListener("click", () => openSheet(els.settingsSheet));
  els.newJob.addEventListener("click", newJob);
  els.sheetNewJob.addEventListener("click", newJob);
  els.currentJob.addEventListener("click", refreshCurrentJob);
  for (const close of document.querySelectorAll(".sheet-close")) close.addEventListener("click", () => closeSheet(close.closest("dialog")));
  for (const sheet of [els.jobsSheet, els.settingsSheet, els.writesSheet]) {
    sheet.addEventListener("click", (event) => { if (event.target === sheet) closeSheet(sheet); });
  }

  els.jobSearch.addEventListener("input", renderJobs);
  els.prompt.addEventListener("input", () => { autoGrowPrompt(); updateSendState(); });
  els.prompt.addEventListener("keydown", (event) => {
    if (event.key === "Enter" && !event.shiftKey && !event.isComposing) {
      event.preventDefault();
      submitPrompt();
    }
  });
  els.send.addEventListener("click", submitPrompt);
  els.stop.addEventListener("click", () => state.abortCtrl?.abort());
  els.attach.addEventListener("click", () => els.imageInput.click());
  els.imageInput.addEventListener("change", () => {
    for (const file of els.imageInput.files) addImage(file);
    els.imageInput.value = "";
  });
  els.prompt.addEventListener("paste", (event) => {
    for (const item of event.clipboardData?.items || []) {
      if (item.type.startsWith("image/")) {
        const file = item.getAsFile();
        if (file) addImage(file);
      }
    }
  });
  for (const eventName of ["dragenter", "dragover"]) {
    els.composer.addEventListener(eventName, (event) => { event.preventDefault(); els.composer.classList.add("drag-over"); });
  }
  for (const eventName of ["dragleave", "drop"]) {
    els.composer.addEventListener(eventName, (event) => { event.preventDefault(); els.composer.classList.remove("drag-over"); });
  }
  els.composer.addEventListener("drop", (event) => { for (const file of event.dataTransfer?.files || []) addImage(file); });
  els.queueChip.addEventListener("click", () => { state.queue = []; renderQueue(); showToast("Queue cleared"); });

  els.endpoint.addEventListener("change", updateContextLabel);
  els.model.addEventListener("input", updateContextLabel);
  els.workingDir.addEventListener("change", () => {
    const value = els.workingDir.value.trim();
    if (value) patchSettings({ workingDirectory: value });
  });
  els.tPlan.addEventListener("change", () => patchSettings({ forcePlan: els.tPlan.checked }));
  els.tPreview.addEventListener("change", () => patchSettings({ writePreview: els.tPreview.checked, allowWrite: els.tPreview.checked || Boolean(state.settings?.allowWrite) }));
  els.tShell.addEventListener("change", () => patchSettings({ allowShell: els.tShell.checked }));
  els.tReadonly.addEventListener("change", () => patchSettings({ readOnly: els.tReadonly.checked }));
  els.toggleTheme.addEventListener("click", () => applyTheme(document.documentElement.dataset.theme === "dark" ? "light" : "dark"));
  els.desktopLink.href = `${BASE_PATH}/ui?desktop=1`;
  els.openWrites.addEventListener("click", async () => {
    closeSheet(els.settingsSheet);
    openSheet(els.writesSheet);
    await refreshPendingWrites();
  });

  document.querySelectorAll("[data-prompt]").forEach((button) => button.addEventListener("click", () => fillPrompt(button.dataset.prompt)));
}

async function boot() {
  applyTheme(localStorage.getItem("daggerTheme") || "dark");
  wireEvents();
  updateJobLabel();
  updateSendState();
  setStatus("Ready", "idle");
  await Promise.allSettled([
    refreshJobs(),
    loadEndpoints(),
    loadSettings(),
    refreshPendingWrites(),
    (async () => {
      try {
        const directories = await api("/working-directories");
        els.workingDirs.replaceChildren(...directories.map((directory) => el("option", { value: directory })));
        if (!els.workingDir.value && directories.length) els.workingDir.value = directories[0];
      } catch (error) { console.warn("Could not load working directories", error); }
    })(),
  ]);
}

boot();
