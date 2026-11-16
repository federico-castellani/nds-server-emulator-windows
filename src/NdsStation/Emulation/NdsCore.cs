using System.Runtime.InteropServices;
using System.Text;

namespace NdsStation.Emulation;

/// <summary>P/Invoke bindings for ndscore.dll (see native/include/ndscore.h).</summary>
public static unsafe partial class NdsCore
{
    private const string Dll = "ndscore.dll";

    public const int ApiVersion = 4;
    public const int ScreenWidth = 256;
    public const int ScreenHeight = 192;
    public const int FrameBytes = ScreenWidth * ScreenHeight * 2 * 4;

    [StructLayout(LayoutKind.Sequential)]
    public struct BootArgs
    {
        public byte* Rom;
        public int RomLength;
        public byte* Save;
        public int SaveLength;
        public byte* SaveName;
        public byte* Nickname;
        public fixed byte PrimaryDns[4];
        public fixed byte SecondaryDns[4];
        public byte* Firmware;
        public int FirmwareLength;
        public fixed byte Mac[6];
    }

    public enum NetMode { None = 0, Slirp = 1, PCap = 2 }

    public enum NetPreference { Auto = 0, Builtin = 1, Direct = 2 }

    /// <summary>Sound while fast-forwarding.</summary>
    public enum FastForwardSound { NormalPitch = 0, SpedUp = 1, Mute = 2 }

    [LibraryImport(Dll, EntryPoint = "ndscore_api_version")]
    public static partial int GetApiVersion();

    [LibraryImport(Dll, EntryPoint = "ndscore_init")]
    public static partial int Init();

    [LibraryImport(Dll, EntryPoint = "ndscore_shutdown")]
    public static partial void Shutdown();

    [LibraryImport(Dll, EntryPoint = "ndscore_boot")]
    private static partial int BootNative(BootArgs* args);

    [LibraryImport(Dll, EntryPoint = "ndscore_stop")]
    public static partial void Stop();

    [LibraryImport(Dll, EntryPoint = "ndscore_unload")]
    public static partial void Unload();

    [LibraryImport(Dll, EntryPoint = "ndscore_is_running")]
    public static partial int IsRunning();

    [LibraryImport(Dll, EntryPoint = "ndscore_set_paused")]
    public static partial void SetPaused(int paused);

    [LibraryImport(Dll, EntryPoint = "ndscore_set_speed")]
    public static partial void SetSpeed(float multiplier);

    [LibraryImport(Dll, EntryPoint = "ndscore_set_volume")]
    public static partial void SetVolume(float volume);

    [LibraryImport(Dll, EntryPoint = "ndscore_set_fast_forward_audio")]
    public static partial void SetFastForwardAudio(FastForwardSound mode);

    [LibraryImport(Dll, EntryPoint = "ndscore_set_input")]
    public static partial void SetInput(uint pressedMask);

    [LibraryImport(Dll, EntryPoint = "ndscore_set_touch")]
    public static partial void SetTouch(int down, int x, int y);

    [LibraryImport(Dll, EntryPoint = "ndscore_get_gamepad_buttons")]
    public static partial uint GetGamepadButtons();

    [LibraryImport(Dll, EntryPoint = "ndscore_get_gamepad_name")]
    private static partial int GetGamepadNameNative(byte* buffer, int capacity);

    [LibraryImport(Dll, EntryPoint = "ndscore_copy_frame")]
    private static partial ulong CopyFrameNative(byte* dst, ulong lastFrame);

    [LibraryImport(Dll, EntryPoint = "ndscore_get_fps")]
    public static partial float GetFps();

    [LibraryImport(Dll, EntryPoint = "ndscore_get_firmware_counter")]
    public static partial uint GetFirmwareCounter();

    [LibraryImport(Dll, EntryPoint = "ndscore_get_firmware")]
    private static partial int GetFirmwareNative(byte* dst, int capacity);

    [LibraryImport(Dll, EntryPoint = "ndscore_get_save_counter")]
    public static partial uint GetSaveCounter();

    [LibraryImport(Dll, EntryPoint = "ndscore_get_save")]
    private static partial int GetSaveNative(byte* dst, int capacity);

    [LibraryImport(Dll, EntryPoint = "ndscore_save_state")]
    private static partial int SaveStateNative(byte* dst, int capacity);

    [LibraryImport(Dll, EntryPoint = "ndscore_load_state")]
    private static partial int LoadStateNative(byte* src, int length);

    [LibraryImport(Dll, EntryPoint = "ndscore_get_net_mode")]
    private static partial int GetNetModeNative();

