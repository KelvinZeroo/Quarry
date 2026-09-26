// Quarry content script - passive high-resolution DOM media scanner and sleek header integration.
(() => {
  "use strict";

  if (window.__quarry_content_injected) return;
  window.__quarry_content_injected = true;

  // Explicitly purge any legacy overlay elements, floating buttons or panel styles from the page DOM
  const purgeOverlayElements = () => {
    const idsToPurge = [
      "quarry-floating-btn",
      "quarry-overlay-container",
      "quarry-mini-panel",
      "quarry-overlay-styles",
      "quarry-panel-styles",
      "quarry-floating-root",
      "quarry-profile-action-wrap",
      "quarry-profile-btn",
    ];
    idsToPurge.forEach((id) => {
      const el = document.getElementById(id);
      if (el) el.remove();
    });

    document.querySelectorAll(".quarry-post-dl-btn, .quarry-profile-dl-btn, .quarry-ig-profile-btn-wrap").forEach((el) => {
      el.remove();
    });

    document.querySelectorAll("button, div, a").forEach((el) => {
      const text = el.textContent || "";
      if (
        text.includes("Quarry Overlay") ||
        (el.id && el.id.includes("quarry-overlay")) ||
        (el.className && typeof el.className === "string" && el.className.includes("quarry-overlay-btn"))
      ) {
        el.remove();
      }
    });
  };
  purgeOverlayElements();

  const MEDIA_RE = /\.(jpe?g|png|gif|webp|avif|bmp|tiff?|mp4|webm|m4v)([?#]|$)/i;
  const SKIP_RE = /\.(svg|css|js|mjs|json|xml|html?|ico|woff2?|ttf|eot)([?#]|$)/i;
  const JUNK_RE = /avatar|logo|sprite|placeholder|spinner|1x1|pixel|loading|favicon|tracking|analytics/i;
  const STYLE_URL_RE = /url\(\s*['"]?([^'")]+)['"]?\s*\)/gi;

  const state = {
    mediaList: [],
    isInstagram: false,
    instagramUser: "",
    meta: {},
    siteName: "",
    serverPort: 8765,
    destDir: "",
  };

  const absolute = (u) => {
    if (!u || typeof u !== "string") return null;
    const s = u.trim().replace(/&amp;/g, "&");
    if (!s || s.startsWith("data:") || s.startsWith("blob:") || s.startsWith("javascript:")) return null;
    try {
      const a = new URL(s, location.href);
      if (a.protocol !== "http:" && a.protocol !== "https:") return null;
      a.hash = "";
      const path = a.pathname;
      if (SKIP_RE.test(path)) return null;
      return a.href;
    } catch {
      return null;
    }
  };

  const bestFromSrcset = (ss) => {
    if (!ss || typeof ss !== "string") return null;
    let best = null, bestW = -1, last = null;
    for (const entry of ss.split(",")) {
      const parts = entry.trim().split(/\s+/);
      if (!parts[0]) continue;
      last = parts[0];
      if (parts[1] && /w$/i.test(parts[1])) {
        const w = parseFloat(parts[1]);
        if (!isNaN(w) && w > bestW) { best = parts[0]; bestW = w; }
      }
    }
    return best || last;
  };

  const detectSite = () => {
    const host = location.hostname.replace(/^www\./i, "").toLowerCase();
    const isInsta = host.includes("instagram.com");
    let username = "";
    let fullName = "";
    let stats = "";
    let bio = "";
    let avatar = "";

    if (isInsta) {
      const parts = location.pathname.split("/").filter(Boolean);
      if (parts.length > 0) {
        const first = parts[0].toLowerCase();
        if (!["p", "reel", "reels", "stories", "explore", "direct", "accounts", "emails"].includes(first)) {
          username = parts[0];
        }
      }
      if (!username) {
        const h2 = document.querySelector("header h2, header h1, h2");
        if (h2 && h2.textContent) username = h2.textContent.trim().replace(/^@/, "");
      }

      const h1 = document.querySelector("header h1, header section h2");
      if (h1 && h1.textContent && h1.textContent.trim() !== username) {
        fullName = h1.textContent.trim();
      }

      const bioContainer = document.querySelector("header section > div:last-child, header div[style*='-webkit-line-clamp'], header div._aa_c");
      if (bioContainer) {
        bio = bioContainer.innerText ? bioContainer.innerText.trim().replace(/\n+/g, " · ") : "";
      }

      const statItems = document.querySelectorAll("header ul li, header section ul li");
      if (statItems.length > 0) {
        const statTexts = Array.from(statItems).map((li) => li.textContent.trim()).filter(Boolean);
        if (statTexts.length > 0) {
          stats = statTexts.join(" · ");
        }
      }

      const avImg = document.querySelector(
        'header img[alt*="profile" i], header img[alt*="photo" i], header img[alt*="picture" i], header img, img[alt*="profile picture" i], header div[role="button"] img, header span[role="link"] img'
      );
      if (avImg) {
        const avSrcset = avImg.getAttribute("srcset");
        const bestAv = avSrcset ? bestFromSrcset(avSrcset) : null;
        avatar = bestAv || avImg.getAttribute("src") || avImg.getAttribute("data-src") || "";
      }
    }

    const domainLabel = isInsta ? (username ? `Instagram: @${username}` : "Instagram") : host.toUpperCase();
    return {
      isInsta,
      username,
      fullName,
      stats,
      bio,
      avatar,
      siteName: domainLabel,
    };
  };

  const collectMedia = () => {
    if (!window.__quarry_media_cache) {
      window.__quarry_media_cache = new Map();
      window.__quarry_last_path = location.pathname;
    }

    // If navigated to another profile/page, reset cache
    if (window.__quarry_last_path !== location.pathname) {
      window.__quarry_media_cache.clear();
      window.__quarry_last_path = location.pathname;
    }

    const urlMap = new Map(window.__quarry_media_cache);

    const add = (rawUrl, type = "image", title = "", thumb = null) => {
      const u = absolute(rawUrl);
      if (!u) return;
      if (urlMap.has(u)) return;

      const isJunk = !state.isInstagram && JUNK_RE.test(new URL(u).pathname);
      if (isJunk) return;

      const item = {
        url: u,
        thumbUrl: thumb ? absolute(thumb) || u : u,
        type: type || "image",
        title: title || "",
        selected: true,
      };
      urlMap.set(u, item);
      window.__quarry_media_cache.set(u, item);
    };

    const siteInfo = detectSite();
    state.isInstagram = siteInfo.isInsta;
    state.instagramUser = siteInfo.username;
    state.siteName = siteInfo.siteName;
    state.meta = siteInfo;

    // 1. Instagram Smart Extraction
    if (siteInfo.isInsta) {
      if (siteInfo.avatar) {
        add(siteInfo.avatar, "image", `${siteInfo.username || "Instagram"} Profile Photo`, siteInfo.avatar);
      }

      // Check posts, reels, carousel slides, dialogs
      const postContainers = document.querySelectorAll(
        "main article, main a[href*='/p/'], main a[href*='/reel/'], div[role='dialog'] article, div[role='dialog']"
      );

      postContainers.forEach((post) => {
        if (post.closest("header") || post.closest("ul[class*='_aak']") || post.closest("div[aria-label*='Highlights' i]")) {
          return;
        }

        const isReel = post.matches("a[href*='/reel/']") ||
          !!post.querySelector("a[href*='/reel/']") ||
          !!post.querySelector("svg[aria-label*='Clip' i], svg[aria-label*='Video' i], svg[aria-label*='Reel' i]");

        // Direct video elements
        post.querySelectorAll("video, source").forEach((el) => {
          const vsrc = el.getAttribute("src") || (el.querySelector && el.querySelector("source") && el.querySelector("source").getAttribute("src"));
          const poster = el.getAttribute("poster") || el.poster;
          if (vsrc && !vsrc.startsWith("blob:")) {
            add(vsrc, "video", "Instagram Video", poster || vsrc);
          } else if (poster) {
            add(poster, "video", "Instagram Video", poster);
          }
        });

        // Post images & thumbnails
        post.querySelectorAll("img").forEach((el) => {
          const rect = el.getBoundingClientRect();
          if (rect.width > 0 && rect.width <= 100 && rect.height > 0 && rect.height <= 100 && !el.closest("article") && !el.closest("a[href*='/p/']")) {
            return;
          }

          const ss = el.getAttribute("srcset");
          const best = ss ? bestFromSrcset(ss) : null;
          const src = best || el.currentSrc || el.getAttribute("src") || el.getAttribute("data-src");
          if (src && !src.startsWith("data:") && !src.startsWith("blob:")) {
            const alt = el.getAttribute("alt") || (isReel ? "Instagram Reel" : "Instagram Post");
            if (!/highlight|story cover/i.test(alt)) {
              add(src, isReel ? "video" : "image", alt, src);
            }
          }
        });
      });
    }

    // 2. Standard Universal Media
    if (!siteInfo.isInsta) {
      document.querySelectorAll("img, picture source").forEach((el) => {
        const ss = el.getAttribute("srcset") || el.getAttribute("data-srcset");
        const best = ss ? bestFromSrcset(ss) : null;
        const src = best ||
          el.getAttribute("src") ||
          el.getAttribute("data-src") ||
          el.getAttribute("data-original") ||
          el.getAttribute("data-lazy") ||
          el.getAttribute("data-highres") ||
          el.getAttribute("data-zoom-image") ||
          el.getAttribute("data-full-url");
        const alt = el.getAttribute("alt") || el.getAttribute("title") || "";
        if (src) add(src, "image", alt);
      });

      // 3. Videos
      document.querySelectorAll("video, video source").forEach((el) => {
        const src = el.getAttribute("src") || el.getAttribute("data-src");
        const poster = el.getAttribute("poster");
        if (src) add(src, "video", "Video", poster);
      });

      // 4. Background images in styles
      document.querySelectorAll("[style*='url('], [style*='url (']").forEach((el) => {
        const style = el.getAttribute("style") || "";
        let m;
        STYLE_URL_RE.lastIndex = 0;
        while ((m = STYLE_URL_RE.exec(style)) !== null) {
          add(m[1], "image", "Background Image");
        }
      });
    }

    // 5. OpenGraph & Twitter tags
    const metaProps = ["og:image", "og:image:secure_url", "og:video", "og:video:secure_url", "twitter:image", "twitter:image:src"];
    metaProps.forEach((prop) => {
      const meta = document.querySelector(`meta[property="${prop}"], meta[name="${prop}"]`);
      if (meta && meta.getAttribute("content")) {
        const type = prop.includes("video") ? "video" : "image";
        add(meta.getAttribute("content"), type, "Meta Media");
      }
    });

    // 6. Direct media links
    document.querySelectorAll("a[href]").forEach((a) => {
      const href = a.getAttribute("href");
      if (href && MEDIA_RE.test(href)) {
        const type = /\.(mp4|webm|m4v)/i.test(href) ? "video" : "image";
        add(href, type, a.textContent.trim() || "Linked Media");
      }
    });

    const list = Array.from(urlMap.values());
    state.mediaList = list;
    return list;
  };

  const fetchPostCarouselMedia = async (postUrl) => {
    try {
      const res = await fetch(postUrl, { credentials: "include" });
      if (!res.ok) return [];
      const text = await res.text();
      const results = [];

      // Regex for image/video URLs inside JSON or HTML scripts
      const regex = /"(?:display_url|video_url|src)":\s*"([^"]+)"/g;
      let match;
      while ((match = regex.exec(text)) !== null) {
        let u = match[1];
        u = u.replace(/\\u0026/g, "&").replace(/&amp;/g, "&").replace(/\\/g, "");
        if (u.startsWith("http")) {
          const isVid = match[0].startsWith('"video_url"');
          results.push({ url: u, type: isVid ? "video" : "image" });
        }
      }

      // Also check og:image and og:video meta tags
      const ogRegex = /<meta\s+property="(og:image|og:video)"\s+content="([^"]+)"/gi;
      let ogMatch;
      while ((ogMatch = ogRegex.exec(text)) !== null) {
        const u = ogMatch[2];
        if (u.startsWith("http")) {
          const isVid = ogMatch[1].toLowerCase() === "og:video";
          results.push({ url: u, type: isVid ? "video" : "image" });
        }
      }

      return results;
    } catch {
      return [];
    }
  };

  const deepScanPage = async (maxScrolls = 8) => {
    const originalScroll = window.scrollY;
    
    // Initial collect
    collectMedia();

    // Auto-scroll down in steps to trigger lazy loading of all media
    for (let i = 0; i < maxScrolls; i++) {
      window.scrollBy({ top: 900, behavior: "smooth" });
      await new Promise((r) => setTimeout(r, 220));
      collectMedia();

      // If reached bottom, break early
      if (window.innerHeight + window.scrollY >= document.body.scrollHeight - 50) {
        break;
      }
    }

    // For Instagram profiles, crawl all post links to resolve all carousel slides and videos
    if (state.isInstagram) {
      const postLinks = Array.from(
        document.querySelectorAll("main a[href*='/p/'], main a[href*='/reel/']")
      ).map((a) => a.href).filter(Boolean);

      const uniqueLinks = Array.from(new Set(postLinks)).slice(0, 30);
      const batchSize = 4;
      for (let i = 0; i < uniqueLinks.length; i += batchSize) {
        const batch = uniqueLinks.slice(i, i + batchSize);
        await Promise.all(
          batch.map(async (link) => {
            const items = await fetchPostCarouselMedia(link);
            items.forEach((item) => {
              if (item.url) {
                const u = absolute(item.url);
                if (u && !window.__quarry_media_cache.has(u)) {
                  const mediaObj = {
                    url: u,
                    thumbUrl: u,
                    type: item.type || "image",
                    title: "Instagram Carousel Item",
                    selected: true,
                  };
                  window.__quarry_media_cache.set(u, mediaObj);
                }
              }
            });
          })
        );
      }
    }

    // Scroll back to original position
    window.scrollTo({ top: originalScroll, behavior: "smooth" });
    await new Promise((r) => setTimeout(r, 150));
    return collectMedia();
  };

  // ------------------------------------------------------------- Subtle In-Page Features

  const downloadProfileMedia = async (btnEl) => {
    const originalContent = btnEl.innerHTML;
    try {
      btnEl.innerHTML = `
        <svg style="width:12px;height:12px;animation:quarry-spin 0.8s linear infinite;" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5">
          <circle cx="12" cy="12" r="10" stroke-opacity="0.25"></circle>
          <path d="M12 2a10 10 0 0 1 10 10"></path>
        </svg>
        <span>Scanning...</span>
      `;
      btnEl.disabled = true;

      const media = await deepScanPage(8);
      const urls = media.map((m) => m.url);
      const username = state.instagramUser || "instagram";
      const subfolder = `instagram/${username}`;

      btnEl.innerHTML = `
        <svg style="width:12px;height:12px;animation:quarry-spin 0.8s linear infinite;" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5">
          <circle cx="12" cy="12" r="10" stroke-opacity="0.25"></circle>
          <path d="M12 2a10 10 0 0 1 10 10"></path>
        </svg>
        <span>Saving (${urls.length})...</span>
      `;

      const res = await fetch(`http://127.0.0.1:${state.serverPort}/download`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          url: location.href,
          destDir: state.destDir ? `${state.destDir}\\${subfolder}` : "",
          pageMedia: urls,
        }),
      });

      if (!res.ok) throw new Error(`HTTP ${res.status}`);

      btnEl.innerHTML = `
        <svg style="width:12px;height:12px;color:#10b981;" viewBox="0 0 24 24" fill="none" stroke="#10b981" stroke-width="2.5">
          <polyline points="20 6 9 17 4 12"></polyline>
        </svg>
        <span style="color:#10b981;">Saved (${urls.length})</span>
      `;
      setTimeout(() => {
        btnEl.innerHTML = originalContent;
        btnEl.disabled = false;
      }, 3000);
    } catch {
      btnEl.innerHTML = `<span>Offline</span>`;
      setTimeout(() => {
        btnEl.innerHTML = originalContent;
        btnEl.disabled = false;
      }, 2500);
    }
  };

  const downloadPostMedia = async (articleEl, btnEl, isCarousel = false) => {
    const originalContent = btnEl.innerHTML;
    try {
      btnEl.innerHTML = `
        <svg style="width:18px;height:18px;animation:quarry-spin 0.8s linear infinite;" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5">
          <circle cx="12" cy="12" r="10" stroke-opacity="0.25"></circle>
          <path d="M12 2a10 10 0 0 1 10 10"></path>
        </svg>
      `;
      btnEl.disabled = true;

      const postMediaUrls = [];
      const seen = new Set();

      articleEl.querySelectorAll("img, video, source").forEach((el) => {
        if (el.tagName === "VIDEO") {
          const vsrc = el.getAttribute("src") || (el.querySelector("source") && el.querySelector("source").getAttribute("src"));
          if (vsrc && !seen.has(vsrc)) {
            seen.add(vsrc);
            postMediaUrls.push(vsrc);
          }
        } else if (el.tagName === "IMG") {
          const ss = el.getAttribute("srcset");
          const best = ss ? bestFromSrcset(ss) : null;
          const src = best || el.getAttribute("src");
          if (src && !seen.has(src)) {
            seen.add(src);
            postMediaUrls.push(src);
          }
        }
      });

      let shortcode = "post";
      const postLink = articleEl.querySelector("a[href*='/p/'], a[href*='/reel/']");
      if (postLink) {
        const match = postLink.getAttribute("href").match(/\/(?:p|reel)\/([a-zA-Z0-9_-]+)/);
        if (match) shortcode = match[1];
      }

      const username = state.instagramUser || "instagram";
      const subfolder = `instagram/${username}`;

      const res = await fetch(`http://127.0.0.1:${state.serverPort}/download`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          url: postLink ? new URL(postLink.getAttribute("href"), location.href).href : location.href,
          destDir: state.destDir ? `${state.destDir}\\${subfolder}` : "",
          pageMedia: postMediaUrls,
        }),
      });

      if (!res.ok) throw new Error(`HTTP ${res.status}`);

      btnEl.innerHTML = `
        <svg style="width:20px;height:20px;color:#10b981;" viewBox="0 0 24 24" fill="none" stroke="#10b981" stroke-width="2.5" stroke-linecap="round" stroke-linejoin="round">
          <polyline points="20 6 9 17 4 12"></polyline>
        </svg>
      `;
      setTimeout(() => {
        btnEl.innerHTML = originalContent;
        btnEl.disabled = false;
      }, 2500);
    } catch {
      btnEl.innerHTML = `
        <svg style="width:18px;height:18px;color:#ef4444;" viewBox="0 0 24 24" fill="none" stroke="#ef4444" stroke-width="2">
          <circle cx="12" cy="12" r="10"></circle>
          <line x1="15" y1="9" x2="9" y2="15"></line>
          <line x1="9" y1="9" x2="15" y2="15"></line>
        </svg>
      `;
      setTimeout(() => {
        btnEl.innerHTML = originalContent;
        btnEl.disabled = false;
      }, 2500);
    }
  };

  const injectStyles = () => {
    if (!state.isInstagram) return;
    if (document.getElementById("quarry-inpage-styles")) return;

    const style = document.createElement("style");
    style.id = "quarry-inpage-styles";
    style.textContent = `
      @keyframes quarry-spin {
        from { transform: rotate(0deg); }
        to { transform: rotate(360deg); }
      }

      /* Subtle Header Action Button matching Instagram's native style */
      .quarry-ig-header-btn {
        display: inline-flex;
        align-items: center;
        justify-content: center;
        gap: 5px;
        background-color: #efefef;
        color: #000000 !important;
        border: 1px solid rgba(0, 0, 0, 0.08);
        border-radius: 8px;
        padding: 0 11px;
        height: 30px;
        font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, sans-serif !important;
        font-size: 12.5px;
        font-weight: 600;
        cursor: pointer;
        margin-left: 8px;
        vertical-align: middle;
        transition: background-color 0.15s ease, opacity 0.15s ease;
        white-space: nowrap;
        text-decoration: none;
      }

      .quarry-ig-header-btn:hover {
        background-color: #dbdbdb;
      }

      .quarry-ig-header-btn:active {
        opacity: 0.7;
      }

      .quarry-ig-header-btn svg {
        width: 13px;
        height: 13px;
      }

      @media (prefers-color-scheme: dark) {
        .quarry-ig-header-btn {
          background-color: #262626;
          color: #f5f5f5 !important;
          border-color: rgba(255, 255, 255, 0.12);
        }
        .quarry-ig-header-btn:hover {
          background-color: #363636;
        }
      }

      /* Post Action Bar Icon (transparent, unobtrusive) */
      .quarry-ig-post-btn {
        display: inline-flex;
        align-items: center;
        justify-content: center;
        background: transparent;
        border: none;
        padding: 8px;
        cursor: pointer;
        color: inherit;
        opacity: 1;
        transition: opacity 0.15s ease, transform 0.15s ease;
        vertical-align: middle;
      }

      .quarry-ig-post-btn:hover {
        opacity: 0.65;
      }

      .quarry-ig-post-btn:active {
        transform: scale(0.92);
      }

      .quarry-ig-action-svg {
        width: 24px;
        height: 24px;
        stroke: currentColor;
        fill: none;
      }
    `;
    document.head.appendChild(style);
  };

  const injectInstagramHeaderButton = () => {
    if (!state.isInstagram || !state.instagramUser) return;
    if (document.getElementById("quarry-ig-header-btn")) return;

    const headerSection = document.querySelector("header section, main header section");
    if (!headerSection) return;

    // Target the top row containing the username and action buttons
    const topRow = headerSection.querySelector("div:first-child");
    if (!topRow) return;

    const btn = document.createElement("button");
    btn.id = "quarry-ig-header-btn";
    btn.className = "quarry-ig-header-btn";
    btn.setAttribute("type", "button");
    btn.title = "Download profile with Quarry";
    btn.innerHTML = `
      <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round">
        <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"></path>
        <polyline points="7 10 12 15 17 10"></polyline>
        <line x1="12" y1="15" x2="12" y2="3"></line>
      </svg>
      <span>Download</span>
    `;

    btn.addEventListener("click", (e) => {
      e.preventDefault();
      e.stopPropagation();
      downloadProfileMedia(btn);
    });

    // Locate the actions container inside the top row (next to Follow/Message/Options)
    const actionsGroup = topRow.querySelector("div[class*='_ab8w'], div[class*='_aa_c'], section, div:nth-child(2)") || topRow;
    actionsGroup.appendChild(btn);
  };

  const injectInstagramPostButtons = () => {
    if (!state.isInstagram) return;

    document.querySelectorAll("article").forEach((article) => {
      if (article.querySelector(".quarry-ig-post-btn")) return;

      const actionsSection = article.querySelector("section");
      if (!actionsSection) return;

      const isCarousel = Boolean(
        article.querySelector("ul[class*='_aak_'], div[class*='_aak_'], div[class*='_aama'], [aria-label*='carousel' i], [aria-label*='dots' i]") ||
        article.querySelectorAll("img, video").length > 1 ||
        article.querySelector("button[aria-label*='Next' i], button[aria-label*='Siguiente' i]")
      );

      const bookmarkSvg = actionsSection.querySelector(
        'svg[aria-label*="Save" i], svg[aria-label*="Bookmark" i], svg[aria-label*="Guardar" i], svg path[d*="20 21"], svg polygon[points*="20 21"]'
      );

      const btn = document.createElement("button");
      btn.className = `quarry-ig-post-btn ${isCarousel ? "quarry-carousel-btn" : ""}`;
      btn.setAttribute("type", "button");
      btn.setAttribute("aria-label", isCarousel ? "Download all photos and videos from this post" : "Download photo or video");
      btn.title = isCarousel ? "Download all photos and videos" : "Download media";

      if (isCarousel) {
        btn.innerHTML = `
          <svg class="quarry-ig-action-svg" viewBox="0 0 24 24" width="24" height="24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
            <path d="M7 16h12a2 2 0 0 0 2-2V6a2 2 0 0 0-2-2H7a2 2 0 0 0-2 2v8a2 2 0 0 0 2 2z"></path>
            <path d="M3 10v10a2 2 0 0 0 2 2h10"></path>
            <polyline points="10 10 13 13 16 10"></polyline>
            <line x1="13" y1="13" x2="13" y2="7"></line>
          </svg>
        `;
      } else {
        btn.innerHTML = `
          <svg class="quarry-ig-action-svg" viewBox="0 0 24 24" width="24" height="24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
            <path d="M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4"></path>
            <polyline points="7 10 12 15 17 10"></polyline>
            <line x1="12" y1="15" x2="12" y2="3"></line>
          </svg>
        `;
      }

      btn.addEventListener("click", (e) => {
        e.preventDefault();
        e.stopPropagation();
        downloadPostMedia(article, btn, isCarousel);
      });

      if (bookmarkSvg) {
        const bookmarkBtn = bookmarkSvg.closest("button") || bookmarkSvg.closest("div[role='button']") || bookmarkSvg.parentElement;
        if (bookmarkBtn && bookmarkBtn.parentElement) {
          bookmarkBtn.parentElement.insertBefore(btn, bookmarkBtn);
          return;
        }
      }

      const actionRow = actionsSection.querySelector("div");
      if (actionRow) {
        actionRow.appendChild(btn);
      } else {
        actionsSection.appendChild(btn);
      }
    });
  };

  // ---------------------------------------------------- Messaging Listener

  chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
    if (!msg) return false;

    if (msg.type === "quarry-collect") {
      try {
        const media = collectMedia();
        // Only the URL list crosses the message port - the popup keeps no
        // thumbnails/preview arrays (media objects stay page-side).
        sendResponse({
          urls: media.map((m) => m.url),
          isInstagram: state.isInstagram,
          instagramUser: state.instagramUser,
          siteName: state.siteName,
          meta: state.meta,
          title: document.title,
        });
      } catch (e) {
        sendResponse({ urls: [], error: e.message });
      }
      return false;
    }

    if (msg.type === "quarry-deep-scan") {
      deepScanPage(msg.scrolls || 8)
        .then((media) => {
          sendResponse({
            urls: media.map((m) => m.url),
            isInstagram: state.isInstagram,
            instagramUser: state.instagramUser,
            siteName: state.siteName,
            meta: state.meta,
            title: document.title,
          });
        })
        .catch((e) => {
          sendResponse({ urls: [], error: e.message });
        });
      return true; // Keep message channel open for async response
    }

    if (msg.type === "quarry-set-config") {
      if (msg.port) state.serverPort = msg.port;
      if (msg.destDir) state.destDir = msg.destDir;
      sendResponse({ ok: true });
      return false;
    }

    return false;
  });

  const initInPageFeatures = () => {
    purgeOverlayElements();
    collectMedia();
    if (state.isInstagram) {
      injectStyles();
      injectInstagramHeaderButton();
      injectInstagramPostButtons();
    }
  };

  const observer = new MutationObserver(() => {
    purgeOverlayElements();
    if (state.isInstagram) {
      injectInstagramHeaderButton();
      injectInstagramPostButtons();
    }
  });

  observer.observe(document.body || document.documentElement, {
    childList: true,
    subtree: true,
  });

  try {
    chrome.storage.local.get({ port: 8765, destDir: "" }, (stored) => {
      state.serverPort = stored.port || 8765;
      state.destDir = stored.destDir || "";
      initInPageFeatures();
    });
  } catch {
    initInPageFeatures();
  }
})();
