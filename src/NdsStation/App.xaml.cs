using Microsoft.UI.Xaml;
using NdsStation.Emulation;
using NdsStation.Services;

namespace NdsStation;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            System.Diagnostics.Debug.WriteLine(e.Exception);
            WriteCrashLog(e.Exception);
        };
    }

    public static ApiClient Api { get; } = new();

    public static AppSettings Settings { get; private set; } = new();

    public static MainWindow MainWindow { get; private set; } = null!;

    /// <summary>False when ndscore.dll could not be loaded; the library still works.</summary>
    public static bool CoreAvailable { get; private set; }

    public static string? CoreError { get; private set; }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Settings = AppSettings.Load();
        Api.BaseUrl = SafeUrl(Settings.ServerUrl);

        try
        {
            if (NdsCore.GetApiVersion() != NdsCore.ApiVersion)
                CoreError = "Versione di ndscore.dll non compatibile.";
            else if (NdsCore.Init() == 0)
                CoreError = NdsCore.LastError;
            else
                CoreAvailable = true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            CoreError = $"Impossibile caricare ndscore.dll: {ex.Message}";
        }

        MainWindow = new MainWindow();
        MainWindow.Start();
        MainWindow.Activate();
    }

    private static string SafeUrl(string url)
    {
        try { return ApiClient.NormalizeBaseUrl(url).ToString(); }
        catch (ApiException) { return "http://localhost:3000"; }
    }

    private static void WriteCrashLog(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(LocalStore.Root);
            File.AppendAllText(Path.Combine(LocalStore.Root, "crash.log"), $"[{DateTime.Now:O}] {ex}\n\n");
        }
        catch (IOException) { }
    }
}
