// The turn engine: what happens between pressing send and the stream going quiet.
//
// Owns the lifecycle (abort controller, streaming flag, cursor teardown), the SSE
// dispatch table, the prompt queue and image attachment. Everything that pokes a
// specific button, pill or strip goes through `host`, because that is exactly where the
// two shells differ - desktop toggles a d-none class and says "paused" when a turn ends,
// mobile flips a hidden attribute and says "Ready".
//
// Job CRUD deliberately stays in the shells. selectJob and friends are mostly labels,
// sheets, focus and confirm text, wrapped around a few lines of shared data flow; the
// shared part is loadJob below, and hoisting the rest would need a bigger adapter than
// the duplication it removes.

export function createSession({ api, streamPost, state, transcript, host }) {
  function handleSseEvent(name, data) {
    switch (name) {
      case "job":
        state.currentJobId = data.jobId;
        host.onJobId(data.jobId);
        break;
      case "delta":
        transcript.appendAnswerChunk(data.text || "");
        break;
      case "thinking":
        transcript.appendThinkingChunk(data.text || "");
        break;
      case "tool_call":
        transcript.appendToolCall(data.id, data.name, data.args);
        break;
      case "tool_result":
        transcript.appendToolResult(data.id, data.excerpt || "", data.length || 0, data.durationMs);
        break;
      case "tool_progress":
        transcript.updateToolProgress(data.calls || []);
        break;
      case "permission_request":
        transcript.showPermissionPrompt(data, async (optionId) => {
          try {
            await api("/permissions/resolve", {
              method: "POST",
              headers: { "Content-Type": "application/json" },
              body: JSON.stringify({ requestId: data.requestId, optionId }),
            });
          } catch (err) { console.error("permission resolve failed", err); }
        });
        break;
      case "permission_resolved":
        transcript.resolvePermissionPrompt(data.requestId, data.optionId);
        break;
      case "usage":
        transcript.setUsageStamp(data);
        break;
      case "status":
        // The shells word this differently enough (and colour it differently) that the
        // whole mapping belongs to them rather than half of it living here.
        host.onSseStatus(data);
        break;
      case "plan_update":
        host.onPlanUpdate?.();
        break;
      case "error":
        transcript.appendAnswerChunk(host.formatError(data.message));
        host.onTurnError?.(new Error(data.message));
        transcript.showRetryButton();
        break;
      case "done":
      default:
        break;
    }
  }

  async function runTurn(prompt, images) {
    state.streaming = true;
    // Remembered so the on-error retry button resends the prompt that actually failed,
    // rather than whatever has been typed since.
    state.lastTurn = { prompt, images };
    state.abortCtrl = new AbortController();
    host.onTurnStart();

    transcript.appendUserMessage(prompt, images);
    transcript.beginAssistantMessage();

    const path = state.currentJobId
      ? `/jobs/${encodeURIComponent(state.currentJobId)}/messages/stream`
      : "/jobs/stream";

    try {
      await streamPost(path, host.buildBody(prompt, images), handleSseEvent, state.abortCtrl.signal);
    } catch (err) {
      // An abort is the cancel button doing its job, not a failure to report.
      if (err.name !== "AbortError") {
        console.error(err);
        transcript.appendAnswerChunk(host.formatError(err.message));
        host.onTurnError?.(err);
        transcript.showRetryButton();
      }
    } finally {
      state.streaming = false;
      state.abortCtrl = null;
      state.currentMsg = null;
      host.onTurnEnd();
      // Draining here rather than at the send site means anything that starts a turn
      // gets the queue drained after it, not only the path that happened to remember to.
      drainQueue();
    }
  }

  function cancelTurn() {
    state.abortCtrl?.abort();
  }

  function enqueue(prompt, images) {
    state.queue.push({ id: Math.random().toString(36).slice(2), prompt, images });
    host.onQueueChange();
  }

  async function drainQueue() {
    if (state.streaming) return;
    const next = state.queue.shift();
    if (!next) return;
    host.onQueueChange();
    await runTurn(next.prompt, next.images);
  }

  // Reads the file as a data URL and keeps both halves: the base64 payload is what the
  // API wants, the data URL is what a thumbnail can render without a second read.
  function addImage(file) {
    if (!file.type.startsWith("image/")) return;
    const reader = new FileReader();
    reader.onload = () => {
      const dataUrl = String(reader.result || "");
      const comma = dataUrl.indexOf(",");
      const base64 = comma >= 0 ? dataUrl.slice(comma + 1) : dataUrl;
      state.pendingImages.push({ mediaType: file.type, base64, dataUrl });
      host.onImagesChange();
    };
    reader.readAsDataURL(file);
  }

  // Loads a job into the transcript and restores the context it was running under, so the
  // next turn stays on the same provider and directory. The endpoint is only restored when
  // the option still exists - a job can outlive the endpoint it was created with, and
  // assigning a missing value silently blanks a <select>.
  async function loadJob(jobId) {
    const view = await api(`/jobs/${encodeURIComponent(jobId)}`);
    transcript.renderHistory(view.history || []);
    const inputs = host.contextInputs || {};
    if (view.workingDirectory && inputs.workingDir) inputs.workingDir.value = view.workingDirectory;
    if (inputs.endpoint && view.endpointId != null
        && Array.from(inputs.endpoint.options).some((o) => o.value === view.endpointId)) {
      inputs.endpoint.value = view.endpointId;
    }
    return view;
  }

  return { handleSseEvent, runTurn, cancelTurn, enqueue, drainQueue, addImage, loadJob };
}
