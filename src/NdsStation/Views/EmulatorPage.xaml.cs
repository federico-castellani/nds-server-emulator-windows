using System.Net;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using NdsStation.Emulation;
using NdsStation.Services;
using Windows.Foundation;
using Windows.Graphics.DirectX;

namespace NdsStation.Views;

public sealed partial class EmulatorPage : Page
{
    private const int StateSlots = 3;
    private const float ScreenGap = 12f;

    private readonly byte[] _frame = new byte[NdsCore.FrameBytes];
    private readonly HashSet<int> _heldHotkeys = new();
    private readonly DispatcherQueueTimer _toastTimer;

    private GameInfo _game = null!;
    private SaveSync? _sync;
    private CanvasBitmap? _bitmap;
    private CancellationTokenSource? _loading;
    private Task<bool>? _ending;

    private ulong _frameId;
    private uint _saveCounter;
    private uint _firmwareCounter;
    private byte[]? _firmware;
    private bool _running;
    private bool _paused;
    private bool _touching;
    private Rect _topRect, _bottomRect;
    private DateTime _lastFpsUpdate;

    public EmulatorPage()
    {
        InitializeComponent();
        _toastTimer = DispatcherQueue.CreateTimer();
        _toastTimer.Interval = TimeSpan.FromSeconds(3);
        _toastTimer.Tick += (_, _) => { Toast.IsOpen = false; _toastTimer.Stop(); };
    }

    /// <summary>True from the moment a game starts loading until its save is safely handled.</summary>
    public bool HasSession { get; private set; }

    private static AppSettings Settings => App.Settings;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _game = (GameInfo)e.Parameter;
        GameTitle.Text = _game.Name;
        App.MainWindow.SetTitle(_game.Name);

        VolumeSlider.Value = Math.Round(Settings.Volume * 100);
        UpdateVolumeIcon();
        SyncLayoutMenu();

