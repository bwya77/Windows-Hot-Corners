// Hot Corners landing page — tiny vanilla JS.
//
// 1) Detects the visitor's OS + CPU architecture and tailors a single download
//    button:
//       Windows + x64    -> "Download for Windows  ·  v0.4.6 · x64"
//       Windows + arm64  -> "Download for Windows  ·  v0.4.6 · ARM64"
//       Anything else    -> "Get Hot Corners (Windows only)" pointing at the
//                            releases page, plus a small "Other downloads" link.
//
// 2) Hits the public GitHub releases API to resolve the actual installer asset
//    URL for the chosen arch. Falls back to the static /releases/latest page
//    on any error (offline, rate-limited, etc.).

(function () {
  "use strict";

  const REPO = "bwya77/Windows-Hot-Corners";
  const API = `https://api.github.com/repos/${REPO}/releases/latest`;
  const RELEASES_PAGE = `https://github.com/${REPO}/releases/latest`;

  const $year = document.getElementById("year");
  if ($year) $year.textContent = new Date().getFullYear();

  const $primary = document.getElementById("download-primary");
  const $sub = document.getElementById("download-sub");
  const $label = document.getElementById("download-label");
  const $other = document.getElementById("download-other");
  const $version = document.getElementById("version");

  if (!$primary) return;

  detectPlatform().then((p) => paint(p));

  async function detectPlatform() {
    // Preferred: User-Agent Client Hints (modern Chromium-based browsers,
    // including Edge on Windows). This is the only reliable way to tell x64
    // from ARM64 on Windows in the browser.
    try {
      if (navigator.userAgentData && navigator.userAgentData.getHighEntropyValues) {
        const hi = await navigator.userAgentData.getHighEntropyValues([
          "architecture", "platform", "bitness",
        ]);
        const platform = (hi.platform || "").toLowerCase();
        const isWin =
          platform.includes("windows") ||
          (navigator.userAgentData.platform || "").toLowerCase().includes("windows");
        if (!isWin) return { os: "other", arch: null };

        const arch = (hi.architecture || "").toLowerCase();
        if (arch === "arm") return { os: "windows", arch: "arm64" };
        if (arch === "x86") return { os: "windows", arch: "x64" };
        // Some browsers report empty arch — fall through to UA sniffing.
      }
    } catch (_) {
      // ignored
    }

    // Fallback: classic navigator.userAgent / .platform sniffing.
    const ua = (navigator.userAgent || "").toLowerCase();
    const plat = (navigator.platform || "").toLowerCase();
    const isWin = ua.includes("windows") || plat.includes("win");
    if (!isWin) return { os: "other", arch: null };

    // ARM Windows usually shows "arm64", "aarch64", or "wow64" hints.
    if (/(arm64|aarch64)/.test(ua) || /arm/.test(plat)) {
      return { os: "windows", arch: "arm64" };
    }
    return { os: "windows", arch: "x64" };
  }

  function paint(platform) {
    // Non-Windows visitor: keep one CTA pointing at the releases page, and
    // surface a quiet "Other downloads" link in case they're shopping for
    // someone else.
    if (platform.os !== "windows") {
      if ($label) $label.textContent = "Get Hot Corners";
      if ($sub) $sub.textContent = "Windows 10 / 11";
      $primary.href = RELEASES_PAGE;
      return;
    }

    // Windows visitor: optimistic label even before the API resolves.
    const archLabel = platform.arch === "arm64" ? "ARM64" : "x64";
    if ($label) $label.textContent = "Download for Windows";
    if ($sub) $sub.textContent = `latest · ${archLabel}`;

    fetch(API, { headers: { Accept: "application/vnd.github+json" } })
      .then((r) => {
        if (!r.ok) throw new Error("GitHub API HTTP " + r.status);
        return r.json();
      })
      .then((rel) => {
        const tag = (rel.tag_name || "").replace(/^v/, "");
        if (tag && $version) $version.textContent = tag;

        const assets = Array.isArray(rel.assets) ? rel.assets : [];
        const preferred = pickAsset(assets, platform.arch);
        const fallback = platform.arch === "arm64"
          ? pickAsset(assets, "x64")
          : pickAsset(assets, "arm64");

        if (preferred) {
          $primary.href = preferred.browser_download_url;
          if ($sub) $sub.textContent = `v${tag} · ${archLabel}`;
        }

        // If both x64 and arm64 exist, show a discreet secondary link for the
        // other arch (handy when someone is downloading for a different PC).
        if (preferred && fallback && $other) {
          const otherArchLabel = platform.arch === "arm64" ? "x64" : "ARM64";
          $other.textContent = `Download ${otherArchLabel} instead`;
          $other.href = fallback.browser_download_url;
          $other.style.display = "";
        }
      })
      .catch(() => {
        // Static fallback links are already in the HTML.
      });
  }

  function pickAsset(assets, arch) {
    const suffix = arch === "arm64" ? "win-arm64.exe" : "win-x64.exe";
    return assets.find(
      (a) =>
        a &&
        typeof a.name === "string" &&
        a.name.toLowerCase().endsWith(suffix) &&
        /setup/i.test(a.name)
    );
  }
})();
