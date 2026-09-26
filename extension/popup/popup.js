// Quarry popup controller - multi-job downloader, site scanning, and lightweight UI.
"use strict";

const $ = (id) => document.getElementById(id);
const api = typeof browser !== "undefined" ? browser : chrome;

const state = {
  port: 8765,
  destDir: "",
  defaultServerDir: "",
  maxImages: 0,
  // Site scanning (parity with the CLI flags)
  autoScan: true,     // site roots scan the whole site (CLI TTY behavior)
  scanForce: false,   // force-scan deep links too (CLI --scan)
  askHowMany: true,   // scan first, then ask "download how many?"
  maxScan: 2000,      // files a scan may discover (0 = no cap)
  askAbove: 500,      // confirm "download ALL" above this count (0 = never)
  theme: "light",
  tab: null,
  activeJobs: {}, // jobId -> jobObject
  pollTimer: null,
  serverOk: false,
  lastFolder: "",
  mediaCount: 0,   // lightweight: a count, never thumbnails/preview arrays
  isUnsupported: false,
  lastError: "",
};

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

function showToast(text) {
  const el = $("hintBanner");
  if (!text) {
    el.style.display = "none";
    return;
  }
  el.textContent = text;
  el.style.display = "block";
  setTimeout(() => {
    el.style.display = "none";
  }, 3500);
}

// ---------------------------------------------------------- Scan Settings UI

function fmtCap(cap) {
  return Number(cap) === 0 ? "∞" : Number(cap).toLocaleString("en-US");
}

function scanEnabled() {
  return !!(state.autoScan || state.scanForce);
}

// Single source of truth for the primary button label: "Download All
// Media (n)" normally, "Scan & Download Site (≤ cap)" when scanning is on.
function updateDownloadButtonLabel() {
  const el = $("downloadBtnText");
  if (!el || !state.serverOk) return;
  if (scanEnabled()) {
    el.textContent = `Scan & Download Site (≤ ${fmtCap(state.maxScan)})`;
    return;
  }
  const n = state.mediaCount;
  el.textContent = n > 0 ? `Download All Media (${n})` : "Download All Media";
}

// Highlight the active preset chip in BOTH chip rows and keep the
// Settings number box in sync (unless the user is typing in it).
function syncScanChips() {
  document.querySelectorAll(".scan-chip-row .chip-scan").forEach((chip) => {
    chip.classList.toggle("active", parseInt(chip.dataset.cap, 10) === state.maxScan);
  });
  const inp = $("settingsMaxScan");
  if (inp && document.activeElement !== inp) inp.value = state.maxScan || "";
}

function updateScanRowVisibility() {
  const row = $("scanLimitRow");
  if (row) row.style.display = scanEnabled() ? "flex" : "none";
}

function persistScanSettings() {
  return api.storage.local.set({
    autoScan: state.autoScan,
    scanForce: state.scanForce,
    askHowMany: state.askHowMany,
    maxScan: state.maxScan,
    askAbove: state.askAbove,
  });
}

function sanitizeCount(value, fallback) {
  const v = parseInt(value, 10);
  return Number.isFinite(v) && v >= 0 ? v : fallback;
}

function wireScanChipRow(row) {
  if (!row) return;
  row.querySelectorAll(".chip-scan").forEach((chip) => {
    chip.addEventListener("click", async () => {
      state.maxScan = sanitizeCount(chip.dataset.cap, 2000);
      syncScanChips();
      updateDownloadButtonLabel();
      await persistScanSettings();
    });
  });
}

function detectSiteName(url) {
  if (!url) return "PAGE";
  try {
    const u = new URL(url);
    if (u.protocol === "edge:" || u.protocol === "chrome:" || u.protocol === "about:" || u.protocol === "chrome-extension:") {
      return "INTERNAL";
    }
    const host = u.hostname.replace(/^www\./i, "").toLowerCase();
    const parts = host.split(".");
    return parts[0].toUpperCase() || "PAGE";
  } catch {
    return "PAGE";
  }
}

// ------------------------------------------------------------- Theme System

function applyTheme(themeName) {
  state.theme = themeName;
  let effectiveTheme = themeName;
  if (themeName === "system") {
    effectiveTheme = window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
  }

  document.documentElement.setAttribute("data-theme", effectiveTheme);

  document.querySelectorAll(".chip-theme").forEach((btn) => {
    if (btn.dataset.themeVal === themeName) {
      btn.classList.add("active");
    } else {
      btn.classList.remove("active");
    }
  });

  api.storage.local.set({ theme: themeName });
}

document.querySelectorAll(".chip-theme").forEach((btn) => {
  btn.addEventListener("click", () => applyTheme(btn.dataset.themeVal));
});

