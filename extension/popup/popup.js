// Quarry popup - talks to the local server (127.0.0.1).
"use strict";

const $ = (id) => document.getElementById(id);
const api = (typeof browser !== "undefined") ? browser : chrome;

const state = {
  port: 8765,
  destDir: "",
  maxImages: 0,
  tab: null,
  jobId: null,
  pollTimer: null,
  serverOk: false,
  lastFolder: "",
};

// ------------------------------------------------------------- utilities

function base() {
  return `http://127.0.0.1:${state.port}`;
}

async function http(method, path, body) {
  const opts = { method };
  if (body !== undefined) {
    opts.headers = { "Content-Type": "application/json" };
    opts.body = JSON.stringify(body);
  }
  const res = await fetch(base() + path, opts);
  const data = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(data.error || `HTTP ${res.status}`);
  return data;
}

function showHint(text) {
  const el = $("hint");
  if (!text) { el.hidden = true; return; }
  el.textContent = text;
  el.hidden = false;
}

function setProgress(text, pct, counts) {
  $("progress").hidden = false;
  $("progressText").textContent = text;
  if (typeof pct === "number") $("barFill").style.width = `${pct}%`;
  if (counts !== undefined) $("progressCounts").textContent = counts;
}

function setResult(text, ok) {
  const el = $("result");
  el.hidden = false;
  const t = $("resultText");
  t.textContent = text;
  t.className = `result-text ${ok ? "ok" : "err"}`;
}

function clearResult() {
  $("result").hidden = true;
  $("progress").hidden = true;
  $("barFill").style.width = "0%";
}

function setBadge(text, color) {
  try {
    api.action.setBadgeText({ text: text || "" });
    if (color) api.action.setBadgeBackgroundColor({ color });
  } catch { /* unsupported on some browsers - ignore */ }
}

// ---------------------------------------------------------------- health

async function checkHealth() {
  try {
    const data = await http("GET", "/health");
    state.serverOk = true;
    $("dot").className = "dot on";
    $("dot").title = `Server online (v${data.version})`;
    $("version").textContent = `v${data.version}`;
    if (!state.destDir && data.downloadDir) {
      $("folderPath").textContent = data.downloadDir;
      state.lastFolder = data.downloadDir;
    }
    return data;
  } catch {
    state.serverOk = false;
    $("dot").className = "dot off";
    $("dot").title = "Server offline";
    $("version").textContent = "";
    showHint("Server offline - start it with .\\start.ps1 (see docs/setup.md).");
    return null;
  }
}

// --------------------------------------------------------------- collect

async function collectPageMedia(tabId) {
  try {
    const res = await api.tabs.sendMessage(tabId, { type: "quarry-collect" });
    return (res && res.urls) || [];
  } catch {
    return []; // content script not present (page open before install, chrome:// ...)
  }
}

// -------------------------------------------------------------- download

async function startDownload() {
  if (!state.tab || !state.tab.url) return;
  clearResult();
  showHint(state.serverOk ? "" : $("hint").textContent);

  if (!state.serverOk) {
    await checkHealth();
    if (!state.serverOk) return;
  }

  const btn = $("downloadBtn");
  btn.disabled = true;
  btn.textContent = "Working...";
  showHint("");

  try {
    setProgress("Collecting media from this page...", 5, "");
    const pageMedia = await collectPageMedia(state.tab.id);

    let cookies = [];
    try {
      cookies = (await api.cookies.getAll({ url: state.tab.url })).map((c) => ({
        name: c.name,
        value: c.value,
        domain: c.domain,
        path: c.path,
      }));
    } catch { /* cookies optional */ }

    const data = await http("POST", "/download", {
      url: state.tab.url,
      destDir: state.destDir,
      cookies,
      pageMedia,
      maxImages: state.maxImages,
    });

    state.jobId = data.jobId;
    await api.storage.local.set({ jobId: state.jobId, jobUrl: state.tab.url });
    setProgress("Queued...", 10, "");
    startPolling();
  } catch (e) {
    setProgress("Failed to start", 0, "");
    setResult(`Could not reach the server: ${e.message}`, false);
    setBadge("!", "#dc2626");
    btn.disabled = false;
    btn.textContent = "Download this page";
    await checkHealth();
  }
}

function startPolling() {
  stopPolling();
  state.pollTimer = setInterval(pollJob, 700);
  pollJob();
}

function stopPolling() {
  if (state.pollTimer) {
    clearInterval(state.pollTimer);
    state.pollTimer = null;
  }
}

