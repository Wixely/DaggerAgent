// Dagger Agent mobile UI. Intentionally focused on conversations; full configuration
// stays available through the desktop UI linked from the options sheet.

const BASE_PATH = window.location.pathname.replace(/\/(?:ui(?:\/.*)?|mobile)\/?$/, "") || "/agent";

const $ = (id) => document.getElementById(id);
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

function node(tag, props = {}, ...children) {
  const element = document.createElement(tag);
  for (const [key, value] of Object.entries(props)) {
    if (key === "class") element.className = value;
    else if (key === "text") element.textContent = value;
    else if (key.startsWith("on") && typeof value === "function") element.addEventListener(key.slice(2).toLowerCase(), value);
    else if (key === "dataset") Object.assign(element.dataset, value);
    else if (value === true) element.setAttribute(key, "");
    else if (value !== false && value != null) element.setAttribute(key, value);
  }
  for (const child of children.flat()) {
    if (child == null || child === false) continue;
    element.appendChild(typeof child === "string" ? document.createTextNode(child) : child);
  }
  return element;
}

function getApiKey() { return localStorage.getItem("daggerApiKey") || ""; }
function setApiKey(value) { localStorage.setItem("daggerApiKey", value || ""); }

function authHeaders(extra = {}) {
  const headers = { ...extra };
  const key = getApiKey();
  if (key) headers["X-Api-Key"] = key;
  return headers;
}

function promptForKey(reason) {
  return new Promise((resolve) => {
    els.apiKeyInput.value = getApiKey();
    els.apiKeyReason.textContent = reason || "Enter the API key configured for this Dagger server.";
    els.apiKeyDialog.showModal();
    els.apiKeyDialog.addEventListener("close", function onClose() {
      els.apiKeyDialog.removeEventListener("close", onClose);
      if (els.apiKeyDialog.returnValue === "save") {
        setApiKey(els.apiKeyInput.value.trim());
        resolve(true);
      } else {
        resolve(false);
      }
    });
  });
}

async function api(path, options = {}) {
  const url = `${BASE_PATH}${path.startsWith("/") ? path : `/${path}`}`;
  const response = await fetch(url, { ...options, headers: authHeaders(options.headers) });
  if (response.status === 401) {
    if (await promptForKey("That key was not accepted. Check it and try again.")) return api(path, options);
    throw new Error("Unauthorized");
  }
  if (!response.ok) {
    const body = await response.text().catch(() => "");
    throw new Error(body.slice(0, 180) || `Request failed (${response.status})`);
  }
  const contentType = response.headers.get("content-type") || "";
  return contentType.includes("application/json") ? response.json() : response.text();
}

async function streamPost(path, body, handler, signal) {
  const url = `${BASE_PATH}${path}`;
  const response = await fetch(url, {
    method: "POST",
    headers: authHeaders({ "Content-Type": "application/json", Accept: "text/event-stream" }),
    body: JSON.stringify(body),
    signal,
  });
  if (response.status === 401) {
    if (await promptForKey("That key was not accepted. Check it and try again.")) return streamPost(path, body, handler, signal);
    throw new Error("Unauthorized");
  }
  if (!response.ok || !response.body) throw new Error(`Request failed (${response.status})`);

  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let buffer = "";
  while (true) {
    const { done, value } = await reader.read();
    if (done) break;
    buffer += decoder.decode(value, { stream: true });
    buffer = buffer.replaceAll("\r\n", "\n");
    let boundary;
    while ((boundary = buffer.indexOf("\n\n")) >= 0) {
      const block = buffer.slice(0, boundary);
      buffer = buffer.slice(boundary + 2);
      let eventName = "message";
      let data = "";
      for (const line of block.split("\n")) {
        if (line.startsWith("event:")) eventName = line.slice(6).trim();
        else if (line.startsWith("data:")) data += line.slice(5).trim();
      }
      let payload = {};
      if (data) {
        try { payload = JSON.parse(data); }
        catch { payload = { raw: data }; }
      }
      handler(eventName, payload);
    }
  }
}

const markdownReady = Boolean(window.marked && window.DOMPurify);
if (markdownReady) window.marked.setOptions({ gfm: true, breaks: true, headerIds: false, mangle: false });

function renderMarkdownInto(element, raw) {
  if (!markdownReady) {
    element.textContent = raw;
    return;
  }
  element.innerHTML = window.DOMPurify.sanitize(window.marked.parse(raw), { ADD_ATTR: ["target"] });
  for (const link of element.querySelectorAll("a[href^='http']")) {
    link.target = "_blank";
    link.rel = "noopener noreferrer";
  }
}