window.matchMedia("(prefers-color-scheme: dark)").addEventListener("change", () => {
  if (state.theme === "system") applyTheme("system");
});

// ------------------------------------------------------------- Tabs Navigation

function switchTab(tabId) {
  document.querySelectorAll(".tab-btn").forEach((btn) => {
    if (btn.dataset.tab === tabId) {
      btn.classList.add("active");
    } else {
      btn.classList.remove("active");
    }
  });

  document.querySelectorAll(".tab-panel").forEach((panel) => {
    if (panel.id === tabId) {
      panel.classList.add("active");
    } else {
      panel.classList.remove("active");
    }
  });
}

document.querySelectorAll(".tab-btn").forEach((btn) => {
  btn.addEventListener("click", () => switchTab(btn.dataset.tab));
});

// ------------------------------------------------------------- Utilities

function escapeHtml(str) {
  if (!str) return "";
  return String(str)
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#039;");
}

// ------------------------------------------------------------- Diagnostics & Reporting

function reportSiteOrError() {
  const currentUrl = (state.tab && state.tab.url) || "Unknown URL";
  const currentTitle = (state.tab && state.tab.title) || "Unknown Title";
  const errorMsg = state.lastError || "No media found on page";

  const diagnosticReport = [
    `### Quarry Site Diagnostic Report`,
    `- **URL**: ${currentUrl}`,
    `- **Title**: ${currentTitle}`,
    `- **Media Detected**: ${state.mediaCount}`,
    `- **Server Status**: ${state.serverOk ? "Online" : "Offline"} (:8765)`,
    `- **Issue**: ${errorMsg}`,
    `- **Timestamp**: ${new Date().toISOString()}`,
    `- **Browser**: ${navigator.userAgent}`,
  ].join("\n");

  navigator.clipboard.writeText(diagnosticReport).then(() => {
    showToast("Diagnostic log copied to clipboard!");
  }).catch(() => {
    showToast("Report ready for " + currentUrl);
  });
}

// ---------------------------------------------------------------- Server Health

async function checkHealth() {
  const pill = $("serverStatusPill");
  const text = $("serverStatusText");
  const offlineBanner = $("serverOfflineBanner");
  const downloadBtn = $("downloadBtn");
  const downloadBtnText = $("downloadBtnText");

  try {
    const data = await http("GET", "/health");
    state.serverOk = true;
    if (pill) pill.className = "status-pill online";
    if (text) text.textContent = "Online";
    if (offlineBanner) offlineBanner.style.display = "none";
    if (downloadBtn) downloadBtn.disabled = false;
    if (downloadBtnText && !state.activeJobs[Object.keys(state.activeJobs)[0]]) {
      updateDownloadButtonLabel();
    }

    state.defaultServerDir = data.downloadDir || "";
    if (!state.destDir && data.downloadDir) {
      if ($("folderPathDisplay")) $("folderPathDisplay").textContent = data.downloadDir;
      if ($("destDirInput")) $("destDirInput").value = data.downloadDir;
      state.lastFolder = data.downloadDir;
    }
    if ($("serverConnDetails")) $("serverConnDetails").textContent = `Connected (v${data.version}) · Default: ${data.downloadDir || "(default)"}`;
    return data;
  } catch {
    state.serverOk = false;
    if (pill) pill.className = "status-pill offline";
    if (text) text.textContent = "Offline";
    if (offlineBanner) offlineBanner.style.display = "flex";
    if (downloadBtn) downloadBtn.disabled = true;
    if (downloadBtnText) downloadBtnText.textContent = "Server Offline (Run 'quarry server')";
    if ($("serverConnDetails")) $("serverConnDetails").textContent = `Server offline. Run 'quarry server' in PowerShell.`;
    return null;
  }
}

// --------------------------------------------------------------- Collect Media

