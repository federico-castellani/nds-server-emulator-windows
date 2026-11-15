using System.Text;

namespace NdsStation.Emulation;

/// <summary>Reads the icon and title embedded in an NDS ROM header (the banner).</summary>
public sealed class RomBanner
{
    public const int IconSize = 32;

    private RomBanner(byte[] iconBgra, string? title)
    {
        IconBgra = iconBgra;
        Title = title;
    }

    /// <summary>32x32 icon as premultiplied BGRA.</summary>
    public byte[] IconBgra { get; }

    /// <summary>English banner title (may span several lines), if present.</summary>
    public string? Title { get; }

    public static RomBanner? TryRead(string romPath)
    {
        try
        {
            using var file = File.OpenRead(romPath);
            var header = new byte[0x200];
            if (file.Read(header, 0, header.Length) != header.Length) return null;

            uint bannerOffset = BitConverter.ToUInt32(header, 0x68);
            if (bannerOffset == 0 || bannerOffset + 0x840 > file.Length) return null;

            var banner = new byte[0x840];
            file.Seek(bannerOffset, SeekOrigin.Begin);
            if (file.Read(banner, 0, banner.Length) != banner.Length) return null;

            return new RomBanner(DecodeIcon(banner), DecodeTitle(banner, 0x340));
        }
        catch (IOException)
        {
            return null;
        }
    }

    // The icon is 4x4 tiles of 8x8 pixels, 4 bits per pixel, with a 16-color BGR555 palette.
    // Palette entry 0 is transparent.
    private static byte[] DecodeIcon(byte[] banner)
    {
        var palette = new uint[16];
        for (int i = 0; i < 16; i++)
        {
            ushort c = BitConverter.ToUInt16(banner, 0x220 + i * 2);
            uint r = (uint)((c & 0x1F) * 255 / 31);
            uint g = (uint)(((c >> 5) & 0x1F) * 255 / 31);
            uint b = (uint)(((c >> 10) & 0x1F) * 255 / 31);
            palette[i] = i == 0 ? 0 : 0xFF000000u | (r << 16) | (g << 8) | b;
        }

        var pixels = new byte[IconSize * IconSize * 4];
        for (int tile = 0; tile < 16; tile++)
        {
            int tileX = tile % 4 * 8, tileY = tile / 4 * 8;
            for (int i = 0; i < 32; i++)
            {
                byte packed = banner[0x20 + tile * 32 + i];
                for (int half = 0; half < 2; half++)
                {
                    int index = half == 0 ? packed & 0x0F : packed >> 4;
                    int px = tileX + (i % 4) * 2 + half;
                    int py = tileY + i / 4;
                    BitConverter.TryWriteBytes(pixels.AsSpan((py * IconSize + px) * 4, 4), palette[index]);
                }
            }
        }
        return pixels;
    }

    private static string? DecodeTitle(byte[] banner, int offset)
    {
        var text = Encoding.Unicode.GetString(banner, offset, 0x100);
        int end = text.IndexOf('\0');
        if (end >= 0) text = text[..end];
        text = text.Trim();
        return text.Length > 0 ? text : null;
    }
}
