# Quarry

Download images, GIFs and videos from any page - from PowerShell or straight
from your browser.

- **One universal command** - the site is detected automatically, no per-site flags
- **Browser extension** for Chrome and Firefox: open a page, press *Download*
- **Clean output** - short folders, files named `0001_...`, plus an
  `info.txt` in every folder with the source link
- **Re-running is free** - finished files are skipped, failed ones retried
- **Works everywhere** - ImageFap, PornPics, EroMe, Rule34, and any other page
  (best effort, see [docs/guides/sites.md](docs/guides/sites.md))

## Quick start

```powershell
.\install.ps1              # checks Python, installs dependencies
.\quarry.ps1 <url>         # download any gallery or page
```

`.\download.ps1` is kept as an alias of `quarry.ps1`.

For the browser button:

```powershell
.\start.ps1               # local server the extension talks to
```

Then load `extension/` into Chrome or Firefox - [docs/guides/setup.md](docs/guides/setup.md).

## Examples

```powershell
.\quarry.ps1 "https://www.erome.com/a/abc1234"
.\quarry.ps1 "https://www.pornpics.com/galleries/example-12345/" -Out "D:\Media"
.\quarry.ps1 "https://www.imagefap.com/search/cats" -MaxGalleries 5
.\quarry.ps1 -File urls.txt -DryRun
```

## Documentation

| | |
|---|---|
| [docs/guides/setup.md](docs/guides/setup.md) | install, config, browser extension |
| [docs/guides/usage.md](docs/guides/usage.md) | CLI flags, folders, browser button |
| [docs/guides/sites.md](docs/guides/sites.md) | supported sites and limits |

Website (open `docs/index.html` in a browser): Home, Install, Usage and
Extension pages in `docs/`.

Settings live in `config.json` (`downloadDir`, `port`, `workers`, `delay`).

---

**Personal use only.** You are responsible for what you download and where you
store it. Respect the terms of service and copyright of every site you use.
