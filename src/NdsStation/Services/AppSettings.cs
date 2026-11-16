using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NdsStation.Emulation;

namespace NdsStation.Services;

public enum ScreenLayout { Vertical, Horizontal, TopFocus }

public enum PadLayout
{
    /// <summary>Buttons by position, like a real DS (Xbox B = DS A).</summary>
    Positional,
    /// <summary>Buttons by label (Xbox A = DS A).</summary>
    ByLabel,
}

public enum AppTheme { System, Light, Dark }

/// <summary>User preferences, stored as JSON in %LOCALAPPDATA%\NDSStation\settings.json.</summary>
public sealed class AppSettings
{
    public string ServerUrl { get; set; } = "http://localhost:3000";
    public string Username { get; set; } = "";
    public bool IsAdmin { get; set; }

    /// <summary>Auth token encrypted with DPAPI for the current Windows user.</summary>
    public string? ProtectedToken { get; set; }

    public double Volume { get; set; } = 0.5;
    public ScreenLayout ScreenLayout { get; set; } = ScreenLayout.Vertical;
    public bool IntegerScaling { get; set; }
    public PadLayout PadLayout { get; set; } = PadLayout.Positional;
    public AppTheme Theme { get; set; } = AppTheme.System;
    public float FastForwardSpeed { get; set; } = 3.0f;
    public NdsCore.FastForwardSound FastForwardSound { get; set; } = NdsCore.FastForwardSound.NormalPitch;

    public NdsCore.NetPreference Network { get; set; } = NdsCore.NetPreference.Auto;
    public string PrimaryDns { get; set; } = "178.62.43.212";
    public string SecondaryDns { get; set; } = "8.8.8.8";

    public Dictionary<DsKey, int> KeyBindings { get; set; } = InputMap.DefaultKeyboard();

    [JsonIgnore]
    public string? Token
    {
        get
        {
            if (string.IsNullOrEmpty(ProtectedToken)) return null;
            try
            {
                var bytes = ProtectedData.Unprotect(Convert.FromBase64String(ProtectedToken), Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                return null;
            }
        }
        set
        {
            ProtectedToken = string.IsNullOrEmpty(value)
                ? null
                : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser));
        }
    }

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NDSStation.Token.v1");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static string FilePath => Path.Combine(LocalStore.Root, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? new AppSettings();
                // Fill in buttons added after the file was written.
                foreach (var (key, vk) in InputMap.DefaultKeyboard())
                    settings.KeyBindings.TryAdd(key, vk);
                return settings;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(LocalStore.Root);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temp, FilePath, overwrite: true);
    }
}
