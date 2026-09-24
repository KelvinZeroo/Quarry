// Quarry site - tiny progressive enhancements only.
(() => {
  "use strict";

  // ------------------------------------------------------- mobile menu
  const toggle = document.getElementById("navToggle");
  const links = document.getElementById("navLinks");

  if (toggle && links) {
    toggle.addEventListener("click", () => {
      const open = links.classList.toggle("open");
      toggle.setAttribute("aria-expanded", String(open));
    });
    // close after choosing a section
    links.addEventListener("click", (e) => {
      if (e.target.tagName === "A") {
        links.classList.remove("open");
        toggle.setAttribute("aria-expanded", "false");
      }
    });
  }

  // ------------------------------------------------ typing demo line
  // Rotates a few realistic commands through the fake terminal (home only).
  const typed = document.getElementById("typedLine");
  if (typed) {
    const lines = [
      '.\\quarry.ps1 "https://www.pornpics.com/galleries/.../"',
      ".\\quarry.ps1 -File urls.txt -DryRun",
      ".\\quarry.ps1 <any-page-url> -MaxImages 20",
      ".\\start.ps1   # server for the browser button",
    ];

    const reduceMotion = window.matchMedia(
      "(prefers-reduced-motion: reduce)"
    ).matches;

    if (reduceMotion) {
      typed.textContent = lines[0];
    } else {
      let line = 0;
      let pos = 0;
      let deleting = false;

      const tick = () => {
        const full = lines[line];
        pos += deleting ? -1 : 1;
        typed.textContent = full.slice(0, pos);

        let delay = deleting ? 18 : 45;
        if (!deleting && pos === full.length) {
          deleting = true;
          delay = 2200;          // hold the finished line
        } else if (deleting && pos === 0) {
          deleting = false;
          line = (line + 1) % lines.length;
          delay = 500;
        }
        setTimeout(tick, delay);
      };
      tick();
    }
  }

  // ------------------------------------- highlight current section (home)
  const sections = document.querySelectorAll("section[id]");
  const navAnchors = document.querySelectorAll('.nav-links a[href^="#"]');

  if (sections.length && navAnchors.length && "IntersectionObserver" in window) {
    const observer = new IntersectionObserver(
      (entries) => {
        for (const entry of entries) {
          if (!entry.isIntersecting) continue;
          navAnchors.forEach((a) => {
            const active = a.getAttribute("href") === "#" + entry.target.id;
            a.style.color = active ? "var(--accent)" : "";
          });
        }
      },
      { rootMargin: "-40% 0px -55% 0px" }
    );
    sections.forEach((s) => observer.observe(s));
  }
})();