const pendingRenders = new WeakSet();
function scheduleMarkdownRender(element) {
  if (pendingRenders.has(element)) return;
  pendingRenders.add(element);
  requestAnimationFrame(() => {
    pendingRenders.delete(element);
    withScrollStick(() => renderMarkdownInto(element, element.dataset.raw || ""));
  });
}

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
  return node("section", { class: "empty-state" },
    node("div", { class: "empty-mark", "aria-hidden": "true" }, "†"),
    node("h1", {}, "What are we building?"),
    node("p", {}, "Describe the outcome. Dagger will plan, use tools, and keep you posted."),
    node("div", { class: "starter-chips", "aria-label": "Example prompts" },
      node("button", { type: "button", onclick: () => fillPrompt("Review this project and suggest the highest-impact improvement.") }, "Review this project"),
      node("button", { type: "button", onclick: () => fillPrompt("Find and fix the most important failing test.") }, "Fix a failing test")));
}

function clearTranscript() { els.transcript.replaceChildren(emptyState()); }

function renderHistory(history) {
  els.transcript.replaceChildren();
  for (const message of history) {
    if (message.role === "user") {
      els.transcript.appendChild(node("div", { class: "msg user" }, message.text || ""));
    } else if (message.role === "assistant") {
      const answer = node("div", { class: "answer markdown-body" });
      answer.dataset.raw = message.text || "";
      renderMarkdownInto(answer, message.text || "");
      els.transcript.appendChild(node("div", { class: "msg assistant" }, answer));
    } else if (message.role === "tool") {
      const details = node("details", { class: "thinking-block" },
        node("summary", {}, "Tool result"),
        node("pre", { class: "thinking-body" }, (message.text || "").slice(0, 1600)));
      els.transcript.appendChild(node("div", { class: "msg assistant" }, details));
    }
  }
  if (!history.length) clearTranscript();
  els.transcript.scrollTop = els.transcript.scrollHeight;
}

function appendUserMessage(text, images) {
  if (els.transcript.querySelector(".empty-state")) els.transcript.replaceChildren();
  const message = node("div", { class: "msg user" });
  if (images?.length) {
    const strip = node("div", { class: "image-strip" });
    for (const image of images) strip.appendChild(node("div", { class: "image-thumb" }, node("img", { src: image.dataUrl, alt: "Attached image" })));
    message.appendChild(strip);
  }
  message.appendChild(document.createTextNode(text));
  withScrollStick(() => els.transcript.appendChild(message));
}

function beginAssistantMessage() {
  const message = node("div", { class: "msg assistant" });
  state.toolCallNodes = {};
  state.lastBlock = null;
  state.lastBlockType = null;
  state.currentFooter = node("div", { class: "msg-footer" },
    node("button", { type: "button", onclick: () => copyMessage(message) }, "Copy"),
    node("span", { class: "usage-stamp" }));
  message.appendChild(state.currentFooter);
  withScrollStick(() => els.transcript.appendChild(message));
  state.currentMsg = message;
}

async function copyMessage(message) {
  const text = Array.from(message.querySelectorAll(".answer, .tc-result"))
    .map((element) => element.dataset.raw || element.textContent || "")
    .join("\n").trim();
  try {
    await navigator.clipboard.writeText(text);
    showToast("Copied");
  } catch { showToast("Could not copy"); }
}

function pushSegment(element) {
  if (!state.currentMsg || !state.currentFooter) return;
  withScrollStick(() => state.currentMsg.insertBefore(element, state.currentFooter));
  state.lastBlock = element;
}

function appendAnswerChunk(text) {
  if (!state.currentMsg) return;
  if (state.lastBlockType !== "answer") {
    const answer = node("div", { class: "answer markdown-body" });
    answer.dataset.raw = "";
    pushSegment(answer);
    state.lastBlockType = "answer";
  }
  state.lastBlock.dataset.raw = (state.lastBlock.dataset.raw || "") + text;
  scheduleMarkdownRender(state.lastBlock);
}

