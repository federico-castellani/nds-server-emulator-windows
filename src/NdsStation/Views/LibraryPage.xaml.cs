using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using NdsStation.Services;

namespace NdsStation.Views;

public sealed partial class LibraryPage : Page
{
    private List<GameItem> _all = new();
    private bool _loading;

    public LibraryPage()
    {
        InitializeComponent();
    }

    public ObservableCollection<GameItem> Games { get; } = new();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        ErrorBar.IsOpen = false;
        EmptyState.Visibility = Visibility.Collapsed;
        LoadingRing.IsActive = Games.Count == 0;

        try
        {
            var games = await App.Api.GetGamesAsync();
            _all = games.Select(g => new GameItem(g)).ToList();
            ApplyFilter();
            foreach (var item in _all) await item.RefreshFromCacheAsync();
        }
        catch (ApiException ex) when (ex.IsUnauthorized)
        {
            ShellPage.SignOut("La sessione è scaduta. Accedi di nuovo.");
        }
        catch (ApiException ex)
        {
            Dialogs.Show(ErrorBar, ex.Message, title: "Impossibile caricare la libreria");
        }
        finally
        {
            LoadingRing.IsActive = false;
            _loading = false;
        }
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text.Trim();
        var visible = query.Length == 0
            ? _all
            : _all.Where(g => g.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();

        Games.Clear();
        foreach (var item in visible) Games.Add(item);

        CountText.Text = _all.Count switch
        {
            0 => "",
            1 => "1 gioco",
            _ => $"{_all.Count} giochi",
        };

        bool empty = visible.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (empty && query.Length > 0)
        {
            EmptyText.Text = "Nessun risultato";
            EmptyHint.Text = $"Nessun gioco corrisponde a “{query}”.";
        }
        else if (empty)
        {
            EmptyText.Text = "Nessun gioco disponibile";
            EmptyHint.Text = App.Settings.IsAdmin
                ? "Carica una ROM dalla sezione Amministrazione."
                : "Chiedi a un amministratore di aggiungere dei giochi.";
        }
    }

    private void OnSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => ApplyFilter();

    private void OnRefresh(object sender, RoutedEventArgs e) => _ = LoadAsync();

    private void OnGameClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is GameItem item) Play(item);
    }

    private void OnPlayMenu(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is GameItem item) Play(item);
    }

    private async void OnRemoveFromCache(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not GameItem item) return;
        LocalStore.DeleteCachedRom(item.Info.Id);
        await item.RefreshFromCacheAsync();
    }

    private async void Play(GameItem item)
    {
        if (!App.CoreAvailable)
        {
            await Dialogs.MessageAsync(XamlRoot, "Emulatore non disponibile", App.CoreError ?? "ndscore.dll non trovato.");
            return;
        }
        App.MainWindow.NavigateRoot(typeof(EmulatorPage), item.Info);
    }
}