async function collectPageMedia(tabId) {
  try {
    let res = null;
    try {
      res = await api.tabs.sendMessage(tabId, { type: "quarry-collect" });
    } catch {
      // Content script may not be injected yet (e.g. extension was reloaded)
      if (api.scripting && tabId) {
        try {
          await api.scripting.executeScript({
            target: { tabId },
            files: ["content.js"],
          });
          res = await api.tabs.sendMessage(tabId, { type: "quarry-collect" });
        } catch {
          res = null;
        }
      }
    }

    if (res) {
      if (res.siteName && $("siteBadge")) {
        $("siteBadge").textContent = res.siteName;
      }
      if (res.meta && (res.meta.username || res.meta.fullName || res.meta.avatar)) {
        if ($("profileMetaWrap")) $("profileMetaWrap").style.display = "flex";
        if (res.meta.avatar && $("profileAvatar")) {
          const av = $("profileAvatar");
          av.onerror = () => { av.style.display = "none"; };
          av.onload = () => { av.style.display = "block"; };
          av.src = res.meta.avatar;
        }
        if ($("profileFullName")) $("profileFullName").textContent = res.meta.fullName || res.meta.username || "";
        if ($("profileUsername")) $("profileUsername").textContent = res.meta.username ? `@${res.meta.username}` : "";
        if (res.meta.stats && $("profileStatsRow")) {
          $("profileStatsRow").textContent = res.meta.stats;
        }
        if (res.meta.bio && $("profileBioText")) {
          $("profileBioText").textContent = res.meta.bio;
          $("profileBioText").style.display = "block";
        }
      }
      if (res.urls) {
        state.mediaCount = res.urls.length;
        return res.urls;
      }
    }
    return [];
  } catch {
    return [];
  }
}

async function triggerDeepScan() {
  if (!state.tab || !state.tab.id || state.isUnsupported) return;

  const btn = $("fetchPageBtn");
  const btnText = $("fetchBtnText");

  if (btn) {
    btn.classList.add("scanning");
    btn.disabled = true;
  }
  if (btnText) btnText.textContent = "Scanning...";
  if ($("mediaCountText")) $("mediaCountText").textContent = "Deep scanning...";

  try {
    let res = null;
    try {
      res = await api.tabs.sendMessage(state.tab.id, { type: "quarry-deep-scan", scrolls: 8 });
    } catch {
      if (api.scripting) {
        try {
          await api.scripting.executeScript({
            target: { tabId: state.tab.id },
            files: ["content.js"],
          });
          res = await api.tabs.sendMessage(state.tab.id, { type: "quarry-deep-scan", scrolls: 8 });
        } catch {
          res = null;
        }
      }
    }

    if (res && res.urls) {
      // Keep only a count in popup memory - the URL list is collected
      // fresh (and sent to the server) when the download starts.
      state.mediaCount = res.urls.length;
      if ($("mediaCountText")) $("mediaCountText").textContent = `${state.mediaCount} media found`;
      updateDownloadButtonLabel();
      showToast(`Fetched ${state.mediaCount} items!`);
    } else {
      showToast("Scan finished.");
    }
  } catch (e) {
    showToast(`Scan error: ${e.message}`);
  } finally {
    if (btn) {
      btn.classList.remove("scanning");
      btn.disabled = false;
    }
    if (btnText) btnText.textContent = "Fetch All";
  }
}

// ------------------------------------------------ Multi-Job & Concurrent Downloads

function renderActiveJobs() {
  const container = $("activeJobsContainer");
  const progressSection = $("progressSection");
  if (!container || !progressSection) return;

  renderAskCards();

  const jobKeys = Object.keys(state.activeJobs);
  if (jobKeys.length === 0) {
    progressSection.style.display = "none";
    return;
  }

  progressSection.style.display = "flex";
  container.innerHTML = jobKeys
    .map((jId) => {
      const job = state.activeJobs[jId];
      const pct = job.displayPct || 0;
      const isDone = job.state === "done";
      const isFailed = job.state === "failed" || job.state === "error";
      const isAsking = job.state === "asking";
      const dotStyle = isDone
        ? "background:#10b981;"
        : isFailed
          ? "background:#ef4444;"
          : isAsking
            ? "background:#f59e0b;"
            : "";
      const pctLabel = isAsking ? "…" : `${pct}%`;

      return `
        <div class="job-item-card" data-jobid="${jId}">
          <div class="job-item-top">
            <div class="job-item-title-wrap">
              <span class="job-item-dot" style="${dotStyle}"></span>
              <span class="job-item-title">${escapeHtml(job.title || "Downloading Batch")}</span>
            </div>
            <div class="job-item-top-right">
              <span class="job-item-pct" style="${dotStyle}">${pctLabel}</span>
              <button class="job-item-cancel-btn" title="Cancel or remove download" data-cancelid="${jId}">
                <svg viewBox="0 0 24 24" width="12" height="12" fill="none" stroke="currentColor" stroke-width="2.5">
                  <line x1="18" y1="6" x2="6" y2="18"></line>
                  <line x1="6" y1="6" x2="18" y2="18"></line>
                </svg>
              </button>
            </div>
          </div>

          <div class="progress-track">
            <div class="progress-fill" style="width: ${pct}%; ${isDone ? "background:#10b981;" : isFailed ? "background:#ef4444;" : isAsking ? "background:#f59e0b;" : ""}"></div>
          </div>

          <div class="hud-stream-card">
            <div class="hud-stream-meta">
              <span class="active-stream-file" style="${isFailed ? "color:#ef4444;" : isAsking ? "color:#f59e0b;" : ""}">${escapeHtml(job.activeFile || "Writing files...")}</span>
              <span class="progress-counts">${job.downloaded || 0} saved of ${job.total || "?"} · ${job.skipped || 0} skipped</span>
            </div>
          </div>
        </div>
      `;
    })
    .join("");

  container.querySelectorAll(".job-item-cancel-btn").forEach((btn) => {
    btn.addEventListener("click", async (e) => {
      e.stopPropagation();
      const cancelId = btn.dataset.cancelid;
      if (cancelId && state.activeJobs[cancelId]) {
        delete state.activeJobs[cancelId];
        saveJobsToStorage();
        renderActiveJobs();
        try {
          await http("POST", "/cancel", { jobId: cancelId });
        } catch {
          /* ignore */
        }
      }
    });
  });
}

