using System.Collections.ObjectModel;
using System.Security.Cryptography;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using NdsStation.Emulation;
using NdsStation.Services;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace NdsStation.Views;

public sealed partial class AdminPage : Page
{
    private List<AdminUserItem> _allUsers = new();

    public AdminPage()
    {
        InitializeComponent();
    }

    public ObservableCollection<AdminUserItem> Users { get; } = new();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        NewPasswordBox.Text = GeneratePassword();
        _ = LoadUsersAsync();
    }

    // --- Games -------------------------------------------------------------------

    private async void OnBrowseRom(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add(".nds");
        InitializeWithWindow.Initialize(picker, App.MainWindow.Hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        RomPathBox.Text = file.Path;
        UploadButtonText.Text = "Carica gioco";

        // Suggest a title from the ROM banner if the admin has not typed one.
        if (string.IsNullOrWhiteSpace(GameTitleBox.Text))
        {
            var banner = await Task.Run(() => RomBanner.TryRead(file.Path));
            var firstLine = banner?.Title?.Split('\n')[0].Trim();
            GameTitleBox.Text = !string.IsNullOrEmpty(firstLine) ? firstLine : Path.GetFileNameWithoutExtension(file.Path);
        }
    }

    private async void OnUpload(object sender, RoutedEventArgs e)
    {
        var title = GameTitleBox.Text.Trim();
        var path = RomPathBox.Text;
        if (title.Length == 0)
        {
            Dialogs.Show(UploadBar, "Inserisci un titolo.");
            return;
        }

        SetUploading(true);
        UploadBar.IsOpen = false;
        try
        {
            if (string.IsNullOrEmpty(path))
            {
                await App.Api.AddGameTitleAsync(title);
                Dialogs.Show(UploadBar, $"“{title}” è stato aggiunto alla libreria.", InfoBarSeverity.Success);
            }
            else
            {
                var progress = new Progress<double>(p =>
                {
                    UploadProgress.IsIndeterminate = false;
                    UploadProgress.Value = p;
                    UploadButtonText.Text = $"Caricamento… {p:P0}";
                });
                await App.Api.UploadGameAsync(title, path, progress);
                Dialogs.Show(UploadBar, $"“{title}” è stato caricato.", InfoBarSeverity.Success);
            }

            GameTitleBox.Text = "";
            RomPathBox.Text = "";
        }
        catch (ApiException ex)
        {
            Dialogs.Show(UploadBar, ex.Message, title: "Caricamento non riuscito");
        }
        catch (IOException ex)
        {
            Dialogs.Show(UploadBar, ex.Message, title: "Impossibile leggere il file");
        }
        finally
        {
            SetUploading(false);
        }
    }

    private void SetUploading(bool uploading)
    {
        UploadButton.IsEnabled = BrowseButton.IsEnabled = GameTitleBox.IsEnabled = !uploading;
        UploadProgress.Visibility = uploading ? Visibility.Visible : Visibility.Collapsed;
        UploadProgress.IsIndeterminate = true;
        UploadProgress.Value = 0;
        UploadButtonText.Text = string.IsNullOrEmpty(RomPathBox.Text) ? "Aggiungi titolo" : "Carica gioco";
    }

    // --- Users -------------------------------------------------------------------

    private void OnGeneratePassword(object sender, RoutedEventArgs e) => NewPasswordBox.Text = GeneratePassword();

    private async void OnCreateUser(object sender, RoutedEventArgs e)
    {
        var username = NewUserBox.Text.Trim();
        var password = NewPasswordBox.Text;
        if (username.Length == 0 || password.Length < Dialogs.MinPasswordLength)
        {
            Dialogs.Show(CreateUserBar, $"Inserisci un nome utente e una password di almeno {Dialogs.MinPasswordLength} caratteri.");
            return;
        }

        CreateUserButton.IsEnabled = false;
        CreateUserBar.IsOpen = false;
        try
        {
            await App.Api.RegisterUserAsync(username, password);
            Dialogs.Show(CreateUserBar, $"Account “{username}” creato. Password temporanea: {password}", InfoBarSeverity.Success);
            NewUserBox.Text = "";
            NewPasswordBox.Text = GeneratePassword();
            await LoadUsersAsync();
        }
        catch (ApiException ex)
        {
            Dialogs.Show(CreateUserBar, ex.Message, title: "Impossibile creare l'account");
        }
        finally
        {
            CreateUserButton.IsEnabled = true;
        }
    }

    private async Task LoadUsersAsync()
    {
        UsersLoading.Visibility = Visibility.Visible;
        try
        {
            var users = await App.Api.GetAdminUsersAsync();
            _allUsers = users.OrderBy(u => u.Id).Select(u => new AdminUserItem(u)).ToList();
            ApplyUserFilter();
        }
        catch (ApiException ex) when (ex.IsUnauthorized)
        {
            Dialogs.Show(UsersBar, "Il tuo account non ha i permessi di amministratore su questo server.");
        }
        catch (ApiException ex)
        {
            Dialogs.Show(UsersBar, ex.Message, title: "Impossibile caricare gli utenti");
        }
        finally
        {
            UsersLoading.Visibility = Visibility.Collapsed;
        }
    }

    private void ApplyUserFilter()
    {
        var query = UserSearchBox.Text.Trim();
        Users.Clear();
        foreach (var item in _allUsers.Where(u => query.Length == 0 || u.User.Username.Contains(query, StringComparison.CurrentCultureIgnoreCase)))
            Users.Add(item);
        UserCountText.Text = $"({_allUsers.Count})";
    }

    private void OnUserSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => ApplyUserFilter();

    private void OnRefreshUsers(object sender, RoutedEventArgs e) => _ = LoadUsersAsync();

    private async void OnResetPassword(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not AdminUserItem item) return;

        var passwordBox = new TextBox { Header = "Nuova password temporanea", Text = GeneratePassword(), FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas") };
        var content = new StackPanel { Spacing = 12, MinWidth = 340 };
        content.Children.Add(new TextBlock
        {
            Text = $"{item.User.Username} dovrà scegliere una nuova password al prossimo accesso.",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(passwordBox);

        var result = await Dialogs.ShowAsync(XamlRoot, "Reimposta password", content, "Reimposta");
        if (result != ContentDialogResult.Primary) return;
        if (passwordBox.Text.Length < Dialogs.MinPasswordLength)
        {
            Dialogs.Show(UsersBar, $"La password deve avere almeno {Dialogs.MinPasswordLength} caratteri.");
            return;
        }

        try
        {
            await App.Api.ResetUserPasswordAsync(item.User.Id, passwordBox.Text);
            Dialogs.Show(UsersBar, $"Password di {item.User.Username} reimpostata a: {passwordBox.Text}", InfoBarSeverity.Success);
            await LoadUsersAsync();
        }
        catch (ApiException ex)
        {
            Dialogs.Show(UsersBar, ex.Message, title: "Reimpostazione non riuscita");
        }
    }

    private async void OnUserSaves(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is AdminUserItem item) await UserSavesDialog.ShowAsync(XamlRoot, item.User);
    }

    private async void OnRenameUser(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not AdminUserItem item) return;

        var nameBox = new TextBox { Header = "Nuovo nome utente", Text = item.User.Username, IsSpellCheckEnabled = false, MaxLength = 50 };
        var content = new StackPanel { Spacing = 12, MinWidth = 340 };
        content.Children.Add(new TextBlock
        {
            Text = item.IsSelf
                ? "Dopo il cambio dovrai accedere di nuovo con il nuovo nome."
                : $"{item.User.Username} verrà disconnesso e dovrà accedere con il nuovo nome. Salvataggi, console e amici restano invariati.",
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(nameBox);
        nameBox.Loaded += (_, _) => { nameBox.Focus(FocusState.Programmatic); nameBox.SelectAll(); };

        if (await Dialogs.ShowAsync(XamlRoot, "Cambia nome utente", content, "Rinomina") != ContentDialogResult.Primary) return;
        var newName = nameBox.Text.Trim();
        if (newName.Length == 0 || newName == item.User.Username) return;

        try
        {
            await App.Api.RenameUserAsync(item.User.Id, newName);
        }
        catch (ApiException ex)
        {
            Dialogs.Show(UsersBar, ex.StatusCode == 409 ? $"Il nome “{newName}” è già in uso." : ex.Message, title: "Rinomina non riuscita");
            return;
        }

        if (item.IsSelf)
        {
            // This session ended with the rename: sign in again, with the new name filled in.
            App.Settings.Username = newName;
            ShellPage.SignOut($"Il tuo nome utente ora è “{newName}”: accedi di nuovo.");
            return;
        }
        Dialogs.Show(UsersBar, $"“{item.User.Username}” ora si chiama “{newName}”: dovrà accedere di nuovo con il nuovo nome.",
            InfoBarSeverity.Success);
        await LoadUsersAsync();
    }

    private async void OnDeleteUser(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not AdminUserItem item) return;
        if (!await Dialogs.ConfirmAsync(XamlRoot, "Eliminare l'utente?",
                $"L'account “{item.User.Username}” verrà eliminato dal server. L'operazione non si può annullare.",
                "Elimina"))
            return;

        try
        {
            await App.Api.DeleteUserAsync(item.User.Id);
            Dialogs.Show(UsersBar, $"Utente {item.User.Username} eliminato.", InfoBarSeverity.Success);
            await LoadUsersAsync();
        }
        catch (ApiException ex)
        {
            Dialogs.Show(UsersBar, ex.Message, title: "Eliminazione non riuscita");
        }
    }

    private static string GeneratePassword()
    {
        const string alphabet = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return string.Concat(Enumerable.Range(0, 10).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]));
    }
}