async function pollJob() {
  if (!state.jobId) { stopPolling(); return; }
  let job;
  try {
    job = await http("GET", `/status/${state.jobId}`);
  } catch (e) {
    stopPolling();
    setProgress("Lost connection to the server", 0, "");
    setResult(`Status check failed: ${e.message}`, false);
    finishButton();
    await checkHealth();
    return;
  }

  const done = job.downloaded + job.skipped + job.failed;
  const total = Math.max(job.total || 0, done);
  const pct = total ? Math.min(100, Math.round((done / total) * 100)) : 10;
  const counts = total
    ? `${job.downloaded} downloaded | ${job.skipped} skipped | ${job.failed} failed / ${total}`
    : "";

  if (job.state === "running" || job.state === "queued") {
    const label = job.title
      ? (job.state === "queued" ? "Queued..." : job.title)
      : "Extracting...";
    setProgress(label, pct, counts);
    if (job.downloaded > 0) setBadge(String(job.downloaded), "#4f46e5");
    return;
  }

  // terminal state
  stopPolling();
  if (job.state === "done") {
    setProgress(job.title || "Done", 100, counts);
    const n = job.downloaded;
    setResult(`Saved ${n} new file${n === 1 ? "" : "s"} to:`, true);
    state.lastFolder = job.folder || state.lastFolder;
    $("folderPath").textContent = state.lastFolder;
    setBadge("OK", "#16a34a");
  } else {
    setProgress("Failed", pct, counts);
    setResult(job.error || "Download failed - see the server log.", false);
    setBadge("!", "#dc2626");
  }
  finishButton();
}

function finishButton() {
  const btn = $("downloadBtn");
  btn.disabled = false;
  btn.textContent = "Download this page";
}

async function resumeJob() {
  // A job from a previous popup session - show where it ended up.
  const stored = await api.storage.local.get(["jobId", "jobUrl"]);
  if (!stored.jobId) return;
  state.jobId = stored.jobId;
  let job;
  try {
    job = await http("GET", `/status/${state.jobId}`);
  } catch {
    state.jobId = null;
    return;
  }
  if (job.state === "running" || job.state === "queued") {
    finishButton();
    $("downloadBtn").disabled = true;
    startPolling();
  } else if (job.url === (state.tab && state.tab.url)) {
    const counts = `${job.downloaded} downloaded | ${job.skipped} skipped | ${job.failed} failed`;
    if (job.state === "done") {
      setProgress(job.title || "Done", 100, counts);
      setResult(`Saved ${job.downloaded} new file(s) to:`, true);
      state.lastFolder = job.folder || state.lastFolder;
      $("folderPath").textContent = state.lastFolder;
      setBadge("OK", "#16a34a");
    } else {
      setResult(job.error || "Previous download failed.", false);
      setBadge("!", "#dc2626");
    }
  }
}

// ------------------------------------------------------------------ init

async function init() {
  const stored = await api.storage.local.get({ port: 8765, destDir: "", maxImages: 0 });
  state.port = stored.port || 8765;
  state.destDir = stored.destDir || "";
  state.maxImages = stored.maxImages || 0;

  const health = await checkHealth();
  if (state.destDir) {
    $("folderPath").textContent = state.destDir;
    state.lastFolder = state.destDir;
  } else if (health && health.downloadDir) {
    $("folderPath").textContent = health.downloadDir;
    state.lastFolder = health.downloadDir;
  }

  const tabs = await api.tabs.query({ active: true, currentWindow: true });
  state.tab = tabs && tabs[0];
  if (state.tab) {
    $("pageTitle").textContent = state.tab.title || "(untitled)";
    $("pageUrl").textContent = state.tab.url || "";
  }

  if (!state.tab || !/^https?:/i.test(state.tab.url || "")) {
    finishButton();
    $("downloadBtn").disabled = true;
    showHint("Open a normal web page (http/https) first.");
  }

  await resumeJob();
}

$("downloadBtn").addEventListener("click", startDownload);

$("revealBtn").addEventListener("click", async () => {
  try {
    await http("POST", "/reveal", { path: state.lastFolder });
  } catch (e) {
    showHint(`Could not open folder: ${e.message}`);
  }
});

$("againBtn").addEventListener("click", () => {
  state.jobId = null;
  api.storage.local.remove(["jobId", "jobUrl"]);
  clearResult();
  setBadge("");
  startDownload();
});

$("settingsBtn").addEventListener("click", () => {
  try { api.runtime.openOptionsPage(); } catch { /* ignore */ }
});

init();