// ----------------------------------------------------- Scan "How Many?" Cards

// Ask cards live OUTSIDE the re-rendered job list so the input keeps focus
// while the 650 ms poll re-renders. A card is only (re)built when its
// {found, askAbove} signature changes.
function renderAskCards() {
  const section = $("askSection");
  if (!section) return;

  const askingIds = Object.keys(state.activeJobs).filter((id) => {
    const j = state.activeJobs[id];
    return j && j.state === "asking" && j.prompt && j.prompt.found > 0;
  });

  section.querySelectorAll("[data-askjob]").forEach((card) => {
    if (!askingIds.includes(card.dataset.askjob)) card.remove();
  });

  askingIds.forEach((id) => {
    const prompt = state.activeJobs[id].prompt;
    const sig = `${prompt.found}|${prompt.askAbove}`;
    let card = section.querySelector(`[data-askjob="${id}"]`);
    if (card) {
      if (card.dataset.sig === sig) return; // user may be typing: keep DOM
      card.remove();
    }
    section.appendChild(buildAskCard(id, prompt));
  });

  section.style.display = askingIds.length ? "flex" : "none";
}

function buildAskCard(jobId, prompt) {
  const found = prompt.found;
  const askAbove = prompt.askAbove || 0;
  const card = document.createElement("div");
  card.className = "glass-card ask-card";
  card.dataset.askjob = jobId;
  card.dataset.sig = `${found}|${askAbove}`;
  card.innerHTML = `
    <div class="ask-head">
      <span class="ask-badge">SCAN</span>
      <div class="ask-head-text">
        <div class="ask-title">Found ${found} files</div>
        <div class="ask-sub">Download how many?</div>
      </div>
    </div>
    <div class="ask-input-row">
      <input type="number" class="ask-input" min="1" max="${found}" value="${found}">
      <button class="btn-sm btn-primary ask-go">Download</button>
      <button class="btn-sm btn-secondary ask-all">All ${found}</button>
      <button class="btn-sm btn-secondary ask-cancel">Cancel</button>
    </div>
    <div class="ask-confirm" style="display:none;">
      <span class="ask-confirm-text">${found} files exceeds ${askAbove} - download ALL?</span>
      <button class="btn-sm btn-primary ask-yes">Yes</button>
      <button class="btn-sm btn-secondary ask-no">No</button>
    </div>
  `;

  const send = (count) => sendAnswer(jobId, count);
  const confirmRow = card.querySelector(".ask-confirm");

  card.querySelector(".ask-go").addEventListener("click", () => {
    const n = parseInt(card.querySelector(".ask-input").value, 10);
    if (!n || n < 1 || n > found) {
      showToast(`Type a number between 1 and ${found}`);
      return;
    }
    send(n);
  });
  card.querySelector(".ask-input").addEventListener("keydown", (e) => {
    if (e.key === "Enter") card.querySelector(".ask-go").click();
  });
  card.querySelector(".ask-all").addEventListener("click", () => {
    if (askAbove > 0 && found > askAbove) {
      confirmRow.style.display = "flex"; // second-step confirm
    } else {
      send(found);
    }
  });
  card.querySelector(".ask-yes").addEventListener("click", () => send(found));
  card.querySelector(".ask-no").addEventListener("click", () => {
    confirmRow.style.display = "none";
  });
  card.querySelector(".ask-cancel").addEventListener("click", () => send(null));
  return card;
}

