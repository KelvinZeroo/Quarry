# Launch Quarry .NET 10 Desktop Application
$publishExe = "$PSScriptRoot\QuarryApp\bin\Publish\QuarryApp.exe"
$releaseExe = "$PSScriptRoot\QuarryApp\bin\Release\QuarryApp.exe"
$debugExe   = "$PSScriptRoot\QuarryApp\bin\Debug\net10.0-windows\QuarryApp.exe"

if (Test-Path $publishExe) {
    Start-Process $publishExe
} elseif (Test-Path $releaseExe) {
    Start-Process $releaseExe
} elseif (Test-Path $debugExe) {
    Start-Process $debugExe
} else {
    Write-Host "Building QuarryApp..." -ForegroundColor Cyan
    dotnet run --project "$PSScriptRoot\QuarryApp\QuarryApp.csproj"
}
