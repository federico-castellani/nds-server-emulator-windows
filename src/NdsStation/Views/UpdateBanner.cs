using Microsoft.UI.Xaml.Controls;
using NdsStation.Services;

namespace NdsStation.Views;

/// <summary>
/// Turns an InfoBar into the "update available" banner: it opens when a check finds a
/// newer release (now or later, while the page is on screen) and its button starts the update.
/// </summary>
public static class UpdateBanner
{
    public static void Attach(InfoBar bar)
    {
        bar.Severity = InfoBarSeverity.Informational;
        bar.Title = "Aggiornamento disponibile";
        bar.IsOpen = false;

        var button = new Button { Content = "Aggiorna" };
        button.Click += async (_, _) =>
        {
            if (App.Updater.Available is { } info) await UpdateDialog.ShowAsync(bar.XamlRoot, info);
        };
        bar.ActionButton = button;

        void Show(UpdateInfo info)
        {
            bar.Message = $"NDS Station {info.Version} è pronto (in uso: {Updater.CurrentVersion}).";
            bar.IsOpen = true;
        }
        void OnFound(UpdateInfo info) => bar.DispatcherQueue.TryEnqueue(() => Show(info));

        bar.Loaded += (_, _) =>
        {
            App.Updater.Found += OnFound;
            if (App.Updater.Available is { } info) Show(info);
        };
        bar.Unloaded += (_, _) => App.Updater.Found -= OnFound;
    }
}
