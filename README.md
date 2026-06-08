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

This downloads the latest Hot Corners installer from GitHub Releases and runs
it with a UAC prompt. The installer drops Hot Corners into
`C:\Program Files\Hot Corners`, registers a clean entry in **Apps & Features**,
opts in to **Start with Windows**, and launches the tray app. **No .NET
runtime required** — the binary is self-contained.

Hot Corners checks GitHub for new versions in the background and prompts you to
install them in-place (you'll see a UAC prompt during the update).

### Manual install

1. Go to [Releases](https://github.com/bwya77/Windows-Hot-Corners/releases/latest).
2. Download `HotCornersSetup-<version>-win-x64.exe` (or `-win-arm64.exe` for ARM PCs).
3. Run it. The installer takes care of the rest.

### Uninstall

Either use **Apps & Features** → **Hot Corners** → **Uninstall**, or run:

```powershell
irm https://raw.githubusercontent.com/bwya77/Windows-Hot-Corners/main/uninstall.ps1 | iex
```

Your settings live at `%AppData%\HotCorners\settings.json` and are preserved
across uninstall/reinstall. Pass `-PurgeSettings` to the uninstall script (or
delete the folder manually) to clear them too.

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