async function sendAnswer(jobId, count) {
  try {
    await http("POST", "/answer", { jobId, count });
  } catch (e) {
    showToast(`Could not send answer: ${e.message}`);
    const stale = document.querySelector(`[data-askjob="${jobId}"]`);
    if (stale) stale.remove();
    renderAskCards();
    return;
  }

  const job = state.activeJobs[jobId];
  if (job) {
    job.prompt = null;
    if (job.state === "asking") job.state = "running";
  }
  saveJobsToStorage();
  renderActiveJobs();
  showToast(count === null ? "Download cancelled." : `Downloading up to ${count} file(s)...`);
  startPolling();
}

async function startDownload() {
  if (!state.tab || !state.tab.url || state.isUnsupported) return;

  $("resultSection").style.display = "none";
  $("unsupportedCard").style.display = "none";

  if (!state.serverOk) {
    await checkHealth();
    if (!state.serverOk) {
      showToast("Quarry daemon is offline. Run .\\start.ps1");
      return;
    }
  }

  const btn = $("downloadBtn");
  btn.disabled = true;
  $("downloadBtnText").textContent = "Queuing...";

  try {
    const pageMedia = await collectPageMedia(state.tab.id);
    const targetTitle = state.tab.title || "Media Batch";

    let cookies = [];
    try {
      cookies = (await api.cookies.getAll({ url: state.tab.url })).map((c) => ({
        name: c.name,
        value: c.value,
        domain: c.domain,
        path: c.path,
      }));
    } catch {
      /* optional cookies */
    }

    const data = await http("POST", "/download", {
      url: state.tab.url,
      destDir: state.destDir,
      cookies,
      pageMedia,
      maxImages: state.maxImages,
      autoScan: state.autoScan,
      scan: state.scanForce,
      askHowMany: state.askHowMany,
      maxScan: state.maxScan,
      askAbove: state.askAbove,
    });

    const newJobId = data.jobId;

    state.activeJobs[newJobId] = {
      jobId: newJobId,
      url: state.tab.url,
      title: targetTitle,
      total: pageMedia.length || 1,
      downloaded: 0,
      skipped: 0,
      failed: 0,
      state: "queued",
      prompt: null,
      displayPct: 8,
      folder: "",
      activeFile: scanEnabled() ? "Scanning site..." : "Downloading files...",
    };

    saveJobsToStorage();
    renderActiveJobs();
    startPolling();
    btn.disabled = false;
    updateDownloadButtonLabel();
  } catch (e) {
    state.lastError = e.message;
    showToast(`Error: ${e.message}`);
    btn.disabled = false;
    updateDownloadButtonLabel();
    await checkHealth();
  }
}

function saveJobsToStorage() {
  api.storage.local.set({ activeJobs: state.activeJobs });
}

function startPolling() {
  stopPolling();
  state.pollTimer = setInterval(pollAllJobs, 650);
  pollAllJobs();
}

function stopPolling() {
  if (state.pollTimer) {
    clearInterval(state.pollTimer);
    state.pollTimer = null;
  }
}

function showResultCard(isSuccess, title, text, folder = "") {
  const sec = $("resultSection");
  if (!sec) return;
  sec.style.display = "flex";

  const checkSvg = $("resultCheckSvg");
  const errSvg = $("resultErrorSvg");
  const titleEl = $("resultTitle");
  const textEl = $("resultText");
  const revealBtn = $("revealBtn");
  const againText = $("againBtnText");
  const actionsWrap = sec.querySelector(".result-actions");

  if (isSuccess) {
    sec.className = "glass-card result-success-card";
    if (checkSvg) checkSvg.style.display = "block";
    if (errSvg) errSvg.style.display = "none";
    if (titleEl) titleEl.textContent = title || "Download Complete";
    if (textEl) textEl.textContent = text || `Saved files to: ${folder || "Downloads"}`;
    if (revealBtn) revealBtn.style.display = "flex";
    if (againText) againText.textContent = "Download Again";
    if (actionsWrap) actionsWrap.classList.remove("single-btn");
  } else {
    sec.className = "glass-card result-error-card";
    if (checkSvg) checkSvg.style.display = "none";
    if (errSvg) errSvg.style.display = "block";
    if (titleEl) titleEl.textContent = title || "Download Failed";
    if (textEl) textEl.textContent = text || "Could not download media files.";
    if (revealBtn) revealBtn.style.display = "none";
    if (againText) againText.textContent = "Retry Download";
    if (actionsWrap) actionsWrap.classList.add("single-btn");
  }
}