function appendThinkingChunk(text) {
  if (!state.currentMsg) return;
  if (state.lastBlockType !== "thinking") {
    const body = node("pre", { class: "thinking-body" });
    const details = node("details", { class: "thinking-block" }, node("summary", {}, "Thinking"), body);
    details.thinkingBody = body;
    pushSegment(details);
    state.lastBlockType = "thinking";
  }
  withScrollStick(() => state.lastBlock.thinkingBody.appendChild(document.createTextNode(text)));
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

function appendToolCall(id, name, args) {
  if (!state.currentMsg) return;
  const tool = node("div", { class: "tool-call" },
    node("span", { class: "tc-name" }, name || "Tool"),
    node("span", { class: "tc-args" }, formatToolArgs(args)),
    node("span", { class: "tc-result" }, "Running…"));
  state.toolCallNodes[id || ""] = tool;
  pushSegment(tool);
  state.lastBlockType = "tool_call";
}

function appendToolResult(id, excerpt, length) {
  const tool = state.toolCallNodes[id || ""];
  if (!tool) return;
  const result = tool.querySelector(".tc-result");
  withScrollStick(() => { result.textContent = `${excerpt || "Complete"}${length ? ` · ${length} chars` : ""}`; });
}

function setUsageStamp(usage) {
  if (!state.currentMsg) return;
  const stamp = state.currentMsg.querySelector(".usage-stamp");
  if (!stamp) return;
  const cost = usage.costUsd ? `$${Number(usage.costUsd).toFixed(4)}` : "$0";
  stamp.textContent = `${usage.inputTokens || 0} in · ${usage.outputTokens || 0} out · ${cost}`;
}

function showRetryButton() {
  if (!state.currentFooter || !state.lastTurn || state.currentFooter.querySelector(".retry")) return;
  state.currentFooter.insertBefore(node("button", {
    class: "retry",
    type: "button",
    onclick: () => { if (!state.streaming) runTurn(state.lastTurn.prompt, state.lastTurn.images); },
  }, "Retry"), state.currentFooter.firstChild);
}

function withScrollStick(callback) {
  const distance = els.transcript.scrollHeight - (els.transcript.scrollTop + els.transcript.clientHeight);
  const shouldStick = distance < 140;
  callback();
  if (shouldStick) els.transcript.scrollTop = els.transcript.scrollHeight;
}

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
    els.imageStrip.appendChild(node("div", { class: "image-thumb" },
      node("img", { src: image.dataUrl, alt: `Attachment ${index + 1}` }),
      node("button", { type: "button", "aria-label": "Remove attachment", onclick: () => {
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
  const jobs = state.jobs.filter((job) => !query || job.jobId.toLowerCase().includes(query) || (job.model || "").toLowerCase().includes(query));
  els.jobsList.replaceChildren();
  if (!jobs.length) {
    els.jobsList.appendChild(node("div", { class: "jobs-empty" }, query ? "No matching jobs." : "No jobs yet."));
    return;
  }
  for (const job of jobs) {
    const meta = [job.status, relativeTime(job.updatedAt), job.jobId.slice(0, 8)].filter(Boolean).join(" · ");
    const actions = node("div", { class: "job-actions" });
    if (job.interrupted) actions.appendChild(node("button", { type: "button", onclick: () => resumeJob(job.jobId) }, "Resume"));
    actions.appendChild(node("button", { class: "delete", type: "button", "aria-label": "Delete job", onclick: () => deleteJob(job.jobId) }, "Delete"));
    els.jobsList.appendChild(node("div", { class: `job-row${job.jobId === state.currentJobId ? " active" : ""}` },
      node("button", { class: "job-main", type: "button", onclick: () => selectJob(job.jobId) },
        node("span", { class: "job-title" }, job.model || "Dagger job"),
        node("span", { class: "job-meta" }, meta)),
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
    els.endpoint.appendChild(node("option", { value: "" }, active ? `Active · ${active.displayName || active.id}` : "Active default"));
    for (const endpoint of state.endpoints.items || []) {
      els.endpoint.appendChild(node("option", { value: endpoint.id }, endpoint.displayName || endpoint.id));
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
    els.writesList.appendChild(node("div", { class: "writes-empty" }, "No staged changes. Turn on Preview writes to approve file edits before they land."));
    return;
  }
  for (const write of writes) {
    const details = node("details", {},
      node("summary", {},
        node("span", { class: "write-path" }, write.displayPath || write.absolutePath || "File change"),
        node("span", { class: "write-size" }, `${write.oldLength || 0} → ${write.newLength || 0}`)),
      node("pre", { class: "write-diff" }, write.unifiedDiff || "No diff available."));
    const actions = node("div", { class: "write-actions" },
      node("button", { class: "approve", type: "button", onclick: () => resolveWrite("confirm", write.absolutePath) }, "Approve"),
      node("button", { type: "button", onclick: () => resolveWrite("discard", write.absolutePath) }, "Discard"));
    els.writesList.appendChild(node("article", { class: "write-card" }, details, actions));
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
        els.workingDirs.replaceChildren(...directories.map((directory) => node("option", { value: directory })));
        if (!els.workingDir.value && directories.length) els.workingDir.value = directories[0];
      } catch (error) { console.warn("Could not load working directories", error); }
    })(),
  ]);
}

boot();
