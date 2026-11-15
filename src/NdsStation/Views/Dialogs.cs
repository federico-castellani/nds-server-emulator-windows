using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NdsStation.Services;

namespace NdsStation.Views;

/// <summary>ContentDialog helpers shared by the pages.</summary>
public static class Dialogs
{
    public const int MinPasswordLength = 6;

    public static async Task<ContentDialogResult> ShowAsync(XamlRoot root, string title, object content,
        string primary, string? secondary = null, string? close = "Annulla",
        ContentDialogButton defaultButton = ContentDialogButton.Primary)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = content,
            PrimaryButtonText = primary,
            SecondaryButtonText = secondary ?? "",
            CloseButtonText = close ?? "",
            DefaultButton = defaultButton,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
        };
        return await dialog.ShowAsync();
    }

    public static Task MessageAsync(XamlRoot root, string title, string message) =>
        ShowAsync(root, title, new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, "OK", close: null);

    public static async Task<bool> ConfirmAsync(XamlRoot root, string title, string message, string confirm) =>
        await ShowAsync(root, title, new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, confirm,
            defaultButton: ContentDialogButton.Close) == ContentDialogResult.Primary;

    /// <summary>
    /// Asks for a new password (twice) and sends it to the server. Returns true once the
    /// server accepted it. When <paramref name="forced"/> is set the dialog explains why.
    /// </summary>
    public static async Task<bool> ChangePasswordAsync(XamlRoot root, bool forced)
    {
        var first = new PasswordBox { Header = "Nuova password", PlaceholderText = $"Almeno {MinPasswordLength} caratteri" };
        var second = new PasswordBox { Header = "Conferma password" };
        var error = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false };
        var panel = new StackPanel { Spacing = 12, MinWidth = 320 };
        if (forced)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Per motivi di sicurezza devi scegliere una nuova password prima di continuare.",
                TextWrapping = TextWrapping.Wrap,
            });
        }
        panel.Children.Add(first);
        panel.Children.Add(second);
        panel.Children.Add(error);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = forced ? "Imposta una nuova password" : "Cambia password",
            Content = panel,
            PrimaryButtonText = "Salva",
            CloseButtonText = forced ? "Esci" : "Annulla",
            DefaultButton = ContentDialogButton.Primary,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
        };

        bool changed = false;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                if (first.Password.Length < MinPasswordLength)
                {
                    Show(error, $"La password deve avere almeno {MinPasswordLength} caratteri.");
                    args.Cancel = true;
                    return;
                }
                if (first.Password != second.Password)
                {
                    Show(error, "Le due password non coincidono.");
                    args.Cancel = true;
                    return;
                }

                dialog.IsPrimaryButtonEnabled = false;
                try
                {
                    await App.Api.ChangePasswordAsync(first.Password);
                    changed = true;
                }
                catch (ApiException ex)
                {
                    Show(error, ex.Message);
                    args.Cancel = true;
                }
                finally
                {
                    dialog.IsPrimaryButtonEnabled = true;
                }
            }
            finally
            {
                deferral.Complete();
            }
        };

        await dialog.ShowAsync();
        return changed;
    }

    public static void Show(InfoBar bar, string message, InfoBarSeverity severity = InfoBarSeverity.Error, string? title = null)
    {
        bar.Severity = severity;
        bar.Title = title ?? "";
        bar.Message = message;
        bar.IsOpen = true;
    }
}
