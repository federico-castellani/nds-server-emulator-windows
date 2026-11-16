using System.ComponentModel;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NdsStation.Services;

namespace NdsStation.Views;

/// <summary>What's new in a release, and the download + install of it.</summary>
public static partial class UpdateDialog
{
    public static async Task ShowAsync(XamlRoot root, UpdateInfo info)
    {
        var notes = new TextBlock { Text = PlainText(info.Notes), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        var progress = new ProgressBar { Maximum = 1, Visibility = Visibility.Collapsed };
        var status = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Text = Updater.IsInstalled
                ? $"Versione in uso: {Updater.CurrentVersion}. NDS Station si chiuderà e riaprirà da solo."
                : $"Versione in uso: {Updater.CurrentVersion}. Questa è una copia portatile: scarica il setup dalla pagina della release.",
        };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new ScrollViewer { Content = notes, MaxHeight = 320 });
        panel.Children.Add(progress);
        panel.Children.Add(status);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = $"NDS Station {info.Version}",
            Content = panel,
            PrimaryButtonText = Updater.IsInstalled ? "Aggiorna ora" : "Apri la pagina di download",
            CloseButtonText = "Più tardi",
            DefaultButton = ContentDialogButton.Primary,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
        };

        using var cts = new CancellationTokenSource();
        string? downloaded = null;
        dialog.CloseButtonClick += (_, _) => cts.Cancel();
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            if (!Updater.IsInstalled)
            {
                Updater.OpenReleasePage(info);
                return;
            }

            args.Cancel = true;   // stay open while downloading
            var deferral = args.GetDeferral();
            dialog.IsPrimaryButtonEnabled = false;
            progress.Visibility = Visibility.Visible;
            progress.Value = 0;
            status.Text = "Download in corso…";
            try
            {
                var path = await Updater.DownloadAsync(info,
                    new Progress<double>(p =>
                    {
                        progress.Value = p;
                        status.Text = $"Download in corso… {p:P0}";
                    }), cts.Token);
                status.Text = "Installazione in corso: NDS Station si riaprirà tra poco.";
                downloaded = path;
                dialog.Hide();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Win32Exception)
            {
                // The user declined the administrator prompt of an all-users installation.
                status.Text = "Installazione annullata.";
                dialog.IsPrimaryButtonEnabled = true;
                progress.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or UpdateException)
            {
                status.Text = ex is UpdateException ? ex.Message : $"Download non riuscito: {ex.Message}";
                dialog.IsPrimaryButtonEnabled = true;
                progress.Visibility = Visibility.Collapsed;
            }
            finally
            {
                deferral.Complete();
            }
        };

        await dialog.ShowAsync();
        if (downloaded is not null) Updater.InstallAndExit(downloaded);
    }

    /// <summary>Release notes are Markdown; shown as plain text.</summary>
    private static string PlainText(string markdown)
    {
        var text = HeadingRegex().Replace(markdown.Replace("\r\n", "\n"), "");
        text = BoldRegex().Replace(text, "$1");
        text = LinkRegex().Replace(text, "$1");
        text = BulletRegex().Replace(text, "• ");
        return string.IsNullOrWhiteSpace(text) ? "Nessuna nota di rilascio." : text.Trim();
    }

    [GeneratedRegex(@"(?m)^#{1,6}\s*")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"\*\*(.+?)\*\*")]
    private static partial Regex BoldRegex();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)")]
    private static partial Regex LinkRegex();

    [GeneratedRegex(@"(?m)^\s*[-*]\s+")]
    private static partial Regex BulletRegex();
}