async function pollAllJobs() {
  const jobIds = Object.keys(state.activeJobs);
  if (jobIds.length === 0) {
    stopPolling();
    return;
  }

  let anyRunning = false;

  for (const jId of jobIds) {
    const currentLocal = state.activeJobs[jId];
    if (currentLocal.state === "done" || currentLocal.state === "failed") continue;

    anyRunning = true;
    try {
      const job = await http("GET", `/status/${jId}`);
      const done = job.downloaded + job.skipped + job.failed;
      const total = Math.max(job.total || currentLocal.total || 1, done);

      // Real progress percentage
      let targetPct = total > 0 ? Math.round((done / total) * 100) : 0;
      if ((job.state === "running" || job.state === "asking") && targetPct < 8) {
        targetPct = 8;
      }

      currentLocal.downloaded = job.downloaded;
      currentLocal.skipped = job.skipped;
      currentLocal.failed = job.failed;
      currentLocal.total = total;
      currentLocal.state = job.state;
      currentLocal.prompt = job.prompt || null;
      currentLocal.folder = job.folder || currentLocal.folder;
      currentLocal.displayPct = isNaN(targetPct) ? 10 : Math.min(100, Math.max(0, targetPct));

      // Active file name / scan progress line
      const lastLog = job.log && job.log.length > 0 ? job.log[job.log.length - 1] : "";
      if (job.state === "error") {
        currentLocal.activeFile = job.error || "Download error";
      } else if (job.state === "done") {
        currentLocal.activeFile = "Complete";
      } else if (job.state === "asking") {
        const found = (job.prompt && job.prompt.found) || 0;
        currentLocal.activeFile = `Found ${found} files - choose how many to download`;
      } else if (lastLog.includes("scanning") || lastLog.includes("found ")) {
        // Scan progress lines ("scanning... 15 pages -> 40 files", "found N file(s)")
        currentLocal.activeFile = lastLog.trim();
      } else {
        currentLocal.activeFile = lastLog.includes("->") ? lastLog.split("->").pop().trim() : (job.title ? `Saving to ${job.title}...` : "Downloading media...");
      }

      if (job.state === "done") {
        currentLocal.displayPct = 100;
        state.lastFolder = job.folder || state.lastFolder;
        const wasCancelled = (job.log || []).some((l) => l.includes("cancelled"));
        if (wasCancelled) {
          showResultCard(true, "Scan Cancelled", "Nothing was downloaded.");
        } else {
          showResultCard(true, `${currentLocal.title} Complete`, `Saved ${job.downloaded} files to: ${job.folder || "Downloads"}`, job.folder);
        }
      } else if (job.state === "error" || job.state === "failed") {
        currentLocal.state = "failed";
        showResultCard(false, "Download Failed", job.error || "Download encountered an error");
      }
    } catch (e) {
      // Job gone server-side (server restarted or job removed): drop it so
      // polling and any pending ask card don't hang forever.
      if (e && /unknown job/.test(e.message || "")) {
        delete state.activeJobs[jId];
      }
      // otherwise: temporary network issue, keep the job
    }
  }

  saveJobsToStorage();
  renderActiveJobs();

  if (!anyRunning) {
    stopPolling();
  }
}

// ------------------------------------------------------------- Initialization

