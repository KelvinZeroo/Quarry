#Requires -Version 5.1
<#
.SYNOPSIS
    The one universal download command.

.EXAMPLE
    .\quarry.ps1 "https://www.erome.com/a/abc123"
    .\quarry.ps1 "https://www.pornpics.com/galleries/example-12345/" -Out "D:\Media"
    .\quarry.ps1 -File urls.txt -MaxImages 20 -DryRun
    .\quarry.ps1 "https://www.erome.com/a/abc123" -Open -Quiet
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Url,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Rest,

    [Alias("Out", "o")]
    [string]$OutputDir,

    [Alias("f")]
    [string]$File,

    [Alias("m")]
    [int]$MaxImages,

    [int]$MaxGalleries,

    [Alias("w")]
    [int]$Workers,

    [Alias("d")]
    [double]$Delay,

    [string]$Proxy,

    [switch]$DryRun,

    [switch]$Force,

    [switch]$ListSites,

    [switch]$Open,

    [switch]$Quiet,

    [switch]$Version,

    [string]$Site,

    [switch]$Help
)

$ErrorActionPreference = "Stop"

# --- find Python (python / python3 / py launcher) ---
$python = $null
foreach ($name in @("python", "python3", "py")) {
    $cmd = Get-Command $name -ErrorAction SilentlyContinue
    if ($cmd) { $python = $cmd.Source; break }
}
if (-not $python) {
    Write-Host "Python not found. Install Python 3.10+ from https://www.python.org/downloads/" -ForegroundColor Red
    exit 1
}

# --- build the argument list for main.py ---
$pyArgs = @((Join-Path $PSScriptRoot "main.py"))
if ($Help) { $pyArgs += "--help"; & $python @pyArgs; exit $LASTEXITCODE }
if ($Version) { $pyArgs += "--version"; & $python @pyArgs; exit $LASTEXITCODE }
if ($ListSites) { $pyArgs += "--list-sites" }
if ($Site) { $pyArgs += @("--site", $Site) }
if ($Url) { $pyArgs += $Url }
if ($Rest) { $pyArgs += $Rest }
if ($File) { $pyArgs += @("--file", $File) }
if ($PSBoundParameters.ContainsKey("OutputDir")) { $pyArgs += @("--out", $OutputDir) }
if ($PSBoundParameters.ContainsKey("MaxImages")) { $pyArgs += @("--max-images", $MaxImages) }
if ($PSBoundParameters.ContainsKey("MaxGalleries")) { $pyArgs += @("--max-galleries", $MaxGalleries) }
if ($PSBoundParameters.ContainsKey("Workers")) { $pyArgs += @("--workers", $Workers) }
if ($PSBoundParameters.ContainsKey("Delay")) { $pyArgs += @("--delay", $Delay) }
if ($Proxy) { $pyArgs += @("--proxy", $Proxy) }
if ($DryRun) { $pyArgs += "--dry-run" }
if ($Force) { $pyArgs += "--force" }
if ($Open) { $pyArgs += "--open" }
if ($Quiet) { $pyArgs += "--quiet" }

& $python @pyArgs
exit $LASTEXITCODE