    [LibraryImport(Dll, EntryPoint = "ndscore_set_network")]
    private static partial int SetNetworkNative(int preference);

    [LibraryImport(Dll, EntryPoint = "ndscore_get_net_description")]
    private static partial int GetNetDescriptionNative(byte* buffer, int capacity);

    [LibraryImport(Dll, EntryPoint = "ndscore_get_net_report")]
    private static partial int GetNetReportNative(byte* buffer, int capacity);

    [LibraryImport(Dll, EntryPoint = "ndscore_last_error")]
    private static partial nint LastErrorNative();

    // --- Managed helpers -------------------------------------------------------

    public static string LastError => Marshal.PtrToStringUTF8(LastErrorNative()) ?? "";

    public static NetMode CurrentNetMode => (NetMode)GetNetModeNative();

    /// <summary>Applies the connection preference; only valid while no game is running.</summary>
    public static NetMode SetNetwork(NetPreference preference) => (NetMode)SetNetworkNative((int)preference);

    public static string NetDescription => ReadString(GetNetDescriptionNative);

    /// <summary>Per-endpoint traffic summary of the current or last session.</summary>
    public static string NetReport => ReadString(GetNetReportNative);

    private delegate int StringGetter(byte* buffer, int capacity);

    private static string ReadString(StringGetter getter)
    {
        int size = getter(null, 0);
        if (size <= 1) return "";
        var buffer = new byte[size];
        fixed (byte* p = buffer)
        {
            int written = getter(p, buffer.Length);
            return Encoding.UTF8.GetString(buffer, 0, Math.Max(0, written));
        }
    }

    public static bool Boot(byte[] rom, byte[]? save, string saveName, string nickname, byte[] primaryDns, byte[] secondaryDns,
        byte[]? firmware, byte[] mac)
    {
        var saveNameBytes = Utf8z(saveName);
        var nicknameBytes = Utf8z(nickname);
        fixed (byte* romPtr = rom)
        fixed (byte* savePtr = save)
        fixed (byte* namePtr = saveNameBytes)
        fixed (byte* nickPtr = nicknameBytes)
        fixed (byte* firmwarePtr = firmware)
        {
            var args = new BootArgs
            {
                Rom = romPtr,
                RomLength = rom.Length,
                Save = savePtr,
                SaveLength = save?.Length ?? 0,
                SaveName = namePtr,
                Nickname = nickPtr,
                Firmware = firmwarePtr,
                FirmwareLength = firmware?.Length ?? 0,
            };
            for (int i = 0; i < 6; i++) args.Mac[i] = mac[i];
            for (int i = 0; i < 4; i++)
            {
                args.PrimaryDns[i] = primaryDns[i];
                args.SecondaryDns[i] = secondaryDns[i];
            }
            return BootNative(&args) != 0;
        }
    }

    /// <summary>The console's current firmware image (Wi-Fi settings, WFC user ID, profile).</summary>
    public static byte[]? GetFirmware()
    {
        int length = GetFirmwareNative(null, 0);
        if (length <= 0) return null;
        var data = new byte[length];
        fixed (byte* dst = data) return GetFirmwareNative(dst, data.Length) == length ? data : null;
    }

    public static ulong CopyFrame(byte[] destination, ulong lastFrame)
    {
        fixed (byte* dst = destination) return CopyFrameNative(dst, lastFrame);
    }

    public static string? GetGamepadName()
    {
        var buffer = stackalloc byte[256];
        return GetGamepadNameNative(buffer, 256) != 0 ? Marshal.PtrToStringUTF8((nint)buffer) : null;
    }

    public static byte[]? GetSave()
    {
        int length = GetSaveNative(null, 0);
        if (length <= 0) return null;
        var data = new byte[length];
        fixed (byte* dst = data)
        {
            int copied = GetSaveNative(dst, data.Length);
            return copied == length ? data : null;
        }
    }

    public static byte[]? SaveState()
    {
        int length = SaveStateNative(null, 0);
        if (length <= 0) return null;
        // The state can grow slightly between the two calls; leave some headroom.
        var data = new byte[length + 1024 * 1024];
        fixed (byte* dst = data)
        {
            int written = SaveStateNative(dst, data.Length);
            return written > 0 ? data.AsSpan(0, written).ToArray() : null;
        }
    }

    public static bool LoadState(byte[] state)
    {
        fixed (byte* src = state) return LoadStateNative(src, state.Length) != 0;
    }

    private static byte[] Utf8z(string value) => Encoding.UTF8.GetBytes(value + "\0");
}
