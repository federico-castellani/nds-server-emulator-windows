using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using NdsStation.Services;

namespace NdsStation.Views;

/// <summary>A row in the admin user list.</summary>
public sealed class AdminUserItem
{
    public AdminUserItem(AdminUser user)
    {
        User = user;
        IsSelf = ApiClient.TokenUserId(App.Api.Token) is { } self
            ? self == user.Id
            : string.Equals(user.Username, App.Settings.Username, StringComparison.OrdinalIgnoreCase);
    }

    public AdminUser User { get; }
    public bool IsSelf { get; }
    public bool CanDelete => !IsSelf;
    public Visibility SelfVisibility => IsSelf ? Visibility.Visible : Visibility.Collapsed;
    public string IdText => $"#{User.Id}";

    public string RoleText => User.IsAdmin ? "Admin" : "Utente";
    public Brush RoleBackground => Resource(User.IsAdmin ? "AccentFillColorDefaultBrush" : "ControlFillColorSecondaryBrush");
    public Brush RoleForeground => Resource(User.IsAdmin ? "TextOnAccentFillColorPrimaryBrush" : "TextFillColorPrimaryBrush");

    public string StatusText => User.MustChangePassword ? "Da configurare" : "Attivo";
    public Brush StatusBackground => Resource(User.MustChangePassword ? "SystemFillColorCautionBackgroundBrush" : "SystemFillColorSuccessBackgroundBrush");
    public Brush StatusForeground => Resource(User.MustChangePassword ? "SystemFillColorCautionBrush" : "SystemFillColorSuccessBrush");

    public override string ToString() => User.Username;

    private static Brush Resource(string key) => (Brush)Application.Current.Resources[key];
}
