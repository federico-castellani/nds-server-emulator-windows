using System.Runtime.InteropServices;
using NdsStation.Services;

namespace NdsStation.Emulation;

/// <summary>DS buttons; values match the ndscore_key bits.</summary>
public enum DsKey
{
    A = 1 << 0,
    B = 1 << 1,
    Select = 1 << 2,
    Start = 1 << 3,
    Right = 1 << 4,
    Left = 1 << 5,
    Up = 1 << 6,
    Down = 1 << 7,
    R = 1 << 8,
    L = 1 << 9,
    X = 1 << 10,
    Y = 1 << 11,
}

/// <summary>Turns keyboard and controller state into the DS input mask.</summary>
public static partial class InputMap
{
    public static readonly DsKey[] AllKeys =
    {
        DsKey.Up, DsKey.Down, DsKey.Left, DsKey.Right,
        DsKey.A, DsKey.B, DsKey.X, DsKey.Y,
        DsKey.L, DsKey.R, DsKey.Start, DsKey.Select,
    };

    // Win32 virtual-key codes.
    public const int VkSpace = 0x20;
    public const int VkEscape = 0x1B;
    public const int VkF5 = 0x74;
    public const int VkF8 = 0x77;
    public const int VkF11 = 0x7A;
    public const int VkP = 0x50;

    public static Dictionary<DsKey, int> DefaultKeyboard() => new()
    {
        [DsKey.Up] = 0x26,
        [DsKey.Down] = 0x28,
        [DsKey.Left] = 0x25,
        [DsKey.Right] = 0x27,
        [DsKey.A] = 'X',
        [DsKey.B] = 'Z',
        [DsKey.X] = 'S',
        [DsKey.Y] = 'A',
        [DsKey.L] = 'Q',
        [DsKey.R] = 'W',
        [DsKey.Start] = 0x0D,
        [DsKey.Select] = 0x10,
    };

    public static string KeyLabel(DsKey key) => key switch
    {
        DsKey.Up => "Su",
        DsKey.Down => "Giù",
        DsKey.Left => "Sinistra",
        DsKey.Right => "Destra",
        _ => key.ToString(),
    };

    public static string VirtualKeyName(int vk) => vk switch
    {
        0x08 => "Backspace",
        0x09 => "Tab",
        0x0D => "Invio",
        0x10 => "Maiusc",
        0x11 => "Ctrl",
        0x12 => "Alt",
        0x14 => "Bloc Maiusc",
        0x20 => "Spazio",
        0x25 => "←",
        0x26 => "↑",
        0x27 => "→",
        0x28 => "↓",
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x60 and <= 0x69 => $"Num {vk - 0x60}",
        >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",
        0xA0 => "Maiusc sinistro",
        0xA1 => "Maiusc destro",
        0xA2 => "Ctrl sinistro",
        0xA3 => "Ctrl destro",
        0xBA => "è",
        0xBB => "+",
        0xBC => ",",
        0xBD => "-",
        0xBE => ".",
        0xC0 => "ò",
        0xDE => "à",
        _ => $"Tasto 0x{vk:X2}",
    };

    // --- Keyboard polling ----------------------------------------------------------
    // Polling GetAsyncKeyState sidesteps XAML focus: buttons in the toolbar never swallow
    // game input. Input is only read while our window is in the foreground.

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int vk);

    public static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public static uint ReadKeyboard(IReadOnlyDictionary<DsKey, int> bindings)
    {
        uint mask = 0;
        foreach (var (key, vk) in bindings)
        {
            if (vk != 0 && IsDown(vk)) mask |= (uint)key;
        }
        return mask;
    }

    // --- Controller -------------------------------------------------------------------
    // Raw button bits from ndscore (SDL_GameControllerButton order).
    private const int PadA = 0, PadB = 1, PadX = 2, PadY = 3, PadBack = 4, PadStart = 6;
    private const int PadLShoulder = 9, PadRShoulder = 10;
    private const int PadUp = 11, PadDown = 12, PadLeft = 13, PadRight = 14;
    public const uint PadStickUp = 1u << 16, PadStickDown = 1u << 17, PadStickLeft = 1u << 18, PadStickRight = 1u << 19;
    public const uint PadLTrigger = 1u << 20, PadRTrigger = 1u << 21;

    public static uint MapGamepad(uint pad, PadLayout layout)
    {
        static bool On(uint pad, int bit) => (pad & (1u << bit)) != 0;

        uint mask = 0;
        void Set(bool down, DsKey key) { if (down) mask |= (uint)key; }

        if (layout == PadLayout.Positional)
        {
            Set(On(pad, PadB), DsKey.A);
            Set(On(pad, PadA), DsKey.B);
            Set(On(pad, PadY), DsKey.X);
            Set(On(pad, PadX), DsKey.Y);
        }
        else
        {
            Set(On(pad, PadA), DsKey.A);
            Set(On(pad, PadB), DsKey.B);
            Set(On(pad, PadX), DsKey.X);
            Set(On(pad, PadY), DsKey.Y);
        }

        Set(On(pad, PadLShoulder), DsKey.L);
        Set(On(pad, PadRShoulder), DsKey.R);
        Set(On(pad, PadStart), DsKey.Start);
        Set(On(pad, PadBack), DsKey.Select);
        Set(On(pad, PadUp) || (pad & PadStickUp) != 0, DsKey.Up);
        Set(On(pad, PadDown) || (pad & PadStickDown) != 0, DsKey.Down);
        Set(On(pad, PadLeft) || (pad & PadStickLeft) != 0, DsKey.Left);
        Set(On(pad, PadRight) || (pad & PadStickRight) != 0, DsKey.Right);
        return mask;
    }
}
