# Usage

## The one command

```powershell
.\quarry.ps1 <url>
```

That is it. The site is detected from the URL; unknown sites fall back to
generic mode (grabs every image/GIF/video the page offers).

### All options

```powershell
.\quarry.ps1 <url> -Out "D:\Media"     # choose the download folder
.\quarry.ps1 <url> -MaxImages 20        # stop after 20 files
.\quarry.ps1 <url> -MaxGalleries 5      # search page: only 5 galleries
.\quarry.ps1 <url> -Workers 4           # slower, politer
.\quarry.ps1 <url> -Delay 1.0           # 1s pause between requests
.\quarry.ps1 <url> -Proxy http://127.0.0.1:7890
.\quarry.ps1 <url> -DryRun              # resolve URLs, download nothing
.\quarry.ps1 <url> -Force               # re-download even completed files
.\quarry.ps1 <url> -Open                # open the folder when it finishes
.\quarry.ps1 <url> -Quiet               # summary only (no per-file lines)
.\quarry.ps1 <url> -Site erome          # advanced: force an adapter
.\quarry.ps1 -File urls.txt             # batch: one URL per line
.\quarry.ps1 -ListSites                 # show supported sites
.\quarry.ps1 -Version                   # print version and exit
.\quarry.ps1 -Help                      # full flag reference
```

`urls.txt` - one URL per line, `#` starts a comment.

PowerShell users can also call the engine directly:
`python main.py <url> [flags]` (same flags with `--`).

## Where files go

Resolution order:

1. `-Out` flag (CLI) or the folder in the extension settings
2. `downloadDir` in `config.json`
3. `<you>\Downloads\Quarry`

Layout - one folder per site, one per gallery, short names:

```
Downloads\Quarry\
|-- imagefap\14340248_mi-3f7c\0001_546949874.jpg ...
|-- erome\abc1234_hot-9f1e\0001_NgyE88io_720p.mp4 ...
|-- rule34\tag_blonde-hair\0001_1001.jpg ...
|-- example.com\some-post-title\0001_photo.jpg ...   <- generic mode
|-- .manifests\...                                   <- bookkeeping (skip list)
`-- .cookies\cookies.json                            <- saved session cookies
```

File extensions are verified against the server's content type and repaired
when wrong (a GIF behind a `.jpg` URL is saved as `.gif`).

### File and folder names

- **Files**: `0001_short-name.ext` - a 4-digit index first (sorts cleanly),
  then at most 16 characters of the site's filename. No usable name means
  `0001.jpg`.
- **Folders**: at most 16 characters. Longer gallery names are cut and get a
  4-character suffix, so two different galleries can never share a folder.
- **`info.txt`**: every gallery folder contains one - site, title, the
  **source URL** (click it to get back to the page), timestamp, result counts
  and the list of files inside. Refreshed on every run.

## Re-running is free

Every gallery keeps a manifest. Run the same command again any time:

- files already downloaded are **skipped**
- failed files are **retried** automatically
- deleted files are downloaded again
- `-Force` redownloads everything

## Listing / search pages

Paste a search, tag, category or profile URL and Quarry crawls the
galleries it links to (capped by `maxGalleries`, default 10):

```powershell
.\quarry.ps1 "https://www.pornpics.com/tags/cosplay/" -MaxGalleries 3
```

## Browser button

1. Start the server: `.\start.ps1` (or install with `-Autostart`)
2. Open any page in Chrome/Firefox
3. Click the extension icon -> **Download this page**
4. Watch the progress; **Open folder** when done

What it does:

- collects media from the live page (including lazy-loaded images the raw
  HTML does not contain)
- forwards your browser cookies, so age-gated / logged-in pages work
- saves to the folder from the extension settings (gear icon)

Closing the popup does not stop the download - reopen it to see the progress.
The badge on the icon shows the file count while running.

## Server API (optional)

For scripts, `http://127.0.0.1:8765` (localhost only; requests from other
websites are rejected):

| Endpoint | Meaning |
|---|---|
| `GET /health` | `{"ok":true,"version":...,"downloadDir":...}` |
| `POST /download` | body `{"url":...,"destDir":...,"cookies":[...],"pageMedia":[...],"maxImages":n}` -> `{"jobId":...}` |
| `GET /status/<jobId>` | progress: `state`, `title`, `folder`, `total`, `downloaded`, `skipped`, `failed`, `error`, `log` |
| `POST /reveal` | body `{"path":"..."}` opens that folder in the file manager |

Next: [sites.md](sites.md) - [setup.md](setup.md) - [../README.md](../../README.md)
