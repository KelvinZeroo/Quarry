# Supported sites

| Site | URL types | Notes |
|---|---|---|
| **ImageFap** | `gallery.php?gid=`, `/gallery/N`, `/pictures/...`, `/photo/N`, searches, profiles | full-size originals; expired signed links are re-minted automatically on 403 |
| **PornPics** | `/galleries/<slug>-<gid>/`, tags, channels, models, searches | thumbnails upgraded to 1280px originals, promos filtered |
| **EroMe** | `/a/<album>`, users, searches | images **and** videos |
| **Rule34** | `tags=` searches, `s=view&id=N` single posts | scraped from the site's HTML pages (its JSON API now needs a key); one tag = one folder, up to 20 result pages |
| **Everything else** | any `http(s)` page | generic mode (see below) |

A URL that is not a single gallery (search / tag / category / profile page)
is crawled for galleries, up to `maxGalleries` (config, default 10, override
with `-MaxGalleries`).

Unsupported booru-style sites (Gelbooru, Danbooru, ...) are handled by
generic mode - usually fine for a single page, but it cannot page through
their search results.

## Generic mode

For pages no adapter knows, Quarry downloads what the page actually
shows: `<img>` sources, the largest `srcset` candidate, `og:image`,
`<video>`/`<source>` files and links ending in a media extension. Junk
(logos, avatars, icons, SVG/CSS/JS) is filtered out.

The browser extension improves this: it also sends media it finds in the
**live DOM**, so lazy-loaded images work even when the raw HTML does not
contain them.

### Limitations (on purpose - it stays simple)

- **No logins or captchas from the CLI.** Open the page in your browser and
  use the extension button - it forwards your cookies. CLI runs reuse saved
  cookies from `.cookies\cookies.json`.
- **Only what the page exposes.** Media that exists solely behind a signed
  API or a JS player (never in the HTML or DOM) cannot be found.
- **No HLS/DASH assembly.** `.m3u8` playlists and split video chunks are not
  merged into one file.
- **No deep site search.** Paste the URL of the page you are on; the CLI does
  not query site search for you.

## Per-site tips

- **ImageFap** - any gallery/photo/search/profile URL works. Signed CDN
  tokens expire mid-run; the engine refreshes them and retries once.
- **PornPics** - `/galleries/...` is a gallery, everything else (tags,
  `?q=` searches) is crawled as a listing.
- **EroMe** - `/a/<album>` URLs; user pages and searches are crawled.
- **Rule34** - `index.php?page=post&s=list&tags=cosplay` downloads the whole
  tag; `page=post&s=view&id=N` downloads that one post. Use `-MaxImages` for
  big tags.

## If a site breaks

Sites change their layout now and then. Symptoms and what they mean:

| Log message | Meaning |
|---|---|
| `page did not contain expected content` | block (captcha/VPN) or layout change |
| `no photos found in gallery` | layout change in the gallery header |
| `warning: resolved X of Y images` | gallery page exposed only part of the list (window/pagination change) |
| `no media found on this page` | generic mode found nothing - page needs JS/login |

Open an issue with the URL (tests use saved page fixtures in `tests/`).

Next: [usage.md](usage.md) - [setup.md](setup.md) - [../README.md](../../README.md)
