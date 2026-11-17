using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NdsStation.Services;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace NdsStation.Views;

/// <summary>
/// An admin's view of a player's saves: download one, or replace it with a .sav file
/// (for example brought over from another emulator) to fix the player's progress.
/// </summary>
public static class UserSavesDialog
{
    private const long MaxSaveBytes = 4 * 1024 * 1024;   // the server's limit

    // DeSmuME appends this to its exports; the server removes it, sizes are compared without it.
    private static readonly byte[] DesmumeFooterEnd = Encoding.ASCII.GetBytes("|-DESMUME SAVE-|");
    private const int DesmumeFooterLength = 122;

    public static async Task ShowAsync(XamlRoot root, AdminUser user)
    {
        var status = new InfoBar { IsClosable = true };
        var loading = new ProgressBar { IsIndeterminate = true };
        var rows = new StackPanel { Spacing = 2 };
        var panel = new StackPanel { Spacing = 12, MinWidth = 480 };
        panel.Children.Add(new TextBlock
        {
            Text = "Puoi scaricare un salvataggio o sostituirlo con un file .sav, anche esportato da un altro emulatore. " +
                   "La versione sostituita resta nella cronologia del server; se il giocatore sta giocando su quella vecchia, " +
                   "all'uscita gli verrà chiesto quale tenere.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        panel.Children.Add(status);
        panel.Children.Add(loading);
        panel.Children.Add(new ScrollViewer { Content = rows, MaxHeight = 380 });

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = $"Salvataggi di {user.Username}",
            Content = panel,
            CloseButtonText = "Chiudi",
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
        };

        // Every message replaces the previous one, including a pending confirmation.
        void Say(string message, InfoBarSeverity severity = InfoBarSeverity.Error, string? title = null)
        {
            status.ActionButton = null;
            Dialogs.Show(status, message, severity, title);
        }

        async Task ReloadAsync()
        {
            loading.Visibility = Visibility.Visible;
            try
            {
                var games = await App.Api.GetGamesAsync();
                var saves = (await App.Api.GetUserSavesAsync(user.Id)).ToDictionary(s => s.GameId);
                rows.Children.Clear();
                foreach (var game in games.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase))
                    rows.Children.Add(Row(game, saves.GetValueOrDefault(game.Id)));
                if (games.Count == 0)
                    rows.Children.Add(new TextBlock { Text = "La libreria non contiene giochi." });
            }
            catch (ApiException ex)
            {
                Say(ex.Message, title: "Impossibile leggere i salvataggi");
            }
            finally
            {
                loading.Visibility = Visibility.Collapsed;
            }
        }

        Grid Row(GameInfo game, AdminSave? save)
        {
            var grid = new Grid { ColumnSpacing = 8, Padding = new Thickness(0, 6, 0, 6) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = game.Name, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock
            {
                Text = save is null
                    ? "Nessun salvataggio"
                    : $"{FormatSize(save.Size)}" + (save.UpdatedAt is { } at ? $" · aggiornato il {at.LocalDateTime:g}" : ""),
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
            grid.Children.Add(text);

            var download = new Button { Content = "Scarica", IsEnabled = save is not null };
            download.Click += async (_, _) => await DownloadAsync(game, save!);
            Grid.SetColumn(download, 1);
            grid.Children.Add(download);

            var replace = new Button { Content = save is null ? "Carica .sav…" : "Sostituisci…" };
            replace.Click += async (_, _) => await PickAndConfirmAsync(game, save);
            Grid.SetColumn(replace, 2);
            grid.Children.Add(replace);
            return grid;
        }

        async Task DownloadAsync(GameInfo game, AdminSave save)
        {
            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.Downloads, SuggestedFileName = $"{game.Name} - {user.Username}" };
            picker.FileTypeChoices.Add("Salvataggio DS", new List<string> { ".sav" });
            InitializeWithWindow.Initialize(picker, App.MainWindow.Hwnd);
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            try
            {
                var data = await App.Api.DownloadUserSaveAsync(user.Id, game.Id);
                await FileIO.WriteBytesAsync(file, data);
                Say($"Salvato in {file.Path}", InfoBarSeverity.Success, "Salvataggio scaricato");
            }
            catch (Exception ex) when (ex is ApiException or IOException or UnauthorizedAccessException)
            {
                Say(ex.Message, title: "Download non riuscito");
            }
        }

        async Task PickAndConfirmAsync(GameInfo game, AdminSave? save)
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };
            foreach (var type in new[] { ".sav", ".dsv", ".bin" }) picker.FileTypeFilter.Add(type);
            InitializeWithWindow.Initialize(picker, App.MainWindow.Hwnd);
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;

            byte[] data;
            try { data = await File.ReadAllBytesAsync(file.Path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Say(ex.Message, title: "Impossibile leggere il file");
                return;
            }
            if (data.Length == 0 || data.Length > MaxSaveBytes + DesmumeFooterLength)
            {
                Say("Il file è vuoto o troppo grande per essere un salvataggio DS.", title: "File non valido");
                return;
            }

            long size = RawSize(data);
            var message = save is null
                ? $"{game.Name}: verrà creato il salvataggio di {user.Username} con {file.Name} ({FormatSize(size)})."
                : $"{game.Name}: il salvataggio di {user.Username} ({FormatSize(save.Size)}) verrà sostituito con {file.Name} ({FormatSize(size)}).";
            if (save is not null && save.Size != size)
                message += " Le dimensioni sono diverse da quelle del salvataggio attuale: controlla che il file sia di questo gioco.";

            var confirm = new Button { Content = save is null ? "Carica" : "Sostituisci" };
            confirm.Click += async (_, _) =>
            {
                status.IsOpen = false;
                try
                {
                    var stored = await App.Api.ReplaceUserSaveAsync(user.Id, game.Id, file.Name, data);
                    Say($"{game.Name}: salvataggio di {user.Username} aggiornato ({FormatSize(stored)}).",
                        InfoBarSeverity.Success, "Fatto");
                    await ReloadAsync();
                }
                catch (ApiException ex)
                {
                    Say(ex.Message, title: "Caricamento non riuscito");
                }
            };
            Say(message, save is not null && save.Size != size ? InfoBarSeverity.Warning : InfoBarSeverity.Informational,
                save is null ? "Creare il salvataggio?" : "Sostituire il salvataggio?");
            status.ActionButton = confirm;
        }

        dialog.Opened += async (_, _) => await ReloadAsync();
        await dialog.ShowAsync();
    }

    private static long RawSize(byte[] data) =>
        data.Length > DesmumeFooterLength && data.AsSpan(data.Length - DesmumeFooterEnd.Length).SequenceEqual(DesmumeFooterEnd)
            ? data.Length - DesmumeFooterLength
            : data.Length;

    private static string FormatSize(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024):0.#} MB"
        : bytes >= 1024 ? $"{bytes / 1024.0:0.#} KB"
        : $"{bytes} byte";
}
