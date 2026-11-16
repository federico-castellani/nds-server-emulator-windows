using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using NdsStation.Services;

namespace NdsStation.Views;

public sealed partial class ShellPage : Page
{
    public ShellPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        App.MainWindow.SetTitle(null);
        AdminItem.Visibility = App.Settings.IsAdmin ? Visibility.Visible : Visibility.Collapsed;
        AccountItem.Content = App.Settings.Username;
        AccountHeader.Text = $"{App.Settings.Username} · {App.Api.BaseUrl}";

        if (ContentFrame.Content is null)
        {
            NavView.SelectedItem = LibraryItem;
            ContentFrame.Navigate(typeof(LibraryPage));
        }

        App.Updater.Found += OnUpdateFound;
        if (App.Updater.Available is { } update) ShowUpdate(update);
        _ = App.Updater.CheckInBackgroundAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        App.Updater.Found -= OnUpdateFound;
    }

    private void OnUpdateFound(UpdateInfo info) => DispatcherQueue.TryEnqueue(() => ShowUpdate(info));

    private void ShowUpdate(UpdateInfo info)
    {
        UpdateBar.Message = $"NDS Station {info.Version} è pronto (in uso: {Updater.CurrentVersion}).";
        UpdateBar.IsOpen = true;
    }

    private async void OnUpdate(object sender, RoutedEventArgs e)
    {
        if (App.Updater.Available is { } info) await UpdateDialog.ShowAsync(XamlRoot, info);
    }

    private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        Type page = args.IsSettingsSelected
            ? typeof(SettingsPage)
            : (args.SelectedItem as NavigationViewItem)?.Tag switch
            {
                "admin" => typeof(AdminPage),
                _ => typeof(LibraryPage),
            };

        if (ContentFrame.CurrentSourcePageType != page)
            ContentFrame.Navigate(page, null, args.RecommendedNavigationTransitionInfo ?? new EntranceNavigationTransitionInfo());
    }

    // ItemInvoked (unlike Tapped) also fires for keyboard and screen-reader activation.
    private void OnItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer == AccountItem) FlyoutBase.ShowAttachedFlyout(AccountItem);
    }

    private async void OnChangePassword(object sender, RoutedEventArgs e)
    {
        if (await Dialogs.ChangePasswordAsync(XamlRoot, forced: false))
            await Dialogs.MessageAsync(XamlRoot, "Password aggiornata", "La nuova password è attiva.");
    }

    private void OnSignOut(object sender, RoutedEventArgs e) => SignOut(null);

    public static void SignOut(string? message)
    {
        App.Api.Logout();
        App.Settings.Token = null;
        App.Settings.IsAdmin = false;
        App.Settings.Save();
        App.MainWindow.NavigateRoot(typeof(LoginPage), new LoginRequest(message));
        App.MainWindow.ClearRootHistory();
    }
}
