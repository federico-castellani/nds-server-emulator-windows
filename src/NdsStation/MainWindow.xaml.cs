using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;
using NdsStation.Emulation;
using NdsStation.Services;
using NdsStation.Views;
using Windows.UI;
using WinRT.Interop;

namespace NdsStation;

public sealed partial class MainWindow : Window
{
    private bool _closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 860));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 720;
            presenter.PreferredMinimumHeight = 560;
        }

        Hwnd = WindowNative.GetWindowHandle(this);
        ApplyTheme(App.Settings.Theme);
        AppWindow.Closing += OnClosing;
    }

    /// <summary>Shows the first page; called once App.MainWindow is assigned.</summary>
    public void Start() => RootFrame.Navigate(typeof(LoginPage));

    public nint Hwnd { get; }

    public bool IsForeground => GetForegroundWindow() == Hwnd;

    public void NavigateRoot(Type page, object? parameter = null) =>
        RootFrame.Navigate(page, parameter, new DrillInNavigationTransitionInfo());

    public void GoBackRoot()
    {
        if (RootFrame.CanGoBack) RootFrame.GoBack();
    }

    public void ClearRootHistory() => RootFrame.BackStack.Clear();

    public void ApplyTheme(AppTheme theme)
    {
        if (Content is not FrameworkElement root) return;
        root.RequestedTheme = theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        bool dark = theme == AppTheme.Dark ||
                    (theme == AppTheme.System && Application.Current.RequestedTheme == ApplicationTheme.Dark);

        var titleBar = AppWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
        titleBar.ButtonHoverBackgroundColor = dark ? Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x10, 0, 0, 0);
        titleBar.ButtonHoverForegroundColor = dark ? Colors.White : Colors.Black;
    }

    public void SetTitle(string? subtitle) =>
        TitleText.Text = string.IsNullOrEmpty(subtitle) ? "NDS Station" : $"{subtitle} · NDS Station";

    public void SetFullScreen(bool fullScreen)
    {
        AppWindow.SetPresenter(fullScreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
        AppTitleBar.Visibility = fullScreen ? Visibility.Collapsed : Visibility.Visible;
    }

    public bool IsFullScreen => AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        // A running game must hand its save to the server before the process exits.
        if (!_closeConfirmed && RootFrame.Content is EmulatorPage emulator && emulator.HasSession)
        {
            args.Cancel = true;
            if (await emulator.EndSessionAsync())
            {
                _closeConfirmed = true;
                Close();
            }
            return;
        }

        _closeConfirmed = true;
        if (App.CoreAvailable) NdsCore.Shutdown();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
}
