#Requires -Version 5.1
<#
.SYNOPSIS
    One-time setup: checks Python, installs dependencies, creates config.json,
    optionally adds the server to Windows startup.

.EXAMPLE
    .\install.ps1              # check + install dependencies
    .\install.ps1 -Autostart   # also start the server automatically at logon
#>
[CmdletBinding()]
param(
    [switch]$Autostart,
    [switch]$SkipDeps
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$failed = $false

Write-Host "Quarry setup" -ForegroundColor Cyan
Write-Host ("=" * 40)

# --- 1. Python check ---
$python = $null
foreach ($name in @("python", "python3", "py")) {
    $cmd = Get-Command $name -ErrorAction SilentlyContinue
    if ($cmd) { $python = $cmd.Source; break }
}
if (-not $python) {
    Write-Host "[X] Python not found." -ForegroundColor Red
    Write-Host "    Install Python 3.10+ from https://www.python.org/downloads/" -ForegroundColor Yellow
    Write-Host "    (tick 'Add python.exe to PATH' during install), then re-run this script."
    exit 1
}
$ver = & $python -c "import sys; print('%d.%d' % sys.version_info[:2])" 2>$null
$verOk = $false
try {
    $parts = $ver.Split(".")
    $verOk = ([int]$parts[0] -gt 3) -or ([int]$parts[0] -eq 3 -and [int]$parts[1] -ge 10)
} catch { }
if ($verOk) {
    Write-Host "[OK] Python $ver ($python)" -ForegroundColor Green
} else {
    Write-Host "[X] Python $ver is too old - 3.10+ required." -ForegroundColor Red
    exit 1
}

# --- 2. Dependencies ---
if ($SkipDeps) {
    Write-Host "[--] Dependency install skipped (-SkipDeps)" -ForegroundColor Yellow
} else {
    $req = Join-Path $root "requirements.txt"
    Write-Host "    Installing dependencies (httpx, beautifulsoup4, lxml)..."
    & $python -m pip install --disable-pip-version-check -r $req
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[X] pip install failed." -ForegroundColor Red
        $failed = $true
    } else {
        Write-Host "[OK] Dependencies installed" -ForegroundColor Green
    }
}

# --- 3. config.json ---
$cfgPath = Join-Path $root "config.json"
if (Test-Path $cfgPath) {
    Write-Host "[OK] config.json present" -ForegroundColor Green
} else {
    $defaultCfg = @'
{
  "downloadDir": "",
  "port": 8765,
  "workers": 8,
  "delay": 0.2,
  "maxImages": 0,
  "maxGalleries": 10,
  "proxy": ""
}
'@
    Set-Content -Path $cfgPath -Value $defaultCfg -Encoding UTF8
    Write-Host "[OK] config.json created" -ForegroundColor Green
}

# --- 4. Optional: start server at logon ---
if ($Autostart) {
    try {
        $startup = [Environment]::GetFolderPath("Startup")
        $lnkPath = Join-Path $startup "Quarry Server.lnk"
        $wsh = New-Object -ComObject WScript.Shell
        $lnk = $wsh.CreateShortcut($lnkPath)
        $lnk.TargetPath = "powershell.exe"
        $lnk.Arguments = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$root\start.ps1`""
        $lnk.WorkingDirectory = $root
        $lnk.Description = "Quarry local server"
        $lnk.Save()
        Write-Host "[OK] Server will start at logon (Startup shortcut)" -ForegroundColor Green
    } catch {
        Write-Host "[X] Could not create startup shortcut: $($_.Exception.Message)" -ForegroundColor Red
        $failed = $true
    }
}

# --- done ---
Write-Host ("=" * 40)
if ($failed) {
    Write-Host "Setup finished with errors - see above." -ForegroundColor Red
    exit 1
}
Write-Host "Setup complete. Next steps:" -ForegroundColor Cyan
Write-Host "  1. .\start.ps1              (start the local server)"
Write-Host "  2. Load the extension       (see docs\setup.md)"
Write-Host "  3. .\quarry.ps1 <url>     (or just use the browser button)"