async function init() {
  if ($("progressSection")) $("progressSection").style.display = "none";
  if ($("resultSection")) $("resultSection").style.display = "none";
  if ($("unsupportedCard")) $("unsupportedCard").style.display = "none";
  if ($("folderInputWrap")) $("folderInputWrap").style.display = "none";
  if ($("hintBanner")) $("hintBanner").style.display = "none";

  let stored = {};
  try {
    stored = await api.storage.local.get({
      port: 8765,
      destDir: "",
      maxImages: 0,
      autoScan: true,
      scanForce: false,
      askHowMany: true,
      maxScan: 2000,
      askAbove: 500,
      theme: "light",
      activeJobs: {},
    });
  } catch {
    stored = {};
  }

  state.port = stored.port || 8765;
  state.destDir = stored.destDir || "";
  state.maxImages = stored.maxImages || 0;
  state.activeJobs = stored.activeJobs || {};
  // Site scanning: defaults ON (scan + ask), same as the CLI on a TTY.
  state.autoScan = stored.autoScan !== false;
  state.scanForce = !!stored.scanForce;
  state.askHowMany = stored.askHowMany !== false;
  state.maxScan = sanitizeCount(stored.maxScan, 2000);
  state.askAbove = sanitizeCount(stored.askAbove, 500);

  applyTheme(stored.theme || "light");

  if ($("settingsPort")) $("settingsPort").value = state.port;
  if ($("destDirInput")) $("destDirInput").value = state.destDir;
  if ($("settingsMaxImages")) $("settingsMaxImages").value = state.maxImages || "";

  if ($("settingsAutoScan")) $("settingsAutoScan").checked = state.autoScan;
  if ($("settingsScanForce")) $("settingsScanForce").checked = state.scanForce;
  if ($("settingsAskHowMany")) $("settingsAskHowMany").checked = state.askHowMany;
  if ($("settingsMaxScan")) $("settingsMaxScan").value = state.maxScan || "";
  if ($("settingsAskAbove")) $("settingsAskAbove").value = state.askAbove || "";
  syncScanChips();
  updateScanRowVisibility();
  wireScanChipRow($("settingsScanChips"));
  wireScanChipRow($("inlineScanChips"));

  if (state.destDir && $("folderPathDisplay")) {
    $("folderPathDisplay").textContent = state.destDir;
    state.lastFolder = state.destDir;
  }

  try {
    const health = await checkHealth();
    if (!state.destDir && health && health.downloadDir) {
      if ($("folderPathDisplay")) $("folderPathDisplay").textContent = health.downloadDir;
      if ($("destDirInput")) $("destDirInput").value = health.downloadDir;
      state.lastFolder = health.downloadDir;
    }
  } catch {
    // health check handled in checkHealth()
  }

  try {
    const tabs = await api.tabs.query({ active: true, currentWindow: true });
    state.tab = tabs && tabs[0];

    if (state.tab) {
      const url = state.tab.url || "";
      if ($("pageTitle")) $("pageTitle").textContent = state.tab.title || "(Untitled Page)";
      if ($("pageUrl")) $("pageUrl").textContent = url;
      if ($("siteBadge")) $("siteBadge").textContent = detectSiteName(url);

      if (!/^https?:/i.test(url)) {
        state.isUnsupported = true;
        if ($("downloadBtn")) $("downloadBtn").disabled = true;
        if ($("mediaCountText")) $("mediaCountText").textContent = "Internal Page";
        if ($("unsupportedCard")) $("unsupportedCard").style.display = "flex";
        if ($("unsupportedTitle")) $("unsupportedTitle").textContent = "Browser System Page";
        if ($("unsupportedDesc")) $("unsupportedDesc").textContent = "Quarry downloads from public web pages. Open any website (like Instagram, Reddit, Pinterest, or photo galleries) to start downloading.";
        return;
      }

      try {
        const mediaUrls = await collectPageMedia(state.tab.id);
        const count = mediaUrls.length;
        if ($("mediaCountText")) $("mediaCountText").textContent = `${count} media found`;
        updateDownloadButtonLabel();

        if (count === 0) {
          if ($("unsupportedCard")) $("unsupportedCard").style.display = "flex";
          if ($("unsupportedTitle")) $("unsupportedTitle").textContent = "No Media Detected";
          if ($("unsupportedDesc")) $("unsupportedDesc").textContent = "No images or videos were detected in the page DOM yet. Scroll to load more, or click below to report this website.";
        }
      } catch {
        if ($("mediaCountText")) $("mediaCountText").textContent = "Ready";
      }
    }
  } catch {
    // tabs query fallback
  }

  renderActiveJobs();
  if (Object.keys(state.activeJobs).length > 0) {
    startPolling();
  }
}

// ------------------------------------------------------------- Event Listeners

if ($("downloadBtn")) $("downloadBtn").addEventListener("click", () => startDownload());

// Site scanning toggles - applied immediately (the inline scan-limit row on
// the Images tab and the button label react right away).
[
  ["settingsAutoScan", "autoScan"],
  ["settingsScanForce", "scanForce"],
  ["settingsAskHowMany", "askHowMany"],
].forEach(([id, key]) => {
  const el = $(id);
  if (!el) return;
  el.addEventListener("change", async () => {
    state[key] = el.checked;
    updateScanRowVisibility();
    updateDownloadButtonLabel();
    await persistScanSettings();
  });
});

// Scan limit number inputs (chips call wireScanChipRow() in init()).
if ($("settingsMaxScan")) {
  $("settingsMaxScan").addEventListener("change", async () => {
    state.maxScan = sanitizeCount($("settingsMaxScan").value, 2000);
    syncScanChips();
    updateDownloadButtonLabel();
    await persistScanSettings();
  });
}
if ($("settingsAskAbove")) {
  $("settingsAskAbove").addEventListener("change", async () => {
    state.askAbove = sanitizeCount($("settingsAskAbove").value, 500);
    await persistScanSettings();
  });
}

if ($("revealBtn")) $("revealBtn").addEventListener("click", async () => {
  try {
    await http("POST", "/reveal", { path: state.lastFolder });
  } catch (e) {
    showToast(`Could not open folder: ${e.message}`);
  }
});

if ($("againBtn")) $("againBtn").addEventListener("click", () => {
  if ($("resultSection")) $("resultSection").style.display = "none";
  startDownload();
});

