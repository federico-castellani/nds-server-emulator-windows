using System.Security.Cryptography;

namespace NdsStation.Services;

/// <summary>
/// Keeps the account's virtual DS identical on every device. The console firmware holds
/// the Nintendo WFC user ID that games tie their friend roster to; if a PC and a phone
/// each had their own, the game would offer to erase the friends every time the player
/// switched. The server (/console) holds the reference copy, this PC a local one.
/// </summary>
public static class ConsoleIdentity
{
    private static readonly SemaphoreSlim UploadLock = new(1, 1);
    private static byte[]? _serverHash;

    /// <summary>
    /// False when this session could not learn what the server has (offline, old server):
    /// then nothing is uploaded, so a console created offline never replaces the real one.
    /// </summary>
    private static bool _canUpload;

    /// <summary>The firmware to boot with: the server's if it has one, else this PC's (or null).</summary>
    public static async Task<byte[]?> ResolveAsync(ApiClient api, CancellationToken ct)
    {
        var local = LocalStore.ReadFirmware();
        _canUpload = false;
        try
        {
            var (supported, server) = await api.GetConsoleAsync(ct);
            if (!supported) return local;
            _canUpload = true;

            if (server is not null)
            {
                _serverHash = SHA256.HashData(server);
                if (local is null || !SHA256.HashData(local).AsSpan().SequenceEqual(_serverHash))
                {
                    try { LocalStore.WriteFirmware(server); }
                    catch (IOException) { }
                }
                return server;
            }

            // The server has no console for this account yet: this device's becomes it.
            _serverHash = null;
            if (local is not null) _ = UploadAsync(api, local);
            return local;
        }
        catch (ApiException ex) when (!ex.IsUnauthorized)
        {
            return local;
        }
    }

    /// <summary>Stores the firmware the console just wrote, here and on the server.</summary>
    public static void Save(ApiClient api, byte[] firmware)
    {
        try { LocalStore.WriteFirmware(firmware); }
        catch (IOException) { }
        if (_canUpload) _ = UploadAsync(api, firmware);
    }

    private static async Task UploadAsync(ApiClient api, byte[] firmware)
    {
        await UploadLock.WaitAsync();
        try
        {
            var hash = SHA256.HashData(firmware);
            if (_serverHash is not null && hash.AsSpan().SequenceEqual(_serverHash)) return;
            await api.UploadConsoleAsync(firmware);
            _serverHash = hash;
        }
        catch (ApiException)
        {
            // Kept locally; the next session uploads it if the server still has none.
        }
        finally
        {
            UploadLock.Release();
        }
    }
}
