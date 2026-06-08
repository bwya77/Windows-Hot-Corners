using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace HotCorners.Settings;

/// <summary>
/// Loads and persists <see cref="AppSettings"/> as JSON under
/// %APPDATA%\HotCorners\settings.json, and raises <see cref="Changed"/> whenever the
/// settings change so the running app can react live (no restart needed).
///
/// Because the tray app (WinForms) and the settings app (WinUI 3) are separate processes
/// that both use this store, propagation uses two mechanisms: a named cross-process
/// <see cref="EventWaitHandle"/> the writer signals for near-instant wake, plus a
/// <see cref="FileSystemWatcher"/> as a fallback. Writes are atomic (temp file + replace)
/// so a reader never catches the file mid-write. For an in-process save, Changed fires
/// synchronously on the caller's thread; for an EXTERNAL edit it fires from a background
/// thread, so subscribers that touch UI must marshal themselves. All disk failures are
/// swallowed so a read-only profile never crashes the app.
/// </summary>
public sealed class SettingsStore : IDisposable
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HotCorners");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");
    private const string SignalName = @"Local\HotCorners_Settings_Changed_v1";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The current, live settings instance.</summary>
    public AppSettings Current { get; private set; } = new();

    /// <summary>Fired after settings change (carries the new snapshot). May fire on a
    /// background thread when the change originated in another process.</summary>
    public event Action<AppSettings>? Changed;

    private readonly FileSystemWatcher? _watcher;
    private readonly EventWaitHandle? _signal;
    private readonly Thread? _signalThread;
    private readonly object _gate = new();
    private string _lastJson = "";
    private volatile bool _disposed;

    public SettingsStore()
    {
        Load();
        try
        {
            Directory.CreateDirectory(Dir);
            _watcher = new FileSystemWatcher(Dir, "settings.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += OnFileEvent;
            _watcher.Created += OnFileEvent;
            _watcher.Renamed += OnFileEvent;
        }
        catch
        {
            _watcher = null;
        }

        try
        {
            _signal = new EventWaitHandle(false, EventResetMode.AutoReset, SignalName);
            _signalThread = new Thread(SignalLoop)
            {
                IsBackground = true,
                Name = "HotCornersSettingsSignal",
            };
            _signalThread.Start();
        }
        catch
        {
            _signal = null;
        }
    }

    private void Load()
    {
        try
        {
            var json = ReadAllTextShared(FilePath);
            if (json != null)
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);
                if (loaded != null)
                {
                    loaded.Normalize();
                    Current = loaded;
                    _lastJson = json;
                    return;
                }
            }
        }
        catch
        {
            // fall through to default
        }
        Current = new AppSettings();
        // Persist defaults on first run so the file exists.
        try { Save(Current); } catch { /* best effort */ }
    }

    /// <summary>Persist the given settings and notify listeners.</summary>
    public void Save(AppSettings settings)
    {
        settings.Normalize();
        Current = settings;
        var json = JsonSerializer.Serialize(settings, JsonOpts);
        lock (_gate) _lastJson = json;
        try
        {
            Directory.CreateDirectory(Dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(FilePath))
                File.Replace(tmp, FilePath, null);
            else
                File.Move(tmp, FilePath);
        }
        catch
        {
            // Non-fatal: settings just won't persist this time.
        }
        Changed?.Invoke(settings);

        try
        {
            _signal?.Set();
            _signal?.Set();
        }
        catch { /* signaling is best-effort */ }
    }

    private void SignalLoop()
    {
        while (!_disposed && _signal != null)
        {
            try
            {
                if (_signal.WaitOne(1000) && !_disposed)
                    ReloadIfChanged();
            }
            catch
            {
                return;
            }
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e) => ReloadIfChanged();

    private void ReloadIfChanged()
    {
        string? json;
        try
        {
            json = ReadAllTextShared(FilePath);
        }
        catch
        {
            return;
        }
        if (json == null) return;

        AppSettings? loaded;
        try
        {
            loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);
        }
        catch
        {
            return;
        }
        if (loaded == null) return;
        loaded.Normalize();

        lock (_gate)
        {
            if (json == _lastJson) return;
            _lastJson = json;
        }

        Current = loaded;
        Changed?.Invoke(loaded);
    }

    private static string? ReadAllTextShared(string path)
    {
        if (!File.Exists(path)) return null;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }

    public void Dispose()
    {
        _disposed = true;

        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnFileEvent;
            _watcher.Created -= OnFileEvent;
            _watcher.Renamed -= OnFileEvent;
            _watcher.Dispose();
        }

        if (_signal != null)
        {
            try { _signal.Set(); } catch { /* wake the loop */ }
            _signalThread?.Join(500);
            _signal.Dispose();
        }
    }
}
