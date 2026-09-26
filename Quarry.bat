@echo off
if exist "%~dp0QuarryApp\bin\Publish\QuarryApp.exe" (
    start "" "%~dp0QuarryApp\bin\Publish\QuarryApp.exe"
) else if exist "%~dp0QuarryApp\bin\Release\QuarryApp.exe" (
    start "" "%~dp0QuarryApp\bin\Release\QuarryApp.exe"
) else (
    dotnet run --project "%~dp0QuarryApp\QuarryApp.csproj"
)
