using System.IO.Compression;
using System.Security.Cryptography;

namespace NdsStation.Services;

/// <summary>
/// Files kept on this PC: the ROM cache, a backup of every save, and save states.
/// Everything is scoped by server and user so several accounts never mix data.
/// </summary>
public static class LocalStore
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NDSStation");

    private static string _scope = "default";
    private static byte[] _scopeHash = new byte[32];

    public static void SetScope(string serverUrl, string username)
    {
        var raw = $"{serverUrl.Trim().TrimEnd('/').ToLowerInvariant()}|{username.Trim().ToLowerInvariant()}";
        _scopeHash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw));
        _scope = Convert.ToHexString(_scopeHash)[..16].ToLowerInvariant();
    }

    private static string RomDir => Path.Combine(Root, "cache", "roms", _scope);
    private static string SaveDir => Path.Combine(Root, "saves", _scope);
    private static string StateDir => Path.Combine(Root, "states", _scope);

    private static string Safe(string id) =>
        string.Concat(id.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    // --- ROM cache ------------------------------------------------------------

    public static string RomPath(string gameId) => Path.Combine(RomDir, Safe(gameId) + ".nds");

    public static bool IsRomCached(string gameId)
    {
        var info = new FileInfo(RomPath(gameId));
        return info.Exists && info.Length > 0;
    }

    public static async Task<byte[]?> ReadCachedRomAsync(string gameId)
    {
        if (!IsRomCached(gameId)) return null;
        try { return await File.ReadAllBytesAsync(RomPath(gameId)); }
        catch (IOException) { return null; }
    }

    public static async Task WriteCachedRomAsync(string gameId, byte[] data)
    {
        Directory.CreateDirectory(RomDir);
        var temp = RomPath(gameId) + ".part";
        await File.WriteAllBytesAsync(temp, data);
        File.Move(temp, RomPath(gameId), overwrite: true);
    }

    public static void DeleteCachedRom(string gameId)
    {
        try { File.Delete(RomPath(gameId)); } catch (IOException) { }
    }

    public static long CacheSize()
    {
        var dir = new DirectoryInfo(Path.Combine(Root, "cache"));
        return dir.Exists ? dir.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;
    }

    public static void ClearCache()
    {
        var dir = Path.Combine(Root, "cache");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    // --- Save backups -----------------------------------------------------------
    // Every save the game writes lands here first. A ".pending" marker means the
    // file has not reached the server yet, so it is offered again on the next launch.

    private static string SavePath(string gameId) => Path.Combine(SaveDir, Safe(gameId) + ".sav");
    private static string PendingPath(string gameId) => SavePath(gameId) + ".pending";

    public static void WriteSaveBackup(string gameId, byte[] data, bool pendingUpload)
    {
        Directory.CreateDirectory(SaveDir);
        var temp = SavePath(gameId) + ".tmp";
        File.WriteAllBytes(temp, data);
        File.Move(temp, SavePath(gameId), overwrite: true);
        if (pendingUpload) File.WriteAllText(PendingPath(gameId), DateTime.UtcNow.ToString("O"));
        else MarkSaveUploaded(gameId);
    }

    public static void MarkSaveUploaded(string gameId)
    {
        try { File.Delete(PendingPath(gameId)); } catch (IOException) { }
    }

    public static bool HasPendingSave(string gameId) => File.Exists(PendingPath(gameId)) && File.Exists(SavePath(gameId));

    public static DateTime? SaveBackupTime(string gameId) =>
        File.Exists(SavePath(gameId)) ? File.GetLastWriteTime(SavePath(gameId)) : null;

    public static byte[]? ReadSaveBackup(string gameId)
    {
        try { return File.Exists(SavePath(gameId)) ? File.ReadAllBytes(SavePath(gameId)) : null; }
        catch (IOException) { return null; }
    }

    // --- Console identity ----------------------------------------------------------
    // Each account is its own virtual DS: a stable MAC address (Nintendo's 00:09:BF
    // prefix) and a firmware image that keeps the Nintendo WFC user ID between sessions.
    // Without them every connection looks like a new console and games wipe their
    // friend roster.

    public static byte[] ConsoleMac => new byte[] { 0x00, 0x09, 0xBF, _scopeHash[0], _scopeHash[1], _scopeHash[2] };

    private static string FirmwarePath => Path.Combine(Root, "firmware", _scope + ".bin");

    public static byte[]? ReadFirmware()
    {
        try { return File.Exists(FirmwarePath) ? File.ReadAllBytes(FirmwarePath) : null; }
        catch (IOException) { return null; }
    }

    public static void WriteFirmware(byte[] data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FirmwarePath)!);
        var temp = FirmwarePath + ".tmp";
        File.WriteAllBytes(temp, data);
        File.Move(temp, FirmwarePath, overwrite: true);
    }

    // --- Network logs ------------------------------------------------------------

    public static string LogDir => Path.Combine(Root, "logs");

    /// <summary>Keeps the network report of each session (the last 30) for troubleshooting.</summary>
    public static void WriteNetworkLog(string gameName, string report)
    {
        Directory.CreateDirectory(LogDir);
        var name = $"rete-{DateTime.Now:yyyyMMdd-HHmmss}-{Safe(gameName)}.txt";
        File.WriteAllText(Path.Combine(LogDir, name), $"{gameName} - {DateTime.Now:F}\r\n\r\n{report.Replace("\n", "\r\n")}");
        foreach (var old in new DirectoryInfo(LogDir).GetFiles("rete-*.txt").OrderByDescending(f => f.Name).Skip(30))
        {
            try { old.Delete(); } catch (IOException) { }
        }
    }

    // --- Save states ------------------------------------------------------------

    // States are ~19 MB of mostly empty memory, so they are stored gzip-compressed.
    public static string StatePath(string gameId, int slot) => Path.Combine(StateDir, $"{Safe(gameId)}.slot{slot}.mst.gz");

    public static void WriteState(string gameId, int slot, byte[] data)
    {
        Directory.CreateDirectory(StateDir);
        var temp = StatePath(gameId, slot) + ".tmp";
        using (var file = File.Create(temp))
        using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
            gzip.Write(data);
        File.Move(temp, StatePath(gameId, slot), overwrite: true);
    }

    public static byte[]? ReadState(string gameId, int slot)
    {
        var path = StatePath(gameId, slot);
        if (!File.Exists(path)) return null;
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var buffer = new MemoryStream();
        gzip.CopyTo(buffer);
        return buffer.ToArray();
    }

    public static DateTime? StateTime(string gameId, int slot)
    {
        var path = StatePath(gameId, slot);
        return File.Exists(path) ? File.GetLastWriteTime(path) : null;
    }
}
