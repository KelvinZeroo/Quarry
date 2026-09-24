// Quarry options page - stores settings in chrome.storage.local.
"use strict";

const api = (typeof browser !== "undefined") ? browser : chrome;

const $ = (id) => document.getElementById(id);

let port = 8765;

function setStatus(text, isErr) {
  const el = $("status");
  el.textContent = text;
  el.className = isErr ? "status err" : "status";
  if (text) setTimeout(() => { el.textContent = ""; }, 2500);
}

async function checkHealth() {
  const el = $("health");
  try {
    const res = await fetch(`http://127.0.0.1:${port}/health`, {
      signal: AbortSignal.timeout ? AbortSignal.timeout(3000) : undefined,
    });
    const data = await res.json();
    if (data.ok) {
      el.textContent = `Online (v${data.version}) - ${data.downloadDir}`;
      el.className = "health ok";
      if (!$("destDir").value) $("destHint").textContent = `Default: ${data.downloadDir}`;
      return;
    }
    throw new Error("bad response");
  } catch {
    el.textContent = `Offline - start it with .\\start.ps1 (port ${port}).`;
    el.className = "health bad";
  }
}

async function init() {
  const stored = await api.storage.local.get({
    destDir: "",
    port: 8765,
    maxImages: 0,
  });
  port = stored.port || 8765;
  $("destDir").value = stored.destDir || "";
  $("port").value = port;
  $("maxImages").value = stored.maxImages || 0;
  checkHealth();
}

async function save(e) {
  if (e) e.preventDefault();
  const newPort = parseInt($("port").value, 10);
  const destDir = $("destDir").value.trim();
  const maxImages = parseInt($("maxImages").value, 10) || 0;

  if (!newPort || newPort < 1 || newPort > 65535) {
    setStatus("Port must be 1-65535", true);
    return;
  }
  if (destDir && !/^[a-zA-Z]:[\\/]/.test(destDir) && !destDir.startsWith("\\\\")) {
    setStatus("Use a full path like D:\\Media (or leave empty)", true);
    return;
  }

  await api.storage.local.set({ destDir, port: newPort, maxImages });
  port = newPort;
  setStatus("Saved");
  checkHealth();
}

async function reset() {
  await api.storage.local.remove(["destDir", "port", "maxImages"]);
  await init();
  setStatus("Defaults restored");
}

$("form").addEventListener("submit", save);
$("resetBtn").addEventListener("click", reset);
$("retryBtn").addEventListener("click", () => {
  const p = parseInt($("port").value, 10);
  if (p) port = p;
  checkHealth();
});

init();
