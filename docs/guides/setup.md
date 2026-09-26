# Setup

Everything runs locally on your machine.

## 1. Requirements

- Windows with PowerShell (5.1 or later)
- Python 3.10+ - tick **Add python.exe to PATH** when installing

## 2. Install

```powershell
.\install.ps1
```

Checks Python, installs the dependencies (`httpx`, `beautifulsoup4`, `lxml`)
and makes sure `config.json` exists.

Add `-Autostart` to start the server automatically when you log in:

```powershell
.\install.ps1 -Autostart
```

## 3. Configuration

`config.json` (next to `quarry.ps1`):

```json
{
  "downloadDir": "",
  "port": 8765,
  "workers": 8,
  "delay": 0.2,
  "maxImages": 0,
  "maxGalleries": 10,
  "maxScan": 2000,
  "askAbove": 500,
  "proxy": ""
}
```

| Key | Meaning |
|---|---|
| `downloadDir` | Where files go. Empty = `<you>\Downloads\Quarry`. `-Out` and the extension settings override this. |
| `port` | Local server port. Must match the extension setting. |
| `workers` | Parallel file downloads. |
| `delay` | Minimum pause between requests to the same site (seconds). |
| `maxImages` | Cap files per gallery (`0` = no cap). |
| `maxGalleries` | Cap galleries crawled from a search/tag page. |
| `maxScan` | How many files a site-wide scan may discover (`0` = no cap). Flag: `-MaxScan`. |
| `askAbove` | Ask "really ALL?" when a scan found more files than this (`0` = never). Flag: `-AskAbove`. |
| `proxy` | HTTP or SOCKS5 proxy URL, empty = none. |

## 4. Browser extension

The extension talks to a small local server - start it first:

```powershell
.\start.ps1            # logs visible; Ctrl+C stops it
.\start.ps1 -NewWindow # runs in its own window
```

The green dot in the popup means everything is connected.

### Chrome / Edge

1. Open `chrome://extensions`
2. Turn on **Developer mode**
3. Click **Load unpacked** and select the `extension` folder
4. Pin the Quarry icon to the toolbar

### Firefox

1. Open `about:debugging#/runtime/this-firefox`
2. Click **Load Temporary Add-on...** and select `extension/manifest.json`
3. The icon appears in the toolbar

> Firefox does not keep unsigned extensions after a restart. Load it again from
> `about:debugging`, or use a Developer/ESR build with
> `xpinstall.signatures.required=false` for a permanent install.

### Extension settings

Right-click the extension icon -> **Options** (or *Settings* in the popup):

- **Download folder** - where the browser button saves (empty = server default)
- **Server port** - must match `config.json`
- **Max files per download** - `0` = everything
- **Site scanning** - the same five options in the popup's Settings tab and
  on the Options page (both share one storage key):
  - *Scan whole site first (site roots)* - on by default, mirrors the CLI's
    interactive scan
  - *Also force-scan deep links* - CLI `--scan`
  - *Ask "how many?" before downloading* - shows the question in the popup
  - *Scan limit* - preset chips (`100 / 500 / 1,000 / 2,000 / 10,000 /
    Unlimited`) plus a custom number; the scan stops at this many files
  - *Confirm "download ALL" above* - second confirmation for large scans
    (`0` = never ask)

The popup's **Images** tab downloads images and videos from the current page
(and the whole site when scanning is on). The **Video** tab (YouTube and
other dedicated video sites) is coming soon.

## Troubleshooting

| Symptom | Fix |
|---|---|
| Red dot in the popup | Server is not running -> `.\start.ps1` |
| "Server offline" in options | Same as above, or wrong port in settings |
| `Python not found` | Install Python 3.10+ and enable *Add to PATH*, re-run `.\install.ps1` |
| HTTP 403 / "blocked" | The site wants a logged-in session. Open the page in your browser, or use the extension button - it forwards your browser cookies. |
| "no media found" | See [sites.md](sites.md) - generic mode only sees what the page shows. |

Next: [usage.md](usage.md) - [sites.md](sites.md) - [../README.md](../../README.md)
