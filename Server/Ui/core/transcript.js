// The streaming transcript: the state machine both shells drive from SSE frames.
//
// What is shared is the mechanics, not the wording. Three cursors on the caller's state
// object (currentMsg, currentFooter, lastBlock) plus a tool-call-id map decide where the
// next frame lands, and every mutation goes through withScrollStick so the view stays
// pinned. Those are the parts that were subtly bug-prone in duplicate.
//
// What differs between the shells is text, classes and a couple of node shapes: mobile
// says "Thinking" where desktop says "thinking…", formats usage for a 390px line, and
// builds a different empty state. Those arrive through `view` rather than being guessed
// at here, so neither shell gives up its own voice to share the machinery.

import { renderMarkdownInto, createMarkdownScheduler } from "./markdown.js";

// Where a <details> keeps its <pre>. A WeakMap rather than an expando on the element:
// the two shells had picked different property names for the same stash, which is the
// kind of accidental divergence this refactor exists to remove.
const thinkingBodies = new WeakMap();

export function createTranscript({ mount, state, view, stickThreshold = 120, onRetry }) {
  // Stick to bottom only if the user was already near the bottom BEFORE the DOM grew.
  // Measuring afterwards misses the sticky cases: a tool_result that replaces one
  // character with five hundred adds hundreds of pixels in a single shot, so the
  // post-change distance is already past the threshold. The threshold is the shell to
  // decide, being how much room one or two buffered streaming chunks need before the
  // user counts as having deliberately scrolled away.
  function withScrollStick(fn) {
    const before = mount.scrollHeight - (mount.scrollTop + mount.clientHeight);
    const sticky = before < stickThreshold;
    fn();
    if (sticky) mount.scrollTop = mount.scrollHeight;
  }

  const scheduleMarkdownRender = createMarkdownScheduler(withScrollStick);

  function clearTranscript() {
    mount.replaceChildren(view.emptyState());
  }

  function answerBlock(text) {
    const node = document.createElement("div");
    node.className = "answer markdown-body";
    node.dataset.raw = text || "";
    if (text) renderMarkdownInto(node, text);
    return node;
  }

  function renderHistory(history) {
    // An empty history means a brand-new job, which should look like a fresh load rather
    // than a blank pane. Mobile did this; desktop left the transcript empty.
    if (!history || !history.length) {
      clearTranscript();
      return;
    }
    mount.replaceChildren();
    for (const m of history) {
      const wrap = document.createElement("div");
      if (m.role === "user") {
        wrap.className = "msg user";
        wrap.textContent = m.text || "";
      } else if (m.role === "assistant") {
        wrap.className = "msg assistant";
        wrap.appendChild(answerBlock(m.text || ""));
      } else if (m.role === "tool") {
        wrap.className = "msg assistant";
        wrap.appendChild(view.historyToolBlock(m.text || ""));
      } else {
        continue;
      }
      mount.appendChild(wrap);
    }
    // Switching history always lands at the bottom, with no stickiness test.
    mount.scrollTop = mount.scrollHeight;
  }

  function appendUserMessage(text, images) {
    if (mount.querySelector(".empty-state")) mount.replaceChildren();
    const node = document.createElement("div");
    node.className = "msg user";
    if (images && images.length) {
      const strip = document.createElement("div");
      strip.className = "image-strip";
      for (const img of images) strip.appendChild(view.userImage(img));
      node.appendChild(strip);
    }
    node.appendChild(document.createTextNode(text));
    withScrollStick(() => mount.appendChild(node));
  }

  function beginAssistantMessage() {
    const msg = document.createElement("div");
    msg.className = "msg assistant";
    state.toolCallNodes = {};
    state.lastBlock = null;
    state.lastBlockType = null;

    // Prefer the raw markdown buffer where there is one, since it preserves the syntax
    // the model wrote. Tool results never have one, so fall back to rendered text.
    const collect = () => Array.from(msg.querySelectorAll(".answer, .tc-result"))
      .map((n) => (n.dataset && n.dataset.raw) || n.textContent || "")
      .join("\n")
      .trim();

    const stamp = document.createElement("span");
    stamp.className = view.usageStampClass || "usage-stamp";

    // The footer stays the very last child; every segment is inserted before it, which is
    // what keeps pushSegment a one-liner.
    const footer = document.createElement("div");
    footer.className = "msg-footer";
    footer.appendChild(view.copyButton(collect));
    footer.appendChild(stamp);

    state.currentFooter = footer;
    msg.appendChild(footer);
    withScrollStick(() => mount.appendChild(msg));
    state.currentMsg = msg;
  }

  function pushSegment(node) {
    if (!state.currentMsg || !state.currentFooter) return;
    withScrollStick(() => state.currentMsg.insertBefore(node, state.currentFooter));
    state.lastBlock = node;
  }

  function appendAnswerChunk(text) {
    if (!state.currentMsg) return;
    if (state.lastBlockType !== "answer") {
      pushSegment(answerBlock(""));
      state.lastBlockType = "answer";
    }
    // The whole buffer is re-rendered each frame: marked needs the complete document to
    // get code fences and lists right, so a per-chunk render would flicker between states.
    state.lastBlock.dataset.raw = (state.lastBlock.dataset.raw || "") + text;
    scheduleMarkdownRender(state.lastBlock);
  }

  function appendThinkingChunk(text) {
    if (!state.currentMsg) return;
    if (state.lastBlockType !== "thinking") {
      const body = document.createElement("pre");
      body.className = "thinking-body";
      const details = document.createElement("details");
      details.className = "thinking-block";
      const summary = document.createElement("summary");
      summary.textContent = view.thinkingSummary;
      details.append(summary, body);
      thinkingBodies.set(details, body);
      pushSegment(details);
      state.lastBlockType = "thinking";
    }
    const body = thinkingBodies.get(state.lastBlock);
    if (!body) return;
    withScrollStick(() => body.appendChild(document.createTextNode(text)));
  }

  function appendToolCall(id, name, args) {
    if (!state.currentMsg) return;
    const node = document.createElement("div");
    node.className = "tool-call";

    const nameEl = document.createElement("span");
    nameEl.className = "tc-name";
    nameEl.textContent = view.toolName(name);

    const argsEl = document.createElement("span");
    argsEl.className = "tc-args";
    argsEl.textContent = view.toolArgs(args);

    const resultEl = document.createElement("span");
    resultEl.className = view.toolPendingClass ? `tc-result ${view.toolPendingClass}` : "tc-result";
    resultEl.textContent = view.toolPending;

    node.append(nameEl, argsEl, resultEl);
    state.toolCallNodes[id || ""] = node;
    pushSegment(node);
    state.lastBlockType = "tool_call";
  }

  function appendToolResult(id, excerpt, length) {
    const node = state.toolCallNodes[id || ""];
    if (!node) return;
    const resultEl = node.querySelector(".tc-result");
    if (!resultEl) return;
    withScrollStick(() => {
      resultEl.textContent = view.toolResult(excerpt, length);
      if (view.toolPendingClass) resultEl.classList.remove(view.toolPendingClass);
    });
  }

  function setUsageStamp(usage) {
    if (!state.currentMsg) return;
    const stamp = state.currentMsg.querySelector(".usage-stamp");
    if (stamp) stamp.textContent = view.usageText(usage);
  }

  // Sits next to copy when a turn errors. The turn is read at click time from
  // state.lastTurn and guarded on state.streaming, so a stale error bubble cannot stack a
  // second turn on top of a running one. Marked with data-retry rather than a class,
  // because the two shells name the button differently in their own CSS.
  function showRetryButton() {
    const footer = state.currentFooter;
    if (!footer || !state.lastTurn || footer.querySelector("[data-retry]")) return;
    const btn = view.retryButton(() => {
      if (!state.streaming && state.lastTurn) onRetry(state.lastTurn);
    });
    if (!btn) return;
    btn.dataset.retry = "1";
    footer.insertBefore(btn, footer.firstChild);
  }

  return {
    withScrollStick,
    clearTranscript,
    renderHistory,
    appendUserMessage,
    beginAssistantMessage,
    pushSegment,
    appendAnswerChunk,
    appendThinkingChunk,
    appendToolCall,
    appendToolResult,
    setUsageStamp,
    showRetryButton,
  };
}
