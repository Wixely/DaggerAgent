// Shared DOM helpers. No shell-specific knowledge, no module-level element lookups —
// anything that needs to know *which* transcript or *which* dialog belongs in the
// shell, or in a core module that takes it as an argument.
//
// withScrollStick deliberately lives with the transcript rather than here: it reads
// scrollHeight off a specific scroller, so it is not a free function.

export const $ = (id) => document.getElementById(id);

// The superset of the two shells' element builders. Mobile's `node` lacked the `html`
// key and defaulted props to {}; both are folded in here, so mobile loses nothing by
// switching to this and desktop's innerHTML call sites keep working.
export function el(tag, props, ...children) {
  const node = document.createElement(tag);
  if (props) {
    for (const [k, v] of Object.entries(props)) {
      if (k === "class") node.className = v;
      else if (k === "html") node.innerHTML = v;
      else if (k === "text") node.textContent = v;
      else if (k.startsWith("on") && typeof v === "function") node.addEventListener(k.slice(2).toLowerCase(), v);
      else if (k === "dataset") Object.assign(node.dataset, v);
      else if (v === true) node.setAttribute(k, "");
      else if (v !== false && v !== null && v !== undefined) node.setAttribute(k, v);
    }
  }
  for (const c of children.flat()) {
    if (c == null || c === false) continue;
    node.appendChild(typeof c === "string" ? document.createTextNode(c) : c);
  }
  return node;
}

export function escapeHtml(s) {
  return String(s)
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll("\"", "&quot;")
    .replaceAll("'", "&#39;");
}
