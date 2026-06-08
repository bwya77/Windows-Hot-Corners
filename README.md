# Hot Corners for Windows

macOS-style **Hot Corners** for Windows. Move your cursor into a corner of your
screen, hold it briefly, and a configurable action fires — Task View, Show
Desktop, virtual desktop switching, lock screen, and more.

Lightweight tray app written in C# / .NET 8 WinForms.

---

## Install

### One-line install (recommended)

Open PowerShell and run:

```powershell
irm https://raw.githubusercontent.com/bwya77/Windows-Hot-Corners/main/install.ps1 | iex
```

This downloads the latest signed-release `HotCorners.exe`, places it in
`%LocalAppData%\Programs\HotCorners\`, registers it to run at sign-in, and
launches it. **No .NET runtime required** — the binary is self-contained.

### Manual install

1. Go to [Releases](https://github.com/bwya77/Windows-Hot-Corners/releases/latest).
2. Download `HotCorners-x64.exe` (or `HotCorners-arm64.exe` for ARM PCs).
3. Save it anywhere you want (e.g. `%LocalAppData%\Programs\HotCorners\HotCorners.exe`).
4. Run it. A tray icon appears.
5. To run at sign-in: right-click the tray icon → **Hot Corners Settings…** →
   tick **Start Hot Corners when I sign in to Windows**.

### Uninstall

```powershell
irm https://raw.githubusercontent.com/bwya77/Windows-Hot-Corners/main/uninstall.ps1 | iex
```

Or manually: stop `HotCorners.exe`, delete `%LocalAppData%\Programs\HotCorners\`,
and remove the `HotCorners` value under
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.

Your settings live at `%AppData%\HotCorners\settings.json` — delete that folder
to also clear preferences.

---

## Use it

**Right-click the tray icon** (or double-click it) → **Hot Corners Settings…**

You get a macOS-style settings window with four corner dropdowns over a
screen-shaped preview. Pick an action for any corner. Settings save instantly.

**Default binding:** top-left corner → Task View (Win+Tab).

### Available actions

| Action | What it does |
|---|---|
| Task View | Win+Tab — opens (or closes) Task View |
| Show Desktop | Win+D — toggles desktop |
| Quick Settings | Win+A |
| Notification Center | Win+N |
| Start Menu | Win |
| Search | Win+S |
| Widgets | Win+W |
| Lock Screen | Win+L |
| Previous Desktop | Ctrl+Win+← |
| Next Desktop | Ctrl+Win+→ |
| Snap Window Left | Win+← |
| Snap Window Right | Win+→ |
| Minimize All | Win+M |
| Put Display to Sleep | Tells the system to power-off the displays |

### Tunables

- **Dwell time** (25–600 ms): how long the cursor must rest in a corner before
  firing. Default 150 ms. Drop to 25–50 ms if you want it nearly instant; bump
  to 250+ ms if you trigger by accident.
- **Suppress in fullscreen apps**: skips firing while a true fullscreen app
  (game, full-screen video) is in the foreground. The Windows shell — Task View,
  Start, Search — is exempt, so you can still re-trigger a corner to toggle
  those off.

---

## How corner detection works

A corner only fires when the cursor is bumped on **both** axes — i.e. there's
no neighboring monitor in either direction. This makes the "internal" corners
between adjacent monitors inert (the cursor can keep moving onto the other
display, so it isn't a true bump). Same behavior as macOS hot corners.

Cursor position is polled at ~66 Hz with a configurable dwell timer, plus a
500 ms cooldown to prevent double-fires.

---

## Build from source

Requires .NET 8 SDK.

```powershell
git clone https://github.com/bwya77/Windows-Hot-Corners.git
cd Windows-Hot-Corners
dotnet run -c Release
```

### Publish a self-contained single-file exe

```powershell
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:DebugType=embedded `
  -p:PublishReadyToRun=true `
  -o publish\x64
```

The resulting `publish\x64\HotCorners.exe` has no external dependencies — copy
it anywhere and run.

---

## License

[MIT](LICENSE) © 2026 Bradley Wyatt
