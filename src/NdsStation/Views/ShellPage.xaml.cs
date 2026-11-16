using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;

namespace NdsStation.Views;

public sealed partial class ShellPage : Page
{
    public ShellPage()
    {
        InitializeComponent();
        UpdateBanner.Attach(UpdateBar);
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
