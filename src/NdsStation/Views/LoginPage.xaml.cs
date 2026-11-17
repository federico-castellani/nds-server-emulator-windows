using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using NdsStation.Services;
using Windows.System;

namespace NdsStation.Views;

/// <summary>Navigation parameter: skip restoring the saved session.</summary>
public sealed record LoginRequest(string? Message = null);

public sealed partial class LoginPage : Page
{
    private CancellationTokenSource? _restore;
    private bool _restoreCancelledByUser;

    public LoginPage()
    {
        InitializeComponent();
        UpdateBanner.Attach(UpdateBar);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        App.MainWindow.SetTitle(null);

        ServerBox.Text = App.Settings.ServerUrl;
        UsernameBox.Text = App.Settings.Username;

        if (e.Parameter is LoginRequest request)
        {
            if (request.Message is not null) Dialogs.Show(ErrorBar, request.Message, InfoBarSeverity.Informational);
            ShowForm();
            return;
        }

        if (App.Settings.Token is string token)
            _ = RestoreSessionAsync(token);
        else
            ShowForm();
    }

    private async Task RestoreSessionAsync(string token)
    {
        RestorePanel.Visibility = Visibility.Visible;
        FormPanel.Visibility = Visibility.Collapsed;
        RestoreText.Text = $"Accesso a {App.Api.BaseUrl} come {App.Settings.Username}…";

        _restore = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        App.Api.Token = token;
        try
        {
            // The game list is the cheapest authenticated call; it validates the token.
            await App.Api.GetGamesAsync(_restore.Token);
            EnterApp();
        }
        catch (ApiException ex) when (ex.IsUnauthorized)
        {
            App.Settings.Token = null;
            App.Settings.Save();
            App.Api.Logout();
            ShowForm("La sessione è scaduta. Accedi di nuovo.", InfoBarSeverity.Warning);
        }
        catch (ApiException ex)
        {
            ShowForm(ex.Message);
        }
        catch (OperationCanceledException)
        {
            if (!_restoreCancelledByUser) ShowForm("Il server non risponde. Riprova più tardi.");
        }
    }

    private void OnCancelRestore(object sender, RoutedEventArgs e)
    {
        _restoreCancelledByUser = true;
        _restore?.Cancel();
        App.Api.Logout();
        App.Settings.Token = null;
        App.Settings.Save();
        ShowForm();
    }

    private void ShowForm(string? message = null, InfoBarSeverity severity = InfoBarSeverity.Error)
    {
        RestorePanel.Visibility = Visibility.Collapsed;
        FormPanel.Visibility = Visibility.Visible;
        if (message is not null) Dialogs.Show(ErrorBar, message, severity);

        if (string.IsNullOrEmpty(UsernameBox.Text)) UsernameBox.Focus(FocusState.Programmatic);
        else PasswordBox.Focus(FocusState.Programmatic);
    }

    private void OnFieldKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            OnSignIn(sender, e);
        }
    }

    private async void OnSignIn(object sender, RoutedEventArgs e)
    {
        var server = ServerBox.Text.Trim();
        var username = UsernameBox.Text.Trim();
        var password = PasswordBox.Password; // never trimmed: spaces may be part of it

        if (server.Length == 0 || username.Length == 0 || password.Length == 0)
        {
            Dialogs.Show(ErrorBar, "Inserisci server, nome utente e password.");
            return;
        }

        SetBusy(true);
        ErrorBar.IsOpen = false;
        try
        {
            App.Api.BaseUrl = server;
            var result = await App.Api.LoginAsync(username, password);

            if (result.MustChangePassword && !await Dialogs.ChangePasswordAsync(XamlRoot, forced: true))
            {
                // Without a new password the account stays locked; don't keep the session.
                App.Api.Logout();
                Dialogs.Show(ErrorBar, "Devi impostare una nuova password per usare questo account.", InfoBarSeverity.Warning);
                return;
            }

            App.Settings.ServerUrl = App.Api.BaseUrl;
            App.Settings.Username = username;
            App.Settings.IsAdmin = result.IsAdmin;
            App.Settings.Token = result.Token;
            App.Settings.Save();
            PasswordBox.Password = "";
            EnterApp();
        }
        catch (ApiException ex)
        {
            Dialogs.Show(ErrorBar, ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        SignInButton.IsEnabled = !busy;
        ServerBox.IsEnabled = UsernameBox.IsEnabled = PasswordBox.IsEnabled = !busy;
        SignInRing.IsActive = busy;
        SignInText.Opacity = busy ? 0 : 1;
    }

    private void EnterApp()
    {
        if (!App.Settings.IsAdmin && ApiClient.TokenSaysAdmin(App.Api.Token)) App.Settings.IsAdmin = true;
        LocalStore.SetScope(App.Api.BaseUrl, ApiClient.TokenUserId(App.Api.Token), App.Settings.Username);
        App.MainWindow.NavigateRoot(typeof(ShellPage));
        App.MainWindow.ClearRootHistory();
    }
}
