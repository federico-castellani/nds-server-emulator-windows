using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NdsStation.Emulation;
using NdsStation.Services;
using Windows.UI;

namespace NdsStation.Views;

/// <summary>A game card in the library.</summary>
public sealed class GameItem : INotifyPropertyChanged
{
    private const int IconScale = 3;

    private static readonly Color[] CoverColors =
    {
        Color.FromArgb(255, 0, 99, 177),
        Color.FromArgb(255, 135, 100, 184),
        Color.FromArgb(255, 0, 120, 110),
        Color.FromArgb(255, 196, 43, 28),
        Color.FromArgb(255, 202, 80, 16),
        Color.FromArgb(255, 16, 124, 16),
        Color.FromArgb(255, 0, 91, 112),
        Color.FromArgb(255, 177, 70, 194),
    };

    private ImageSource? _icon;
    private string _subtitle = "Nintendo DS";
    private bool _isCached;

    public GameItem(GameInfo info)
    {
        Info = info;
        var words = info.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Initials = words.Length switch
        {
            0 => "?",
            1 => words[0][..Math.Min(2, words[0].Length)].ToUpperInvariant(),
            _ => string.Concat(words.Take(2).Select(w => char.ToUpperInvariant(w[0]))),
        };

        int hash = 0;
        foreach (var c in info.Id + info.Name) hash = hash * 31 + c;
        var color = CoverColors[(int)((uint)hash % CoverColors.Length)];
        CoverBrush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 1),
            GradientStops =
            {
                new GradientStop { Color = color, Offset = 0 },
                new GradientStop { Color = Darken(color), Offset = 1 },
            },
        };
    }

    public GameInfo Info { get; }
    public string Name => Info.Name;
    public string Initials { get; }
    public Brush CoverBrush { get; }

    public ImageSource? Icon
    {
        get => _icon;
        private set { _icon = value; OnChanged(); OnChanged(nameof(ShowIcon)); OnChanged(nameof(ShowInitials)); }
    }

    public Visibility ShowIcon => _icon is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ShowInitials => _icon is null ? Visibility.Visible : Visibility.Collapsed;

    public string Subtitle
    {
        get => _subtitle;
        private set { _subtitle = value; OnChanged(); }
    }

    public bool IsCached
    {
        get => _isCached;
        private set { _isCached = value; OnChanged(); OnChanged(nameof(CachedVisibility)); }
    }

    public Visibility CachedVisibility => _isCached ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Reads the banner from the cached ROM, if any. Call on the UI thread.</summary>
    public async Task RefreshFromCacheAsync()
    {
        IsCached = LocalStore.IsRomCached(Info.Id);
        if (!IsCached)
        {
            Icon = null;
            Subtitle = "Nintendo DS";
            return;
        }

        var banner = await Task.Run(() => RomBanner.TryRead(LocalStore.RomPath(Info.Id)));
        if (banner is null) return;

        var lines = banner.Title?.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines is { Length: > 1 }) Subtitle = lines[^1];

        Icon = await CreateIconAsync(banner.IconBgra);
    }

    // Scaled up with nearest neighbour so the 32 px pixel art stays crisp.
    private static async Task<ImageSource> CreateIconAsync(byte[] bgra)
    {
        int size = RomBanner.IconSize * IconScale;
        var scaled = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int src = ((y / IconScale) * RomBanner.IconSize + x / IconScale) * 4;
                Buffer.BlockCopy(bgra, src, scaled, (y * size + x) * 4, 4);
            }
        }

        var bitmap = new WriteableBitmap(size, size);
        using (var stream = bitmap.PixelBuffer.AsStream())
            await stream.WriteAsync(scaled);
        bitmap.Invalidate();
        return bitmap;
    }

    private static Color Darken(Color c) =>
        Color.FromArgb(255, (byte)(c.R * 0.55), (byte)(c.G * 0.55), (byte)(c.B * 0.55));

    // Screen readers announce list items by their ToString().
    public override string ToString() => Name;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
