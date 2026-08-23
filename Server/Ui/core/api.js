// Shared HTTP + SSE client. Knows about the API key and the base path; knows nothing
// about dialogs, transcripts or which shell is calling.
//
// The 401 path is the only place this needs the host: it hands back a reason string
// and expects a Promise<boolean> — true meaning "a new key was entered, retry".

const API_KEY_STORAGE = "daggerApiKey";

export function getApiKey() { return localStorage.getItem(API_KEY_STORAGE) || ""; }
export function setApiKey(v) { localStorage.setItem(API_KEY_STORAGE, v || ""); }

export function authHeaders(extra) {
  const h = Object.assign({}, extra || {});
  const k = getApiKey();
  if (k) h["X-Api-Key"] = k;
  return h;
}

// Both shells are served from a path that ends in the shell's own segment: the desktop
// one at <base>/ui (or /ui/<asset>) and mobile at <base>/mobile. Strip whichever is
// present and what remains is the configurable ServerOptions.Path.
export function resolveBasePath(pathname) {
  return (pathname ?? window.location.pathname).replace(/\/(?:ui(?:\/.*)?|mobile)\/?$/, "") || "/agent";
}

export function createApi({ basePath, onUnauthorized }) {
  const url = (path) => (path.startsWith("/") ? `${basePath}${path}` : `${basePath}/${path}`);

  // Single-flight the key prompt. Without this, N requests failing 401 together each
  // call showModal() on the same <dialog>, and every call after the first throws
  // InvalidStateError. Mobile had this guard; desktop did not.
  let pending = null;
  function askForKey(reason) {
    if (!onUnauthorized) return Promise.resolve(false);
    if (!pending) pending = Promise.resolve(onUnauthorized(reason)).finally(() => { pending = null; });
    return pending;
  }

  const RETRY_REASON = "Server rejected the API key. Try again.";

  async function api(path, opts = {}) {
    const target = url(path);
    const r = await fetch(target, Object.assign({}, opts, { headers: authHeaders(opts.headers) }));
    if (r.status === 401) {
      if (!(await askForKey(RETRY_REASON))) throw new Error("Unauthorized");
      return api(path, opts);
    }
    if (!r.ok) {
      const body = await r.text().catch(() => "");
      throw new Error(`HTTP ${r.status} on ${target}: ${body.slice(0, 200)}`);
    }
    const ct = r.headers.get("content-type") || "";
    return ct.includes("application/json") ? r.json() : r.text();
  }

  async function streamPost(path, body, handlers, signal) {
    const target = url(path);
    const r = await fetch(target, {
      method: "POST",
      headers: authHeaders({ "Content-Type": "application/json", Accept: "text/event-stream" }),
      body: JSON.stringify(body),
      signal,
    });
    if (r.status === 401) {
      if (!(await askForKey(RETRY_REASON))) throw new Error("Unauthorized");
      return streamPost(path, body, handlers, signal);
    }
    if (!r.ok || !r.body) throw new Error(`HTTP ${r.status} on ${target}`);

    const reader = r.body.getReader();
    const decoder = new TextDecoder();
    let buf = "";
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      buf += decoder.decode(value, { stream: true });
      // SSE frames are delimited by a blank line, which is CRLFCRLF from any server or
      // proxy that writes canonical line endings. Normalising first means the split
      // below works either way — mobile did this, desktop did not.
      buf = buf.replaceAll("\r\n", "\n");
      let nl;
      while ((nl = buf.indexOf("\n\n")) >= 0) {
        const block = buf.slice(0, nl);
        buf = buf.slice(nl + 2);
        let name = "message", data = "";
        for (const line of block.split("\n")) {
          if (line.startsWith("event:")) name = line.slice(6).trim();
          else if (line.startsWith("data:")) data += line.slice(5).trim();
        }
        let payload = {};
        if (data) {
          try { payload = JSON.parse(data); }
          catch { payload = { raw: data }; }
        }
        // One bad frame must not kill the stream — the turn would hang with no "done".
        try { handlers(name, payload); }
        catch (err) { console.error("SSE handler failed", name, err); }
      }
    }
  }

  return { api, streamPost };
}
