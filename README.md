# Quarry — Native Media Downloader & Archiver (.NET 10)

Quarry is a standalone native Windows desktop application built with **C# and .NET 10 (WPF)** designed in the classic **Internet Download Manager (IDM)** style.

It has **zero browser dependencies** (no Chrome, Edge, Electron, Node.js, or WebViews).

---

## ⚡ Features

- **100% Standalone Native Desktop App**: Fast, lightweight WPF GUI with classic IDM layout (Menu Bar, Action Ribbon, Category Tree, Sharp DataGrid).
- **Clipboard Auto-Catch**: Automatically monitors the Windows clipboard and pops up an Add Download dialog when a supported media link is copied.
- **Site Grabber**: Multi-depth crawl wizard and XML sitemap scanner to bulk archive whole galleries or websites.
- **High Performance Downloader**: Multi-threaded async engine with SHA-256 deduplication and `.quarry_manifest.json` metadata tracking.
- **Embedded Local Server**: Built-in HTTP server listening on `127.0.0.1:8765` for 1-click browser extension capture.
- **Site Adapters Included**:
  - EroMe (Albums, Photos & HD Videos)
  - PornPics (Galleries & Search Tags)
  - Booru / Rule34 (Tag Search & High-Res Posts)
  - ImageFap (Galleries & Albums)
  - CreateAIAsian (Posts & Sitemaps)
  - Instagram (Media Scraping)
  - Generic / Fallback (Universal HTML page image & video extractor)

---

## 🚀 Quick Start

### Option 1: Run the Standalone App
Double-click `Quarry.bat` or run:
```powershell
.\Start-Quarry.ps1
```
Or directly launch:
```text
QuarryApp\bin\Publish\QuarryApp.exe
```

### Option 2: Build & Run from Source (.NET 10 SDK)
```powershell
dotnet run --project ./QuarryApp/QuarryApp.csproj
```

---

## 📁 Project Structure

```
Quarry/
├── QuarryApp/                    # Complete C# .NET 10 Application
│   ├── Adapters/                 # Site scrapers & parsers
│   ├── Core/                     # DownloadEngine, HttpClient, Config, Scanner
│   ├── Models/                   # DownloadJob, Gallery, ImageItem, Settings
│   ├── Server/                   # LocalApiServer (127.0.0.1:8765)
│   ├── Windows/                  # AddDownloadDialog, SiteGrabberDialog
│   ├── MainWindow.xaml           # IDM UI View
│   ├── MainWindow.xaml.cs        # IDM UI Controller & Logic
│   └── QuarryApp.csproj          # .NET 10 Project File
├── extension/                    # Browser extension for 1-click send to Quarry
├── Start-Quarry.ps1              # 1-Click Launch Script
└── Quarry.bat                    # Windows Batch Launcher
```
