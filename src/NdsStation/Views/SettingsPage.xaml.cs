using System.Diagnostics;
using System.Net;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using NdsStation.Emulation;
using NdsStation.Services;

namespace NdsStation.Views;

public sealed partial class SettingsPage : Page
{
    private readonly List<KeyBindingItem> _bindings = new();
    private KeyBindingItem? _rebinding;
    private DispatcherQueueTimer? _padTimer;
    private bool _initializing = true;

    public SettingsPage()
    {
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private static AppSettings Settings => App.Settings;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _initializing = true;

        ThemeCombo.SelectedIndex = (int)Settings.Theme;
        LayoutCombo.SelectedIndex = (int)Settings.ScreenLayout;
        IntegerScalingToggle.IsOn = Settings.IntegerScaling;
        VolumeSlider.Value = Math.Round(Settings.Volume * 100);
        PadLayoutCombo.SelectedIndex = (int)Settings.PadLayout;
        FastForwardCombo.SelectedItem = FastForwardCombo.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(i => float.Parse((string)i.Tag) == Settings.FastForwardSpeed) ?? FastForwardCombo.Items[1];
        PrimaryDnsBox.Text = Settings.PrimaryDns;
        SecondaryDnsBox.Text = Settings.SecondaryDns;

        LoadBindings();
        UpdateCacheRow();

        VersionText.Text = $"NDS Station {typeof(App).Assembly.GetName().Version?.ToString(3)}";
        CoreStatusText.Text = App.CoreAvailable ? "Core di emulazione caricato." : App.CoreError ?? "Core di emulazione non disponibile.";
        NetworkCombo.SelectedIndex = (int)Settings.Network;
        UpdateNetworkRow();

        UpdateGamepad();
        _padTimer = DispatcherQueue.CreateTimer();
        _padTimer.Interval = TimeSpan.FromSeconds(1);
        _padTimer.Tick += (_, _) => UpdateGamepad();
        _padTimer.Start();

        _initializing = false;
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _padTimer?.Stop();
        CommitDns();
    }

    private void UpdateGamepad()
    {
        var name = App.CoreAvailable ? NdsCore.GetGamepadName() : null;
        GamepadRow.Description = name is null ? "Nessun controller collegato" : $"Collegato: {name}";
    }

    private void UpdateCacheRow()
    {
        double mb = LocalStore.CacheSize() / (1024.0 * 1024.0);
        CacheRow.Description = mb < 0.1 ? "Nessuna ROM salvata su questo PC" : $"{mb:N0} MB usati per giocare più velocemente e offline";
    }

