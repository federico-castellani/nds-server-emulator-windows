using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace NdsStation.Services;

/// <summary>A newer release published on GitHub.</summary>
public sealed record UpdateInfo(Version Version, string Notes, string PageUrl, string DownloadUrl, long Size, string? Sha256);

/// <summary>
/// In-app updates from the GitHub releases of this repository. The setup is downloaded,
/// checked against the size and SHA-256 GitHub reports, then run silently: it closes the
/// app, replaces it and starts it again. A portable copy (no uninstaller next to it) is
/// not touched: the player is sent to the release page instead.
/// </summary>
public sealed class Updater
{
    private const string Repository = "federico-castellani/nds-server-emulator-windows";
    private const string AssetPrefix = "NDSStation-Setup-";

    // Declared before Http: static initializers run in order and the client's User-Agent uses it.
    public static Version CurrentVersion { get; } = Normalize(typeof(App).Assembly.GetName().Version ?? new Version(0, 0, 0));

    private static readonly HttpClient Http = CreateClient();

    /// <summary>Installed with the setup (which can then update it in place), not a portable copy.</summary>
    public static bool IsInstalled { get; } = File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    /// <summary>The newer release found by the last check, if any.</summary>
    public UpdateInfo? Available { get; private set; }

    /// <summary>Raised (on a worker thread) when a check finds a newer release.</summary>
    public event Action<UpdateInfo>? Found;

    private int _backgroundChecked;

    /// <summary>Once per app run, when enabled in the settings; failures are ignored.</summary>
    public async Task CheckInBackgroundAsync()
    {
        if (!App.Settings.CheckForUpdates || Interlocked.Exchange(ref _backgroundChecked, 1) == 1) return;
        try
        {
            await CheckAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or UpdateException)
        {
        }
    }

    /// <summary>Returns the newer release, or null when this is the latest version.</summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var response = await Http.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", ct);
        if (!response.IsSuccessStatusCode)
            throw new UpdateException($"GitHub ha risposto {(int)response.StatusCode}.");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var release = json.RootElement;
        var tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
        version = Normalize(version);
        if (version <= CurrentVersion)
        {
            Available = null;
            return null;
        }

        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString() ?? "";
            if (!name.StartsWith(AssetPrefix, StringComparison.OrdinalIgnoreCase) ||
                !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                continue;

            string? sha256 = null;
            if (asset.TryGetProperty("digest", out var digest) && digest.GetString() is { } d &&
                d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                sha256 = d["sha256:".Length..];

            var info = new UpdateInfo(
                version,
                release.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
                release.GetProperty("html_url").GetString() ?? $"https://github.com/{Repository}/releases",
                asset.GetProperty("browser_download_url").GetString()!,
                asset.GetProperty("size").GetInt64(),
                sha256);
            Available = info;
            Found?.Invoke(info);
            return info;
        }
        return null;   // a release without the setup yet (still uploading)
    }

    /// <summary>Downloads and verifies the setup; returns its path.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double> progress, CancellationToken ct)
    {
        var folder = Path.Combine(Path.GetTempPath(), "NDSStation-update");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{AssetPrefix}{info.Version}.exe");
        var partial = path + ".part";

        using (var response = await Http.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            if (!response.IsSuccessStatusCode)
                throw new UpdateException($"Download non riuscito ({(int)response.StatusCode}).");

            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = File.Create(partial);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                hash.AppendData(buffer, 0, read);
                done += read;
                progress.Report(info.Size > 0 ? (double)done / info.Size : 0);
            }

            if (done != info.Size)
                throw new UpdateException("Il file scaricato è incompleto. Riprova.");
            if (info.Sha256 is not null &&
                !Convert.ToHexString(hash.GetHashAndReset()).Equals(info.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new UpdateException("Il file scaricato non corrisponde a quello pubblicato. Riprova.");
        }

        File.Move(partial, path, overwrite: true);
        return path;
    }

    /// <summary>
    /// Runs the setup silently and closes the app. The setup keeps the install location and
    /// mode of the current installation and starts NDS Station again when done (/RELAUNCH).
    /// </summary>
    public static void InstallAndExit(string setupPath)
    {
        Process.Start(new ProcessStartInfo(setupPath, "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RELAUNCH")
        {
            UseShellExecute = true,
        });
        Microsoft.UI.Xaml.Application.Current.Exit();
    }

    public static void OpenReleasePage(UpdateInfo info) =>
        Process.Start(new ProcessStartInfo(info.PageUrl) { UseShellExecute = true });

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        // GitHub requires a User-Agent; a generic one, nothing about the user.
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NDSStation-Updater", CurrentVersion.ToString()));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }
}

public sealed class UpdateException(string message) : Exception(message);
