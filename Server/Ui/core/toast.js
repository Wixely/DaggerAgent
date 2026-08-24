// Transient status message. Both shells report the same kinds of failure - a save that
// did not take, a delete that was refused - and neither should block the page to say so.
//
// The element and its styling are the shell's (see .toast in primitives.css); this owns
// only the show-and-expire behaviour, including the timer reset that stops a second
// message inheriting the remainder of the first one's countdown.

const VISIBLE_MS = 2600;

export function createToast(node, anchor) {
  // The toast sits above the composer, and the composer grows as its textarea does, so
  // a fixed offset is wrong the moment anyone types a few lines. Track the real height
  // instead and let the stylesheet read it as --toast-offset. Both shells pin their
  // composer to the bottom, so this works for either without knowing which is which.
  if (anchor && typeof ResizeObserver !== "undefined") {
    const sync = () => document.documentElement.style.setProperty(
      "--toast-offset", `${Math.round(anchor.getBoundingClientRect().height) + 14}px`);
    new ResizeObserver(sync).observe(anchor);
    sync();
  }

  let timer = null;
  return function showToast(message) {
    if (!node) return;
    clearTimeout(timer);
    node.textContent = message;
    node.hidden = false;
    timer = setTimeout(() => { node.hidden = true; }, VISIBLE_MS);
  };
}
