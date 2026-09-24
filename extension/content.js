// Quarry content script - collects media URLs from the live DOM.
// Only answers when the popup asks; never touches the page itself.
(() => {
  "use strict";

  const MEDIA_RE = /\.(jpe?g|png|gif|webp|avif|bmp|tiff?|mp4|webm|m4v)([?#]|$)/i;
  const SKIP_RE = /\.(svg|css|js|mjs|json|xml|html?|ico|woff2?|ttf|eot)([?#]|$)/i;
  const JUNK_RE = /avatar|logo|sprite|placeholder|spinner|1x1|pixel|loading|favicon|tracking/i;

  const absolute = (u) => {
    if (!u || typeof u !== "string") return null;
    const s = u.trim();
    if (!s || s.startsWith("data:") || s.startsWith("blob:") || s.startsWith("javascript:")) return null;
    try {
      const a = new URL(s, location.href);
      if (a.protocol !== "http:" && a.protocol !== "https:") return null;
      a.hash = "";
      const path = a.pathname;
      if (SKIP_RE.test(path) || JUNK_RE.test(path)) return null;
      return a.href;
    } catch {
      return null;
    }
  };

  // Largest candidate from a srcset attribute.
  const bestFromSrcset = (ss) => {
    let best = null, bestW = -1, last = null;
    for (const entry of ss.split(",")) {
      const parts = entry.trim().split(/\s+/);
      if (!parts[0]) continue;
      last = parts[0];
      if (parts[1] && /w$/i.test(parts[1])) {
        const w = parseFloat(parts[1]);
        if (!isNaN(w) && w > bestW) { best = parts[0]; bestW = w; }
      }
    }
    return best || last;
  };

  const collect = () => {
    const urls = new Set();
    const add = (u) => { const a = absolute(u); if (a) urls.add(a); };

    for (const el of document.querySelectorAll("img, source, video")) {
      add(el.getAttribute("src"));
      add(el.getAttribute("data-src"));
      add(el.getAttribute("data-original"));
      add(el.getAttribute("data-lazy"));
      const ss = el.getAttribute("srcset") || el.getAttribute("data-srcset");
      if (ss) add(bestFromSrcset(ss));
    }

    const og = document.querySelector('meta[property="og:image"]');
    if (og) add(og.getAttribute("content"));

    // Links only when they really look like media files.
    for (const a of document.querySelectorAll("a[href]")) {
      const href = a.getAttribute("href");
      if (href && MEDIA_RE.test(href)) add(href);
    }

    return [...urls];
  };

  chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
    if (msg && msg.type === "quarry-collect") {
      try {
        sendResponse({ urls: collect() });
      } catch {
        sendResponse({ urls: [] });
      }
    }
    return false; // synchronous response
  });
})();
