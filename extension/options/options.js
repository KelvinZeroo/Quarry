// Quarry options controller.
"use strict";

const api = typeof browser !== "undefined" ? browser : chrome;
const $ = (id) => document.getElementById(id);

function setStatus(text, isErr = false) {
  const el = $("saveStatus");
  el.textContent = text;
  el.className = isErr ? "save-status err" : "save-status";
  if (text) setTimeout(() => { el.textContent = ""; }, 2500);
}

function applyTheme(themeName) {
  let effectiveTheme = themeName;
  if (themeName === "system") {
    effectiveTheme = window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
  }
  document.documentElement.setAttribute("data-theme", effectiveTheme);
}

async function checkHealth() {
  const badge = $("statusBadge");
  const text = $("statusText");
  const port = parseInt($("port").value, 10) || 8765;

  text.textContent = "Connecting...";

  try {
    const res = await fetch(`http://127.0.0.1:${port}/health`, {
      signal: AbortSignal.timeout ? AbortSignal.timeout(3000) : undefined,
    });
    const data = await res.json();
    if (data.ok) {
      badge.className = "status-badge online";
      text.textContent = `Online (v${data.version})`;
      if (!$("destDir").value && data.downloadDir) {
        $("destHint").textContent = `Default: ${data.downloadDir}`;
      }
      return;
    }
    throw new Error("Invalid response");
  } catch {
    badge.className = "status-badge offline";
    text.textContent = "Offline";
  }
}

function syncScanChips(value) {
  document.querySelectorAll(".chip-opt").forEach((chip) => {
    chip.classList.toggle("active", parseInt(chip.dataset.cap, 10) === value);
  });
}

async function init() {
  const stored = await api.storage.local.get({
    destDir: "",
    port: 8765,
    maxImages: 0,
    autoScan: true,
    scanForce: false,
    askHowMany: true,
    maxScan: 2000,
    askAbove: 500,
    theme: "light",
    alwaysShowCarousel: true,
  });

  $("destDir").value = stored.destDir || "";
  $("port").value = stored.port || 8765;
  $("maxImages").value = stored.maxImages || 0;
  if ($("alwaysShowCarousel")) $("alwaysShowCarousel").checked = stored.alwaysShowCarousel !== false;
  if ($("themeSelect")) $("themeSelect").value = stored.theme || "light";

  // Site scanning settings (same storage keys as the popup)
  if ($("autoScan")) $("autoScan").checked = stored.autoScan !== false;
  if ($("scanForce")) $("scanForce").checked = !!stored.scanForce;
  if ($("askHowMany")) $("askHowMany").checked = stored.askHowMany !== false;
  const maxScan = Number.isFinite(parseInt(stored.maxScan, 10)) ? parseInt(stored.maxScan, 10) : 2000;
  const askAbove = Number.isFinite(parseInt(stored.askAbove, 10)) ? parseInt(stored.askAbove, 10) : 500;
  if ($("maxScan")) $("maxScan").value = maxScan || "";
  if ($("askAbove")) $("askAbove").value = askAbove || "";
  syncScanChips(maxScan);

  applyTheme(stored.theme || "light");
  await checkHealth();
}

async function save(e) {
  if (e) e.preventDefault();

  const newPort = parseInt($("port").value, 10);
  const destDir = $("destDir").value.trim();
  const maxImages = parseInt($("maxImages").value, 10) || 0;
  const alwaysShowCarousel = $("alwaysShowCarousel") ? $("alwaysShowCarousel").checked : true;
  const theme = $("themeSelect") ? $("themeSelect").value : "light";

  // Site scanning settings
  const autoScan = $("autoScan") ? $("autoScan").checked : true;
  const scanForce = $("scanForce") ? $("scanForce").checked : false;
  const askHowMany = $("askHowMany") ? $("askHowMany").checked : true;
  const maxScanV = parseInt($("maxScan") ? $("maxScan").value : "", 10);
  const askAboveV = parseInt($("askAbove") ? $("askAbove").value : "", 10);
  const maxScan = Number.isFinite(maxScanV) && maxScanV >= 0 ? maxScanV : 2000;
  const askAbove = Number.isFinite(askAboveV) && askAboveV >= 0 ? askAboveV : 500;

  if (!newPort || newPort < 1 || newPort > 65535) {
    setStatus("Port must be 1-65535", true);
    return;
  }

  if (destDir && !/^[a-zA-Z]:[\\/]/.test(destDir) && !destDir.startsWith("\\\\") && !destDir.startsWith("/")) {
    setStatus("Enter a valid full path", true);
    return;
  }

  await api.storage.local.set({
    destDir,
    port: newPort,
    maxImages,
    autoScan,
    scanForce,
    askHowMany,
    maxScan,
    askAbove,
    alwaysShowCarousel,
    theme,
  });

  applyTheme(theme);
  setStatus("Saved");
  checkHealth();
}

async function reset() {
  await api.storage.local.remove([
    "destDir",
    "port",
    "maxImages",
    "autoScan",
    "scanForce",
    "askHowMany",
    "maxScan",
    "askAbove",
    "alwaysShowCarousel",
    "theme",
  ]);
  await init();
  setStatus("Defaults restored");
}

$("settingsForm").addEventListener("submit", save);
$("resetBtn").addEventListener("click", reset);
$("port").addEventListener("change", checkHealth);
if ($("themeSelect")) $("themeSelect").addEventListener("change", (e) => applyTheme(e.target.value));

// Scan limit chips only fill the number field - the value is saved with the
// form (Save Settings), keeping the options page's explicit-save model.
document.querySelectorAll(".chip-opt").forEach((chip) => {
  chip.addEventListener("click", () => {
    const cap = parseInt(chip.dataset.cap, 10) || 0;
    if ($("maxScan")) $("maxScan").value = cap || "";
    syncScanChips(cap);
  });
});
if ($("maxScan")) {
  $("maxScan").addEventListener("change", () => {
    syncScanChips(parseInt($("maxScan").value, 10));
  });
}

init();