        _ = StartAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        StopRenderLoop();
        if (App.MainWindow.IsFullScreen) App.MainWindow.SetFullScreen(false);
        Canvas.RemoveFromVisualTree();
    }

    // --- Startup ------------------------------------------------------------------

    private async Task StartAsync()
    {
        HasSession = true;
        _loading = new CancellationTokenSource();
        var ct = _loading.Token;
        ShowOverlay("Preparazione del gioco", "Download della ROM…", cancellable: true);

        try
        {
            var romTask = LoadRomAsync(ct);
            var saveTask = LoadServerSaveAsync(ct);
            var firmwareTask = ConsoleIdentity.ResolveAsync(App.Api, ct);
            var rom = await romTask;
            OverlayText.Text = "Download del salvataggio…";
            OverlayProgress.IsIndeterminate = true;
            var (serverSave, serverSaveOk) = await saveTask;
            _firmware = await firmwareTask;

            var save = await ChooseSaveAsync(serverSave, serverSaveOk);
            if (save is null)
            {
                await LeaveWithoutSessionAsync();
                return;
            }

            if (!Boot(rom, save.Value.Data))
            {
                await Dialogs.MessageAsync(XamlRoot, "Impossibile avviare il gioco", NdsCore.LastError);
                await LeaveWithoutSessionAsync();
                return;
            }

            _sync = new SaveSync(App.Api, _game.Id, serverSaveOk ? serverSave : null);
            _sync.StateChanged += state => DispatcherQueue.TryEnqueue(() => UpdateSyncIndicator(state));
            if (save.Value.FromLocalBackup)
            {
                _sync.MarkDirty();
                _ = _sync.FlushAsync();
            }
            UpdateSyncIndicator(save.Value.FromLocalBackup ? SyncState.Pending : SyncState.Synced);

            HideOverlay();
            StartRenderLoop();
        }
        catch (OperationCanceledException)
        {
            await LeaveWithoutSessionAsync();
        }
        catch (ApiException ex) when (ex.IsUnauthorized)
        {
            HasSession = false;
            ShellPage.SignOut("La sessione è scaduta. Accedi di nuovo.");
        }
        catch (ApiException ex)
        {
            await Dialogs.MessageAsync(XamlRoot, "Impossibile avviare il gioco", ex.Message);
            await LeaveWithoutSessionAsync();
        }
    }

    private async Task<byte[]> LoadRomAsync(CancellationToken ct)
    {
        if (await LocalStore.ReadCachedRomAsync(_game.Id) is byte[] cached)
            return cached;

        var progress = new Progress<double>(p =>
        {
            OverlayProgress.IsIndeterminate = false;
            OverlayProgress.Value = p;
            OverlayText.Text = $"Download della ROM… {p:P0}";
        });
        var rom = await App.Api.DownloadRomAsync(_game.Id, progress, ct);
        try { await LocalStore.WriteCachedRomAsync(_game.Id, rom); }
        catch (IOException) { }
        return rom;
    }

    private async Task<(byte[]? Save, bool Ok)> LoadServerSaveAsync(CancellationToken ct)
    {
        try
        {
            return (await App.Api.DownloadSaveAsync(_game.Id, ct), true);
        }
        catch (ApiException ex) when (!ex.IsUnauthorized)
        {
            return (null, false);
        }
    }

    /// <summary>
    /// Decides which save to boot with. Returns null when the user backs out.
    /// Never starts from an empty save when the server one could not be read, since the
    /// first auto-upload would then overwrite real progress.
    /// </summary>
    private async Task<(byte[]? Data, bool FromLocalBackup)?> ChooseSaveAsync(byte[]? serverSave, bool serverOk)
    {
        var local = LocalStore.ReadSaveBackup(_game.Id);
        var localTime = LocalStore.SaveBackupTime(_game.Id);

        if (serverOk && LocalStore.HasPendingSave(_game.Id) && local is not null &&
            (serverSave is null || !local.AsSpan().SequenceEqual(serverSave)))
        {
            var choice = await Dialogs.ShowAsync(XamlRoot, "Salvataggio non sincronizzato",
                new TextBlock
                {
                    Text = $"Su questo PC c'è un salvataggio del {localTime:g} che non è mai arrivato sul server " +
                           "(per esempio perché la connessione era assente). Quale vuoi usare?",
                    TextWrapping = TextWrapping.Wrap,
                },
                "Usa quello locale", "Usa quello del server", "Annulla");
            return choice switch
            {
                ContentDialogResult.Primary => (local, true),
                ContentDialogResult.Secondary => (serverSave, false),
                _ => null,
            };
        }

        if (serverOk) return (serverSave, false);

        while (true)
        {
            var hasLocal = local is not null;
            var choice = await Dialogs.ShowAsync(XamlRoot, "Salvataggio non disponibile",
                new TextBlock
                {
                    Text = hasLocal
                        ? $"Non è stato possibile scaricare il salvataggio dal server. Puoi usare la copia locale del {localTime:g}."
                        : "Non è stato possibile scaricare il salvataggio dal server. Per non sovrascriverlo, il gioco non viene avviato.",
                    TextWrapping = TextWrapping.Wrap,
                },
                "Riprova", hasLocal ? "Usa la copia locale" : null, "Annulla");

            if (choice == ContentDialogResult.Secondary) return (local, true);
            if (choice != ContentDialogResult.Primary) return null;

            ShowOverlay("Preparazione del gioco", "Download del salvataggio…", cancellable: false);
            var (retry, ok) = await LoadServerSaveAsync(CancellationToken.None);
            if (ok) return await ChooseSaveAsync(retry, true);
        }
    }

    private bool Boot(byte[] rom, byte[]? save)
    {
        NdsCore.SetVolume((float)Settings.Volume);
        NdsCore.SetFastForwardAudio(Settings.FastForwardSound);
        NdsCore.SetSpeed(1);
        NdsCore.SetNetwork(Settings.Network);
        // An existing console keeps its own MAC address; the account-derived one is only
        // for a brand new console.
        bool ok = NdsCore.Boot(rom, save, _game.Id, Settings.Username,
            ParseIp(Settings.PrimaryDns), ParseIp(Settings.SecondaryDns),
            _firmware, _firmware is null ? LocalStore.ConsoleMac : new byte[6]);
        if (ok)
        {
            _saveCounter = NdsCore.GetSaveCounter();
            _firmwareCounter = NdsCore.GetFirmwareCounter();
            // Keep a new console's firmware right away, so its MAC address and profile
            // stay the same even before the console ever writes to it.
            if (_firmware is null) SaveFirmware();
            _frameId = 0;
            _paused = false;
        }
        return ok;
    }

    private static byte[] ParseIp(string text) =>
        IPAddress.TryParse(text, out var ip) && ip.GetAddressBytes() is { Length: 4 } bytes ? bytes : new byte[4];

    private static void SaveFirmware()
    {
        if (NdsCore.GetFirmware() is byte[] firmware) ConsoleIdentity.Save(App.Api, firmware);
    }

    private void SaveNetworkLog()
    {
        try
        {
            var report = NdsCore.NetReport;
            // Only sessions where the game went online are worth keeping.
            if (report.Contains("NAT negotiation") || report.Contains("GameSpy") || report.Contains("ICMP tipo"))
                LocalStore.WriteNetworkLog(_game.Name, report);
        }
        catch (IOException) { }
    }

    private async Task LeaveWithoutSessionAsync()
    {
        HasSession = false;
        if (App.CoreAvailable) NdsCore.Unload();
        await Task.Yield();
        App.MainWindow.GoBackRoot();
    }

    // --- Ending the session ---------------------------------------------------------

    private async void OnBack(object sender, RoutedEventArgs e)
    {
        if (await EndSessionAsync()) App.MainWindow.GoBackRoot();
    }

    private void OnCancelLoading(object sender, RoutedEventArgs e) => _loading?.Cancel();

    /// <summary>
    /// Pauses the game and makes sure its save reached the server (or the user chose to
    /// keep it locally). Returns false if the user went back to the game instead.
    /// </summary>
    public Task<bool> EndSessionAsync()
    {
        if (!HasSession) return Task.FromResult(true);
        if (_loading is { IsCancellationRequested: false } && !_running)
        {
            _loading.Cancel();
            return Task.FromResult(true);
        }
        return _ending ??= EndSessionCoreAsync();
    }

    private async Task<bool> EndSessionCoreAsync()
    {
        try
        {
            if (App.MainWindow.IsFullScreen) SetFullScreen(false);
            NdsCore.SetPaused(1);
            FpsText.Text = "— FPS";
            NdsCore.SetInput(0);
            NdsCore.SetTouch(0, 0, 0);

            if (_sync is not null)
            {
                _sync.CancelPending();
                bool force = false;
                while (true)
                {
                    ShowOverlay("Sincronizzazione", "Invio del salvataggio al server…", cancellable: false);
                    if (await _sync.FlushAsync(force: force)) break;

                    // Another device saved meanwhile: overwriting would lose that progress,
                    // so it is the player's call.
                    bool conflict = _sync.State == SyncState.Conflict;
                    var choice = conflict
                        ? await Dialogs.ShowAsync(XamlRoot, "Salvataggio più recente sul server",
                            new TextBlock
                            {
                                Text = $"{_sync.LastError}\n\nPuoi sovrascriverlo con questa partita, oppure uscire senza inviarla: " +
                                       "resterà su questo PC e ti verrà proposta al prossimo avvio.",
                                TextWrapping = TextWrapping.Wrap,
                            },
                            "Sovrascrivi il server", "Esci senza inviare", "Torna al gioco")
                        : await Dialogs.ShowAsync(XamlRoot, "Salvataggio non inviato",
                            new TextBlock
                            {
                                Text = $"{_sync.LastError}\n\nSe esci ora il salvataggio resta su questo PC e ti verrà proposto alla prossima partita.",
                                TextWrapping = TextWrapping.Wrap,
                            },
                            "Riprova", "Esci comunque", "Torna al gioco");

                    if (choice == ContentDialogResult.Secondary) break;
                    if (choice != ContentDialogResult.Primary)
                    {
                        HideOverlay();
                        NdsCore.SetPaused(_paused ? 1 : 0);
                        return false;
                    }
                    force = conflict;
                }
            }

            StopRenderLoop();
            SaveNetworkLog();
            if (NdsCore.GetFirmwareCounter() != 0) SaveFirmware();
            NdsCore.Unload();
            _sync?.Dispose();
            _sync = null;
            HasSession = false;
            Settings.Save();
            return true;
        }
        finally
        {
            _ending = null;
        }
    }

    // --- Frame loop -------------------------------------------------------------------

    private void StartRenderLoop()
    {
        _running = true;
        _lastFpsUpdate = DateTime.UtcNow;
        StatusPanel.Visibility = GameCommands.Visibility = Visibility.Visible;
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopRenderLoop()
    {
        _running = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, object e)
    {
        if (!_running) return;

        bool foreground = App.MainWindow.IsForeground;
        uint pad = NdsCore.GetGamepadButtons();
        uint input = 0;
        bool fastHeld = false;
        if (foreground)
        {
            input = InputMap.ReadKeyboard(Settings.KeyBindings) | InputMap.MapGamepad(pad, Settings.PadLayout);
            fastHeld = InputMap.IsDown(InputMap.VkSpace) || (pad & InputMap.PadRTrigger) != 0;
            HandleHotkeys();
        }
        NdsCore.SetInput(input);

        bool fast = fastHeld || FastForwardButton.IsChecked == true;
        NdsCore.SetSpeed(fast ? Settings.FastForwardSpeed : 1f);

        ulong id = NdsCore.CopyFrame(_frame, _frameId);
        if (id != 0)
        {
            _frameId = id;
            _bitmap?.SetPixelBytes(_frame);
            Canvas.Invalidate();
        }

        uint counter = NdsCore.GetSaveCounter();
        if (counter != _saveCounter)
        {
            _saveCounter = counter;
            _sync?.OnSaveWritten();
        }

        // The console writes its firmware when it registers with Nintendo WFC (user ID)
        // or changes Wi-Fi settings; it must outlive the session.
        uint firmwareCounter = NdsCore.GetFirmwareCounter();
        if (firmwareCounter != _firmwareCounter)
        {
            _firmwareCounter = firmwareCounter;
            SaveFirmware();
        }

        var now = DateTime.UtcNow;
        if (now - _lastFpsUpdate > TimeSpan.FromMilliseconds(500))
        {
            _lastFpsUpdate = now;
            FpsText.Text = _paused ? "— FPS" : $"{NdsCore.GetFps():0} FPS";
        }
    }

    private void HandleHotkeys()
    {
        if (Pressed(InputMap.VkP)) TogglePause();
        if (Pressed(InputMap.VkF11)) SetFullScreen(!App.MainWindow.IsFullScreen);
        if (Pressed(InputMap.VkEscape) && App.MainWindow.IsFullScreen) SetFullScreen(false);
        if (Pressed(InputMap.VkF5)) SaveState(1);
        if (Pressed(InputMap.VkF8)) LoadState(1);
    }

    /// <summary>True once per key press (edge-triggered).</summary>
    private bool Pressed(int vk)
    {
        bool down = InputMap.IsDown(vk);
        if (down && _heldHotkeys.Add(vk)) return true;
        if (!down) _heldHotkeys.Remove(vk);
        return false;
    }

    // --- Drawing ------------------------------------------------------------------------

    private void OnCreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args)
    {
        _bitmap = CanvasBitmap.CreateFromBytes(sender, _frame, NdsCore.ScreenWidth, NdsCore.ScreenHeight * 2,
            DirectXPixelFormat.B8G8R8A8UIntNormalized);
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        ComputeLayout((float)sender.ActualWidth, (float)sender.ActualHeight);
        if (_bitmap is null || _frameId == 0) return;

        var ds = args.DrawingSession;
        ds.Antialiasing = CanvasAntialiasing.Aliased;
        var w = NdsCore.ScreenWidth;
        var h = NdsCore.ScreenHeight;
        ds.DrawImage(_bitmap, _topRect, new Rect(0, 0, w, h), 1f, CanvasImageInterpolation.NearestNeighbor);
        ds.DrawImage(_bitmap, _bottomRect, new Rect(0, h, w, h), 1f, CanvasImageInterpolation.NearestNeighbor);

        var border = Windows.UI.Color.FromArgb(0x30, 0x80, 0x80, 0x80);
        ds.DrawRectangle(Inflate(_topRect, 0.5), border);
        ds.DrawRectangle(Inflate(_bottomRect, 0.5), border);
    }

    private static Rect Inflate(Rect r, double d) => new(r.X - d, r.Y - d, r.Width + 2 * d, r.Height + 2 * d);

    private void ComputeLayout(float width, float height)
    {
        const float w = NdsCore.ScreenWidth, h = NdsCore.ScreenHeight;
        const float margin = 24f;
        const float smallRatio = 0.42f;
        float availW = Math.Max(1, width - 2 * margin), availH = Math.Max(1, height - 2 * margin);

        // Total content size at scale 1 for each layout.
        (float cw, float ch) = Settings.ScreenLayout switch
        {
            ScreenLayout.Horizontal => (2 * w + ScreenGap, h),
            ScreenLayout.TopFocus => (w + ScreenGap + w * smallRatio, h),
            _ => (w, 2 * h + ScreenGap),
        };

        float scale = Math.Min(availW / cw, availH / ch);
        if (Settings.IntegerScaling && scale >= 1) scale = MathF.Floor(scale);
        scale = Math.Max(scale, 0.25f);

        float sw = w * scale, sh = h * scale, gap = ScreenGap * Math.Min(scale, 2);
        float totalW, totalH;
        switch (Settings.ScreenLayout)
        {
            case ScreenLayout.Horizontal:
                totalW = 2 * sw + gap; totalH = sh;
                float x0 = (width - totalW) / 2, y0 = (height - totalH) / 2;
                _topRect = new Rect(x0, y0, sw, sh);
                _bottomRect = new Rect(x0 + sw + gap, y0, sw, sh);
                break;

            case ScreenLayout.TopFocus:
                float bw = sw * smallRatio, bh = sh * smallRatio;
                totalW = sw + gap + bw; totalH = sh;
                float x1 = (width - totalW) / 2, y1 = (height - totalH) / 2;
                _topRect = new Rect(x1, y1, sw, sh);
                _bottomRect = new Rect(x1 + sw + gap, y1 + sh - bh, bw, bh);
                break;

            default:
                totalW = sw; totalH = 2 * sh + gap;
                float x2 = (width - totalW) / 2, y2 = (height - totalH) / 2;
                _topRect = new Rect(x2, y2, sw, sh);
                _bottomRect = new Rect(x2, y2 + sh + gap, sw, sh);
                break;
        }

        // Snap to whole pixels so nearest-neighbour scaling stays even.
        _topRect = Snap(_topRect);
        _bottomRect = Snap(_bottomRect);
    }

    private static Rect Snap(Rect r) => new(Math.Round(r.X), Math.Round(r.Y), Math.Round(r.Width), Math.Round(r.Height));

    // --- Stylus -------------------------------------------------------------------------

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_running) return;
        var point = e.GetCurrentPoint(Canvas).Position;
        if (!_bottomRect.Contains(point)) return; // touches outside the bottom screen do nothing
        _touching = Canvas.CapturePointer(e.Pointer);
        UpdateTouch(point);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_touching) UpdateTouch(e.GetCurrentPoint(Canvas).Position);
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_touching) return;
        _touching = false;
        NdsCore.SetTouch(0, 0, 0);
        Canvas.ReleasePointerCaptures();
    }

    private void UpdateTouch(Point p)
    {
        double x = (p.X - _bottomRect.X) / _bottomRect.Width * NdsCore.ScreenWidth;
        double y = (p.Y - _bottomRect.Y) / _bottomRect.Height * NdsCore.ScreenHeight;
        NdsCore.SetTouch(1, (int)Math.Clamp(x, 0, NdsCore.ScreenWidth - 1), (int)Math.Clamp(y, 0, NdsCore.ScreenHeight - 1));
    }

    // --- Toolbar ------------------------------------------------------------------------

    private void OnPauseClick(object sender, RoutedEventArgs e) => SetPaused(PauseButton.IsChecked == true);

    private void TogglePause() => SetPaused(!_paused);

    private void SetPaused(bool paused)
    {
        _paused = paused;
        PauseButton.IsChecked = paused;
        PausedBadge.Visibility = paused ? Visibility.Visible : Visibility.Collapsed;
        NdsCore.SetPaused(paused ? 1 : 0);
    }

    private void OnVolumeChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        Settings.Volume = e.NewValue / 100.0;
        if (App.CoreAvailable) NdsCore.SetVolume((float)Settings.Volume);
        UpdateVolumeIcon();
    }

    private void UpdateVolumeIcon() => VolumeIcon.Glyph = Settings.Volume switch
    {
        <= 0 => "",
        < 0.34 => "",
        < 0.67 => "",
        _ => "",
    };

    private void OnLayoutClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string tag && Enum.TryParse<ScreenLayout>(tag, out var layout))
        {
            Settings.ScreenLayout = layout;
            Settings.Save();
            Canvas.Invalidate();
        }
    }

    private void OnIntegerScalingClick(object sender, RoutedEventArgs e)
    {
        Settings.IntegerScaling = IntegerScalingItem.IsChecked;
        Settings.Save();
        Canvas.Invalidate();
    }

    private void SyncLayoutMenu()
    {
        LayoutVertical.IsChecked = Settings.ScreenLayout == ScreenLayout.Vertical;
        LayoutHorizontal.IsChecked = Settings.ScreenLayout == ScreenLayout.Horizontal;
        LayoutTopFocus.IsChecked = Settings.ScreenLayout == ScreenLayout.TopFocus;
        IntegerScalingItem.IsChecked = Settings.IntegerScaling;
    }

    private void OnFullScreenClick(object sender, RoutedEventArgs e) => SetFullScreen(!App.MainWindow.IsFullScreen);

    private void SetFullScreen(bool fullScreen)
    {
        App.MainWindow.SetFullScreen(fullScreen);
        Toolbar.Visibility = fullScreen ? Visibility.Collapsed : Visibility.Visible;
        FullScreenIcon.Glyph = fullScreen ? "" : "";
        if (fullScreen) ShowToast("Premi Esc o F11 per uscire dallo schermo intero.", InfoBarSeverity.Informational);
    }

    // --- Save states ----------------------------------------------------------------------

    private void OnStatesMenuOpening(object? sender, object e)
    {
        StatesMenu.Items.Clear();
        for (int slot = 1; slot <= StateSlots; slot++)
        {
            int s = slot;
            var save = new MenuFlyoutItem { Text = $"Salva nello slot {s}" + (s == 1 ? "  (F5)" : ""), Icon = new FontIcon { Glyph = "" } };
            save.Click += (_, _) => SaveState(s);
            StatesMenu.Items.Add(save);
        }
        StatesMenu.Items.Add(new MenuFlyoutSeparator());
        for (int slot = 1; slot <= StateSlots; slot++)
        {
            int s = slot;
            var time = LocalStore.StateTime(_game.Id, s);
            var load = new MenuFlyoutItem
            {
                Text = time is null ? $"Slot {s}: vuoto" : $"Carica slot {s} · {time:g}" + (s == 1 ? "  (F8)" : ""),
                IsEnabled = time is not null,
                Icon = new FontIcon { Glyph = "" },
            };
            load.Click += (_, _) => LoadState(s);
            StatesMenu.Items.Add(load);
        }
    }

    private void SaveState(int slot)
    {
        if (!_running) return;
        var state = NdsCore.SaveState();
        if (state is null)
        {
            ShowToast(NdsCore.LastError, InfoBarSeverity.Error);
            return;
        }
        try
        {
            LocalStore.WriteState(_game.Id, slot, state);
            ShowToast($"Stato salvato nello slot {slot}.", InfoBarSeverity.Success);
        }
        catch (IOException ex)
        {
            ShowToast(ex.Message, InfoBarSeverity.Error);
        }
    }

    private void LoadState(int slot)
    {
        if (!_running) return;
        var state = LocalStore.ReadState(_game.Id, slot);
        if (state is null)
        {
            ShowToast($"Lo slot {slot} è vuoto.", InfoBarSeverity.Warning);
            return;
        }
        if (NdsCore.LoadState(state))
        {
            // The state carries its own copy of the cartridge save.
            _sync?.MarkDirty();
            _sync?.OnSaveWritten();
            ShowToast($"Stato dello slot {slot} caricato.", InfoBarSeverity.Success);
        }
        else
            ShowToast(NdsCore.LastError, InfoBarSeverity.Error);
    }

    // --- Status UI ----------------------------------------------------------------------

    private void UpdateSyncIndicator(SyncState state)
    {
        (string glyph, string text, string tooltip, Brush brush) = state switch
        {
            SyncState.Pending => ("", "In attesa", "Modifiche salvate su questo PC, verranno inviate tra poco", Brush("TextFillColorSecondaryBrush")),
            SyncState.Uploading => ("", "Invio…", "Invio del salvataggio al server", Brush("AccentTextFillColorPrimaryBrush")),
            SyncState.Failed => ("", "Non inviato", $"{_sync?.LastError} Il salvataggio è al sicuro su questo PC.", Brush("SystemFillColorCautionBrush")),
            SyncState.Conflict => ("", "Più recente sul server", $"{_sync?.LastError} Uscendo potrai scegliere quale tenere.", Brush("SystemFillColorCautionBrush")),
            _ => ("", "Sincronizzato", "Il salvataggio sul server è aggiornato", Brush("SystemFillColorSuccessBrush")),
        };
        SyncIcon.Glyph = glyph;
        SyncIcon.Foreground = brush;
        SyncText.Text = text;
        ToolTipService.SetToolTip(SyncPanel, tooltip);
    }

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    private void ShowOverlay(string title, string text, bool cancellable)
    {
        OverlayTitle.Text = title;
        OverlayText.Text = text;
        OverlayProgress.IsIndeterminate = true;
        OverlayCancel.Visibility = cancellable ? Visibility.Visible : Visibility.Collapsed;
        Overlay.Visibility = Visibility.Visible;
    }

    private void HideOverlay() => Overlay.Visibility = Visibility.Collapsed;

    private void ShowToast(string message, InfoBarSeverity severity)
    {
        Toast.Severity = severity;
        Toast.Message = message;
        Toast.IsOpen = true;
        _toastTimer.Stop();
        _toastTimer.Start();
    }
}