if ($("clearAllJobsBtn")) $("clearAllJobsBtn").addEventListener("click", () => {
  state.activeJobs = {};
  saveJobsToStorage();
  renderActiveJobs();
  stopPolling();
  if ($("resultSection")) $("resultSection").style.display = "none";
  showToast("All downloads cleared.");
});

if ($("editFolderBtn")) $("editFolderBtn").addEventListener("click", () => {
  const wrap = $("folderInputWrap");
  if (!wrap) return;
  const isHidden = wrap.style.display === "none";
  wrap.style.display = isHidden ? "flex" : "none";
  if (isHidden && $("destDirInput")) $("destDirInput").focus();
});

if ($("cancelFolderBtn")) $("cancelFolderBtn").addEventListener("click", () => {
  if ($("folderInputWrap")) $("folderInputWrap").style.display = "none";
});

if ($("saveFolderBtn")) $("saveFolderBtn").addEventListener("click", async () => {
  const newPath = $("destDirInput") ? $("destDirInput").value.trim() : "";
  state.destDir = newPath;
  await api.storage.local.set({ destDir: newPath });
  if ($("folderPathDisplay")) $("folderPathDisplay").textContent = newPath || (state.lastFolder || "Default folder");
  if ($("folderInputWrap")) $("folderInputWrap").style.display = "none";
  showToast("Destination updated!");
});

if ($("copyPathBtn")) $("copyPathBtn").addEventListener("click", () => {
  const path = $("folderPathDisplay") ? $("folderPathDisplay").textContent.trim() : "";
  if (path && path !== "...") {
    navigator.clipboard.writeText(path).then(() => showToast("Folder path copied!"));
  }
});

document.querySelectorAll(".chip-preset").forEach((btn) => {
  btn.addEventListener("click", async () => {
    const preset = btn.dataset.preset;
    let chosenPath = "";
    if (preset === "default") {
      chosenPath = state.defaultServerDir || "";
    } else if (preset === "downloads") {
      chosenPath = state.defaultServerDir ? `${state.defaultServerDir}\\Downloads` : "C:\\Users\\Downloads\\Quarry";
    } else if (preset === "pictures") {
      chosenPath = state.defaultServerDir ? `${state.defaultServerDir}\\Pictures` : "D:\\Pictures\\Archive";
    }

    state.destDir = chosenPath;
    await api.storage.local.set({ destDir: chosenPath });
    if ($("folderPathDisplay")) $("folderPathDisplay").textContent = chosenPath || "Default folder";
    if ($("destDirInput")) $("destDirInput").value = chosenPath;
    showToast(`Set to ${preset}!`);
  });
});

if ($("saveSettingsBtn")) $("saveSettingsBtn").addEventListener("click", async () => {
  const newPort = parseInt($("settingsPort").value, 10) || 8765;
  const newDest = $("destDirInput") ? $("destDirInput").value.trim() : "";
  const maxImg = parseInt($("settingsMaxImages").value, 10) || 0;

  state.port = newPort;
  state.destDir = newDest;
  state.maxImages = maxImg;

  // Site scanning settings (same values the toggles/chips persist live)
  if ($("settingsAutoScan")) state.autoScan = $("settingsAutoScan").checked;
  if ($("settingsScanForce")) state.scanForce = $("settingsScanForce").checked;
  if ($("settingsAskHowMany")) state.askHowMany = $("settingsAskHowMany").checked;
  state.maxScan = sanitizeCount($("settingsMaxScan") ? $("settingsMaxScan").value : state.maxScan, 2000);
  state.askAbove = sanitizeCount($("settingsAskAbove") ? $("settingsAskAbove").value : state.askAbove, 500);

  await api.storage.local.set({ port: newPort, destDir: newDest, maxImages: maxImg });
  await persistScanSettings();
  syncScanChips();
  updateScanRowVisibility();
  updateDownloadButtonLabel();
  if ($("folderPathDisplay")) $("folderPathDisplay").textContent = newDest || (state.lastFolder || "Default folder");
  showToast("Settings saved!");
  checkHealth();
});

if ($("fetchPageBtn")) $("fetchPageBtn").addEventListener("click", triggerDeepScan);

if ($("testConnBtn")) $("testConnBtn").addEventListener("click", checkHealth);
if ($("retryServerBtn")) $("retryServerBtn").addEventListener("click", checkHealth);
if ($("reportSiteBtn")) $("reportSiteBtn").addEventListener("click", reportSiteOrError);
if ($("reportDeveloperBtn")) $("reportDeveloperBtn").addEventListener("click", reportSiteOrError);

init();
