// Markdown rendering for the streaming transcript. marked and DOMPurify are vendored
// as embedded assets and loaded as classic scripts before either shell's module, so
// they are on window by the time this module is evaluated.

const ready = Boolean(window.marked && window.DOMPurify);

// GitHub-flavoured: linkify URLs, hard line-breaks inside paragraphs (which is what the
// model usually intends by a single \n), no header IDs, no mangling.
if (ready) window.marked.setOptions({ gfm: true, breaks: true, headerIds: false, mangle: false });

export function renderMarkdownInto(node, rawText) {
  if (!ready) {
    // Plain text, and let CSS white-space: pre-wrap deal with the newlines.
    node.textContent = rawText;
    return;
  }
  // DOMPurify strips any <script>, javascript: or on*= the model might emit. ADD_ATTR
  // keeps target, which the loop below sets.
  node.innerHTML = window.DOMPurify.sanitize(window.marked.parse(rawText), { ADD_ATTR: ["target"] });
  // External links open in a new tab: following one in-place would unload the agent UI
  // mid-stream and lose the turn.
  for (const a of node.querySelectorAll("a[href^='http']")) {
    a.target = "_blank";
    a.rel = "noopener noreferrer";
  }
}

// Re-rendering on every streamed token thrashes the browser, so coalesce to one render
// per animation frame per block.
//
// `wrap` is how the shell keeps the transcript pinned to the bottom across the render -
// each has its own withScrollStick with a different stick threshold, so it is injected
// rather than assumed. Defaults to calling straight through for any caller that has no
// scroller to preserve.
export function createMarkdownScheduler(wrap = (fn) => fn()) {
  const pending = new WeakSet();
  return function scheduleMarkdownRender(node) {
    if (pending.has(node)) return;
    pending.add(node);
    requestAnimationFrame(() => {
      pending.delete(node);
      wrap(() => renderMarkdownInto(node, node.dataset.raw || ""));
    });
  };
}
