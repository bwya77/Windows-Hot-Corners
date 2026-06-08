using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace HotCorners.Updates;

/// <summary>
/// Checks GitHub Releases for a newer build than the one currently running.
/// Uses the public REST API (no auth, 60 req/hour/IP is ample for a startup check).
/// All failures are swallowed so a missing network never disrupts the app.
/// </summary>
internal sealed class UpdateChecker
{
    private const string Owner = "bwya77";
    private const string Repo = "Windows-Hot-Corners";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public sealed record UpdateInfo(Version Latest, string Tag, string HtmlUrl, string? InstallerUrl, long InstallerSize, string? Notes);

    /// <summary>
    /// The running build's version, normalized to Major.Minor.Build. Reads the informational
    /// version (which the release build stamps via -p:Version); the bare AssemblyVersion stays
    /// at the 0.1.0 default unless explicitly set, so it can't always be trusted.
    /// </summary>
    public static Version CurrentVersion
    {
        get
        {
            var asm = Assembly.GetExecutingAssembly();
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            var parsed = ParseVersion(info);
            if (parsed != null) return parsed;
            var v = asm.GetName().Version ?? new Version(0, 0, 0);
            return Normalize(v);
        }
    }

    /// <summary>
    /// Returns details of a newer release if one exists, otherwise null (already up to date,
    /// or the check could not be completed).
    /// </summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            var url = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("HotCorners-UpdateChecker");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            var htmlUrl = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(htmlUrl))
                htmlUrl = $"https://github.com/{Owner}/{Repo}/releases/latest";

            var notes = root.TryGetProperty("body", out var b) ? b.GetString() : null;

            var latest = ParseVersion(tag);
            if (latest == null) return null;
            if (latest <= CurrentVersion) return null;

            string? installerUrl = null;
            long installerSize = 0;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                var arch = ArchToken();
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
                    if (name.StartsWith("HotCornersSetup", StringComparison.OrdinalIgnoreCase) &&
                        name.EndsWith($"win-{arch}.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        installerUrl = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                        installerSize = a.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
                        break;
                    }
                }
            }

            return new UpdateInfo(latest, tag, htmlUrl, installerUrl, installerSize, notes);
        }
        catch
        {
            return null;
        }
    }

    public static async Task<string?> DownloadInstallerAsync(string url, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        try
        {
            var tempPath = Path.Combine(Path.GetTempPath(), "HotCornersSetup-" + Guid.NewGuid().ToString("N") + ".exe");

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd("HotCorners-UpdateChecker");

            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            var total = resp.Content.Headers.ContentLength ?? 0;
            var read = 0L;

            await using var input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var output = File.Create(tempPath);
            var buf = new byte[81920];
            int n;
            while ((n = await input.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                read += n;
                if (total > 0 && progress != null)
                    progress.Report((double)read / total);
            }

            return tempPath;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The release-asset architecture token for the running process.</summary>
    private static string ArchToken() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.Arm64 => "arm64",
        _ => "x64",
    };

    /// <summary>Parse a release tag like "v0.1.5" into a Major.Minor.Build version.</summary>
    internal static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var s = tag.Trim();

        var start = 0;
        while (start < s.Length && !char.IsDigit(s[start])) start++; // skip leading "v"
        s = s[start..];

        var end = 0;
        while (end < s.Length && (char.IsDigit(s[end]) || s[end] == '.')) end++;
        s = s[..end];
        if (s.Length == 0) return null;
        if (!s.Contains('.')) s += ".0";

        return Version.TryParse(s, out var v) ? Normalize(v) : null;
    }

    private static Version Normalize(Version v) =>
        new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));
}