    // --- Appearance, screen, audio -----------------------------------------------

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || ThemeCombo.SelectedIndex < 0) return;
        Settings.Theme = (AppTheme)ThemeCombo.SelectedIndex;
        Settings.Save();
        App.MainWindow.ApplyTheme(Settings.Theme);
    }

    private void OnLayoutChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || LayoutCombo.SelectedIndex < 0) return;
        Settings.ScreenLayout = (ScreenLayout)LayoutCombo.SelectedIndex;
        Settings.Save();
    }

    private void OnIntegerScalingToggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        Settings.IntegerScaling = IntegerScalingToggle.IsOn;
        Settings.Save();
    }

    private void OnVolumeChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_initializing) return;
        Settings.Volume = e.NewValue / 100.0;
        Settings.Save();
    }

    private void OnFastForwardChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || FastForwardCombo.SelectedItem is not ComboBoxItem item) return;
        Settings.FastForwardSpeed = float.Parse((string)item.Tag);
        Settings.Save();
    }

    private void OnPadLayoutChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || PadLayoutCombo.SelectedIndex < 0) return;
        Settings.PadLayout = (PadLayout)PadLayoutCombo.SelectedIndex;
        Settings.Save();
    }

    // --- Key bindings ------------------------------------------------------------

    private void LoadBindings()
    {
        _bindings.Clear();
        foreach (var key in InputMap.AllKeys)
            _bindings.Add(new KeyBindingItem(key, Settings.KeyBindings.GetValueOrDefault(key)));
        KeyBindingsRepeater.ItemsSource = null;
        KeyBindingsRepeater.ItemsSource = _bindings;
    }

    private void OnRebindKey(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not KeyBindingItem item) return;
        if (_rebinding is not null) _rebinding.Waiting = false;
        _rebinding = item;
        item.Waiting = true;
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_rebinding is null) return;
        e.Handled = true;

        var item = _rebinding;
        _rebinding = null;
        item.Waiting = false;
        if (e.Key == Windows.System.VirtualKey.Escape) return;

        int vk = (int)e.Key;
        // A key drives a single DS button: take it away from any other button.
        foreach (var other in _bindings.Where(b => b != item && b.VirtualKey == vk))
        {
            other.VirtualKey = 0;
            Settings.KeyBindings[other.Key] = 0;
        }
        item.VirtualKey = vk;
        Settings.KeyBindings[item.Key] = vk;
        Settings.Save();
    }

    private void OnResetKeys(object sender, RoutedEventArgs e)
    {
        Settings.KeyBindings = InputMap.DefaultKeyboard();
        Settings.Save();
        LoadBindings();
    }

    // --- Online ------------------------------------------------------------------

    private void OnNetworkChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || NetworkCombo.SelectedIndex < 0) return;
        Settings.Network = (NdsCore.NetPreference)NetworkCombo.SelectedIndex;
        Settings.Save();
        if (App.CoreAvailable) NdsCore.SetNetwork(Settings.Network);
        UpdateNetworkRow();
    }

    private void UpdateNetworkRow()
    {
        if (!App.CoreAvailable)
        {
            NetworkRow.Description = "Non disponibile";
            NetworkCombo.IsEnabled = false;
            return;
        }
        NdsCore.SetNetwork(Settings.Network);
        NetworkRow.Description = $"In uso: {NdsCore.NetDescription}";
        _ = UpdateFirewallRowAsync();
    }

    private async Task UpdateFirewallRowAsync()
    {
        bool allowed = await Firewall.RuleExistsAsync();
        LanRow.Description = allowed
            ? $"Il firewall accetta le console degli altri PC della rete locale (UDP {Firewall.RelayPort})."
            : "Serve a lotte e scambi tra due PC collegati allo stesso router: consenti la porta " +
              $"UDP {Firewall.RelayPort} nel Firewall di Windows, solo dalla rete locale. Va fatto su entrambi i PC.";
        FirewallButton.Content = allowed ? "Consentito" : "Consenti nel firewall";
        FirewallButton.IsEnabled = !allowed;
    }

    private async void OnAllowFirewall(object sender, RoutedEventArgs e)
    {
        FirewallButton.IsEnabled = false;
        if (!await Firewall.AllowAsync())
            await Dialogs.MessageAsync(XamlRoot, "Regola non creata",
                "Windows non ha creato la regola (forse la richiesta di autorizzazione è stata annullata). Puoi riprovare quando vuoi.");
        await UpdateFirewallRowAsync();
    }

    private void OnOpenNetworkLogs(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(LocalStore.LogDir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{LocalStore.LogDir}\"") { UseShellExecute = true });
    }

    private void OnDnsChanged(object sender, RoutedEventArgs e) => CommitDns();

    private void CommitDns()
    {
        if (IsIPv4(PrimaryDnsBox.Text)) Settings.PrimaryDns = PrimaryDnsBox.Text.Trim();
        else PrimaryDnsBox.Text = Settings.PrimaryDns;
        if (IsIPv4(SecondaryDnsBox.Text)) Settings.SecondaryDns = SecondaryDnsBox.Text.Trim();
        else SecondaryDnsBox.Text = Settings.SecondaryDns;
        Settings.Save();
    }

    private static bool IsIPv4(string text) =>
        IPAddress.TryParse(text.Trim(), out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;

    // --- Storage -----------------------------------------------------------------

    private async void OnClearCache(object sender, RoutedEventArgs e)
    {
        if (!await Dialogs.ConfirmAsync(XamlRoot, "Svuotare la cache?",
                "Le ROM verranno scaricate di nuovo dal server alla prossima partita. Salvataggi e stati non vengono toccati.",
                "Svuota"))
            return;
        try { LocalStore.ClearCache(); }
        catch (IOException ex) { await Dialogs.MessageAsync(XamlRoot, "Errore", ex.Message); }
        UpdateCacheRow();
    }

    private void OnOpenDataFolder(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(LocalStore.Root);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{LocalStore.Root}\"") { UseShellExecute = true });
    }
}
