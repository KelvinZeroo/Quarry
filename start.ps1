#Requires -Version 5.1
<#
.SYNOPSIS
    Starts the local server the browser extension talks to (127.0.0.1 only).

.EXAMPLE
    .\start.ps1              # run in this window (logs visible, Ctrl+C stops)
    .\start.ps1 -NewWindow   # run in a separate window
#>
[CmdletBinding()]
param(
    [switch]$NewWindow
)

$ErrorActionPreference = "Stop"

# --- find Python ---
$python = $null
foreach ($name in @("python", "python3", "py")) {
    $cmd = Get-Command $name -ErrorAction SilentlyContinue
    if ($cmd) { $python = $cmd.Source; break }
}
if (-not $python) {
    Write-Host "Python not found. Install Python 3.10+ from https://www.python.org/downloads/" -ForegroundColor Red
    exit 1
}

# --- read port from config.json (fallback 8765) ---
$port = 8765
$configPath = Join-Path $PSScriptRoot "config.json"
if (Test-Path $configPath) {
    try {
        $cfg = Get-Content $configPath -Raw | ConvertFrom-Json
        if ($cfg.port) { $port = [int]$cfg.port }
    } catch { }
}

$server = Join-Path $PSScriptRoot "server.py"

if ($NewWindow) {
    Start-Process -FilePath $python -ArgumentList "`"$server`"" -WorkingDirectory $PSScriptRoot
    Write-Host "Server starting in a new window: http://127.0.0.1:$port" -ForegroundColor Green
} else {
    Write-Host "Quarry server: http://127.0.0.1:$port  (Ctrl+C to stop)" -ForegroundColor Green
    & $python $server
    exit $LASTEXITCODE
}
