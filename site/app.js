// Hot Corners landing page — tiny vanilla JS.
// Hits the public GitHub releases API to wire up the download buttons
// to the actual installer assets for x64 and arm64 of the latest release.
// Falls back to the static /releases/latest link on any error.

(function () {
  "use strict";

  const REPO = "bwya77/Windows-Hot-Corners";
  const API = `https://api.github.com/repos/${REPO}/releases/latest`;

  const $year = document.getElementById("year");
  if ($year) $year.textContent = new Date().getFullYear();

  const $primary = document.getElementById("download-primary");
  const $sub = document.getElementById("download-sub");
  const $arm = document.getElementById("download-arm");
  const $version = document.getElementById("version");

  if (!$primary) return;

  fetch(API, { headers: { Accept: "application/vnd.github+json" } })
    .then((r) => {
      if (!r.ok) throw new Error("GitHub API: HTTP " + r.status);
      return r.json();
    })
    .then((rel) => {
      const tag = (rel.tag_name || "").replace(/^v/, "");
      if (tag && $version) $version.textContent = tag;

      const assets = Array.isArray(rel.assets) ? rel.assets : [];
      const x64 = assets.find((a) => /win-x64\.exe$/i.test(a.name) && /Setup/i.test(a.name));
      const arm64 = assets.find((a) => /win-arm64\.exe$/i.test(a.name) && /Setup/i.test(a.name));

      if (x64) {
        $primary.href = x64.browser_download_url;
        if ($sub) $sub.textContent = `v${tag} · x64`;
      }
      if (arm64) {
        $arm.href = arm64.browser_download_url;
        $arm.textContent = `Download for ARM64 · v${tag}`;
      } else {
        $arm.style.display = "none";
      }
    })
    .catch(() => {
      // Static fallback links already set in the HTML.
    });
})();
